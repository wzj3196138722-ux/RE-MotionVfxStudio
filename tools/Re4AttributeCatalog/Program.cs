// Re4AttributeCatalog: counts which attribute types the RE4 corpus puts on which owners, for the preview's
// attribute catalogue (VfxStudio.Re4Render/Data/re4-attribute-usage.json), and picks initial field values
// for new attributes (re4-attribute-defaults.json): per type, the corpus instance that agrees with the most
// common value of the most fields, so the values come from one real attribute and stay consistent.
// Usage: dotnet run -c Release --project tools/Re4AttributeCatalog [-- <natives/stm folder> [<output folder>]]
// Also writes re4-ptbehavior-templates.json: per PtBehavior script class, the usual parameter list with values from
// one real instance, the usual carrier entry, and the DecalPrefab paths EffectDecal uses.
// Files are opened read-only; nothing in the game folder is written.
using System.Text.Json;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using VfxStudio.LiveEditor;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
string exe = Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
string root = args.Length > 0 ? args[0] : Path.Combine(Path.GetDirectoryName(exe)!, "RE4_EXTRACT", "natives", "stm");
string outputFolder = args.Length > 1 ? args[1] : Path.Combine(FindRepository(), "VfxStudio.Re4Render", "Data");
string output = Path.Combine(outputFolder, "re4-attribute-usage.json");
const int SamplesPerType = 1500;
// Paths a freshly created attribute of each type exposes as editable: the only ones a default can set.
var freshPaths = new Dictionary<EfxAttributeType, HashSet<string>?>();
var samples = new Dictionary<EfxAttributeType, List<Dictionary<string, string>>>();
// PtBehavior: per script class, (parameter signature, values) of every instance, and the carrier entries' attribute lists.
var behaviors = new Dictionary<string, List<(string Signature, ReeLib.Efx.Structs.Pt.PtBehaviorVariable[] Parameters)>>(StringComparer.Ordinal);
var carriers = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
var decalPrefabs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

// Owner keys: "entry:<Type>" for top-level entries ("entry:" alone when the entry has no Type attribute),
// "embedded:<Type>" for entries inside a PlayEmitter, "action" and "embedded-action".
var owners = new SortedDictionary<string, (int Count, SortedDictionary<string, int> Attributes)>(StringComparer.Ordinal);
int files = 0, failed = 0;
foreach (var path in Directory.EnumerateFiles(root, "*.efx.3539837", SearchOption.AllDirectories))
{
    try
    {
        using var stream = File.OpenRead(path);
        var file = new EfxFile(new FileHandler(stream, path) { FileVersion = (int)EfxVersion.RE4 });
        if (!file.Read()) { failed++; continue; }
        files++;
        Walk(file, embedded: false);
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or IndexOutOfRangeException or InvalidOperationException)
    {
        failed++;
    }
}

void Walk(EfxFile file, bool embedded)
{
    foreach (var entry in file.Entries) Visit(entry, (embedded ? "embedded:" : "entry:") + (entry.TypeAttribute?.type.ToString() ?? ""));
    foreach (var action in file.Actions) Visit(action, embedded ? "embedded-action" : "action");
}

void Visit(EFXEntryBase owner, string key)
{
    if (!owners.TryGetValue(key, out var o)) o = (0, new(StringComparer.Ordinal));
    o.Count++;
    foreach (var type in owner.Attributes.Select(a => a.type.ToString()).Distinct())
        o.Attributes[type] = o.Attributes.GetValueOrDefault(type) + 1;
    owners[key] = o;
    foreach (var attribute in owner.Attributes) Sample(attribute);
    foreach (var behavior in owner.Attributes.OfType<ReeLib.Efx.Structs.Pt.EFXAttributePtBehavior>())
    {
        string cls = behavior.behaviorString ?? "";
        if (!behaviors.TryGetValue(cls, out var found)) behaviors[cls] = found = [];
        found.Add((string.Join("|", behavior.properties.Select(p => p.behaviorProperty + ":" + p.dataType)), behavior.properties.ToArray()));
        if (owner is EFXEntry && !key.StartsWith("embedded", StringComparison.Ordinal))
        {
            if (!carriers.TryGetValue(cls, out var lists)) carriers[cls] = lists = new(StringComparer.Ordinal);
            string list = string.Join(",", owner.Attributes.Select(a => a.type.ToString()));
            lists[list] = lists.GetValueOrDefault(list) + 1;
        }
        if (cls == "via.effect.script.EffectDecal")
            foreach (var p in behavior.properties)
                if (p.behaviorProperty == "DecalPrefab" && p.ValueText() is { Length: > 0 } prefab) decalPrefabs[prefab] = decalPrefabs.GetValueOrDefault(prefab) + 1;
    }
    foreach (var attribute in owner.Attributes)
        if (attribute is EFXAttributePlayEmitter { efxrData: { } inner }) Walk(inner, embedded: true);
}

