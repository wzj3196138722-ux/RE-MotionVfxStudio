param([string]$Helper = (Join-Path $PSScriptRoot '..\DesktopBridge\bin\Release\net10.0-windows\MotionVfxDesktop.exe'))
$ErrorActionPreference = 'Stop'
$Helper = (Resolve-Path -LiteralPath $Helper).Path
$fixture = Join-Path $PSScriptRoot ('test-output\desktop-dialogs-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture -Force
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class DialogProbe {
    delegate bool EnumProc(IntPtr h,IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p,IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h,EnumProc p,IntPtr l);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder b,int n);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h,uint cmd);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetStyle(IntPtr h,int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h,uint msg,IntPtr w,string text);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
    public static bool TopMost(IntPtr h) { return (GetStyle(h,-20).ToInt64()&8)!=0; }
    public static IntPtr Control(IntPtr dialog,int id,string cls) {
        IntPtr result=IntPtr.Zero;
        EnumChildWindows(dialog,(h,l)=>{var c=new StringBuilder(256);GetClassName(h,c,c.Capacity);
            if(GetDlgCtrlID(h)==id && c.ToString()==cls) {result=h;return false;} return true;},IntPtr.Zero);
        return result;
    }
    public static IntPtr Find(uint pid) {
        IntPtr result=IntPtr.Zero;
        EnumWindows((h,l)=>{ uint p;GetWindowThreadProcessId(h,out p);var c=new StringBuilder(256);
            GetClassName(h,c,c.Capacity);
            if(p==pid && c.ToString()=="#32770" && IsWindowVisible(h)) {result=h;return false;}
            return true;},IntPtr.Zero);
        return result;
    }
}
'@
$results = [System.Collections.Generic.List[object]]::new()
foreach ($mode in @('pick','pick_efx','pick_motion','pick_pfb','export')) {
    $id = [guid]::NewGuid().ToString('N')
    $requestPath = Join-Path $fixture "desktop-request-$id.json"
    $statusPath = Join-Path $fixture "desktop-status-$id.json"
    $request = @{id=$id;mode=$mode;items=@(@{key='efx';name='test.efx.1769672';label='特效文件'})}
    $request | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $requestPath -Encoding utf8
    # Exercise even a legacy launcher that still hides the helper's first window.
    $child = Start-Process -FilePath $Helper -ArgumentList ('"'+$requestPath+'"') -WindowStyle Hidden -PassThru
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $dialog = [IntPtr]::Zero
        while ($watch.Elapsed.TotalSeconds -lt 12 -and $dialog -eq [IntPtr]::Zero -and -not $child.HasExited) {
            Start-Sleep -Milliseconds 100
            $dialog = [DialogProbe]::Find([uint32]$child.Id)
        }
        if ($dialog -eq [IntPtr]::Zero) { throw "${mode}: native file dialog did not become visible" }
        $owner = [DialogProbe]::GetWindow($dialog,4)
        if ($owner -eq [IntPtr]::Zero -or -not [DialogProbe]::IsWindowVisible($owner) -or -not [DialogProbe]::TopMost($owner)) {
            throw "${mode}: dialog lacks visible topmost owner"
        }
        @{focus=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture "desktop-focus-$id.json")
        $watch.Restart()
        while ((Test-Path -LiteralPath (Join-Path $fixture "desktop-focus-$id.json")) -and $watch.Elapsed.TotalSeconds -lt 4) { Start-Sleep -Milliseconds 100 }
        if (Test-Path -LiteralPath (Join-Path $fixture "desktop-focus-$id.json")) { throw "${mode}: focus request unhandled" }
        @{stop=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture "desktop-stop-$id.json")
        if (-not $child.WaitForExit(5000)) { throw "${mode}: dialog did not cancel cooperatively" }
        $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
        $expected = if ($mode -eq 'export') { 'complete' } else { 'cancelled' }
        if ($status.phase -ne $expected) { throw "${mode}: unexpected phase $($status.phase): $($status.message)" }
        $results.Add(@{mode=$mode;visible=$true;topmostOwner=$true;focusHandled=$true;cooperativeCancel=$true;exitCode=$child.ExitCode})
        Write-Output "PASS ${mode}: visible owner, native dialog, refocus, cooperative cancel"
    } finally {
        if (-not $child.HasExited) { $child.Kill(); $null=$child.WaitForExit(3000) }
        $child.Dispose()
    }
}
# Select an actual Unicode path through the native dialog and check the durable result.
$id = [guid]::NewGuid().ToString('N')
$selectedFile = Join-Path $fixture '测试 特效.efx.1769672'
[IO.File]::WriteAllText($selectedFile,'file-dialog fixture; not a game resource')
$requestPath = Join-Path $fixture "desktop-request-$id.json"
@{id=$id;mode='pick_efx'} | ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding utf8
$child = Start-Process -FilePath $Helper -ArgumentList ('"'+$requestPath+'"') -WindowStyle Hidden -PassThru
try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $dialog = [IntPtr]::Zero
    while ($watch.Elapsed.TotalSeconds -lt 12 -and $dialog -eq [IntPtr]::Zero -and -not $child.HasExited) {
        Start-Sleep -Milliseconds 100
        $dialog = [DialogProbe]::Find([uint32]$child.Id)
    }
    if ($dialog -eq [IntPtr]::Zero) { throw 'select: native dialog not visible' }
    # Target only native controls belonging to this helper's known modal window.
    Start-Sleep -Milliseconds 500
    $edit = [DialogProbe]::Control($dialog,1148,'Edit')
    $open = [DialogProbe]::Control($dialog,1,'Button')
    if ($edit -eq [IntPtr]::Zero -or $open -eq [IntPtr]::Zero) { throw 'select: native controls missing' }
    $null = [DialogProbe]::SendMessage($edit,0xC,[IntPtr]::Zero,$selectedFile)
    $null = [DialogProbe]::PostMessage($open,0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
    if (-not $child.WaitForExit(5000)) { throw 'select: native selection did not complete' }
    $status = Get-Content -LiteralPath (Join-Path $fixture "desktop-status-$id.json") -Raw | ConvertFrom-Json
    if ($status.phase -ne 'complete' -or $status.files[0] -ne $selectedFile) { throw "select: result mismatch $($status.phase)" }
    $results.Add(@{mode='pick_efx';unicodeSelection=$true;exactSelectedPath=$true;exitCode=$child.ExitCode})
    Write-Output 'PASS native picker selected Unicode file and retained exact path'
} finally {
    if (-not $child.HasExited) { $child.Kill(); $null=$child.WaitForExit(3000) }
    $child.Dispose()
}
$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $fixture 'result.json') -Encoding utf8
Write-Output "Results: $fixture"
