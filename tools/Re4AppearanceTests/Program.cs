// GPU vs C# for the RE4 preview colour chain: the billboard shaders' GLSL functions run in a compute
// shader on random inputs and are compared value by value with AppearanceReference.
// Usage: dotnet run -c Release --project tools/Re4AppearanceTests
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using VfxStudio.Re4Render;
using R = VfxStudio.Re4Render.AppearanceReference;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
int checks = 0, failures = 0;
void Check(bool ok, string name, object? detail = null)
{
    checks++;
    Console.WriteLine((ok ? "ok   " : "FAIL ") + name + (detail == null ? "" : " :: " + detail));
    if (!ok) failures++;
}

// ---- C#-only: byte packing matches the engine's truncation and wrap-around (RUNTIME 4.7).
uint b = R.PackAlphaCorrection(1, 1f, .2f, 0, 0);
Check((b & 255) == 255 && (b >> 8 & 255) == 51 && (b >> 16 & 255) == 127 && b >> 24 == 1, "AC pack mode 1: Hi 1.0 -> 255, Lo 0.2 -> 51, curve 0 -> 127", b.ToString("X8"));
b = R.PackAlphaCorrection(2, 1.01f, -.01f, 3, 0);
// 1.01 x 255.5 = 258 wraps to 2; -0.01 x 255.5 = -2.555 truncates to -2, whose low byte is 0xFE.
Check((b & 255) == 2 && (b >> 8 & 255) == 0xFE && (b >> 16 & 255) == 254, "AC pack wraps above 255 and below 0 (truncation toward zero), curve +3 -> 254", b.ToString("X8"));
b = R.PackAlphaCorrection(3, 4f, 8f, 0, 128);
Check((b & 255) == 127 && (b >> 8 & 255) == 255 && (b >> 16 & 255) == 128 && b >> 24 == 3, "AC pack mode 3: exponents x31.875, byte 2 = life", b.ToString("X8"));
foreach (float y in new[] { 0f, .01f, .2f, .5f, .9f, .99f })
    Check(MathF.Abs(R.Aces(R.InverseAces(y)) - y) < 1e-4f, $"ACES(InverseAces({y})) = {y}");

// ---- Polygon orientation: native dfae10 outputs for euler (0.4, -1.1, 2.0), RotationOrder 0..5.
float[][] native =
[
    [0.52257f, -0.134259f, 0.759181f, 0.364059f], [0.52257f, -0.4193f, 0.646969f, 0.364059f], [-0.339548f, -0.4193f, 0.759181f, 0.364059f],
    [-0.339548f, -0.4193f, 0.646969f, 0.538819f], [0.52257f, -0.134259f, 0.646969f, 0.538819f], [-0.339548f, -0.134259f, 0.759181f, 0.538819f],
];
for (uint order = 0; order < 6; order++)
{
    var q = BillboardInstance.PolygonRotation(.4f, -1.1f, 2f, order);
    var n = native[order];
    bool same = MathF.Abs(q.X - n[0]) < 1e-5f && MathF.Abs(q.Y - n[1]) < 1e-5f && MathF.Abs(q.Z - n[2]) < 1e-5f && MathF.Abs(q.W - n[3]) < 1e-5f;
    Check(same, $"Polygon quaternion, RotationOrder {order} = native dfae10", q);
}
// The vertex shader rotates with v + 2·q.xyz × (q.xyz × v + w·v); same result as System.Numerics.
{
    var q = BillboardInstance.PolygonRotation(.4f, -1.1f, 2f, 2);
    var v = new Vector3(.3f, -.7f, 0);
    var u = new Vector3(q.X, q.Y, q.Z);
    var shader = v + 2 * Vector3.Cross(u, Vector3.Cross(u, v) + q.W * v);
    Check(Vector3.Distance(shader, Vector3.Transform(v, q)) < 1e-5f, "shader quaternion rotation = Vector3.Transform");
}

// ---- GPU
var options = WindowOptions.Default with { IsVisible = false, Size = new Vector2D<int>(64, 64),
    API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6)) };
