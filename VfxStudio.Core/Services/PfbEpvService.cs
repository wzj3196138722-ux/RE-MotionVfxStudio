using System.Collections;
using System.Numerics;
using System.Text.Json;
using ReeLib;
using ReeLib.Pfb;
using VfxStudio.Core.Models;

namespace VfxStudio.Core.Services;

public class PfbElementEntry
{
    public string SourcePfbPath { get; set; } = string.Empty;
    public string SourcePfbName { get; set; } = string.Empty;
    public string ContainerGameObjectName { get; set; } = string.Empty;
    public int ContainerId { get; set; } = -1;
    public int ElementId { get; set; } = -1;
    public int? ElementIdFixed { get; set; }
    public int? ActivateId { get; set; }
    public string? StateTriggerName { get; set; }
    public int? StateTriggerId { get; set; }
    public string ProviderClassName { get; set; } = string.Empty;
    public string FullId => ContainerId >= 0 ? $"{ContainerId}-{ElementId}" : ElementId.ToString();
    public int RelationChildId { get; set; } = -1;
    public string JointName { get; set; } = string.Empty;
    public Vector3 Offset { get; set; } = Vector3.Zero;
    public Vector3 Rotation { get; set; } = Vector3.Zero;
    public Vector3 Scale { get; set; } = Vector3.One;
    public string ColorHex { get; set; } = "FFFFFFFF";
    public List<string> ResourceEfxPaths { get; set; } = new();
    public List<string> ResolvedLocalEfxFiles { get; set; } = new();
    public List<string> GroupNames { get; set; } = new();
}

public sealed record WeaponPfbResourceCandidate(
    PfbElementEntry Source,
    string RawResourcePath,
    string? LocalEfxPath);

public sealed record EffectTriggerDefinition(string Name, int FixedId, int TriggerId);

public sealed record PfbSlotResolution(
    PfbElementEntry? Element,
    List<string> LocalEfxPaths,
    string Status,
    IReadOnlyList<PfbElementEntry> Candidates);

/// <summary>
/// Parses .pfb.18 (Effect Provider EPVStandardData / EPVExpertCommonData) files for a weapon
/// and maps motion VfxSlot (ContainerId-ElementId, e.g. "10-0", "0-203", "2-225") to the actual .efx files,
/// target bones, weapon part relations (1020=Primary, 1021=Secondary), and group parameters.
/// </summary>
public class PfbEpvService
{
    private readonly string _assetsDir;
    private RszFileOption? _rszOption;
    private readonly Dictionary<int, EffectTriggerDefinition> _stateTriggersByFixedId;

    public string CurrentWeaponCode { get; private set; } = string.Empty;
    public List<string> LoadedPfbPaths { get; } = new();
    public List<PfbElementEntry> AllElements { get; } = new();
    public List<WeaponPfbResourceCandidate> WeaponResourceCandidates { get; } = new();

    // Keyed by "ContainerId-ElementId" (e.g. "10-0", "0-203") -> list of elements (weapon-specific first)
    private readonly Dictionary<string, List<PfbElementEntry>> _elementsByFullId = new(StringComparer.OrdinalIgnoreCase);

    public PfbEpvService(string assetsDir)
    {
        _assetsDir = assetsDir;
        string mapPath = Path.Combine(assetsDir, "Lists", "effect_id_common_fixed_map.json");
        _stateTriggersByFixedId = File.Exists(mapPath)
            ? (JsonSerializer.Deserialize<List<EffectTriggerDefinition>>(File.ReadAllText(mapPath)) ?? new())
                .ToDictionary(trigger => trigger.FixedId)
            : new();
    }

