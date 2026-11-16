using System.Numerics;
using System.Runtime.InteropServices;

namespace VfxStudio.Re4Render;

/// <summary>Per-entry shading constants, laid out as the std140 block <c>EntryParams</c>.</summary>
public sealed class EntryUniforms
{
    public const int FadeDepthBit = 1, FadeAngleBit = 2, AlphaCorrectionBit = 4, RgbCommonBit = 8, RgbWaterBit = 16,
        SoftBit = 32, DepthBiasBit = 64, DetonemapBit = 128, AlphaRateBit = 256, TextureFilterBit = 512, TexelChannelBit = 1024,
        LitBit = 2048, FrameBlendBit = 4096;
    public int Flags, Blend, FadeAngleFlags, CommonFlags;
    public uint AcBytes;
    public float ColorRate, Intensity, EdgeBlendRange, AlphaRate;
    public Vector4 ColorRange;
    public Vector4 FadeDepth = new(float.MinValue, float.MinValue, float.MaxValue, float.MaxValue);
    public float Cone, Spread, AngleAlphaRate;
    public Vector3 ConeDir = Vector3.UnitZ, Emitter;
    public float SoftDistance, DepthBias, Detonemap, PhysicalGain = 1;
    public RgbCommonSettings? Common;
    public RgbWaterSettings? Water;
    public float Exposure = 1, Near = .05f, Far = 100;
    /// <summary>TextureFilter (TexelAlphaRate, TexelAlphaHPThreshold, TexelAlphaHPMinValue) = cbTextureFilter (x, y, z).</summary>
    public Vector3 TextureFilter = new(1, 0, 0);
    /// <summary>cbTexelChannelOperatorParam as the engine packed it into the type block's constant.</summary>
    public uint[] TexelChannel = new uint[4];
    /// <summary>Preview light for lit particles (ShaderSettings.ParticleLighting 1-4): ambient, key light
    /// direction (towards the light) and key intensity. A compromise: the preview has no scene lights.</summary>
    public Vector3 Ambient = new(.45f), LightDirection = Vector3.Normalize(new(.4f, 1, .3f));
    public float KeyLight = .75f;
    public bool Has(int bit) => (Flags & bit) != 0;

    public static EntryUniforms Build(ColorHeader header, EntryAppearance a, CompositionConfig config, Vector3 emitter)
    {
        var u = new EntryUniforms
        {
            Blend = header.Blend, ColorRate = header.ColorRate, Intensity = header.Intensity, EdgeBlendRange = header.EdgeBlendRange,
            AlphaRate = header.AlphaRate, ColorRange = header.ColorRange, Emitter = emitter, PhysicalGain = config.PhysicalGain,
            SoftDistance = a.SoftParticleDistance, DepthBias = a.DepthBias, Detonemap = a.DetonemapRatio
        };
        if (config.Fades && a.FadeDepth is { } depth) { u.Flags |= FadeDepthBit; u.FadeDepth = depth; }
        if (config.Fades && a.FadeAngle is { } angle)
        {
            u.Flags |= FadeAngleBit; u.FadeAngleFlags = (int)angle.Flags; u.Cone = angle.Cone; u.Spread = angle.Spread; u.AngleAlphaRate = angle.AlphaRate;
            // ConeDir is emitter-local; the emitter matrix is identity in this preview (RENDER-HOST §4.3).
            u.ConeDir = angle.ConeDir.LengthSquared() > 0 ? Vector3.Normalize(angle.ConeDir) : Vector3.UnitZ;
        }
        if (config.AlphaCorrection && a.AlphaCorrection is { } ac) { u.Flags |= AlphaCorrectionBit; u.AcBytes = ac.Bytes; }
        if (config.RgbLayers && a.RgbCommon is { } common) { u.Flags |= RgbCommonBit; u.Common = common; u.CommonFlags = (int)common.Flags; }
        else if (config.RgbLayers && a.RgbWater is { } water) { u.Flags |= RgbWaterBit; u.Water = water; }
        if (config.SoftParticles && a.SoftParticleDistance > 0) u.Flags |= SoftBit;
        if (config.DepthBias && a.DepthBias != 0) u.Flags |= DepthBiasBit;
        if (config.Detonemap && a.DetonemapRatio != 0) u.Flags |= DetonemapBit;
        if (config.AlphaRate) u.Flags |= AlphaRateBit;
        if (config.TextureFilter && a.TextureFilter is { } tf) { u.Flags |= TextureFilterBit; u.TextureFilter = tf; }
        if (config.Lighting && a.Lit)
        {
            u.Flags |= LitBit; u.Ambient = config.Ambient; u.KeyLight = config.KeyLight;
            u.LightDirection = config.LightDirection.LengthSquared() > 0 ? Vector3.Normalize(config.LightDirection) : Vector3.UnitY;
        }
        if (config.FrameBlend && a.FrameBlend) u.Flags |= FrameBlendBit;
        return u;
    }

