using System.Runtime.InteropServices;

namespace VfxStudio.Renderer;

/// <summary>
/// GPU Hardware acceleration configuration and native exports to force high-performance discrete GPU.
/// </summary>
public static class GpuConfig
{
    /// <summary>
    /// NVIDIA Optimus export to force discrete GPU execution on hybrid GPU laptops/workstations.
    /// 0x00000001 = Enable high-performance discrete GPU.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "NvOptimusEnablement")]
    public static uint NvOptimusEnablement() => 0x00000001;

    /// <summary>
    /// AMD PowerXpress export to force discrete high-performance GPU execution.
    /// 1 = Enable high-performance discrete GPU.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "AmdPowerXpressRequestHighPerformance")]
    public static int AmdPowerXpressRequestHighPerformance() => 1;

    /// <summary>
    /// Maximum supported bone capacity in GPU shaders and SSBO/UBO buffers.
    /// Set to 512 to fully accommodate Monster Hunter Wilds full hunter skeleton (393+ bones).
    /// </summary>
    public const int MaxBones = 512;

    /// <summary>
    /// Target OpenGL major and minor version (4.6 Core Profile).
    /// </summary>
    public const int GlMajorVersion = 4;
    public const int GlMinorVersion = 6;
}
