namespace VfxStudio.Core.Models;

/// <summary>
/// Global constants for bone hierarchies, skinning matrices, and GPU limits.
/// MHWilds hunter armature contains 250+ bones (including finger, cloth, and auxiliary helper bones).
/// The bone matrix capacity is expanded to 512 to prevent high-index skinning overflow.
/// </summary>
public static class BoneConstants
{
    /// <summary>
    /// Maximum number of bone matrices supported in GPU uniforms/SSBO buffers and CPU skinning pipelines.
    /// </summary>
    public const int MaxBones = 512;

    /// <summary>
    /// Default weapon grip attach bones on hunter armature.
    /// </summary>
    public const string RightWeaponAttachBone = "R_Weapon";
    public const string LeftWeaponAttachBone = "L_Weapon";
    public const string RightHandBone = "R_Hand";
    public const string LeftHandBone = "L_Hand";
}
