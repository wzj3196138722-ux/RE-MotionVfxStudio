param([Parameter(Mandatory=$true)][string]$GameRoot, [switch]$ValidateOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$workspaceRoot = Split-Path $projectRoot -Parent
$gamePath = (Resolve-Path -LiteralPath $GameRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $gamePath 'MonsterHunterWilds.exe'))) { throw 'Selected directory does not contain MonsterHunterWilds.exe.' }
$sources = @(
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive.lua'; Relative = 'reframework/autorun/MotionVfxLive.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/手动重载MotionVfxLive.lua'; Relative = 'reframework/data/MotionVfxLive/Reload.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/刷新MotionVfxLive输入框.lua'; Relative = 'reframework/data/MotionVfxLive/RefreshTextInputs.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/修复MotionVfxLive资源还原.lua'; Relative = 'reframework/data/MotionVfxLive/RepairBindings.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_action_capture.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_action_capture.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_action_bindings.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_action_bindings.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_action_sources.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_action_sources.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_attachment_targets.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_attachment_targets.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_pfb_page.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_pfb_page.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_file_dialog.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_file_dialog.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_pfb_preview_runtime.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_pfb_preview_runtime.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_equipment_sources.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_equipment_sources.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_equipment_pfb_preview_runtime.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_equipment_pfb_preview_runtime.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_action_preview_transaction.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_action_preview_transaction.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_action_preview_runtime.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_action_preview_runtime.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_ai_bridge.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_ai_bridge.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_debug_hotkeys.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_debug_hotkeys.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_precache_runtime.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_precache_runtime.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_d2d_widgets.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_d2d_widgets.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_d2d_workbench.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_d2d_workbench.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_parameter_ui.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_parameter_ui.lua' },
    @{ Source = Join-Path $projectRoot 'game-plugin/images/MotionVfxLive/logo.png'; Relative = 'reframework/images/MotionVfxLive/logo.png' },
    @{ Source = Join-Path $projectRoot 'game-plugin/images/MotionVfxLive/author-logo.png'; Relative = 'reframework/images/MotionVfxLive/author-logo.png' },
    @{ Source = Join-Path $projectRoot 'third-party/reframework-d2d/package/reframework/plugins/reframework-d2d.dll'; Relative = 'reframework/plugins/reframework-d2d.dll' },
    @{ Source = Join-Path $projectRoot 'third-party/reframework-d2d/package/reframework/autorun/reframework-d2d.lua'; Relative = 'reframework/autorun/reframework-d2d.lua' },
    @{ Source = Join-Path $projectRoot 'third-party/reframework-d2d/LICENSE'; Relative = 'reframework/plugins/reframework-d2d-LICENSE.txt' },
    @{ Source = Join-Path $projectRoot 'tools/DebugInput/build/MotionVfxDebugInput.dll'; Relative = 'reframework/plugins/MotionVfxDebugInput.dll' },
    @{ Source = Join-Path $projectRoot 'tools/DebugInput/THIRD-PARTY.txt'; Relative = 'reframework/plugins/MotionVfxDebugInput-LICENSE.txt' },
    @{ Source = Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_preview_runtime.lua'; Relative = 'reframework/autorun/MotionVfxLive/fx_preview_runtime.lua' }
)
foreach ($item in $sources) { if (-not (Test-Path -LiteralPath $item.Source)) { throw ('Missing prepared source: ' + $item.Source) } }
# Refuse a binary replacement before any script is copied. An identical DLL
# needs no replacement and permits Lua-only fixes while the game is running.
foreach ($item in $sources | Where-Object { $_.Relative.EndsWith('.dll') }) {
    $nativeTarget = Join-Path $gamePath $item.Relative
    $changed = -not (Test-Path -LiteralPath $nativeTarget)
    if (-not $changed) { $changed = (Get-FileHash -LiteralPath $item.Source).Hash -ne (Get-FileHash -LiteralPath $nativeTarget).Hash }
    if ($changed -and (Get-Process -Name MonsterHunterWilds -ErrorAction SilentlyContinue)) {
        throw 'A native input or D2D component is new or changed. Close the game before installing; no scripts have been replaced.'
    }
}
if ($ValidateOnly) { Write-Output 'All plugin installation sources passed preflight. No files were installed.'; return }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$dataPath = Join-Path $gamePath 'reframework/data/MotionVfxLive'
$backupRoot = Join-Path $dataPath ('backups/' + $stamp)
$records = @()
function Copy-Content([string]$Source,[string]$Target) {
    # Copy bytes, not EFS attributes inherited from a different drive.
    $inputFile=[IO.File]::OpenRead($Source)
    try {
        $outputFile=[IO.File]::Open($Target,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $inputFile.CopyTo($outputFile);$outputFile.Flush($true) } finally { $outputFile.Dispose() }
    } finally { $inputFile.Dispose() }
}
try {
foreach ($item in $sources) {
    $destination = [IO.Path]::GetFullPath((Join-Path $gamePath $item.Relative))
    if (-not $destination.StartsWith($gamePath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Deployment path escaped the selected game directory.' }
    $hash = (Get-FileHash -LiteralPath $item.Source -Algorithm SHA256).Hash
    if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $hash) { continue }
    $backup = $null
    if (Test-Path -LiteralPath $destination) {
        $backup = Join-Path $backupRoot $item.Relative
        New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null
        Copy-Content $destination $backup
    }
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    $records += [ordered]@{ destination = $destination; backup = $backup; sha256 = $hash }
    $temporary = $destination + '.' + $stamp + '.tmp'
    try {
        Copy-Content $item.Source $temporary
        Move-Item -LiteralPath $temporary -Destination $destination -Force
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) { throw ('Installed file failed checksum: '+$destination) }
}
} catch {
    for ($i=$records.Count-1;$i -ge 0;$i--) {
        $record=$records[$i]
        if ($record.backup) { Copy-Content $record.backup $record.destination }
        elseif (Test-Path -LiteralPath $record.destination) { Remove-Item -LiteralPath $record.destination }
    }
    throw
}
New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
$manifestPath = Join-Path $dataPath ('install-' + $stamp + '.json')
[ordered]@{ installedAt = [DateTime]::UtcNow.ToString('o'); gameRoot = $gamePath; files = $records } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output ('Prepared game scripts. Manifest: ' + $manifestPath)
Write-Output 'The editor loads automatically on the next game start.'
Write-Output 'Debug keyboard input uses a native plugin. Its first installation or binary update requires restarting the game; Reload.lua cannot load the DLL.'
Write-Output ('First load via REFramework / ScriptRunner / Run script: ' + (Join-Path $gamePath 'reframework/autorun/MotionVfxLive.lua'))
Write-Output ('To update only an already-loaded editor, run: ' + (Join-Path $dataPath 'Reload.lua'))
Write-Output 'Capture 1.4.2 requires restoring resources and scene state, then Reset scripts when updating a running game; Reload.lua deliberately retains the existing shared capture module.'
Write-Output ('To preserve running binding module 1.1.1 or 1.2.0 resource backups while upgrading to 1.2.1, run: ' + (Join-Path $dataPath 'RepairBindings.lua'))
Write-Output 'If the shared playback runtime has changed, restart the game to load that runtime update.'
