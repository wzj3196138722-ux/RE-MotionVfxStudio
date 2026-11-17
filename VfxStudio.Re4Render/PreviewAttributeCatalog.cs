using System.Text.Json;
using ReeLib.Efx;

namespace VfxStudio.Re4Render;

/// <summary>Whether an attribute type can be added to an entry, as the catalogue sees it.</summary>
public enum PreviewCatalogStatus
{
    /// <summary>RE4 files put this type on entries with the same Type attribute.</summary>
    Common,
    /// <summary>Allowed by the backend, but no RE4 entry with this Type has it (other owners do).</summary>
    Unseen,
    /// <summary>Allowed by the backend, but no RE4 file uses this type anywhere.</summary>
    Unused,
    /// <summary>The entry already has this type; the backend refuses duplicates.</summary>
    Present,
    /// <summary>A Type* attribute while the entry already has one; the backend allows only one.</summary>
    SecondType,
    /// <summary>An Expression / Clip whose base attribute the entry lacks; the backend refuses it.</summary>
    MissingBase,
    /// <summary>PlayEmitter / PlayEfx: Action attributes, refused on entries.</summary>
    ActionOnly,
    /// <summary>A Root attribute on an ordinary entry: RE4 puts it only on the Root entry. The backend allows it.</summary>
    RootOnly,
    /// <summary>An ordinary attribute on the Root entry, which in RE4 holds only Root attributes. The backend allows it.</summary>
    NotOnRoot,
    /// <summary>The editor library has no class for this type, so the backend cannot create it.</summary>
    NotCreatable,
}

/// <param name="Base">For Expression, Clip and DelayFrame/Modifier variants: the attribute they extend.</param>
/// <param name="Uses">Entries with the same Type that have this attribute (top-level and embedded).</param>
/// <param name="OwnerCount">Entries with the same Type (the denominator of <paramref name="Uses"/>).</param>
/// <param name="TotalUses">Owners of any kind in the corpus that have this attribute.</param>
/// <param name="Usual">At least 95 % of entries with this Type have it.</param>
public sealed record PreviewCatalogItem(string Type, string Category, string? Base, PreviewCatalogStatus Status,
    int Uses, int OwnerCount, int TotalUses, bool Usual)
{
    /// <summary>The backend accepts it (it may still be unusual for this Type).</summary>
    public bool CanAdd => Status is PreviewCatalogStatus.Common or PreviewCatalogStatus.Unseen or PreviewCatalogStatus.Unused
        or PreviewCatalogStatus.RootOnly or PreviewCatalogStatus.NotOnRoot;
    public float Share => OwnerCount == 0 ? 0 : Uses / (float)OwnerCount;

    public string Reason => Status switch
    {
        PreviewCatalogStatus.Common => $"RE4 里同 Type 的 entry 有 {Uses} / {OwnerCount} 个带这个属性（{Share:P0}）。",
        PreviewCatalogStatus.Unseen => $"RE4 里没有同 Type 的 entry 带这个属性（其他 entry 共 {TotalUses} 处使用）。可以添加，但游戏是否支持没有依据。",
        PreviewCatalogStatus.Unused => "RE4 的文件里从未使用这个属性。可以添加，但游戏是否支持没有依据。",
        PreviewCatalogStatus.Present => "这个 entry 已经有这个属性，同类属性只能有一个。",
        PreviewCatalogStatus.SecondType => "这个 entry 已经有 Type 属性，一个 entry 只能有一个。",
        PreviewCatalogStatus.MissingBase => $"需要先有基础属性 {Base}。",
        PreviewCatalogStatus.ActionOnly => "这是 Action 的属性，不能加到 entry 上。",
        PreviewCatalogStatus.RootOnly => "RE4 里这个属性只出现在 Root entry 上（文件第一个、没有 Type 的 entry），用于全局控制。可以添加，但普通 entry 带它没有依据。",
        PreviewCatalogStatus.NotOnRoot => "Root entry 在 RE4 里只放全局控制属性（UnitCulling、EmitterPriority、DrawOverlay、RenderTarget）。可以添加，但没有依据。",
        PreviewCatalogStatus.NotCreatable => "编辑器还不认识这个属性的字段结构，不能新建。",
        _ => "",
    };
}

