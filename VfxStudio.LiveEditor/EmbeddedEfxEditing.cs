using System.Text.Json;
using System.Text.Json.Nodes;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;

namespace VfxStudio.LiveEditor;

internal sealed partial class EditorEngine
{
    private const int MaximumEmbeddedDepth = 16;
    private const int MaximumEmbeddedNodes = 50000;
    private sealed record EmbeddedHop(string Kind, int Index, int AttributeIndex);
    private sealed record EmbeddedGroup(int Index, string Name, int[] Entries);
    private sealed record EmbeddedTree(EmbeddedNode[] Entries, EmbeddedNode[] Actions, EmbeddedGroup[] Groups);
    private sealed record EmbeddedNode(int Index, string Name, Target Target, EmbeddedAttribute[] Attributes);
    private sealed record EmbeddedAttribute(int Index, string Type, string? ClrType, Target Target, EmbeddedTree? Embedded);

    private static EFXEntryBase ResolveContainer(EfxFile file, string kind, int index) => kind switch
    {
        "entry" when index >= 0 && index < file.Entries.Count => file.Entries[index],
        "action" when index >= 0 && index < file.Actions.Count => file.Actions[index],
        _ => throw new ArgumentOutOfRangeException("target", "Target kind or index is invalid.")
    };

    private EfxFile ResolveTargetFile(Target target, EfxFile? file = null)
    {
        file ??= Require();
        var path = target.EmbeddedPath ?? [];
        if (path.Length > MaximumEmbeddedDepth) throw new ArgumentException("Embedded EFX path exceeds 16 levels.");
        var ancestors = new HashSet<EfxFile>(ReferenceEqualityComparer.Instance) { file };
        foreach (var hop in path)
        {
            var owner = ResolveContainer(file, hop.Kind, hop.Index);
            if (hop.AttributeIndex < 0 || hop.AttributeIndex >= owner.Attributes.Count)
                throw new ArgumentOutOfRangeException("embeddedPath", "Embedded EFX attribute index is invalid.");
            if (owner.Attributes[hop.AttributeIndex] is not EFXAttributePlayEmitter { efxrData: { } embedded })
                throw new ArgumentException("Each embeddedPath step must select a PlayEmitter with embedded EFX content.");
            if (!ancestors.Add(embedded)) throw new InvalidDataException("Embedded EFX contains a cyclic file reference.");
            file = embedded;
        }
        return file;
    }

    private static EmbeddedTree DescribeEmbeddedTree(EfxFile file)
    {
        var ancestors = new HashSet<EfxFile>(ReferenceEqualityComparer.Instance);
        int nodes = 0;
        return Walk(file, []);

        EmbeddedTree Walk(EfxFile current, EmbeddedHop[] path)
        {
            if (path.Length > MaximumEmbeddedDepth) throw new InvalidDataException("Embedded EFX tree exceeds 16 levels; no nodes were omitted.");
            if (!ancestors.Add(current)) throw new InvalidDataException("Embedded EFX contains a cyclic file reference.");
            try
            {
                EmbeddedNode Container(EFXEntryBase owner, int index, string kind)
                {
                    if (++nodes > MaximumEmbeddedNodes) throw new InvalidDataException("Embedded EFX tree exceeds 50000 nodes; no nodes were omitted.");
                    var target = new Target(kind, index, null, path);
                    return new(index, owner.name ?? $"{kind} {index}", target, owner.Attributes.Select((attribute, attributeIndex) =>
                    {
                        if (++nodes > MaximumEmbeddedNodes) throw new InvalidDataException("Embedded EFX tree exceeds 50000 nodes; no nodes were omitted.");
                        EmbeddedTree? child = attribute is EFXAttributePlayEmitter { efxrData: { } embedded }
                            ? Walk(embedded, [.. path, new(kind, index, attributeIndex)]) : null;
                        return new EmbeddedAttribute(attributeIndex, attribute.type.ToString(), attribute.GetType().FullName,
                            target with { AttributeIndex = attributeIndex }, child);
                    }).ToArray());
                }
                return new(current.Entries.Select((e, i) => Container(e, i, "entry")).ToArray(),
                    current.Actions.Select((a, i) => Container(a, i, "action")).ToArray(),
                    current.EffectGroups.Select((g, i) => new EmbeddedGroup(i, g.groupName, g.efxEntryIndexes ?? [])).ToArray());
            }
            finally { ancestors.Remove(current); }
        }
    }

