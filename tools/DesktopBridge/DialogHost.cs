using System.Runtime.InteropServices;

namespace MotionVfx;

// A real taskbar window owns every native file dialog. This survives a hidden helper
// startup and puts the picker above a borderless game without disabling the game HWND.
internal sealed class DialogHost : Form
{
    private readonly string stopFile;
    private readonly string focusFile;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 150 };
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);

    internal DialogHost(string mailbox, string id)
    {
        stopFile = Path.Combine(mailbox, "desktop-stop-" + id + ".json");
        focusFile = Path.Combine(mailbox, "desktop-focus-" + id + ".json");
        Text = "MotionVfx · 文件选择";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = true; TopMost = true;
        ClientSize = new Size(520, 100);
        StartPosition = FormStartPosition.Manual;
        var area = Screen.FromHandle(GetForegroundWindow()).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Text = "请在文件选择窗口中选择文件。\n取消选择后可回到游戏继续编辑。", AutoSize = false });
        timer.Tick += (_, _) =>
        {
            if (File.Exists(stopFile))
            {
                nint popup = GetWindow(Handle, 6); // GW_ENABLEDPOPUP: our modal picker only.
                if (popup != 0 && popup != Handle) PostMessage(popup, 0x10, 0, 0);
                return;
            }
            if (!File.Exists(focusFile)) return;
            try { File.Delete(focusFile); } catch (IOException) { }
            FocusPicker();
        };
        Show();
        // Explicit second show also protects older launchers that still pass SW_HIDE.
        ShowWindow(Handle, 1);
        Activate();
        timer.Start();
    }

    private void FocusPicker()
    {
        ShowWindow(Handle, 9); // SW_RESTORE
        nint popup = GetWindow(Handle, 6);
        if (popup == 0) popup = Handle;
        ShowWindow(popup, 9);
        SetForegroundWindow(popup);
    }

    internal DialogResult Choose(CommonDialog dialog)
    {
        if (File.Exists(stopFile)) return DialogResult.Cancel;
        return dialog.ShowDialog(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