    private RszFileOption? EnsureRszOption()
    {
        if (_rszOption != null) return _rszOption;

        string compactJson = Path.Combine(_assetsDir, "Lists", "rsz_pfb_classes.json");
        if (!File.Exists(compactJson))
        {
            // Optional explicit schema override for legacy desktop workflows.
            string? alt = Environment.GetEnvironmentVariable("MOTIONVFX_RSZ_SCHEMA");
            if (!string.IsNullOrWhiteSpace(alt) && File.Exists(alt)) compactJson = alt;
        }

        if (File.Exists(compactJson))
        {
            try
            {
                var parser = RszParser.GetInstance(compactJson);
                _rszOption = new RszFileOption(GameName.mhwilds, parser);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PfbEpvService] Failed to init RszParser from {compactJson}: {ex.Message}");
            }
        }
        return _rszOption;
    }

    /// <summary>
    /// Loads all .pfb.18 files for the specified weapon from Assets/WeaponBases/{weaponCode} and Assets/VfxShared.
    /// </summary>
    public void LoadWeaponPfbs(string weaponCode, IEnumerable<string>? availableLocalEfxFiles = null)
    {
        CurrentWeaponCode = weaponCode.ToLowerInvariant();
        LoadedPfbPaths.Clear();
        AllElements.Clear();
        WeaponResourceCandidates.Clear();
        _elementsByFullId.Clear();

        var opt = EnsureRszOption();
        if (opt == null) return;

        var localEfxList = availableLocalEfxFiles?.ToList() ?? new List<string>();
        string wpDir = Path.Combine(_assetsDir, "WeaponBases", CurrentWeaponCode);
        if (!Directory.Exists(wpDir)) return;

        // The asset copy contains both flat and natives/stm layouts. Equal
        // payloads are one provider; distinct payloads remain separate.
        var pfbFiles = Directory.GetFiles(wpDir, "*.pfb.18", SearchOption.AllDirectories)
            .OrderByDescending(f => Path.GetFileName(f).StartsWith("epvr_it", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f)
            .ToList();

        string sharedDir = Path.Combine(_assetsDir, "VfxShared", "natives", "stm", "art", "vfx", "effectprovider");
        if (Directory.Exists(sharedDir))
        {
            string itemDir = Path.Combine(sharedDir, "weapon", "it" + CurrentWeaponCode[2..]);
            if (Directory.Exists(itemDir))
                pfbFiles.AddRange(Directory.GetFiles(itemDir, "*.pfb.18", SearchOption.AllDirectories));
            string commonDir = Path.Combine(sharedDir, "common");
            if (Directory.Exists(commonDir))
                pfbFiles.AddRange(Directory.GetFiles(commonDir, "*.pfb.18", SearchOption.AllDirectories));
        }

        var seenPayloads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pfbPath in pfbFiles)
        {
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pfbPath)));
            if (!seenPayloads.Add(hash)) continue;
            LoadSinglePfb(pfbPath, localEfxList);
        }
        string weaponResourcePrefix = $"/weapon/it{CurrentWeaponCode[2..]}/";
        foreach (var entry in AllElements)
            foreach (string raw in entry.ResourceEfxPaths)
                if (raw.Replace('\\', '/').Contains(weaponResourcePrefix, StringComparison.OrdinalIgnoreCase))
                    WeaponResourceCandidates.Add(new WeaponPfbResourceCandidate(entry, raw,
                        ResolveLocalEfxPath(raw, localEfxList)));
    }

    public void LoadSinglePfb(string pfbPath, List<string>? availableLocalEfxFiles = null)
    {
        if (!File.Exists(pfbPath)) return;
        var opt = EnsureRszOption();
        if (opt == null) return;

        try
        {
            using var fs = File.OpenRead(pfbPath);
            var pfb = new PfbFile(opt, new FileHandler(fs, pfbPath));
            if (!pfb.Read()) return;
            pfb.SetupGameObjects();

            LoadedPfbPaths.Add(pfbPath);
            string pfbFileName = Path.GetFileName(pfbPath);

            void TraverseGameObject(PfbGameObject go)
            {
                string goName = go.Name ?? string.Empty;
                int containerId = ParseLeadingContainerId(goName);

                foreach (var comp in go.Components)
                {
                    string className = comp.RszClass?.name ?? string.Empty;
                    if (!className.Contains("EPV", StringComparison.OrdinalIgnoreCase))
                        continue;

                    for (int f = 0; f < comp.Fields.Length; f++)
                    {
                        if (comp.Fields[f].name == "Elements" && comp.Values[f] is IList elemList)
                        {
                            foreach (var item in elemList)
                            {
                                if (item is not RszInstance elemInst) continue;
                                var entry = ParseElementInstance(elemInst, pfbFileName, goName, containerId, availableLocalEfxFiles);
                                entry.SourcePfbPath = pfbPath;
                                entry.ProviderClassName = className;
                                AllElements.Add(entry);
                                if (!_elementsByFullId.TryGetValue(entry.FullId, out var bucket))
                                    _elementsByFullId[entry.FullId] = bucket = new List<PfbElementEntry>();
                                bucket.Add(entry);
                            }
                        }
                    }
                }

                foreach (var child in go.Children)
                {
                    TraverseGameObject(child);
                }
            }

            foreach (var root in pfb.GameObjects)
            {
                TraverseGameObject(root);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PfbEpvService] Warning reading PFB {pfbPath}: {ex.Message}");
        }
    }

    private PfbElementEntry ParseElementInstance(
        RszInstance elemInst,
        string pfbFileName,
        string goName,
        int containerId,
        List<string>? availableLocalEfxFiles)
    {
        var entry = new PfbElementEntry
        {
            SourcePfbName = pfbFileName,
            ContainerGameObjectName = goName,
            ContainerId = containerId
        };

        for (int ef = 0; ef < elemInst.Fields.Length; ef++)
        {
            string fn = elemInst.Fields[ef].name;
            object? val = elemInst.Values[ef];

            if (fn == "ID")
            {
                if (val is uint u) entry.ElementId = (int)u;
                else if (val is int i) entry.ElementId = i;
            }
            else if (fn == "ElementID_Fixed" && val is int fixedId)
            {
                entry.ElementIdFixed = fixedId;
                if (entry.ElementId < 0) entry.ElementId = fixedId;
                if (_stateTriggersByFixedId.TryGetValue(fixedId, out var trigger))
                {
                    entry.StateTriggerName = trigger.Name;
                    entry.StateTriggerId = trigger.TriggerId;
                }
            }
            else if (fn == "ActivateID")
            {
                if (val is int activateId) entry.ActivateId = activateId;
                else if (val is uint unsignedActivateId) entry.ActivateId = unchecked((int)unsignedActivateId);
            }
            else if (fn == "RelationChildId" && val is int rc)
            {
                entry.RelationChildId = rc;
            }
            else if (fn == "JointName" && val is string jn)
            {
                entry.JointName = jn;
            }
            else if (fn == "Offset" && val is Vector3 off)
            {
                entry.Offset = off;
            }
            else if (fn == "Rotation" && val is Vector3 rot)
            {
                entry.Rotation = rot;
            }
            else if (fn == "Scale" && val is Vector3 scl)
            {
                entry.Scale = scl;
            }
            else if (fn == "Color" && val != null)
            {
                entry.ColorHex = val.ToString() ?? "FFFFFFFF";
            }
            else if (fn == "Resources" && val is IList resList)
            {
                foreach (var r in resList)
                {
                    string rawPath = (r?.ToString() ?? string.Empty).Trim().TrimStart('@', '/');
                    if (string.IsNullOrEmpty(rawPath) || rawPath.Contains("11_dammy.efx", StringComparison.OrdinalIgnoreCase))
                        continue;

                    entry.ResourceEfxPaths.Add(rawPath);
                    string? localPath = ResolveLocalEfxPath(rawPath, availableLocalEfxFiles);
                    if (!string.IsNullOrEmpty(localPath) && !entry.ResolvedLocalEfxFiles.Contains(localPath, StringComparer.OrdinalIgnoreCase))
                    {
                        entry.ResolvedLocalEfxFiles.Add(localPath);
                    }
                }
            }
            else if (fn == "GroupNameParameters" && val is IList grpList)
            {
                foreach (var g in grpList)
                {
                    if (g is RszInstance gi && gi.Values.Length >= 2 && gi.Values[1] is string gName && !string.IsNullOrEmpty(gName))
                    {
                        entry.GroupNames.Add(gName);
                    }
                }
            }
        }

        return entry;
    }

    public string? ResolveLocalEfxPath(string internalEfxPath, IEnumerable<string>? availableLocalEfxFiles = null)
    {
        if (string.IsNullOrWhiteSpace(internalEfxPath)) return null;
        string clean = internalEfxPath.Trim().TrimStart('@', '/').Replace('\\', '/');
        if (clean.StartsWith("natives/stm/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring("natives/stm/".Length);
        }

        string versioned = clean.EndsWith(".5571972", StringComparison.OrdinalIgnoreCase)
            ? clean : clean + ".5571972";
        string fileName = Path.GetFileName(versioned);
        string relative = versioned.Replace('/', Path.DirectorySeparatorChar);
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddIfPresent(string candidate)
        {
            if (File.Exists(candidate)) exact.Add(Path.GetFullPath(candidate));
        }

        if (!string.IsNullOrEmpty(CurrentWeaponCode))
        {
            string weapon = Path.Combine(_assetsDir, "WeaponBases", CurrentWeaponCode);
            AddIfPresent(Path.Combine(weapon, relative));
            AddIfPresent(Path.Combine(weapon, "natives", "stm", relative));
        }
        string shared = Path.Combine(_assetsDir, "VfxShared");
        AddIfPresent(Path.Combine(shared, relative));
        AddIfPresent(Path.Combine(shared, "natives", "stm", relative));
        if (exact.Count == 1) return exact.Single();
        if (exact.Count > 1)
            return ChooseIdenticalCopy(exact);

        // A basename is evidence only when it has one local candidate.
        var byName = (availableLocalEfxFiles ?? Array.Empty<string>())
            .Where(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase) && File.Exists(f))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return byName.Length == 1 ? byName[0] : ChooseIdenticalCopy(byName);
    }

    private static string? ChooseIdenticalCopy(IEnumerable<string> candidates)
    {
        var paths = candidates.ToArray();
        if (paths.Length == 0) return null;
        string? firstHash = null;
        foreach (string path in paths)
        {
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            if (firstHash != null && hash != firstHash) return null;
            firstHash = hash;
        }
        // The flat copy is the project's editable file; the preserved
        // natives/stm and shared copies are only interchangeable when equal.
        return paths.OrderBy(p => p.Contains("\\natives\\stm\\", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(p => p.Length).First();
    }

    /// <summary>
    /// Resolves a motion VfxSlot to its matching PfbElementEntry (if found in PFB) and local .efx file path(s).
    /// </summary>
    public (PfbElementEntry? Element, List<string> LocalEfxPaths) ResolveSlot(
        VfxSlot slot,
        IReadOnlyList<string> weaponEfxFiles)
    {
        var result = ResolveSlotDetailed(slot, weaponEfxFiles);
        return (result.Element, result.LocalEfxPaths);
    }

    public PfbSlotResolution ResolveSlotDetailed(VfxSlot slot, IReadOnlyList<string> weaponEfxFiles)
    {
        int cid = slot.ContainerId;
        int eid = slot.ElementId;
        if (!string.IsNullOrWhiteSpace(slot.EffectId))
        {
            string trimmed = slot.EffectId.Trim();
            if (trimmed.Contains('-'))
            {
                var parts = trimmed.Split('-');
                if (parts.Length != 2 || !int.TryParse(parts[0], out cid) || !int.TryParse(parts[1], out eid))
                    return new(null, new(), "EffectId 格式无法解析", Array.Empty<PfbElementEntry>());
            }
            else if (int.TryParse(trimmed, out int singleEid))
            {
                eid = singleEid;
            }
            else
            {
                // A user-entered exact file path can be resolved, but fuzzy
                // substring guesses are not an action-to-resource reference.
                if (!trimmed.Contains(".efx", StringComparison.OrdinalIgnoreCase))
                    return new(null, new(), "EffectId 不是可解析的容器/元素编号", Array.Empty<PfbElementEntry>());
                string? direct = ResolveLocalEfxPath(trimmed, weaponEfxFiles);
                return direct == null
                    ? new(null, new(), "直接 EFX 路径缺失或同名歧义", Array.Empty<PfbElementEntry>())
                    : new(null, new() { direct }, "手动路径；非 Provider 已确认调用", Array.Empty<PfbElementEntry>());
            }
        }
        string lookupKey = $"{cid}-{eid}";
        if (!_elementsByFullId.TryGetValue(lookupKey, out var matches) || matches.Count == 0)
            return new(null, new(), $"Provider 未找到 {lookupKey}", Array.Empty<PfbElementEntry>());
        if (matches.Count > 1)
            return new(null, new(), $"Provider 歧义：{lookupKey} 有 {matches.Count} 个来源", matches);

        var element = matches[0];
        var paths = new List<string>();
        foreach (string raw in element.ResourceEfxPaths)
        {
            string? resolved = ResolveLocalEfxPath(raw, weaponEfxFiles);
            if (resolved != null && !paths.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                paths.Add(resolved);
        }
        string status = element.ResourceEfxPaths.Count == 0 ? "Provider 元素无直接 EFX 资源"
            : paths.Count == element.ResourceEfxPaths.Count ? "已解析" : "部分资源缺失或路径歧义";
        return new(element, paths, status, matches);
    }

    private static int ParseLeadingContainerId(string goName)
    {
        if (string.IsNullOrEmpty(goName)) return -1;
        int underscore = goName.IndexOf('_');
        if (underscore > 0 && int.TryParse(goName.AsSpan(0, underscore), out int cid))
        {
            return cid;
        }
        return -1;
    }
}
