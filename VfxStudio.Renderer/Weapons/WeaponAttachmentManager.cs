using System.Numerics;
using ReeLib;
using Silk.NET.OpenGL;
using VfxStudio.Core.Models;
using VfxStudio.Renderer.Animation;
using VfxStudio.Renderer.Meshes;
using VfxStudio.Renderer.Shaders;
using VfxStudio.Renderer.Textures;
using Shader = VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.Renderer.Weapons;

public class WeaponPart
{
    public int PartIndex { get; set; }
    public string PartDescription { get; set; } = string.Empty;
    public string MeshPath { get; set; } = string.Empty;
    public GpuMesh? Mesh { get; set; }
    public GpuTexture? Texture { get; set; }

    // Real-time user-configurable mounting parameters (persisted in weapon_mount_presets.json)
    public string TargetBoneName { get; set; } = "R_Hand_HJ_01";
    public string[] FallbackBoneNames { get; set; } = [];
    public Vector3 PositionOffset { get; set; } = Vector3.Zero;
    public Vector3 RotationEuler { get; set; } = Vector3.Zero; // Degrees (-180 ~ +180)
    public float Scale { get; set; } = 1.0f;
    public bool UseWeaponBaseBoneInverse { get; set; } = true;

    public Matrix4x4 WeaponBaseBoneMatrix { get; set; } = Matrix4x4.Identity;
    public Vector4 BaseColor { get; set; } = Vector4.One;

    public void ApplyPreset(WeaponPartMountPreset preset)
    {
        if (!string.IsNullOrEmpty(preset.PartDescription))
            PartDescription = preset.PartDescription;
        if (!string.IsNullOrEmpty(preset.TargetBoneName))
            TargetBoneName = preset.TargetBoneName;
        PositionOffset = preset.PositionVector;
        RotationEuler = preset.RotationEulerVector;
        Scale = preset.Scale > 0.001f ? preset.Scale : 1.0f;
        UseWeaponBaseBoneInverse = preset.UseWeaponBaseBoneInverse;
    }

    public WeaponPartMountPreset ToPreset()
    {
        return new WeaponPartMountPreset
        {
            PartIndex = PartIndex,
            PartDescription = PartDescription,
            TargetBoneName = TargetBoneName,
            PositionOffset = [PositionOffset.X, PositionOffset.Y, PositionOffset.Z],
            RotationEuler = [RotationEuler.X, RotationEuler.Y, RotationEuler.Z],
            Scale = Scale,
            UseWeaponBaseBoneInverse = UseWeaponBaseBoneInverse
        };
    }
}

public class WeaponAttachmentManager : IDisposable
{
    private readonly GL _gl;
    public WeaponDefinition? CurrentWeapon { get; private set; }
    public List<WeaponPart> Parts { get; } = new();

    public string PresetsJsonPath { get; private set; } = string.Empty;
    public WeaponMountPresetStore PresetStore { get; private set; } = new();
    public string? CurrentMotionKey { get; private set; }

    public bool IsUsingMotionOverride =>
        !string.IsNullOrWhiteSpace(CurrentMotionKey) &&
        CurrentWeapon != null &&
        PresetStore.HasMotionOverride(CurrentWeapon.WpCode, CurrentMotionKey);

    public WeaponAttachmentManager(GL gl, string? presetsJsonPath = null)
    {
        _gl = gl;
        if (!string.IsNullOrEmpty(presetsJsonPath))
        {
            InitializePresets(presetsJsonPath);
        }
    }

    public void InitializePresets(string jsonPath)
    {
        PresetsJsonPath = jsonPath;
        PresetStore = WeaponMountPresetStore.LoadOrCreate(jsonPath);
    }

    /// <summary>
    /// Reloads weapon_mount_presets.json from disk and applies presets to all weapon sub-parts.
    /// </summary>
    public void LoadWeapon(string weaponCode, string weaponBaseDir, string? presetsJsonPath = null)
    {
        Dispose();

        if (!string.IsNullOrEmpty(presetsJsonPath))
        {
            PresetsJsonPath = presetsJsonPath;
        }
        if (!string.IsNullOrEmpty(PresetsJsonPath))
        {
            PresetStore = WeaponMountPresetStore.LoadOrCreate(PresetsJsonPath);
        }

        CurrentWeapon = WeaponDefinition.FromCode(weaponCode);
        if (CurrentWeapon == null) return;

        string wpDir = Path.Combine(weaponBaseDir, CurrentWeapon.WpCode);
        if (!Directory.Exists(wpDir)) return;

        var meshFiles = Directory.GetFiles(wpDir, "*.mesh.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/streaming/") && !f.Contains("\\streaming\\"))
            .OrderBy(f => f)
            .ToList();

