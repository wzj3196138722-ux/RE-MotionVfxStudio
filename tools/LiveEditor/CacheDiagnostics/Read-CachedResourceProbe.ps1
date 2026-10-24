[CmdletBinding(DefaultParameterSetName = 'Probe')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Probe')][string]$GameRoot,
    [Parameter(Mandatory, ParameterSetName = 'Probe')][string]$LogicalResourcePath,
    [Parameter(ParameterSetName = 'Probe')][switch]$InspectDirectPointers,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')][switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -Path @((Join-Path $PSScriptRoot 'EfxBufferValidator.cs'), (Join-Path $PSScriptRoot 'ReadOnlyResourceProbe.cs'))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../test-output'))
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)

function Write-Report([object]$report, [string]$prefix) {
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    $destination = Join-Path $outputRoot ($prefix + '-' + $runId + '.json')
    [IO.File]::WriteAllText($destination, ($report | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Write-Output $destination
}

function Normalize-Efx([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { throw 'Missing logical EFX resource path.' }
    $path = $value.Trim().Replace('\', '/')
    if ($path.StartsWith('@')) { $path = $path.Substring(1).TrimStart('/') }
    if ($path.StartsWith('natives/stm/', [StringComparison]::OrdinalIgnoreCase)) { $path = $path.Substring(12) }
    $path = [regex]::Replace($path, '(?i)\.efx\.\d+$', '.efx')
    if (!$path.EndsWith('.efx', [StringComparison]::OrdinalIgnoreCase) -or $path -match '[:\x00-\x1f*?"<>|]' -or
        @($path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
        throw 'Expected a complete game-relative EFX path, without absolute path, traversal, or wildcards.'
    }
    return $path.ToLowerInvariant()
}

function Read-FreshSource([string]$statusPath, [string]$logical) {
    $file = Get-Item -LiteralPath $statusPath
    if ($file.Length -lt 1 -or $file.Length -gt 8MB) { throw 'UI diagnostics file size is outside the allowed bound.' }
    $snapshot = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json -AsHashtable
    if ($snapshot -isnot [System.Collections.IDictionary] -or $null -eq $snapshot['observedAt']) {
        throw 'No usable capture record/source: UI diagnostics are incomplete. Record the action again and keep its list.'
    }
    $now = [DateTimeOffset]::UtcNow
    $observed = [DateTimeOffset]::FromUnixTimeSeconds([long]$snapshot.observedAt)
    $age = ($now - $observed).TotalSeconds
    if ($age -lt -2 -or $age -gt 8 -or ($now.UtcDateTime - $file.LastWriteTimeUtc).TotalSeconds -gt 8) {
        throw 'UI diagnostics are not fresh (maximum age: 8 seconds). No process memory was accepted.'
    }
    $actor = $snapshot['captureActor']
    if ($snapshot['loaded'] -ne $true -or $actor -isnot [System.Collections.IDictionary] -or $actor['available'] -ne $true) {
        throw 'No usable capture record/source: the active local actor/capture UI is not ready.'
    }
    $resources = $snapshot['captureResources']
    if ($null -eq $resources -or $resources -is [System.Collections.IDictionary] -or $resources -is [string] -or @($resources).Count -eq 0) {
        throw 'No usable capture record/source: the capture list is empty or was cleared. Record the action again and keep its list.'
    }
    $matches = @()
    foreach ($record in $resources) {
        if ($record -isnot [System.Collections.IDictionary] -or [string]::IsNullOrWhiteSpace([string]$record['canonicalPath'])) {
            throw 'No usable capture record/source: a resource record is incomplete. Record the action again and keep its list.'
        }
        if ((Normalize-Efx ([string]$record['canonicalPath'])) -eq $logical) { $matches += $record }
    }
    if ($matches.Count -ne 1) { throw 'Expected exactly one matching resource in current capture diagnostics; record the action again.' }
    $resource = $matches[0]
    $source = $resource['latestSource']
    if ($source -isnot [System.Collections.IDictionary]) {
        throw 'No usable capture record/source: the selected resource has no retained source. Record the action again and keep its list.'
    }
    foreach ($required in @('localConfirmed', 'availability', 'sourceKind', 'sourceId', 'eventId', 'sceneEpoch', 'sharedTargetPath', 'actualPath', 'actualHolderIdentity')) {
        if ($null -eq $source[$required]) {
            throw ('No usable capture record/source: the retained source is incomplete (' + $required + '). Record the action again and keep its list.')
        }
    }
    if ($resource['localConfirmed'] -ne $true -or $source.localConfirmed -ne $true -or
        $source.availability -ne 'retained_observation' -or $source.sourceKind -ne 'actual_effect_player_holder') {
        throw 'The resource has no retained, locally confirmed actual EffectPlayer holder observation.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$source.sourceId) -or [string]::IsNullOrWhiteSpace([string]$source.eventId) -or
        [long]$source.sceneEpoch -lt 1 -or [long]$source.sceneEpoch -ne [long]$snapshot.captureActor.sceneEpoch) {
        throw 'The retained observation is missing its source/event identity or belongs to another scene epoch.'
    }
    if ((Normalize-Efx ([string]$source.sharedTargetPath)) -ne $logical) { throw 'The retained observation does not belong to the requested shared resource.' }
    # actualPath may intentionally be MotionVfxLive/rev_N; preserve that real observation.
    # sharedTargetPath identifies the edited resource, while actualPath identifies what was created.
    $null = Normalize-Efx ([string]$source.actualPath)
    $holderText = [string]$source.actualHolderIdentity
    if ($holderText -match '^0[xX][0-9a-fA-F]+$') { $holder = [Convert]::ToUInt64($holderText.Substring(2), 16) }
    elseif ($holderText -match '^\d+$') { $holder = [UInt64]::Parse($holderText, [Globalization.CultureInfo]::InvariantCulture) }
    else { throw 'The retained holder identity is not an exact integer address.' }
    if (![MotionVfx.CacheDiagnostics.ReadOnlyProcessMemory]::PlausiblePointer($holder)) { throw 'The retained holder identity is outside the allowed address range.' }
    return @{ snapshot = $snapshot; source = $source; holder = $holder; ageSeconds = $age }
}

if ($SelfTest) {
    $checks = @([MotionVfx.CacheDiagnostics.ReadOnlyProcessMemory]::RunSelfFixture())
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    $fixturePath = Join-Path $outputRoot ('cache-probe-source-fixture-' + $runId + '.json')
    $fixtureLogical = 'vfx/fixture/original.efx'
    $fixture = @{
        loaded = $true; loadedAt = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        observedAt = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        captureActor = @{ available = $true; sceneEpoch = 2 }
        captureResources = @(@{
            canonicalPath = $fixtureLogical; localConfirmed = $true
            latestSource = @{
                sourceId = 'synthetic-source-only'; eventId = 'synthetic-event-only'; sceneEpoch = 2
                availability = 'retained_observation'; sourceKind = 'actual_effect_player_holder'; localConfirmed = $true
                actualPath = 'MotionVfxLive/rev_7/vfx/fixture/original.efx'; sharedTargetPath = $fixtureLogical
                actualHolderIdentity = '0x0000000000010000'
            }
        })
    }
    function Write-SourceFixture {
        [IO.File]::WriteAllText($fixturePath, ($fixture | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    }
    function Assert-SourceRejected([string]$reason, [string]$expectedMessage = '') {
        Write-SourceFixture
        $rejected = $false
        try { $null = Read-FreshSource $fixturePath $fixtureLogical } catch {
            $rejected = $true
            if ($expectedMessage -and !$_.Exception.Message.Contains($expectedMessage)) {
                throw ('Synthetic fixture did not return the expected clear error for ' + $reason + ': ' + $_.Exception.Message)
            }
        }
        if (!$rejected) { throw ('Synthetic source fixture was accepted unexpectedly: ' + $reason) }
    }
    Write-SourceFixture
    $valid = Read-FreshSource $fixturePath $fixtureLogical
    if ($valid.source.actualPath -ne 'MotionVfxLive/rev_7/vfx/fixture/original.efx') { throw 'Revision actual path was not preserved.' }
    $checks += 'Fresh retained actual holder accepts a different generated revision path while matching sharedTargetPath'
    $fixture.observedAt -= 20
    Assert-SourceRejected 'stale status'
    $checks += 'Stale source diagnostics rejected'
    $fixture.observedAt = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $fixture.captureResources[0].latestSource.sceneEpoch = 1
    Assert-SourceRejected 'scene epoch mismatch'
    $checks += 'Source from another scene epoch rejected'
    $fixture.captureResources[0].latestSource.sceneEpoch = 2
    $fixture.captureResources[0].latestSource.localConfirmed = $false
    Assert-SourceRejected 'unconfirmed holder'
    $checks += 'Unconfirmed holder observation rejected'
    $fixture.captureResources[0].latestSource.localConfirmed = $true
    $fixture.captureResources[0].latestSource.sourceKind = 'path_lookup'
    Assert-SourceRejected 'path lookup instead of actual effect holder'
    $checks += 'Path-only lookup cannot substitute for actual effect holder observation'
    $fixture.captureResources[0].latestSource.sourceKind = 'actual_effect_player_holder'
    $fixture.captureResources[0].latestSource.actualHolderIdentity = '1.2345E+12'
    Assert-SourceRejected 'rounded scientific notation address'
    $checks += 'Rounded or inexact holder addresses rejected'
    $fixture.captureResources[0].latestSource.actualHolderIdentity = '0x0000000000010000'
    $fixture.captureResources[0].latestSource.sharedTargetPath = 'vfx/fixture/different.efx'
    Assert-SourceRejected 'unrelated shared resource'
    $checks += 'Unrelated shared target rejected'
    $fixture.captureResources[0].latestSource.sharedTargetPath = $fixtureLogical
    $validResources = $fixture.captureResources
    $fixture.captureResources = $null
    Assert-SourceRejected 'null capture list' 'No usable capture record/source:'
    $checks += 'Null capture list returns an explicit no-record/source error before any game handle is opened'
    $fixture.captureResources = @()
    Assert-SourceRejected 'empty capture list' 'No usable capture record/source:'
    $checks += 'Empty capture list returns an explicit no-record/source error'
    $fixture.Remove('captureResources')
    Assert-SourceRejected 'missing capture list' 'No usable capture record/source:'
    $checks += 'Missing capture list returns an explicit no-record/source error'
    $fixture.captureResources = @(@{})
    Assert-SourceRejected 'incomplete resource dictionary' 'No usable capture record/source:'
    $checks += 'Incomplete resource dictionary returns an explicit no-record/source error'
    $fixture.captureResources = @(@{ canonicalPath = $fixtureLogical; localConfirmed = $true })
    Assert-SourceRejected 'missing retained source' 'No usable capture record/source:'
    $checks += 'Resource without a retained source returns an explicit no-record/source error'
    $fixture.captureResources[0].latestSource = @{}
    Assert-SourceRejected 'incomplete retained source' 'No usable capture record/source:'
    $checks += 'Incomplete retained-source dictionary returns an explicit no-record/source error'
    $fixture.captureResources = $validResources
    Write-SourceFixture
    Write-Report ([ordered]@{
        observedAtUtc = [DateTime]::UtcNow.ToString('O')
        status = 'self_process_fixture_passed'
        checks = $checks
        sourceFixturePath = $fixturePath
        gameRead = $false
        nativeProcessWrites = $false
        requestedProcessAccess = '0x410 = QUERY_INFORMATION | VM_READ'
    }) 'cache-probe-self-fixture'
    Write-Output ('Self fixture passed: ' + $checks.Count + ' checks; no game process was accessed.')
    exit 0
}

$reader = $null
$report = [ordered]@{
    observedAtUtc = [DateTime]::UtcNow.ToString('O')
    status = 'not_started'
    authenticatedCacheFile = $false
    usableForEditing = $false
    nativeProcessWrites = $false
    candidateFiles = @()
}
try {
    $resolvedRoot = (Resolve-Path -LiteralPath $GameRoot).Path
    $expectedExecutable = [IO.Path]::GetFullPath((Join-Path $resolvedRoot 'MonsterHunterWilds.exe'))
    if (!(Test-Path -LiteralPath $expectedExecutable -PathType Leaf)) { throw 'Expected game executable was not found in GameRoot.' }
    $logical = Normalize-Efx $LogicalResourcePath
    $statusPath = Join-Path $resolvedRoot 'reframework/data/MotionVfxLive/ui-status.json'
    $before = Read-FreshSource $statusPath $logical
    $processes = @(Get-Process -Name 'MonsterHunterWilds' -ErrorAction Stop)
    if ($processes.Count -ne 1) { throw 'Exactly one MonsterHunterWilds process must exist.' }
    $gameProcess = $processes[0]
    # Get-Process is only used to discover the unique name/PID. Cross-process module
    # enumeration may be unavailable; validate the exact executable and creation time
    # through the same read-only native handle that performs the bounded reads.
    $reader = [MotionVfx.CacheDiagnostics.ReadOnlyProcessMemory]::new($gameProcess.Id, $expectedExecutable)
    $loadedAt = [DateTimeOffset]::FromUnixTimeSeconds([long]$before.snapshot.loadedAt)
    $processStartedAtUtc = $reader.CreationTimeUtc
    if ($loadedAt.UtcDateTime -lt $processStartedAtUtc.AddSeconds(-1)) { throw 'UI diagnostics were loaded before the current game process started.' }
    $report.processId = $reader.ProcessId
    $report.processStartedAtUtc = $processStartedAtUtc.ToString('O')
    $report.logicalResourcePath = $logical
    $report.before = $before.source
    $report.beforeObservedAt = $before.snapshot.observedAt
    $reader.EnsureIdentity()
    $probe = $reader.ProbeRetainedHolder([UInt64]$before.holder, $InspectDirectPointers.IsPresent)
    $after = Read-FreshSource $statusPath $logical
    foreach ($key in @('sourceId', 'eventId', 'sceneEpoch', 'actualHolderIdentity', 'actualPath', 'sharedTargetPath')) {
        if ([string]$before.source[$key] -cne [string]$after.source[$key]) { throw "Retained observation changed during reading: $key." }
    }
    if ([long]$before.snapshot.loadedAt -ne [long]$after.snapshot.loadedAt -or [UInt64]$before.holder -ne [UInt64]$after.holder) {
        throw 'UI lifetime or retained holder identity changed while reading.'
    }
    # Recheck the holder AFTER the fresh source check, not just inside the native-layout probe.
    $finalHolder = $reader.ReadExact([UInt64]$after.holder, 24)
    $finalNative = [MotionVfx.CacheDiagnostics.ReadOnlyProcessMemory]::Hex([MotionVfx.CacheDiagnostics.ReadOnlyProcessMemory]::PointerAt($finalHolder, 16))
    if ($finalNative -cne $probe.NativeResourceAddress) { throw 'Holder native pointer changed after source revalidation.' }
    $reader.EnsureIdentity()
    $report.after = $after.source
    $report.afterObservedAt = $after.snapshot.observedAt
    $report.result = $probe
    $report.status = 'diagnostic_complete_not_authenticated'
    $report.holderLayoutBasis = 'Local REFramework REManagedObject::runtime_size() = 0x10; Resource::create_holder stores Resource* at holder+runtime_size. This is not an EffectResource data-buffer layout.'
    $report.directPointerDepth = $(if ($InspectDirectPointers) { 1 } else { 0 })
    $report.rawNativeBinarySaved = $false
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    $index = 0
    foreach ($candidate in $probe.Candidates) {
        $bytes = $candidate.GetBytes()
        if ($null -eq $bytes -or !$candidate.StableDoubleRead -or !$candidate.Framing.Valid) { continue }
        $candidatePath = Join-Path $outputRoot ('cache-candidate-' + $runId + '-' + $index + '.unauthenticated.bin')
        [IO.File]::WriteAllBytes($candidatePath, $bytes)
        $report.candidateFiles += @{
            path = $candidatePath; bytes = $bytes.Length
            sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            address = $candidate.Address; status = 'UNAUTHENTICATED_DIAGNOSTIC_ONLY_NOT_FOR_EDITING'
        }
        $index++
    }
    Write-Report $report 'cache-resource-probe'
}
catch {
    $report.status = 'rejected'
    $report.error = $_.Exception.GetBaseException().Message
    Write-Report $report 'cache-resource-probe-rejected'
    Write-Error -ErrorAction Continue $report.error
    exit 1
}
finally { if ($null -ne $reader) { $reader.Dispose() } }
