. (Join-Path $PSScriptRoot 'Release-Common.ps1')
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../game-plugin/MotionVfxLive.lua'))
$version = [regex]::Match($source, 'local M = \{ version = "([^"]+)"').Groups[1].Value
Assert-ReleaseVersion $version
$notes = Join-Path $PSScriptRoot "../release-notes/$version.md"
if (-not (Test-Path -LiteralPath $notes -PathType Leaf)) { throw "Missing bilingual release notes: $version.md" }
$exists = Test-ReleaseExists "v$version"
$publish = if ($exists) { 'false' } else { 'true' }
@("version=$version", "publish=$publish") | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
if ($exists) {
    "Release or tag v$version already exists. Existing tags and assets were preserved; nothing was rebuilt or published." | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
} else {
    "Preparing a new v$version release from $env:GITHUB_SHA. The game is not needed by this workflow." | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}
