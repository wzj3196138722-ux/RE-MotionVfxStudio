param([Parameter(Mandatory)][string]$Version)
. (Join-Path $PSScriptRoot 'Release-Common.ps1')
Assert-ReleaseVersion $Version
$tag = "v$Version"
if ($env:GITHUB_SHA -notmatch '^[a-fA-F0-9]{40}$') { throw 'A full source commit SHA is required.' }
if (Test-ReleaseExists $tag) {
    "Skipped publication: $tag appeared while the build ran. Existing tag and assets were preserved." | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    exit 0
}
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $project 'artifacts/release'
$assetNames = @("MotionVfxLive-$Version-Setup.exe", "MotionVfxLive-$Version-Setup.exe.sha256", "MotionVfxLive-$Version-win-x64.zip", "MotionVfxLive-$Version-win-x64.zip.sha256", "MotionVfxLive-$Version-installer-checks.json", "MotionVfxLive-$Version-build.json")
$assetPaths = @($assetNames | ForEach-Object { (Get-Item -LiteralPath (Join-Path $output $_) -ErrorAction Stop).FullName })
$test = Get-Content -LiteralPath (Join-Path $output "MotionVfxLive-$Version-installer-checks.json") -Raw | ConvertFrom-Json
if ($test.passed -ne $true) { throw 'Passing installer checks are required before publication.' }
# Creating a ref fails if it already exists. Never force-update an existing tag.
$created = Invoke-ReleaseApi -Path 'git/refs' -Method POST -Body @{ ref = "refs/tags/$tag"; sha = $env:GITHUB_SHA }
if ($created.object.sha -ne $env:GITHUB_SHA) { throw 'Created tag does not match the tested commit.' }
$notes = Join-Path $PSScriptRoot "../release-notes/$Version.md"
& gh release create $tag @assetPaths --repo $env:GITHUB_REPOSITORY --verify-tag --title "MotionVfx Live $Version" --notes-file $notes
if ($LASTEXITCODE -ne 0) { throw 'Release upload failed. The tag was preserved. Inspect GitHub before retrying; this workflow never overwrites existing tags or assets.' }
$release = Invoke-ReleaseApi -Path "releases/tags/$tag"
if ($release.draft) { throw 'Release is still a draft.' }
foreach ($name in $assetNames) {
    $asset = @($release.assets | Where-Object name -EQ $name)
    $size = (Get-Item -LiteralPath (Join-Path $output $name)).Length
    if ($asset.Count -ne 1 -or $asset[0].state -ne 'uploaded' -or $asset[0].size -ne $size) { throw "Published asset verification failed: $name" }
}
"Published [$tag]($($release.html_url)) with all $($assetNames.Count) verified assets." | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
