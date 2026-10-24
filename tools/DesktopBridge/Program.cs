using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class Program
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        string requestPath = Path.GetFullPath(args[0]);
        var request = JsonNode.Parse(File.ReadAllText(requestPath))!.AsObject();
        string mailbox = Path.GetDirectoryName(requestPath)!;
        string id = request["id"]!.GetValue<string>(), mode = request["mode"]!.GetValue<string>();
        void Status(string phase, string message, object? files = null, object? replay = null, object? destinations = null)
        {
            MotionVfx.DesktopStatus.Write(mailbox, request, phase, message, files, replay, destinations);
        }
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            if (mode is "pick" or "pick_efx" or "pick_motion" or "pick_pfb")
            {
                using var owner = new MotionVfx.DialogHost(mailbox, id);
                Status("choosing", "文件选择窗口已打开；再次点击打开文件可重新显示窗口");
                using var dialog = new OpenFileDialog {
                    Title = mode == "pick_motion" ? "MotionVfx · 打开本地动作文件" : mode == "pick_efx" ? "MotionVfx · 打开本地特效文件" : mode == "pick_pfb" ? "MotionVfx · 打开本地 PFB 文件" : "MotionVfx · 选择已转换的游戏资源", Multiselect = mode == "pick",
                    CheckFileExists = true, RestoreDirectory = true,
                    Filter = mode == "pick_motion" ? "动作文件|*.mot;*.mot.*;*.motlist;*.motlist.*|所有文件|*.*" :
                        mode == "pick_efx" ? "特效文件|*.efx;*.efx.*|所有文件|*.*" :
                        mode == "pick_pfb" ? "PFB 文件|*.pfb;*.pfb.18;*.pfb.*|所有文件|*.*" :
                        "游戏资源|*.mesh;*.mesh.*;*.mdf;*.mdf.*;*.mdf2;*.mdf2.*;*.uvs;*.uvs.*;*.tex;*.tex.*;*.pfb;*.pfb.*;*.mot;*.mot.*;*.motlist;*.motlist.*;*.efx;*.efx.*|所有文件|*.*" };
                if (owner.Choose(dialog) == DialogResult.OK) Status("complete", "文件已选择", dialog.FileNames);
                else Status("cancelled", "已取消选择");
            }
            else if (mode == "export")
            {
                using var owner = new MotionVfx.DialogHost(mailbox, id);
                var destinations = new List<object>();
                foreach (var item in request["items"]!.AsArray())
                {
                    string name = item!["name"]!.GetValue<string>(), key = item["key"]!.GetValue<string>();
                    Status("choosing", "选择导出位置：" + name);
                    using var dialog = new SaveFileDialog { Title = "MotionVfx · 导出" + item["label"]!.GetValue<string>() + "（取消可跳过此文件）",
                        FileName = name, Filter = "游戏文件|*.*", AddExtension = false, RestoreDirectory = true, OverwritePrompt = true };
                    if (owner.Choose(dialog) != DialogResult.OK) continue;
                    string? expectedHash = File.Exists(dialog.FileName) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dialog.FileName))) : null;
                    destinations.Add(new { key, path = dialog.FileName, expectedHash });
                }
                Status("complete", destinations.Count == 0 ? "已取消导出" : "已选择导出位置", destinations: destinations);
            }
            else if (mode == "copy")
            {
                Clipboard.SetText(request["text"]?.GetValue<string>() ?? "");
                Status("complete", "路径已复制");
            }
            else if (mode == "author_link")
            {
                string url = MotionVfx.AuthorLinks.Url(request["site"]?.GetValue<string>());
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Status("complete", "已在浏览器中打开作者主页");
            }
            else if (mode == "open_replay_folder")
            {
                string gameRoot = Path.GetFullPath(request["gameRoot"]!.GetValue<string>());
                string folder = Path.Combine(gameRoot, "reframework", "images", "MotionVfxLive", "replays");
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                Status("complete", "已打开录像保存目录", files: new[] { folder });
            }
            else if (mode == "record")
            {
                string gameRoot = Path.GetFullPath(request["gameRoot"]!.GetValue<string>());
                var selected = MotionVfx.GameCaptureWindow.Find(gameRoot);
                using var game = Process.GetProcessById(selected.ProcessId);
                nint window = selected.Handle;
                string replayId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8];
                string imageBase = "MotionVfxLive/replays/" + replayId;
                string output = Path.Combine(gameRoot, "reframework", "images", imageBase.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(output);
                var frames = new List<MotionVfx.ReplayFrame>();
                const int fps = 20;
                int seconds = Math.Clamp(request["seconds"]?.GetValue<int>() ?? 15, 1, 30);
                var elapsed = Stopwatch.StartNew();
                var foregroundTime = new Stopwatch();
                long bytes = 0; int width = 0, height = 0;
                Status("recording", "3 秒后开始；请回到游戏并隐藏菜单");
                while (elapsed.Elapsed.TotalSeconds < 3)
                {
                    if (File.Exists(Path.Combine(mailbox, "desktop-stop-" + id + ".json")))
                    { Status("cancelled", "录制已取消"); return 0; }
                    Thread.Sleep(50);
                }
                elapsed.Restart();
                Status("recording", "正在录制；仅记录游戏在前台的画面");
                long next = 0, lastStatusAt = -1000;
                bool? wasForeground = null;
                while (foregroundTime.Elapsed.TotalSeconds < seconds && elapsed.Elapsed.TotalSeconds < seconds + 60)
                {
                    if (File.Exists(Path.Combine(mailbox, "desktop-stop-" + id + ".json"))) break;
                    if (game.HasExited) break;
                    bool inForeground = GetForegroundWindow() == window && !IsIconic(window);
                    if (inForeground != wasForeground || elapsed.ElapsedMilliseconds - lastStatusAt >= 1000)
                    {
                        Status("recording", inForeground ?
                            $"正在录制游戏画面：{foregroundTime.Elapsed.TotalSeconds:0.0} / {seconds} 秒 · {frames.Count} 帧" :
                            $"等待游戏回到前台；已录制 {frames.Count} 帧，切换到其他程序时暂停采集。");
                        wasForeground = inForeground; lastStatusAt = elapsed.ElapsedMilliseconds;
                    }
                    if (!inForeground)
                    { foregroundTime.Stop(); Thread.Sleep(80); continue; }
                    foregroundTime.Start();
                    if (foregroundTime.ElapsedMilliseconds < next) { Thread.Sleep(4); continue; }
                    double capturedAt = foregroundTime.Elapsed.TotalSeconds;
                    if (!GetClientRect(window, out var rect)) throw new InvalidOperationException("无法读取游戏画面范围。");
                    var origin = new Point();
                    if (!ClientToScreen(window, ref origin)) throw new InvalidOperationException("无法读取游戏画面位置。");
                    int sourceW = rect.Right - rect.Left, sourceH = rect.Bottom - rect.Top;
                    if (sourceW < 64 || sourceH < 64) { Thread.Sleep(50); continue; }
                    using var screen = new Bitmap(sourceW, sourceH);
                    using (var gfx = Graphics.FromImage(screen))
                    {
                        // Recheck immediately before copying: never record a different app after Alt+Tab.
                        if (GetForegroundWindow() != window) { foregroundTime.Stop(); continue; }
                        gfx.CopyFromScreen(origin.X, origin.Y, 0, 0, screen.Size);
                    }
                    if (GetForegroundWindow() != window) { foregroundTime.Stop(); continue; }
                    width = Math.Min(1280, sourceW); height = Math.Max(1, (int)Math.Round((double)sourceH * width / sourceW));
                    using var resized = new Bitmap(width, height);
                    using (var gfx = Graphics.FromImage(resized)) gfx.DrawImage(screen, 0, 0, width, height);
                    string file = $"frame_{frames.Count:00000}.jpg", path = Path.Combine(output, file);
                    resized.Save(path, ImageFormat.Jpeg);
                    bytes += new FileInfo(path).Length;
                    frames.Add(new MotionVfx.ReplayFrame(imageBase + "/" + file, capturedAt));
                    next = Math.Max(next + 1000 / fps, (long)(capturedAt * 1000));
                    if (bytes > 192 * 1024 * 1024) break;
                }
                var replay = MotionVfx.ReplayManifest.Create(replayId, fps, width, height, output, frames);
                Atomic(replay.ManifestPath, replay);
                Atomic(Path.Combine(mailbox, "last-replay.json"), replay);
                Status("complete", $"已保存 {frames.Count} 帧（{replay.Duration:0.00} 秒）；可在「时间与回看」中重播。保存位置：{output}", replay: replay);
            }
            else throw new ArgumentException("未知桌面操作。");
            return 0;
        }
        catch (Exception ex) { Status("error", ex.Message); return 1; }
    }
    static void Atomic(string path, object data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, Json));
        try { File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
