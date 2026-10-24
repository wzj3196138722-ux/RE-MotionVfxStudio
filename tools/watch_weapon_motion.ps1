param(
    [Parameter(Mandatory=$true)][string]$GameData,
    [string]$Output = 'docs\efx-completion\weapon-motion-samples.csv',
    [int]$IntervalMs = 180
)

$endpoint = 'http://localhost:8899/api/explorer'
$sessionFile = Join-Path $GameData 'codex_weapon_session.txt'
if (-not (Test-Path -LiteralPath $Output)) {
    'Time,Session,Bank,Motion,Frame' | Set-Content -LiteralPath $Output -Encoding utf8
}

function Resolve-LayerZero {
    $chain = @{
        start = @{ singleton = 'app.PlayerManager' }
        steps = @(
            @{ type = 'method'; name = 'getMasterPlayer' },
            @{ type = 'method'; name = 'get_Character' },
            @{ type = 'method'; name = 'get_MotionComponent' }
        )
    } | ConvertTo-Json -Depth 6 -Compress
    $motion = (Invoke-RestMethod -Uri "$endpoint/chain" -Method Post -Body $chain `
        -ContentType 'application/json' -TimeoutSec 5).results[0]
    if (-not $motion.address) { throw 'Motion component unavailable' }
    $request = @{
        address = $motion.address
        kind = $motion.kind
        typeName = $motion.typeName
        methodName = 'getLayer'
        args = @(@{ value = 0; type = 'System.UInt32' })
    } | ConvertTo-Json -Depth 5 -Compress
    $layer = Invoke-RestMethod -Uri "$endpoint/method" -Method Post -Body $request `
        -ContentType 'application/json' -TimeoutSec 5
    if (-not $layer.childAddress) { throw 'Layer zero unavailable' }
    return $layer
}

$layerZero = $null
$lastKey = ''
while ($true) {
    try {
        if (-not $layerZero) {
            $layerZero = Resolve-LayerZero
            Write-Output "MOTION_LAYER $($layerZero.childAddress)"
        }
        $chain = @{
            start = @{
                address = $layerZero.childAddress
                kind = $layerZero.childKind
                typeName = $layerZero.childTypeName
            }
            steps = @(@{
                type = 'collect'
                methods = @('get_MotionBankID', 'get_MotionID', 'get_Frame')
            })
        } | ConvertTo-Json -Depth 5 -Compress
        $result = Invoke-RestMethod -Uri "$endpoint/chain" -Method Post -Body $chain `
            -ContentType 'application/json' -TimeoutSec 5
        if ($result.error -or -not $result.results[0].get_MotionID) { throw 'Motion sample unavailable' }
        $sample = $result.results[0]
        $session = if (Test-Path -LiteralPath $sessionFile) {
            (Get-Content -LiteralPath $sessionFile -Raw).Trim()
        } else { 'unmarked' }
        if ($session -ne 'unmarked') {
            $now = [DateTimeOffset]::Now.ToString('O')
            '"{0}","{1}","{2}","{3}","{4}"' -f $now,$session,
                $sample.get_MotionBankID,$sample.get_MotionID,$sample.get_Frame |
                Add-Content -LiteralPath $Output -Encoding utf8
            $key = "$session|$($sample.get_MotionBankID)|$($sample.get_MotionID)"
            if ($key -ne $lastKey) {
                Write-Output "MOTION $now $key"
                $lastKey = $key
            }
        }
    } catch {
        $layerZero = $null
    }
    Start-Sleep -Milliseconds $IntervalMs
}
