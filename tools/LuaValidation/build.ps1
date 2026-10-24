param([string]$REFrameworkRoot = $env:REFRAMEWORK_ROOT)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$workspaceRoot = Split-Path $projectRoot -Parent
if(-not $REFrameworkRoot){$REFrameworkRoot=Join-Path $projectRoot 'third-party/reframework-sdk'}
$luaSource = Join-Path ([IO.Path]::GetFullPath($REFrameworkRoot)) 'dependencies/lua/src'
if(-not(Test-Path -LiteralPath (Join-Path $luaSource 'lua.h'))){throw 'Lua 5.4.3 source is missing. Pass -REFrameworkRoot <checkout> or set REFRAMEWORK_ROOT; expected dependencies/lua/src/lua.h.'}
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if(-not(Test-Path -LiteralPath $vswherePath)){throw 'Visual Studio C++ Build Tools are required.'}
$vsRoot = & $vswherePath -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw 'Visual Studio C++ build tools were not found.' }
$setupPath = Join-Path $vsRoot 'VC/Auxiliary/Build/vcvars64.bat'
$response = @('/nologo', '/O2', '/D_CRT_SECURE_NO_WARNINGS', '/Fe:lua-runner.exe', ('/I"{0}"' -f $luaSource), 'runner.c')
$response += Get-ChildItem -LiteralPath $luaSource -Filter '*.c' | Where-Object { $_.Name -notin @('lua.c','luac.c') } | ForEach-Object { '"{0}"' -f $_.FullName }
$response | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'build.rsp') -Encoding ascii
Push-Location $PSScriptRoot
try {
    # Give cmd its exact command line instead of letting PowerShell 7 rewrite
    # nested quotes around a Visual Studio path containing spaces.
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $env:ComSpec
    $start.Arguments = '/d /s /c ""{0}" >nul && cl @build.rsp"' -f $setupPath
    $start.WorkingDirectory = $PSScriptRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $compiler = [Diagnostics.Process]::Start($start)
    try {
        $outputTask = $compiler.StandardOutput.ReadToEndAsync()
        $errorTask = $compiler.StandardError.ReadToEndAsync()
        $compiler.WaitForExit()
        $compilerOutput = $outputTask.GetAwaiter().GetResult()
        $compilerError = $errorTask.GetAwaiter().GetResult()
        if ($compilerOutput) { Write-Host $compilerOutput }
        if ($compilerError) { Write-Host $compilerError }
        if ($compiler.ExitCode -ne 0) { throw ('Lua test runner compilation failed with exit code ' + $compiler.ExitCode + '.') }
    } finally { $compiler.Dispose() }
} finally { Pop-Location }
