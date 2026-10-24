using System.Diagnostics;
using System.Reflection;
using MotionVfxSetup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? Value(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        try
        {
            if (args.Contains("--self-test")) return InstallerTests.Run(Value("--output") ?? throw new ArgumentException("--output is required"), Value("--fixture"), Value("--legacy-installer"), Value("--legacy-package"));
            if (args.Contains("--verify-only"))
            {
                InstallEngine.ExtractAndVerify(Value("--output") ?? throw new ArgumentException("--output is required")); return 0;
            }
            if (args.Contains("--install"))
            {
                string game = Value("--game-root") ?? throw new ArgumentException("--game-root is required");
                var result = InstallEngine.Install(game, Value("--install-root") ?? InstallEngine.DefaultRoot(game), !args.Contains("--no-shortcut"), (_, _) => { });
                if (Value("--result") is string report) InstallEngine.Atomic(report, result);
                return result.BackendReady ? 0 : 3;
            }
            string appRoot = Path.GetDirectoryName(Environment.ProcessPath)!;
            if (args.Contains("--start") || (!args.Contains("--setup") && File.Exists(Path.Combine(appRoot, "installation.json"))))
            {
                InstallEngine.Start(appRoot);
                if (!args.Contains("--quiet")) MessageBox.Show("编辑器后台已就绪。\n\n现在可正常启动游戏，在 REFramework 中打开 MotionVfx Live。", "MotionVfx Live", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            using var form = new SetupWindow();
            if (Value("--screenshot") is string screenshot)
                form.Shown += (_, _) =>
                {
                    Application.DoEvents();
                    using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, form.Bounds with { X = 0, Y = 0 });
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!); bitmap.Save(screenshot); form.Close();
                };
            Application.Run(form); return 0;
        }
        catch (Exception ex)
        {
            string message = ex.GetBaseException().Message;
            if (Value("--result") is string report) InstallEngine.Atomic(report, new { ok = false, error = message });
            if (!args.Contains("--quiet")) MessageBox.Show(message, "MotionVfx Live", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed class SetupWindow : Form
{
    private readonly ComboBox game = new() { DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat };
    private readonly Button install = new() { Text = "一键安装", FlatStyle = FlatStyle.Flat };
    private readonly Button browse = new() { Text = "选择目录", FlatStyle = FlatStyle.Flat };
    private readonly CheckBox shortcut = new() { Text = "在桌面创建 MotionVfx Live 启动入口", Checked = true, AutoSize = true };
    private readonly Label status = new() { Text = "自动查找 Steam 游戏目录 · 安装时自动备份旧版", AutoSize = false };
    private readonly ProgressBar progress = new() { Style = ProgressBarStyle.Continuous };
    private readonly Color muted = Color.FromArgb(153, 168, 194);
    private bool busy;
    private InstallResult? result;
    private readonly Image logo;
    internal SetupWindow()
    {
        Text = "MotionVfx Live · 安装 / 更新"; ClientSize = new Size(760, 535); FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; BackColor = Color.FromArgb(13, 17, 28); ForeColor = Color.FromArgb(230, 237, 249);
        Font = new Font("Microsoft YaHei UI", 10);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MotionVfx.logo.png")!;
        using var loaded = Image.FromStream(stream); logo = new Bitmap(loaded);
        Controls.Add(new PictureBox { Image = logo, Location = new Point(34, 30), Size = new Size(66, 66), SizeMode = PictureBoxSizeMode.Zoom });
        AddLabel("MotionVfx Live", 116, 28, 560, 42, 26);
        AddLabel("在游戏中编辑动作与特效", 118, 76, 570, 26, 10, muted);
        AddLabel("安装到《怪物猎人 荒野》", 36, 140, 680, 32, 17);
        AddLabel("内含编辑器、D2D 界面、运行库与 AI MCP。无需手动复制文件。", 36, 181, 690, 28, 10, muted);
        game.SetBounds(36, 233, 551, 34); game.BackColor = Color.FromArgb(29, 39, 58); game.ForeColor = ForeColor;
        foreach (string path in InstallEngine.FindGames()) game.Items.Add(path);
        if (game.Items.Count > 0) game.SelectedIndex = 0;
        Controls.Add(game); Style(browse, 602, 230, 122, 36, Color.FromArgb(36, 47, 68));
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "选择包含 MonsterHunterWilds.exe 的游戏目录", UseDescriptionForTitle = true, SelectedPath = game.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) game.Text = dialog.SelectedPath; };
        AddLabel("请先退出游戏。已有 REFramework 会保留；首次使用需先安装 REFramework。", 36, 278, 690, 37, 9, muted);
        shortcut.Location = new Point(36, 325); Controls.Add(shortcut);
        status.SetBounds(36, 379, 688, 42); status.ForeColor = muted; Controls.Add(status);
        progress.SetBounds(36, 428, 688, 5); Controls.Add(progress);
        AddLabel("v" + InstallEngine.Version + "  ·  Windows x64", 36, 476, 300, 26, 9, muted);
        Style(install, 512, 459, 212, 48, Color.FromArgb(120, 102, 245)); install.Font = new Font(Font.FontFamily, 13);
        install.Click += async (_, _) => await BeginInstall();
        game.TextChanged += (_, _) => RefreshTarget();
        RefreshTarget();
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
    }
    private void RefreshTarget()
    {
        if (busy || result != null) return;
        string? old = null;
        try { old = InstallEngine.InstalledVersion(game.Text.Trim().Trim('"')); } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        install.Text = old == InstallEngine.Version ? "修复安装" : old != null ? "一键更新" : "一键安装";
        status.Text = old != null ? "已检测到 " + old + " → " + InstallEngine.Version + " · 保留设置、备注及素材，自动备份旧程序"
            : "自动查找 Steam 游戏目录 · 支持覆盖旧版，无需卸载";
    }
    private void AddLabel(string text, int x, int y, int w, int h, int size, Color? color = null)
        => Controls.Add(new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), Font = new Font(Font.FontFamily, size), ForeColor = color ?? ForeColor });
    private void Style(Button button, int x, int y, int w, int h, Color color)
    { button.SetBounds(x, y, w, h); button.BackColor = color; button.ForeColor = ForeColor; button.FlatAppearance.BorderSize = 0; Controls.Add(button); }
    private async Task BeginInstall()
    {
        if (result != null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { result.InstallRoot } }); return;
        }
        string path = game.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)) { status.Text = "请选择游戏目录。"; return; }
        busy = true; install.Enabled = browse.Enabled = game.Enabled = shortcut.Enabled = false;
        try
        {
            bool createShortcut = shortcut.Checked;
            var observer = new Progress<(int Percent, string Message)>(p => { progress.Value = p.Percent; status.Text = p.Message; });
            result = await Task.Run(() => InstallEngine.Install(path, InstallEngine.DefaultRoot(path), createShortcut,
                (percent, message) => ((IProgress<(int, string)>)observer).Report((percent, message))));
            install.Text = "打开安装目录";
            status.Text = result.BackendReady ? "安装完成！后台已就绪。之后双击桌面入口即可启动编辑器。" : "文件已安装，后台未就绪。请打开安装目录查看日志。";
        }
        catch (Exception ex) { status.Text = "安装未完成，可修正后重试。"; MessageBox.Show(this, ex.GetBaseException().Message, "安装未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { busy = false; install.Enabled = true; if (result == null) browse.Enabled = game.Enabled = shortcut.Enabled = true; }
    }
    protected override void Dispose(bool disposing) { if (disposing) logo.Dispose(); base.Dispose(disposing); }
}
