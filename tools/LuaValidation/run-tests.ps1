param([Parameter(Mandatory=$true)][string]$MaterialsRoot)
# Historical standalone-material suite. For the current editor use Run-OfflineTests.ps1.
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$workspaceRoot = Split-Path $projectRoot -Parent
$materials = [IO.Path]::GetFullPath($MaterialsRoot)
if(-not(Test-Path -LiteralPath (Join-Path $materials 'fx_hook - 副本.lua'))){throw 'This historical suite requires the original standalone Lua materials, which are not part of the public product. Use Run-OfflineTests.ps1 for the current editor.'}
$runner = Join-Path $PSScriptRoot 'lua-runner.exe'
if (-not (Test-Path -LiteralPath $runner)) { & (Join-Path $PSScriptRoot 'build.ps1') }
$outputDir = Join-Path $PSScriptRoot 'test-output'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$report = Join-Path $outputDir 'test-results.txt'
'Offline Lua 5.4 regression; no REFramework/game calls.' | Set-Content -LiteralPath $report -Encoding utf8
function Invoke-CheckedLua([string[]] $Arguments) {
    $lines = @(& $runner @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $lines | Add-Content -LiteralPath $report -Encoding utf8
    $lines | Write-Output
    if ($exitCode -ne 0) { throw ('Lua validation failed (exit {0}): {1}' -f $exitCode, $Arguments[0]) }
}
Push-Location $materials
try {
    $luaFiles = @(Get-ChildItem -LiteralPath $materials -Filter '*.lua' -File)
    foreach ($file in $luaFiles) { Invoke-CheckedLua -Arguments @('-p', $file.FullName) }
    $syntax = 'PASS syntax: {0} Lua scripts' -f $luaFiles.Count
    $syntax | Add-Content -LiteralPath $report -Encoding utf8
    Write-Output $syntax
    foreach ($name in @('playback-tests.lua', 'startup-tests.lua', 'ui-playback-tests.lua', 'pointcloud-tests.lua')) {
        Invoke-CheckedLua -Arguments @((Join-Path $PSScriptRoot $name))
    }
    $copySource = Get-Content -LiteralPath (Join-Path $materials 'fx_hook - 副本.lua') -Raw -Encoding utf8
    Invoke-CheckedLua -Arguments @((Join-Path $PSScriptRoot 'hook-tests.lua'), $copySource)
    $mockJson = Join-Path $outputDir 'mock-effect-ids.json'
    Invoke-CheckedLua -Arguments @((Join-Path $PSScriptRoot 'utility-tests.lua'), $mockJson)
    $parsed = Get-Content -LiteralPath $mockJson -Raw -Encoding utf8 | ConvertFrom-Json
    if (-not $parsed) { throw 'Mock JSON was empty.' }
    'PASS independent PowerShell JSON parse (mock data only)' | Add-Content -LiteralPath $report -Encoding utf8
    Write-Output 'PASS independent PowerShell JSON parse (mock data only)'
} finally { Pop-Location }
