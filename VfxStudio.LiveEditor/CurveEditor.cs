using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReeLib.Efx.Structs.Common;

namespace VfxStudio.LiveEditor;

internal sealed record CurveKeyInfo(int Index, string Time, string Value, string Interpolation, string? OutX, string? OutY, string? InX, string? InY);
internal sealed record CurveChannelInfo(int Index, string ValueType, CurveKeyInfo[] Keys);
internal sealed record CurveInfo(string Path, bool Editable, string? Reason, string Duration, CurveChannelInfo[] Channels);

/// <summary>Edits existing channels without changing their bit/property mapping. Rebuilds flat key and tangent arrays together.</summary>
internal static class CurveEditor
{
    private sealed record Key(EfxClipFrame Frame, EfxClipInterpolationTangents Tangents);
    private sealed record Channel(ClipValueType ValueType, List<Key> Keys);
    private const int MaxKeys = 100000;

    public static CurveInfo Inspect(EditableCurve curve)
    {
        try
        {
            var channels = Read(curve.Data);
            return new(curve.Path, true, null, ContentInspector.Format(curve.Data.clipDuration), channels.Select((c, i) =>
                new CurveChannelInfo(i, c.ValueType.ToString(), c.Keys.Select((k, j) =>
                    new CurveKeyInfo(j, ContentInspector.Format(k.Frame.frameTime), Value(k.Frame, c.ValueType), k.Frame.type.ToString(),
                        k.Frame.type == FrameInterpolationType.Bezier ? ContentInspector.Format(k.Tangents.out_x) : null,
                        k.Frame.type == FrameInterpolationType.Bezier ? ContentInspector.Format(k.Tangents.out_y) : null,
                        k.Frame.type == FrameInterpolationType.Bezier ? ContentInspector.Format(k.Tangents.in_x) : null,
                        k.Frame.type == FrameInterpolationType.Bezier ? ContentInspector.Format(k.Tangents.in_y) : null)).ToArray())).ToArray());
        }
        catch (InvalidDataException ex) { return new(curve.Path, false, ex.Message, ContentInspector.Format(curve.Data.clipDuration), []); }
    }

    public static void Edit(EfxClipData data, JsonObject request)
    {
        var channels = Read(data);
        int channelIndex = request["channel"]?.GetValue<int>() ?? throw new ArgumentException("channel is required.");
        if (channelIndex < 0 || channelIndex >= channels.Count) throw new ArgumentOutOfRangeException("channel");
        var channel = channels[channelIndex];
        string operation = request["operation"]?.GetValue<string>() ?? throw new ArgumentException("operation is required.");
        int keyIndex = request["keyIndex"]?.GetValue<int>() ?? -1;
        if (operation is "update" or "remove" && (keyIndex < 0 || keyIndex >= channel.Keys.Count)) throw new ArgumentOutOfRangeException("keyIndex");
        if (operation == "remove")
        {
            // A channel is referenced by an external clip bit/material mapping.
            // Keep one key rather than silently removing that mapped channel.
            if (channel.Keys.Count <= 1) throw new InvalidOperationException("A mapped curve channel must retain at least one key.");
            channel.Keys.RemoveAt(keyIndex);
        }
        else if (operation is "add" or "update")
        {
            if (operation == "add" && channels.Sum(c => c.Keys.Count) >= MaxKeys) throw new InvalidOperationException("Curve key limit exceeded.");
            var key = operation == "update" ? channel.Keys[keyIndex] : new Key(new EfxClipFrame(), default);
            var frame = key.Frame;
            frame.frameTime = Float(request, "time");
            string text = request["value"]?.GetValue<string>() ?? throw new ArgumentException("value is required.");
            if (channel.ValueType == ClipValueType.Int) frame.IntValue = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else frame.FloatValue = Finite(text);
            string interpolation = request["interpolation"]?.GetValue<string>() ?? frame.type.ToString();
            if (!Enum.TryParse<FrameInterpolationType>(interpolation, false, out var mode) || !Enum.IsDefined(mode))
                throw new ArgumentException("Unsupported interpolation value.");
            frame.type = mode;
            var tangents = key.Tangents;
            if (mode == FrameInterpolationType.Bezier)
            {
                // Switching to Bezier requires explicit tangents, never invented handle values.
                tangents.out_x = Float(request, "outX"); tangents.out_y = Float(request, "outY");
                tangents.in_x = Float(request, "inX"); tangents.in_y = Float(request, "inY");
            }
            var replacement = new Key(frame, tangents);
            if (operation == "update") channel.Keys[keyIndex] = replacement;
            else channel.Keys.Add(replacement);
            // Stable sorting preserves author order for equal-time keys.
            var ordered = channel.Keys.OrderBy(k => k.Frame.frameTime).ToArray();
            channel.Keys.Clear(); channel.Keys.AddRange(ordered);
        }
        else throw new ArgumentException("Unknown curve key operation.");

        // Do not touch channel count, clip bits, material property hashes or indices.
        data.clips = channels.Select(c => new EfxClipHeader { frameCount = c.Keys.Count, valueType = c.ValueType }).ToArray();
        data.frames = channels.SelectMany(c => c.Keys).Select(k => k.Frame).ToArray();
        data.interpolationData = channels.SelectMany(c => c.Keys).Where(k => k.Frame.type == FrameInterpolationType.Bezier).Select(k => k.Tangents).ToArray();
        data.clipCount = data.clips.Length; data.frameCount = data.frames.Length; data.interpolationDataCount = data.interpolationData.Length;
        data.clipDataSize = checked(data.clipCount * Marshal.SizeOf<EfxClipHeader>());
        data.frameDataSize = checked(data.frameCount * Marshal.SizeOf<EfxClipFrame>());
        data.interpolationDataSize = checked(data.interpolationDataCount * Marshal.SizeOf<EfxClipInterpolationTangents>());
        // Preserve intentional tail duration. Extend only when the edited keys need it.
        data.clipDuration = Math.Max(data.clipDuration, data.frames.Select(f => f.frameTime).DefaultIfEmpty(0).Max());
    }

