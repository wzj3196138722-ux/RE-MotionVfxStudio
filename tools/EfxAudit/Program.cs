using System.Reflection;
using System.Security.Cryptography;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Enums;
using VfxStudio.Core.Services;

if (args.Length != 2) throw new ArgumentException("Usage: EfxAudit <workspace> <output-dir>");
string workspace = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);

static string Csv(params object?[] values) => string.Join(',', values.Select(value =>
    "\"" + (value?.ToString() ?? "").Replace("\"", "\"\"").Replace('\r', ' ').Replace('\n', ' ') + "\""));

var version = EfxVersion.MHWilds;
var registered = EfxAttributeTypeRemapper.GetAllTypes(version).OrderBy(pair => pair.Key).ToArray();
var rows = new List<string> { Csv("game/version", "TypeId", "type", "member path", "CLR type", "serialization condition", "editor", "save test", "undo test", "preview consumer", "reference", "sample", "status") };
foreach (var (id, kind) in registered)
{
    EFXAttribute? attribute;
    try { attribute = EFXAttribute.Create(version, kind); }
    catch (Exception ex)
    {
        rows.Add(Csv(version, id, kind, "<unmapped>", "", "", "none", "none", "none", "none", "EfxAttributeTypes.cs", "", "UNMAPPED: " + ex.Message));
        continue;
    }
    var seen = new HashSet<Type>();
    Walk(attribute.GetType(), kind.ToString(), 0, seen);

    void Walk(Type type, string path, int depth, HashSet<Type> ancestors)
    {
        if (depth > 12 || !ancestors.Add(type)) return;
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (field.IsStatic) continue;
            var attrs = field.GetCustomAttributesData();
            string condition = string.Join(";", attrs.Where(a => a.AttributeType.Name is "RszVersionAttribute" or "RszVersionExactAttribute" or "RszConditionalAttribute")
                .Select(a => a.ToString()));
            bool ignored = attrs.Any(a => a.AttributeType.Name == "RszIgnoreAttribute");
            if (type == typeof(EFXAttribute) && field.Name is "type" or "UniqueID" or "Version") ignored = true;
            string fieldPath = path + "." + field.Name;
            string status = ignored ? "READONLY_OR_MANUAL_REVIEW" : "AUDIT_PENDING";
            rows.Add(Csv(version, id, kind, fieldPath, field.FieldType.FullName, condition + (ignored ? ";RszIgnore/base" : ""),
                "legacy inspector: properties only", "not tested", "not tested", "not mapped", "REE-Lib format model", "", status));
            Type child = field.FieldType.IsArray ? field.FieldType.GetElementType()! : field.FieldType;
            if (child.IsGenericType && child.GetGenericTypeDefinition() == typeof(List<>)) child = child.GetGenericArguments()[0];
            if (child.Namespace?.StartsWith("ReeLib", StringComparison.Ordinal) == true && !child.IsEnum && child != type && !child.IsPrimitive)
                Walk(child, fieldPath + (field.FieldType != child ? "[]" : ""), depth + 1, ancestors);
        }
        if (type.BaseType is { } parent && parent != typeof(object) && parent != typeof(EFXAttribute)) Walk(parent, path, depth + 1, ancestors);
        ancestors.Remove(type);
    }
}
File.WriteAllLines(Path.Combine(output, "attribute-coverage.csv"), rows);

