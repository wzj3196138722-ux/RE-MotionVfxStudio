param(
    [Parameter(Mandatory=$true)][string]$Survey,
    [string]$Mapping = 'docs\efx-completion\weapon-state-trigger-map.csv',
    [string]$Candidates = 'docs\efx-completion\weapon-pfb-resource-candidates.csv',
    [string]$MotionResolution = 'docs\efx-completion\motion-efx-resolution.csv',
    [string]$MotionEvents = 'docs\efx-completion\weapon-runtime-motion-events.csv',
    [string]$ResourceRequests = 'docs\efx-completion\weapon-runtime-resource-requests.csv',
    [string]$Output = 'docs\efx-completion\weapon-runtime-coverage.csv',
    [string]$AppEvidence = 'Assets\RuntimeEvidence\weapon-file-coverage.tsv',
    [string[]]$ExcludedSessions = @('wp09_run1')
)

$observed = @{}
if (Test-Path -LiteralPath $Survey) {
    foreach ($line in Get-Content -LiteralPath $Survey) {
        $parts = $line -split "`t"
        if ($parts.Count -lt 8 -or $parts[1] -notin @('PLAY', 'LOOP') -or
            $parts[2] -notmatch '^(wp\d\d)_run\d+$') { continue }
        if ($parts[2] -in $ExcludedSessions) { continue }
        $key = "$($Matches[1])|$($parts[5])"
        if (-not $observed.ContainsKey($key)) {
            $observed[$key] = [pscustomobject]@{ First = $parts[0]; Last = $parts[0]; Count = 0 }
        }
        $observed[$key].Last = $parts[0]
        $observed[$key].Count++
    }
}

$byFile = @{}
$filesByState = @{}
foreach ($row in Import-Csv -LiteralPath $Mapping) {
    $key = "$($row.Weapon)|$($row.LocalEfx)".ToLowerInvariant()
    if (-not $byFile.ContainsKey($key)) { $byFile[$key] = @() }
    $byFile[$key] += $row
    $stateKey = "$($row.Weapon)|$($row.TriggerId)".ToLowerInvariant()
    if (-not $filesByState.ContainsKey($stateKey)) { $filesByState[$stateKey] = @{} }
    $filesByState[$stateKey][$key] = $true
}

$directByFile = @{}
foreach ($row in Import-Csv -LiteralPath $MotionResolution) {
    if ([string]::IsNullOrWhiteSpace($row.'resolved EFX')) { continue }
    $motlistName = [IO.Path]::GetFileName($row.motlist) -replace '\.motlist.*$', ''
    if ($motlistName -notmatch '^wp\d\d_') { continue }
    $weapon = $motlistName.Substring(0, 4)
    $key = "$weapon|$($row.'resolved EFX')".ToLowerInvariant()
    if (-not $directByFile.ContainsKey($key)) { $directByFile[$key] = @{} }
    $directByFile[$key]["${motlistName}_$($row.motion)"] = $true
}

$runtimeMotionByFile = @{}
if (Test-Path -LiteralPath $MotionEvents) {
    foreach ($row in Import-Csv -LiteralPath $MotionEvents) {
        if ([int]$row.CandidateFileCount -ne 1) { continue }
        $key = "$($row.Weapon)|$($row.CandidatePaths)".ToLowerInvariant()
        if (-not $runtimeMotionByFile.ContainsKey($key)) {
            $runtimeMotionByFile[$key] = [pscustomobject]@{ Calls = 0; First = $row.FirstSeen }
        }
        $runtimeMotionByFile[$key].Calls += [int]$row.Calls
        if ($row.FirstSeen -lt $runtimeMotionByFile[$key].First) {
            $runtimeMotionByFile[$key].First = $row.FirstSeen
        }
    }
}

$resourceByFile = @{}
if ((Test-Path -LiteralPath $ResourceRequests) -and
    (Get-Item -LiteralPath $ResourceRequests).Length -gt 0) {
    foreach ($row in Import-Csv -LiteralPath $ResourceRequests) {
        if ([string]::IsNullOrWhiteSpace($row.LocalEfx)) { continue }
        $resourceByFile["$($row.Weapon)|$($row.LocalEfx)".ToLowerInvariant()] = $row
    }
}

