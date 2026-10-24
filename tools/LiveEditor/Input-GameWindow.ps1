param(
    [Nullable[int]]$ClickX,
    [Nullable[int]]$ClickY,
    [ValidateSet('Insert','Enter','Escape','Tab','CtrlA','AltN','Down','Up')][string]$Key,
    [string]$Text,
    [int]$Wheel = 0
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GameWindowSelection.ps1')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LiveEditorInput {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int dx,dy; public uint data,flags,time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyboardInput { public ushort key,scan; public uint flags,time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] public struct Union { [FieldOffset(0)] public MouseInput mouse; [FieldOffset(0)] public KeyboardInput keyboard; }
    [StructLayout(LayoutKind.Sequential)] public struct Input { public uint type; public Union u; }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint SendInput(uint count,Input[] inputs,int size);
    public static void TypeText(IntPtr target,string text) {
        foreach(char c in text) {
            if(GetForegroundWindow()!=target)throw new Exception("Game render window lost focus; remaining text was not sent");
            var down=new Input {type=1,u=new Union {keyboard=new KeyboardInput{scan=c,flags=4}}};
            var up=new Input {type=1,u=new Union {keyboard=new KeyboardInput{scan=c,flags=6}}};
            var items=new[]{down,up};
            if(SendInput(2,items,Marshal.SizeOf<Input>())!=2)throw new Exception("Text input rejected");
        }
    }
    public static void Key(byte key) { keybd_event(key,0,0,UIntPtr.Zero); keybd_event(key,0,2,UIntPtr.Zero); }
    public static void Chord(byte modifier,byte key) { keybd_event(modifier,0,0,UIntPtr.Zero); Key(key); keybd_event(modifier,0,2,UIntPtr.Zero); }
}
'@
$gameWindow = Get-MotionVfxRenderWindow
$renderWindow = $gameWindow.Handle
function Assert-GameRenderForeground {
    if ([LiveEditorInput]::GetForegroundWindow() -ne $renderWindow) { throw 'Game render window is not foreground; no input will be sent to the console or another application.' }
}
Assert-GameRenderForeground
if (($null -eq $ClickX) -ne ($null -eq $ClickY)) { throw 'Both click coordinates are required; no input sent.' }
if ($null -ne $ClickX -and $null -ne $ClickY) {
    $rect = [LiveEditorInput+Rect]::new()
    if (-not [LiveEditorInput]::GetWindowRect($renderWindow, [ref]$rect)) { throw 'Cannot read the game render window bounds; no input sent.' }
    if ($ClickX -lt 0 -or $ClickY -lt 0 -or $ClickX -ge ($rect.Right-$rect.Left) -or $ClickY -ge ($rect.Bottom-$rect.Top)) { throw 'Click position is outside the game render window; no input sent.' }
    $targetX = $rect.Left + $ClickX
    $targetY = $rect.Top + $ClickY
    if (-not [LiveEditorInput]::SetCursorPos($targetX, $targetY)) { throw 'System cursor movement was rejected; no click sent.' }
    Start-Sleep -Milliseconds 180
    $actual = [LiveEditorInput+Point]::new()
    if (-not [LiveEditorInput]::GetCursorPos([ref]$actual) -or [Math]::Abs($actual.X - $targetX) -gt 2 -or [Math]::Abs($actual.Y - $targetY) -gt 2) { throw 'System cursor did not reach the requested game position; no click sent.' }
    Assert-GameRenderForeground
    [LiveEditorInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 70
    [LiveEditorInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
}
if ($Wheel -ne 0) { Assert-GameRenderForeground; [LiveEditorInput]::mouse_event(0x0800,0,0,[BitConverter]::ToUInt32([BitConverter]::GetBytes($Wheel),0),[UIntPtr]::Zero) }
Assert-GameRenderForeground
switch ($Key) {
    'Insert' { [LiveEditorInput]::Key(45) }
    'Enter' { [LiveEditorInput]::Key(13) }
    'Escape' { [LiveEditorInput]::Key(27) }
    'Tab' { [LiveEditorInput]::Key(9) }
    'CtrlA' { [LiveEditorInput]::Chord(17,65) }
    'AltN' { [LiveEditorInput]::Chord(18,78) }
    'Down' { [LiveEditorInput]::Key(40) }
    'Up' { [LiveEditorInput]::Key(38) }
}
if ($Text) { [LiveEditorInput]::TypeText($renderWindow,$Text) }