/// <summary>The attribute types an RE4 entry can take, grouped into categories, with what the RE4 corpus puts
/// on entries of each Type (counts from tools/Re4AttributeCatalog). Validity follows the backend's own rules
/// (a class exists, one Type, no duplicates, Expression/Clip classes need their base, PlayEmitter/PlayEfx only
/// on Actions); the corpus counts only rank and flag the choices.</summary>
public sealed class PreviewAttributeCatalog
{
    public static readonly string[] Categories = ["Root", "类型", "发射", "变换与运动", "生命与动画", "外观", "UV", "淡化与遮挡", "粒子行为与碰撞", "Action", "其他"];

    /// <summary>Attributes RE4 puts only on the Root entry: the first entry of a file, named Root, with no Type
    /// (1422 files). It holds global controls; ordinary entries never have these, and Root never has anything else.</summary>
    public static readonly string[] RootAttributes = ["UnitCulling", "EmitterPriority", "DrawOverlay", "RenderTarget"];

    public static bool IsRootAttribute(string type) => RootAttributes.Contains(BaseOf(type) ?? type, StringComparer.Ordinal);

    /// <summary>A Root entry: no Type, and named Root or already holding a Root attribute.</summary>
    public static bool IsRoot(string? entryType, string? entryName, IEnumerable<string> existing) =>
        entryType == null && (entryName == "Root" || existing.Any(IsRootAttribute));

    private readonly Dictionary<string, (int Count, Dictionary<string, int> Attributes)> owners;
    private readonly Dictionary<string, int> totals = new(StringComparer.Ordinal);
    private readonly string[] types;
    private readonly HashSet<string> creatable, typeAttributes;
    private readonly Dictionary<string, string[]> requiredBases = new(StringComparer.Ordinal);
    public int Files { get; }

    private PreviewAttributeCatalog(Dictionary<string, (int, Dictionary<string, int>)> owners, int files)
    {
        this.owners = owners; Files = files;
        foreach (var (_, (_, attributes)) in owners)
            foreach (var (type, n) in attributes) totals[type] = totals.GetValueOrDefault(type) + n;
        types = EfxAttributeTypeRemapper.GetAllTypes(EfxVersion.RE4).Values.Select(t => t.ToString()).Distinct()
            .Where(t => !t.StartsWith("Unknown", StringComparison.Ordinal) || totals.ContainsKey(t)).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        creatable = new(StringComparer.Ordinal); typeAttributes = new(StringComparer.Ordinal);
        // The backend's rules depend on the attribute class (its interfaces), not only the name: build an instance
        // of each type and apply the same tests as EFXEntryBase.AddAttribute.
        foreach (var type in types)
        {
            var instance = EfxAttributeTypeRemapper.Create(Enum.Parse<EfxAttributeType>(type), EfxVersion.RE4);
            if (instance == null) continue;
            creatable.Add(type);
            instance.type = Enum.Parse<EfxAttributeType>(type);
            if (instance.IsTypeAttribute) typeAttributes.Add(type);
            var bases = new List<string>();
            void Need(string name) { if (name != type && Enum.TryParse<EfxAttributeType>(name, false, out _)) bases.Add(name); }
            if (instance is IExpressionAttribute) Need(type.Replace("Expression", ""));
            if (instance is IMaterialExpressionAttribute) Need(type.Replace("MaterialExpression", ""));
            if (instance is IClipAttribute) Need(type.Replace("MaterialClip", "").Replace("Clip", ""));
            if (bases.Count > 0) requiredBases[type] = bases.Distinct().ToArray();
        }
    }

    private static PreviewAttributeCatalog? shared;
    /// <summary>The catalogue built from the bundled RE4 usage counts.</summary>
    public static PreviewAttributeCatalog Re4 => shared ??= Load();

    private static PreviewAttributeCatalog Load()
    {
        using var stream = typeof(PreviewAttributeCatalog).Assembly.GetManifestResourceStream("VfxStudio.Re4Render.re4-attribute-usage.json")
            ?? throw new InvalidDataException("缺少属性目录数据 re4-attribute-usage.json。");
        return Parse(stream);
    }

    public static PreviewAttributeCatalog Parse(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        var owners = new Dictionary<string, (int, Dictionary<string, int>)>(StringComparer.Ordinal);
        foreach (var owner in document.RootElement.GetProperty("owners").EnumerateObject())
            owners[owner.Name] = (owner.Value.GetProperty("count").GetInt32(),
                owner.Value.GetProperty("attributes").EnumerateObject().ToDictionary(a => a.Name, a => a.Value.GetInt32(), StringComparer.Ordinal));
        return new(owners, document.RootElement.GetProperty("files").GetInt32());
    }

    private Dictionary<string, KeyValuePair<string, string>[]>? defaults;