$result = foreach ($group in (Import-Csv -LiteralPath $Candidates | Group-Object {
    "$($_.weapon)|$($_.'local EFX')".ToLowerInvariant()
})) {
    $candidate = $group.Group[0]
    $matches = @($byFile[$group.Name] | Where-Object { $null -ne $_ } | Sort-Object TriggerId -Unique)
    $unique = @($matches | Where-Object { $filesByState["$($candidate.weapon)|$($_.TriggerId)"].Count -eq 1 })
    $ambiguous = @($matches | Where-Object { $filesByState["$($candidate.weapon)|$($_.TriggerId)"].Count -gt 1 })
    $seen = @($unique | Where-Object { $observed.ContainsKey("$($candidate.weapon)|$($_.TriggerId)") })
    $ambiguousSeen = @($ambiguous | Where-Object { $observed.ContainsKey("$($candidate.weapon)|$($_.TriggerId)") })
    $direct = @($directByFile[$group.Name].Keys | Sort-Object)
    $motionEvent = $runtimeMotionByFile[$group.Name]
    $resourceEvent = $resourceByFile[$group.Name]
    $first = @($seen | ForEach-Object { $observed["$($candidate.weapon)|$($_.TriggerId)"].First } | Sort-Object | Select-Object -First 1)
    [pscustomobject]@{
        Weapon = $candidate.weapon
        EfxFile = [IO.Path]::GetFileName($candidate.'local EFX')
        LocalEfx = $candidate.'local EFX'
        EquipmentScope = if ([IO.Path]::GetFileName($candidate.'local EFX') -match '_9\d{2}(?:_|\.efx)') {
            'current equipment series only'
        } else { 'weapon family; other conditions may apply' }
        StateMapCount = $matches.Count
        UniqueStateMapCount = $unique.Count
        ObservedStateCount = $seen.Count
        AmbiguousStateMapCount = $ambiguous.Count
        ObservedAmbiguousStateCount = $ambiguousSeen.Count
        DirectMotionCount = $direct.Count
        MotionCallbackCount = if ($motionEvent) { $motionEvent.Calls } else { 0 }
        ResourceRequestCount = if ($resourceEvent) { $resourceEvent.RequestCount } else { 0 }
        Status = if ($resourceEvent) { 'exact EFX resource requested in session' }
            elseif ($unique.Count -gt 0 -and $seen.Count -eq $unique.Count) { 'all unique mapped IDs observed in session' }
            elseif ($seen.Count -gt 0) { 'some mapped IDs observed in session' }
            elseif ($motionEvent) { 'motion create callback observed' }
            elseif ($direct.Count -gt 0) { 'direct MOT reference' }
            elseif ($ambiguousSeen.Count -gt 0) { 'shared state ID observed; file attribution ambiguous' }
            elseif ($matches.Count -gt 0) { 'mapped IDs not yet observed in session' }
            else { 'no state ID or direct MOT reference' }
        MappedTriggers = ($matches.TriggerName -join '; ')
        ObservedTriggers = ($seen.TriggerName -join '; ')
        AmbiguousObservedTriggers = ($ambiguousSeen.TriggerName -join '; ')
        DirectMotions = ($direct -join '; ')
        FirstObserved = $first[0]
        FirstMotionCallback = if ($motionEvent) { $motionEvent.First } else { '' }
        FirstResourceRequest = if ($resourceEvent) { $resourceEvent.FirstSeen } else { '' }
    }
}

$result | Sort-Object Weapon,EfxFile | Export-Csv -LiteralPath $Output -NoTypeInformation -Encoding utf8
$appRows = [System.Collections.Generic.List[string]]::new()
$appRows.Add("Weapon`tEfxFile`tResourceRequestCount`tObservedStateCount`tMotionCallbackCount")
foreach ($row in @($result | Sort-Object Weapon,EfxFile)) {
    $appRows.Add("$($row.Weapon)`t$($row.EfxFile)`t$($row.ResourceRequestCount)`t$($row.ObservedStateCount)`t$($row.MotionCallbackCount)")
}
$appRows | Set-Content -LiteralPath $AppEvidence -Encoding utf8
$result | Group-Object Weapon | ForEach-Object {
    $complete = @($_.Group | Where-Object { [int]$_.UniqueStateMapCount -gt 0 -and [int]$_.ObservedStateCount -eq [int]$_.UniqueStateMapCount }).Count
    $partial = @($_.Group | Where-Object { [int]$_.ObservedStateCount -gt 0 -and [int]$_.ObservedStateCount -lt [int]$_.UniqueStateMapCount }).Count
    $unmapped = @($_.Group | Where-Object StateMapCount -eq 0).Count
    $direct = @($_.Group | Where-Object { $_.DirectMotionCount -gt 0 }).Count
    $runtime = @($_.Group | Where-Object { $_.MotionCallbackCount -gt 0 }).Count
    $requested = @($_.Group | Where-Object { $_.ResourceRequestCount -gt 0 }).Count
    '{0}: files={1} exact-resource={2} complete={3} partial={4} motion-callback={5} direct-motion={6} without-state-id={7}' -f $_.Name,$_.Count,$requested,$complete,$partial,$runtime,$direct,$unmapped
}
