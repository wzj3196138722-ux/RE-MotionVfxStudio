param(
    [Parameter(Mandatory=$true)][string]$Survey,
    [string]$Candidates = 'docs\efx-completion\weapon-pfb-resource-candidates.csv',
    [string]$Output = 'docs\efx-completion\weapon-runtime-motion-events.csv',
    [string[]]$ExcludedSessions = @('wp09_run1')
)

$byId = @{}
foreach ($row in Import-Csv -LiteralPath $Candidates) {
    if ($row.'source object' -notmatch '^(\d+)_') { continue }
    $container = [int]$Matches[1]
    if ($row.'element ID' -notmatch '^\d+$') { continue }
    $key = "$($row.weapon)|$container|$($row.'element ID')"
    if (-not $byId.ContainsKey($key)) { $byId[$key] = @{} }
    $byId[$key][$row.'local EFX'] = $true
}

$events = @{}
foreach ($line in Get-Content -LiteralPath $Survey) {
    $parts = $line -split "`t"
    if ($parts.Count -lt 5 -or $parts[1] -ne 'MOTION' -or
        $parts[2] -notmatch '^(wp\d\d)_run\d+$') { continue }
    if ($parts[2] -in $ExcludedSessions) { continue }
    $weapon = $Matches[1]
    $hex = $parts[4] -split '-'
    if ($hex.Count -lt 32) { continue }
    $bytes = [byte[]]($hex | ForEach-Object { [Convert]::ToByte($_,16) })
    $container = [BitConverter]::ToInt32($bytes,20)
    $element = [BitConverter]::ToInt32($bytes,24)
    $key = "$weapon|$container|$element"
    if (-not $events.ContainsKey($key)) {
        $files = if ($byId.ContainsKey($key)) { @($byId[$key].Keys | Sort-Object) } else { @() }
        $events[$key] = [pscustomobject]@{
            Weapon = $weapon
            ContainerId = $container
            ElementId = $element
            FirstSeen = $parts[0]
            LastSeen = $parts[0]
            Calls = 0
            CandidateFileCount = $files.Count
            CandidateFiles = ($files | ForEach-Object { [IO.Path]::GetFileName($_) }) -join '; '
            CandidatePaths = $files -join '; '
        }
    }
    $events[$key].LastSeen = $parts[0]
    $events[$key].Calls++
}

$events.Values | Sort-Object Weapon,ContainerId,ElementId |
    Export-Csv -LiteralPath $Output -NoTypeInformation -Encoding utf8
$events.Values | Group-Object Weapon | ForEach-Object {
    $resolved = @($_.Group | Where-Object CandidateFileCount -eq 1).Count
    '{0}: motion IDs={1}, uniquely mapped={2}' -f $_.Name,$_.Count,$resolved
}
