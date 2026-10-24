[CmdletBinding()]
param(
    [string]$GameDirectory,
    [switch]$Restore
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$script:MaxLuaBytes = 16MB
$script:PatchId = 'MotionVfxLive-MDFXL-frame-guard-v1'
$script:MarkerStart = '-- BEGIN MotionVfxLive MDF-XL compatibility v1'
$script:MarkerEnd = '-- END MotionVfxLive MDF-XL compatibility v1'

function Get-CanonicalPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full -eq [System.IO.Path]::GetPathRoot($full)) { return $full }
    return $full.TrimEnd([char[]]'\/')
}

function Assert-NoReparsePoint([string]$Path) {
    $candidate = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($candidate)) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a symbolic link or junction: $candidate"
            }
        }
        $parent = [System.IO.Path]::GetDirectoryName($candidate)
        if ($parent -eq $candidate) { break }
        $candidate = $parent
    }
}

function Assert-OwnedPath([string]$Path, [string]$Root) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $prefix = $Root.TrimEnd([char[]]'\/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a path outside the game directory: $full"
    }
    Assert-NoReparsePoint $full
}

function Get-Hash([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Read-Bytes([string]$Path, [long]$Limit) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or $item.Length -gt $Limit) {
        throw "File is not a supported regular file, or is too large: $Path"
    }
    return ,([System.IO.File]::ReadAllBytes($Path))
}

function Decode-Utf8([byte[]]$Bytes) {
    $offset = 0
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 239 -and $Bytes[1] -eq 187 -and $Bytes[2] -eq 191) { $offset = 3 }
    try { return $script:Utf8.GetString($Bytes, $offset, $Bytes.Length - $offset) }
    catch { throw 'The Lua file is not valid UTF-8. No file has been replaced.' }
}

function Assert-SupportedOriginal([byte[]]$Bytes) {
    if ($Bytes.Length -gt $script:MaxLuaBytes) { throw 'The original MDF-XL file is too large for this patch.' }
    $text = Decode-Utf8 $Bytes
    if ($text.Contains('MotionVfxLive MDF-XL compatibility')) {
        throw 'An existing or unknown compatibility prefix was found. Its original metadata is required; refusing to insert another prefix.'
    }
    $headers = @(
        '(?m)^\s*local\s+modName\s*=\s*["'']MDF-XL["'']',
        '(?m)^\s*local\s+modAuthor\s*=\s*["'']SilverEzredes["'']',
        '(?m)^\s*local\s+modVersion\s*=\s*["'']v1\.5\.36["'']'
    )
    foreach ($pattern in $headers) {
        if ($text -notmatch $pattern) {
            throw 'This patch supports the original MDF-XL v1.5.36 by SilverEzredes only. The expected script header was not found.'
        }
    }
    if ($text -notmatch '\bre\s*\.\s*on_frame\s*\(') { throw 'The expected MDF-XL frame registration was not found.' }
    # Intentionally conservative: a pre-existing local re can hide the prefix.
    if ($text -match '(?m)^\s*local\s+(?:[A-Za-z_][A-Za-z_0-9]*\s*,\s*)*re(?=\s*[,=;]|\s*$)' -or
        $text -match '(?m)^\s*local\s+function\s+re\s*\(') {
        throw 'The script already declares a local re variable. Refusing to modify an unverified layout.'
    }
}

function Read-Guard {
    $path = Join-Path $PSScriptRoot 'MDF-XL-frame-guard.lua'
    Assert-NoReparsePoint $path
    $bytes = Read-Bytes $path 1MB
    $text = Decode-Utf8 $bytes
    $trimmed = $text.TrimEnd([char[]]"`r`n")
    if (-not $text.StartsWith($script:MarkerStart + "`r`n", [System.StringComparison]::Ordinal) -and
        -not $text.StartsWith($script:MarkerStart + "`n", [System.StringComparison]::Ordinal)) {
        throw 'The supplied compatibility prefix has an invalid start marker.'
    }
    if (-not $trimmed.EndsWith($script:MarkerEnd, [System.StringComparison]::Ordinal) -or
        ([regex]::Matches($text, [regex]::Escape($script:MarkerStart))).Count -ne 1 -or
        ([regex]::Matches($text, [regex]::Escape($script:MarkerEnd))).Count -ne 1) {
        throw 'The supplied compatibility prefix has invalid or repeated markers.'
    }
    # The prefix has its own terminal newline; original bytes are never normalized.
    return ,($script:Utf8.GetBytes($trimmed + "`r`n"))
}

function New-PatchedBytes([byte[]]$Original, [byte[]]$Guard) {
    $bomLength = 0
    if ($Original.Length -ge 3 -and $Original[0] -eq 239 -and $Original[1] -eq 187 -and $Original[2] -eq 191) { $bomLength = 3 }
    $result = New-Object byte[] ($Original.Length + $Guard.Length)
    if ($bomLength -gt 0) { [System.Array]::Copy($Original, 0, $result, 0, $bomLength) }
    [System.Array]::Copy($Guard, 0, $result, $bomLength, $Guard.Length)
    [System.Array]::Copy($Original, $bomLength, $result, $bomLength + $Guard.Length, $Original.Length - $bomLength)
    return ,$result
}

