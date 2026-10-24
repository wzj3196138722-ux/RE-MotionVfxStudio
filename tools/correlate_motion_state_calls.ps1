param(
    [string]$MotionSamples = 'docs\efx-completion\weapon-motion-samples.csv',
    [Parameter(Mandatory=$true)][string]$StateSurvey,
    [string]$StateMap = 'docs\efx-completion\weapon-state-trigger-map.csv',
    [string]$Output = 'docs\efx-completion\weapon-motion-state-correlations.csv',
    [string]$AppEvidence = 'Assets\RuntimeEvidence\motion-state-observed.tsv',
    [int]$MaxGapMs = 450
)

$fileByTrigger = @{}
foreach ($group in (Import-Csv -LiteralPath $StateMap | Group-Object {
    "$($_.Weapon)|$($_.TriggerName)".ToLowerInvariant()
})) {
    $paths = @($group.Group.LocalEfx | Where-Object { $_ } | Sort-Object -Unique)
    if ($paths.Count -eq 1) {
        $fileByTrigger[$group.Name] = [regex]::Match($paths[0], '[^\\/]+\.efx').Value
    }
}

$samplesBySession = @{}
foreach ($row in Import-Csv -LiteralPath $MotionSamples) {
    if (-not $samplesBySession.ContainsKey($row.Session)) {
        $samplesBySession[$row.Session] = [System.Collections.Generic.List[object]]::new()
    }
    $samplesBySession[$row.Session].Add([pscustomobject]@{
        Ticks = [DateTimeOffset]::Parse($row.Time).UtcTicks
        Bank = [int64]$row.Bank
        Motion = [int64]$row.Motion
    })
}
foreach ($session in @($samplesBySession.Keys)) {
    $samplesBySession[$session] = @($samplesBySession[$session] | Sort-Object Ticks)
}

$result = [System.Collections.Generic.List[object]]::new()
$maxGapTicks = $MaxGapMs * [TimeSpan]::TicksPerMillisecond
foreach ($line in Get-Content -LiteralPath $StateSurvey) {
    $parts = $line -split "`t"
    if ($parts.Count -lt 5 -or $parts[1] -notin @('PLAY', 'LOOP')) { continue }
    $session = $parts[2]
    $weapon = $parts[3]
    $trigger = $parts[4]
    $key = "$weapon|$trigger".ToLowerInvariant()
    if (-not $samplesBySession.ContainsKey($session) -or
        -not $fileByTrigger.ContainsKey($key) -or
        -not $session.StartsWith($weapon + '_run')) { continue }
    $samples = $samplesBySession[$session]
    $when = [DateTimeOffset]::Parse($parts[0]).UtcTicks
    $lo = 0
    $hi = $samples.Count
    while ($lo -lt $hi) {
        $mid = [int][math]::Floor(($lo + $hi) / 2)
        if ($samples[$mid].Ticks -le $when) { $lo = $mid + 1 }
        else { $hi = $mid }
    }
    $before = if ($lo -gt 0) { $samples[$lo - 1] } else { $null }
    $after = if ($lo -lt $samples.Count) { $samples[$lo] } else { $null }
    $chosen = $null
    $evidence = 'no surrounding motion samples'
    if ($before -and $after -and $when - $before.Ticks -le $maxGapTicks -and
        $after.Ticks - $when -le $maxGapTicks) {
        if ($before.Bank -eq $after.Bank -and $before.Motion -eq $after.Motion -and
            $before.Bank -ne 0 -and $before.Motion -ne 0 -and
            $before.Motion -ne 4294967295) {
            $chosen = $before
            $evidence = 'same motion sampled before and after state call'
        } else {
            $evidence = 'motion changed near state call; attribution uncertain'
        }
    }
    $result.Add([pscustomobject]@{
        Time = $parts[0]
        Session = $session
        Weapon = $weapon
        MotionBank = if ($chosen) { $chosen.Bank } else { '' }
        Motion = if ($chosen) { $chosen.Motion } else { '' }
        TriggerName = $trigger
        EfxFile = $fileByTrigger[$key]
        Evidence = $evidence
    })
}
$result | Export-Csv -LiteralPath $Output -NoTypeInformation -Encoding utf8
$appRows = [System.Collections.Generic.List[string]]::new()
$appRows.Add("Weapon`tMotion`tEfxFile`tCallCount`tFirstSeen`tTriggerName")
foreach ($group in @($result | Where-Object { $_.Motion -ne '' } |
    Group-Object { "$($_.Weapon)|$($_.Motion)|$($_.EfxFile)|$($_.TriggerName)" } | Sort-Object Name)) {
    $first = $group.Group | Sort-Object Time | Select-Object -First 1
    $appRows.Add("$($first.Weapon)`t$($first.Motion)`t$($first.EfxFile)`t$($group.Count)`t$($first.Time)`t$($first.TriggerName)")
}
$appRows | Set-Content -LiteralPath $AppEvidence -Encoding utf8
'state calls={0} motion-attributed={1}' -f $result.Count,
    @($result | Where-Object { $_.Motion -ne '' }).Count
