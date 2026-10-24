param(
    [Parameter(Mandatory=$true)][string]$REFrameworkNetPath,
    [switch]$RunSelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$destination = Join-Path $projectRoot 'game-plugin/MotionVfxCacheDiagnostics.cs'
$sources = @('EfxBufferValidator.cs', 'ReadOnlyResourceProbe.cs', 'NativeTypeMetadataProbe.cs', 'CacheProbeMailbox.cs')
$usings = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
$bodies = [Collections.Generic.List[string]]::new()
foreach ($source in $sources) {
    $path = Join-Path $PSScriptRoot $source
    $text = [IO.File]::ReadAllText($path)
    $match = [regex]::Match($text, '(?m)^namespace MotionVfx\.CacheDiagnostics')
    if (!$match.Success) { throw "Unexpected source layout: $source" }
    foreach ($using in [regex]::Matches($text.Substring(0, $match.Index), '(?m)^using [^\r\n]+;')) { $null = $usings.Add($using.Value) }
    $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($path)))
    $bodies.Add('// Source: tools/LiveEditor/CacheDiagnostics/' + $source + '; SHA256 ' + $sha + "`n" + $text.Substring($match.Index))
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
$combined = "// Generated standalone REFramework.NET source. Do not edit this copy directly.`n// Regenerate with tools/LiveEditor/CacheDiagnostics/Build-CacheDiagnosticsPlugin.ps1.`n// Starts an explicit-request mailbox only. Candidates are diagnostic and never editing input.`n" + ($usings -join "`n") + "`n`n" + ($bodies -join "`n`n")
[IO.File]::WriteAllText($destination, $combined, [Text.UTF8Encoding]::new($false))
Write-Output $destination
if ($RunSelfTest) {
    if (!(Test-Path -LiteralPath $REFrameworkNetPath -PathType Leaf)) { throw 'REFramework.NET.dll was not found; pass -REFrameworkNetPath.' }
    $fixtureProject = Join-Path $PSScriptRoot 'PluginFixture/PluginFixture.csproj'
    $output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../test-output'))
    & dotnet run --project $fixtureProject --configuration Release "-p:REFrameworkNetPath=$REFrameworkNetPath" -- $output
    if ($LASTEXITCODE -ne 0) { throw "Plugin compile/self-process fixture failed with exit $LASTEXITCODE." }
}
