param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'test-output/action-preview-api'))
$ErrorActionPreference='Stop'
# Read-only type metadata. Does not invoke game methods, set fields, deploy a
# plugin, load resources, start the game, or alter live actor state.
$types=@('via.motion.Motion','via.motion.MotionBank','via.motion.DynamicMotionBank',
    'via.motion.MotionListResourceHolder','via.Prefab','via.effect.script.ObjectEffectManager2',
    'via.effect.script.ObjectEffectManager2.ExternalDataContainer','via.effect.script.EffectManager',
    'via.effect.script.EPVStandardData')
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach($name in $types){
    $url='http://localhost:8899/api/explorer/type?typeName='+[uri]::EscapeDataString($name)+'&includeInherited=true'
    $result=Invoke-RestMethod -Uri $url -NoProxy -TimeoutSec 15
    $result | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $OutputDirectory ($name+'.json')) -Encoding utf8
    [pscustomobject]@{type=$name;methods=@($result.methods).Count;error=$result.error}
}