    /// <summary>Takes the TexelChannelOperator constant from the live type block (the engine packs it at
    /// Polygon3DConstant +512); all-zero words mean the entry has no TexelChannelOperator.</summary>
    public void SetTexelChannel(PreviewEntryState state, CompositionConfig config)
    {
        Flags &= ~TexelChannelBit;
        int c = state.ConstantOffset;
        if (!config.TexelChannel || c < 0 || c + 512 + 16 > state.TypeBlock.Length) return;
        for (int i = 0; i < 4; i++) TexelChannel[i] = FrameDecoder.U32(state.TypeBlock, c + 512 + 4 * i);
        if (TexelChannel.Any(w => w != 0)) Flags |= TexelChannelBit;
    }

    public const int Std140Bytes = 21 * 16;

    public byte[] ToStd140()
    {
        var f = new float[Std140Bytes / 4];
        int o = 0;
        void V(float x, float y, float z, float w) { f[o] = x; f[o + 1] = y; f[o + 2] = z; f[o + 3] = w; o += 4; }
        void I(int x, int y, int z, int w)
        { f[o] = BitConverter.Int32BitsToSingle(x); f[o + 1] = BitConverter.Int32BitsToSingle(y); f[o + 2] = BitConverter.Int32BitsToSingle(z); f[o + 3] = BitConverter.Int32BitsToSingle(w); o += 4; }
        var c = Common; var w2 = Water;
        I(Flags, Blend, FadeAngleFlags, CommonFlags);                       // modes
        I(unchecked((int)AcBytes), 0, 0, 0);                                // acInfo
        V(ColorRate, Intensity, EdgeBlendRange, AlphaRate);                 // header
        V(ColorRange.X, ColorRange.Y, ColorRange.Z, ColorRange.W);          // colorRange
        V(FadeDepth.X, FadeDepth.Y, FadeDepth.Z, FadeDepth.W);              // fadeDepth
        V(Cone, Spread, AngleAlphaRate, 0);                                 // fadeAngle
        V(ConeDir.X, ConeDir.Y, ConeDir.Z, 0);                              // coneDir
        V(Emitter.X, Emitter.Y, Emitter.Z, 0);                              // emitter
        V(SoftDistance, DepthBias, Detonemap, PhysicalGain);                // shading
        V(c?.GreenColor.X ?? 0, c?.GreenColor.Y ?? 0, c?.GreenColor.Z ?? 0, c?.GreenColor.W ?? 0);
        V(c?.GreenIntensity ?? 0, c?.GreenSaturate ?? 0, c?.GreenCurve ?? 0, c?.GreenLighting == true ? 1 : 0);
        V(c?.RedColor.X ?? 0, c?.RedColor.Y ?? 0, c?.RedColor.Z ?? 0, c?.RedColor.W ?? 0);
        V(c?.RedIntensity ?? 0, c?.RedAlphaToB ?? 0, c?.Alpha ?? 0, c?.RedLighting == true ? 1 : 0);
        V(w2?.SpecularColor.X ?? 0, w2?.SpecularColor.Y ?? 0, w2?.SpecularColor.Z ?? 0, w2?.SpecularColor.W ?? 0);
        V(w2?.SheetColor.X ?? 0, w2?.SheetColor.Y ?? 0, w2?.SheetColor.Z ?? 0, w2?.SheetColor.W ?? 0);
        V(w2?.SpecularIntensity ?? 0, w2?.SheetIntensity ?? 0, w2?.GtoB ?? 0, w2?.Alpha ?? 0);
        V(Exposure, Near, Far, 0);                                          // view
        V(TextureFilter.X, TextureFilter.Y, TextureFilter.Z, 0);            // textureFilterP
        I(unchecked((int)TexelChannel[0]), unchecked((int)TexelChannel[1]), unchecked((int)TexelChannel[2]), unchecked((int)TexelChannel[3])); // tcoP
        V(Ambient.X, Ambient.Y, Ambient.Z, KeyLight);                       // lightA
        V(LightDirection.X, LightDirection.Y, LightDirection.Z, 0);         // lightB
        return MemoryMarshal.AsBytes(f.AsSpan()).ToArray();
    }
}

