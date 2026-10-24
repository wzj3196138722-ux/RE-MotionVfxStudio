param([string]$BackendDirectory=(Join-Path $PSScriptRoot 'prepared-editor-0.8.0/backend'))
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=Join-Path $PSScriptRoot ('test-output/pfb-api-'+[guid]::NewGuid().ToString('N'))
$mailbox=Join-Path $root 'mailbox'
$game=Join-Path $root 'isolated-game'
New-Item -ItemType Directory -Path $mailbox,$game -Force | Out-Null
'LooseFileLoader_Enabled=true' | Set-Content -LiteralPath (Join-Path $game 're2_fw_config.txt') -Encoding utf8
$arguments=@('--assets',(Join-Path $project 'Assets'),'--mailbox',$mailbox,'--output',(Join-Path $root 'output'),'--game-root',$game)
$line=($arguments | ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) -join ' '
$process=Start-Process -FilePath (Join-Path ([IO.Path]::GetFullPath($BackendDirectory)) 'VfxStudio.LiveEditor.exe') -ArgumentList $line -WorkingDirectory $project -WindowStyle Hidden -PassThru
$checks=0
function Check([bool]$Ok,[string]$Message){if(-not $Ok){throw $Message};$script:checks++;Write-Output ('PASS '+$Message)}
function Request([string]$Action,[hashtable]$Data=@{},[bool]$Success=$true){
    $Data.id=[guid]::NewGuid().ToString('N');$Data.action=$Action
    $temporary=Join-Path $mailbox 'request.tmp';$Data | ConvertTo-Json -Depth 20 -Compress | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination (Join-Path $mailbox 'request.json') -Force
    for($i=0;$i -lt 200;$i++){
        Start-Sleep -Milliseconds 50
        try{$r=Get-Content -LiteralPath (Join-Path $mailbox 'response.json') -Raw | ConvertFrom-Json}catch{continue}
        if($r.id -ne $Data.id){continue}
        if($r.ok -ne $Success){throw ($Action+': '+$r.error)}
        return $r
    }
    throw ('No result for '+$Action)
}
try {
    for($i=0;$i -lt 200;$i++){
        if($process.HasExited){throw 'Test backend exited before readiness'}
        try{$ready=Get-Content -LiteralPath (Join-Path $mailbox 'status.json') -Raw | ConvertFrom-Json}catch{$ready=$null}
        if($ready.ready -and $ready.pid -eq $process.Id){break}
        Start-Sleep -Milliseconds 100
    }
    if(-not $ready.ready){throw 'Test backend did not become ready'}
    $efx=Get-Item -LiteralPath (Join-Path $project 'Assets/WeaponBases/wp02/11_it02_000.efx.5571972')
    $first=Request 'open' @{path=$efx.FullName}
    $groupPath='art/validation/groups.efx'
    $groupFile=Join-Path $game ('natives/stm/'+$groupPath+'.5571972')
    New-Item -ItemType Directory -Path (Split-Path $groupFile -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $efx.FullName -Destination $groupFile
    $groups=Request 'action_effect_groups' @{resourcePath=$groupPath}
    $expected=@($first.document.groups | ForEach-Object { $_.name } | Select-Object -Unique)
    $actual=@($groups.effectGroups.groups)
    Check ($expected.Count -ge 3 -and ($expected -join '|') -ceq ($actual -join '|')) 'Action dropdown returns all actual EFX group names without changing the current document'
    $effect=Get-ChildItem -LiteralPath (Join-Path $project 'Assets/WeaponBases/wp02') -Filter 'epvr_it02.pfb.18' -Recurse -File | Select-Object -First 1
    $opened=Request 'pfb_open' @{path=$effect.FullName}
    Check ($opened.pfbDocument.type -eq 'effect') 'Backend routes PFB independently and detects effect type'
    $copy=Request 'pfb_copy' @{pfbRevision=$opened.pfbDocument.revision;slotKey=$opened.pfbDocument.slots[0].key}
    $paste=Request 'pfb_paste' @{pfbRevision=$copy.pfbDocument.revision;slotKey=$copy.pfbDocument.slots[0].key}
    Check ($paste.pfbDocument.slots.Count -eq $opened.pfbDocument.slots.Count+1) 'Clipboard paste adds exactly one slot through the API'
    Check ($paste.document.source -eq $first.document.source -and $paste.document.revision -eq $first.document.revision) 'PFB editing preserves the EFX document and its revision'
    $failed=Request 'pfb_remove' @{pfbRevision=$opened.pfbDocument.revision;slotKey=$paste.pfbDocument.slots[0].key} $false
    Check ($failed.error -match '变化|过期') 'Stale PFB revision cannot delete a slot'
    $equipment=Request 'pfb_open' @{path=(Join-Path $PSScriptRoot 'test-output/pfb-editor-fixtures/it0200_0000_0.pfb.18')}
    Check ($equipment.pfbDocument.type -eq 'equipment' -and $equipment.pfbDocument.addKinds.Count -eq 4) 'Equipment API exposes all four supported resource slots'
    $reopen=Request 'pfb_open' @{path=$effect.FullName}
    Check ($reopen.pfbDocument.revision -eq $paste.pfbDocument.revision -and $reopen.pfbDocument.slots.Count -eq $paste.pfbDocument.slots.Count) 'Switching PFB files retains committed edits'
    $bad=Request 'pfb_open' @{path=(Join-Path $root 'missing.pfb.18')} $false
    $reset=Request 'pfb_reset' @{pfbRevision=$reopen.pfbDocument.revision}
    Check ($reset.pfbDocument.slots.Count -eq $opened.pfbDocument.slots.Count) 'Failed open preserves the previous PFB and reset restores its baseline'
    $proof=@{originalPath='art/validation/target.pfb';actorIdentity='test';sceneEpoch=1;weaponId=0;weaponType=0}
    $bad=Request 'pfb_publish' @{pfbRevision=$reset.pfbDocument.revision;proof=$proof} $false
    Check ($bad.error -match '离线') ('Offline PFB publication is rejected before writing game files: '+$bad.error)
    @{captureActor=@{characterIdentity='test';sceneEpoch=1;weaponId=0;weaponType=0}} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $mailbox 'ui-status.json') -Encoding utf8
    $published=Request 'pfb_publish' @{pfbRevision=$reset.pfbDocument.revision;proof=$proof}
    $resource=$published.pfbPreview.resources[0]
    $native=Join-Path $game ('natives/stm/'+$resource.path+'.18')
    Check ((Test-Path -LiteralPath $native) -and (Get-FileHash -LiteralPath $native).Hash -eq $resource.sha256) 'PFB publication verifies complete content in a unique isolated revision'
    Check ($published.pfbPreview.source.pfbType -eq 'effect' -and $published.pfbPreview.source.expectedElements.Count -gt 0) 'Runtime receives the actual expected provider elements'
    Request 'shutdown' | Out-Null
    if(-not $process.WaitForExit(8000)){throw 'Test backend did not shut down normally'}
    Write-Output ('RESULT '+$checks+' PFB API checks passed; no game resources changed; '+$root)
} finally {
    if(-not $process.HasExited){try{Request 'shutdown' | Out-Null;$process.WaitForExit(8000)|Out-Null}catch{Write-Warning $_}}
}
