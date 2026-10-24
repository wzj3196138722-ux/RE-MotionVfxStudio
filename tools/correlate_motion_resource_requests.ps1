param(
    [string]$MotionSamples = 'docs\efx-completion\weapon-motion-samples.csv',
    [Parameter(Mandatory=$true)][string]$ResourceSurvey,
    [string]$Output = 'docs\efx-completion\weapon-motion-resource-correlations.csv',
    [string]$AppEvidence = 'Assets\RuntimeEvidence\motion-efx-observed.tsv',
    [int]$MaxGapMs = 450
)

$samplesBySession = @{}
foreach ($row in Import-Csv -LiteralPath $MotionSamples) {
    if (-not $samplesBySession.ContainsKey($row.Session)) {
        $samplesBySession[$row.Session] = [System.Collections.Generic.List[object]]::new()
    }
    $samplesBySession[$row.Session].Add([pscustomobject]@{
        Ticks = [DateTimeOffset]::Parse($row.Time).UtcTicks
        Bank = [int64]$row.Bank
        Motion = [int64]$row.Motion
        Frame = [double]::Parse($row.Frame, [cultureinfo]::InvariantCulture)
    })
}
foreach ($session in @($samplesBySession.Keys)) {
    $sorted = @($samplesBySession[$session] | Sort-Object Ticks)
    $samplesBySession[$session] = $sorted
}

$result = [System.Collections.Generic.List[object]]::new()
$maxGapTicks = $MaxGapMs * [TimeSpan]::TicksPerMillisecond
foreach ($line in Get-Content -LiteralPath $ResourceSurvey) {
    $parts = $line -split "`t"
    if ($parts.Count -lt 6 -or $parts[1] -ne 'RESOURCE') { continue }
    $session = $parts[2]
    $path = $parts[5]
    if ($path -notmatch '(?i)/Weapon/it(\d\d)/([^/]+\.efx)$') { continue }
    $weapon = 'wp' + $Matches[1]
    $file = $Matches[2]
    if (-not $session.StartsWith($weapon + '_run') -or
        -not $samplesBySession.ContainsKey($session)) { continue }
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
            $evidence = 'same motion sampled before and after request'
        } else {
            $evidence = 'motion changed near request; attribution uncertain'
        }
    }
    $result.Add([pscustomobject]@{
        Time = $parts[0]
        Session = $session
        Weapon = $weapon
        EfxFile = $file
        ResourcePath = $path
        MotionBank = if ($chosen) { $chosen.Bank } else { '' }
        Motion = if ($chosen) { $chosen.Motion } else { '' }
        MotionFrameBefore = if ($chosen) { [math]::Round($chosen.Frame, 3) } else { '' }
        Evidence = $evidence
    })
}

$result | Export-Csv -LiteralPath $Output -NoTypeInformation -Encoding utf8
$evidenceDir = Split-Path -Parent $AppEvidence
if (-not (Test-Path -LiteralPath $evidenceDir)) {
    New-Item -ItemType Directory -Path $evidenceDir | Out-Null
}
$appRows = [System.Collections.Generic.List[string]]::new()
$appRows.Add("Weapon`tMotion`tEfxFile`tRequestCount`tFirstSeen")
foreach ($group in @($result | Where-Object { $_.Motion -ne '' } |
    Group-Object { "$($_.Weapon)|$($_.Motion)|$($_.EfxFile)" } | Sort-Object Name)) {
    $first = $group.Group | Sort-Object Time | Select-Object -First 1
    $appRows.Add("$($first.Weapon)`t$($first.Motion)`t$($first.EfxFile)`t$($group.Count)`t$($first.Time)")
}
$appRows | Set-Content -LiteralPath $AppEvidence -Encoding utf8
'resource requests={0} motion-attributed={1}' -f $result.Count,
    @($result | Where-Object { $_.Motion -ne '' }).Count