using var window = Window.Create(options);
window.Load += () =>
{
    try
    {
        var gl = GL.GetApi(window);
        RunGpu(gl);
        if (args.Contains("--corpus")) RunCorpus(gl);
    }
    catch (Exception ex) { Check(false, "GPU tests ran", ex); }
    window.Close();
};
window.Run();
Console.WriteLine($"{(failures == 0 ? "PASS" : "FAIL")}: {checks - failures}/{checks}");
return failures == 0 ? 0 : 1;

unsafe void RunGpu(GL gl)
{
    const string Compute = """
        layout(local_size_x = 64) in;
        struct Case { vec4 v0; vec4 v1; vec4 v2; vec4 v3; uvec4 u; };
        layout(std430, binding = 0) readonly buffer Cases { Case cases[]; };
        layout(std430, binding = 1) writeonly buffer Results { vec4 results[]; };
        uniform int kind;
        uniform int count;
        void main() {
            uint i = gl_GlobalInvocationID.x;
            if (i >= uint(count)) return;
            Case c = cases[i];
            vec4 r = vec4(0);
            if (kind == 0) { particleLight = c.v3.xyz; r = shade(c.v0, c.v1, c.v2.x, c.u.x); }
            else if (kind == 1) r.x = fadeDepth(c.v2.x, c.v0);
            else if (kind == 2) r.x = fadeAngle(c.v0.xyz, c.v1.xyz, c.v2.xyz, c.v3.xyz, int(c.u.x), c.v0.w, c.v1.w, c.v2.w);
            else if (kind == 3) r.x = alphaCorrect(c.v0.x, c.v0.y, c.u.x);
            else if (kind == 4) r.x = inverseAces(c.v0.x);
            else if (kind == 5) r.x = textureFilter(c.v0.x, c.v1.xyz);
            else if (kind == 6) r = texelChannel(c.v0, c.u, c.v2.x);
            else if (kind == 7) r.xyz = previewLight(c.v0.xyz);
            results[i] = r;
        }
        """;
    uint shader = gl.CreateShader(ShaderType.ComputeShader);
    gl.ShaderSource(shader, "#version 460 core\n" + AppearanceShaderLibrary.Block + "\n" + AppearanceShaderLibrary.Functions + "\n" + Compute);
    gl.CompileShader(shader);
    gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
    if (ok == 0) throw new InvalidOperationException(gl.GetShaderInfoLog(shader));
    uint program = gl.CreateProgram(); gl.AttachShader(program, shader); gl.LinkProgram(program);
    gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out ok);
    if (ok == 0) throw new InvalidOperationException(gl.GetProgramInfoLog(program));
    uint caseBuffer = gl.GenBuffer(), resultBuffer = gl.GenBuffer(), ubo = gl.GenBuffer();
    Check(true, "compute shader with the shared library compiles on " + gl.GetStringS(StringName.Renderer));

    Vector4[] Run(int kind, Case[] cases, EntryUniforms? u = null)
    {
        u ??= new EntryUniforms();
        var bytes = u.ToStd140();
        gl.BindBuffer(BufferTargetARB.UniformBuffer, ubo);
        fixed (byte* p = bytes) gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)bytes.Length, p, BufferUsageARB.DynamicDraw);
        gl.BindBufferBase(BufferTargetARB.UniformBuffer, 3, ubo);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, caseBuffer);
        fixed (Case* p = cases) gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(cases.Length * sizeof(Case)), p, BufferUsageARB.StaticDraw);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, caseBuffer);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, resultBuffer);
        gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(cases.Length * 16), null, BufferUsageARB.StaticRead);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, resultBuffer);
        gl.UseProgram(program);
        gl.Uniform1(gl.GetUniformLocation(program, "kind"), kind);
        gl.Uniform1(gl.GetUniformLocation(program, "count"), cases.Length);
        gl.DispatchCompute((uint)(cases.Length + 63) / 64, 1, 1);
        gl.MemoryBarrier(MemoryBarrierMask.BufferUpdateBarrierBit);
        var results = new Vector4[cases.Length];
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, resultBuffer);
        fixed (Vector4* p = results) gl.GetBufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(cases.Length * 16), p);
        return results;
    }

    var rng = new Random(20261115);
    float F(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);
    Vector3 Dir() { var v = new Vector3(F(-1, 1), F(-1, 1), F(-1, 1)); return v.LengthSquared() < 1e-4f ? Vector3.UnitY : Vector3.Normalize(v); }
    static bool Close(float a, float e, float tol = 2e-4f) => float.IsFinite(a) && MathF.Abs(a - e) <= tol * MathF.Max(1, MathF.Abs(e));
    void Compare(string name, Vector4[] gpu, Func<int, Vector4> expected, int components, Func<int, bool>? skip = null, float tol = 2e-4f)
    {
        int bad = 0, compared = 0; string? first = null;
        for (int i = 0; i < gpu.Length; i++)
        {
            if (skip?.Invoke(i) == true) continue;
            compared++;
            var e = expected(i);
            bool same = Close(gpu[i].X, e.X, tol) && (components < 2 || Close(gpu[i].Y, e.Y, tol) && Close(gpu[i].Z, e.Z, tol)) && (components < 4 || Close(gpu[i].W, e.W, tol));
            if (!same) { bad++; first ??= $"case {i}: gpu {gpu[i]} vs C# {e}"; }
        }
        Check(bad == 0 && compared > gpu.Length / 2, $"{name}: {compared} cases equal", first);
    }

    const int N = 4096;
    // FadeByDepth (RUNTIME 4.6)
    var cases = new Case[N];
    for (int i = 0; i < N; i++)
    {
        float ns = F(-1, 5), ne = ns + F(.01f, 5), fs = ne + F(0, 20), fe = fs + F(.01f, 20);
        cases[i] = new Case { V0 = new(ns, ne, fs, fe), V2 = new(F(-2, 60), 0, 0, 0) };
    }
    var gpu = Run(1, cases);
    Compare("fadeDepth", gpu, i => new(R.FadeDepth(cases[i].V2.X, cases[i].V0), 0, 0, 0), 1);

    // FadeByAngle, all 8 flag combinations
    for (int i = 0; i < N; i++)
        cases[i] = new Case { V0 = new(Dir(), F(0, 2)), V1 = new(F(-2, 2), F(-2, 2), F(-2, 2), F(0, 2)), V2 = new(F(-2, 2), F(-2, 2), F(-2, 2), F(0, 1)),
            V3 = new(F(-6, 6), F(1, 6), F(-6, 6), 0), U = new() { X = (uint)(i & 7) } };
    gpu = Run(2, cases);
    Compare("fadeAngle", gpu, i => new(R.FadeAngle(new(cases[i].V0.X, cases[i].V0.Y, cases[i].V0.Z), new(cases[i].V1.X, cases[i].V1.Y, cases[i].V1.Z),
        new(cases[i].V2.X, cases[i].V2.Y, cases[i].V2.Z), new(cases[i].V3.X, cases[i].V3.Y, cases[i].V3.Z), cases[i].U.X, cases[i].V0.W, cases[i].V1.W, cases[i].V2.W), 0, 0, 0), 1, tol: 1e-3f);

    // AlphaCorrection, modes 0-3
    for (int i = 0; i < N; i++)
    {
        uint mode = (uint)(i & 3);
        cases[i] = new Case { V0 = new(F(0, 1), F(0, 1), 0, 0), U = new() { X = R.PackAlphaCorrection(mode, F(0, mode == 3 ? 4 : 1), F(0, mode == 3 ? 4 : 1), F(-3, 3), (byte)rng.Next(256)) } };
    }
    gpu = Run(3, cases);
    // Mode 1 becomes a hard threshold when Lo - T <= 1e-6 (division by 1e-6): skip texels within 1e-3 of it.
    bool NearThreshold(int i)
    {
        uint u = cases[i].U.X; if ((u >> 24 & 3) != 1) return false;
        float hi = (u & 255) / 255f, lo = (u >> 8 & 255) / 255f, s = ((u >> 16 & 255) - 127f) * 0.2141272f, v = cases[i].V0.Y;
        float f = MathF.Abs(s) < 1e-4f ? v : R.Saturate((1 - MathF.Exp(-s * v)) / (1 - MathF.Exp(-s)));
        float t = 1 - hi * f;
        return lo - t < 1e-2f && MathF.Abs(cases[i].V0.X - t) < 2e-2f;
    }
    Compare("alphaCorrect", gpu, i => new(R.AlphaCorrect(cases[i].V0.X, cases[i].V0.Y, cases[i].U.X), 0, 0, 0), 1, NearThreshold, 2e-3f);

    // Inverse ACES
    for (int i = 0; i < N; i++) cases[i] = new Case { V0 = new(F(0, 1.2f), 0, 0, 0) };
    gpu = Run(4, cases);
    Compare("inverseAces", gpu, i => new(R.InverseAces(cases[i].V0.X), 0, 0, 0), 1, tol: 1e-3f);

    // TextureFilter (corpus-like ranges, including threshold == min)
    for (int i = 0; i < N; i++)
    {
        float min = F(0, .5f), thr = (i & 7) == 0 ? min : min + F(0, .8f);
        cases[i] = new Case { V0 = new(F(0, 1), 0, 0, 0), V1 = new(F(.05f, 2), thr, min, 0) };
    }
    gpu = Run(5, cases);
    Compare("textureFilter", gpu, i => new(R.TextureFilter(cases[i].V0.X, new(cases[i].V1.X, cases[i].V1.Y, cases[i].V1.Z)), 0, 0, 0), 1, tol: 2e-3f);

    // TexelChannelOperator: the packed constant of bluefire (re4.exe, native) and random packings
    uint[] Tco(int i)
    {
        if (i % 8 == 0) return [0x01d119b7u, 0x00301308u, 0x40400000u, 0x3c003c00u];
        uint b() => (uint)rng.Next(256);
        uint h(float v) => BitConverter.HalfToUInt16Bits((Half)v);
        return [b() | (uint)rng.Next(40) << 8 | b() << 16 | (uint)rng.Next(10) << 24, b() | b() << 8 | b() << 16 | b() << 24,
            BitConverter.SingleToUInt32Bits(F(.5f, 10)), h(F(.5f, 5)) | h(F(0, 1)) << 16];
    }
    for (int i = 0; i < N; i++)
    {
        var w = Tco(i);
        cases[i] = new Case { V0 = new(F(0, 1), F(0, 1), F(0, 1), F(0, 1)), V2 = new(i % 4 == 0 ? 1 : F(0, 1), 0, 0, 0), U = new() { X = w[0], Y = w[1], Z = w[2], W = w[3] } };
    }
    gpu = Run(6, cases);
    Compare("texelChannel", gpu, i => R.TexelChannel(cases[i].V0, [cases[i].U.X, cases[i].U.Y, cases[i].U.Z, cases[i].U.W], cases[i].V2.X), 4, tol: 5e-3f);

    // Preview light
    var lightU = new EntryUniforms { Ambient = new(.3f, .35f, .4f), KeyLight = .8f, LightDirection = Vector3.Normalize(new(.4f, 1, .3f)) };
    for (int i = 0; i < N; i++) cases[i] = new Case { V0 = new(Dir(), 0) };
    gpu = Run(7, cases, lightU);
    Compare("previewLight", gpu, i => new(R.PreviewLight(new(cases[i].V0.X, cases[i].V0.Y, cases[i].V0.Z), lightU), 0), 3);

    // shade() for every BlendType with each colour-chain variant
    var header = (int blend) => new ColorHeader(blend, F(0, 10), F(0, 8), new Vector4(F(0, 1), F(0, 1), F(0, 1), 1), F(0, 4), F(0, 2));
    var common = new RgbCommonSettings(5, new(.9f, .6f, .2f, .8f), 3, 1.5f, 1.2f, new(.4f, .1f, .05f, .7f), 2, .4f, 1.3f, true, false);
    var water = new RgbWaterSettings(new(1, .9f, .8f, .6f), 4, new(.3f, .5f, .7f, .9f), 1.5f, .3f, .8f);
    var variants = new (string Name, EntryAppearance Appearance, CompositionConfig Config)[]
    {
        ("plain", EntryAppearance.None, new CompositionConfig()),
        ("AlphaCorrection 1", EntryAppearance.None with { AlphaCorrection = new(1, R.PackAlphaCorrection(1, 1, .22f, .8f, 0)) }, new CompositionConfig()),
        ("AlphaCorrection 2", EntryAppearance.None with { AlphaCorrection = new(2, R.PackAlphaCorrection(2, .9f, .1f, 0, 0)) }, new CompositionConfig()),
        ("RgbCommon", EntryAppearance.None with { RgbCommon = common }, new CompositionConfig()),
        ("RgbWater", EntryAppearance.None with { RgbWater = water }, new CompositionConfig()),
        ("Detonemap 1", EntryAppearance.None with { DetonemapRatio = 1 }, new CompositionConfig()),
        ("Detonemap 0.3 + AlphaRate", EntryAppearance.None with { DetonemapRatio = .3f }, new CompositionConfig { AlphaRate = true }),
        ("TextureFilter", EntryAppearance.None with { TextureFilter = new(.56f, .16f, .07f) }, new CompositionConfig()),
        ("TextureFilter + RgbCommon", EntryAppearance.None with { TextureFilter = new(1.36f, .18f, 0), RgbCommon = common }, new CompositionConfig()),
        ("TextureFilter + AlphaCorrection 2", EntryAppearance.None with { TextureFilter = new(.5f, 0, 0), AlphaCorrection = new(2, R.PackAlphaCorrection(2, .9f, .1f, 0, 0)) }, new CompositionConfig()),
        ("lit", EntryAppearance.None with { ParticleLighting = 2 }, new CompositionConfig()),
        ("lit + RgbCommon", EntryAppearance.None with { ParticleLighting = 1, RgbCommon = common }, new CompositionConfig()),
        ("TexelChannel", EntryAppearance.None, new CompositionConfig()),
    };
    foreach (var (name, appearance, config) in variants)
        for (int blend = 0; blend <= 4; blend++)
        {
            var u = EntryUniforms.Build(header(blend), appearance, config, Vector3.Zero);
            u.Exposure = F(.5f, 2);
            if (name == "TexelChannel") { u.TexelChannel = [0x01d119b7u, 0x00301308u, 0x40400000u, 0x3c003c00u]; u.Flags |= EntryUniforms.TexelChannelBit; }
            for (int i = 0; i < N; i++)
                cases[i] = new Case { V0 = new(F(0, 1), F(0, 1), F(0, 1), F(0, 1)), V1 = new(F(0, 1), F(0, 1), F(0, 1), F(0, 1)), V2 = new(F(0, 1), 0, 0, 0),
                    V3 = new(F(0, 2), F(0, 2), F(0, 2), 0), U = new() { X = u.AcBytes } };
            gpu = Run(0, cases, u);
            Compare($"shade {name}, blend {blend}", gpu, i => R.Shade(cases[i].V0, cases[i].V1, cases[i].V2.X, cases[i].U.X, u, new(cases[i].V3.X, cases[i].V3.Y, cases[i].V3.Z)), 4,
                u.Has(EntryUniforms.AlphaCorrectionBit) ? NearShadeThreshold : null, 2e-3f);

            bool NearShadeThreshold(int i)
            {
                uint x = u.AcBytes; if ((x >> 24 & 3) != 1) return false;
                float hi = (x & 255) / 255f, lo = (x >> 8 & 255) / 255f, s = ((x >> 16 & 255) - 127f) * 0.2141272f, v = cases[i].V1.W;
                float f = MathF.Abs(s) < 1e-4f ? v : R.Saturate((1 - MathF.Exp(-s * v)) / (1 - MathF.Exp(-s)));
                float t = 1 - hi * f;
                return lo - t < 1e-2f && MathF.Abs(cases[i].V0.W - t) < 2e-2f;
            }
        }
}