void Sample(EFXAttribute attribute)
{
    if (!freshPaths.TryGetValue(attribute.type, out var paths))
    {
        var fresh = EfxAttributeTypeRemapper.Create(attribute.type, EfxVersion.RE4);
        if (fresh != null) fresh.type = attribute.type;
        freshPaths[attribute.type] = paths = fresh == null ? null
            : EfxFieldValues.Editable(fresh, EfxVersion.RE4).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
    }
    if (paths == null || paths.Count == 0) return;
    if (!samples.TryGetValue(attribute.type, out var list)) samples[attribute.type] = list = [];
    if (list.Count >= SamplesPerType) return;
    try
    {
        list.Add(EfxFieldValues.Editable(attribute, EfxVersion.RE4).Where(v => paths.Contains(v.Key))
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal));
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException) { }
}

var defaults = new SortedDictionary<string, object>(StringComparer.Ordinal);
int unapplied = 0;
foreach (var (type, list) in samples)
{
    if (list.Count == 0) continue;
    // Most common value of each path, then the sample that matches the most of them.
    var modes = list.SelectMany(d => d).GroupBy(v => v.Key)
        .ToDictionary(g => g.Key, g => g.GroupBy(v => v.Value).OrderByDescending(v => v.Count()).First().Key, StringComparer.Ordinal);
    var best = list.OrderByDescending(d => d.Count(v => modes.GetValueOrDefault(v.Key) == v.Value)).First();
    // Keep only values that change a fresh attribute and apply cleanly.
    var fresh = EfxAttributeTypeRemapper.Create(type, EfxVersion.RE4)!; fresh.type = type;
    var initial = EfxFieldValues.Editable(fresh, EfxVersion.RE4).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
    var values = best.Where(v => initial.GetValueOrDefault(v.Key) != v.Value).ToList();
    try
    {
        var check = EfxAttributeTypeRemapper.Create(type, EfxVersion.RE4)!; check.type = type;
        var (_, skipped) = EfxFieldValues.Apply(check, EfxVersion.RE4, values);
        if (skipped.Length > 0) { values.RemoveAll(v => skipped.Contains(v.Key)); unapplied += skipped.Length; }
    }
    catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or OverflowException)
    {
        Console.WriteLine($"{type}: defaults not applicable ({ex.Message})"); continue;
    }
    if (values.Count > 0)
        defaults[type.ToString()] = new { samples = list.Count, fields = values.ToDictionary(v => v.Key, v => v.Value) };
}
File.WriteAllText(Path.Combine(outputFolder, "re4-attribute-defaults.json"), JsonSerializer.Serialize(new
{
    source = "RE4 natives/stm *.efx.3539837: per type the sample (up to " + SamplesPerType + ") agreeing with the most per-field modes",
    types = defaults,
}, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
Console.WriteLine($"defaults for {defaults.Count} types ({unapplied} values dropped as not applicable)");

// PtBehavior templates: the most common parameter list of each class; its values from the instance agreeing with
// the most per-parameter modes (as for attribute defaults).
var templates = new SortedDictionary<string, object>(StringComparer.Ordinal);
foreach (var (cls, found) in behaviors)
{
    var signature = found.GroupBy(f => f.Signature).OrderByDescending(g => g.Count()).First();
    var instances = signature.Select(f => f.Parameters).ToArray();
    var modes = Enumerable.Range(0, instances[0].Length)
        .Select(i => instances.GroupBy(x => x[i].ValueText()).OrderByDescending(g => g.Count()).First().Key).ToArray();
    var best = instances.OrderByDescending(x => x.Select((p, i) => p.ValueText() == modes[i] ? 1 : 0).Sum()).First();
    var carrier = carriers.GetValueOrDefault(cls)?.OrderByDescending(c => c.Value).First();
    templates[cls] = new
    {
        count = found.Count,
        signatureShare = Math.Round(signature.Count() / (double)found.Count, 3),
        parameters = best.Select(p => new { name = p.behaviorProperty, type = p.dataType.ToString(), value = p.ValueText() }),
        carrier = carrier == null ? null : new { attributes = carrier.Value.Key.Split(','), entries = carrier.Value.Value },
    };
}
File.WriteAllText(Path.Combine(outputFolder, "re4-ptbehavior-templates.json"), JsonSerializer.Serialize(new
{
    source = "RE4 natives/stm *.efx.3539837: per PtBehavior class the most common parameter list, values from the instance agreeing with the most per-parameter modes; carrier = most common attribute list of top-level entries with that class",
    classes = templates,
    decalPrefabs = decalPrefabs.OrderByDescending(d => d.Value).ThenBy(d => d.Key, StringComparer.OrdinalIgnoreCase).Select(d => new { path = d.Key, uses = d.Value }),
}, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
Console.WriteLine($"PtBehavior templates for {templates.Count} classes, {decalPrefabs.Count} decal prefabs");

var document = new
{
    source = "RE4 natives/stm *.efx.3539837",
    files,
    failed,
    owners = owners.ToDictionary(o => o.Key, o => new { count = o.Value.Count, attributes = o.Value.Attributes }),
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"{files} files, {failed} unreadable, {owners.Count} owner kinds -> {output}");
if (files == 0) Environment.ExitCode = 1;

static string FindRepository()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "RE-MotionVfxStudio.sln"))) return dir.FullName;
    throw new DirectoryNotFoundException("Repository root not found.");
}
