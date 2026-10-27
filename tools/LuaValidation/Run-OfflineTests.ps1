param([string]$LuaRunner, [string]$REFrameworkRoot = $env:REFRAMEWORK_ROOT)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(-not $LuaRunner){$LuaRunner=Join-Path $PSScriptRoot 'lua-runner.exe'}
$LuaRunner=[IO.Path]::GetFullPath($LuaRunner)
if(-not(Test-Path -LiteralPath $LuaRunner)){
    & (Join-Path $PSScriptRoot 'build.ps1') -REFrameworkRoot $REFrameworkRoot
    if(-not(Test-Path -LiteralPath $LuaRunner)){throw 'Lua test runner was not built.'}
}
$output=Join-Path $PSScriptRoot ('test-output/offline-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
function Run-Lua([string[]]$Arguments){
    & $LuaRunner @Arguments
    if($LASTEXITCODE -ne 0){throw ('Offline Lua check failed: '+$Arguments[0])}
}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $project 'game-plugin') -Recurse -File -Filter '*.lua'){
    Run-Lua @('-p',$file.FullName)
}
foreach($name in @('file-dialog-tests.lua','file-dialog-explorer-tests.lua','d2d-workbench-tests.lua','clipboard-layout-tests.lua')){
    Run-Lua @((Join-Path $PSScriptRoot $name),$project,$output)
}
foreach($name in @('playemitter-ui-tests.lua','clipboard-ui-tests.lua','ui-performance-tests.lua','ai-transport-performance-tests.lua','capture-refresh-tests.lua')){
    Run-Lua @((Join-Path $PSScriptRoot $name),$project)
}
& (Join-Path $PSScriptRoot 'Run-LiveUiTests.ps1') -LuaRunner $LuaRunner
Push-Location (Join-Path $project 'game-plugin')
try { Run-Lua @((Join-Path $project 'game-plugin/tests/action-capture-tests.lua'),(Join-Path $project 'game-plugin/MotionVfxLive/fx_action_capture.lua')) }
finally { Pop-Location }
Run-Lua @((Join-Path $PSScriptRoot 'action-bindings-tests.lua'),(Join-Path $project 'game-plugin/MotionVfxLive/fx_action_bindings.lua'))
# Deliberately omit the optional local MDF-XL source argument. CI uses the
# portable failure fixture and never needs or redistributes a third-party mod.
Run-Lua @('-p',(Join-Path $project 'tools/MdfxlCompatibility/MDF-XL-frame-guard.lua'))
Run-Lua @((Join-Path $PSScriptRoot 'mdfxl-compat-tests.lua'),$project)
Write-Output 'Offline Lua syntax, browser, workbench, nested Action, UI performance, reload, AI transport, capture, bindings and portable MDF-XL checks passed. No game or game assets were used.'
