param([string]$OutputPath = (Join-Path $PSScriptRoot 'game-window.png'), [switch]$Focus, [switch]$WindowOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GameWindowSelection.ps1')
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LiveEditorWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint first, uint second, bool attach);
}
'@
$gameWindow = Get-MotionVfxRenderWindow
$renderWindow = $gameWindow.Handle
if ($Focus) {
    [uint32]$targetProcess = 0
    $targetThread = [LiveEditorWindow]::GetWindowThreadProcessId($renderWindow, [ref]$targetProcess)
    $localThread = [LiveEditorWindow]::GetCurrentThreadId()
    $attached = $false
    try {
        if ($targetThread -ne $localThread) { $attached = [LiveEditorWindow]::AttachThreadInput($localThread, $targetThread, $true) }
        [LiveEditorWindow]::ShowWindow($renderWindow, 9) | Out-Null
        [LiveEditorWindow]::SetForegroundWindow($renderWindow) | Out-Null
    } finally { if ($attached) { [LiveEditorWindow]::AttachThreadInput($localThread, $targetThread, $false) | Out-Null } }
    Start-Sleep -Milliseconds 250
}
if (-not $WindowOnly -and [LiveEditorWindow]::GetForegroundWindow() -ne $renderWindow) { throw 'Game render window is not foreground; refusing to capture another application or the console.' }
$rect = [LiveEditorWindow+Rect]::new()
if (-not [LiveEditorWindow]::GetWindowRect($renderWindow, [ref]$rect)) { throw 'Cannot get game window bounds.' }
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -lt 1 -or $height -lt 1) { throw 'Invalid game window bounds.' }
$bitmap = [Drawing.Bitmap]::new($width, $height)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    if ($WindowOnly) {
        $dc = $graphics.GetHdc()
        try { $captured = [LiveEditorWindow]::PrintWindow($renderWindow, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc) }
        if (-not $captured) { throw 'Game did not supply window content.' }
        $varied = [Collections.Generic.HashSet[int]]::new()
        for ($y=20; $y -lt $height-20; $y+=31) {
            for ($x=20; $x -lt $width-20; $x+=31) { $varied.Add($bitmap.GetPixel($x,$y).ToArgb()) | Out-Null }
        }
        if ($varied.Count -lt 32) { throw 'Game window capture appears blank; no image was saved.' }
    } else {
        if ([LiveEditorWindow]::GetForegroundWindow() -ne $renderWindow) { throw 'Focus changed before capture; no image was saved.' }
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, [Drawing.Size]::new($width, $height))
        if ([LiveEditorWindow]::GetForegroundWindow() -ne $renderWindow) { throw 'Focus changed during capture; no image was saved.' }
    }
    $bitmap.Save([IO.Path]::GetFullPath($OutputPath), [Drawing.Imaging.ImageFormat]::Png)
    Write-Output ([IO.Path]::GetFullPath($OutputPath))
} finally { $graphics.Dispose(); $bitmap.Dispose() }
