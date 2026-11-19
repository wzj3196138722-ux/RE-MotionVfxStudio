using System.Globalization;

namespace VfxStudio.Re4Render;

/// <summary>Clip curve evaluation. Interpolation names are those of the shared RE Engine clip enum
/// (1 Discrete, 2 Linear, 3 Event, 5 Hermite; the C# model calls them Type1, Type2, Type3, Bezier).
/// <list type="bullet">
/// <item>A key's interpolation governs the segment that starts at it. Measured in the native emulator on an
/// EmitterColorClip (alpha at emitter +0x137): Type2 is linear, Type1 holds the key's value until the next key.</item>
/// <item>Type3 (Event) holds like Type1; raw 5 is Hermite with tangents (out_x, out_y) / (in_x, in_y) stored as
/// offsets from the key, equivalent to Bezier handles at a third of them (MHWs-EFX-Editor clip_fcurve.py,
/// tested in game on MHWilds). RE4's corpus uses only Type1 and Type2.</item>
/// <item>Other values (Unknown, Slerp, Bezier 7, ...) stay 0 or misbehave in game; they are drawn flat at 0 and
/// cannot be chosen.</item>
/// <item>Before the first key the first value holds; after the last key the last value holds.</item>
/// <item>Emitter-time clips run in frames: tick t samples clip time (t − 1) modulo (duration + 1), so the key at
/// t = duration is still reached before the clip starts over (measured).</item>
/// </list></summary>
public static class PreviewCurveMath
{
    public readonly record struct Key(float Time, float Value, string Interpolation, float OutX = 0, float OutY = 0, float InX = 0, float InY = 0);

    /// <summary>Interpolations the editor offers, with display names.</summary>
    public static readonly (string Name, string Label)[] Editable =
        [("Type1", "保持（Discrete）"), ("Type2", "线性（Linear）"), ("Type3", "事件（Event，形状同保持）"), ("Bezier", "Hermite（带切线）")];

    public static bool IsHold(string interpolation) => interpolation is "Type1" or "Type3";
    public static bool IsHermite(string interpolation) => interpolation == "Bezier";
    public static bool IsKnown(string interpolation) => interpolation is "Type1" or "Type2" or "Type3" or "Bezier";
    /// <summary>Measured in RE4 itself (the others come from MHWilds tests of the shared clip format).</summary>
    public static bool IsMeasuredInRe4(string interpolation) => interpolation is "Type1" or "Type2";

    public static Key[] Parse(PreviewCurveChannel channel) =>
        channel.Keys.Select(k => new Key(ParseFloat(k.Time), ParseFloat(k.Value), k.Interpolation,
            Opt(k.OutX), Opt(k.OutY), Opt(k.InX), Opt(k.InY))).ToArray();

    public static float ParseFloat(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : 0;
    private static float Opt(string? text) => text == null ? 0 : ParseFloat(text);

    public static float Evaluate(IReadOnlyList<Key> keys, float time)
    {
        if (keys.Count == 0) return 0;
        if (time <= keys[0].Time) return Value(keys[0]);
        for (int i = 0; i < keys.Count - 1; i++)
        {
            var a = keys[i]; var b = keys[i + 1];
            if (time >= b.Time) continue;
            if (!IsKnown(a.Interpolation)) return 0;
            if (IsHold(a.Interpolation) || b.Time <= a.Time) return a.Value;
            if (IsHermite(a.Interpolation)) return Hermite(a, b, time);
            return a.Value + (b.Value - a.Value) * (time - a.Time) / (b.Time - a.Time);
        }
        return Value(keys[^1]);
    }

    private static float Value(Key k) => IsKnown(k.Interpolation) ? k.Value : 0;

    /// <summary>Cubic Bezier through the key's out handle and the next key's in handle (a third of each
    /// tangent); the next key contributes an in handle only when it is Hermite itself.</summary>
    private static float Hermite(Key a, Key b, float time)
    {
        float x0 = a.Time, y0 = a.Value, x1 = a.Time + a.OutX / 3, y1 = a.Value + a.OutY / 3;
        bool bHermite = IsHermite(b.Interpolation);
        float x2 = bHermite ? b.Time + b.InX / 3 : b.Time, y2 = bHermite ? b.Value + b.InY / 3 : b.Value, x3 = b.Time, y3 = b.Value;
        // Solve x(s) = time by bisection (x is monotone for well-formed handles; clamped otherwise).
        float lo = 0, hi = 1, s = .5f;
        for (int i = 0; i < 40; i++)
        {
            s = (lo + hi) / 2;
            float x = Cubic(x0, x1, x2, x3, s);
            if (x < time) lo = s; else hi = s;
        }
        return Cubic(y0, y1, y2, y3, s);
    }

    private static float Cubic(float p0, float p1, float p2, float p3, float s)
    {
        float u = 1 - s;
        return u * u * u * p0 + 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s * p3;
    }

    /// <summary>Clip time of an emitter-time clip at a preview tick (tick 1 = the first simulated frame).</summary>
    public static float EmitterClipTime(int tick, float duration)
    {
        float t = Math.Max(0, tick - 1);
        float period = duration + 1;
        return period > 0 ? t % period : t;
    }

    /// <summary>Clip attributes whose time is the particle's age rather than the emitter's (the Pt* family).</summary>
    public static bool IsParticleTime(string? attributeType) => attributeType?.StartsWith("Pt", StringComparison.Ordinal) == true;
}