    private static IEnumerable<EfxFile> EnumerateEmbeddedFiles(EfxFile file, EFXAttribute? removed = null)
    {
        var ancestors = new HashSet<EfxFile>(ReferenceEqualityComparer.Instance);
        int nodes = 0;
        return Walk(file, 0);

        IEnumerable<EfxFile> Walk(EfxFile current, int depth)
        {
            if (depth > MaximumEmbeddedDepth || ++nodes > MaximumEmbeddedNodes)
                throw new InvalidDataException("Embedded EFX exceeds the supported tree bounds.");
            if (!ancestors.Add(current)) throw new InvalidDataException("Embedded EFX contains a cyclic file reference.");
            try
            {
                yield return current;
                foreach (var emitter in current.Entries.Cast<EFXEntryBase>().Concat(current.Actions).SelectMany(e => e.Attributes).OfType<EFXAttributePlayEmitter>())
                    if (!ReferenceEquals(emitter, removed) && emitter.efxrData != null)
                        foreach (var nested in Walk(emitter.efxrData, depth + 1)) yield return nested;
            }
            finally { ancestors.Remove(current); }
        }
    }

    private static IEnumerable<EFXAttribute> AllEmbeddedAttributes(EfxFile file) => EnumerateEmbeddedFiles(file)
        .SelectMany(f => f.Entries.Cast<EFXEntryBase>().Concat(f.Actions)).SelectMany(e => e.Attributes);

    private static void RejectEmbeddedRootOperation(JsonObject request)
    {
        foreach (var node in new[] { request["embeddedPath"], request["target"]?["embeddedPath"] })
        {
            if (node == null) continue;
            if (node is not JsonArray path) throw new ArgumentException("embeddedPath must be an array.");
            if (path.Count > 0) throw new NotSupportedException("This structural operation supports only top-level EFX entries; an embedded entry cannot be replaced by a root entry with the same index.");
        }
    }

    private sealed record Target(string Kind, int Index, int? AttributeIndex, EmbeddedHop[]? EmbeddedPath = null)
    {
        public static Target From(JsonObject request)
        {
            var target = request["target"]?.AsObject() ?? throw new ArgumentException("target is required.");
            EmbeddedHop[] path = [];
            if (target["embeddedPath"] is JsonNode raw)
            {
                if (raw is not JsonArray array) throw new ArgumentException("embeddedPath must be an array.");
                if (array.Count > MaximumEmbeddedDepth) throw new ArgumentException("Embedded EFX path exceeds 16 levels.");
                path = array.Select(node =>
                {
                    var hop = node as JsonObject ?? throw new ArgumentException("Each embeddedPath step must be an object.");
                    string kind = Text(hop, "kind"); int index = Int(hop, "index"), attribute = Int(hop, "attributeIndex");
                    if (kind is not ("entry" or "action") || index < 0 || attribute < 0)
                        throw new ArgumentException("Embedded steps require entry/action kind and non-negative indices.");
                    return new EmbeddedHop(kind, index, attribute);
                }).ToArray();
            }
            string targetKind = Text(target, "kind");
            if (targetKind == "group" && path.Length > 0) throw new NotSupportedException("Embedded group notes are not supported; the root group will not be modified.");
            return new(targetKind, Int(target, "index"), target["attributeIndex"]?.GetValue<int>(), path);
        }
    }
}
