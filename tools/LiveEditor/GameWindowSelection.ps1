# Loading this helper only defines the window selector; it performs no input or activation.
if (-not ('MotionVfxRenderWindowSelector' -as [type])) {
Add-Type @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
public sealed class MotionVfxWindowCandidate {
    public IntPtr Handle;
    public uint ProcessId;
    public string ClassName, Title;
    public bool Visible;
    public int Width, Height;
}
public static class MotionVfxRenderWindowSelector {
    private delegate bool EnumProc(IntPtr window,IntPtr state);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc,IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder text,int max);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int max);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public static MotionVfxWindowCandidate Choose(IEnumerable<MotionVfxWindowCandidate> candidates,uint[] gameProcesses,IntPtr foreground) {
        var allowed=new HashSet<uint>(gameProcesses);
        // A REFramework console can be reported as Process.MainWindowHandle.
        // Only the game's visible native render class is eligible, never a same-PID console/dialog.
        return candidates.Where(w=>allowed.Contains(w.ProcessId)&&w.Visible&&w.Handle!=IntPtr.Zero&&
            string.Equals(w.ClassName,"via",StringComparison.OrdinalIgnoreCase)&&w.Width>=64&&w.Height>=64)
            .OrderByDescending(w=>w.Handle==foreground)
            .ThenByDescending(w=>string.Equals(w.Title,"Monster Hunter Wilds",StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w=>(long)w.Width*w.Height).FirstOrDefault();
    }
    public static MotionVfxWindowCandidate Find(uint[] gameProcesses) {
        var allowed=new HashSet<uint>(gameProcesses);
        var candidates=new List<MotionVfxWindowCandidate>();
        EnumWindows((window,state)=>{
            uint process; GetWindowThreadProcessId(window,out process);
            if(!allowed.Contains(process)||!IsWindowVisible(window))return true;
            var cls=new StringBuilder(256);GetClassName(window,cls,cls.Capacity);
            if(!string.Equals(cls.ToString(),"via",StringComparison.OrdinalIgnoreCase))return true;
            Rect rect;if(!GetWindowRect(window,out rect))return true;
            var title=new StringBuilder(512);GetWindowText(window,title,title.Capacity);
            candidates.Add(new MotionVfxWindowCandidate{Handle=window,ProcessId=process,ClassName=cls.ToString(),
                Title=title.ToString(),Visible=true,Width=rect.Right-rect.Left,Height=rect.Bottom-rect.Top});
            return true;
        },IntPtr.Zero);
        return Choose(candidates,gameProcesses,GetForegroundWindow());
    }
}
'@
}
function Get-MotionVfxRenderWindow {
    [uint32[]]$gameProcessIds = @(Get-Process -Name MonsterHunterWilds -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
    if ($gameProcessIds.Count -eq 0) { throw 'Monster Hunter Wilds is not running.' }
    $selected = [MotionVfxRenderWindowSelector]::Find($gameProcessIds)
    if ($null -eq $selected) { throw 'No visible Monster Hunter Wilds render window (class via) was found; no other window will be used.' }
    return $selected
}