    public static string Fingerprint(EfxClipData data) => JsonSerializer.Serialize(Inspect(new("", data)), Host.Json);

    private static List<Channel> Read(EfxClipData data)
    {
        var headers = data.clips ?? [];
        var frames = data.frames ?? [];
        var tangents = data.interpolationData ?? [];
        if (headers.Length > 4096 || frames.Length > MaxKeys || headers.Any(h => h.frameCount < 0) ||
            headers.Sum(h => (long)h.frameCount) != frames.Length) throw new InvalidDataException("Curve channel/key counts are inconsistent.");
        if (frames.Count(f => f.type == FrameInterpolationType.Bezier) != tangents.Length) throw new InvalidDataException("Curve Bezier tangent count is inconsistent.");
        if (!float.IsFinite(data.clipDuration)) throw new InvalidDataException("Curve duration is not finite.");
        int frameIndex = 0, tangentIndex = 0;
        var channels = new List<Channel>();
        foreach (var header in headers)
        {
            if (header.valueType is not (ClipValueType.Int or ClipValueType.Float)) throw new InvalidDataException("Unknown curve value type; raw fields remain available.");
            var keys = new List<Key>();
            for (int i = 0; i < header.frameCount; i++)
            {
                var frame = frames[frameIndex++];
                if (!float.IsFinite(frame.frameTime) || header.valueType == ClipValueType.Float && !float.IsFinite(frame.FloatValue))
                    throw new InvalidDataException("Curve contains a non-finite time or floating point value.");
                var tangent = frame.type == FrameInterpolationType.Bezier ? tangents[tangentIndex++] : default;
                if (frame.type == FrameInterpolationType.Bezier && (!float.IsFinite(tangent.out_x) || !float.IsFinite(tangent.out_y) || !float.IsFinite(tangent.in_x) || !float.IsFinite(tangent.in_y)))
                    throw new InvalidDataException("Curve contains non-finite Bezier tangents.");
                keys.Add(new(frame, tangent));
            }
            channels.Add(new(header.valueType, keys));
        }
        return channels;
    }

    private static string Value(EfxClipFrame frame, ClipValueType type) => type == ClipValueType.Int ? ContentInspector.Format(frame.IntValue) : ContentInspector.Format(frame.FloatValue);
    private static float Float(JsonObject request, string name) => Finite(request[name]?.GetValue<string>() ?? throw new ArgumentException(name + " is required."));
    private static float Finite(string text)
    {
        float value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!float.IsFinite(value)) throw new ArgumentException("Curve values must be finite.");
        return value;
    }
}
