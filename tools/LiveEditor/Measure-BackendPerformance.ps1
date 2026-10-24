param(
    [Parameter(Mandatory=$true)][string]$AssemblyPath,
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [Parameter(Mandatory=$true)][string]$Phase
)
$ErrorActionPreference = 'Stop'
$assemblyFile = [IO.Path]::GetFullPath($AssemblyPath)
$assemblyDirectory = [IO.Path]::GetDirectoryName($assemblyFile)
foreach ($name in @('Standart.Hash.xxHash.dll','ZstdSharp.dll','VfxStudio.Core.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $assemblyDirectory $name))
}
$assembly = [Reflection.Assembly]::LoadFrom($assemblyFile)
$methodName = if ($Phase -eq 'regression') { 'Regression' } elseif ($Phase -eq 'transport') { 'Transport' } elseif ($Phase -eq 'memory') { 'Run' } else { 'Benchmark' }
$typeName = if ($Phase -eq 'memory') { 'VfxStudio.LiveEditor.MemorySerializationTests' } else { 'VfxStudio.LiveEditor.BackendPerformanceTests' }
$method = $assembly.GetType($typeName).GetMethod($methodName,[Reflection.BindingFlags]'Static,NonPublic')
$arguments = @([IO.Path]::GetFullPath($ProjectPath),[IO.Path]::GetFullPath($OutputPath))
if ($Phase -notin @('regression','transport','memory')) { $arguments += $Phase }
$method.Invoke($null,$arguments)
