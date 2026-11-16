using System.Numerics;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Main;
using ReeLib.Efx.Structs.Misc;
using ReeLib.Efx.Structs.Transforms;

namespace VfxStudio.Re4Render;

/// <summary>Type* colour header as uploaded by the engine, read from the live 0x2f8 type block
/// (Polygon3DConstant starts at block+0x54; RE4-EFX-RUNTIME §4.2). Reading it per frame lets
/// TypeBillboard3DExpression writes reach the picture.</summary>
public readonly record struct ColorHeader(int Blend, float ColorRate, float Intensity, Vector4 ColorRange, float EdgeBlendRange, float AlphaRate)
{
    /// <param name="document">Source of the header when the type block has no Polygon3DConstant
    /// (Billboard2D): its file values; the preview cannot load Billboard2D expressions anyway.</param>
    public static ColorHeader From(PreviewEntryState state, PreviewDocument? document = null)
    {
        int c = state.ConstantOffset;
        if (c < 0)
        {
            var t = document?.Entry.TypeAttribute as EFXAttributeTypeBillboard2D ?? throw new InvalidDataException($"Type {state.Type} needs its file header");
            static float U(int v) => v / 255f;
            return new((int)(t.Flags & 15), t.ColorRate, t.Intensity, new Vector4(U(t.ColorRange.R), U(t.ColorRange.G), U(t.ColorRange.B), U(t.ColorRange.A)),
                t.EdgeBlendRange, t.AlphaRate);
        }
        var b = state.TypeBlock;
        return new(state.Blend, FrameDecoder.F32(b, c + 0x10c), FrameDecoder.F32(b, c + 0xec),
            new Vector4(FrameDecoder.F32(b, c + 0xf0), FrameDecoder.F32(b, c + 0xf4), FrameDecoder.F32(b, c + 0xf8), FrameDecoder.F32(b, c + 0xfc)),
            FrameDecoder.F32(b, c + 0x108), FrameDecoder.F32(b, c + 0xe4));
    }
}

public sealed record FadeAngleSettings(uint Flags, float Cone, float Spread, float AlphaRate, Vector3 ConeDir);

/// <param name="Bytes">AlphaCorrection's four packed bytes (RUNTIME §4.7) for modes 0–2; mode 3 takes
/// byte 2 from each particle's life byte.</param>
public sealed record AlphaCorrectionSettings(int Mode, uint Bytes);

public sealed record RgbCommonSettings(uint Flags, Vector4 GreenColor, float GreenIntensity, float GreenSaturate, float GreenCurve,
    Vector4 RedColor, float RedIntensity, float RedAlphaToB, float Alpha, bool GreenLighting, bool RedLighting);

public sealed record RgbWaterSettings(Vector4 SpecularColor, float SpecularIntensity, Vector4 SheetColor, float SheetIntensity, float GtoB, float Alpha);

