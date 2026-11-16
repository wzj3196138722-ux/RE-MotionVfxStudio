// Re4AttributeCatalog: counts which attribute types the RE4 corpus puts on which owners, for the preview's
// attribute catalogue (VfxStudio.Re4Render/Data/re4-attribute-usage.json).
// Usage: dotnet run -c Release --project tools/Re4AttributeCatalog [-- <natives/stm folder> [<output.json>]]
// Files are opened read-only; nothing in the game folder is written.
using System.Text.Json;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
string exe = Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
string root = args.Length > 0 ? args[0] : Path.Combine(Path.GetDirectoryName(exe)!, "RE4_EXTRACT", "natives", "stm");
string output = args.Length > 1 ? args[1] : Path.Combine(FindRepository(), "VfxStudio.Re4Render", "Data", "re4-attribute-usage.json");

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
    foreach (var attribute in owner.Attributes)
        if (attribute is EFXAttributePlayEmitter { efxrData: { } inner }) Walk(inner, embedded: true);
}

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
