# Isolated mailbox test. Reads installed action/PFB files; never writes to the game.
param([Parameter(Mandatory=$true)][string]$GameRoot)
$ErrorActionPreference='Stop'
$project=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$exe=Join-Path $PSScriptRoot 'prepared-slot-delete-0.5.4/backend/VfxStudio.LiveEditor.exe'
$fixture=Join-Path $PSScriptRoot ('test-output/slot-recapture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
$proof=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-output/action-037-root-proof.json') -Raw|ConvertFrom-Json -AsHashtable
$record=@{id='new-record-after-recapture';componentKind='character';actorIdentity=$proof.actorIdentity;sceneEpoch=$proof.sceneEpoch;weaponType=$proof.weaponType;weaponId=$proof.weaponId;motion=@{componentIdentity=$proof.motionIdentity;motionId=$proof.motionId;bankId=$proof.bankId;bankResourcePath=$proof.bankPath}}
$ui=@{captureActor=@{characterIdentity=$proof.actorIdentity;sceneEpoch=$proof.sceneEpoch};captureActions=@($record);actionPreview=@{busy=$false;requiresRestore=$false}}
function Write-Ui {$ui|ConvertTo-Json -Depth 20|Set-Content -LiteralPath (Join-Path $fixture 'ui-status.json') -Encoding utf8}
function Request([hashtable]$data){
  Write-Ui
  $data.id=[guid]::NewGuid().ToString('N')
  $temp=Join-Path $fixture 'request.tmp'
  $data|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $temp -Encoding utf8
  Move-Item -LiteralPath $temp -Destination (Join-Path $fixture 'request.json') -Force
  for($i=0;$i -lt 500;$i++){
    Start-Sleep -Milliseconds 100
    if(Test-Path -LiteralPath (Join-Path $fixture 'response.json')){
      $r=Get-Content -LiteralPath (Join-Path $fixture 'response.json') -Raw|ConvertFrom-Json
      if($r.id -eq $data.id){return $r}
    }
  }
  throw 'Isolated test request timed out'
}
$arguments=@('--assets',('"'+(Join-Path $project 'Assets')+'"'),'--mailbox',('"'+$fixture+'"'),'--output',('"'+(Join-Path $fixture 'output')+'"'),'--game-root',('"'+$GameRoot+'"'))
$process=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $fixture 'stdout.txt') -RedirectStandardError (Join-Path $fixture 'stderr.txt')
try {
  for($i=0;$i -lt 150;$i++){if(Test-Path -LiteralPath (Join-Path $fixture 'status.json')){break};Start-Sleep -Milliseconds 100}
  $opened=Request @{action='open_action';source=$proof;prefabIndex=0}
  if(-not $opened.ok){throw $opened.error}
  $record.motion.motionId=38
  $wrong=Request @{action='open_action';source=$proof;prefabIndex=0}
  if($wrong.ok){throw 'Different action accepted'}
  $record.motion.motionId=$proof.motionId
  $ui.actionPreview.requiresRestore=$true
  $blocked=Request @{action='reset_action_slots';actionRevision=0}
  if($blocked.ok){throw 'Reset while applied accepted'}
  $ui.actionPreview.requiresRestore=$false
  $restored=Request @{action='reset_action_slots';actionRevision=0}
  if(-not $restored.ok -or $restored.actionDocument.totalAddedSlots -ne 0){throw 'Reset failed'}
  [pscustomobject]@{recapturedRecordAccepted=$true;differentActionRejected=$true;resetWhileAppliedRejected=$true;resetAfterRestoreAccepted=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $fixture 'result.json') -Encoding utf8
  Write-Output ('PASS isolated recapture/reset checks: '+$fixture)
} finally {
  if(-not $process.HasExited){$null=Request @{action='shutdown'};if(-not $process.WaitForExit(8000)){throw 'Isolated backend did not shut down'}}
}
