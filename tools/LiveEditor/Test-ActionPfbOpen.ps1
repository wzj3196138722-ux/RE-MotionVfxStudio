# Read-only installed-file regression: the mailbox and provenance fixture are
# isolated. This never changes the live editor document or any game resource.
param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [string]$BackendDirectory=(Join-Path $PSScriptRoot 'backend')
)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=Join-Path $PSScriptRoot ('test-output/action-pfb-open-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$proof=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-output/action-037-root-proof.json') -Raw | ConvertFrom-Json -AsHashtable
# Routing fixture for the exact files shown in the bug report. Identities below
# are synthetic in this isolated test, not new evidence about the live actor.
$proof.actionId='isolated-open-regression';$proof.motionId=0;$proof.motionName='wp03_00_000_Loop'
$proof.bankPath='Motion/Player/Weapon/Wp03/wp03_00/wp03_00.motlist';$proof.observedBankPath=$proof.bankPath
$proof.weaponType=3;$proof.weaponId=74
$proof.prefabs[1].path='Art/VFX/EffectProvider/Weapon/it03/epvr_it03.pfb'
$proof.prefabs[1].observedPath=$proof.prefabs[1].path;$proof.prefabs[1].dataContainerId=3
$record=@{id=$proof.actionId;componentKind='character';actorIdentity=$proof.actorIdentity;sceneEpoch=$proof.sceneEpoch;weaponType=3;weaponId=74;
    motion=@{componentIdentity=$proof.motionIdentity;bankId=$proof.bankId;motionId=0;bankResourcePath=$proof.bankPath}}
$ui=@{captureActor=@{characterIdentity=$proof.actorIdentity;sceneEpoch=$proof.sceneEpoch};captureActions=@($record);actionPreview=@{busy=$false;requiresRestore=$false}}
$results=[Collections.Generic.List[object]]::new()
function Request([hashtable]$Data){
    $ui | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $root 'ui-status.json') -Encoding utf8
    $Data.id=[guid]::NewGuid().ToString('N')
    $temp=Join-Path $root 'request.tmp'
    $Data | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temp -Encoding utf8
    Move-Item -LiteralPath $temp -Destination (Join-Path $root 'request.json') -Force
    for($i=0;$i -lt 500;$i++){
        Start-Sleep -Milliseconds 100
        try{$r=Get-Content -LiteralPath (Join-Path $root 'response.json') -Raw | ConvertFrom-Json}catch{continue}
        if($r.id -eq $Data.id){if(-not $r.ok){throw $r.error};return $r}
    }
    throw 'Isolated PFB opening timed out.'
}
$launch=[Diagnostics.ProcessStartInfo]::new((Join-Path ([IO.Path]::GetFullPath($BackendDirectory)) 'VfxStudio.LiveEditor.exe'))
$launch.UseShellExecute=$false;$launch.CreateNoWindow=$true;$launch.WindowStyle='Hidden';$launch.WorkingDirectory=$project
foreach($value in @('--assets',(Join-Path $project 'Assets'),'--mailbox',$root,'--output',(Join-Path $root 'output'),'--game-root',$GameRoot,'--log-directory',$root)){$launch.ArgumentList.Add($value)}
$process=[Diagnostics.Process]::Start($launch)
try{
    for($i=0;$i -lt 100;$i++){
        if($process.HasExited){throw 'Isolated backend exited.'}
        if(Test-Path -LiteralPath (Join-Path $root 'status.json')){break}
        Start-Sleep -Milliseconds 100
    }
    foreach($index in @(0,1,0)){
        $r=Request @{action='open_action';source=$proof;prefabIndex=$index}
        $doc=$r.actionDocument
        if($doc.pfbPath -ne $proof.prefabs[$index].path -or $doc.dataContainerId -ne $proof.prefabs[$index].dataContainerId -or @($doc.templates).Count -lt 1){throw 'Opened document did not match its selected PFB.'}
        $results.Add(@{path=$doc.pfbPath;dataContainerId=$doc.dataContainerId;motionName=$doc.name;templateCount=@($doc.templates).Count;passed=$true})
        Write-Output ('PASS '+$doc.pfbPath+'; templates='+@($doc.templates).Count)
    }
    @{type='isolated API + real installed files; synthetic actor proof';results=$results} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'result.json') -Encoding utf8
    Write-Output ('Report: '+(Join-Path $root 'result.json'))
}finally{
    if(-not $process.HasExited){$null=Request @{action='shutdown'};if(-not $process.WaitForExit(8000)){throw 'Isolated backend did not stop.'}}
    $process.Dispose()
}