/// <summary>GLSL statements of the colour chain, shared by the billboard shaders and the GPU tests.
/// Each function mirrors the AppearanceReference method of the same name.</summary>
public static class AppearanceShaderLibrary
{
    public const string Block = """
        layout(std140, binding = 3) uniform EntryParams {
            ivec4 modes;       // flags, blend, fadeAngle file flags, RgbCommon flags
            ivec4 acInfo;      // packed AlphaCorrection bytes
            vec4 header;       // ColorRate, Intensity, EdgeBlendRange, AlphaRate
            vec4 colorRange;
            vec4 fadeDepthP;   // NearStart, NearEnd, FarStart, FarEnd
            vec4 fadeAngleP;   // Cone, Spread, AlphaRate
            vec4 coneDir;
            vec4 emitterPos;
            vec4 shading;      // SoftParticleDistance, DepthBias, DetonemapRatio, Physical gain
            vec4 greenColor;
            vec4 greenP;       // intensity, saturate, curve, lighting
            vec4 redColor;
            vec4 redP;         // intensity, AlphaToB, alpha, lighting
            vec4 waterSpec;
            vec4 waterSheet;
            vec4 waterP;       // SpecularIntensity, SheetIntensity, GtoB, Alpha
            vec4 viewP;        // exposure, near, far
            vec4 textureFilterP; // TexelAlphaRate, HPThreshold, HPMinValue
            uvec4 tcoP;        // packed cbTexelChannelOperatorParam
            vec4 lightA;       // ambient rgb, key intensity
            vec4 lightB;       // key light direction (towards the light)
        };
        bool hasFlag(int bit) { return (modes.x & bit) != 0; }
        """;

