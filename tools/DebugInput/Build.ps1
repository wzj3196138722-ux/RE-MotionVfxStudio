param([string]$REFrameworkRoot = $env:REFRAMEWORK_ROOT)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(-not $REFrameworkRoot){$REFrameworkRoot=Join-Path $project 'third-party/reframework-sdk'}
$reference = [IO.Path]::GetFullPath($REFrameworkRoot)
$lua = Join-Path $reference 'dependencies/lua/src'
if(-not(Test-Path -LiteralPath (Join-Path $reference 'include/reframework/API.h')) -or -not(Test-Path -LiteralPath (Join-Path $lua 'lua.h'))){
    throw 'REFramework API headers and Lua sources are missing. Pass -REFrameworkRoot <checkout> or set REFRAMEWORK_ROOT. Use API 1.15 and Lua 5.4.3; see docs/development/BUILD.en.md.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if(-not(Test-Path -LiteralPath $vswhere)){throw 'Visual Studio C++ Build Tools with the Windows SDK are required.'}
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ tools are required.' }
$setup = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$build = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$luaSources = Get-ChildItem -LiteralPath $lua -Filter '*.c' | Where-Object Name -notin @('lua.c','luac.c')
$response = @('/nologo','/O2','/MT','/D_CRT_SECURE_NO_WARNINGS',('/I"{0}"' -f $lua),'/c')
$response += $luaSources | ForEach-Object { '"' + $_.FullName + '"' }
[IO.File]::WriteAllLines((Join-Path $build 'lua.rsp'), $response, [Text.Encoding]::ASCII)
$cpp = @('/nologo','/std:c++17','/EHsc','/O2','/MT','/LD',('/I"{0}"' -f $lua),('/I"{0}"' -f (Join-Path $reference 'include')),
    ('"{0}"' -f (Join-Path $PSScriptRoot 'Plugin.cpp')), '/Fe:MotionVfxDebugInput.dll')
$cpp += $luaSources | ForEach-Object { $_.BaseName + '.obj' }
$cpp += @('user32.lib','dxguid.lib','/link','/INCREMENTAL:NO')
[IO.File]::WriteAllLines((Join-Path $build 'plugin.rsp'), $cpp, [Text.Encoding]::ASCII)
$fixture = @('/nologo','/std:c++17','/EHsc','/O2','/MT',('/I"{0}"' -f $lua),('/I"{0}"' -f (Join-Path $reference 'include')),
    ('"{0}"' -f (Join-Path $PSScriptRoot 'PluginTests.cpp')), '/Fe:PluginTests.exe')
$fixture += $luaSources | ForEach-Object { $_.BaseName + '.obj' }
$fixture += 'user32.lib'
[IO.File]::WriteAllLines((Join-Path $build 'fixture.rsp'), $fixture, [Text.Encoding]::ASCII)
Push-Location $build
try {
    & cmd /c ('""{0}" >nul && cl @lua.rsp && cl @plugin.rsp && cl /nologo /std:c++17 /EHsc /MT /Fe:InputTests.exe ../Tests.cpp && cl /nologo /std:c++17 /EHsc /MT /Fe:PollingTests.exe ../PollingTests.cpp user32.lib dxguid.lib && cl @fixture.rsp"' -f $setup)
    if ($LASTEXITCODE -ne 0) { throw 'Native input build failed.' }
    & ./InputTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Native input tests failed.' }
    & ./PollingTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Polling adapter tests failed.' }
    & ./PluginTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Plugin DLL/Lua integration failed.' }
} finally { Pop-Location }
Write-Output (Join-Path $build 'MotionVfxDebugInput.dll')
