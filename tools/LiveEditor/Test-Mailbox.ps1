param([string]$BackendPath, [string]$SamplePath)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $BackendPath) { $BackendPath = Join-Path $projectRoot 'VfxStudio.LiveEditor/bin/Release/net8.0/VfxStudio.LiveEditor.exe' }
if (-not $SamplePath) { $SamplePath = Join-Path $projectRoot 'Assets/WeaponBases/wp02/11_pl_touch_w_006.efx.5571972' }
$runRoot = Join-Path $PSScriptRoot ('test-output/mailbox-' + [Guid]::NewGuid().ToString('N'))
$mailbox = Join-Path $runRoot 'mailbox'
$game = Join-Path $runRoot 'mock-game'
$output = Join-Path $runRoot 'output'
New-Item -ItemType Directory -Path $mailbox,$game,$output -Force | Out-Null
$beforeHash = (Get-FileHash -LiteralPath $SamplePath -Algorithm SHA256).Hash
$arguments = '--mailbox "{0}" --assets "{1}" --game-root "{2}" --output "{3}"' -f $mailbox,(Join-Path $projectRoot 'Assets'),$game,$output
$process = Start-Process -FilePath $BackendPath -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'stdout.log') -RedirectStandardError (Join-Path $runRoot 'stderr.log')
$results = [Collections.Generic.List[object]]::new()
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL: ' + $Name) }
    $results.Add(@{name=$Name;passed=$true})
    Write-Output ('PASS: ' + $Name)
}
function Send-Request([hashtable]$Data) {
    if (-not $Data.ContainsKey('id')) { $Data.id = [Guid]::NewGuid().ToString('N') }
    $temporary = Join-Path $mailbox 'request.tmp'
    $Data | ConvertTo-Json -Depth 16 -Compress | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination (Join-Path $mailbox 'request.json') -Force
    for ($n=0; $n -lt 200; $n++) {
        Start-Sleep -Milliseconds 50
        $responseFile = Join-Path $mailbox 'response.json'
        if (Test-Path -LiteralPath $responseFile) {
            try {
                $response = Get-Content -LiteralPath $responseFile -Raw -Encoding utf8 | ConvertFrom-Json
                if ($response.id -eq $Data.id) { return $response }
            } catch { }
        }
        if ($process.HasExited) { throw 'Backend exited during integration test.' }
    }
    throw ('Mailbox response timed out for ' + $Data.action)
}
try {
    $ready=$false
    for ($n=0; $n -lt 60; $n++) {
        Start-Sleep -Milliseconds 100
        if (Test-Path -LiteralPath (Join-Path $mailbox 'status.json')) {
            $status=Get-Content -LiteralPath (Join-Path $mailbox 'status.json') -Raw -Encoding utf8 | ConvertFrom-Json
            if ($status.ready) { $ready=$true; break }
        }
    }
    Check $ready 'background process reports readiness'
    $catalog=Send-Request @{action='catalog'}
    Check ($catalog.ok -and $catalog.catalog.Count -gt 0) 'catalog reads real project EFX files'
    $opened=Send-Request @{action='open';path=$SamplePath}
    Check ($opened.ok -and $opened.document.entries.Count -gt 0) 'real EFX opens over file mailbox'
    $entryCount=$opened.document.entries.Count
    $oldName=$opened.document.entries[0].name
    $inspected=Send-Request @{action='inspect';target=@{kind='entry';index=0}}
    Check ($inspected.ok -and @($inspected.fields | Where-Object {$_.path -eq 'name' -and $_.editable}).Count -eq 1) 'production inspector exposes editable entry name'
    $set=Send-Request @{action='set';revision=$opened.document.revision;target=@{kind='entry';index=0};path='name';value='游戏内编辑验证_α'}
    Check ($set.ok -and $set.document.entries[0].name -eq '游戏内编辑验证_α') 'Unicode edit survives actual EFX serialization'
    $stale=Send-Request @{action='set';revision=$opened.document.revision;target=@{kind='entry';index=0};path='name';value='obsolete'}
    Check (-not $stale.ok) 'stale revision is rejected'
    $duplicateRequest=@{action='duplicate_entry';index=0;revision=$set.document.revision;id=[Guid]::NewGuid().ToString('N')}
    $duplicated=Send-Request $duplicateRequest
    Check ($duplicated.ok -and $duplicated.document.entries.Count -eq ($entryCount+1)) 'entry duplication is persisted'
    $same=Send-Request $duplicateRequest
    Check ($same.ok -and $same.document.entries.Count -eq ($entryCount+1) -and $same.revision -eq $duplicated.revision) 'same request ID never duplicates mutation'
    $undo=Send-Request @{action='undo';revision=$duplicated.document.revision}
    Check ($undo.ok -and $undo.document.entries.Count -eq $entryCount) 'undo restores structure'
    $redo=Send-Request @{action='redo';revision=$undo.document.revision}
    Check ($redo.ok -and $redo.document.entries.Count -eq ($entryCount+1)) 'redo restores duplicated entry'
    $saved=Send-Request @{action='save';revision=$redo.document.revision}
    Check ($saved.ok -and (Test-Path -LiteralPath $saved.outputPath)) 'save produces a separate valid file'
    $protected=Send-Request @{action='save';revision=$redo.document.revision;path=$SamplePath}
    Check (-not $protected.ok) 'original imported file cannot be overwritten'
    $published=Send-Request @{action='publish';revision=$redo.document.revision}
    Check ($published.ok -and $published.resourcePath.StartsWith('Art/VFX/EffectEditor/MotionVfxLive/') -and (Test-Path -LiteralPath $published.outputPath)) 'publish uses dedicated versioned game resource path'
    $reopened=Send-Request @{action='open';path=$saved.outputPath}
    Check ($reopened.ok -and $reopened.document.entries[0].name -eq '游戏内编辑验证_α' -and $reopened.document.entries.Count -eq ($entryCount+1)) 'saved file reopens with edited value and structure'
    Check ((Get-FileHash -LiteralPath $SamplePath -Algorithm SHA256).Hash -eq $beforeHash) 'source asset hash is unchanged'
    [ordered]@{type='offline real-EFX mailbox integration';sample=$SamplePath;sourceSha256=$beforeHash;checks=$results;gamePathIsMock=$true} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8
    Write-Output ('RESULT mailbox: ' + $results.Count + ' passed; report ' + (Join-Path $runRoot 'results.json'))
} finally {
    if (-not $process.HasExited) {
        try { Send-Request @{action='shutdown'} | Out-Null; $process.WaitForExit(5000) | Out-Null }
        catch { Write-Warning ('Test backend still may be running; PID=' + $process.Id + '. ' + $_.Exception.Message) }
    }
}
