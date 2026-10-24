using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VfxStudio.LiveEditor;

internal sealed class DesktopBridge : IDisposable
{
    private sealed record Operation(string Id, string Mode, string Channel, Process Process, JsonObject Request);
    private readonly Options options;
    private readonly string executable;
    private readonly List<Operation> operations = [];
    private bool disposed;

    internal DesktopBridge(Options options, string? executable = null)
    {
        this.options = options;
        this.executable = executable ?? Path.Combine(AppContext.BaseDirectory, "desktop", "MotionVfxDesktop.exe");
    }

    internal object Start(JsonObject request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Reap();
        string mode = request["mode"]?.GetValue<string>() ?? "";
        string channel = MotionVfx.DesktopStatus.Channel(mode);
        if (mode == "cancel_dialog")
        {
            var current = operations.FirstOrDefault(x => x.Channel == "dialog");
            if (current != null) SignalStop(current);
            bool cancelledPrevious = current == null && CancelPreviousDialog();
            string cancelId = Guid.NewGuid().ToString("N");
            var cancelRequest = new JsonObject { ["id"] = cancelId, ["mode"] = mode };
            MotionVfx.DesktopStatus.Write(options.Mailbox, cancelRequest, "complete",
                current == null && !cancelledPrevious ? "当前没有文件选择窗口。" : "已请求取消文件选择。");
            return new { id = cancelId, mode, accepted = true, channel, statusFile = MotionVfx.DesktopStatus.FileName(cancelId) };
        }
        if (channel == "recording" && operations.Any(x => x.Channel == channel))
            throw new InvalidOperationException("已有画面录制正在进行，请先结束该次录制。");
        if (channel == "dialog" && operations.FirstOrDefault(x => x.Channel == channel) is { } dialog)
        {
            Host.AtomicJson(Path.Combine(options.Mailbox, "desktop-focus-" + dialog.Id + ".json"), new { focus = true });
            return new { id = dialog.Id, mode = dialog.Mode, accepted = true, channel,
                statusFile = MotionVfx.DesktopStatus.FileName(dialog.Id), reused = true };
        }
        string? site = request["site"]?.GetValue<string>();
        if (mode == "author_link") _ = MotionVfx.AuthorLinks.Url(site);
        if (mode is "record" or "open_replay_folder" && options.GameRoot == null) throw new InvalidOperationException("录制及打开录像目录需要配置游戏目录。");
        if (!File.Exists(executable)) throw new FileNotFoundException("桌面文件选择/录制组件尚未安装。", executable);
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(options.Mailbox, "desktop-request-" + id + ".json");
        var body = new JsonObject {
            ["id"] = id, ["mode"] = mode, ["gameRoot"] = options.GameRoot,
            ["seconds"] = Math.Clamp(request["seconds"]?.GetValue<int>() ?? 15, 1, 30),
            ["text"] = request["text"]?.GetValue<string>(), ["site"] = site,
            ["exportToken"] = request["exportToken"]?.GetValue<string>(), ["items"] = request["items"]?.DeepClone()
        };
        Host.AtomicJson(path, body);
        MotionVfx.DesktopStatus.Write(options.Mailbox, body, "starting", "正在启动桌面操作");
        // File dialogs are interactive windows. SW_HIDE in STARTUPINFO can suppress the first
        // native dialog, while the headless recording/utility helpers should remain hidden.
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = channel == "dialog" || mode == "open_replay_folder" ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(path);
        try
        {
            var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动桌面组件。");
            operations.Add(new(id, mode, channel, process, body));
            process.Exited += (_, _) => ReportUnexpectedExit(id, body);
            process.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            MotionVfx.DesktopStatus.Write(options.Mailbox, body, "error", ex.Message);
            throw;
        }
        return new { id, mode, accepted = true, channel, statusFile = MotionVfx.DesktopStatus.FileName(id) };
    }

    internal object Stop()
    {
        Reap();
        // A file dialog/link must never change the target of the recording stop button.
        var recording = operations.SingleOrDefault(x => x.Channel == "recording");
        if (recording != null) SignalStop(recording);
        return new { requested = recording != null, id = recording?.Id, mode = "record" };
    }

    private void SignalStop(Operation operation) => Host.AtomicJson(
        Path.Combine(options.Mailbox, "desktop-stop-" + operation.Id + ".json"), new { stop = true });

    private bool CancelPreviousDialog()
    {
        // The game UI may retain a previous backend's "choosing" status after that
        // backend exited. Cancel only a validated dialog ID, never recorder state.
        try
        {
            string snapshot = Path.Combine(options.Mailbox, "desktop-dialog-status.json");
            if (!File.Exists(snapshot)) return false;
            var previous = JsonNode.Parse(File.ReadAllText(snapshot))?.AsObject();
            string? id = previous?["id"]?.GetValue<string>(), mode = previous?["mode"]?.GetValue<string>();
            if (!Guid.TryParseExact(id, "N", out _) || mode is not ("pick" or "pick_efx" or "pick_motion" or "pick_pfb" or "export") ||
                previous?["phase"]?.GetValue<string>() is not ("starting" or "choosing")) return false;
            Host.AtomicJson(Path.Combine(options.Mailbox, "desktop-stop-" + id + ".json"), new { stop = true });
            MotionVfx.DesktopStatus.Write(options.Mailbox, previous!, "cancelled", "已取消旧文件选择；可重新打开文件。");
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return false; }
    }

    private void ReportUnexpectedExit(string id, JsonObject request)
    {
        // Run without waiting for another UI command, otherwise a crashed picker can stay
        // at "choosing" indefinitely. Never let a diagnostic IO failure crash the backend.
        try
        {
            if (!MotionVfx.DesktopStatus.IsTerminal(options.Mailbox, id))
                MotionVfx.DesktopStatus.Write(options.Mailbox, request, "error", "桌面操作已意外结束，请再次点击打开文件重试。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Reap()
    {
        foreach (var operation in operations.ToArray())
        {
            if (!operation.Process.HasExited) continue;
            if (!MotionVfx.DesktopStatus.IsTerminal(options.Mailbox, operation.Id))
                MotionVfx.DesktopStatus.Write(options.Mailbox, operation.Request, "error", "桌面操作已意外结束，请重试。");
            operation.Process.Dispose();
            operations.Remove(operation);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var operation in operations) SignalStop(operation);
        var wait = Stopwatch.StartNew();
        foreach (var operation in operations)
        {
            try
            {
                if (!operation.Process.HasExited && !operation.Process.WaitForExit(Math.Max(0, 2000 - (int)wait.ElapsedMilliseconds)))
                {
                    if (!MotionVfx.DesktopStatus.IsTerminal(options.Mailbox, operation.Id))
                        MotionVfx.DesktopStatus.Write(options.Mailbox, operation.Request, "cancelled", "编辑器已关闭，桌面操作已取消。");
                    // Only our exact child process, never a launched browser or another editor.
                    operation.Process.Kill(entireProcessTree: false);
                    operation.Process.WaitForExit(1000);
                }
                if (!MotionVfx.DesktopStatus.IsTerminal(options.Mailbox, operation.Id))
                    MotionVfx.DesktopStatus.Write(options.Mailbox, operation.Request, "cancelled", "编辑器已关闭，桌面操作已取消。");
            }
            catch (InvalidOperationException) { /* The child exited between the checks. */ }
            finally { operation.Process.Dispose(); }
        }
        operations.Clear();
    }
}
