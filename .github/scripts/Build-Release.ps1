param([Parameter(Mandatory)][string]$Version)
. (Join-Path $PSScriptRoot 'Release-Common.ps1')
Assert-ReleaseVersion $Version
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $project
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdk -ne '10.0.300') { throw "Expected .NET SDK 10.0.300, got $sdk." }
$vendor = Join-Path $project 'third-party/reframework-sdk'
$manifest = Get-Content -LiteralPath (Join-Path $vendor 'SHA256.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest) {
    if ($entry.path -match '(^|[\\/])\.\.([\\/]|$)|:|^[/\\]') { throw 'Invalid SDK manifest path.' }
    $file = Join-Path $vendor $entry.path
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Vendored SDK changed: $($entry.path)" }
}
& (Join-Path $project 'tools/LuaValidation/Run-OfflineTests.ps1') -REFrameworkRoot $vendor
$output = Join-Path $project 'artifacts/release'
& (Join-Path $project 'distribution/Build-Package.ps1') -OutputRoot $output -REFrameworkRoot $vendor
$zip = Join-Path $output "MotionVfxLive-$Version-win-x64.zip"
& (Join-Path $project 'tools/Installer/Build.ps1') -PayloadArchive $zip -OutputDirectory $output
$installer = Join-Path $output "MotionVfxLive-$Version-Setup.exe"
$testRoot = Join-Path $project 'artifacts/installer-self-test'
$start = [Diagnostics.ProcessStartInfo]::new($installer)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
foreach ($argument in @('--self-test', '--quiet', '--output', $testRoot)) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
if (-not $process.WaitForExit(900000)) { $process.Kill($true); throw 'Isolated installer test exceeded 15 minutes.' }
$reportPath = Join-Path $testRoot 'installer-tests.json'
if (-not (Test-Path -LiteralPath $reportPath)) { throw "Installer test produced no report (exit $($process.ExitCode))." }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
Copy-Item -LiteralPath $reportPath -Destination (Join-Path $output "MotionVfxLive-$Version-installer-checks.json")
if ($process.ExitCode -ne 0 -or $report.passed -ne $true) { throw "Isolated installer test failed: $($report.error)" }
if ($report.gameTouched -ne $false -or $report.registryTouched -ne $false) { throw 'Installer test isolation was not confirmed.' }
$release = Get-Content -LiteralPath (Join-Path $output "MotionVfxLive-$Version-win-x64/release.json") -Raw | ConvertFrom-Json
if ($release.runtime -ne 'self-contained .NET 10.0.8 win-x64' -or $release.version -ne $Version) { throw 'Unexpected payload runtime or version.' }
$payload = Join-Path $output "MotionVfxLive-$Version-win-x64"
& (Join-Path $payload 'mcp/VfxStudio.Mcp.exe') --self-test
if ($LASTEXITCODE -ne 0) { throw 'The packaged MCP failed its isolated protocol/mailbox tests.' }
$payloadManifest = Get-Content -LiteralPath (Join-Path $payload 'SHA256.json') -Raw | ConvertFrom-Json
if ($release.mdfxlCompatibilityVersion -ne '1.0.0') { throw 'Expected integrated MDF-XL compatibility component 1.0.0.' }
foreach ($relative in @(
    'compatibility/MDFXL/Apply-MdfxlCompatibility.ps1',
    'compatibility/MDFXL/MDF-XL-frame-guard.lua',
    'Source/tools/MdfxlCompatibility/Apply-MdfxlCompatibility.ps1',
    'Source/tools/MdfxlCompatibility/MDF-XL-frame-guard.lua'
)) {
    $entries = @($payloadManifest | Where-Object { $_.path -ceq $relative })
    if ($entries.Count -ne 1) { throw "Required compatibility file is missing from the package manifest: $relative" }
    $file = Join-Path $payload $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entries[0].sha256) {
        throw "Packaged compatibility file does not match its manifest: $relative"
    }
}
if (@(Get-ChildItem -LiteralPath $payload -Recurse -File -Filter 'fx_json_reader.lua').Count -gt 0) {
    throw 'An unpublished fx_json_reader.lua draft was included in the payload.'
}
$assets = @()
foreach ($path in @($installer, $zip)) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $sidecar = (Get-Content -LiteralPath ($path + '.sha256') -Raw).Trim().Split(' ')[0].ToLowerInvariant()
    if ($hash -ne $sidecar) { throw "Hash sidecar mismatch: $path" }
    $assets += [ordered]@{ name = [IO.Path]::GetFileName($path); size = (Get-Item -LiteralPath $path).Length; sha256 = $hash }
}
$provenance = [ordered]@{
    version = $Version; sourceCommit = $env:GITHUB_SHA; repository = $env:GITHUB_REPOSITORY
    workflowRun = "https://github.com/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID"
    sdk = $sdk; runtime = $release.runtime; builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    installerChecks = $report.count
    offlineLuaSuites = @('file-dialog', 'file-dialog-explorer', 'd2d-workbench', 'playemitter-ui', 'ui-performance', 'ai-transport-performance', 'capture-refresh', 'live-ui-and-reload', 'action-capture', 'action-bindings', 'mdfxl-compat-portable')
    mcpProtocolAndMailboxChecks = $true; fixtureTests = $false; legacySixVersionMatrix = $false
    mdfxlCompatibilityVersion = $release.mdfxlCompatibilityVersion
    note = 'Fresh source build. Its hashes differ from the prior local binary. No game-content fixtures or live-game tests were run in CI.'
    assets = $assets
}
$provenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output "MotionVfxLive-$Version-build.json") -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) {
    "Built v$Version with $($report.count) isolated installer checks. No game fixture, old-version matrix, or in-game visual test was run in CI." | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}
