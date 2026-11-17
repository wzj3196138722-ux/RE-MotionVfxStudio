using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ReeLib.Efx;

namespace VfxStudio.LiveEditor;

/// <summary>One group of bits inside a flag field. Width 1 without options is an independent switch; a group with
/// options is exclusive. Locked bits change the file structure and are edited through the exact value only.</summary>
public sealed record FieldBitGroup(int Shift, int Width, string Label, string[]? Options = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Locked = false);
public sealed record FieldBits(string Summary, FieldBitGroup[] Groups);
/// <summary>What a field means, read from re4.exe. Status names the part that is not settled.</summary>
public sealed record FieldSemantic(string? Help, string? Unit, string? Status, FieldBits? Bits);

/// <summary>RE4 field semantics from FieldSemantics.re4.json, keyed by attribute type name and field path.
/// A key also covers the paths below it (AppearFrame covers AppearFrame.min). Only RE4 files get them:
/// other games may use the same names differently.</summary>
public static class FieldSemantics
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, FieldSemantic>>> Table = new(Load);

    public static FieldSemantic? For(string? attributeType, string path, EfxVersion version)
    {
        if (version != EfxVersion.RE4 || attributeType == null || !Table.Value.TryGetValue(attributeType, out var fields)) return null;
        for (string key = path; key.Length > 0; key = Parent(key))
            if (fields.TryGetValue(key, out var semantic)) return semantic;
        return null;
    }

    /// <summary>Attribute type names that have entries (for tests and listings).</summary>
    public static IReadOnlyCollection<string> AttributeTypes => Table.Value.Keys;
    public static IReadOnlyDictionary<string, FieldSemantic> Fields(string attributeType) =>
        Table.Value.TryGetValue(attributeType, out var fields) ? fields : new Dictionary<string, FieldSemantic>();

    private static string Parent(string path)
    {
        int cut = Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['));
        return cut > 0 ? path[..cut] : "";
    }

    private static Dictionary<string, Dictionary<string, FieldSemantic>> Load()
    {
        using var stream = typeof(FieldSemantics).Assembly.GetManifestResourceStream("VfxStudio.LiveEditor.FieldSemantics.re4.json")
            ?? throw new InvalidOperationException("FieldSemantics.re4.json is not embedded.");
        var root = JsonNode.Parse(stream)!.AsObject();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var shared = (root["bitSets"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value!.Deserialize<FieldBits>(options)!);
        var table = new Dictionary<string, Dictionary<string, FieldSemantic>>(StringComparer.Ordinal);
        foreach (var (type, node) in root["attributes"]!.AsObject())
        {
            var fields = new Dictionary<string, FieldSemantic>(StringComparer.Ordinal);
            foreach (var (path, value) in node!.AsObject())
            {
                var item = value!.AsObject();
                FieldBits? bits = item["bits"] switch
                {
                    JsonValue name => shared[name.GetValue<string>()],
                    JsonObject inline => inline.Deserialize<FieldBits>(options),
                    _ => null
                };
                fields[path] = new((string?)item["help"], (string?)item["unit"], (string?)item["status"], bits);
            }
            table[type] = fields;
        }
        return table;
    }
}
