using System.Text.Json.Nodes;

namespace VfxStudio.Re4Render;

/// <summary>One step into an embedded EFX: the PlayEmitter at <paramref name="AttributeIndex"/> of entry or Action
/// <paramref name="Index"/>.</summary>
public sealed record PreviewEditHop(string Kind, int Index, int AttributeIndex);

/// <summary>The entry or Action the property panel edits: <paramref name="Kind"/> "entry" or "action" at
/// <paramref name="Index"/> of the file that <paramref name="Path"/> leads to (the open file when empty). This is the
/// backend's target with its embeddedPath. It is kept apart from the preview manifest, whose entry is always a
/// top-level one: editing an embedded entry leaves the preview on its entry and republishes the whole file.</summary>
public sealed record PreviewEditTarget(string Kind, int Index, IReadOnlyList<PreviewEditHop> Path)
{
    public const int MaximumDepth = 16;

    public static PreviewEditTarget Root(int position) => new("entry", position, []);

    /// <summary>An Action of the open file (not inside an embedded one).</summary>
    public bool IsRootAction() => Kind == "action" && Path.Count == 0;

    /// <summary>A top-level entry: the one the preview manifest can point at.</summary>
    public bool IsRoot => Kind == "entry" && Path.Count == 0;

    /// <summary>Stable text form, for comparing targets and keying UI state.</summary>
    public string Key => string.Concat(Path.Select(h => $"{h.Kind[0]}{h.Index}@{h.AttributeIndex}>")) + $"{Kind[0]}{Index}";

    public bool Equals(PreviewEditTarget? other) => other is not null && Key == other.Key;
    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);

    /// <summary>A short name for the panel: "[3]" for a top-level entry, "Action 0 › PlayEmitter 1 › [2]" inside.</summary>
    public string Label => string.Concat(Path.Select(h => (h.Kind == "action" ? $"Action {h.Index}" : $"[{h.Index}]") + $" › 属性 {h.AttributeIndex} › "))
        + (Kind == "action" ? $"Action {Index}" : $"[{Index}]");

    /// <summary>The backend target object (kind, index, embeddedPath, and attributeIndex when given).</summary>
    public JsonObject ToJson(int? attributeIndex = null)
    {
        var target = new JsonObject { ["kind"] = Kind, ["index"] = Index };
        if (attributeIndex is int a) target["attributeIndex"] = a;
        if (Path.Count > 0)
            target["embeddedPath"] = new JsonArray(Path.Select(h => (JsonNode?)new JsonObject
                { ["kind"] = h.Kind, ["index"] = h.Index, ["attributeIndex"] = h.AttributeIndex }).ToArray());
        return target;
    }

    /// <summary>Reads a target from AI arguments: kind (default "entry"), index, and an optional embeddedPath.</summary>
    public static PreviewEditTarget Parse(string kind, int index, JsonNode? embeddedPath)
    {
        if (kind is not ("entry" or "action")) throw new ArgumentException("kind 应为 entry 或 action。");
        if (index < 0) throw new ArgumentException("index 不能为负。");
        var hops = new List<PreviewEditHop>();
        if (embeddedPath != null)
        {
            if (embeddedPath is not JsonArray array) throw new ArgumentException("embeddedPath 必须是数组。");
            if (array.Count > MaximumDepth) throw new ArgumentException($"embeddedPath 最多 {MaximumDepth} 层。");
            foreach (var node in array)
            {
                if (node is not JsonObject hop) throw new ArgumentException("embeddedPath 的每一步需要 kind、index、attributeIndex。");
                string hopKind = hop["kind"]?.GetValue<string>() ?? "action";
                int hopIndex = hop["index"]?.GetValue<int>() ?? throw new ArgumentException("embeddedPath 的每一步需要 index。");
                int attribute = hop["attributeIndex"]?.GetValue<int>() ?? throw new ArgumentException("embeddedPath 的每一步需要 attributeIndex。");
                if (hopKind is not ("entry" or "action") || hopIndex < 0 || attribute < 0)
                    throw new ArgumentException("embeddedPath 的每一步需要 entry/action 与非负序号。");
                hops.Add(new(hopKind, hopIndex, attribute));
            }
        }
        return new(kind, index, hops);
    }

    /// <summary>The target's node (index, name, attributes) in the backend's document tree, or null when the path no
    /// longer leads anywhere (an edit removed or moved it).</summary>
    public JsonObject? Find(JsonNode? document)
    {
        JsonNode? tree = document;
        foreach (var hop in Path)
        {
            var owner = Container(tree, hop.Kind, hop.Index);
            tree = (owner?["attributes"] as JsonArray)?.FirstOrDefault(a => a?["index"]?.GetValue<int>() == hop.AttributeIndex)?["embedded"];
            if (tree == null) return null;
        }
        return Container(tree, Kind, Index);
    }

    private static JsonObject? Container(JsonNode? tree, string kind, int index) =>
        (tree?[kind == "action" ? "actions" : "entries"] as JsonArray)?.FirstOrDefault(e => e?["index"]?.GetValue<int>() == index) as JsonObject;

    public override string ToString() => Key;
}
