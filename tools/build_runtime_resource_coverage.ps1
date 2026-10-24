param(
    [Parameter(Mandatory=$true)][string]$Survey,
    [string]$Candidates = 'docs\efx-completion\weapon-runtime-coverage.csv',
    [string]$Output = 'docs\efx-completion\weapon-runtime-resource-requests.csv',
    [string[]]$ExcludedSessions = @('wp09_run1')
)

$known = @{}
foreach ($row in Import-Csv -LiteralPath $Candidates) {
    $name = $row.EfxFile -replace '\.\d+$', ''
    $known[("$($row.Weapon)|$name").ToLowerInvariant()] = $row.LocalEfx
}
$requests = @{}
if (Test-Path -LiteralPath $Survey) {
    foreach ($line in Get-Content -LiteralPath $Survey) {
        $parts = $line -split "`t"
        if ($parts.Count -lt 6 -or $parts[1] -ne 'RESOURCE' -or
            $parts[2] -notmatch '^(wp\d\d)_run\d+$' -or $parts[2] -in $ExcludedSessions) { continue }
        $sessionWeapon = $Matches[1]
        $path = $parts[5].Replace('\', '/')
        if ($path -notmatch '(?i)/weapon/it(\d\d)/([^/]+\.efx)(?:$|[./])') { continue }
        $pathWeapon = 'wp' + $Matches[1]
        $name = $Matches[2]
        if ($pathWeapon -ne $sessionWeapon) { continue }
        $key = "$sessionWeapon|$name".ToLowerInvariant()
        if (-not $requests.ContainsKey($key)) {
            $requests[$key] = [pscustomobject]@{
                Weapon = $sessionWeapon
                EfxFile = $name
                LocalEfx = if ($known.ContainsKey($key)) { $known[$key] } else { '' }
                FirstSeen = $parts[0]
                LastSeen = $parts[0]
                RequestCount = 0
                Sessions = @{}
                Modes = @{}
                ResourcePaths = @{}
            }
        }
        $entry = $requests[$key]
        $entry.LastSeen = $parts[0]
        $entry.RequestCount++
        $entry.Sessions[$parts[2]] = $true
        $entry.Modes[$parts[3]] = $true
        $entry.ResourcePaths[$path] = $true
    }
}

$requests.Values | ForEach-Object {
    [pscustomobject]@{
        Weapon = $_.Weapon
        EfxFile = $_.EfxFile
        LocalEfx = $_.LocalEfx
        RequestCount = $_.RequestCount
        FirstSeen = $_.FirstSeen
        LastSeen = $_.LastSeen
        Sessions = ($_.Sessions.Keys | Sort-Object) -join '; '
        Modes = ($_.Modes.Keys | Sort-Object) -join '; '
        ResourcePaths = ($_.ResourcePaths.Keys | Sort-Object) -join '; '
    }
} | Sort-Object Weapon,EfxFile | Export-Csv -LiteralPath $Output -NoTypeInformation -Encoding utf8

'resource files={0} matched={1}' -f $requests.Count,
    @($requests.Values | Where-Object { -not [string]::IsNullOrEmpty($_.LocalEfx) }).Count
