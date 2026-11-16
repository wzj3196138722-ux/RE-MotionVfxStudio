using System.Numerics;
using Silk.NET.OpenGL;
using VfxStudio.Renderer.Textures;
using Shader = VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.Re4Render;

/// <summary>An approximate EffectDecal: the game script projects the prefab onto the terrain; the preview has only the
/// flat ground at y = 0, so each live carrier particle within SearchTerrainDistance above it gets a flat quad there
/// with the prefab's colour texture. How the script sizes, rotates and fades the decal is not read from the game:
/// the size follows <see cref="Footprint"/> times a calibration factor, the roll is random within RandomRollRange,
/// and the alpha is the texture's times BaseColorMod's times the particle's Life alpha.</summary>
public sealed class PreviewDecalRenderer : IDisposable
{
    private readonly GL gl;
    private readonly Shader shader;
    private readonly uint vao, vbo;
    private readonly PreviewScene scene;
    private readonly List<(int Table, PreviewDecal Decal, GpuTexture Texture, bool Owned)> decals = [];
    private float[] vertices = [];
    private const int Floats = 9;     // position 3, uv 2, colour 4
    public int Drawn { get; private set; }

    public PreviewDecalRenderer(GL gl, PreviewScene scene, BillboardGpuCache? cache)
    {
        this.gl = gl; this.scene = scene;
        for (int i = 0; i < scene.Shown.Count; i++)
            if (scene.Shown[i].Decal is { Albedo: { } albedo } decal)
            {
                var texture = cache?.Texture(albedo) ?? GpuTexture.CreateFromTexBytes(gl, albedo.Bytes, albedo.Path + ".143221013");
                if (texture != null) decals.Add((i, decal, texture, cache == null));
            }
        shader = new Shader(gl, """
            #version 460 core
            layout(location=0) in vec3 position;
            layout(location=1) in vec2 uv;
            layout(location=2) in vec4 color;
            uniform mat4 vp;
            out vec2 vUv; out vec4 vColor;
            void main() { gl_Position = vp * vec4(position,1); vUv = uv; vColor = color; }
            """, """
            #version 460 core
            in vec2 vUv; in vec4 vColor;
            uniform sampler2D albedo;
            out vec4 color;
            void main()
            {
                vec4 t = texture(albedo, vUv);
                color = vec4(t.rgb * vColor.rgb, t.a * vColor.a);
                if (color.a < 0.002) discard;
            }
            """);
        vao = gl.GenVertexArray(); vbo = gl.GenBuffer();
        unsafe
        {
            gl.BindVertexArray(vao); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            for (uint k = 0; k < 3; k++) gl.EnableVertexAttribArray(k);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Floats * 4, (void*)0);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, Floats * 4, (void*)12);
            gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, Floats * 4, (void*)20);
            gl.BindVertexArray(0);
        }
    }

    public bool Any => decals.Count > 0;

    /// <summary>Full width and depth in metres of one decal with the given random draw <paramref name="u"/> (0..1):
    /// 2 × Extent.X / Extent.Z (Extent is a half size, measured in game: Extent 1, scale 1 covers twice a 1 m Polygon)
    /// times ScaleXY (as a random scale ScaleXY.X + u·ScaleXY.Y, or as width / depth scales), times the calibration factor.</summary>
    public static Vector2 Footprint(PreviewDecal decal, CompositionConfig config, float u = 0)
    {
        var scale = config.DecalScaleXYRandom ? new Vector2(decal.ScaleXY.X + u * decal.ScaleXY.Y) : decal.ScaleXY;
        return new Vector2(decal.Extent.X, decal.Extent.Z) * 2 * scale * config.DecalScale;
    }

    /// <summary>Decals of <paramref name="frame"/>: one per live carrier particle between the ground and
    /// SearchTerrainDistance above it, for every scene entry with a decal (texture or not).</summary>
    public static IReadOnlyList<(int Table, Vector3 Center, Vector2 Size, float Roll, Vector4 Color)> Place(PreviewScene scene, PreviewFrame frame,
        CompositionConfig config, int? soloTable = null)
    {
        var placed = new List<(int, Vector3, Vector2, float, Vector4)>();
        if (frame.EntryStates == null) return placed;
        for (int table = 0; table < scene.Shown.Count && table < frame.EntryStates.Count; table++)
        {
            if (scene.Shown[table].Decal is not { } decal || soloTable != null && table != soloTable) continue;
            var state = frame.EntryStates[table];
            for (int k = state.First; k < state.First + state.Count; k++)
            {
                var p = frame.Particles[k];
                if (!float.IsFinite(p.Position.X) || p.Position.Y < -0.05f || p.Position.Y > decal.SearchDistance) continue;
                // Per-particle draws from its id, so a decal keeps its roll and size while the particle lives.
                float u = Hash(p.Id, 1), v = Hash(p.Id, 2);
                float roll = (v * 2 - 1) * decal.RollRange * MathF.PI / 180;
                placed.Add((table, new Vector3(p.Position.X, 0.002f, p.Position.Z), Footprint(decal, config, u), roll,
                    decal.Color with { W = decal.Color.W * p.LifeAlpha / 255f }));
            }
        }
        return placed;
    }

    public unsafe void Render(PreviewFrame frame, Matrix4x4 viewProjection, CompositionConfig config, int? soloTable = null)
    {
        Drawn = 0;
        if (!config.Decals || decals.Count == 0) return;
        var placed = Place(scene, frame, config, soloTable).Where(d => decals.Any(x => x.Table == d.Table)).ToList();
        if (placed.Count == 0) return;
        if (vertices.Length < placed.Count * 6 * Floats) vertices = new float[placed.Count * 6 * Floats];
        shader.Use(); shader.SetUniform("vp", viewProjection); shader.SetUniform("albedo", 0);
        gl.Enable(EnableCap.DepthTest); gl.DepthMask(false); gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.BindVertexArray(vao); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        // One draw per decal texture, in table order.
        foreach (var group in placed.GroupBy(d => d.Table))
        {
            int n = 0;
            foreach (var (_, center, size, roll, color) in group)
            {
                var x = new Vector3(MathF.Cos(roll), 0, MathF.Sin(roll)) * size.X / 2;
                var z = new Vector3(-MathF.Sin(roll), 0, MathF.Cos(roll)) * size.Y / 2;
                (Vector3, Vector2)[] corners = [(center - x - z, new(0, 1)), (center + x - z, new(1, 1)), (center + x + z, new(1, 0)),
                    (center - x - z, new(0, 1)), (center + x + z, new(1, 0)), (center - x + z, new(0, 0))];
                foreach (var (position, uv) in corners)
                {
                    int o = n++ * Floats;
                    vertices[o] = position.X; vertices[o + 1] = position.Y; vertices[o + 2] = position.Z;
                    vertices[o + 3] = uv.X; vertices[o + 4] = uv.Y;
                    vertices[o + 5] = color.X; vertices[o + 6] = color.Y; vertices[o + 7] = color.Z; vertices[o + 8] = color.W;
                }
            }
            decals.First(d => d.Table == group.Key).Texture.Bind(TextureUnit.Texture0);
            fixed (float* ptr = vertices)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(n * Floats * 4), ptr, BufferUsageARB.StreamDraw);
            gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)n);
            Drawn += n / 6;
        }
        gl.BindVertexArray(0);
        gl.DepthMask(true);
    }

    private static float Hash(uint id, uint salt)
    {
        uint h = id * 0x9E3779B1u ^ salt * 0x85EBCA77u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    public void Dispose()
    {
        foreach (var d in decals) if (d.Owned) d.Texture.Dispose();
        gl.DeleteBuffer(vbo); gl.DeleteVertexArray(vao); shader.Dispose();
    }
}
