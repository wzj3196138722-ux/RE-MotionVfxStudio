param(
    [string]$AssetsPath = (Join-Path $PSScriptRoot '../../../Assets'),
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'EfxBufferValidator.cs')
$assets = (Resolve-Path -LiteralPath $AssetsPath).Path
$seen = [Collections.Generic.HashSet[string]]::new()
$failures = [Collections.Generic.List[object]]::new()
$examples = [Collections.Generic.List[object]]::new()
$checks = 0
$passed = 0
$minimum = [int]::MaxValue
$maximum = 0
$fixture = $null

function Check([bool]$condition, [string]$label, [string]$path) {
    $script:checks++
    if ($condition) { $script:passed++ }
    else { $script:failures.Add(@{ label = $label; path = $path }) }
}

# Only local serialized files are read. No game, process-memory, deployment or live API is used.
foreach ($item in [IO.Directory]::EnumerateFiles($assets, '*.efx.5571972', [IO.SearchOption]::AllDirectories)) {
    $bytes = [IO.File]::ReadAllBytes($item)
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if (!$seen.Add($hash)) { continue }
    $result = [MotionVfx.CacheDiagnostics.EfxBufferValidator]::Validate($bytes)
    Check ($result.Valid -and $result.ExactLength -eq $bytes.Length) 'Exact framing of real file' $item
    $minimum = [Math]::Min($minimum, $bytes.Length)
    $maximum = [Math]::Max($maximum, $bytes.Length)
    if ($examples.Count -lt 5) {
        $examples.Add(@{ path = $item; sha256 = $hash; bytes = $bytes.Length; result = $result })
    }
    foreach ($cut in @(1, 4, 8, 16)) {
        $truncated = [byte[]]::new($bytes.Length - $cut)
        [Array]::Copy($bytes, $truncated, $truncated.Length)
        $shortResult = [MotionVfx.CacheDiagnostics.EfxBufferValidator]::Validate($truncated)
        Check (!$shortResult.Valid) "Reject missing final $cut bytes" $item
    }
    $withTail = [byte[]]::new($bytes.Length + 64)
    [Array]::Copy($bytes, $withTail, $bytes.Length)
    [Array]::Fill[byte]($withTail, 0xCD, $bytes.Length, 64)
    $longResult = [MotionVfx.CacheDiagnostics.EfxBufferValidator]::Validate($withTail)
    Check ($longResult.Valid -and $longResult.ExactLength -eq $bytes.Length) 'Exclude surrounding allocation bytes from exact length' $item
    $boundedResult = [MotionVfx.CacheDiagnostics.EfxBufferValidator]::Validate($bytes, $bytes.Length - 1)
    Check (!$boundedResult.Valid) 'Respect caller candidate-size bound' $item
    $afterHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($item)))
    Check ($hash -eq $afterHash) 'Source bytes remain unchanged' $item
    if ([IO.Path]::GetFileName($item) -eq '11_it00_900.efx.5571972') { $fixture = $bytes }
}
if ($seen.Count -eq 0) { throw 'No real EFX fixtures were found.' }
if ($null -eq $fixture) { throw 'The known it00_900 framing fixture was not found.' }

foreach ($mutation in @(
    @{ Name = 'Wrong magic'; Offset = 0; Value = 0 },
    @{ Name = 'Negative entry count'; Offset = 8; Value = -1 },
    @{ Name = 'Unbounded entry count'; Offset = 8; Value = [int]::MaxValue },
    @{ Name = 'Unbounded string table'; Offset = 12; Value = [int]::MaxValue },
    @{ Name = 'Inconsistent group table'; Offset = 32; Value = 1 }
)) {
    $bad = [byte[]]$fixture.Clone()
    [Array]::Copy([BitConverter]::GetBytes([int]$mutation.Value), 0, $bad, [int]$mutation.Offset, 4)
    Check (![MotionVfx.CacheDiagnostics.EfxBufferValidator]::Validate($bad).Valid) $mutation.Name '<synthetic mutation>'
}
# This fixture has no actions or field parameters: its first entry follows the fixed prelude.
if ([BitConverter]::ToInt32($fixture, 16) -ne 0 -or [BitConverter]::ToInt32($fixture, 20) -ne 0) {
    throw 'The known fixture layout changed; update the targeted malformed-attribute test.'
}
$entryStart = 48 + [BitConverter]::ToInt32($fixture, 12) + 24 * [BitConverter]::ToInt32($fixture, 24) + 8 * [BitConverter]::ToInt32($fixture, 36) + 2 * [BitConverter]::ToInt32($fixture, 40)
$badSize = [byte[]]$fixture.Clone()
[Array]::Copy([BitConverter]::GetBytes([int]3), 0, $badSize, $entryStart + 20, 4)
Check (![MotionVfx.CacheDiagnostics.EfxBufferValidator]::Validate($badSize).Valid) 'Attribute size must include at least its UniqueID' '<synthetic mutation>'
$prefixed = [byte[]]::new($fixture.Length + 8)
[Array]::Copy($fixture, 0, $prefixed, 8, $fixture.Length)
Check (![MotionVfx.CacheDiagnostics.EfxBufferValidator]::Validate($prefixed).Valid) 'No searching for a later magic value' '<prefixed fixture>'

$report = [ordered]@{
    observedAtUtc = [DateTime]::UtcNow.ToString('O')
    assets = $assets
    uniqueFiles = $seen.Count
    checks = $checks
    passed = $passed
    failed = $failures.Count
    minimumFileBytes = $minimum
    maximumFileBytes = $maximum
    examples = $examples
    failures = $failures
    liveMemoryRead = $false
    scope = 'Framing/candidate exclusion only. Does not prove live resource provenance or attribute semantics.'
}
$json = $report | ConvertTo-Json -Depth 6
if ($OutputPath) {
    $output = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
    [IO.File]::WriteAllText($output, $json, [Text.UTF8Encoding]::new($false))
}
$json
if ($failures.Count -gt 0) { exit 1 }
