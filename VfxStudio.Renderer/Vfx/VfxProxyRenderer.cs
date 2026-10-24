using System.Numerics;
using Silk.NET.OpenGL;
using VfxStudio.Core.Models;
using VfxStudio.Renderer.Animation;
using VfxStudio.Renderer.Meshes;
using VfxStudio.Renderer.Shaders;
using VfxStudio.Renderer.Textures;
using Shader = VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.Renderer.Vfx;

public enum EmitterShapeType
{
    Point,
    Box,
    Sphere,
    Cylinder,
    Cone
}

public readonly record struct PreviewEffectRenderItem(
    string Id, string Path, ReeLib.EfxFile Efx, float StartFrame, float EndFrame);

public class VfxProxyRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _vfxShader;
    private readonly Shader _wireframeShader;

    // Billboard Quad VAO & VBO (DynamicDraw for UVSequence sub-rect updates)
    private uint _quadVao;
    private uint _quadVbo;

    // Wireframe Shapes VAO & VBO
    private uint _wireframeVao;
    private uint _wireframeVbo;

    // Trail Ribbon Dynamic VAO & VBO
    private uint _trailVao;
    private uint _trailVbo;
    private const int MaxTrailVertices = 256;

    // Default white particle texture
    private readonly GpuTexture _defaultParticleTex;

    // Companion asset cache (.uvs, .tex, .mesh, .mdf2) & deterministic EfxSimulator pool
    public VfxAssetCache? AssetCache { get; private set; }
    private readonly Dictionary<string, EfxSimulator> _slotSimulators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EfxSimulator> _previewSimulators = new(StringComparer.OrdinalIgnoreCase);

    // Transform Gizmo hook for ImGuizmo (Phase 4 integration)
    public Matrix4x4 SelectedTransform = Matrix4x4.Identity;

    public VfxProxyRenderer(GL gl, string? assetsDir = null)
    {
        _gl = gl;
        _vfxShader = new Shader(_gl, ShaderSources.VfxVertexShader, ShaderSources.VfxFragmentShader);
        _wireframeShader = new Shader(_gl, ShaderSources.WireframeVertexShader, ShaderSources.WireframeFragmentShader);

        _defaultParticleTex = GpuTexture.CreateSolidColor(_gl, 255, 255, 255, 255);

        if (!string.IsNullOrEmpty(assetsDir))
        {
            AssetCache = new VfxAssetCache(_gl, assetsDir);
        }

        SetupQuadGeometry();
        SetupWireframeGeometry();
        SetupTrailGeometry();
    }

    public void InitializeAssetCache(string assetsDir)
    {
        AssetCache?.Dispose();
        AssetCache = new VfxAssetCache(_gl, assetsDir);
    }

    public void ClearSimulators()
    {
        _slotSimulators.Clear();
        _previewSimulators.Clear();
    }

    public void ClearPreviewSimulators() => _previewSimulators.Clear();

    public void RenderPreviewEffects(
        IReadOnlyList<PreviewEffectRenderItem> effects,
        float currentFrame,
        BoneAnimationEvaluator hunterEval,
        Weapons.WeaponAttachmentManager? weaponManager,
        Shader meshShader,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPos)
    {
        if (effects.Count == 0 || AssetCache == null) return;

        Matrix4x4 attachment = Matrix4x4.Identity;
        if (weaponManager?.Parts.Count > 0)
            attachment = weaponManager.GetTargetBoneWorldMatrix(weaponManager.Parts[0], hunterEval);
        if (attachment == Matrix4x4.Identity)
            attachment = hunterEval.GetBoneWorldMatrix("R_Hand_HJ_01");
        if (attachment == Matrix4x4.Identity)
            attachment = hunterEval.GetBoneWorldMatrix("R_Hand");

        foreach (var effect in effects)
        {
            if (currentFrame < effect.StartFrame || currentFrame > effect.EndFrame)
            {
                if (_previewSimulators.TryGetValue(effect.Id, out var inactive) && inactive.CurrentSimFrame >= 0)
                    inactive.Reset(Matrix4x4.Identity);
                continue;
            }

            if (!_previewSimulators.TryGetValue(effect.Id, out var sim) ||
                !ReferenceEquals(sim.Efx, effect.Efx))
            {
                ulong seed = unchecked((ulong)effect.Id.GetHashCode(StringComparison.OrdinalIgnoreCase));
                sim = new EfxSimulator(effect.Path, effect.Efx, AssetCache, seed);
                _previewSimulators[effect.Id] = sim;
            }

            int localFrame = Math.Max(0, (int)MathF.Floor(currentFrame - effect.StartFrame));
            sim.StepToFrame(localFrame, attachment);
            if (sim.HasActiveParticles)
                sim.Render(_gl, _vfxShader, meshShader, _defaultParticleTex,
                    _quadVao, _quadVbo, _trailVao, _trailVbo, MaxTrailVertices,
                    attachment, view, projection, cameraPos);
        }
    }

    public (int Simulators, int Active) GetPreviewSimulationState() =>
        (_previewSimulators.Count, _previewSimulators.Values.Count(sim => sim.HasActiveParticles));

    private unsafe void SetupQuadGeometry()
    {
        // Centered quad: X, Y, Z, U, V, R, G, B, A
        float[] quadVerts = [
            // Pos              // UV       // Color
            -0.5f, -0.5f, 0.0f,  0.0f, 0.0f, 1f, 1f, 1f, 1f,
             0.5f, -0.5f, 0.0f,  1.0f, 0.0f, 1f, 1f, 1f, 1f,
             0.5f,  0.5f, 0.0f,  1.0f, 1.0f, 1f, 1f, 1f, 1f,

            -0.5f, -0.5f, 0.0f,  0.0f, 0.0f, 1f, 1f, 1f, 1f,
             0.5f,  0.5f, 0.0f,  1.0f, 1.0f, 1f, 1f, 1f, 1f,
            -0.5f,  0.5f, 0.0f,  0.0f, 1.0f, 1f, 1f, 1f, 1f,
        ];

        _quadVao = _gl.GenVertexArray();
        _quadVbo = _gl.GenBuffer();

        _gl.BindVertexArray(_quadVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);

        fixed (float* v = quadVerts)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quadVerts.Length * sizeof(float)), v, BufferUsageARB.DynamicDraw);
        }

        int stride = 9 * sizeof(float);
        // Pos
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
        // UV
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
        // Color
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(5 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    private unsafe void SetupWireframeGeometry()
    {
        _wireframeVao = _gl.GenVertexArray();
        _wireframeVbo = _gl.GenBuffer();
    }

    private unsafe void SetupTrailGeometry()
    {
        _trailVao = _gl.GenVertexArray();
        _trailVbo = _gl.GenBuffer();

        _gl.BindVertexArray(_trailVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(MaxTrailVertices * 9 * sizeof(float)), null, BufferUsageARB.DynamicDraw);

        int stride = 9 * sizeof(float);
        // Pos
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
        // UV
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
        // Color
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(5 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Renders active VFX slots at the current animation frame.
    /// Evaluates attachment bone matrices, renders wireframe bounds, particle billboards, and blade ribbons.
    /// </summary>
    public void RenderActiveSlots(
        IEnumerable<VfxSlot> slots,
        float currentFrame,
        BoneAnimationEvaluator hunterEval,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPos,
        int selectedSlotId = -1)
    {
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); // Additive blend for luminous VFX

        foreach (var slot in slots)
        {
            if (currentFrame < slot.StartFrame || currentFrame > slot.EndFrame)
            {
                continue;
            }

            // Calculate progress (0.0 -> 1.0) through slot lifecycle
            float duration = Math.Max(slot.EndFrame - slot.StartFrame, 1f);
            float progress = (currentFrame - slot.StartFrame) / duration;

            // 1. Calculate attachment world matrix
            Matrix4x4 boneMatrix = Matrix4x4.Identity;
            if (slot.JointHash != 0)
            {
                boneMatrix = hunterEval.GetBoneWorldMatrixByHash(slot.JointHash);
            }
            if (boneMatrix == Matrix4x4.Identity && !string.IsNullOrEmpty(slot.JointName))
            {
                boneMatrix = hunterEval.GetBoneWorldMatrix(slot.JointName);
            }

            // If still identity, try common weapon attachment bone (e.g. R_Weapon) or shoulder fallback
            if (boneMatrix == Matrix4x4.Identity)
            {
                boneMatrix = hunterEval.GetBoneWorldMatrix("R_Weapon");
                if (boneMatrix == Matrix4x4.Identity)
                {
                    var shoulder = hunterEval.GetBoneWorldMatrix("R_Shoulder");
                    if (shoulder != Matrix4x4.Identity)
                    {
                        boneMatrix = Matrix4x4.CreateTranslation(0.2f, -0.45f, 0.2f) * shoulder;
                    }
                }
            }

            // Local translation offset from VfxSlot
            Matrix4x4 offsetMatrix = Matrix4x4.CreateTranslation(slot.OffsetX, slot.OffsetY, slot.OffsetZ);
            Matrix4x4 slotWorldMatrix = offsetMatrix * boneMatrix;

            if (slot.SlotId == selectedSlotId)
            {
                SelectedTransform = slotWorldMatrix;
            }

            Vector3 worldPos = slotWorldMatrix.Translation;

            // 2. Render Wireframe Spatial Bounds & Coordinate Axes
            DrawCoordinateAxes(worldPos, view, projection, 0.4f);
            DrawWireframeSphere(worldPos, 0.35f, view, projection, new Vector4(1.0f, 0.5f, 0.1f, 0.8f));

            // 3. Render HDR Luminous Particle Clusters (Sparks / Fire / Light)
            DrawParticleCluster(worldPos, progress, view, projection, cameraPos);

            // 4. Render Slash Blade Ribbon Trail
            DrawSlashTrail(worldPos, progress, view, projection);
        }

        _gl.Disable(EnableCap.Blend);
    }

    private unsafe void DrawCoordinateAxes(Vector3 origin, Matrix4x4 view, Matrix4x4 projection, float length)
    {
        _wireframeShader.Use();
        _wireframeShader.SetMatrix4("u_Model", Matrix4x4.Identity);
        _wireframeShader.SetMatrix4("u_View", view);
        _wireframeShader.SetMatrix4("u_Projection", projection);

        float[] axisVertices = [
            // X (Red)
            origin.X, origin.Y, origin.Z,
            origin.X + length, origin.Y, origin.Z,
            // Y (Green)
            origin.X, origin.Y, origin.Z,
            origin.X, origin.Y + length, origin.Z,
            // Z (Blue)
            origin.X, origin.Y, origin.Z,
            origin.X, origin.Y, origin.Z + length,
        ];

        _gl.BindVertexArray(_wireframeVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _wireframeVbo);

        fixed (float* v = axisVertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(axisVertices.Length * sizeof(float)), v, BufferUsageARB.DynamicDraw);
        }

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

        _gl.LineWidth(1.0f);

        // Draw X
        _wireframeShader.SetUniform("u_Color", new Vector4(1f, 0.2f, 0.2f, 1f));
        _gl.DrawArrays(PrimitiveType.Lines, 0, 2);

        // Draw Y
        _wireframeShader.SetUniform("u_Color", new Vector4(0.2f, 1f, 0.2f, 1f));
        _gl.DrawArrays(PrimitiveType.Lines, 2, 2);

        // Draw Z
        _wireframeShader.SetUniform("u_Color", new Vector4(0.2f, 0.4f, 1f, 1f));
        _gl.DrawArrays(PrimitiveType.Lines, 4, 2);

        _gl.BindVertexArray(0);
    }

    public unsafe void DrawWireframeSphere(Vector3 center, float radius, Matrix4x4 view, Matrix4x4 projection, Vector4 color)
    {
        _wireframeShader.Use();
        _wireframeShader.SetMatrix4("u_Model", Matrix4x4.Identity);
        _wireframeShader.SetMatrix4("u_View", view);
        _wireframeShader.SetMatrix4("u_Projection", projection);
        _wireframeShader.SetUniform("u_Color", color);

        const int segments = 24;
        List<float> verts = new();

        // XY circle
        for (int i = 0; i < segments; i++)
        {
            float theta1 = (float)(i * 2.0 * Math.PI / segments);
            float theta2 = (float)((i + 1) * 2.0 * Math.PI / segments);

            verts.Add(center.X + radius * MathF.Cos(theta1));
            verts.Add(center.Y + radius * MathF.Sin(theta1));
            verts.Add(center.Z);

            verts.Add(center.X + radius * MathF.Cos(theta2));
            verts.Add(center.Y + radius * MathF.Sin(theta2));
            verts.Add(center.Z);
        }

        // XZ circle
        for (int i = 0; i < segments; i++)
        {
            float theta1 = (float)(i * 2.0 * Math.PI / segments);
            float theta2 = (float)((i + 1) * 2.0 * Math.PI / segments);

            verts.Add(center.X + radius * MathF.Cos(theta1));
            verts.Add(center.Y);
            verts.Add(center.Z + radius * MathF.Sin(theta1));

            verts.Add(center.X + radius * MathF.Cos(theta2));
            verts.Add(center.Y);
            verts.Add(center.Z + radius * MathF.Sin(theta2));
        }

        // YZ circle
        for (int i = 0; i < segments; i++)
        {
            float theta1 = (float)(i * 2.0 * Math.PI / segments);
            float theta2 = (float)((i + 1) * 2.0 * Math.PI / segments);

            verts.Add(center.X);
            verts.Add(center.Y + radius * MathF.Cos(theta1));
            verts.Add(center.Z + radius * MathF.Sin(theta1));

            verts.Add(center.X);
            verts.Add(center.Y + radius * MathF.Cos(theta2));
            verts.Add(center.Z + radius * MathF.Sin(theta2));
        }

        float[] arr = verts.ToArray();

        _gl.BindVertexArray(_wireframeVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _wireframeVbo);
        fixed (float* v = arr)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(arr.Length * sizeof(float)), v, BufferUsageARB.DynamicDraw);
        }

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

        _gl.LineWidth(1.0f);
        _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(arr.Length / 3));

        _gl.BindVertexArray(0);
    }

    private void DrawParticleCluster(Vector3 center, float progress, Matrix4x4 view, Matrix4x4 projection, Vector3 cameraPos)
    {
        _vfxShader.Use();
        _vfxShader.SetMatrix4("u_View", view);
        _vfxShader.SetMatrix4("u_Projection", projection);

        _defaultParticleTex.Bind(TextureUnit.Texture0);
        _vfxShader.SetInt("u_HasTexture", 1);
        _vfxShader.SetInt("u_ParticleTex", 0);

        // Spherical billboard facing camera
        Vector3 forward = Vector3.Normalize(cameraPos - center);
        if (forward.LengthSquared() < 0.001f) forward = Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 up = Vector3.Cross(forward, right);

        Matrix4x4 billboardRotation = new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1
        );

        _gl.BindVertexArray(_quadVao);

        // Render 8 dynamic particles swirling/radiating out
        int particleCount = 8;
        for (int p = 0; p < particleCount; p++)
        {
            float seed = p * 0.785398f; // 45 deg intervals
            float speed = 0.5f + (p % 3) * 0.25f;
            float radius = progress * speed * 0.8f;

            float px = center.X + MathF.Cos(seed + progress * 4f) * radius;
            float py = center.Y + MathF.Sin(seed * 2f + progress * 2f) * radius * 0.5f;
            float pz = center.Z + MathF.Sin(seed + progress * 4f) * radius;

            float particleScale = (1.0f - progress * 0.6f) * (0.15f + (p % 2) * 0.1f);
            Matrix4x4 model = Matrix4x4.CreateScale(particleScale) * billboardRotation * Matrix4x4.CreateTranslation(px, py, pz);

            _vfxShader.SetMatrix4("u_Model", model);

            // Fiery orange/cyan HDR color (values > 1.0 glow via HDR Bloom!)
            Vector4 color = (p % 2 == 0)
                ? new Vector4(2.5f, 0.9f, 0.2f, 1.0f) // Blazing Orange
                : new Vector4(0.4f, 1.8f, 2.8f, 1.0f); // Electric Cyan

            _vfxShader.SetUniform("u_TintColor", color);
            _vfxShader.SetFloat("u_Intensity", 2.2f); // High intensity triggers Bloom

            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        _gl.BindVertexArray(0);
    }

    private unsafe void DrawSlashTrail(Vector3 center, float progress, Matrix4x4 view, Matrix4x4 projection)
    {
        _vfxShader.Use();
        _vfxShader.SetMatrix4("u_Model", Matrix4x4.Identity);
        _vfxShader.SetMatrix4("u_View", view);
        _vfxShader.SetMatrix4("u_Projection", projection);

        _defaultParticleTex.Bind(TextureUnit.Texture0);
        _vfxShader.SetInt("u_HasTexture", 1);
        _vfxShader.SetInt("u_ParticleTex", 0);
        _vfxShader.SetUniform("u_TintColor", new Vector4(2.8f, 1.2f, 0.3f, 1.0f)); // Super bright slash arc
        _vfxShader.SetFloat("u_Intensity", 2.5f);

        // Generate curved ribbon strip representing weapon slash trajectory
        int segments = 16;
        float arcAngle = 1.6f; // ~90 degrees
        float startAngle = -arcAngle * 0.5f + progress * 0.8f;
        float radius = 0.9f;
        float ribbonWidth = 0.18f;

        List<float> ribbonVerts = new();

        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = startAngle + t * arcAngle;

            float cosA = MathF.Cos(angle);
            float sinA = MathF.Sin(angle);

            // Inner vertex
            float innerR = radius - ribbonWidth * 0.5f;
            float x1 = center.X + cosA * innerR;
            float y1 = center.Y + sinA * 0.3f;
            float z1 = center.Z + sinA * innerR;

            // Outer vertex
            float outerR = radius + ribbonWidth * 0.5f;
            float x2 = center.X + cosA * outerR;
            float y2 = center.Y + sinA * 0.3f + ribbonWidth * 0.4f;
            float z2 = center.Z + sinA * outerR;

            float alpha = MathF.Sin(t * MathF.PI); // Fade at trail tips

            // Vertex 1: X, Y, Z, U, V, R, G, B, A
            ribbonVerts.AddRange([x1, y1, z1, t, 0.0f, 1f, 1f, 1f, alpha]);
            // Vertex 2: X, Y, Z, U, V, R, G, B, A
            ribbonVerts.AddRange([x2, y2, z2, t, 1.0f, 1f, 1f, 1f, alpha]);
        }

        float[] arr = ribbonVerts.ToArray();
        if (arr.Length == 0) return;

        _gl.BindVertexArray(_trailVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);

        fixed (float* v = arr)
        {
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(arr.Length * sizeof(float)), v);
        }

        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, (uint)(arr.Length / 9));

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Renders an instanced mesh proxy for EFX entries with TypeMesh / MeshEmitter.
    /// </summary>
    public void DrawMeshInstance(GpuMesh mesh, Matrix4x4 transform, Matrix4x4 view, Matrix4x4 projection, Vector4 tintColor, Shader meshShader)
    {
        meshShader.Use();
        meshShader.SetMatrix4("u_Model", transform);
        meshShader.SetMatrix4("u_View", view);
        meshShader.SetMatrix4("u_Projection", projection);
        meshShader.SetUniform("u_BaseColor", tintColor);
        meshShader.SetUniform("u_EmissionColor", new Vector3(tintColor.X, tintColor.Y, tintColor.Z));
        meshShader.SetFloat("u_EmissionIntensity", 1.5f);
        meshShader.SetInt("u_HasSkinning", 0);
        meshShader.SetInt("u_HasTexture", 0);

        mesh.Draw();
    }

    public void DrawParticleClusterCustomColor(Vector3 center, float progress, Matrix4x4 view, Matrix4x4 projection, Vector3 cameraPos, Vector4 customColor, float scale)
    {
        _vfxShader.Use();
        _vfxShader.SetMatrix4("u_View", view);
        _vfxShader.SetMatrix4("u_Projection", projection);

        _defaultParticleTex.Bind(TextureUnit.Texture0);
        _vfxShader.SetInt("u_HasTexture", 1);
        _vfxShader.SetInt("u_ParticleTex", 0);

        Vector3 forward = Vector3.Normalize(cameraPos - center);
        if (forward.LengthSquared() < 0.001f) forward = Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 up = Vector3.Cross(forward, right);

        Matrix4x4 billboardRotation = new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1
        );

        _gl.BindVertexArray(_quadVao);

        int particleCount = 12;
        for (int p = 0; p < particleCount; p++)
        {
            float seed = p * (MathF.PI * 2f / particleCount);
            float speed = 0.5f + (p % 3) * 0.3f;
            float radius = progress * speed * 0.8f * scale;

            float px = center.X + MathF.Cos(seed + progress * 5f) * radius;
            float py = center.Y + MathF.Sin(seed * 2f + progress * 3f) * radius * 0.6f;
            float pz = center.Z + MathF.Sin(seed + progress * 5f) * radius;

            float particleScale = (1.0f - progress * 0.5f) * (0.18f + (p % 2) * 0.12f) * scale;
            Matrix4x4 model = Matrix4x4.CreateScale(particleScale) * billboardRotation * Matrix4x4.CreateTranslation(px, py, pz);

            _vfxShader.SetMatrix4("u_Model", model);
            _vfxShader.SetUniform("u_TintColor", customColor);
            _vfxShader.SetFloat("u_Intensity", 2.2f);

            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        _gl.BindVertexArray(0);
    }

    public unsafe void DrawSlashTrailCustomColor(Vector3 center, float progress, Matrix4x4 view, Matrix4x4 projection, Vector4 customColor, float scale)
    {
        _vfxShader.Use();
        _vfxShader.SetMatrix4("u_Model", Matrix4x4.Identity);
        _vfxShader.SetMatrix4("u_View", view);
        _vfxShader.SetMatrix4("u_Projection", projection);

        _defaultParticleTex.Bind(TextureUnit.Texture0);
        _vfxShader.SetInt("u_HasTexture", 1);
        _vfxShader.SetInt("u_ParticleTex", 0);
        _vfxShader.SetUniform("u_TintColor", customColor);
        _vfxShader.SetFloat("u_Intensity", 2.5f);

        int segments = 16;
        float arcAngle = 1.6f;
        float startAngle = -arcAngle * 0.5f + progress * 0.8f;
        float radius = 0.9f * scale;
        float ribbonWidth = 0.18f * scale;

        List<float> ribbonVerts = new();

        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = startAngle + t * arcAngle;

            float cosA = MathF.Cos(angle);
            float sinA = MathF.Sin(angle);

            float innerR = radius - ribbonWidth * 0.5f;
            float x1 = center.X + cosA * innerR;
            float y1 = center.Y + sinA * 0.3f;
            float z1 = center.Z + sinA * innerR;

            float outerR = radius + ribbonWidth * 0.5f;
            float x2 = center.X + cosA * outerR;
            float y2 = center.Y + sinA * 0.3f + ribbonWidth * 0.4f;
            float z2 = center.Z + sinA * outerR;

            float alpha = MathF.Sin(t * MathF.PI);

            ribbonVerts.AddRange([x1, y1, z1, t, 0.0f, 1f, 1f, 1f, alpha]);
            ribbonVerts.AddRange([x2, y2, z2, t, 1.0f, 1f, 1f, 1f, alpha]);
        }

        float[] arr = ribbonVerts.ToArray();
        if (arr.Length == 0) return;

        _gl.BindVertexArray(_trailVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);

        fixed (float* v = arr)
        {
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(arr.Length * sizeof(float)), v);
        }

        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, (uint)(arr.Length / 9));
        _gl.BindVertexArray(0);
    }

    public void RenderLiveEfxEntry(
        ReeLib.Efx.EFXEntryBase entry,
        BoneAnimationEvaluator animator,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPos,
        float time)
    {
        if (entry == null) return;

        // 1. Resolve Parent Bone World Matrix
        string targetBone = "R_Weapon";
        var parentOpt = entry.Attributes.OfType<ReeLib.Efx.Structs.Basic.EFXAttributeParentOptions>().FirstOrDefault();
        if (parentOpt != null)
        {
            var bName = parentOpt.ParentBone ?? parentOpt.BoneName;
            if (!string.IsNullOrEmpty(bName) && !bName.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                targetBone = bName;
            }
        }

        var boneWorld = animator.GetBoneWorldMatrix(targetBone);
        if (boneWorld == Matrix4x4.Identity)
        {
            boneWorld = animator.GetBoneWorldMatrix("R_Weapon");
            if (boneWorld == Matrix4x4.Identity)
            {
                boneWorld = Matrix4x4.CreateTranslation(0f, 1.0f, 0f);
            }
        }

        // 2. Resolve Transform3D (Local Position, Rotation, Scale)
        Vector3 localPos = Vector3.Zero;
        Vector3 localRot = Vector3.Zero;
        Vector3 localScale = Vector3.One;

        var trans = entry.Attributes.OfType<ReeLib.Efx.Structs.Transforms.EFXAttributeTransform3D>().FirstOrDefault();
        if (trans != null)
        {
            localPos = trans.LocalPosition;
            localRot = trans.LocalRotation;
            localScale = trans.LocalScale;
            if (localScale == Vector3.Zero) localScale = Vector3.One;
        }

        var rotMat = Matrix4x4.CreateRotationX(localRot.X) *
                     Matrix4x4.CreateRotationY(localRot.Y) *
                     Matrix4x4.CreateRotationZ(localRot.Z);

        Matrix4x4 efxWorld = Matrix4x4.CreateScale(localScale) * rotMat * Matrix4x4.CreateTranslation(localPos) * boneWorld;
        SelectedTransform = efxWorld;
        Vector3 worldPos = efxWorld.Translation;

        // 3. Resolve Color from PtColor or default HDR gold
        Vector4 tintColor = new Vector4(2.5f, 1.2f, 0.4f, 1.0f);
        foreach (var attr in entry.Attributes)
        {
            var colProp = attr.GetType().GetProperty("Color") ?? attr.GetType().GetProperty("PtColor");
            if (colProp != null)
            {
                var val = colProp.GetValue(attr);
                if (val is Vector4 v4) tintColor = v4;
                else if (val is ReeLib.via.Color c) tintColor = new Vector4(c.R / 255f * 2.5f, c.G / 255f * 2.5f, c.B / 255f * 2.5f, c.A / 255f);
            }
        }

        // 4. Render Live Visuals:
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);

        float visualScale = MathF.Max(0.5f, MathF.Max(localScale.X, localScale.Y));
        DrawCoordinateAxes(worldPos, view, projection, 0.35f * visualScale);
        DrawWireframeSphere(worldPos, 0.25f * visualScale, view, projection, new Vector4(tintColor.X * 0.5f, tintColor.Y * 0.5f, tintColor.Z * 0.5f, 0.7f));

        float progress = (time * 0.05f) % 1.0f;
        DrawParticleClusterCustomColor(worldPos, progress, view, projection, cameraPos, tintColor, visualScale);
        DrawSlashTrailCustomColor(worldPos, progress, view, projection, tintColor, visualScale);

        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>
    /// Renders active motion VFX slots using the deterministic EfxSimulator and companion assets
    /// (.efx, .pfb, .uvs, .tex, .mesh, .mdf2). Strictly renders only slots where StartFrame <= currentFrame <= EndFrame.
    /// </summary>
    public void RenderSlotDrivenSimulators(
        IReadOnlyList<VfxSlot> slots,
        float currentFrame,
        BoneAnimationEvaluator hunterEval,
        Weapons.WeaponAttachmentManager? weaponManager,
        Shader meshShader,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPos,
        int selectedSlotId,
        Func<VfxSlot, (VfxStudio.Core.Services.PfbElementEntry? Element, List<(string Path, ReeLib.EfxFile Efx)> EfxList)> resolveSlotEfx)
    {
        if (slots == null || slots.Count == 0 || AssetCache == null) return;

        foreach (var slot in slots)
        {
            string slotPrefix = $"slot_{slot.SlotId}_";
            if (currentFrame < slot.StartFrame || currentFrame > slot.EndFrame)
            {
                foreach (var kv in _slotSimulators)
                {
                    if (kv.Key.StartsWith(slotPrefix, StringComparison.OrdinalIgnoreCase) && kv.Value.CurrentSimFrame >= 0)
                    {
                        kv.Value.Reset(Matrix4x4.Identity);
                    }
                }
                continue;
            }

            var (pfbElem, efxList) = resolveSlotEfx(slot);

            // Resolve target attachment bone world matrix
            string effectiveBone = !string.IsNullOrWhiteSpace(slot.JointName)
                ? slot.JointName
                : (!string.IsNullOrWhiteSpace(pfbElem?.JointName) ? pfbElem!.JointName : "VFX_Attack");
            int relChildId = pfbElem?.RelationChildId ?? (slot.ContainerId == 3 ? 1021 : -1);

            Matrix4x4 boneMatrix = Matrix4x4.Identity;
            if (!effectiveBone.Equals("VFX_Attack", StringComparison.OrdinalIgnoreCase) &&
                !effectiveBone.Equals("R_Wep", StringComparison.OrdinalIgnoreCase) &&
                !effectiveBone.Equals("L_Wep", StringComparison.OrdinalIgnoreCase) &&
                !effectiveBone.Equals("Base", StringComparison.OrdinalIgnoreCase))
            {
                boneMatrix = hunterEval.GetBoneWorldMatrix(effectiveBone);
            }

            if (boneMatrix == Matrix4x4.Identity)
            {
                if (weaponManager != null && weaponManager.Parts.Count > 0)
                {
                    bool useSecondary = (relChildId == 1021 || slot.ContainerId == 3 ||
                                         effectiveBone.StartsWith("L_", StringComparison.OrdinalIgnoreCase)) &&
                                        weaponManager.Parts.Count > 1;
                    var targetPart = useSecondary ? weaponManager.Parts[1] : weaponManager.Parts[0];
                    boneMatrix = weaponManager.GetTargetBoneWorldMatrix(targetPart, hunterEval);
                }
                else
                {
                    boneMatrix = hunterEval.GetBoneWorldMatrix("R_Hand_HJ_01");
                    if (boneMatrix == Matrix4x4.Identity)
                        boneMatrix = hunterEval.GetBoneWorldMatrix("R_Hand");
                }
            }

            Matrix4x4 offsetMatrix = Matrix4x4.CreateTranslation(slot.OffsetX, slot.OffsetY, slot.OffsetZ);
            Matrix4x4 slotWorldMatrix = offsetMatrix * boneMatrix;

            if (slot.SlotId == selectedSlotId)
            {
                SelectedTransform = slotWorldMatrix;
                DrawCoordinateAxes(slotWorldMatrix.Translation, view, projection, 0.3f);
            }

            int localFrame = Math.Max(0, (int)MathF.Floor(currentFrame - slot.StartFrame));

            foreach (var (efxPath, efxFile) in efxList)
            {
                string simKey = $"{slotPrefix}{efxPath}";
                if (!_slotSimulators.TryGetValue(simKey, out var sim) || !ReferenceEquals(sim.Efx, efxFile))
                {
                    ulong seed = (ulong)(slot.SlotId * 7919 + efxPath.GetHashCode(StringComparison.OrdinalIgnoreCase));
                    sim = new EfxSimulator(efxPath, efxFile, AssetCache, seed);
                    _slotSimulators[simKey] = sim;
                }

                sim.StepToFrame(localFrame, slotWorldMatrix);
                if (sim.HasActiveParticles)
                {
                    sim.Render(
                        _gl,
                        _vfxShader,
                        meshShader,
                        _defaultParticleTex,
                        _quadVao,
                        _quadVbo,
                        _trailVao,
                        _trailVbo,
                        MaxTrailVertices,
                        slotWorldMatrix,
                        view,
                        projection,
                        cameraPos);
                }
            }

            // Unsupported render bodies must not be represented by invented sparks.
        }
    }

    public (int Simulators, int Active) GetSlotSimulationState(int slotId)
    {
        string prefix = $"slot_{slotId}_";
        var sims = _slotSimulators.Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value).ToArray();
        return (sims.Length, sims.Count(sim => sim.HasActiveParticles));
    }

    public void Dispose()
    {
        AssetCache?.Dispose();
        _slotSimulators.Clear();
        _previewSimulators.Clear();
        _vfxShader.Dispose();
        _wireframeShader.Dispose();
        _defaultParticleTex.Dispose();

        if (_quadVbo != 0) { _gl.DeleteBuffer(_quadVbo); _quadVbo = 0; }
        if (_quadVao != 0) { _gl.DeleteVertexArray(_quadVao); _quadVao = 0; }

        if (_wireframeVbo != 0) { _gl.DeleteBuffer(_wireframeVbo); _wireframeVbo = 0; }
        if (_wireframeVao != 0) { _gl.DeleteVertexArray(_wireframeVao); _wireframeVao = 0; }

        if (_trailVbo != 0) { _gl.DeleteBuffer(_trailVbo); _trailVbo = 0; }
        if (_trailVao != 0) { _gl.DeleteVertexArray(_trailVao); _trailVao = 0; }
    }
}
