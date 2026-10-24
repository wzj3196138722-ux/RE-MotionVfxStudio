$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$runner = Join-Path $PSScriptRoot 'lua-runner.exe'
if (-not (Test-Path -LiteralPath $runner)) { & (Join-Path $PSScriptRoot 'build.ps1') }
$reportRoot = Join-Path $PSScriptRoot 'test-output'
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$report = Join-Path $reportRoot 'action-tests.txt'
'Offline Lua 5.4 real-action workflow tests; mocked engine only.' | Set-Content -LiteralPath $report -Encoding utf8
function Run-Checked([string[]]$Arguments) {
    $lines = @(& $runner @Arguments 2>&1)
    $code = $LASTEXITCODE
    $lines | Add-Content -LiteralPath $report -Encoding utf8
    $lines | Write-Output
    if ($code -ne 0) { throw ('Action test failed: ' + $Arguments[0]) }
}
Push-Location (Join-Path $projectRoot 'game-plugin')
try {
    Run-Checked @('-p', 'MotionVfxLive/fx_action_capture.lua')
    Run-Checked @('-p', 'MotionVfxLive/fx_action_bindings.lua')
    Run-Checked @('tests/action-capture-tests.lua')
    Run-Checked @((Join-Path $PSScriptRoot 'action-bindings-tests.lua'), (Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_action_bindings.lua'))
} finally { Pop-Location }
