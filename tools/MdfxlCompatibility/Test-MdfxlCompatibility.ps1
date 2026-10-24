[CmdletBinding()]
param(
    [string]$PowerShellExecutable = 'powershell.exe',
    [string]$OriginalScript,
    [switch]$Quick
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$tool = Join-Path $PSScriptRoot 'Apply-MdfxlCompatibility.ps1'
$guard = Join-Path $PSScriptRoot 'MDF-XL-frame-guard.lua'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('.test-' + [guid]::NewGuid().ToString('N'))))
$checks = 0
$cases = 0
$junctions = New-Object System.Collections.Generic.List[string]
$source = @'
local modName = "MDF-XL"
local modAuthor = "SilverEzredes"
local modVersion = "v1.5.36"
local original_settings = { unchanged = true }
re.on_frame(function()
    return original_settings
end)
'@
# Keep this test source ASCII so Windows PowerShell 5.1 reads it correctly.
$chinese = ([char]0x6D4B).ToString() + [char]0x8BD5
$source += "`r`n-- " + $chinese + "`r`n"
$script:fixtureSequence = 0

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAILED: $Message" }
    $script:checks++
}

function Same-Bytes([byte[]]$Left, [byte[]]$Right) {
    return [Convert]::ToBase64String($Left) -ceq [Convert]::ToBase64String($Right)
}

function New-Fixture([switch]$Bom, [string]$Name = 'game') {
    $script:fixtureSequence++
    $root = Join-Path $workspace ($script:fixtureSequence.ToString() + '-' + $Name)
    $autorun = Join-Path $root 'reframework\autorun'
    [System.IO.Directory]::CreateDirectory($autorun) | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $root 'MonsterHunterWilds.exe'), [byte[]]@())
    $target = Join-Path $autorun 'MDF-XL.lua'
    [byte[]]$bytes = $utf8.GetBytes($source)
    if ($Bom) { $bytes = [byte[]](@(239, 187, 191) + $bytes) }
    [System.IO.File]::WriteAllBytes($target, $bytes)
    $settings = Join-Path $autorun 'MDF-XL-settings.json'
    $other = Join-Path $autorun 'OtherMod.lua'
    [System.IO.File]::WriteAllText($settings, '{"preserve":"user settings"}', $utf8)
    [System.IO.File]::WriteAllText($other, '-- untouched', $utf8)
    return @{ Root = $root; Target = $target; Backup = $target + '.motionvfx-compat.bak'; Metadata = $target + '.motionvfx-compat.json'; Original = $bytes; Settings = $settings; Other = $other }
}

function Invoke-Patch($Fixture, [switch]$Restore, [string]$ScriptPath = $tool, [switch]$AutoDirectory, [switch]$RunningGame, [int]$ExpectedExit = 0) {
    $script:cases++
    $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass')
    if ($RunningGame) {
        # The test is isolated from actual processes: provide a fake process lookup
        # in the child PowerShell scope, without adding an installation bypass flag.
        $escapedTool = $ScriptPath.Replace("'", "''")
        $escapedRoot = $Fixture.Root.Replace("'", "''")
        $command = "function Get-Process { param(`$Name, `$ErrorAction) [pscustomobject]@{ ProcessName = 'MonsterHunterWilds' } }; & '$escapedTool' -GameDirectory '$escapedRoot'"
        $arguments += @('-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)))
    } else {
        $arguments += @('-File', $ScriptPath)
        if (-not $AutoDirectory) { $arguments += @('-GameDirectory', $Fixture.Root) }
        if ($Restore) { $arguments += '-Restore' }
    }
    $result = & $PowerShellExecutable @arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    Assert ($code -eq $ExpectedExit) "case $cases expected exit $ExpectedExit, got $code : $result"
    return $result
}

function Read-Meta($Fixture) {
    return ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($Fixture.Metadata, $utf8))
}

function Write-Meta($Fixture, $Metadata) {
    [System.IO.File]::WriteAllText($Fixture.Metadata, ($Metadata | ConvertTo-Json -Depth 5), $utf8)
}

