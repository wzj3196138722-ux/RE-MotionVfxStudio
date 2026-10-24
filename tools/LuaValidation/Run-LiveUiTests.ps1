param([string]$LuaRunner)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$workspaceRoot = Split-Path $projectRoot -Parent
$runner = if ($LuaRunner) { [IO.Path]::GetFullPath($LuaRunner) } else { Join-Path $PSScriptRoot 'lua-runner.exe' }
$testScript = Join-Path $PSScriptRoot 'live-ui-tests.lua'
$mainScript = Join-Path $projectRoot 'game-plugin/MotionVfxLive.lua'
$reloadSource = Get-Content -LiteralPath (Join-Path $projectRoot 'game-plugin/手动重载MotionVfxLive.lua') -Raw
$fixture = Join-Path $PSScriptRoot ('live-ui-fixtures/' + [Guid]::NewGuid().ToString('N'))
$autorun = Join-Path $fixture 'reframework/autorun'
New-Item -ItemType Directory -Path (Join-Path $autorun 'MotionVfxLive') -Force | Out-Null
Copy-Item -LiteralPath $mainScript -Destination (Join-Path $autorun 'MotionVfxLive.lua')
Copy-Item -LiteralPath (Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_preview_runtime.lua') -Destination (Join-Path $autorun 'MotionVfxLive/fx_preview_runtime.lua')
Copy-Item -LiteralPath (Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_ai_bridge.lua') -Destination (Join-Path $autorun 'MotionVfxLive/fx_ai_bridge.lua')
Copy-Item -LiteralPath (Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_debug_hotkeys.lua') -Destination (Join-Path $autorun 'MotionVfxLive/fx_debug_hotkeys.lua')
foreach ($module in @('fx_precache_runtime','fx_d2d_widgets','fx_d2d_workbench','fx_parameter_ui','fx_action_sources','fx_attachment_targets','fx_action_preview_runtime','fx_action_preview_transaction','fx_pfb_page','fx_file_dialog','fx_pfb_preview_runtime','fx_equipment_sources','fx_equipment_pfb_preview_runtime')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('game-plugin/MotionVfxLive/'+$module+'.lua')) -Destination (Join-Path $autorun ('MotionVfxLive/'+$module+'.lua'))
}
Push-Location $fixture
try {
    & $runner $testScript $mainScript $reloadSource 'real-files'
    if ($LASTEXITCODE -ne 0) { throw ('Live UI tests failed with exit code ' + $LASTEXITCODE) }
} finally { Pop-Location }
