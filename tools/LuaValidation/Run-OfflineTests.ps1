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
foreach($name in @('file-dialog-tests.lua','file-dialog-explorer-tests.lua','d2d-workbench-tests.lua')){
    Run-Lua @((Join-Path $PSScriptRoot $name),$project,$output)
}
# Deliberately omit the optional local MDF-XL source argument. CI uses the
# portable failure fixture and never needs or redistributes a third-party mod.
Run-Lua @('-p',(Join-Path $project 'tools/MdfxlCompatibility/MDF-XL-frame-guard.lua'))
Run-Lua @((Join-Path $PSScriptRoot 'mdfxl-compat-tests.lua'),$project)
Write-Output 'Offline production Lua syntax, file browser, workbench and portable MDF-XL compatibility checks passed. No game or game assets were used.'
