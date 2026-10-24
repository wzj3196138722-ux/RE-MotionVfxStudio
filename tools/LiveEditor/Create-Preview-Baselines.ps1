param([string]$SamplePath)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $SamplePath) { $SamplePath = Join-Path $projectRoot 'Assets/VfxShared/natives/stm/art/vfx/effecteditor/weapon/it07/11_it07_201.efx.5571972' }
$runRoot = Join-Path $PSScriptRoot ('test-output/preview-baselines-' + [Guid]::NewGuid().ToString('N'))
$mailbox = Join-Path $runRoot 'mailbox'
New-Item -ItemType Directory -Path $mailbox -Force | Out-Null
$sourceHash = (Get-FileHash -LiteralPath $SamplePath -Algorithm SHA256).Hash
$backend = Join-Path $projectRoot 'VfxStudio.LiveEditor/publish/VfxStudio.LiveEditor.exe'
$arguments = '--mailbox "{0}" --assets "{1}" --output "{2}"' -f $mailbox,(Join-Path $projectRoot 'Assets'),$runRoot
$process = Start-Process -FilePath $backend -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'stdout.log') -RedirectStandardError (Join-Path $runRoot 'stderr.log')
$script:document = $null
function Request([hashtable]$Data) {
    $Data.id = [Guid]::NewGuid().ToString('N')
    if ($script:document) { $Data.revision = $script:document.revision }
    $temporary = Join-Path $mailbox 'request.tmp'
    $Data | ConvertTo-Json -Depth 10 -Compress | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination (Join-Path $mailbox 'request.json') -Force
    for ($n=0; $n -lt 300; $n++) {
        Start-Sleep -Milliseconds 25
        $responsePath = Join-Path $mailbox 'response.json'
        if (Test-Path -LiteralPath $responsePath) {
            try {
                $stream = [IO.File]::Open($responsePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                $reader = [IO.StreamReader]::new($stream)
                try { $response = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            } catch { continue }
            if ($response.id -eq $Data.id) {
                if (-not $response.ok) { throw $response.error }
                if ($response.document) { $script:document = $response.document }
                return $response
            }
        }
        if ($process.HasExited) { throw 'Isolated baseline backend exited.' }
    }
    throw ('Isolated baseline request timeout: ' + $Data.action)
}
function Attr([string]$Type, [int]$Entry = 1) {
    $match = @($script:document.entries[$Entry].attributes | Where-Object type -eq $Type)
    if ($match.Count -ne 1) { throw ('Expected exactly one ' + $Type) }
    return @{kind='entry';index=$Entry;attributeIndex=$match[0].index}
}
$reports = [Collections.Generic.List[object]]::new()
try {
    for ($n=0; $n -lt 60; $n++) {
        if (Test-Path -LiteralPath (Join-Path $mailbox 'status.json')) { break }
        Start-Sleep -Milliseconds 100
    }
    foreach ($variant in @('cpu','gpu')) {
        Request @{action='open';path=$SamplePath} | Out-Null
        Request @{action='remove_entry';index=$(if($variant -eq 'cpu'){2}else{1})} | Out-Null
        Request @{action='set';target=@{kind='entry';index=1};path='name';value=('Preview_Baseline_' + $variant.ToUpperInvariant())} | Out-Null
        Request @{action='remove_attribute';target=(Attr 'FadeByDepth')} | Out-Null
        $changes = [Collections.Generic.List[object]]::new()
        $settings = [ordered]@{
            Spawn = [ordered]@{
                'SpawnNum.x'='4';'SpawnNum.y'='4';'IntervalFrame.x'='0';'IntervalFrame.y'='0'
                'LoopNum.r'='1';'LoopNum.s'='0';'UseSpawnFrame'='false';'SpawnFrame.r'='0';'SpawnFrame.s'='0'
                'Interpolate'='false';'DistancePerSpawn'='1';'RingBufferMode'='false';'UseRevival'='false'
                'EmitterDelayFrame.x'='0';'EmitterDelayFrame.y'='0';'InitializeFull'='false'
            }
            Life = [ordered]@{
                'AppearFrame.r'='0';'AppearFrame.s'='0';'KeepFrame.r'='300';'KeepFrame.s'='0'
                'VanishFrame.r'='60';'VanishFrame.s'='0';'KeepHoldFrame.r'='0';'KeepHoldFrame.s'='0'
            }
            Velocity3D = [ordered]@{
                'Speed.s'='0';'Speed.r'='0';'GravityRate.s'='0';'GravityRate.r'='0'
                'InheritRate.s'='0';'InheritRate.r'='0'
            }
            ScaleAnim = [ordered]@{'SizeScalarAdd.s'='0';'SizeScalarAdd.r'='0';'SizeScalarCoef.s'='1';'SizeScalarCoef.r'='0'}
        }
        if ($variant -eq 'cpu') {
            $settings['TypeBillboard3D'] = [ordered]@{'SizeScalar.s'='0.8';'SizeScalar.r'='0'}
            $settings['RgbCommon'] = [ordered]@{'RedChKeepFrame.r'='300';'RedChKeepFrame.s'='0';'RedChVanishFrame.r'='60';'RedChVanishFrame.s'='0'}
        } else {
            $settings['TypeGpuBillboard'] = [ordered]@{'SizeScalar.s'='0.5';'SizeScalar.r'='0'}
        }
        foreach ($type in $settings.Keys) {
            $target = Attr $type
            $before = Request @{action='inspect';target=$target}
            foreach ($path in $settings[$type].Keys) {
                $old = @($before.fields | Where-Object path -eq $path)
                if ($old.Count -ne 1 -or -not $old[0].editable) { throw ('Expected editable field ' + $type + '.' + $path) }
                $new = $settings[$type][$path]
                if ($old[0].value -ne $new) { Request @{action='set';target=$target;path=$path;value=$new} | Out-Null }
                $changes.Add([ordered]@{type=$type;path=$path;before=$old[0].value;expected=$new})
            }
        }
        $path = Join-Path $runRoot ('preview_baseline_' + $variant + '.efx.5571972')
        Request @{action='save';path=$path} | Out-Null
        Request @{action='open';path=$path} | Out-Null
        if ($script:document.entries.Count -ne 2) { throw 'Baseline entry count mismatch after reread.' }
        foreach ($type in $settings.Keys) {
            $fields = (Request @{action='inspect';target=(Attr $type)}).fields
            foreach ($change in @($changes | Where-Object type -eq $type)) {
                $field = @($fields | Where-Object path -eq $change.path)
                if ($field.Count -ne 1 -or $field[0].value -ne $change.expected) { throw ('Reread mismatch ' + $type + '.' + $change.path) }
                $change['actual'] = $field[0].value
                $change['passed'] = $true
            }
        }
        $full = Request @{action='inspect';target=@{kind='document';index=0}}
        $full.fields | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $runRoot ($variant + '-fields.json')) -Encoding utf8
        $dependencies = @($script:document.entries.attributes.type | Where-Object {$_ -match 'PointCloud|Expression|MeshEmitter|Fluid|PtBehavior|Trigger|Listener'})
        if ($dependencies.Count -ne 0) { throw ('Unexpected runtime input dependencies: ' + ($dependencies -join ',')) }
        if (@($full.fields | Where-Object {$_.path -match '^(EffectGroups|ExpressionParameters)\['}).Count -ne 0) { throw 'Unexpected external expressions or effect groups.' }
        if (@($full.fields | Where-Object {$_.path -match '\.(BoneName|ParentBone)$' -and $_.value -ne ''}).Count -ne 0) { throw 'Unexpected bone dependency.' }
        $reports.Add([ordered]@{
            variant=$variant;outputPath=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            entries=$script:document.entries;changes=$changes;structuralChanges=@('Removed the other emitter','Removed FadeByDepth from the retained emitter','Renamed retained emitter')
            unresolvedRuntimeInputs=@();resourceDependencies=@($full.fields | Where-Object {$_.path -match '(UVSPath|UvarGroups\[\d+\]\.path)$'} | Select-Object path,value)
            reopenedAndVerified=$true;sourceNotModified=((Get-FileHash -LiteralPath $SamplePath -Algorithm SHA256).Hash -eq $sourceHash)
            gameVisibilityVerified=$false;notes='A deterministic stationary burst based on an original material. Particle lifetime is 300 keep + 60 vanish frames. Actual engine visibility, UVS resolution and pause must be checked in game.'
        })
        Write-Output ('READY ' + $path)
    }
    [ordered]@{source=$SamplePath;sourceSha256=$sourceHash;noGameWrites=$true;isolatedMailbox=$mailbox;variants=$reports} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8
    Write-Output ('REPORT ' + (Join-Path $runRoot 'results.json'))
} finally {
    if (-not $process.HasExited) { Request @{action='shutdown'} | Out-Null; $process.WaitForExit(5000) | Out-Null }
}
