// Re4AttributeCatalog: counts which attribute types the RE4 corpus puts on which owners, for the preview's
// attribute catalogue (VfxStudio.Re4Render/Data/re4-attribute-usage.json), and picks initial field values
// for new attributes (re4-attribute-defaults.json): per type, the corpus instance that agrees with the most
// common value of the most fields, so the values come from one real attribute and stay consistent.
// Usage: dotnet run -c Release --project tools/Re4AttributeCatalog [-- <natives/stm folder> [<output folder>]]
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
