param([Parameter(Mandatory=$true)][string]$GameRoot, [string]$PreparedDirectory, [string]$RecoveryFile)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$gamePath = (Resolve-Path -LiteralPath $GameRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $gamePath 'MonsterHunterWilds.exe'))) { throw 'Game directory does not contain MonsterHunterWilds.exe.' }
$mailbox = Join-Path $gamePath 'reframework/data/MotionVfxLive'
$statusPath = Join-Path $mailbox 'status.json'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$staging = Join-Path $PSScriptRoot ('test-output/backend-upgrade-' + $stamp)
$published = Join-Path $staging 'published'
# Build first. A build failure leaves the running editor and its document alone.
if ($PreparedDirectory) {
    $published=(Resolve-Path -LiteralPath $PreparedDirectory).Path
    if (-not (Test-Path -LiteralPath (Join-Path $published 'VfxStudio.LiveEditor.exe')) -or
        -not (Test-Path -LiteralPath (Join-Path $published 'desktop/MotionVfxDesktop.exe'))) { throw 'Prepared backend or desktop bridge is missing.' }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
} else {
    & dotnet publish (Join-Path $projectRoot 'VfxStudio.LiveEditor/VfxStudio.LiveEditor.csproj') -c Release -p:MotionVfxPortable=true -o $published --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Backend build failed; no running backend was stopped.' }
    & dotnet publish (Join-Path $projectRoot 'tools/DesktopBridge/MotionVfxDesktop.csproj') -c Release -o (Join-Path $published 'desktop') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Desktop bridge build failed; no running backend was stopped.' }
}
function Request([hashtable]$Data) {
    $id = 'upgrade-' + [Guid]::NewGuid().ToString('N')
    $Data.id = $id
    $requestPath = Join-Path $mailbox 'request.json'
    $tempPath = Join-Path $mailbox ($id + '.tmp')
    $Data | ConvertTo-Json -Depth 10 -Compress | Set-Content -LiteralPath $tempPath -Encoding utf8
    Move-Item -LiteralPath $tempPath -Destination $requestPath -Force
    for ($n = 0; $n -lt 200; $n++) {
        Start-Sleep -Milliseconds 100
        try { $response = Get-Content -LiteralPath (Join-Path $mailbox 'response.json') -Raw -Encoding utf8 | ConvertFrom-Json } catch { continue }
        if ($response.id -ne $id) { continue }
        if (-not $response.ok) { throw ('Backend rejected ' + $Data.action + ': ' + $response.error) }
        return $response
    }
    throw ('No response to ' + $Data.action + '; no forced process termination will be attempted.')
}
$checkpoint = $null
$originalDocument = $null
function Copy-BackendContent([string]$Source,[string]$Target) {
    New-Item -ItemType Directory -Path (Split-Path $Target -Parent) -Force | Out-Null
    $inputFile=[IO.File]::OpenRead($Source)
    try {
        $outputFile=[IO.File]::Open($Target,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $inputFile.CopyTo($outputFile);$outputFile.Flush($true) } finally { $outputFile.Dispose() }
    } finally { $inputFile.Dispose() }
}
if ($RecoveryFile) {
    $recovery = Get-Content -LiteralPath $RecoveryFile -Raw -Encoding utf8 | ConvertFrom-Json
    if (-not $recovery.checkpoint -or -not (Test-Path -LiteralPath $recovery.checkpoint -PathType Leaf)) { throw 'The requested recovery checkpoint is missing.' }
    $checkpoint = $recovery.checkpoint
    $originalDocument = $recovery.document
    Copy-BackendContent $RecoveryFile (Join-Path $staging 'prior-saved-session.json')
}
if (Test-Path -LiteralPath $statusPath) {
    $status = Get-Content -LiteralPath $statusPath -Raw -Encoding utf8 | ConvertFrom-Json
    $age = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - [long]$status.heartbeatUnixSeconds
    if ($status.ready -and $age -ge -5 -and $age -le 8) {
        if ([IO.Path]::GetFullPath($status.mailbox).TrimEnd('\') -ne $mailbox.TrimEnd('\')) { throw 'Unexpected mailbox identity.' }
        $running = Get-Process -Id $status.pid -ErrorAction Stop
        if ($running.ProcessName -ne 'VfxStudio.LiveEditor') { throw 'Status PID is not the live editor backend.' }
        $uiFile = Join-Path $mailbox 'ui-status.json'
        if (Test-Path -LiteralPath $uiFile) {
            $ui = Get-Content -LiteralPath $uiFile -Raw -Encoding utf8 | ConvertFrom-Json
            if ($ui.pendingId -or $ui.pending) { throw 'Wait for the active UI request to finish before upgrading.' }
            if ($ui.actionPreview.busy -or $ui.actionPreview.requiresRestore) { throw 'Restore the action/PFB preview before upgrading.' }
            if ($ui.pfbPreview.busy -or $ui.pfbPreview.requiresRestore) { throw 'Restore the independent PFB preview before upgrading.' }
        }
        $current = Request @{action='status'}
        $originalDocument = $current.document
        if ($current.document) {
            $checkpoint = (Request @{action='save';revision=$current.document.revision}).outputPath
            $latest = Request @{action='status'}
            if ($latest.document.revision -ne $current.document.revision) { throw 'Document changed during backup; upgrade cancelled. Retry after finishing the edit.' }
        }
        [ordered]@{at=[DateTime]::UtcNow.ToString('o');originalDocument=$originalDocument;checkpoint=$checkpoint;oldVersion=$status.version;oldPid=$status.pid} |
            ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $staging 'recovery.json') -Encoding utf8
        Request @{action='shutdown'} | Out-Null
        if (-not $running.WaitForExit(10000)) { throw 'Backend did not stop; binaries were not replaced.' }
    }
}
$destination = Join-Path $PSScriptRoot 'backend'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $published -Recurse -File) {
    $relative = $file.FullName.Substring($published.TrimEnd('\').Length+1)
    $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (-not $target.StartsWith([IO.Path]::GetFullPath($destination).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Backend copy escaped its destination.' }
    Copy-BackendContent $file.FullName $target
    if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw ('Backend copy failed verification: '+$relative) }
}
& (Join-Path $PSScriptRoot 'Start-Backend.ps1') -GameRoot $gamePath
if ($checkpoint) {
    # A queued UI operation may resume as soon as the new heartbeat is ready.
    # Let that operation finish before reopening the checkpoint through the
    # shared request mailbox; otherwise the two clients overwrite requests.
    $idleSince = $null
    $idleDeadline = [DateTime]::UtcNow.AddSeconds(35)
    do {
        Start-Sleep -Milliseconds 200
        if (-not (Get-Process -Name MonsterHunterWilds -ErrorAction SilentlyContinue)) { break }
        $ui = Get-Content -LiteralPath (Join-Path $mailbox 'ui-status.json') -Raw -Encoding utf8 | ConvertFrom-Json
        $fresh = [Math]::Abs([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - [long]$ui.observedAt) -le 8
        if ($fresh -and -not $ui.pendingId -and -not $ui.pending -and -not $ui.actionPreview.busy) {
            if (-not $idleSince) { $idleSince = [DateTime]::UtcNow }
        } else { $idleSince = $null }
        if ([DateTime]::UtcNow -gt $idleDeadline) { throw ('UI operation did not finish; preserved checkpoint: ' + $checkpoint) }
    } until ($idleSince -and ([DateTime]::UtcNow - $idleSince).TotalSeconds -ge 2)
    $reopened = Request @{action='open';path=$checkpoint}
    if (-not $reopened.document -or $reopened.document.entries.Count -ne $originalDocument.entries.Count -or $reopened.document.actions.Count -ne $originalDocument.actions.Count) {
        throw ('Checkpoint reload structure differs. Preserved recovery file: ' + $checkpoint)
    }
    if ($originalDocument.sourceKind -eq 'game_files' -and $originalDocument.gameSource) {
        try {
            $latestUi = Get-Content -LiteralPath (Join-Path $mailbox 'ui-status.json') -Raw -Encoding utf8 | ConvertFrom-Json
            $restored = Request @{ action='restore_game_checkpoint'; checkpointPath=$checkpoint;
                sourceDescriptor=$originalDocument.gameSource; currentSceneEpoch=$latestUi.captureActor.sceneEpoch;
                expectedContentHash=$originalDocument.gameSource.contentHash; revision=$reopened.document.revision }
            if ($restored.document.sourceKind -ne 'game_files' -or $restored.document.entries.Count -ne $originalDocument.entries.Count -or
                $restored.document.actions.Count -ne $originalDocument.actions.Count) { throw 'Restored game-source checkpoint did not match the saved structure.' }
            Write-Output 'Preserved the edited checkpoint bytes and verified game-installation source association.'
        } catch {
            # The saved local checkpoint was already opened. Source verification
            # failures leave those edited bytes available without a role binding.
            Write-Warning ('Edited checkpoint is preserved; game-source association could not be verified: ' + $_.Exception.Message)
        }
    }
    Write-Output ('Preserved the open document in: ' + $checkpoint)
    Write-Output 'The saved checkpoint is now open. Previous undo history is retained only by the stopped session; the checkpoint preserves the current edited bytes.'
}
$recoveryRecord = Join-Path $staging 'recovery.json'
if (-not (Test-Path -LiteralPath $recoveryRecord)) { $recoveryRecord = Join-Path $staging 'prior-saved-session.json' }
Write-Output ('Backend upgrade complete. Recovery record: ' + $recoveryRecord)