    /// <summary>Initial field values for a new attribute of this type (set_fields paths and text), taken from one
    /// RE4 attribute of that type (tools/Re4AttributeCatalog); empty when none are known.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Defaults(string type)
    {
        if (defaults == null)
        {
            using var stream = typeof(PreviewAttributeCatalog).Assembly.GetManifestResourceStream("VfxStudio.Re4Render.re4-attribute-defaults.json");
            var loaded = new Dictionary<string, KeyValuePair<string, string>[]>(StringComparer.Ordinal);
            if (stream != null)
            {
                using var document = JsonDocument.Parse(stream);
                foreach (var t in document.RootElement.GetProperty("types").EnumerateObject())
                    loaded[t.Name] = t.Value.GetProperty("fields").EnumerateObject().Select(f => KeyValuePair.Create(f.Name, f.Value.GetString() ?? "")).ToArray();
            }
            defaults = loaded;
        }
        return defaults.GetValueOrDefault(type) ?? [];
    }

    private PreviewBehaviorTemplates? behaviorTemplates;

    /// <summary>PtBehavior script classes with their usual parameters and carrier entry, and the decal prefabs RE4 uses
    /// (tools/Re4AttributeCatalog, re4-ptbehavior-templates.json).</summary>
    public PreviewBehaviorTemplates Behaviors => behaviorTemplates ??= PreviewBehaviorTemplates.Load();

    /// <summary>Attributes of the entry that need <paramref name="type"/> as their base (the backend would have
    /// refused them without it), so removing it would leave them orphaned.</summary>
    public IReadOnlyList<string> Dependents(string type, IEnumerable<string> existing) =>
        existing.Where(e => requiredBases.TryGetValue(e, out var bases) && bases.Contains(type)).ToArray();

    /// <summary>Attributes for a new entry of a Type: the Type and what at least 95 % of RE4 entries with it have,
    /// in the order the backend keeps them.</summary>
    public IReadOnlyList<string> Template(string entryType) =>
        new[] { entryType }.Concat(For(entryType, []).Where(i => i.Usual && i.CanAdd).Select(i => i.Type)).Distinct().ToArray();

    /// <summary>Every attribute type of the RE4 version (unknown placeholders only when the corpus uses them).</summary>
    public IReadOnlyList<string> Types => types;

    /// <summary>Entries in the corpus with this Type attribute (null or "" for entries without one), top-level and embedded.</summary>
    public int OwnerCount(string? entryType) => Owner(entryType).Count;

