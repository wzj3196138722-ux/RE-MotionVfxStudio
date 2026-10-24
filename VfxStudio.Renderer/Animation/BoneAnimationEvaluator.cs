using System.Numerics;
using System.Text.Json;
using ReeLib;
using ReeLib.Common;
using ReeLib.Mot;
using Silk.NET.OpenGL;
using VfxStudio.Renderer.Meshes;

namespace VfxStudio.Renderer.Animation;

public class BoneAnimationEvaluator : IDisposable
{
    private readonly GL _gl;
    private uint _boneSsboHandle;

    private MeshFile? _mesh;
    private FbxHunterModel? _hunterModel;
    private readonly List<HunterBone> _bones = new();
    private readonly Dictionary<string, int> _boneNameToIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, int> _boneHashToIndex = new();

    private readonly Matrix4x4[] _boneGlobalTransforms = new Matrix4x4[GpuConfig.MaxBones];
    private readonly Matrix4x4[] _finalSkinMatrices = new Matrix4x4[GpuConfig.MaxBones];

    private readonly Dictionary<string, Quaternion> _weaponGripRotations = new(StringComparer.OrdinalIgnoreCase);

    public MeshFile? Mesh => _mesh;
    public FbxHunterModel? HunterModel => _hunterModel;
    public IReadOnlyList<HunterBone> Bones => _bones;

    public BoneAnimationEvaluator(GL gl)
    {
        _gl = gl;
        InitSsbo();
        ResetMatrices();
    }