function Write-Atomic([string]$Path, [byte[]]$Bytes, [bool]$Replace, [string]$Root) {
    Assert-OwnedPath $Path $Root
    $temporary = $Path + '.motionvfx-tmp-' + [System.Guid]::NewGuid().ToString('N')
    Assert-OwnedPath $temporary $Root
    try {
        $stream = New-Object System.IO.FileStream($temporary, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        Assert-OwnedPath $Path $Root
        if ($Replace) {
            # No destination backup is requested here: the immutable .bak already exists.
            # PowerShell 5.1 coerces $null to an empty string for this overload.
            # NullString passes a real null backup path to the .NET method.
            [System.IO.File]::Replace($temporary, $Path, [System.Management.Automation.Language.NullString]::Value)
        } else {
            # Move fails instead of overwriting an existing backup or metadata file.
            [System.IO.File]::Move($temporary, $Path)
        }
    } finally {
        if ([System.IO.File]::Exists($temporary)) { [System.IO.File]::Delete($temporary) }
    }
}

function Read-Metadata([string]$Path) {
    $text = Decode-Utf8 (Read-Bytes $Path 256KB)
    try { $meta = ConvertFrom-Json -InputObject $text }
    catch { throw 'The compatibility metadata is not valid JSON. No files have been replaced.' }
    if ($null -eq $meta -or $meta -is [array]) { throw 'Invalid compatibility metadata.' }
    $required = @('schemaVersion', 'patchId', 'targetFile', 'backupFile', 'sourceVersion', 'sourceAuthor', 'originalSha256', 'installedSha256', 'guardSha256', 'originalLength', 'installedLength', 'installedUtc')
    $actual = @($meta.PSObject.Properties.Name)
    if ($actual.Count -ne $required.Count) { throw 'Incomplete or unknown compatibility metadata schema.' }
    foreach ($key in $required) {
        if ($actual -cnotcontains $key) { throw "Missing compatibility metadata field: $key" }
    }
    foreach ($key in @('patchId', 'targetFile', 'backupFile', 'sourceVersion', 'sourceAuthor')) {
        if ($meta.$key -isnot [string]) { throw "Invalid metadata field type: $key" }
    }
    if (($meta.schemaVersion -isnot [int] -and $meta.schemaVersion -isnot [long]) -or $meta.schemaVersion -ne 1 -or
        $meta.patchId -cne $script:PatchId -or $meta.targetFile -cne 'MDF-XL.lua' -or
        $meta.backupFile -cne 'MDF-XL.lua.motionvfx-compat.bak' -or
        $meta.sourceVersion -cne 'v1.5.36' -or $meta.sourceAuthor -cne 'SilverEzredes') {
        throw 'Unsupported compatibility metadata or unexpected file names.'
    }
    foreach ($key in @('originalSha256', 'installedSha256', 'guardSha256')) {
        if ($meta.$key -isnot [string] -or $meta.$key -cnotmatch '^[a-f0-9]{64}$') { throw "Invalid metadata hash: $key" }
    }
    foreach ($key in @('originalLength', 'installedLength')) {
        if (($meta.$key -isnot [int] -and $meta.$key -isnot [long]) -or $meta.$key -le 0 -or $meta.$key -gt ($script:MaxLuaBytes + 1MB)) {
            throw "Invalid metadata file length: $key"
        }
    }
    if ($meta.installedLength -le $meta.originalLength -or $meta.originalSha256 -ceq $meta.installedSha256) { throw 'Inconsistent compatibility metadata.' }
    $parsedDate = [datetime]::MinValue
    # PowerShell 7.5+ automatically parses ISO dates; Windows PowerShell 5.1
    # preserves strings. Both are legitimate representations of our timestamp.
    if ($meta.installedUtc -isnot [datetime] -and
        ($meta.installedUtc -isnot [string] -or
        -not [datetime]::TryParseExact($meta.installedUtc, 'o', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsedDate))) {
        throw 'Invalid metadata timestamp.'
    }
    return $meta
}

try {
    if ([string]::IsNullOrWhiteSpace($GameDirectory)) {
        $search = [System.IO.DirectoryInfo]$PSScriptRoot
        while ($null -ne $search -and -not (Test-Path -LiteralPath (Join-Path $search.FullName 'MonsterHunterWilds.exe') -PathType Leaf)) { $search = $search.Parent }
        if ($null -eq $search) { throw 'Game directory not found. Extract this tool into the game directory beside MonsterHunterWilds.exe, or pass -GameDirectory with its full path.' }
        $GameDirectory = $search.FullName
    }
    $root = Get-CanonicalPath $GameDirectory
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Game directory does not exist: $root" }
    Assert-NoReparsePoint $root
    $exe = Join-Path $root 'MonsterHunterWilds.exe'
    Assert-OwnedPath $exe $root
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "MonsterHunterWilds.exe was not found in: $root" }
    if (@(Get-Process -Name MonsterHunterWilds -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Close Monster Hunter Wilds completely before applying or restoring this patch.' }

    $target = Join-Path $root 'reframework\autorun\MDF-XL.lua'
    $backup = $target + '.motionvfx-compat.bak'
    $metadataPath = $target + '.motionvfx-compat.json'
    foreach ($path in @($target, $backup, $metadataPath)) { Assert-OwnedPath $path $root }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "MDF-XL.lua was not found at the supported location: $target" }
    $current = Read-Bytes $target ($script:MaxLuaBytes + 1MB)
    $currentHash = Get-Hash $current
    $hasMetadata = Test-Path -LiteralPath $metadataPath
    $hasBackup = Test-Path -LiteralPath $backup
    $meta = $null

    if ($hasMetadata) {
        $meta = Read-Metadata $metadataPath
        if (-not $hasBackup) { throw 'The original backup is missing. Refusing to change the current script.' }
        $original = Read-Bytes $backup $script:MaxLuaBytes
        Assert-SupportedOriginal $original
        if ((Get-Hash $original) -cne $meta.originalSha256 -or $original.Length -ne $meta.originalLength) { throw 'The original backup does not match its metadata. Refusing to change the current script.' }
        if ($currentHash -cne $meta.originalSha256 -and $currentHash -cne $meta.installedSha256) { throw 'MDF-XL.lua has changed since the patch was installed (possibly an update or user edit). It will not be overwritten.' }
        if (($currentHash -ceq $meta.originalSha256 -and $current.Length -ne $meta.originalLength) -or
            ($currentHash -ceq $meta.installedSha256 -and $current.Length -ne $meta.installedLength)) { throw 'Current script length does not match the metadata.' }
    } else {
        Assert-SupportedOriginal $current
        $original = $current
        if ($hasBackup) {
            $original = Read-Bytes $backup $script:MaxLuaBytes
            Assert-SupportedOriginal $original
            if ((Get-Hash $original) -cne $currentHash) { throw 'An unmatched backup exists without metadata. Refusing to overwrite either file.' }
        }
    }

    if ($Restore) {
        if ($null -eq $meta -or $currentHash -ceq $meta.originalSha256) {
            Write-Host 'The original MDF-XL script is already in place. No files were changed.' -ForegroundColor Green
        } else {
            Assert-OwnedPath $target $root
            if ((Get-Hash (Read-Bytes $target ($script:MaxLuaBytes + 1MB))) -cne $currentHash) { throw 'The script changed during validation. No replacement was made.' }
            Write-Atomic $target $original $true $root
            Write-Host 'Restored the exact original MDF-XL.lua. The backup and metadata have been kept.' -ForegroundColor Green
        }
    } else {
        $guard = Read-Guard
        $patched = New-PatchedBytes $original $guard
        $guardHash = Get-Hash $guard
        $patchedHash = Get-Hash $patched
        if ($null -ne $meta) {
            if ($meta.guardSha256 -cne $guardHash -or $meta.installedSha256 -cne $patchedHash -or $meta.installedLength -ne $patched.Length) {
                throw 'This tool does not match the previously installed compatibility prefix. Restore with the original tool before changing patch versions.'
            }
        } else {
            if (-not $hasBackup) { Write-Atomic $backup $original $false $root }
            $meta = [ordered]@{
                schemaVersion = 1; patchId = $script:PatchId; targetFile = 'MDF-XL.lua'; backupFile = 'MDF-XL.lua.motionvfx-compat.bak'
                sourceVersion = 'v1.5.36'; sourceAuthor = 'SilverEzredes'; originalSha256 = $currentHash; installedSha256 = $patchedHash
                guardSha256 = $guardHash; originalLength = $original.Length; installedLength = $patched.Length; installedUtc = [datetime]::UtcNow.ToString('o')
            }
            # Metadata is committed before the script. A partial install still has a verified original.
            Write-Atomic $metadataPath ($script:Utf8.GetBytes(($meta | ConvertTo-Json -Depth 4) + "`r`n")) $false $root
        }
        if ($currentHash -ceq $patchedHash) {
            Write-Host 'This exact compatibility patch is already installed. No files were changed.' -ForegroundColor Green
        } else {
            Assert-OwnedPath $target $root
            if ((Get-Hash (Read-Bytes $target ($script:MaxLuaBytes + 1MB))) -cne $currentHash) { throw 'The script changed during validation. No replacement was made.' }
            if ((Get-Hash (Read-Bytes $backup $script:MaxLuaBytes)) -cne $meta.originalSha256) { throw 'The backup changed during validation. No replacement was made.' }
            Write-Atomic $target $patched $true $root
            Write-Host 'Installed the MDF-XL v1.5.36 compatibility prefix. Start the game normally.' -ForegroundColor Green
        }
    }
    Write-Host 'Only reframework\autorun\MDF-XL.lua is patched; MDF settings, presets and other mods are untouched.'
    exit 0
} catch {
    Write-Host ('FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'No unrelated files are removed. If access is denied, check that the game is closed and this folder is writable.'
    exit 1
}