        if (meshFiles.Count == 0) return;

        // 1. Part 0: Primary Weapon (_0.mesh)
        var p0Path = meshFiles.FirstOrDefault(f => f.Contains("_0.mesh", StringComparison.OrdinalIgnoreCase)) ?? meshFiles[0];
        var preset0 = PresetStore.GetEffectivePartPreset(CurrentWeapon.WpCode, 0, CurrentMotionKey);
        var p0 = new WeaponPart
        {
            PartIndex = 0,
            MeshPath = p0Path,
            FallbackBoneNames = ["R_Hand_HJ_01", "R_Hand", "R_Weapon", "R_Palm", "R_Wep"],
            BaseColor = new Vector4(0.85f, 0.85f, 0.9f, 1.0f)
        };
        p0.ApplyPreset(preset0);
        LoadPartMesh(p0);
        Parts.Add(p0);

        // 2. Part 1: Secondary Weapon / Shield / Quiver / Sheath (_1.mesh)
        var p1Path = meshFiles.FirstOrDefault(f => f.Contains("_1.mesh", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(p1Path) && p1Path != p0Path)
        {
            var preset1 = PresetStore.GetEffectivePartPreset(CurrentWeapon.WpCode, 1, CurrentMotionKey);
            var p1 = new WeaponPart
            {
                PartIndex = 1,
                MeshPath = p1Path,
                FallbackBoneNames = ["L_Hand_HJ_01", "L_Hand", "Spine_2_WpConst", "Spine_1_WpConst", "L_Weapon", "L_Palm"],
                BaseColor = new Vector4(0.8f, 0.8f, 0.85f, 1.0f)
            };
            p1.ApplyPreset(preset1);
            LoadPartMesh(p1);
            Parts.Add(p1);
        }
    }

    /// <summary>
    /// Applies the per-motion weapon mount override if one exists for motionKey,
    /// or reverts to the weapon's general default preset if none exists.
    /// </summary>
    public void ApplyMotionPreset(string? motionKey)
    {
        CurrentMotionKey = string.IsNullOrWhiteSpace(motionKey) ? null : motionKey.Trim();
        if (CurrentWeapon == null) return;

        foreach (var part in Parts)
        {
            var preset = PresetStore.GetEffectivePartPreset(CurrentWeapon.WpCode, part.PartIndex, CurrentMotionKey);
            part.ApplyPreset(preset);
        }
    }

    /// <summary>
    /// Writes the current weapon's live mounting configuration into weapon_mount_presets.json as general default.
    /// </summary>
    public void SaveCurrentWeaponPresets()
    {
        if (CurrentWeapon == null || string.IsNullOrEmpty(PresetsJsonPath)) return;

        foreach (var part in Parts)
        {
            PresetStore.UpdatePartPreset(CurrentWeapon.WpCode, part.ToPreset());
        }
        PresetStore.Save(PresetsJsonPath);
    }

    /// <summary>
    /// Saves the current live mounting configuration as a per-motion override for the specified motion.
    /// </summary>
    public void SaveCurrentMotionOverride(string motionKey)
    {
        if (CurrentWeapon == null || string.IsNullOrWhiteSpace(motionKey) || string.IsNullOrEmpty(PresetsJsonPath)) return;

        CurrentMotionKey = motionKey.Trim();
        PresetStore.SetMotionOverride(CurrentWeapon.WpCode, CurrentMotionKey, Parts.Select(p => p.ToPreset()));
        PresetStore.Save(PresetsJsonPath);
    }