    /// <summary>The catalogue for one entry: every type with its status, in category order, most used first.</summary>
    /// <param name="entryType">The entry's Type* attribute, or null when it has none.</param>
    /// <param name="existing">Attribute types the entry already has.</param>
    /// <param name="entryName">The entry's name; with no Type, "Root" marks the Root entry (see <see cref="IsRoot"/>).</param>
    public IReadOnlyList<PreviewCatalogItem> For(string? entryType, IEnumerable<string> existing, string? entryName = null)
    {
        var have = existing.ToHashSet(StringComparer.Ordinal);
        bool root = IsRoot(entryType, entryName, have);
        var (count, uses) = Owner(entryType);
        int usualAt = Math.Max(1, (int)Math.Ceiling(count * .95));
        var all = types.Select(type =>
            {
                int n = uses.GetValueOrDefault(type);
                string? required = requiredBases.GetValueOrDefault(type)?.FirstOrDefault(b => !have.Contains(b));
                bool isType = typeAttributes.Contains(type);
                var status = have.Contains(type) ? PreviewCatalogStatus.Present
                    : !creatable.Contains(type) ? PreviewCatalogStatus.NotCreatable
                    : type is "PlayEmitter" or "PlayEfx" ? PreviewCatalogStatus.ActionOnly
                    : isType && entryType != null ? PreviewCatalogStatus.SecondType
                    : required != null ? PreviewCatalogStatus.MissingBase
                    : IsRootAttribute(type) != root ? (root ? PreviewCatalogStatus.NotOnRoot : PreviewCatalogStatus.RootOnly)
                    : n > 0 ? PreviewCatalogStatus.Common
                    : totals.ContainsKey(type) ? PreviewCatalogStatus.Unseen : PreviewCatalogStatus.Unused;
                // A Type attribute on an entry without one: rank by how many entries have that Type.
                if (isType && entryType == null && !root && status is not (PreviewCatalogStatus.Present or PreviewCatalogStatus.NotCreatable))
                {
                    n = Owner(type).Count;
                    status = n > 0 ? PreviewCatalogStatus.Common : status;
                }
                return new PreviewCatalogItem(type, CategoryOf(type), required ?? BaseOf(type), status, n, isType && entryType == null ? n : count,
                    totals.GetValueOrDefault(type), count > 0 && n >= usualAt && !isType);
            })
            .ToArray();
        // Variants (Expression, Clip, Modifier, ...) follow their base: groups rank by their most used member, the
        // base leads its group. Sorting variants by their own count put them under unrelated attributes.
        var groupUses = all.GroupBy(GroupOf).ToDictionary(g => g.Key, g => g.Max(i => i.Uses), StringComparer.Ordinal);
        return all.OrderBy(i => Array.IndexOf(Categories, i.Category)).ThenByDescending(i => groupUses[GroupOf(i)])
            .ThenBy(GroupOf, StringComparer.Ordinal).ThenBy(i => GroupOf(i) == i.Type ? 0 : 1)
            .ThenByDescending(i => i.Uses).ThenBy(i => i.Type, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The attribute a catalogue row is listed under: its base when that is an RE4 type, else itself.</summary>
    public string GroupOf(PreviewCatalogItem item) => BaseOf(item.Type) is { } b && types.Contains(b, StringComparer.Ordinal) ? b : item.Type;

    /// <summary>Checks one type for an entry: the catalogue item, or null for a name that is not an RE4 attribute type.</summary>
    public PreviewCatalogItem? Check(string type, string? entryType, IEnumerable<string> existing, string? entryName = null)
        => types.Contains(type, StringComparer.Ordinal) ? For(entryType, existing, entryName).First(i => i.Type == type) : null;

    private (int Count, Dictionary<string, int> Attributes) Owner(string? entryType)
    {
        string type = entryType ?? "";
        var merged = new Dictionary<string, int>(StringComparer.Ordinal);
        int count = 0;
        foreach (var key in new[] { "entry:" + type, "embedded:" + type })
            if (owners.TryGetValue(key, out var o))
            {
                count += o.Count;
                foreach (var (a, n) in o.Attributes) merged[a] = merged.GetValueOrDefault(a) + n;
            }
        return (count, merged);
    }

    /// <summary>By name: Type* without an Expression / Clip / DelayFrame suffix (for finding an entry's Type
    /// among its attribute names; the statuses use the backend's class-based test).</summary>
    public static bool IsTypeAttribute(string type) => type.StartsWith("Type", StringComparison.Ordinal) && Variant(type) == null;

    private static string? Variant(string type) =>
        new[] { "MaterialExpression", "MaterialClip", "Expression", "Clip", "DelayFrame" }.FirstOrDefault(s => type.EndsWith(s, StringComparison.Ordinal) && type.Length > s.Length);

    /// <summary>The attribute a variant extends, for grouping (also DelayFrame and Modifier variants, which the
    /// backend does not enforce).</summary>
    public static string? BaseOf(string type)
    {
        foreach (var suffix in new[] { "MaterialExpression", "MaterialClip", "Expression", "Clip", "DelayFrame", "Modifier" })
            if (type.EndsWith(suffix, StringComparison.Ordinal) && type.Length > suffix.Length)
            {
                string name = type[..^suffix.Length];
                if (name.EndsWith("Modifier", StringComparison.Ordinal) && suffix == "DelayFrame") name = name[..^"Modifier".Length];
                return name;
            }
        return null;
    }

    public static string CategoryOf(string type)
    {
        string name = BaseOf(type) ?? type;
        if (RootAttributes.Contains(name, StringComparer.Ordinal)) return "Root";
        if (IsTypeAttribute(name) || name.StartsWith("Type", StringComparison.Ordinal)) return "类型";
        if (name is "PlayEmitter" or "PlayEfx") return "Action";
        if (name is "Spawn" or "EmitterShape2D" or "EmitterShape3D" or "MeshEmitter" or "ScreenSpaceEmitter"
            or "FixRandomGenerator" or "AssignCSV") return "发射";
        if (name.StartsWith("UV", StringComparison.Ordinal) || name.StartsWith("PtUv", StringComparison.Ordinal)) return "UV";
        if (name.StartsWith("FadeBy", StringComparison.Ordinal) || name is "DepthOcclusion" or "VanishArea3D") return "淡化与遮挡";
        if (name is "Life" or "PtLife" or "ScaleAnim" or "RotateAnim" or "Blink" or "ScaleByDepth") return "生命与动画";
        if (name.StartsWith("Transform", StringComparison.Ordinal) || name.StartsWith("PtTransform", StringComparison.Ordinal) ||
            name.Contains("Velocity", StringComparison.Ordinal) || name is "ParentOptions" or "Attractor" or "Noise" or "VectorFieldParameter"
            or "WindInfluence3D" or "PtVortexelWind" or "PtPathTranslate") return "变换与运动";
        if (name is "ShaderSettings" or "EmitterColor" or "PtColor" or "RgbCommon" or "RgbWater" or "AlphaCorrection" or "TexelChannelOperator"
            or "TextureFilter" or "TextureUnit" or "Distortion" or "ProceduralDistortion" or "StretchBlur" or "LuminanceBleed" or "ContrastHighlighter"
            or "DepthOperator" or "EffectShader" or "FakeDoF" or "VolumetricLighting") return "外观";
        if (name.StartsWith("Pt", StringComparison.Ordinal) || name.Contains("Collider", StringComparison.Ordinal) || name is "FluidSimulator2D" or "TestBehaviorUpdater")
            return "粒子行为与碰撞";
        return "其他";
    }
}

/// <param name="Type">PtBehaviorPropType name.</param>
/// <param name="Value">Text as PtBehaviorVariable.Create reads it.</param>
public sealed record PreviewBehaviorParameter(string Name, string Type, string Value);

/// <param name="Uses">PtBehavior instances of this class in RE4 (top-level and embedded).</param>
/// <param name="Carrier">The usual attribute list of a top-level entry carrying it, PtBehavior included; empty when
/// RE4 has none.</param>
public sealed record PreviewBehaviorClass(string Name, int Uses, IReadOnlyList<PreviewBehaviorParameter> Parameters, IReadOnlyList<string> Carrier)
{
    /// <summary>The class name without its namespace (EffectDecal).</summary>
    public string ShortName => Name[(Name.LastIndexOf('.') + 1)..];

    /// <summary>The backend's add_attribute / add_entry "behavior" object.</summary>
    public System.Text.Json.Nodes.JsonObject Request() => new()
    {
        ["class"] = Name,
        ["parameters"] = new System.Text.Json.Nodes.JsonArray(Parameters.Select(p => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
            { ["name"] = p.Name, ["type"] = p.Type, ["value"] = p.Value }).ToArray()),
    };
}

/// <summary>PtBehavior templates taken from RE4: per script class the most common parameter list with the values of one
/// real instance, and the decal prefab paths EffectDecal uses.</summary>
public sealed class PreviewBehaviorTemplates
{
    public const string Decal = "via.effect.script.EffectDecal";
    /// <summary>Most used first.</summary>
    public IReadOnlyList<PreviewBehaviorClass> Classes { get; }
    /// <summary>DecalPrefab paths with how many RE4 decals use them, most used first.</summary>
    public IReadOnlyList<(string Path, int Uses)> DecalPrefabs { get; }

    private PreviewBehaviorTemplates(IReadOnlyList<PreviewBehaviorClass> classes, IReadOnlyList<(string, int)> prefabs)
    { Classes = classes; DecalPrefabs = prefabs; }

    public PreviewBehaviorClass? Find(string name) => Classes.FirstOrDefault(c => c.Name == name);

    internal static PreviewBehaviorTemplates Load()
    {
        using var stream = typeof(PreviewBehaviorTemplates).Assembly.GetManifestResourceStream("VfxStudio.Re4Render.re4-ptbehavior-templates.json")
            ?? throw new InvalidDataException("缺少 PtBehavior 模板数据 re4-ptbehavior-templates.json。");
        using var document = JsonDocument.Parse(stream);
        var classes = document.RootElement.GetProperty("classes").EnumerateObject().Select(c => new PreviewBehaviorClass(c.Name,
                c.Value.GetProperty("count").GetInt32(),
                c.Value.GetProperty("parameters").EnumerateArray().Select(p => new PreviewBehaviorParameter(
                    p.GetProperty("name").GetString()!, p.GetProperty("type").GetString()!, p.GetProperty("value").GetString()!)).ToArray(),
                c.Value.GetProperty("carrier").ValueKind == JsonValueKind.Object
                    ? c.Value.GetProperty("carrier").GetProperty("attributes").EnumerateArray().Select(a => a.GetString()!).ToArray() : []))
            .OrderByDescending(c => c.Uses).ToArray();
        var prefabs = document.RootElement.GetProperty("decalPrefabs").EnumerateArray()
            .Select(p => (p.GetProperty("path").GetString()!, p.GetProperty("uses").GetInt32())).ToArray();
        return new(classes, prefabs);
    }
}