function Assert-Untouched($Fixture) {
    Assert (([System.IO.File]::ReadAllText($Fixture.Settings, $utf8)) -ceq '{"preserve":"user settings"}') 'User settings were modified.'
    Assert (([System.IO.File]::ReadAllText($Fixture.Other, $utf8)) -ceq '-- untouched') 'Another mod was modified.'
    Assert (@(Get-ChildItem -LiteralPath (Split-Path -Parent $Fixture.Target) -Filter '*.motionvfx-tmp-*' -File).Count -eq 0) 'A temporary write file was left behind.'
}

try {
    [System.IO.Directory]::CreateDirectory($workspace) | Out-Null
    foreach ($bom in @($false, $true)) {
        $f = New-Fixture -Bom:$bom -Name ($chinese + " spaces' [brackets]")
        Invoke-Patch $f | Out-Null
        $installed = [System.IO.File]::ReadAllBytes($f.Target)
        $meta = Read-Meta $f
        Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($f.Backup)) $f.Original) 'Backup is not byte-for-byte original.'
        Assert ($meta.originalLength -eq $f.Original.Length -and $meta.installedLength -eq $installed.Length) 'Recorded lengths are incorrect.'
        $decoded = $utf8.GetString($installed)
        Assert (([regex]::Matches($decoded, '-- BEGIN MotionVfxLive MDF-XL compatibility v1')).Count -eq 1) 'Prefix was not inserted exactly once.'
        Assert (($installed[0] -eq 239 -and $installed[1] -eq 187 -and $installed[2] -eq 191) -eq $bom) 'UTF-8 BOM changed.'
        $originalOffset = $installed.Length - $f.Original.Length
        $bodyOffset = 0
        if ($bom) { $bodyOffset = 3; $originalOffset += 3 }
        $expectedBody = [byte[]]$f.Original[$bodyOffset..($f.Original.Length - 1)]
        $actualBody = [byte[]]$installed[$originalOffset..($installed.Length - 1)]
        Assert (Same-Bytes $expectedBody $actualBody) 'Original Lua bytes were normalized or changed.'
        Invoke-Patch $f | Out-Null
        Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($f.Target)) $installed) 'Repeated installation changed the installed file.'
        Invoke-Patch $f -Restore | Out-Null
        Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($f.Target)) $f.Original) 'Restore did not preserve the exact original bytes.'
        Invoke-Patch $f -Restore | Out-Null
        Assert (Test-Path -LiteralPath $f.Backup) 'Restore deleted the original backup.'
        Invoke-Patch $f | Out-Null
        Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($f.Target)) $installed) 'Reapply after restore changed the installed bytes.'
        Assert-Untouched $f
    }

    if (-not $Quick) {
    $f = New-Fixture
    Invoke-Patch $f | Out-Null
    $updated = $utf8.GetBytes($source + "`r`n-- user update")
    [System.IO.File]::WriteAllBytes($f.Target, $updated)
    Invoke-Patch $f -ExpectedExit 1 | Out-Null
    Invoke-Patch $f -Restore -ExpectedExit 1 | Out-Null
    Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($f.Target)) $updated) 'A user update was overwritten.'
    Assert-Untouched $f

    $f = New-Fixture
    [System.IO.File]::Delete((Join-Path $f.Root 'MonsterHunterWilds.exe'))
    Invoke-Patch $f -ExpectedExit 1 | Out-Null
    Assert (-not (Test-Path -LiteralPath $f.Backup)) 'A missing game root still created a backup.'

    $f = New-Fixture
    Invoke-Patch $f -RunningGame -ExpectedExit 1 | Out-Null
    Assert (-not (Test-Path -LiteralPath $f.Backup)) 'A running game was not refused before writes.'

    $f = New-Fixture
    [System.IO.File]::WriteAllBytes($f.Backup, $f.Original)
    Invoke-Patch $f | Out-Null
    Invoke-Patch $f -Restore | Out-Null
    Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($f.Target)) $f.Original) 'Orphan backup recovery failed.'

    $f = New-Fixture
    Invoke-Patch $f | Out-Null
    [System.IO.File]::WriteAllBytes($f.Target, $f.Original)
    Invoke-Patch $f | Out-Null
    Assert (($utf8.GetString([System.IO.File]::ReadAllBytes($f.Target))).Contains('-- BEGIN MotionVfxLive MDF-XL compatibility v1')) 'Interrupted install with metadata did not resume.'

    $f = New-Fixture
    [System.IO.File]::WriteAllText($f.Backup, '-- mismatched original', $utf8)
    Invoke-Patch $f -ExpectedExit 1 | Out-Null
    Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($f.Target)) $f.Original) 'Unmatched orphan backup changed the current file.'

    $f = New-Fixture
    Invoke-Patch $f | Out-Null
    $installed = [System.IO.File]::ReadAllBytes($f.Target)
    [System.IO.File]::Delete($f.Metadata)
    Invoke-Patch $f -ExpectedExit 1 | Out-Null
    Invoke-Patch $f -Restore -ExpectedExit 1 | Out-Null
    Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($f.Target)) $installed) 'Patched file without metadata was modified.'

    foreach ($failure in @('bad-json', 'unknown-schema', 'missing-key', 'wrong-field-type', 'path-traversal', 'bad-hash', 'wrong-guard', 'wrong-date', 'wrong-length', 'missing-backup', 'changed-backup')) {
        $f = New-Fixture
        Invoke-Patch $f | Out-Null
        $installed = [System.IO.File]::ReadAllBytes($f.Target)
        $meta = Read-Meta $f
        switch ($failure) {
            'bad-json' { [System.IO.File]::WriteAllText($f.Metadata, '{', $utf8) }
            'unknown-schema' { $meta.schemaVersion = 2; Write-Meta $f $meta }
            'missing-key' { $meta.PSObject.Properties.Remove('guardSha256'); Write-Meta $f $meta }
            'wrong-field-type' { $meta.targetFile = @(); Write-Meta $f $meta }
            'path-traversal' { $meta.backupFile = '..\..\outside.lua'; Write-Meta $f $meta }
            'bad-hash' { $meta.originalSha256 = 'bad'; Write-Meta $f $meta }
            'wrong-guard' { $meta.guardSha256 = ('0' * 64); Write-Meta $f $meta }
            'wrong-date' { $meta.installedUtc = 'not a date'; Write-Meta $f $meta }
            'wrong-length' { $meta.originalLength = -1; Write-Meta $f $meta }
            'missing-backup' { [System.IO.File]::Delete($f.Backup) }
            'changed-backup' { [System.IO.File]::WriteAllText($f.Backup, $source + '-- changed', $utf8) }
        }
        Invoke-Patch $f -ExpectedExit 1 | Out-Null
        Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($f.Target)) $installed) "Malformed state $failure changed the target."
    }

    foreach ($failure in @('wrong-name', 'wrong-author', 'wrong-version', 'missing-frame', 'local-re', 'unknown-prefix', 'invalid-utf8')) {
        $f = New-Fixture
        $changed = $source
        switch ($failure) {
            'wrong-name' { $changed = $changed.Replace('MDF-XL', 'OtherMod') }
            'wrong-author' { $changed = $changed.Replace('SilverEzredes', 'OtherAuthor') }
            'wrong-version' { $changed = $changed.Replace('v1.5.36', 'v1.5.37') }
            'missing-frame' { $changed = $changed.Replace('re.on_frame', 're.on_draw_ui') }
            'local-re' { $changed = "local re = re`r`n" + $changed }
            'unknown-prefix' { $changed = "-- BEGIN MotionVfxLive MDF-XL compatibility v99`r`n" + $changed }
        }
        $bytes = $utf8.GetBytes($changed)
        if ($failure -eq 'invalid-utf8') { $bytes = [byte[]]($bytes + @(255)) }
        [System.IO.File]::WriteAllBytes($f.Target, $bytes)
        Invoke-Patch $f -ExpectedExit 1 | Out-Null
        Assert (-not (Test-Path -LiteralPath $f.Backup)) "Unsupported source $failure created a backup."
    }

    $f = New-Fixture
    $nested = Join-Path $f.Root 'downloaded patch\nested'
    [System.IO.Directory]::CreateDirectory($nested) | Out-Null
    $nestedTool = Join-Path $nested 'Apply-MdfxlCompatibility.ps1'
    [System.IO.File]::Copy($tool, $nestedTool)
    [System.IO.File]::Copy($guard, (Join-Path $nested 'MDF-XL-frame-guard.lua'))
    Invoke-Patch $f -ScriptPath $nestedTool -AutoDirectory | Out-Null
    Assert (Test-Path -LiteralPath $f.Backup) 'Automatic upward game-root discovery failed.'

    # A junction is available without administrator/developer-mode privileges.
    # All link targets remain in this dedicated test workspace.
    $f = New-Fixture
    $outside = Join-Path $workspace 'outside-owner'
    [System.IO.Directory]::CreateDirectory($outside) | Out-Null
    $outsideTarget = Join-Path $outside 'MDF-XL.lua'
    [System.IO.File]::WriteAllBytes($outsideTarget, $f.Original)
    $originalAutorun = Split-Path -Parent $f.Target
    $preservedAutorun = $originalAutorun + '-preserved'
    [System.IO.Directory]::Move($originalAutorun, $preservedAutorun)
    try {
        New-Item -ItemType Junction -Path $originalAutorun -Target $outside | Out-Null
        $junctions.Add($originalAutorun)
        Invoke-Patch $f -ExpectedExit 1 | Out-Null
        Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($outsideTarget)) $f.Original) 'A junction escaped the game directory.'
        Assert (-not (Test-Path -LiteralPath ($outsideTarget + '.motionvfx-compat.bak'))) 'A backup was created through a junction.'
    } catch {
        if (Test-Path -LiteralPath $originalAutorun) { throw }
        throw "Junction fixture could not be created; test not complete: $($_.Exception.Message)"
    }

    $f = New-Fixture
    $rootAlias = Join-Path $workspace 'game-root-alias'
    New-Item -ItemType Junction -Path $rootAlias -Target $f.Root | Out-Null
    $junctions.Add($rootAlias)
    Invoke-Patch @{ Root = $rootAlias } -ExpectedExit 1 | Out-Null
    Assert (-not (Test-Path -LiteralPath $f.Backup)) 'A reparse-point game root was accepted.'
    }

    if (-not [string]::IsNullOrWhiteSpace($OriginalScript)) {
        $f = New-Fixture
        $bytes = [System.IO.File]::ReadAllBytes([System.IO.Path]::GetFullPath($OriginalScript))
        [System.IO.File]::WriteAllBytes($f.Target, $bytes)
        Invoke-Patch $f | Out-Null
        Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($f.Backup)) $bytes) 'Actual MDF-XL source backup differed.'
        Invoke-Patch $f | Out-Null
        Invoke-Patch $f -Restore | Out-Null
        Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($f.Target)) $bytes) 'Actual MDF-XL source restore differed.'
        Assert (Same-Bytes ([System.IO.File]::ReadAllBytes($OriginalScript)) $bytes) 'The source outside the fixture was changed.'
        Assert-Untouched $f
    }

    Write-Host "PASS: $checks assertions across $cases patch/restore invocations using $PowerShellExecutable. Only isolated test fixtures were modified." -ForegroundColor Green
} finally {
    foreach ($junction in $junctions) {
        $full = [System.IO.Path]::GetFullPath($junction)
        if (-not $full.StartsWith($workspace + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the test workspace.' }
        if (Test-Path -LiteralPath $full) { [System.IO.Directory]::Delete($full) }
    }
    $expectedParent = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([char[]]'\/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $workspace.StartsWith($expectedParent, [System.StringComparison]::OrdinalIgnoreCase) -or -not ([System.IO.Path]::GetFileName($workspace)).StartsWith('.test-')) { throw 'Refusing unexpected recursive cleanup path.' }
    if (Test-Path -LiteralPath $workspace) { Remove-Item -LiteralPath $workspace -Recurse -Force }
}