    public const string Functions = """
        const float CULL = 1.1920929e-7;
        float fadeDepth(float d, vec4 p) {
            if (d < p.x || d > p.w) return 0.0;
            if (d < p.y) return (d - p.x) / (p.y - p.x);
            if (d > p.z) return 1.0 - (d - p.z) / (p.w - p.z);
            return 1.0;
        }
        float fadeAngle(vec3 coneDirWorld, vec3 emitter, vec3 particle, vec3 camera, int flags, float cone, float spread, float alphaRate) {
            vec3 p = (flags & 4) != 0 ? particle : emitter;
            vec3 v = normalize(p - camera);
            float d = dot(coneDirWorld, v);
            float c = (flags & 1) != 0 ? abs(d) : -d;
            float cosCone = cos(cone), cosOuter = cos(clamp(cone + spread, 0.0, 3.14159265));
            bool inv = (flags & 2) != 0;
            if (c >= cosCone) return inv ? alphaRate : 1.0;
            if (c < cosOuter) return inv ? 1.0 : alphaRate;
            float t = (c - cosOuter) / (cosCone - cosOuter);
            return alphaRate + (1.0 - alphaRate) * (inv ? 1.0 - t : t);
        }
        float alphaCorrect(float a, float v, uint bytes) {
            uint mode = (bytes >> 24) & 3u;
            float hi = float(bytes & 255u) / 255.0, lo = float((bytes >> 8) & 255u) / 255.0, b2 = float((bytes >> 16) & 255u);
            if (mode == 1u) {
                float s = (b2 - 127.0) * 0.2141272;
                float f = abs(s) < 1e-4 ? v : clamp((1.0 - exp(-s * v)) / (1.0 - exp(-s)), 0.0, 1.0);
                float t = 1.0 - hi * f;
                return clamp((a - t) / max(1e-6, lo - t), 0.0, 1.0);
            }
            if (mode == 2u) return smoothstep(lo, hi, a * v);
            if (mode == 3u) {
                float x = hi * 8.0, y = lo * 8.0, z = b2 / 255.0;
                return pow(a * v, x + (y - x) * clamp(z, 0.0, 1.0)) * z;
            }
            return a * v;
        }
        // RUNTIME 4.10 step 6 (primitivevfx PS): t = saturate((a - z)/(y - z)); a' = (t^2 (3 - 2t) a)^x.
        // Without a TextureFilter the engine writes (1, 0, 0), which makes this the identity.
        float textureFilter(float a, vec3 p) {
            float t = p.y == p.z ? (a > p.z ? 1.0 : 0.0) : clamp((a - p.z) / (p.y - p.z), 0.0, 1.0);
            float b = t * t * (3.0 - 2.0 * t) * a;
            return b > 0.0 ? pow(b, p.x) : 0.0;
        }
        // TexelChannelOperator (primitivevfx_4 b013 646-861): texel intensity -> blackbody colour (Krystek),
        // hue shift, desaturation, shade colour. env = IntensityEnvelope (1 without UseIntEnvelope).
        vec4 texelChannel(vec4 tex, uvec4 p, float env) {
            float hueShift = float(p.x & 255u) / 255.0, weightG = float((p.x >> 8) & 255u);
            float desaturate = float((p.x >> 16) & 255u) / 255.0, weightRB = float(p.x >> 24);
            vec3 shadeColor = vec3(float(p.y & 255u), float((p.y >> 8) & 255u), float((p.y >> 16) & 255u)) / 255.0;
            float t = float(p.y >> 24) / 255.0;
            float rate = uintBitsToFloat(p.z);
            vec2 h = unpackHalf2x16(p.w);       // EmissivePower, Opacity
            float rb = tex.x + (tex.z - tex.x) * t;
            float s = pow(max(rb, 1e-6), h.x) * weightRB + pow(max(tex.y, 1e-6), h.x) * weightG;
            float T = env * 100.0 * s, T2 = T * T;
            float u = (0.860117757 + 1.54118254e-4 * T + 1.28641212e-7 * T2) / (1.0 + 8.42420235e-4 * T + 7.08145163e-7 * T2);
            float v = (0.317398726 + 4.22806245e-5 * T + 4.20481691e-8 * T2) / (1.0 - 2.89741816e-5 * T + 1.61456053e-7 * T2);
            float d = 2.0 * u - 8.0 * v + 4.0;
            float x = 3.0 * u / d, y = 2.0 * v / d;
            float X = x / y, Z = (1.0 - x - y) / y;
            vec3 rgb = vec3(3.2404542 * X - 1.5371385 - 0.4985314 * Z,
                            -0.9692660 * X + 1.8760108 + 0.0415560 * Z,
                            0.0556434 * X - 0.2040259 + 1.0572252 * Z);
            float k = T * 4e-4; k *= k; k *= k;
            vec3 e = floor(rate * 1e5 * rgb * k) * 1e-5;
            float mx = max(e.r, max(e.g, e.b)), mn = min(e.r, min(e.g, e.b)), dl = mx - mn;
            float sat = mx != 0.0 ? dl / mx : 0.0;
            float inv = dl == 0.0 ? 0.0 : 1.0 / dl;
            float hue = e.r == mx ? (e.g - e.b) * inv : e.g == mx ? (e.b - e.r) * inv + 2.0 : (e.r - e.g) * inv + 4.0;
            float hh = fract(hue / 6.0) + hueShift;
            vec3 q = clamp(abs(fract(vec3(hh, hh + 2.0 / 3.0, hh + 1.0 / 3.0)) * 2.0 - 1.0) * 3.0 - 1.0, 0.0, 1.0);
            vec3 c = ((q - 1.0) * sat + 1.0) * mx;
            float luma = dot(c, vec3(0.3, 0.59, 0.11));
            return vec4(c + (luma - c) * desaturate + shadeColor * rb, (tex.w + (tex.z - tex.w) * t) * h.y);
        }
        // Light reaching a lit particle (set by the caller before shade(); 1 for unlit particles).
        vec3 particleLight = vec3(1.0);
        vec3 previewLight(vec3 normal) {
            float wrap = clamp(dot(normal, lightB.xyz) * 0.5 + 0.5, 0.0, 1.0);
            return lightA.rgb + lightA.w * wrap;
        }
        float inverseAces(float y) {
            y = clamp(y, 0.0, 0.999);
            float qa = 2.51 - 2.43 * y, qb = 0.03 - 0.59 * y, qc = -0.14 * y;
            return (-qb + sqrt(qb * qb - 4.0 * qa * qc)) / (2.0 * qa);
        }
        // Returns lit in .xyz of litOut, unlit in unlitOut, layer alpha as the return value.
        float rgbCommon(vec4 tex, out vec3 litOut, out vec3 unlitOut) {
            float m = pow(max(tex.y, 1e-7), greenP.z);
            float ig = greenColor.w * m, igi = ig * greenP.x;
            float w = clamp(igi / max(greenP.y, 1e-6), 0.0, 1.0); w *= w;
            vec3 greenRgb = igi * mix(greenColor.rgb, vec3(1.0), w);
            float toB = redP.y;
            float v1 = (modes.w & 4) != 0 ? 0.0 : toB, v2 = toB;
            vec3 redRgb = (tex.x + (tex.z - tex.x) * v1) * redP.x * redColor.rgb;
            float redA = (tex.w + (tex.z - tex.w) * v2) * redColor.w;
            float alpha = clamp((ig + redA) * redP.z, 0.0, 1.0);
            if (alpha <= 1e-4) alpha = 0.0;
            bool gl = greenP.w != 0.0, rl = redP.w != 0.0;
            litOut = (gl ? greenRgb : vec3(0.0)) + (rl ? redRgb : vec3(0.0));
            unlitOut = (gl ? vec3(0.0) : greenRgb) + (rl ? vec3(0.0) : redRgb);
            return alpha;
        }
        float rgbWater(vec4 tex, out vec3 litOut) {
            float m = tex.y + (tex.z - tex.y) * waterP.z;
            litOut = waterSpec.rgb * tex.x * waterP.x + waterSheet.rgb * m * waterP.y;
            return clamp(tex.w * waterP.w * (waterSheet.w * m + waterSpec.w * tex.x), 0.0, 1.0);
        }
        vec4 shade(vec4 tex, vec4 vertex, float soft, uint acBytes) {
            int blend = modes.y;
            if (hasFlag(1024)) tex = texelChannel(tex, tcoP, 1.0);
            // With an Rgb layer the filter shapes the layer alpha instead (RUNTIME 4.9: after the layers).
            if (hasFlag(512) && !hasFlag(8) && !hasFlag(16)) tex.w = textureFilter(tex.w, textureFilterP.xyz);
            vec3 L = hasFlag(2048) ? particleLight : vec3(1.0);
            float alpha = hasFlag(4) ? alphaCorrect(tex.w, vertex.w, acBytes) : tex.w * vertex.w;
            if (hasFlag(256)) alpha *= header.w;
            vec3 rgb;
            if (hasFlag(8)) {
                vec3 lit, unlit;
                float layer = rgbCommon(tex, lit, unlit);
                alpha = (hasFlag(512) ? textureFilter(layer, textureFilterP.xyz) : layer) * vertex.w;
                lit *= L;
                rgb = blend == 1 ? (lit * shading.w + unlit) * vertex.rgb : blend == 3 ? unlit : lit + unlit;
            } else if (hasFlag(16)) {
                vec3 lit;
                float layer = rgbWater(tex, lit);
                alpha = (hasFlag(512) ? textureFilter(layer, textureFilterP.xyz) : layer) * vertex.w;
                lit *= L;
                rgb = blend == 1 ? lit * shading.w * vertex.rgb : blend == 3 ? vec3(0.0) : lit;
            } else if (blend == 3) {
                float t = clamp(tex.w / max(header.z, 1e-4), 0.0, 1.0);
                rgb = mix(colorRange.rgb * header.y, vertex.rgb * header.x, t) * tex.rgb * L;
            } else rgb = vertex.rgb * tex.rgb * header.x * L;
            alpha *= soft;
            if (blend == 4) return vec4(mix(vec3(1.0), rgb, alpha), alpha);
            if (hasFlag(128) && shading.z != 0.0) {
                vec3 inv = vec3(inverseAces(rgb.r), inverseAces(rgb.g), inverseAces(rgb.b)) / max(viewP.x, 1e-4);
                rgb = mix(rgb, inv, shading.z);
            }
            return vec4(rgb, alpha);
        }
        """;
}
