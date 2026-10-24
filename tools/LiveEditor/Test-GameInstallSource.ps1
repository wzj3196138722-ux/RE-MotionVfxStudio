param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [string]$BackendPath,
    [string]$ResourcePath = 'art/vfx/effecteditor/weapon/it02/11_it02_000.efx'
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $BackendPath) { $BackendPath = Join-Path $projectRoot 'VfxStudio.LiveEditor/bin/Release/net8.0/VfxStudio.LiveEditor.exe' }
$gamePath = (Resolve-Path -LiteralPath $GameRoot).Path
$ui = Get-Content -LiteralPath (Join-Path $gamePath 'reframework/data/MotionVfxLive/ui-status.json') -Raw | ConvertFrom-Json
$resource = @($ui.captureResources | Where-Object { $_.canonicalPath -eq $ResourcePath -and $_.localConfirmed -eq $true })
if ($resource.Count -ne 1 -or -not $resource[0].latestSource) { throw 'Expected one retained, locally confirmed action resource.' }
$descriptor = $resource[0].latestSource
if ($descriptor.sceneEpoch -ne $ui.captureActor.sceneEpoch) { throw 'Captured resource belongs to another scene.' }
$runRoot = Join-Path $PSScriptRoot ('test-output/game-install-source-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$mailbox = Join-Path $runRoot 'mailbox'
New-Item -ItemType Directory -Path $mailbox -Force | Out-Null
$arguments = '--mailbox "{0}" --assets "{1}" --game-root "{2}" --output "{3}"' -f $mailbox,(Join-Path $projectRoot 'Assets'),$gamePath,(Join-Path $runRoot 'saved')
# This separate mailbox never issues publish, bindings, Lua, or game commands.
$process = Start-Process -FilePath $BackendPath -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'stdout.log') -RedirectStandardError (Join-Path $runRoot 'stderr.log')
function Request([hashtable]$Data) {
    $Data.id = [Guid]::NewGuid().ToString('N')
    $temporary = Join-Path $mailbox ($Data.id + '.tmp')
    $Data | ConvertTo-Json -Depth 12 -Compress | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination (Join-Path $mailbox 'request.json') -Force
    for ($n=0; $n -lt 600; $n++) {
        Start-Sleep -Milliseconds 50
        if ($process.HasExited -and $Data.action -ne 'shutdown') { throw 'Test backend exited unexpectedly.' }
        try { $response = Get-Content -LiteralPath (Join-Path $mailbox 'response.json') -Raw | ConvertFrom-Json } catch { continue }
        if ($response.id -ne $Data.id) { continue }
        if (-not $response.ok) { throw ('Test request failed: ' + $response.error) }
        return $response
    }
    throw ('Test backend timed out: ' + $Data.action)
}
try {
    $ready = $false
    for ($n=0; $n -lt 80; $n++) {
        Start-Sleep -Milliseconds 100
        try { $status = Get-Content -LiteralPath (Join-Path $mailbox 'status.json') -Raw | ConvertFrom-Json } catch { continue }
        if ($status.ready -and $status.pid -eq $process.Id) { $ready=$true; break }
    }
    if (-not $ready) { throw 'Test backend did not become ready.' }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $opened = Request @{ action='open_game_resource'; sourceDescriptor=$descriptor; currentSceneEpoch=$ui.captureActor.sceneEpoch }
    $timer.Stop()
    $doc = $opened.document
    if ($doc.sourceKind -ne 'game_files' -or $doc.gameSource.actualPath -ne $descriptor.actualPath) { throw 'Game-file provenance was not preserved.' }
    $inspected = Request @{ action='inspect'; target=@{kind='entry';index=0} }
    if (@($inspected.fields).Count -eq 0) { throw 'Extracted document has no inspectable entry fields.' }
    $saved = Request @{ action='save'; revision=$doc.revision }
    if ($saved.document.sourceKind -ne 'game_files') { throw 'Saving lost the source association.' }
    $reopened = Request @{ action='open'; path=$saved.outputPath }
    if ($reopened.document.entries.Count -ne $doc.entries.Count -or $reopened.document.actions.Count -ne $doc.actions.Count) { throw 'Saved document structure changed.' }
    $report = [ordered]@{
        type='isolated mailbox reading real game installation'; backendVersion=$status.version
        testedAt=[DateTimeOffset]::UtcNow.ToString('o'); resource=$ResourcePath; gameSource=$doc.gameSource
        openSeconds=$timer.Elapsed.TotalSeconds; entries=$doc.entries.Count; actions=$doc.actions.Count
        entryNames=@($doc.entries | ForEach-Object name); inspectedFields=@($inspected.fields).Count
        savedCopy=$saved.outputPath; sourcePreservedOnSave=$true; savedStructureVerified=$true
        gameFilesModified=$false; gameResourceApplied=$false; liveEditorMailboxModified=$false
    }
    $reportPath = Join-Path $runRoot 'result.json'
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output ('PASS actual installation source: ' + $reportPath)
    Write-Output ('Origin: ' + $doc.gameSource.originPath + '; entries=' + $doc.entries.Count + '; seconds=' + $timer.Elapsed.TotalSeconds)
} finally {
    if (-not $process.HasExited) {
        try { Request @{action='shutdown'} | Out-Null; $process.WaitForExit(5000) | Out-Null }
        catch { Write-Warning ('Test backend may still be running; PID=' + $process.Id + '. ' + $_.Exception.Message) }
    }
}
