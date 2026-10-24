[CmdletBinding(DefaultParameterSetName = 'Read')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Read')][string]$ExecutablePath,
    [Parameter(Mandatory, ParameterSetName = 'Read')][string]$MetadataReportPath,
    [Parameter(ParameterSetName = 'Read')][string[]]$PropertyWhitelist,
    [Parameter(Mandatory, ParameterSetName = 'Fixture')][switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $PSScriptRoot 'ExecutablePropertyCodeReader.cs')
$outputRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../test-output'))
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$run = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
if ($SelfTest) {
    $checks = [MotionVfx.CacheDiagnostics.ExecutablePropertyCodeReader]::RunSelfFixture($outputRoot)
    $report = [ordered]@{ status = 'synthetic_pe_fixture_passed'; checks = $checks; gameRead = $false; processMemoryRead = $false; nativeProcessWrites = $false }
    $destination = Join-Path $outputRoot ('pe-getter-self-fixture-' + $run + '.json')
} else {
    if (!$PropertyWhitelist) { $PropertyWhitelist = [MotionVfx.CacheDiagnostics.ExecutablePropertyCodeReader]::DefaultProperties }
    $report = [MotionVfx.CacheDiagnostics.ExecutablePropertyCodeReader]::Read($ExecutablePath, $MetadataReportPath, $PropertyWhitelist)
    $destination = Join-Path $outputRoot ('pe-property-code-' + $run + '.json')
}
[IO.File]::WriteAllText($destination, ($report | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Output $destination
if ($SelfTest) { Write-Output ('Synthetic PE fixture passed: ' + $checks.Count + ' checks; no game or process memory was accessed.') }
