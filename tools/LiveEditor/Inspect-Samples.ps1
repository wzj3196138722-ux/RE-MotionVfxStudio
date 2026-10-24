param([string[]]$SamplePaths, [string]$BackendPath)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $SamplePaths) {
    $effects = Join-Path $projectRoot 'Assets/VfxShared/natives/stm/art/vfx/effecteditor'
    $SamplePaths = @(
        (Join-Path $effects 'common/grassburn/11_cm_grassburn_fire.efx.5571972'),
        (Join-Path $effects 'common/grassburn/11_cm_grassburn_smoke.efx.5571972'),
        (Join-Path $effects 'common/grassburn/11_cm_oilburn_fire.efx.5571972'),
        (Join-Path $effects 'common/guide/11_guide_001.efx.5571972'),
        (Join-Path $effects 'common/guide/11_guide_002.efx.5571972'),
        (Join-Path $effects 'common/guide/11_guide_100.efx.5571972')
    )
}
$runRoot = Join-Path $PSScriptRoot ('test-output/sample-inspection-' + [Guid]::NewGuid().ToString('N'))
$mailbox = Join-Path $runRoot 'mailbox'
New-Item -ItemType Directory -Path $mailbox -Force | Out-Null
$backend = if ($BackendPath) { (Resolve-Path -LiteralPath $BackendPath).Path } else { Join-Path $PSScriptRoot 'backend/VfxStudio.LiveEditor.exe' }
$arguments = '--mailbox "{0}" --assets "{1}" --output "{2}"' -f $mailbox,(Join-Path $projectRoot 'Assets'),(Join-Path $runRoot 'unused-output')
$process = Start-Process -FilePath $backend -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'stdout.log') -RedirectStandardError (Join-Path $runRoot 'stderr.log')
function Request([hashtable]$Data) {
    $Data.id = [Guid]::NewGuid().ToString('N')
    $Data | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath (Join-Path $mailbox 'request.json') -Encoding utf8
    for ($n=0; $n -lt 200; $n++) {
        Start-Sleep -Milliseconds 50
        $responsePath = Join-Path $mailbox 'response.json'
        if (Test-Path -LiteralPath $responsePath) {
            $response = Get-Content -LiteralPath $responsePath -Raw | ConvertFrom-Json
            if ($response.id -eq $Data.id) { if (-not $response.ok) {throw $response.error}; return $response }
        }
    }
    throw 'isolated inspector timeout'
}
try {
    for ($n=0; $n -lt 50; $n++) { if(Test-Path -LiteralPath (Join-Path $mailbox 'status.json')) {break}; Start-Sleep -Milliseconds 100 }
    foreach ($sample in $SamplePaths) {
        if (-not (Test-Path -LiteralPath $sample)) { continue }
        $hash = (Get-FileHash -LiteralPath $sample -Algorithm SHA256).Hash
        $opened = Request @{action='open';path=$sample}
        $inspected = Request @{action='inspect';target=@{kind='document';index=0}}
        [ordered]@{source=$sample;sha256=$hash;document=$opened.document;fields=$inspected.fields;curves=$inspected.curves} | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath (Join-Path $runRoot ((Split-Path $sample -Leaf) + '.json')) -Encoding utf8
        $selected=@($inspected.fields | Where-Object { $_.editable -and ($_.path -match 'ExpressionParameters|EffectGroups|Groups\[|entryAssignment|\.name$|SpawnNum|LoopNum|KeepFrame|MaxParticles|\.filename|\.filePath|\.path$|\.mesh|\.mdf|texturePath|EmitMask|EmitterDelayFrame|AppearFrame|VanishFrame|KeepHoldFrame|distance|Distance|emission|Emission|\.Enabled' ) } | Select-Object path,value)
        Write-Output ((Split-Path $sample -Leaf) + ' ' + (@{entries=@($opened.document.entries).Count;actions=@($opened.document.actions).Count;fields=@($inspected.fields).Count;selected=$selected} | ConvertTo-Json -Depth 5 -Compress))
    }
    Write-Output ('REPORT ' + $runRoot)
} finally {
    if(-not $process.HasExited) { Request @{action='shutdown'} | Out-Null; $process.WaitForExit(5000) | Out-Null }
}
