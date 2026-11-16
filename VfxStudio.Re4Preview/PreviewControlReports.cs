using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

/// <summary>JSON shapes returned over the AI control pipe, and argument readers for it.</summary>
internal static class PreviewControlReports
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static JsonObject Document(PreviewEditorState? state)
    {
        if (state == null) return new JsonObject { ["open"] = false };
        var manifest = state.Manifest;
        return new JsonObject
        {
            ["open"] = manifest != null,
            ["revision"] = state.Revision,
            ["dirty"] = state.Dirty,
            ["canUndo"] = state.CanUndo,
            ["canRedo"] = state.CanRedo,
            ["documentId"] = manifest?.DocumentId,
            ["source"] = manifest?.Source,
            ["entryPosition"] = manifest?.EntryPosition,
            ["entryId"] = manifest?.EntryId,
            ["publishedRevision"] = manifest?.Revision,
            ["savedPath"] = state.SavedPath,
            ["publicationError"] = state.PublicationError,
            ["warning"] = state.Warning,
            ["attributes"] = new JsonArray((state.Attributes ?? []).Select(a => (JsonNode?)new JsonObject { ["index"] = a.Index, ["type"] = a.Type }).ToArray())
        };
    }

    /// <summary>The attribute catalogue for the current entry: what can be added, with RE4 usage counts.</summary>
    public static JsonObject Catalog(PreviewEditorState? state)
    {
        var existing = (state?.Attributes ?? []).Select(a => a.Type).ToArray();
        string? entryType = existing.FirstOrDefault(PreviewAttributeCatalog.IsTypeAttribute);
        var catalog = PreviewAttributeCatalog.Re4;
        return new JsonObject
        {
            ["entryType"] = entryType,
            ["sameTypeEntries"] = catalog.OwnerCount(entryType),
            ["corpusFiles"] = catalog.Files,
            ["items"] = new JsonArray(catalog.For(entryType, existing).Select(i => (JsonNode?)new JsonObject
            {
                ["type"] = i.Type,
                ["category"] = i.Category,
                ["status"] = JsonNamingPolicy.CamelCase.ConvertName(i.Status.ToString()),
                ["canAdd"] = i.CanAdd,
                ["uses"] = i.Uses,
                ["usual"] = i.Usual,
                ["base"] = i.Base,
            }).ToArray()),
        };
    }

    public static JsonObject Particle(ParticleRecord particle, PreviewEntryState entry, uint uvFlags, PreviewTypeKind kind = PreviewTypeKind.Billboard3D)
    {
        var instance = BillboardInstance.Decode(particle, entry, uvFlags, kind);
        return new JsonObject
        {
            ["slot"] = particle.Slot,
            ["id"] = particle.Id,
            ["age"] = Finite(particle.Age),
            ["lifeAlpha"] = particle.LifeAlpha,
            ["position"] = Vector(particle.Position),
            ["width"] = Finite(instance.Size.X),
            ["height"] = Finite(instance.Size.Y),
            ["roll"] = Finite(instance.PositionRoll.W),
            ["uvSequence"] = instance.Uv & 255,
            ["uvFrame"] = (instance.Uv >> 8) & 255,
            // Vertex color bytes in memory order (byte 3 is alpha after the life and alpha rate factors).
            ["vertexColor"] = new JsonArray(instance.Color & 255, (instance.Color >> 8) & 255, (instance.Color >> 16) & 255, instance.Color >> 24)
        };
    }

    /// <summary>Summary of the displayed frame plus up to <paramref name="limit"/> decoded particles.
    /// <paramref name="entryPosition"/> limits the statistics and particles to one entry.</summary>
    public static JsonObject Frame(PreviewFrame frame, PreviewScene? scene, OfflinePreviewManifest? manifest, int limit, int? entryPosition = null)
    {
        var states = frame.EntryStates ?? [];
        var result = new JsonObject
        {
            ["tick"] = frame.Tick,
            ["time"] = frame.Time,
            ["revision"] = manifest?.Revision,
            ["alive"] = frame.Particles.Count,
            ["simulationMs"] = Math.Round(frame.SimulationMs, 3)
        };
        var tables = Enumerable.Range(0, states.Count).Where(t => entryPosition == null || scene?.AtTable(t)?.Position == entryPosition).ToArray();
        if (entryPosition != null && tables.Length == 0) throw new ArgumentException($"entry {entryPosition} 不在当前预览中（未显示或不存在）。");
        result["entries"] = new JsonArray(tables.Select(t =>
        {
            var e = scene?.AtTable(t);
            if (e?.Logic == true)       // NoDraw trigger carrier: simulated, nothing drawn
                return (JsonNode?)new JsonObject { ["position"] = e.Position, ["index"] = states[t].Index, ["name"] = e.Name, ["alive"] = states[t].Count,
                    ["type"] = e.Type, ["logicOnly"] = true };
            var h = ColorHeader.From(states[t], e?.Document);
            var a = e?.Document == null ? EntryAppearance.None : EntryAppearance.From(e.Document.Entry);
            return (JsonNode?)new JsonObject { ["position"] = e?.Position, ["label"] = e?.Label, ["index"] = states[t].Index, ["name"] = e?.Name, ["alive"] = states[t].Count,
                ["type"] = e?.Type, ["blend"] = h.Blend, ["colorRate"] = Finite(h.ColorRate), ["intensity"] = Finite(h.Intensity), ["edgeBlendRange"] = Finite(h.EdgeBlendRange),
                ["appearance"] = new JsonObject
                {
                    ["softParticleDistance"] = a.SoftParticleDistance, ["depthBias"] = a.DepthBias, ["detonemapRatio"] = a.DetonemapRatio,
                    ["distanceBias"] = a.DistanceBias, ["fadeByDepth"] = a.FadeDepth != null, ["fadeByAngle"] = a.FadeAngle != null,
                    ["alphaCorrectionMode"] = a.AlphaCorrection?.Mode, ["rgbCommon"] = a.RgbCommon != null, ["rgbWater"] = a.RgbWater != null
                } };
        }).ToArray());
        if (entryPosition != null) result["alive"] = tables.Sum(t => states[t].Count);
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        float minW = float.PositiveInfinity, maxW = float.NegativeInfinity, minH = float.PositiveInfinity, maxH = float.NegativeInfinity;
        double sumW = 0, sumH = 0, sumAlpha = 0; int finite = 0, minAlpha = 255, maxAlpha = 0;
        var sequences = new SortedDictionary<uint, int>();
        var listed = new JsonArray();
        foreach (int t in tables)
        {
            var state = states[t];
            var e = scene?.AtTable(t);
            if (e?.Logic == true) continue;      // carriers are not drawn: keep them out of the size statistics
            uint uvFlags = e?.Document?.UvFlags ?? 0;
            var kind = e?.Document?.Kind ?? PreviewTypeKind.Billboard3D;
            for (int i = state.First; i < state.First + state.Count; i++)
            {
                var particle = frame.Particles[i];
                var instance = BillboardInstance.Decode(particle, state, uvFlags, kind);
                if (listed.Count < limit)
                {
                    var row = Particle(particle, state, uvFlags, kind);
                    row["entryPosition"] = e?.Position;
                    listed.Add(row);
                }
                if (!float.IsFinite(particle.Position.X) || !float.IsFinite(particle.Position.Y) || !float.IsFinite(particle.Position.Z) ||
                    !float.IsFinite(instance.Size.X) || !float.IsFinite(instance.Size.Y)) continue;
                finite++;
                min = Vector3.Min(min, particle.Position); max = Vector3.Max(max, particle.Position);
                minW = Math.Min(minW, instance.Size.X); maxW = Math.Max(maxW, instance.Size.X); sumW += instance.Size.X;
                minH = Math.Min(minH, instance.Size.Y); maxH = Math.Max(maxH, instance.Size.Y); sumH += instance.Size.Y;
                int alpha = (int)(instance.Color >> 24);
                minAlpha = Math.Min(minAlpha, alpha); maxAlpha = Math.Max(maxAlpha, alpha); sumAlpha += alpha;
                uint sequence = instance.Uv & 255;
                sequences[sequence] = sequences.GetValueOrDefault(sequence) + 1;
            }
        }
        result["finite"] = finite;
        if (finite > 0)
        {
            result["bounds"] = new JsonObject { ["min"] = Vector(min), ["max"] = Vector(max) };
            result["width"] = new JsonObject { ["min"] = minW, ["max"] = maxW, ["mean"] = Math.Round(sumW / finite, 6) };
            result["height"] = new JsonObject { ["min"] = minH, ["max"] = maxH, ["mean"] = Math.Round(sumH / finite, 6) };
            result["vertexAlpha"] = new JsonObject { ["min"] = minAlpha, ["max"] = maxAlpha, ["mean"] = Math.Round(sumAlpha / finite, 2) };
            result["uvSequences"] = new JsonObject(sequences.Select(p => KeyValuePair.Create(p.Key.ToString(CultureInfo.InvariantCulture), (JsonNode?)p.Value)));
        }
        result["particles"] = listed;
        return result;
    }

    /// <summary>Every entry of the displayed file: whether it is simulated, and why not.</summary>
    public static JsonArray SceneEntries(PreviewScene scene, int? selected) => new(scene.Entries.Select(e => (JsonNode?)new JsonObject
    {
        ["position"] = e.Position, ["index"] = e.Index, ["name"] = e.Name, ["type"] = e.Type,
        ["shown"] = e.Shown, ["selected"] = e.Position == selected, ["excluded"] = e.Excluded,
        ["logicOnly"] = e.Logic ? true : null, ["firesActions"] = e.Actions == null ? null : new JsonArray(e.Actions.Select(a => (JsonNode?)a).ToArray())
    }).ToArray());

    /// <summary>Entries embedded in child Actions: which Action plays them and whether the preview runs them.</summary>
    public static JsonArray SceneChildren(PreviewScene scene) => new(scene.Children.Select(e => (JsonNode?)new JsonObject
    {
        ["action"] = e.Action, ["child"] = e.Child, ["label"] = e.Label, ["index"] = e.Index, ["name"] = e.Name, ["type"] = e.Type,
        ["shown"] = e.Shown, ["table"] = e.Shown ? scene.Shown.ToList().IndexOf(e) : null, ["excluded"] = e.Excluded
    }).ToArray());

    public static JsonObject Inspection(JsonObject reply, int attributeIndex) => new()
    {
        ["attributeIndex"] = attributeIndex,
        ["revision"] = reply["revision"]?.DeepClone(),
        ["fields"] = reply["fields"]?.DeepClone() ?? new JsonArray(),
        ["colorGroups"] = reply["colorGroups"]?.DeepClone() ?? new JsonArray(),
        ["curves"] = reply["curves"]?.DeepClone() ?? new JsonArray(),
        // Clip attributes: the animated component of each curve channel, in channel order (for example ["A"]).
        ["clipChannels"] = reply["clipChannels"]?.DeepClone()
    };

    public static JsonArray Vector(Vector3 value) => new(Finite(value.X), Finite(value.Y), Finite(value.Z));
    private static JsonNode? Finite(float value) => float.IsFinite(value) ? JsonValue.Create(value) : null;

    public static int Int(JsonObject arguments, string name, int minimum, int maximum, int? fallback = null)
    {
        if (arguments[name] == null) return fallback ?? throw new ArgumentException($"缺少参数 {name}。");
        if (arguments[name] is not JsonValue value || !value.TryGetValue(out long number)) throw new ArgumentException($"{name} 必须是整数。");
        if (number < minimum || number > maximum) throw new ArgumentException($"{name} 应在 {minimum}–{maximum} 之间。");
        return (int)number;
    }

    public static long Long(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonValue value || !value.TryGetValue(out long number)) throw new ArgumentException($"缺少整数参数 {name}。");
        return number;
    }

    public static float? Float(JsonObject arguments, string name, float minimum, float maximum)
    {
        if (arguments[name] == null) return null;
        if (arguments[name] is not JsonValue value || !value.TryGetValue(out double number) || !double.IsFinite(number) || number < minimum || number > maximum)
            throw new ArgumentException($"{name} 应为 {minimum.ToString(CultureInfo.InvariantCulture)}–{maximum.ToString(CultureInfo.InvariantCulture)} 的数值。");
        return (float)number;
    }

    public static string? String(JsonObject arguments, string name)
    {
        if (arguments[name] == null) return null;
        if (arguments[name] is not JsonValue value || !value.TryGetValue(out string? text)) throw new ArgumentException($"{name} 必须是字符串。");
        return text;
    }

    public static bool Bool(JsonObject arguments, string name)
    {
        if (arguments[name] == null) return false;
        if (arguments[name] is not JsonValue value || !value.TryGetValue(out bool flag)) throw new ArgumentException($"{name} 必须是布尔值。");
        return flag;
    }
}