var corpus = new List<string> { Csv("path", "sha256", "bytes", "parse result", "entries", "actions", "attribute types", "render bodies", "texture formats", "materials", "test entry/frames", "unchanged roundtrip", "attribute field roundtrip") };
var duplicateHashes = new HashSet<string>();
var service = new EfxEditorService();
foreach (string path in Directory.EnumerateFiles(workspace, "*.efx.*", SearchOption.AllDirectories)
    .Where(p => !p.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) && !p.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)))
{
    byte[] bytes = File.ReadAllBytes(path);
    string hash = Convert.ToHexString(SHA256.HashData(bytes));
    if (!duplicateHashes.Add(hash)) continue;
    string result, kinds = "", bodies = "", roundtrip = "not tested", fieldRoundtrip = "not tested";
    int entries = 0, actions = 0;
    try
    {
        var efx = service.Load(bytes, path);
        entries = efx.Entries.Count;
        actions = efx.Actions.Count;
        kinds = string.Join('|', efx.Entries.SelectMany(e => e.Attributes).Concat(efx.Actions.SelectMany(a => a.Attributes)).Select(a => a.type).Distinct().OrderBy(a => a));
        bodies = string.Join('|', efx.Entries.SelectMany(e => e.Attributes).Where(a => a.IsTypeAttribute).Select(a => a.type).Distinct());
        result = "parsed";
        try
        {
            string beforeFields = AttributeFingerprint(efx);
            byte[] saved = service.SaveToBytes(efx);
            var reread = service.Load(saved, path);
            string left = string.Join('|', efx.Entries.SelectMany(e => e.Attributes).Concat(efx.Actions.SelectMany(a => a.Attributes)).Select(a => a.type));
            string right = string.Join('|', reread.Entries.SelectMany(e => e.Attributes).Concat(reread.Actions.SelectMany(a => a.Attributes)).Select(a => a.type));
            roundtrip = reread.Entries.Count == entries && reread.Actions.Count == actions && left == right
                ? (bytes.AsSpan().SequenceEqual(saved) ? "byte-identical" : "structure-only")
                : "structure-mismatch";
            fieldRoundtrip = beforeFields == AttributeFingerprint(reread) ? "match" : "DIFF";
        }
        catch (Exception ex)
        {
            roundtrip = ex.GetType().Name + ": " + ex.Message;
            if (ex is OverflowException) Console.Error.WriteLine($"ROUNDTRIP FAIL {path}: {ex}");
        }
    }
    catch (Exception ex) { result = ex.GetType().Name + ": " + ex.Message; }
    corpus.Add(Csv(Path.GetRelativePath(workspace, path), hash, bytes.Length, result, entries, actions, kinds, bodies, "not traced", "not traced", "not selected", roundtrip, fieldRoundtrip));
}
foreach (string path in Directory.EnumerateFiles(workspace, "*.*", SearchOption.AllDirectories)
    .Where(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"\.(uvs|tex|mesh|mdf2)\.\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
        !p.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) && !p.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)))
{
    using var stream = File.OpenRead(path);
    string hash = Convert.ToHexString(SHA256.HashData(stream));
    if (!duplicateHashes.Add(hash)) continue;
    corpus.Add(Csv(Path.GetRelativePath(workspace, path), hash, stream.Length, "dependency candidate; EFX link not traced",
        "", "", "", "", "format not decoded", "not traced", "not selected", "not applicable", "not applicable"));
}
File.WriteAllLines(Path.Combine(output, "asset-corpus.csv"), corpus);
string? smokeSource = Directory.EnumerateFiles(workspace, "*.efx.5571972", SearchOption.AllDirectories).FirstOrDefault();
if (smokeSource != null)
{
    string smokeTarget = Path.Combine(output, $"save-smoke-{Guid.NewGuid():N}.efx.5571972");
    try
    {
        var efx = service.Load(smokeSource);
        service.Save(efx, smokeTarget);
        var reopened = service.Load(smokeTarget);
        if (reopened.Entries.Count != efx.Entries.Count || reopened.Actions.Count != efx.Actions.Count)
            throw new InvalidDataException("Atomic save smoke test changed structure counts.");
        Console.WriteLine("Atomic EFX Save/reopen smoke test passed; original sample untouched.");
    }
    finally { if (File.Exists(smokeTarget)) File.Delete(smokeTarget); }
}
Console.WriteLine($"Registered types: {registered.Length}; descriptor rows: {rows.Count - 1}; unique EFX files: {corpus.Count(r => r.Contains("\"parsed\""))}; unique corpus assets: {corpus.Count - 1}");

static string AttributeFingerprint(EfxFile file)
{
    var parts = new List<string>();
    foreach (var attribute in file.Entries.SelectMany(e => e.Attributes).Concat(file.Actions.SelectMany(a => a.Attributes)))
    {
        parts.Add(attribute.type.ToString());
        Visit(attribute, attribute.GetType().Name, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }
    return string.Join('|', parts);

    void Visit(object? value, string path, int depth, HashSet<object> ancestors)
    {
        if (value == null) { parts.Add(path + "=<null>"); return; }
        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string or decimal)
        {
            parts.Add(path + "=" + (value is float f ? BitConverter.SingleToInt32Bits(f).ToString("X8") :
                value is double d ? BitConverter.DoubleToInt64Bits(d).ToString("X16") : value.ToString()));
            return;
        }
        if (depth > 24) { parts.Add(path + "=<depth>"); return; }
        if (value is System.Collections.IEnumerable sequence)
        {
            int i = 0;
            foreach (object? item in sequence) Visit(item, path + "[" + i++ + "]", depth + 1, ancestors);
            parts.Add(path + ".Count=" + i);
            return;
        }
        if (!type.IsValueType && !ancestors.Add(value)) { parts.Add(path + "=<cycle>"); return; }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.IsStatic || field.DeclaringType == typeof(EFXAttribute) ||
                field.GetCustomAttributesData().Any(a => a.AttributeType.Name == "RszIgnoreAttribute")) continue;
            Visit(field.GetValue(value), path + "." + field.Name, depth + 1, ancestors);
        }
        if (!type.IsValueType) ancestors.Remove(value);
    }
}
