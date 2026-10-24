param(
    [Parameter(Mandatory=$true)][string]$GameData,
    [string]$Log = 'docs\efx-completion\weapon-session-switches.csv',
    [int]$InitialWeapon = 0
)

$endpoint = 'http://localhost:8899/api/explorer/chain'
$body = @{
    start = @{ singleton = 'app.PlayerManager' }
    steps = @(
        @{ type = 'method'; name = 'getMasterPlayer' },
        @{ type = 'method'; name = 'get_Character' },
        @{ type = 'method'; name = 'get_WeaponHandling' },
        @{ type = 'method'; name = 'get_Weapon' },
        @{ type = 'collect'; methods = @('get_WpType') }
    )
} | ConvertTo-Json -Depth 6 -Compress

$current = $InitialWeapon
$runs = @{}
$runs[$InitialWeapon] = 1
if (-not (Test-Path -LiteralPath $Log)) {
    'Time,Weapon,Session' | Set-Content -LiteralPath $Log -Encoding utf8
}
while ($true) {
    try {
        $response = Invoke-RestMethod -Method Post -Uri $endpoint -Body $body `
            -ContentType 'application/json' -TimeoutSec 3
        $type = [int]$response.results[0].get_WpType
        if ($type -ge 0 -and $type -le 13 -and $type -ne $current) {
            $current = $type
            $runs[$type] = if ($runs.ContainsKey($type)) { [int]$runs[$type] + 1 } else { 1 }
            $code = 'wp{0:D2}' -f $type
            $session = '{0}_run{1}' -f $code,$runs[$type]
            Set-Content -LiteralPath (Join-Path $GameData 'codex_weapon_session.txt') `
                -Value $session -Encoding ascii
            $time = [DateTimeOffset]::Now.ToString('O')
            '"{0}","{1}","{2}"' -f $time,$code,$session |
                Add-Content -LiteralPath $Log -Encoding utf8
            Write-Output "SWITCH $time $session"
        }
    } catch {
        # Title screens and loading transitions may temporarily have no player instance.
    }
    Start-Sleep -Milliseconds 1500
}