/// <summary>Per-entry rendering constants that come from the file (no runtime writer in the CPU path,
/// except the few *Expression variants, which this preview does not evaluate).</summary>
public sealed record EntryAppearance(
    float SoftParticleDistance, float DepthBias, float DistanceBias, int PriorityBias, float DetonemapRatio,
    Vector4? FadeDepth, FadeAngleSettings? FadeAngle, AlphaCorrectionSettings? AlphaCorrection,
    RgbCommonSettings? RgbCommon, RgbWaterSettings? RgbWater,
    Vector3? TextureFilter = null, int ParticleLighting = 0, bool FrameBlend = false)
{
    /// <summary>ShaderSettings.ParticleLighting 1-4 and 6 light the particle; 0 and 5 do not (RUNTIME 4.4).</summary>
    public bool Lit => ParticleLighting is not (0 or 5);

    public static readonly EntryAppearance None = new(0, 0, 0, 0, 0, null, null, null, null, null);

    public static EntryAppearance From(EFXEntry entry)
    {
        var shader = entry.Attributes.OfType<EFXAttributeShaderSettings>().FirstOrDefault();
        var depth = entry.Attributes.OfType<EFXAttributeFadeByDepth>().FirstOrDefault();
        var angle = entry.Attributes.OfType<EFXAttributeFadeByAngle>().FirstOrDefault();
        var ac = entry.Attributes.OfType<EFXAttributeAlphaCorrection>().FirstOrDefault();
        var common = entry.Attributes.OfType<EFXAttributeRgbCommon>().FirstOrDefault();
        var water = entry.Attributes.OfType<EFXAttributeRgbWater>().FirstOrDefault();
        var filter = entry.Attributes.OfType<EFXAttributeTextureFilter>().FirstOrDefault();
        var uv = entry.Attributes.OfType<EFXAttributeUVSequence>().FirstOrDefault();
        static Vector4 Color(ReeLib.via.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
        return new(
            shader != null && (shader.Flags & 4) != 0 ? Math.Max(0, shader.SoftParticleDistance) : 0,
            shader?.DepthBias ?? 0, shader?.DistanceBias ?? 0, shader?.PriorityBias ?? 0, shader?.DetonemapRatio ?? 0,
            depth == null ? null : new Vector4(depth.NearStart, depth.NearEnd, depth.FarStart, depth.FarEnd),
            angle == null ? null : new FadeAngleSettings(angle.Flags & 7, angle.Cone, angle.Spread, angle.AlphaRate, angle.ConeDir),
            ac == null ? null : new AlphaCorrectionSettings((int)(ac.Flags & 3), AppearanceReference.PackAlphaCorrection(ac.Flags & 3, ac.HiPass, ac.LoPass, ac.CurveConst, 0)),
            // Per-particle green/red colour randomisation (Flags bits 0/1) and the life envelopes are not
            // located in the particle record yet; u = 0 uses the base colours, envelopes count as 1.
            common == null ? null : new RgbCommonSettings(common.Flags & 7, Color(common.GreenChColor), common.GreenChIntensity, common.GreenChSaturate,
                common.GreenChCurve, Color(common.RedChColor), common.RedChIntensity, common.RedChAlphaToB, common.Alpha, common.GreenChLighting, common.RedChLighting),
            water == null ? null : new RgbWaterSettings(Color(water.SpecularColor), water.SpecularIntensity, Color(water.SheetColor), water.SheetIntensity, water.GtoB, water.Alpha),
            // cbTextureFilter (x, y, z) = (TexelAlphaRate, TexelAlphaHPThreshold, TexelAlphaHPMinValue): inferred
            // from the names and the corpus values; the writer of the constant is not located in re4.exe.
            filter == null ? null : new Vector3(filter.TexelAlphaRate, filter.TexelAlphaHPThreshold, filter.TexelAlphaHPMinValue),
            (int)(shader?.ParticleLighting ?? 0),
            uv != null && (uv.Flags & 0x400) != 0);
    }
}

/// <summary>Switches for the compromise steps of the colour chain (RENDER-HOST §6.9). Defaults follow
/// the design document; the panel and the MCP tool change them live.</summary>
public sealed class CompositionConfig
{
    public bool Fades { get; set; } = true;
    public bool AlphaCorrection { get; set; } = true;
    public bool RgbLayers { get; set; } = true;
    public bool SoftParticles { get; set; } = true;
    public bool DepthBias { get; set; } = true;
    public bool Detonemap { get; set; } = true;
    /// <summary>Multiply alpha by the header's AlphaRate. Off: the value is 0 in most official files,
    /// so it cannot be a plain alpha factor.</summary>
    public bool AlphaRate { get; set; }
    public bool SortEntries { get; set; } = true;
    public bool SortWithinEntry { get; set; }
    /// <summary>Gain K on the lit part of RgbCommon under Physical (RUNTIME §4.9).</summary>
    public float PhysicalGain { get; set; } = 1;
    public bool TextureFilter { get; set; } = true;
    public bool TexelChannel { get; set; } = true;
    public bool FrameBlend { get; set; } = true;
    /// <summary>Light lit particles (ParticleLighting 1-4) with the preview light below. The preview has
    /// no scene lights, so this is a compromise; off draws them unlit.</summary>
    public bool Lighting { get; set; } = true;
    public Vector3 Ambient { get; set; } = new(.45f);
    public float KeyLight { get; set; } = .75f;
    public Vector3 LightDirection { get; set; } = new(.4f, 1, .3f);
    /// <summary>Source and destination blend factor per BlendType 0–4 (RENDER-HOST §6.3).</summary>
    public BlendPair[] Blends { get; } =
    [
        new(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha),
        new(BlendFactor.SrcAlpha, BlendFactor.One),
        new(BlendFactor.SrcAlpha, BlendFactor.One),
        new(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha),
        new(BlendFactor.DstColor, BlendFactor.Zero),
    ];
}

public enum BlendFactor { Zero, One, SrcAlpha, OneMinusSrcAlpha, DstColor, OneMinusDstColor, SrcColor, OneMinusSrcColor }
public sealed class BlendPair(BlendFactor source, BlendFactor destination)
{
    public BlendFactor Source { get; set; } = source;
    public BlendFactor Destination { get; set; } = destination;
}

/// <summary>C# statements of the colour-chain formulas, kept line by line identical to the GLSL in
/// <see cref="AppearanceShaderLibrary"/>; the shader tests compare the two.</summary>
public static class AppearanceReference
{
    public const float CullThreshold = 1.1920929e-7f; // 2^-23

    /// <summary>cvttss2si then the low 8 bits, as the engine packs each byte.</summary>
    private static uint Byte(float v) => (uint)(int)v & 255;

    public static uint PackAlphaCorrection(uint mode, float hi, float lo, float curve, byte life)
    {
        mode &= 3;
        if (mode == 3) return Byte(hi * 31.875f) | Byte(lo * 31.875f) << 8 | Byte(life / 255f * 255f) << 16 | 3u << 24;
        return Byte(hi * 255.5f) | Byte(lo * 255.5f) << 8 | Byte(curve / 3f * 127f + 127.5f) << 16 | mode << 24;
    }

    public static float Saturate(float v) => Math.Clamp(v, 0f, 1f);

    public static float FadeDepth(float d, Vector4 p)
    {
        if (d < p.X || d > p.W) return 0;
        if (d < p.Y) return (d - p.X) / (p.Y - p.X);
        if (d > p.Z) return 1 - (d - p.Z) / (p.W - p.Z);
        return 1;
    }

    /// <param name="flags">File flags: bit 0 double cone, bit 1 invert, bit 2 view from the particle.</param>
    public static float FadeAngle(Vector3 coneDirWorld, Vector3 emitter, Vector3 particle, Vector3 camera, uint flags, float cone, float spread, float alphaRate)
    {
        var p = (flags & 4) != 0 ? particle : emitter;
        var v = Vector3.Normalize(p - camera);
        float dot = Vector3.Dot(coneDirWorld, v);
        float c = (flags & 1) != 0 ? MathF.Abs(dot) : -dot;
        float cosCone = MathF.Cos(cone), cosOuter = MathF.Cos(Math.Clamp(cone + spread, 0, MathF.PI));
        bool invert = (flags & 2) != 0;
        if (c >= cosCone) return invert ? alphaRate : 1;
        if (c < cosOuter) return invert ? 1 : alphaRate;
        float t = (c - cosOuter) / (cosCone - cosOuter);
        return alphaRate + (1 - alphaRate) * (invert ? 1 - t : t);
    }

    /// <summary>AlphaCorrection: A = texel alpha, V = vertex alpha including fades.</summary>
    public static float AlphaCorrect(float a, float v, uint bytes)
    {
        uint mode = bytes >> 24 & 3;
        float hi = (bytes & 255) / 255f, lo = (bytes >> 8 & 255) / 255f, b2 = bytes >> 16 & 255;
        switch (mode)
        {
            case 1:
            {
                float s = (b2 - 127f) * 0.2141272f;
                float f = MathF.Abs(s) < 1e-4f ? v : Saturate((1 - MathF.Exp(-s * v)) / (1 - MathF.Exp(-s)));
                float t = 1 - hi * f;
                return Saturate((a - t) / MathF.Max(1e-6f, lo - t));
            }
            case 2: return SmoothStep(lo, hi, a * v);
            case 3:
            {
                float x = hi * 8, y = lo * 8, z = b2 / 255f;
                return MathF.Pow(a * v, x + (y - x) * Saturate(z)) * z;
            }
            default: return a * v;
        }
    }

    public static float SmoothStep(float e0, float e1, float x)
    {
        float t = Saturate((x - e0) / (e1 - e0));
        return t * t * (3 - 2 * t);
    }

    /// <summary>RgbCommon layers. Returns lit and unlit colour and the layer alpha.</summary>
    public static (Vector3 Lit, Vector3 Unlit, float Alpha) RgbCommon(Vector4 tex, RgbCommonSettings s)
    {
        float m = MathF.Pow(MathF.Max(tex.Y, 1e-7f), s.GreenCurve);
        float ig = s.GreenColor.W * m, igi = ig * s.GreenIntensity;
        float w = Saturate(igi / MathF.Max(s.GreenSaturate, 1e-6f)); w *= w;
        var greenRgb = igi * Vector3.Lerp(new Vector3(s.GreenColor.X, s.GreenColor.Y, s.GreenColor.Z), Vector3.One, w);
        float toB = s.RedAlphaToB;
        float v1 = (s.Flags & 4) != 0 ? 0 : toB, v2 = toB;
        var redRgb = (tex.X + (tex.Z - tex.X) * v1) * s.RedIntensity * new Vector3(s.RedColor.X, s.RedColor.Y, s.RedColor.Z);
        float redA = (tex.W + (tex.Z - tex.W) * v2) * s.RedColor.W;
        float alpha = Saturate((ig + redA) * s.Alpha);
        if (alpha <= 1e-4f) alpha = 0;
        var lit = (s.GreenLighting ? greenRgb : Vector3.Zero) + (s.RedLighting ? redRgb : Vector3.Zero);
        var unlit = (s.GreenLighting ? Vector3.Zero : greenRgb) + (s.RedLighting ? Vector3.Zero : redRgb);
        return (lit, unlit, alpha);
    }

    /// <summary>RgbWater: everything is lit. Colours are byte values / 255 already.</summary>
    public static (Vector3 Lit, float Alpha) RgbWater(Vector4 tex, RgbWaterSettings s)
    {
        float mix = tex.Y + (tex.Z - tex.Y) * s.GtoB;
        var rgb = new Vector3(s.SpecularColor.X, s.SpecularColor.Y, s.SpecularColor.Z) * tex.X * s.SpecularIntensity
                + new Vector3(s.SheetColor.X, s.SheetColor.Y, s.SheetColor.Z) * mix * s.SheetIntensity;
        float alpha = Saturate(tex.W * s.Alpha * (s.SheetColor.W * mix + s.SpecularColor.W * tex.X));
        return (rgb, alpha);
    }

    private const float A = 2.51f, B = 0.03f, C = 2.43f, D = 0.59f, E = 0.14f;
    public static float Aces(float x) => Saturate(x * (A * x + B) / (x * (C * x + D) + E));

    /// <summary>Inverse of the composite's ACES curve on [0, 0.999]: the HDR value that maps to y.</summary>
    public static float InverseAces(float y)
    {
        y = Math.Clamp(y, 0, 0.999f);
        float qa = A - C * y, qb = B - D * y, qc = -E * y;
        return (-qb + MathF.Sqrt(qb * qb - 4 * qa * qc)) / (2 * qa);
    }

    /// <summary>Fragment shading of one particle texel; mirrors <c>shade()</c> in AppearanceShaderLibrary.</summary>
    /// <param name="tex">Filtered texel.</param>
    /// <param name="vertex">Vertex colour; alpha already includes life, blink and the vertex fades.</param>
    /// <param name="soft">Soft-particle factor (1 when off).</param>
    /// <summary>Mirrors <c>textureFilter()</c>.</summary>
    public static float TextureFilter(float a, Vector3 p)
    {
        float t = p.Y == p.Z ? (a > p.Z ? 1 : 0) : Saturate((a - p.Z) / (p.Y - p.Z));
        float b = t * t * (3 - 2 * t) * a;
        return b > 0 ? MathF.Pow(b, p.X) : 0;
    }

    /// <summary>Mirrors <c>texelChannel()</c> (primitivevfx_4 b013 lines 646-861).</summary>
    public static Vector4 TexelChannel(Vector4 tex, ReadOnlySpan<uint> p, float env)
    {
        float hueShift = (p[0] & 255) / 255f, weightG = (p[0] >> 8) & 255, desaturate = ((p[0] >> 16) & 255) / 255f, weightRB = p[0] >> 24;
        var shadeColor = new Vector3(p[1] & 255, (p[1] >> 8) & 255, (p[1] >> 16) & 255) / 255f;
        float t = (p[1] >> 24) / 255f, rate = BitConverter.UInt32BitsToSingle(p[2]);
        float power = (float)BitConverter.UInt16BitsToHalf((ushort)(p[3] & 0xffff)), opacity = (float)BitConverter.UInt16BitsToHalf((ushort)(p[3] >> 16));
        float rb = tex.X + (tex.Z - tex.X) * t;
        float s = MathF.Pow(MathF.Max(rb, 1e-6f), power) * weightRB + MathF.Pow(MathF.Max(tex.Y, 1e-6f), power) * weightG;
        float T = env * 100 * s, T2 = T * T;
        float u = (0.860117757f + 1.54118254e-4f * T + 1.28641212e-7f * T2) / (1 + 8.42420235e-4f * T + 7.08145163e-7f * T2);
        float v = (0.317398726f + 4.22806245e-5f * T + 4.20481691e-8f * T2) / (1 - 2.89741816e-5f * T + 1.61456053e-7f * T2);
        float d = 2 * u - 8 * v + 4, x = 3 * u / d, y = 2 * v / d, X = x / y, Z = (1 - x - y) / y;
        var rgb = new Vector3(3.2404542f * X - 1.5371385f - 0.4985314f * Z, -0.9692660f * X + 1.8760108f + 0.0415560f * Z,
            0.0556434f * X - 0.2040259f + 1.0572252f * Z);
        float k = T * 4e-4f; k *= k; k *= k;
        var e = new Vector3(MathF.Floor(rate * 1e5f * rgb.X * k), MathF.Floor(rate * 1e5f * rgb.Y * k), MathF.Floor(rate * 1e5f * rgb.Z * k)) * 1e-5f;
        float mx = MathF.Max(e.X, MathF.Max(e.Y, e.Z)), mn = MathF.Min(e.X, MathF.Min(e.Y, e.Z)), dl = mx - mn;
        float sat = mx != 0 ? dl / mx : 0, inv = dl == 0 ? 0 : 1 / dl;
        float hue = e.X == mx ? (e.Y - e.Z) * inv : e.Y == mx ? (e.Z - e.X) * inv + 2 : (e.X - e.Y) * inv + 4;
        static float Frac(float f) => f - MathF.Floor(f);
        float hh = Frac(hue / 6) + hueShift;
        float Q(float o) => Saturate(MathF.Abs(Frac(hh + o) * 2 - 1) * 3 - 1);
        var c = new Vector3(((Q(0) - 1) * sat + 1) * mx, ((Q(2f / 3) - 1) * sat + 1) * mx, ((Q(1f / 3) - 1) * sat + 1) * mx);
        float luma = Vector3.Dot(c, new Vector3(0.3f, 0.59f, 0.11f));
        var o = c + (new Vector3(luma) - c) * desaturate + shadeColor * rb;
        return new(o, (tex.W + (tex.Z - tex.W) * t) * opacity);
    }

    /// <summary>Mirrors <c>previewLight()</c>.</summary>
    public static Vector3 PreviewLight(Vector3 normal, EntryUniforms u)
    {
        float wrap = Saturate(Vector3.Dot(normal, u.LightDirection) * 0.5f + 0.5f);
        return u.Ambient + new Vector3(u.KeyLight * wrap);
    }

    public static Vector4 Shade(Vector4 tex, Vector4 vertex, float soft, uint acBytes, EntryUniforms u) => Shade(tex, vertex, soft, acBytes, u, Vector3.One);

    /// <param name="light">The light reaching the particle (<c>particleLight</c>); used only for lit entries.</param>
    public static Vector4 Shade(Vector4 tex, Vector4 vertex, float soft, uint acBytes, EntryUniforms u, Vector3 light)
    {
        if (u.Has(EntryUniforms.TexelChannelBit)) tex = TexelChannel(tex, u.TexelChannel, 1);
        bool layered = u.Has(EntryUniforms.RgbCommonBit) || u.Has(EntryUniforms.RgbWaterBit);
        bool filter = u.Has(EntryUniforms.TextureFilterBit);
        if (filter && !layered) tex.W = TextureFilter(tex.W, u.TextureFilter);
        var L = u.Has(EntryUniforms.LitBit) ? light : Vector3.One;
        var vrgb = new Vector3(vertex.X, vertex.Y, vertex.Z);
        var trgb = new Vector3(tex.X, tex.Y, tex.Z);
        float alpha = u.Has(EntryUniforms.AlphaCorrectionBit) ? AlphaCorrect(tex.W, vertex.W, acBytes) : tex.W * vertex.W;
        if (u.Has(EntryUniforms.AlphaRateBit)) alpha *= u.AlphaRate;
        Vector3 rgb;
        if (u.Has(EntryUniforms.RgbCommonBit))
        {
            var (lit, unlit, a) = RgbCommon(tex, u.Common!);
            alpha = (filter ? TextureFilter(a, u.TextureFilter) : a) * vertex.W;
            lit *= L;
            rgb = u.Blend switch { 1 => (lit * u.PhysicalGain + unlit) * vrgb, 3 => unlit, _ => lit + unlit };
        }
        else if (u.Has(EntryUniforms.RgbWaterBit))
        {
            var (lit, a) = RgbWater(tex, u.Water!);
            alpha = (filter ? TextureFilter(a, u.TextureFilter) : a) * vertex.W;
            lit *= L;
            rgb = u.Blend switch { 1 => lit * u.PhysicalGain * vrgb, 3 => Vector3.Zero, _ => lit };
        }
        else if (u.Blend == 3)
        {
            // EdgeBlend: ColorRate = InnerIntensity, Intensity = OuterIntensity, ColorRange = EdgeOuterColor.
            float t = Saturate(tex.W / MathF.Max(u.EdgeBlendRange, 1e-4f));
            var outer = new Vector3(u.ColorRange.X, u.ColorRange.Y, u.ColorRange.Z) * u.Intensity;
            rgb = Vector3.Lerp(outer, vrgb * u.ColorRate, t) * trgb * L;
        }
        else rgb = vrgb * trgb * u.ColorRate * L;
        alpha *= soft;
        if (u.Blend == 4) return new(Vector3.Lerp(Vector3.One, rgb, alpha), alpha);
        if (u.Has(EntryUniforms.DetonemapBit) && u.Detonemap != 0)
        {
            var inv = new Vector3(InverseAces(rgb.X), InverseAces(rgb.Y), InverseAces(rgb.Z)) / MathF.Max(u.Exposure, 1e-4f);
            rgb = Vector3.Lerp(rgb, inv, u.Detonemap);
        }
        return new(rgb, alpha);
    }
}
