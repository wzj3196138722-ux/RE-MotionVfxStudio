# Fails when production code hard-codes one game's file versions, executable, RSZ table or
# game-layer type names outside the game profile. The profile files are the only allowed place:
#   VfxStudio.Core/Games/GameProfile.cs and game-plugin/MotionVfxLive/fx_game_profile.lua
# Excluded on purpose (documented in VfxStudio.Core/Games/README.md):
#   - *Tests.cs / SelfTests.cs / ActionSlotAudit.cs: offline tests built on Wilds fixtures
#   - fx_equipment_*.lua: Wilds-only equipment rebuild, loaded only when features.equipment_rebuild
#   - VfxStudio.App / Renderer / Unpacker and Core/Services other than EfxEditorService: legacy desktop prototype
#   - Formats/: the embedded ReeLib copy, which supports every game by design
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tokens = @(
    '5571972', '3539837', '.pfb.18', '.motlist.992', '.mot.932',
    'MonsterHunterWilds', 'EfxVersion.MHWilds', 'ClipVersion.MHWilds', 'GameName.mhwilds', 'rsz_pfb_classes.json',
    '"app.', "'app.", '"ace.', '"chainsaw.', 'getMasterPlayer', 'get_WeaponID'
)
$files = @()
$files += Get-ChildItem -LiteralPath (Join-Path $root 'VfxStudio.LiveEditor') -Filter '*.cs' -File |
    Where-Object { $_.Name -notmatch 'Tests\.cs$' -and $_.Name -ne 'ActionSlotAudit.cs' }
$files += Get-ChildItem -LiteralPath (Join-Path $root 'VfxStudio.Mcp') -Filter '*.cs' -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
$files += Get-Item -LiteralPath (Join-Path $root 'VfxStudio.Core/Services/EfxEditorService.cs')
$files += Get-Item -LiteralPath (Join-Path $root 'game-plugin/MotionVfxLive.lua')
$files += Get-ChildItem -LiteralPath (Join-Path $root 'game-plugin/MotionVfxLive') -Filter 'fx_*.lua' -File |
    Where-Object { $_.Name -ne 'fx_game_profile.lua' -and $_.Name -notlike 'fx_equipment_*' }

$hits = @()
foreach ($file in $files) {
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
        $lineNumber++
        foreach ($token in $tokens) {
            if ($line.Contains($token)) {
                $hits += ('{0}:{1}: {2}' -f $file.FullName.Substring($root.Length + 1), $lineNumber, $token)
            }
        }
    }
}
if ($hits.Count -gt 0) {
    $hits | ForEach-Object { Write-Output $_ }
    throw ('Game-specific values outside the game profile: ' + $hits.Count + ' hit(s). Move them into GameProfile.cs / fx_game_profile.lua.')
}
Write-Output ('Game coupling check passed: ' + $files.Count + ' production files, no hard-coded game values outside the profiles.')