    private void InitSsbo()
    {
        _boneSsboHandle = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _boneSsboHandle);
        unsafe
        {
            _gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (nuint)(GpuConfig.MaxBones * sizeof(Matrix4x4)),
                null,
                BufferUsageARB.DynamicDraw
            );
        }
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private void ResetMatrices()
    {
        for (int i = 0; i < GpuConfig.MaxBones; i++)
        {
            _boneGlobalTransforms[i] = Matrix4x4.Identity;
            _finalSkinMatrices[i] = Matrix4x4.Identity;
        }
    }

    public void LoadWeaponGrip(string jsonPath)
    {
        _weaponGripRotations.Clear();
        if (!File.Exists(jsonPath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;

            void ParseSection(string sectionName)
            {
                if (root.TryGetProperty(sectionName, out var sec) && sec.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in sec.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() == 4)
                        {
                            // JSON specifies: [W, X, Y, Z]
                            float w = prop.Value[0].GetSingle();
                            float x = prop.Value[1].GetSingle();
                            float y = prop.Value[2].GetSingle();
                            float z = prop.Value[3].GetSingle();
                            _weaponGripRotations[prop.Name] = new Quaternion(x, y, z, w);
                        }
                    }
                }
            }

            ParseSection("LeftHand");
            ParseSection("RightHand");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BoneAnimationEvaluator] Error parsing weapon grip: {ex.Message}");
        }
    }

    public void SetHunterModel(FbxHunterModel model)
    {
        _hunterModel = model;
        _mesh = null;
        _bones.Clear();
        _boneNameToIndex.Clear();
        _boneHashToIndex.Clear();

        foreach (var b in model.Bones)
        {
            _bones.Add(b);
            _boneNameToIndex[b.Name] = b.Index;
            _boneHashToIndex[b.Hash] = b.Index;
        }
    }

    public void SetMesh(MeshFile mesh)
    {
        _mesh = mesh;
        _hunterModel = null;
        _bones.Clear();
        _boneNameToIndex.Clear();
        _boneHashToIndex.Clear();

        if (_mesh.BoneData?.Bones == null) return;

        for (int i = 0; i < _mesh.BoneData.Bones.Count && i < GpuConfig.MaxBones; i++)
        {
            var b = _mesh.BoneData.Bones[i];
            if (!string.IsNullOrEmpty(b.name))
            {
                var hBone = new HunterBone
                {
                    Index = b.index,
                    Name = b.name,
                    Hash = MurMur3HashUtils.GetHash(b.name),
                    ParentIndex = b.parentIndex,
                    LocalRestTransform = b.localTransform.ToSystem(),
                    InverseBindPose = b.inverseGlobalTransform.ToSystem()
                };
                _bones.Add(hBone);
                _boneNameToIndex[b.name] = b.index;
                _boneHashToIndex[hBone.Hash] = b.index;
            }
        }
    }

    /// <summary>
    /// Evaluates the 512 bone matrices for the given .mot file at the specified animation frame.
    /// Strictly guarantees anti-distortion fallback: un-animated bones fallback to rest pose,
    /// hands receive WeaponGrip rotations, and final matrices multiply by InverseBindPose.
    /// </summary>
    public void EvaluatePose(MotFile? mot, float frame, bool applyGrip = true)
    {
        if (_bones.Count == 0)
        {
            ResetMatrices();
            UploadToGpu();
            return;
        }

        int count = Math.Min(_bones.Count, GpuConfig.MaxBones);

        // Pre-build lookup for animation clips by bone hash
        Dictionary<uint, BoneMotionClip>? clipLookup = null;
        if (mot?.BoneClips != null)
        {
            clipLookup = new Dictionary<uint, BoneMotionClip>(mot.BoneClips.Count);
            foreach (var c in mot.BoneClips)
            {
                clipLookup[c.ClipHeader.boneHash] = c;
            }
        }

        for (int i = 0; i < count; i++)
        {
            var bone = _bones[i];

            // 1. Initial Rest Pose Local Transform fallback
            Matrix4x4.Decompose(bone.LocalRestTransform, out var localScale, out var localRot, out var localPos);

            uint boneHash = bone.Hash;

            // 2. Sample animated curves from .mot if available
            bool wasAnimated = false;
            if (clipLookup != null && boneHash != 0 && clipLookup.TryGetValue(boneHash, out var clip))
            {
                if (clip.HasTranslation && clip.Translation != null)
                {
                    var (t1, t2, interp) = FindFrames(clip.Translation, frame);
                    if (t1 >= 0 && clip.Translation.translations != null && clip.Translation.translations.Length > t2)
                    {
                        localPos = Vector3.Lerp(clip.Translation.translations[t1], clip.Translation.translations[t2], interp);
                        wasAnimated = true;
                    }
                }

                if (clip.HasRotation && clip.Rotation != null)
                {
                    var (t1, t2, interp) = FindFrames(clip.Rotation, frame);
                    if (t1 >= 0 && clip.Rotation.rotations != null && clip.Rotation.rotations.Length > t2)
                    {
                        localRot = Quaternion.Slerp(clip.Rotation.rotations[t1], clip.Rotation.rotations[t2], interp);
                        wasAnimated = true;
                    }
                }

                if (clip.HasScale && clip.Scale != null)
                {
                    var (t1, t2, interp) = FindFrames(clip.Scale, frame);
                    if (t1 >= 0 && clip.Scale.translations != null && clip.Scale.translations.Length > t2)
                    {
                        localScale = Vector3.Lerp(clip.Scale.translations[t1], clip.Scale.translations[t2], interp);
                        wasAnimated = true;
                    }
                }
            }

            // 3. Mathematical Anti-Distortion: Apply WeaponGrip finger rotations if not animated or holding weapon
            if (applyGrip && !wasAnimated && !string.IsNullOrEmpty(bone.Name) && _weaponGripRotations.TryGetValue(bone.Name, out var gripRot))
            {
                localRot = gripRot;
            }

            // 4. Construct local transform matrix
            var localTransform = Matrix4x4.CreateScale(localScale) *
                                 Matrix4x4.CreateFromQuaternion(localRot) *
                                 Matrix4x4.CreateTranslation(localPos);

            // 5. Hierarchical accumulation from parent
            Matrix4x4 globalTransform;
            if (bone.ParentIndex >= 0 && bone.ParentIndex < count && bone.ParentIndex != i)
            {
                globalTransform = localTransform * _boneGlobalTransforms[bone.ParentIndex];
            }
            else
            {
                globalTransform = localTransform;
            }
            _boneGlobalTransforms[bone.Index] = globalTransform;

            // 6. Compute FinalSkinMatrix = InverseBindPose * GlobalTransform
            if (bone.Index >= 0 && bone.Index < GpuConfig.MaxBones)
            {
                _finalSkinMatrices[bone.Index] = bone.InverseBindPose * globalTransform;
            }
        }

        UploadToGpu();
    }

    private unsafe void UploadToGpu()
    {
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _boneSsboHandle);
        fixed (Matrix4x4* ptr = _finalSkinMatrices)
        {
            _gl.BufferSubData(
                BufferTargetARB.ShaderStorageBuffer,
                0,
                (nuint)(GpuConfig.MaxBones * sizeof(Matrix4x4)),
                ptr
            );
        }
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    public void BindBoneBuffer(uint bindingIndex = 0)
    {
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, bindingIndex, _boneSsboHandle);
    }

    public Matrix4x4 GetBoneWorldMatrix(string boneName)
    {
        if (_boneNameToIndex.TryGetValue(boneName, out int index) && index >= 0 && index < GpuConfig.MaxBones)
        {
            var mat = _boneGlobalTransforms[index];
            if (mat != Matrix4x4.Identity) return mat;
        }

        // Weapon sockets smart fallback to Hand joints
        if (boneName.Equals("R_Hand_HJ_01", StringComparison.OrdinalIgnoreCase) ||
            boneName.Equals("R_Weapon", StringComparison.OrdinalIgnoreCase) ||
            boneName.Equals("R_Wep", StringComparison.OrdinalIgnoreCase))
        {
            if (_boneNameToIndex.TryGetValue("R_Hand_HJ_01", out int rhJIdx) && rhJIdx >= 0)
            {
                var mat = _boneGlobalTransforms[rhJIdx];
                if (mat != Matrix4x4.Identity) return mat;
            }
            if (_boneNameToIndex.TryGetValue("R_Hand", out int rhIdx) && rhIdx >= 0)
            {
                var mat = _boneGlobalTransforms[rhIdx];
                if (mat != Matrix4x4.Identity) return mat;
            }
        }

        if (boneName.Equals("L_Hand_HJ_01", StringComparison.OrdinalIgnoreCase) ||
            boneName.Equals("L_Weapon", StringComparison.OrdinalIgnoreCase) ||
            boneName.Equals("L_Wep", StringComparison.OrdinalIgnoreCase) ||
            boneName.Equals("L_Shield", StringComparison.OrdinalIgnoreCase))
        {
            if (_boneNameToIndex.TryGetValue("L_Hand_HJ_01", out int lhJIdx) && lhJIdx >= 0)
            {
                var mat = _boneGlobalTransforms[lhJIdx];
                if (mat != Matrix4x4.Identity) return mat;
            }
            if (_boneNameToIndex.TryGetValue("L_Hand", out int lhIdx) && lhIdx >= 0)
            {
                var mat = _boneGlobalTransforms[lhIdx];
                if (mat != Matrix4x4.Identity) return mat;
            }
        }

        // Sheath sockets fallback to Spine
        if (boneName.Equals("Spine_2_WpConst", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Sheath", StringComparison.OrdinalIgnoreCase) ||
            boneName.Equals("Back_Quiver", StringComparison.OrdinalIgnoreCase))
        {
            if (_boneNameToIndex.TryGetValue("Spine_2_WpConst", out int spIdx) && spIdx >= 0)
            {
                var mat = _boneGlobalTransforms[spIdx];
                if (mat != Matrix4x4.Identity) return mat;
            }
            if (_boneNameToIndex.TryGetValue("Spine_1_WpConst", out spIdx) && spIdx >= 0)
            {
                var mat = _boneGlobalTransforms[spIdx];
                if (mat != Matrix4x4.Identity) return mat;
            }
            if (_boneNameToIndex.TryGetValue("Spine_2", out spIdx) && spIdx >= 0)
            {
                var mat = _boneGlobalTransforms[spIdx];
                if (mat != Matrix4x4.Identity) return mat;
            }
        }

        return Matrix4x4.Identity;
    }

    public Matrix4x4 GetBoneWorldMatrixByHash(uint hash)
    {
        if (_boneHashToIndex.TryGetValue(hash, out int index) && index >= 0 && index < GpuConfig.MaxBones)
        {
            var mat = _boneGlobalTransforms[index];
            if (mat != Matrix4x4.Identity) return mat;
        }

        if (hash == MurMur3HashUtils.GetHash("R_Hand_HJ_01") ||
            hash == MurMur3HashUtils.GetHash("R_Weapon") ||
            hash == MurMur3HashUtils.GetHash("R_Wep"))
        {
            return GetBoneWorldMatrix("R_Hand_HJ_01");
        }

        if (hash == MurMur3HashUtils.GetHash("L_Hand_HJ_01") ||
            hash == MurMur3HashUtils.GetHash("L_Weapon") ||
            hash == MurMur3HashUtils.GetHash("L_Wep") ||
            hash == MurMur3HashUtils.GetHash("L_Shield"))
        {
            return GetBoneWorldMatrix("L_Hand_HJ_01");
        }

        if (hash == MurMur3HashUtils.GetHash("Spine_2_WpConst") ||
            hash == MurMur3HashUtils.GetHash("Back_Quiver") ||
            hash == MurMur3HashUtils.GetHash("Sheath"))
        {
            return GetBoneWorldMatrix("Spine_2_WpConst");
        }

        return Matrix4x4.Identity;
    }

    private static (int first, int second, float interpolation) FindFrames(Track track, float frame)
    {
        var keyframes = track.frameIndexes;
        if (keyframes == null || keyframes.Length == 0)
        {
            bool hasData = (track.translations?.Length > 0) || (track.rotations?.Length > 0);
            return hasData ? (0, 0, 0f) : (-1, -1, 0f);
        }

        int len = keyframes.Length;
        if (len == 1) return (0, 0, 0f);

        if (frame <= keyframes[0]) return (0, 0, 0f);
        if (frame >= keyframes[len - 1]) return (len - 1, len - 1, 0f);

        for (int i = 0; i < len - 1; i++)
        {
            if (frame >= keyframes[i] && frame <= keyframes[i + 1])
            {
                int diff = keyframes[i + 1] - keyframes[i];
                float t = diff > 0 ? (frame - keyframes[i]) / (float)diff : 0f;
                return (i, i + 1, t);
            }
        }
        return (len - 1, len - 1, 0f);
    }

    public void Dispose()
    {
        if (_boneSsboHandle != 0)
        {
            _gl.DeleteBuffer(_boneSsboHandle);
            _boneSsboHandle = 0;
        }
    }
}