    /// <summary>
    /// Clears the per-motion override for the specified (or current) motion and reverts to general weapon preset.
    /// </summary>
    public bool ClearMotionOverride(string? motionKey = null)
    {
        string? key = string.IsNullOrWhiteSpace(motionKey) ? CurrentMotionKey : motionKey.Trim();
        if (CurrentWeapon == null || string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(PresetsJsonPath)) return false;

        bool removed = PresetStore.ClearMotionOverride(CurrentWeapon.WpCode, key);
        if (removed)
        {
            PresetStore.Save(PresetsJsonPath);
            ApplyMotionPreset(CurrentMotionKey);
        }
        return removed;
    }

    private void LoadPartMesh(WeaponPart part)
    {
        try
        {
            if (!File.Exists(part.MeshPath)) return;
            using var fs = File.OpenRead(part.MeshPath);
            var handler = new FileHandler(fs, part.MeshPath);
            var meshFile = new MeshFile(handler);
            if (meshFile.Read())
            {
                if (meshFile.BoneData?.Bones != null && meshFile.BoneData.Bones.Count > 0)
                {
                    var gripBone = meshFile.BoneData.Bones.FirstOrDefault(b => b.name.Equals("Base", StringComparison.OrdinalIgnoreCase))
                                ?? meshFile.BoneData.Bones.FirstOrDefault(b => b.name.Equals("Grip", StringComparison.OrdinalIgnoreCase))
                                ?? meshFile.BoneData.Bones[0];

                    var bMat = gripBone.globalTransform.ToSystem();
                    if (bMat == Matrix4x4.Identity || bMat.M44 == 0)
                    {
                        bMat = gripBone.localTransform.ToSystem();
                    }
                    if (bMat != Matrix4x4.Identity && bMat.M44 != 0)
                    {
                        part.WeaponBaseBoneMatrix = bMat;
                    }
                }
                if (meshFile.RequiresStreamingData)
                {
                    string meshDir = Path.GetDirectoryName(part.MeshPath) ?? "";
                    string meshFileName = Path.GetFileName(part.MeshPath);
                    var candidatePaths = new[]
                    {
                        Path.Combine(meshDir, "streaming", meshFileName),
                        Path.Combine(meshDir, "..", "streaming", meshFileName),
                        part.MeshPath.Replace(@"\natives\stm\", @"\natives\stm\streaming\"),
                        part.MeshPath.Replace(@"/natives/stm/", @"/natives/stm/streaming/")
                    };
                    string? foundStreaming = candidatePaths.FirstOrDefault(File.Exists);
                    if (foundStreaming != null)
                    {
                        using var sfs = File.OpenRead(foundStreaming);
                        meshFile.LoadStreamingData(new FileHandler(sfs, foundStreaming));
                        meshFile.MergeStreamingMeshData();
                    }
                }
                part.Mesh = GpuMesh.CreateFromMeshFile(_gl, meshFile);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WeaponAttachmentManager] Error loading weapon mesh {part.MeshPath}: {ex.Message}");
        }
    }

    public Matrix4x4 GetTargetBoneWorldMatrix(WeaponPart part, BoneAnimationEvaluator animator)
    {
        var boneWorld = animator.GetBoneWorldMatrix(part.TargetBoneName);
        if (boneWorld == Matrix4x4.Identity && part.FallbackBoneNames.Length > 0)
        {
            foreach (var fb in part.FallbackBoneNames)
            {
                var candidate = animator.GetBoneWorldMatrix(fb);
                if (candidate != Matrix4x4.Identity)
                {
                    boneWorld = candidate;
                    break;
                }
            }
        }

        if (boneWorld == Matrix4x4.Identity)
        {
            string shoulderBone = (part.PartIndex == 0) ? "R_Shoulder" : "L_Shoulder";
            var shoulder = animator.GetBoneWorldMatrix(shoulderBone);
            if (shoulder != Matrix4x4.Identity)
            {
                Vector3 armOffset = (part.PartIndex == 0)
                    ? new Vector3(0.2f, -0.45f, 0.2f)
                    : new Vector3(-0.2f, -0.45f, 0.2f);
                boneWorld = Matrix4x4.CreateTranslation(armOffset) * shoulder;
            }
            else
            {
                Vector3 defaultHandPos = (part.PartIndex == 0)
                    ? new Vector3(0.35f, 1.0f, 0.2f)
                    : new Vector3(-0.35f, 1.0f, 0.2f);
                boneWorld = Matrix4x4.CreateTranslation(defaultHandPos);
            }
        }

        return boneWorld;
    }

    /// <summary>
    /// Computes the live world transform matrix for the weapon part strictly from its dynamic parameters:
    /// WeaponWorldMatrix = (UseWeaponBaseBoneInverse ? InvBaseMatrix : Identity) * Scale * UserRotationMatrix * Translation(PositionOffset) * TargetBoneWorldMatrix
    /// </summary>
    public Matrix4x4 GetPartWorldMatrix(WeaponPart part, BoneAnimationEvaluator animator)
    {
        Matrix4x4 targetBoneWorldMatrix = GetTargetBoneWorldMatrix(part, animator);

        Matrix4x4 invBaseMatrix = Matrix4x4.Identity;
        if (part.UseWeaponBaseBoneInverse && part.WeaponBaseBoneMatrix != Matrix4x4.Identity)
        {
            Matrix4x4.Invert(part.WeaponBaseBoneMatrix, out invBaseMatrix);
        }

        Matrix4x4 userRotationMatrix = CreateRotationFromEulerDegrees(part.RotationEuler);
        Matrix4x4 userScaleMatrix = Matrix4x4.CreateScale(part.Scale);
        Matrix4x4 userTranslationMatrix = Matrix4x4.CreateTranslation(part.PositionOffset);

        return invBaseMatrix * userScaleMatrix * userRotationMatrix * userTranslationMatrix * targetBoneWorldMatrix;
    }

    public static Matrix4x4 CreateRotationFromEulerDegrees(Vector3 eulerDegrees)
    {
        float radX = eulerDegrees.X * (MathF.PI / 180.0f);
        float radY = eulerDegrees.Y * (MathF.PI / 180.0f);
        float radZ = eulerDegrees.Z * (MathF.PI / 180.0f);
        return Matrix4x4.CreateRotationX(radX) * Matrix4x4.CreateRotationY(radY) * Matrix4x4.CreateRotationZ(radZ);
    }

    public static Vector3 QuaternionToEulerDegrees(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
        return RotationMatrixToEulerDegrees(m);
    }

    public static Vector3 RotationMatrixToEulerDegrees(Matrix4x4 m)
    {
        float sinY = Math.Clamp(-m.M13, -1.0f, 1.0f);
        float radY = MathF.Asin(sinY);
        float cosY = MathF.Cos(radY);

        float radX, radZ;
        if (MathF.Abs(cosY) > 1e-4f)
        {
            radX = MathF.Atan2(m.M23, m.M33);
            radZ = MathF.Atan2(m.M12, m.M11);
        }
        else
        {
            radX = MathF.Atan2(-m.M32, m.M22);
            radZ = 0f;
        }

        const float radToDeg = 180.0f / MathF.PI;
        return new Vector3(
            NormalizeAngle180(radX * radToDeg),
            NormalizeAngle180(radY * radToDeg),
            NormalizeAngle180(radZ * radToDeg)
        );
    }

    public static float NormalizeAngle180(float angleDeg)
    {
        angleDeg %= 360.0f;
        if (angleDeg > 180.0f) angleDeg -= 360.0f;
        if (angleDeg < -180.0f) angleDeg += 360.0f;
        return angleDeg;
    }

    public void Render(Shader shader, BoneAnimationEvaluator animator)
    {
        shader.Use();
        shader.SetUniform("u_HasSkinning", 0);
        shader.SetUniform("u_EmissionColor", Vector3.Zero);
        shader.SetUniform("u_EmissionIntensity", 0.0f);

        foreach (var part in Parts)
        {
            if (part.Mesh == null) continue;

            var modelMat = GetPartWorldMatrix(part, animator);
            shader.SetUniform("u_Model", modelMat);
            shader.SetUniform("u_BaseColor", part.BaseColor);

            if (part.Texture != null)
            {
                shader.SetUniform("u_HasTexture", 1);
                part.Texture.Bind(TextureUnit.Texture0);
                shader.SetUniform("u_AlbedoMap", 0);
            }
            else
            {
                shader.SetUniform("u_HasTexture", 0);
            }

            part.Mesh.Render();
        }
    }

    public void Dispose()
    {
        foreach (var p in Parts)
        {
            p.Mesh?.Dispose();
            p.Texture?.Dispose();
        }
        Parts.Clear();
    }
}
