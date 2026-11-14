using ReeLib;
using ReeLib.Clip;
using ReeLib.Efx;

namespace VfxStudio.Core.Games;

/// <summary>
/// Everything that differs between supported RE Engine games. Production code must take
/// these values from the active profile instead of hard-coding one game's file versions,
/// executable or capabilities. tools/Check-GameCoupling.ps1 guards against regressions.
/// </summary>
public sealed record GameProfile(
    string Id,
    string DisplayName,
    string ReframeworkGameName,
    GameName ReeLibGame,
    string ExecutableName,
    string SteamFolderName,
    EfxVersion EfxVersion,
    string EfxSuffix,
    string PfbSuffix,
    string MotlistSuffix,
    string MotSuffix,
    ClipVersion ClipVersion,
    string RszSchemaFile,
    string PreviewLogicalRoot,
    IReadOnlyList<string> ActionResourceRoots,
    IReadOnlyList<string> PrefabResourceRoots,
    IReadOnlyList<string> EquipmentComponentTypes,
    GameFeatures Features)
{
    /// <summary>Process name as reported by <see cref="System.Diagnostics.Process.ProcessName"/>.</summary>
    public string ProcessName => Path.GetFileNameWithoutExtension(ExecutableName);

    public string SuffixFor(string kind)
    {
        string suffix = kind switch
        {
            "efx" => EfxSuffix,
            "pfb" => PfbSuffix,
            "motlist" => MotlistSuffix,
            "mot" => MotSuffix,
            _ => throw new ArgumentException("Unsupported resource kind: " + kind, nameof(kind)),
        };
        // An empty suffix means the version was never verified for this game; never guess one.
        return suffix.Length > 0 ? suffix : throw new NotSupportedException($"{DisplayName}: .{kind} file version is not configured.");
    }

    /// <summary>Regex alternation for <see cref="ActionResourceRoots"/>, e.g. <c>(?:art|motion)</c>.</summary>
    public string ActionRootPattern => "(?:" + string.Join("|", ActionResourceRoots.Select(System.Text.RegularExpressions.Regex.Escape)) + ")";
    /// <summary>Regex alternation for <see cref="PrefabResourceRoots"/>.</summary>
    public string PrefabRootPattern => "(?:" + string.Join("|", PrefabResourceRoots.Select(System.Text.RegularExpressions.Regex.Escape)) + ")";

    /// <summary>Appends the version suffix, e.g. <c>a/b.efx</c> → <c>a/b.efx.5571972</c>.</summary>
    public string Versioned(string logicalPath, string kind) => logicalPath + "." + SuffixFor(kind);
}

/// <summary>Editor capabilities that depend on game-specific runtime APIs or formats.</summary>
public sealed record GameFeatures(bool EquipmentRebuild, bool ActionSlots, bool PfbEditing);

public static class GameProfiles
{
    public static readonly GameProfile MHWilds = new(
        Id: "mhwilds",
        DisplayName: "Monster Hunter Wilds",
        ReframeworkGameName: "mhwilds",
        ReeLibGame: GameName.mhwilds,
        ExecutableName: "MonsterHunterWilds.exe",
        SteamFolderName: "MonsterHunterWilds",
        EfxVersion: EfxVersion.MHWilds,
        EfxSuffix: "5571972",
        PfbSuffix: "18",
        MotlistSuffix: "992",
        MotSuffix: "932",
        ClipVersion: ClipVersion.MHWilds,
        RszSchemaFile: "rsz_pfb_classes.json",
        PreviewLogicalRoot: "Art/VFX/EffectEditor/MotionVfxLive",
        ActionResourceRoots: ["art", "motion"],
        PrefabResourceRoots: ["art", "gamedesign"],
        // A PFB containing one of these components is weapon/armour equipment, not an effect provider.
        EquipmentComponentTypes: ["app.Weapon", "app.CharacterEditRegion"],
        Features: new(EquipmentRebuild: true, ActionSlots: true, PfbEditing: true));

    /// <summary>
    /// Resident Evil 4 (2023). Suffixes are from the extracted game files (.efx.3539837, .pfb.17); MOTLIST/MOT
    /// versions are not verified yet, so action slots stay off. RSZ table: REasy's rszre4.json.
    /// </summary>
    public static readonly GameProfile RE4 = new(
        Id: "re4",
        DisplayName: "Resident Evil 4",
        ReframeworkGameName: "re4",
        ReeLibGame: GameName.re4,
        ExecutableName: "re4.exe",
        SteamFolderName: "RESIDENT EVIL 4  BIOHAZARD RE4",
        EfxVersion: EfxVersion.RE4,
        EfxSuffix: "3539837",
        PfbSuffix: "17",
        MotlistSuffix: "",
        MotSuffix: "",
        ClipVersion: ClipVersion.RE4,
        RszSchemaFile: "rszre4.json",
        PreviewLogicalRoot: "_Chainsaw/VFX/EffectEditor/MotionVfxLive",
        ActionResourceRoots: ["_chainsaw", "_anotherorder", "_mercenaries", "re_engine_library"],
        PrefabResourceRoots: ["_chainsaw", "_anotherorder", "_mercenaries", "re_engine_library"],
        EquipmentComponentTypes: [],
        // Weapon-resident EFX and effects on actions need action slots / PFB editing: planned, not implemented.
        Features: new(EquipmentRebuild: false, ActionSlots: false, PfbEditing: false));

    public static IReadOnlyList<GameProfile> All { get; } = [MHWilds, RE4];

    public static GameProfile FromId(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown game '{id}'. Supported: {string.Join(", ", All.Select(p => p.Id))}.", nameof(id));

    /// <summary>Identifies the game installed in <paramref name="gameRoot"/> by its executable, or null.</summary>
    public static GameProfile? DetectFromGameRoot(string? gameRoot)
    {
        if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot)) return null;
        var matches = All.Where(p => File.Exists(Path.Combine(gameRoot, p.ExecutableName))).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Explicit <paramref name="id"/> wins; otherwise the game root's executable decides; otherwise MHWilds,
    /// the only game this editor supported before profiles existed (keeps old launch commands working).
    /// </summary>
    public static GameProfile Resolve(string? id, string? gameRoot) =>
        !string.IsNullOrWhiteSpace(id) ? FromId(id) : DetectFromGameRoot(gameRoot) ?? MHWilds;
}