unsafe void RunCorpus(GL gl)
{
    string exe = Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
    string game = Path.GetDirectoryName(exe)!, stm = Path.Combine(game, "RE4_EXTRACT", "natives", "stm");
    string repo = AppContext.BaseDirectory;
    while (!File.Exists(Path.Combine(repo, "tools", "Re4Native", "re4host.py"))) repo = Path.GetDirectoryName(repo.TrimEnd(Path.DirectorySeparatorChar))!;
    using var resources = new Re4ResourceSource(game, Path.Combine(game, "RE4_EXTRACT"));
    using var client = new NativePreviewClient("python", Path.Combine(repo, "tools", "Re4Native", "re4host.py"), exe);
    using var hdr = new PreviewHdrPipeline(gl, 640, 360);
    using var cache = new BillboardGpuCache(gl);
    var view = Matrix4x4.CreateLookAt(new Vector3(0, 1.5f, 6), new Vector3(0, .4f, 0), Vector3.UnitY);
    var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, 640f / 360, .05f, 100);
    var context = new PreviewRenderContext(new CompositionConfig(), 1, .05f, 100, hdr.OpaqueDepthTexture);
    var files = new (string Label, string Path)[]
    {
        ("25 entries (sm82_320)", "_anotherorder/vfx/effecteditor/efd_setmodel/efd_sm82_320_00/efd_0015_sm82_320_00_0000.efx.3539837"),
        ("256 entries (laser)", "_anotherorder/vfx/effecteditor/efd_event/cs/efd_cse460_s00/efd_0015_cse460_s00_laser_0001.efx.3539837"),
        ("208 entries (sm84_335)", "_anotherorder/vfx/effecteditor/efd_setmodel/efd_sm84_335_00/efd_0015_sm84_335_00_0000.efx.3539837"),
        ("89 entries (sm82_614)", "_chainsaw/vfx/effecteditor/efd_setmodel/efd_sm82_614_00/efd_0015_sm82_614_00_0000.efx.3539837"),
        ("96 entries (sm84_785)", "_chainsaw/vfx/effecteditor/efd_setmodel/efd_sm84_785_00/efd_0015_sm84_785_00_0001.efx.3539837"),
        ("Multiply", "_chainsaw/vfx/effecteditor/efd_character/efd_cha1/efd_0015_cha1_hold_dead_0001.efx.3539837"),
        ("EdgeBlend", "_anotherorder/vfx/effecteditor/efd_event/cs/efd_cse440_s00/efd_0015_cse440_s00_smoke_0000.efx.3539837"),
        ("AddContrast", "blend2"),
        ("AlphaCorrection", "re_engine_library/vfx_library/effecteditor/efd_spark/efd_capcom_spark_loop00_l_0000.efx.3539837"),
        ("FadeByDepth", "fadeByDepth"),
        ("FadeByAngle", "_anotherorder/vfx/effecteditor/efd_character/efd_ao_chfa/efd_0015_ao_chfa_0001_eyelight_0000.efx.3539837"),
        ("RgbCommon", "_chainsaw/vfx/effecteditor/efd_event/cs/efd_csa075_s00/efd_0015_csa075_s00_fire_0100.efx.3539837"),
        ("RgbWater", "_anotherorder/vfx/effecteditor/efd_event/cs/efd_cse150_s00/efd_0015_cse150_s00_fire_0000.efx.3539837"),
        ("Polygon", "polygon"),
        ("Billboard2D", "billboard2d"),
        ("RibbonFollow", "_chainsaw/vfx/effecteditor/efd_weapon/efd_wp5837/efd_0015_wp5837_explosion_0000.efx.3539837"),
        ("RibbonFollow + RibbonLength (spark)", "re_engine_library/vfx_library/effecteditor/efd_spark/efd_capcom_spark_loop00_l_0000.efx.3539837"),
    };
    // Candidates for features whose first sample file the emulator cannot run: the first file where an
    // entry with the feature is actually shown is used.
    var candidates = new Dictionary<string, (string[] Files, Func<PreviewSceneEntry, bool> Has)>
    {
        ["blend2"] = (["_chainsaw/vfx/effecteditor/efd_weapon/efd_wp4900/efd_0015_wp4900_explosion_0001.efx.3539837",
            "_anotherorder/vfx/effecteditor/efd_event/cs/efd_cse550_s00/efd_0015_cse550_s00_wind_0000.efx.3539837",
            "_anotherorder/vfx/effecteditor/efd_stage/efd_st44/efd_st44_101/efd_0015_st44_101_1051.efx.3539837",
            "_anotherorder/vfx/effecteditor/efd_stage/efd_st45/efd_st45_300/efd_0015_st45_300_0000.efx.3539837",
            "_anotherorder/vfx/effecteditor/efd_setmodel/efd_sm84_363_00/efd_0015_sm84_363_00_0000.efx.3539837"],
            e => (e.Document!.TypeFlags & 15) == 2),
        ["polygon"] = (["_anotherorder/vfx/effecteditor/efd_character/efd_ao_chfb/efd_0015_ao_chfb_0000_footstep_0000.efx.3539837"],
            e => e.Document!.Kind == PreviewTypeKind.Polygon),
        ["billboard2d"] = (["_anotherorder/vfx/effecteditor/efd_ui/efd_ui1010/efd_0015_ui1010_firedust_0000.efx.3539837",
            "_chainsaw/ui/ui_effect/cs_ui_smoke_full.efx.3539837", "_chainsaw/ui/ui_effect/cs_ui_maskeffect.efx.3539837"],
            e => e.Document!.Kind == PreviewTypeKind.Billboard2D),
        ["fadeByDepth"] = (["_anotherorder/vfx/effecteditor/efd_character/efd_ao_chd3/efd_0015_ao_chd3_fall_to_water_0000.efx.3539837",
            "_anotherorder/vfx/effecteditor/efd_character/efd_ao_chd7/efd_0015_ao_chd7_downsmoke_0000.efx.3539837",
            "_anotherorder/vfx/effecteditor/efd_character/efd_ao_chf9/efd_0015_ao_chf9_down_0000.efx.3539837",
            "_anotherorder/vfx/effecteditor/efd_character/efd_ao_chf9/efd_0015_ao_chf9_footstep_0000.efx.3539837",
            "_anotherorder/vfx/effecteditor/efd_character/efd_ao_chc0/efd_0015_ao_chc0_grapple_0th_com_1200_adafatal_0000.efx.3539837"],
            e => EntryAppearance.From(e.Document!.Entry).FadeDepth != null),
    };
    // .tex files without an image header table (mip 0 right after the mip table) used to decode as a
    // tiny transparent "image" or throw; both must decode at their real size.
    foreach (var (texPath, w, h) in new[] { ("RE_ENGINE_LIBRARY/VFX_Library/Texture/TEX_Spark/tex_capcom_spark_00_0023_ALPG.tex", 1024, 512),
        ("RE_ENGINE_LIBRARY/VFX_Library/Texture/TEX_Smoke/tex_capcom_smoke_00_0050_ALPG.tex", 1024, 1024) })
    {
        var bytes = resources.Read(texPath, ".143221013").Bytes;
        using var t = VfxStudio.Renderer.Textures.GpuTexture.CreateFromTexBytes(gl, bytes, texPath + ".143221013");
        Check(t != null && t.Width == w && t.Height == h, $"{Path.GetFileName(texPath)} decodes at {w}x{h}", t == null ? "null" : $"{t.Width}x{t.Height}");
    }
    var pixels = new float[640 * 360 * 4];
    foreach (var (label, item) in files)
    {
        var excluded = new Dictionary<int, string>();
        string path = "";
        try
        {
            PreviewScene? scene = null;
            if (candidates.TryGetValue(item, out var pick))
            {
                foreach (var f in pick.Files)
                {
                    path = Path.Combine(stm, f.Replace('/', Path.DirectorySeparatorChar)); excluded = new();
                    try { scene = PreviewSceneLoader.LoadAsync(client, path, resources, excluded).GetAwaiter().GetResult(); }
                    catch (NotSupportedException) { scene = null; continue; }
                    if (scene.Shown.Any(pick.Has)) break;
                    scene = null;
                }
                if (scene == null) { Console.WriteLine($"SKIP corpus {label}: no candidate file has a runnable entry with this feature"); continue; }
                Console.WriteLine($"     {label} sample: {Path.GetFileName(path)}");
            }
            else
            {
                path = Path.Combine(stm, item.Replace('/', Path.DirectorySeparatorChar));
                scene = PreviewSceneLoader.LoadAsync(client, path, resources, excluded).GetAwaiter().GetResult();
            }
            using var renderer = new PreviewSceneRenderer(gl, scene, cache);
            bool finite = true; int lit = 0, drawn = 0; var blends = new SortedSet<int>();
            foreach (int tick in new[] { 30, 120, 300 })
            {
                PreviewFrame frame;
                while (true)
                {
                    try { frame = client.FrameAsync(tick).GetAwaiter().GetResult(); break; }
                    catch (Exception error) when (PreviewSceneLoader.ExcludeFailedEntry(scene, error, excluded, tick) != null)
                    { scene = PreviewSceneLoader.LoadAsync(client, path, resources, excluded).GetAwaiter().GetResult(); }
                }
                using (frame)
                {
                    using var current = scene == renderer.Scene ? null : new PreviewSceneRenderer(gl, scene, cache);
                    var r = current ?? renderer;
                    hdr.BeginScene(0, 0, 0, 1);
                    hdr.CopyOpaqueDepth();
                    r.Render(frame, view, projection, context);
                    drawn = Math.Max(drawn, r.Drawn);
                    foreach (var st in frame.EntryStates!) blends.Add(st.Blend);
                    gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
                    fixed (float* p = pixels) gl.ReadPixels(0, 0, 640, 360, PixelFormat.Rgba, PixelType.Float, p);
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        if (!float.IsFinite(pixels[i]) || !float.IsFinite(pixels[i + 1]) || !float.IsFinite(pixels[i + 2])) finite = false;
                        else if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 1e-4f) lit++;
                    }
                }
            }
            int nativeOut = excluded.Count(p => p.Value.StartsWith("原生模拟器"));
            Check(finite && drawn > 0 && gl.GetError() == GLEnum.NoError, $"corpus {label}: {scene.Shown.Count}/{scene.Entries.Count} entries drawn without NaN/Inf",
                $"blends {string.Join(",", blends)}, lit pixels {lit}, native exclusions {nativeOut}" + string.Concat(excluded.Where(p => p.Value.StartsWith("原生模拟器")).Take(3).Select(p => $"; [{p.Key}] {p.Value[..Math.Min(110, p.Value.Length)]}")));
        }
        catch (NotSupportedException ex) when (excluded.Count > 0 && excluded.Values.All(v => v.StartsWith("原生模拟器")) || ex.Message.Contains("原生模拟器"))
        { Console.WriteLine($"SKIP corpus {label}: every drawable entry fails in the native emulator :: {ex.Message[..Math.Min(200, ex.Message.Length)]}"); }
        catch (Exception ex) { Check(false, $"corpus {label}", ex.GetBaseException().Message); }
    }
}

[StructLayout(LayoutKind.Sequential)]
struct UVec4 { public uint X, Y, Z, W; }
[StructLayout(LayoutKind.Sequential)]
struct Case { public Vector4 V0, V1, V2, V3; public UVec4 U; }
