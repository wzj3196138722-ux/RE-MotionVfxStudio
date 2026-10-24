param([string]$REFrameworkRoot = $env:REFRAMEWORK_ROOT)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if(-not $REFrameworkRoot){$REFrameworkRoot=Join-Path $project 'third-party/reframework-sdk'}
$lua=Join-Path ([IO.Path]::GetFullPath($REFrameworkRoot)) 'dependencies/lua/src'
if(-not(Test-Path -LiteralPath (Join-Path $lua 'lua.h'))){throw 'Lua 5.4.3 source is missing. Pass -REFrameworkRoot <checkout> or set REFRAMEWORK_ROOT; expected dependencies/lua/src/lua.h.'}
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if(-not(Test-Path -LiteralPath $vswhere)){throw 'Visual Studio C++ Build Tools are required.'}
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $vs){throw 'Visual Studio C++ build tools are required.'}
$setup=Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$build=Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$luaFiles=Get-ChildItem -LiteralPath $lua -Filter '*.c' | Where-Object Name -notin @('lua.c','luac.c')
$luaArgs=@('/nologo','/O2','/MT','/D_CRT_SECURE_NO_WARNINGS',('/I"'+$lua+'"'),'/c')
$luaArgs+=$luaFiles | ForEach-Object {'"'+$_.FullName+'"'}
[IO.File]::WriteAllLines((Join-Path $build 'lua.rsp'),$luaArgs,[Text.Encoding]::ASCII)
$cpp=@('/nologo','/std:c++17','/EHsc','/O2','/MT',('/I"'+$lua+'"'),'/I../imgui-1.92.4','../OrbRunner.cpp',
    '../imgui-1.92.4/imgui.cpp','../imgui-1.92.4/imgui_draw.cpp','../imgui-1.92.4/imgui_widgets.cpp','../imgui-1.92.4/imgui_tables.cpp','/Fe:OrbRunner.exe')
$cpp+=$luaFiles | ForEach-Object {$_.BaseName+'.obj'}
[IO.File]::WriteAllLines((Join-Path $build 'runner.rsp'),$cpp,[Text.Encoding]::ASCII)
Push-Location $build
try {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$env:ComSpec
    $start.Arguments='/d /s /c ""{0}" >nul && cl @lua.rsp && cl @runner.rsp"' -f $setup
    $start.WorkingDirectory=$build
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $compiler=[Diagnostics.Process]::Start($start)
    try {
        $outputTask=$compiler.StandardOutput.ReadToEndAsync()
        $errorTask=$compiler.StandardError.ReadToEndAsync()
        $compiler.WaitForExit()
        $compilerOutput=$outputTask.GetAwaiter().GetResult()
        $compilerError=$errorTask.GetAwaiter().GetResult()
        if($compilerOutput){Write-Host $compilerOutput}
        if($compilerError){Write-Host $compilerError}
        if($compiler.ExitCode -ne 0){throw ('Native ImGui test build failed with exit code '+$compiler.ExitCode+'.')}
    } finally {$compiler.Dispose()}
} finally {Pop-Location}
