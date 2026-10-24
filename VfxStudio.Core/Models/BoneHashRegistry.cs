using ReeLib;
using ReeLib.Common;
using ReeLib.Mesh;

namespace VfxStudio.Core.Models;

/// <summary>
/// Bidirectional registry between Bone Names and Murmur3 Bone Hashes.
/// Pre-seeded with standard MHWilds Hunter bones and effect anchor joints.
/// Dynamically expands whenever a MeshFile or Skeleton is loaded.
/// </summary>
public class BoneHashRegistry
{
    private static readonly Dictionary<uint, string> HashToName = new();
    private static readonly Dictionary<string, uint> NameToHash = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SyncLock = new();

    static BoneHashRegistry()
    {
        // Standard Hunter Core Armature Bones
        Register("root");
        Register("COG");
        Register("Waist");
        Register("Spine_0");
        Register("Spine_1");
        Register("Spine_2");
        Register("Neck_0");
        Register("Neck_1");
        Register("Head");
        Register("Face_Root");

        // Arms & Shoulders
        Register("R_Clavicle");
        Register("L_Clavicle");
        Register("R_UpperArm");
        Register("L_UpperArm");
        Register("R_Forearm");
        Register("L_Forearm");
        Register("R_Hand");
        Register("L_Hand");
        Register("R_Weapon");
        Register("L_Weapon");

        // Legs & Feet
        Register("R_Thigh");
        Register("L_Thigh");
        Register("R_Shin");
        Register("L_Shin");
        Register("R_Foot");
        Register("L_Foot");
        Register("R_Toe");
        Register("L_Toe");

        // Left Fingers
        Register("L_Thumb1"); Register("L_Thumb2"); Register("L_Thumb3");
        Register("L_IndexF1"); Register("L_IndexF2"); Register("L_IndexF3");
        Register("L_MiddleF1"); Register("L_MiddleF2"); Register("L_MiddleF3");
        Register("L_RingF1"); Register("L_RingF2"); Register("L_RingF3");
        Register("L_PinkyF1"); Register("L_PinkyF2"); Register("L_PinkyF3");

        // Right Fingers
        Register("R_Thumb1"); Register("R_Thumb2"); Register("R_Thumb3");
        Register("R_IndexF1"); Register("R_IndexF2"); Register("R_IndexF3");
        Register("R_MiddleF1"); Register("R_MiddleF2"); Register("R_MiddleF3");
        Register("R_RingF1"); Register("R_RingF2"); Register("R_RingF3");
        Register("R_PinkyF1"); Register("R_PinkyF2"); Register("R_PinkyF3");

        // Common Action & VFX Anchor Joints
        Register("VFX_Attack");
        Register("VFX_Root");
        Register("VFX_Weapon_Tip");
        Register("VFX_Weapon_Base");
        Register("Wep_Attach_R");
        Register("Wep_Attach_L");
        Register("Sheath_Attach");
        Register("Hip_Attach");
        Register("Back_Attach");
        Register("Camera_Target");
    }

    public static uint Register(string boneName)
    {
        if (string.IsNullOrWhiteSpace(boneName)) return 0;
        lock (SyncLock)
        {
            uint hash = MurMur3HashUtils.GetHash(boneName);
            HashToName[hash] = boneName;
            NameToHash[boneName] = hash;
            return hash;
        }
    }

    public static uint GetHash(string boneName)
    {
        if (string.IsNullOrWhiteSpace(boneName)) return 0;
        lock (SyncLock)
        {
            if (NameToHash.TryGetValue(boneName, out var hash))
                return hash;

            return Register(boneName);
        }
    }

    public static string GetName(uint hash)
    {
        if (hash == 0) return "None";
        lock (SyncLock)
        {
            if (HashToName.TryGetValue(hash, out var name))
                return name;

            return $"0x{hash:X8}";
        }
    }

    public static bool TryGetName(uint hash, out string name)
    {
        lock (SyncLock)
        {
            return HashToName.TryGetValue(hash, out name!);
        }
    }

    /// <summary>
    /// Ingests all bone names from a loaded RE Engine MeshFile.
    /// </summary>
    public static void IngestFromMesh(MeshFile mesh)
    {
        if (mesh?.BoneData?.Bones == null) return;
        lock (SyncLock)
        {
            foreach (var b in mesh.BoneData.Bones)
            {
                if (!string.IsNullOrEmpty(b.name))
                {
                    Register(b.name);
                }
            }
        }
    }

    public static IReadOnlyCollection<string> GetAllKnownBoneNames()
    {
        lock (SyncLock)
        {
            return NameToHash.Keys.ToList();
        }
    }
}
