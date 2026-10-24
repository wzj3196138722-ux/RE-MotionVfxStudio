using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MotionVfx;

internal static class GameCaptureWindow
{
    internal sealed record Candidate(nint Handle, int ProcessId, string ClassName, string Title, bool Visible, int Width, int Height);
    internal sealed record Selection(int ProcessId, nint Handle);
    private delegate bool EnumProc(nint window, nint state);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, nint state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    // Query-limited access works across elevation levels where Process.MainModule can fail.
    internal static string? ExecutablePath(int processId)
    {
        nint process = OpenProcess(0x1000, false, processId);
        if (process == 0) return null;
        try
        {
            var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    internal static bool MatchesRoot(string? executable, string gameRoot) => executable != null &&
        string.Equals(Path.GetFileName(executable), "MonsterHunterWilds.exe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(executable))!),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot)), StringComparison.OrdinalIgnoreCase);

    internal static Candidate? Choose(IEnumerable<Candidate> candidates, ISet<int> gameProcesses, nint foreground) => candidates
        .Where(w => gameProcesses.Contains(w.ProcessId) && w.Handle != 0 && w.Visible && w.Width >= 64 && w.Height >= 64 &&
            string.Equals(w.ClassName, "via", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(w => w.Handle == foreground)
        .ThenByDescending(w => string.Equals(w.Title, "Monster Hunter Wilds", StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(w => (long)w.Width * w.Height).FirstOrDefault();

    internal static Selection Find(string gameRoot)
    {
        var processIds = new HashSet<int>();
        bool unreadable = false;
        foreach (var process in Process.GetProcessesByName("MonsterHunterWilds"))
        {
            using (process)
            {
                string? path = ExecutablePath(process.Id);
                if (path == null) unreadable = true;
                if (MatchesRoot(path, gameRoot)) processIds.Add(process.Id);
            }
        }
        if (processIds.Count == 0) throw new InvalidOperationException(unreadable ?
            "无法确认游戏进程路径；请确保编辑器可读取游戏进程，然后重试。" : "未找到配置目录中的游戏进程。");
        var candidates = new List<Candidate>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint id);
            if (!processIds.Contains((int)id) || !IsWindowVisible(window) || !GetClientRect(window, out var rect)) return true;
            var className = new StringBuilder(256); GetClassName(window, className, className.Capacity);
            var title = new StringBuilder(512); GetWindowText(window, title, title.Capacity);
            candidates.Add(new Candidate(window, (int)id, className.ToString(), title.ToString(), true,
                rect.Right - rect.Left, rect.Bottom - rect.Top));
            return true;
        }, 0);
        var selected = Choose(candidates, processIds, GetForegroundWindow()) ??
            throw new InvalidOperationException("游戏画面窗口尚未就绪；不会录制控制台或其他程序，请回到游戏后重试。");
        return new Selection(selected.ProcessId, selected.Handle);
    }
}
