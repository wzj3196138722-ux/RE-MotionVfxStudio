param([Parameter(Mandatory=$true)][string]$GameRoot)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$game=[IO.Path]::GetFullPath($GameRoot)
if(-not (Test-Path -LiteralPath (Join-Path $game 'MonsterHunterWilds.exe'))){throw 'Invalid game directory.'}
$mailbox=Join-Path $game 'reframework/data/MotionVfxLive'
$uiPath=Join-Path $mailbox 'ui-status.json'
# A closed game's final diagnostic can still describe in-memory overrides.
# Those references no longer exist; only a running game can require restoration.
if((Get-Process -Name MonsterHunterWilds -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $uiPath)){
    $ui=Get-Content -LiteralPath $uiPath -Raw|ConvertFrom-Json
    if($ui.pending -or $ui.aiBusy -or $ui.applyRequested -or $ui.bindings.busy -or $ui.bindings.appliedCount -gt 0 -or
        $ui.bindings.requiresRestoreBeforeReset -or $ui.actionPreview.busy -or $ui.actionPreview.requiresRestore -or $ui.hasDrafts){
        throw 'The editor is busy or holds unrestored references; no files were changed.'
    }
}
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backup=Join-Path $mailbox ('backups/action-preview-'+$stamp)
$mapping=@(
    @('game-plugin/MotionVfxLive/fx_action_preview_transaction.lua','reframework/autorun/MotionVfxLive/fx_action_preview_transaction.lua'),
    @('game-plugin/MotionVfxLive/fx_action_preview_runtime.lua','reframework/autorun/MotionVfxLive/fx_action_preview_runtime.lua'),
    @('game-plugin/MotionVfxLive/fx_action_sources.lua','reframework/autorun/MotionVfxLive/fx_action_sources.lua'),
    @('game-plugin/MotionVfxLive/fx_d2d_workbench.lua','reframework/autorun/MotionVfxLive/fx_d2d_workbench.lua'),
    @('game-plugin/手动重载MotionVfxLive.lua','reframework/data/MotionVfxLive/Reload.lua'),
    @('game-plugin/MotionVfxLive.lua','reframework/autorun/MotionVfxLive.lua')
)
function Copy-Bytes([string]$Source,[string]$Target){
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Target))|Out-Null
    $inputStream=[IO.File]::OpenRead($Source)
    try{$outputStream=[IO.File]::Open($Target,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$inputStream.CopyTo($outputStream);$outputStream.Flush($true)}finally{$outputStream.Dispose()}
    }finally{$inputStream.Dispose()}
}
$changes=[Collections.Generic.List[object]]::new()
try{
    foreach($item in $mapping){
        $source=Join-Path $project $item[0]
        $target=[IO.Path]::GetFullPath((Join-Path $game $item[1]))
        if(-not $target.StartsWith($game.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Target escaped game directory.'}
        $saved=$null
        if(Test-Path -LiteralPath $target){$saved=Join-Path $backup $item[1];Copy-Bytes $target $saved}
        $record=[ordered]@{target=$target;backup=$saved;sha256=(Get-FileHash -LiteralPath $source).Hash}
        $changes.Add($record)
        Copy-Bytes $source $target
        if((Get-FileHash -LiteralPath $target).Hash -ne $record.sha256){throw 'Installed script checksum mismatch.'}
    }
}catch{
    for($i=$changes.Count-1;$i -ge 0;$i--){$record=$changes[$i]
        if($record.backup){Copy-Bytes $record.backup $record.target}
        elseif(Test-Path -LiteralPath $record.target){Remove-Item -LiteralPath $record.target}
    }
    throw
}
$manifest=[ordered]@{installedAt=[DateTime]::UtcNow.ToString('o');uiVersion='0.7.2';backendVersion='0.5.2';files=$changes;requiresControlledReload=$true}
$manifest|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $backup 'manifest.json') -Encoding utf8
$manifest|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'test-output/action-preview-installed.json') -Encoding utf8
Write-Output ('Installed six Lua files; backup: '+$backup)
Write-Output 'The running Lua controller was not reset or reloaded. Run the existing Reload.lua when ready.'
