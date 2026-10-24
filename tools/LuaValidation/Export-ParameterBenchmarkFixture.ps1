param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $PSScriptRoot 'test-output/native-ui-perf'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$assemblyFile=[IO.Path]::GetFullPath($AssemblyPath)
$assemblyDir=[IO.Path]::GetDirectoryName($assemblyFile)
foreach($name in @('Standart.Hash.xxHash.dll','ZstdSharp.dll','VfxStudio.Core.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $assemblyDir $name))
}
$asm=[Reflection.Assembly]::LoadFrom($assemblyFile)
$optionsType=$asm.GetType('VfxStudio.LiveEditor.Options')
$optionsCtor=$optionsType.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance')|Where-Object {$_.GetParameters().Count -eq 6}|Select-Object -First 1
$options=$optionsCtor.Invoke([object[]]@([string](Join-Path $project 'Assets'),[string](Join-Path $output 'mailbox'),[string](Join-Path $output 'generated'),$null,$true,$null))
$engineCtor=$asm.GetType('VfxStudio.LiveEditor.EditorEngine').GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance')|Where-Object {$_.GetParameters().Count -eq 2}|Select-Object -First 1
$engine=$engineCtor.Invoke([object[]]@($options,$null))
$handle=$engine.GetType().GetMethod('Handle')
$source=Join-Path $project 'Assets/VfxShared/natives/stm/art/vfx/effecteditor/weapon/it03/11_it03_005.efx.5571972'
$cwd=[Environment]::CurrentDirectory
Push-Location $output
try {
    [Environment]::CurrentDirectory=$output
    $request=[System.Text.Json.Nodes.JsonNode]::Parse((@{id='bench-open';action='open';path=$source}|ConvertTo-Json -Compress))
    $opened=$handle.Invoke($engine,[object[]](,$request))
    if(-not $opened['ok'].GetValue[bool]()){throw $opened.ToJsonString()}
    $request=[System.Text.Json.Nodes.JsonNode]::Parse('{"id":"bench-inspect","action":"inspect","target":{"kind":"action","index":0,"attributeIndex":0}}')
    $response=$handle.Invoke($engine,[object[]](,$request))
    if(-not $response['ok'].GetValue[bool]()){throw $response.ToJsonString()}
    $json=$response.ToJsonString()
    [IO.File]::WriteAllText((Join-Path $output 'playemitter-full-response.json'),$json)
    function LuaValue($value) {
        if($null -eq $value){return 'nil'}
        if($value -is [string]){return (ConvertTo-Json -InputObject $value -Compress -EscapeHandling Default)}
        if($value -is [bool]){return $value.ToString().ToLowerInvariant()}
        if($value -is [System.Collections.IDictionary]){
            $items=@(foreach($key in $value.Keys){'['+(LuaValue ([string]$key))+']='+(LuaValue $value[$key])})
            return '{'+($items -join ',')+'}'
        }
        if($value -is [System.Collections.IEnumerable]){
            $items=@(foreach($item in $value){LuaValue $item});return '{'+($items -join ',')+'}'
        }
        return [Convert]::ToString($value,[Globalization.CultureInfo]::InvariantCulture)
    }
    $data=$json|ConvertFrom-Json -AsHashtable -Depth 100
    $data.document=($opened.ToJsonString()|ConvertFrom-Json -AsHashtable -Depth 100).document
    [IO.File]::WriteAllText((Join-Path $output 'playemitter-full-response.lua'),('return '+(LuaValue $data)),[Text.UTF8Encoding]::new($false))
    Write-Output ('Exported fields='+$data.fields.Count+' colors='+$data.colorGroups.Count+' sha256='+(Get-FileHash -LiteralPath $source).Hash)
} finally {[Environment]::CurrentDirectory=$cwd;Pop-Location}
