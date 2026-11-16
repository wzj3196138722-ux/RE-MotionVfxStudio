using System.Globalization;

namespace VfxStudio.Re4Render;

/// <summary>One group of bits inside a RE4 u32 flag field. Width 1 without options is an independent
/// switch; any group with options is exclusive (one value of the group at a time).</summary>
public sealed record PreviewBitGroup(int Shift, int Width, string Label, string[]? Options = null)
{
    public uint Mask => (Width >= 32 ? uint.MaxValue : (1u << Width) - 1) << Shift;
    public bool IsSwitch => Width == 1 && Options == null;
    public uint Read(uint value) => (value & Mask) >> Shift;
    public uint Write(uint value, uint part) => value & ~Mask | part << Shift & Mask;
    public string OptionLabel(uint part) => Options != null && part < Options.Length ? $"{part}: {Options[part]}" : $"{part}: 未定义";
}

public sealed record PreviewBitField(string Summary, PreviewBitGroup[] Groups)
{
    public uint KnownMask => Groups.Aggregate(0u, (m, g) => m | g.Mask);
    public static bool TryParse(string text, out uint value) =>
        uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

/// <summary>Bit layouts of RE4 attribute flag fields, from docs/development/RE4-EFX-RUNTIME.zh-CN.md.
/// Keyed by attribute type name (as the preview backend lists it) and field path. Bits not listed here
/// are kept as they are and shown as "其他位".</summary>
public static class PreviewFieldBits
{
    private static readonly string[] Blend = ["AlphaBlend", "Physical", "AddContrast", "EdgeBlend", "Multiply"];
    private static readonly string[] TriState = ["关", "总是", "每粒子 50% 随机", "保留旧值（不要用）"];

    private static PreviewBitField ColorHeader() => new("公共颜色头：混合方式与出生颜色随机",
    [
        new(0, 4, "混合方式", Blend),
        new(4, 1, "出生颜色在 Color 与 ColorRange 之间随机"),
        new(5, 1, "位 5（常量 +0x110 = 1.0，含义未知）"),
    ]);

    private static readonly Dictionary<(string Type, string Path), PreviewBitField> Table = Build();

    public static PreviewBitField? For(string? attributeType, string path) =>
        attributeType != null && Table.TryGetValue((attributeType, path), out var field) ? field : null;

    private static Dictionary<(string, string), PreviewBitField> Build()
    {
        var t = new Dictionary<(string, string), PreviewBitField>();
        foreach (string type in new[] { "TypeBillboard3D", "TypeBillboard2D", "TypePolygon" }) t[(type, "Flags")] = ColorHeader();
        foreach (string type in new[] { "TypeRibbonFollow", "TypeRibbonLength", "TypeRibbonChain", "TypeRibbonFixEnd", "TypeRibbonTrail" })
            t[(type, "BlendFlags")] = ColorHeader();

        t[("TypePolygon", "Flags2")] = new("Polygon 朝向开关（部分待确认）",
        [
            new(2, 1, "0x04：某槽向量置 (1,1,1)"),
            new(6, 1, "0x40：朝向跟随运动方向（OrientDirectionUpVector 选上方向轴）"),
            new(7, 1, "0x80：非新粒子时锁定朝向"),
        ]);
        t[("TypeRibbonLength", "LengthFlags")] = new("RibbonLength 轴向与起点",
        [
            new(0, 1, "位 0：条带轴取本帧位移方向"),
            new(1, 1, "位 1：首次取到方向后锁定"),
            new(2, 1, "位 2：位移改由上下文矩阵测量"),
            new(3, 1, "位 3：固定起点模式"),
            new(9, 1, "位 9：固定起点不随发射器重新变换"),
            new(10, 1, "位 10（未追踪）"),
        ]);
        t[("TypeRibbonFollow", "FollowFlags")] = new("RibbonFollow 历史点",
        [
            new(0, 1, "位 0：定长绳模式"),
            new(1, 1, "位 1：历史点存发射器局部坐标"),
            new(2, 1, "位 2：调试色（跳过颜色求值）"),
            new(3, 1, "位 3：调试色（跳过颜色求值）"),
            new(4, 1, "位 4（未追踪）"),
            new(5, 1, "位 5（未追踪）"),
            new(6, 1, "位 6（未追踪）"),
        ]);
        t[("TypeRibbonFixEnd", "FixEndFlags")] = new("RibbonFixEnd 冻结与渐变",
        [
            new(0, 1, "位 0：释放时冻结整条（部分待确认）"),
            new(1, 2, "逐点颜色", ["恒定", "头尾渐变", "头尾渐变", "头尾渐变"]),
            new(3, 2, "逐点尺寸", ["1.0", "渐变", "渐变", "渐变"]),
        ]);
        t[("TypePolygonTrail", "TrailFlags")] = new("PolygonTrail 模式",
        [
            new(3, 1, "位 3：定长模式（清零为间隔模式）"),
            new(4, 1, "0x10（未追踪）"),
            new(5, 2, "逐顶点颜色", ["头部颜色", "HeadColor/Place1/Place2 渐变", "HeadColor/Place1/Place2 渐变", "HeadColor/Place1/Place2 渐变"]),
            new(7, 1, "0x80（未追踪）"),
            new(8, 1, "0x100（未追踪）"),
        ]);
        t[("TypeStrainRibbon", "Flags")] = new("StrainRibbon",
        [
            new(0, 1, "0x1：U 从 0 到 N−1"),
            new(1, 1, "0x2：终点用内嵌形状抖动"),
            new(2, 1, "0x4：起点 = InitialPosition 原值"),
            new(6, 1, "0x40：逐点颜色渐变"),
            new(7, 1, "0x80：逐点颜色渐变"),
            new(8, 1, "0x100：总用悬链线"),
            new(10, 1, "0x400：启用断裂"),
            new(13, 1, "0x2000：U 按世界长度平铺"),
            new(14, 1, "0x4000：平铺锚在远端"),
            new(15, 1, "0x8000：平铺除以粒子缩放"),
        ]);
        t[("ShaderSettings", "Flags")] = new("渲染参数开关",
        [
            new(2, 1, "位 2：软粒子（SoftParticleDistance 生效）"),
            new(9, 1, "位 9：光照相关"),
            new(10, 1, "位 10：光照相关"),
            new(11, 1, "位 11：恒置"),
            new(13, 1, "位 13：StencilTarget"),
            new(16, 1, "位 16：每粒子光照缓冲"),
        ]);
        t[("ShaderSettings", "ParticleLighting")] = new("粒子受光（取值名部分待确认）",
        [
            new(0, 32, "受光", ["不受光", "受光 n=1", "受光 n=2", "受光 n=4", "受光 n=8", "按 0 分配", "按 n=1"]),
        ]);
        t[("AlphaCorrection", "Flags")] = new("逐纹素 alpha 修正（只读低 2 位）",
        [
            new(0, 2, "模式", ["Disable", "AlphaCurve", "AlphaThreshold", "LifePower"]),
        ]);
        t[("FadeByAngle", "Flags")] = new("按角度淡出",
        [
            new(0, 1, "位 0：双锥"),
            new(1, 1, "位 1：反转"),
            new(2, 1, "位 2：从粒子看"),
        ]);
        t[("RgbCommon", "Flags")] = new("RGB 通道",
        [
            new(0, 1, "位 0：绿通道随机"),
            new(1, 1, "位 1：红通道随机"),
            new(2, 1, "位 2：RedChAlphaToB 只作用于红层覆盖"),
        ]);
        t[("UVSequence", "Flags")] = new("翻页动画",
        [
            new(0, 2, "PlayMode", ["静止", "循环", "播一遍后粒子死亡", "播一遍停在末帧"]),
            new(2, 2, "FlipU", TriState),
            new(4, 2, "FlipV", TriState),
            new(6, 2, "Reverse（倒放）", TriState),
            new(8, 2, "Rotate90（仅 GPU 路径）", TriState),
            new(10, 1, "位 10：FrameBlend 前后帧交叉淡化"),
            new(11, 1, "位 11（未知）"),
        ]);
        t[("UVSequenceModifier", "Flags")] = new("翻页修正",
        [
            new(0, 1, "位 0：启用"),
            new(1, 1, "第三项", ["帧数", "系数"]),
        ]);
        t[("Life", "Flags")] = new("保持模式（只读低 2 位）",
        [
            new(0, 2, "保持模式", ["无", "KeepHold", "KeepHold，释放时重放 Keep", "CPU 每帧 alpha = 0"]),
        ]);
        t[("Velocity3D", "Flags")] = new("初速度",
        [
            new(0, 1, "位 0：继承发射器速度"),
            new(2, 1, "位 2：法线不乘形状旋转、重力固定 (0,1,0)"),
            new(5, 1, "位 5：启用 SpeedDelayFrame"),
            new(6, 1, "位 6：启用 GravityDelayFrame"),
        ]);
        t[("RotateAnim", "Flags")] = new("持续自转",
        [
            new(0, 1, "位 0：随机方向（每轴独立）"),
        ]);
        t[("PtLife", "Flags")] = new("生命阶段触发（只读位 0..4）",
        [
            new(0, 1, "位 0：发射器停止或粒子死亡时释放子实例"),
            new(1, 1, "位 1：Mesh 粒子时子矩阵再乘旋转"),
            new(2, 1, "位 2：每帧跟随位置"),
            new(3, 1, "位 3：每帧跟随旋转"),
            new(4, 1, "位 4：每帧跟随缩放"),
        ]);
        // Bits 0/1 decide whether LayerName/MaskName strings exist in the file; they stay in "其他位".
        t[("PtColliderAction", "Flags")] = new("命中触发（位 0/1 决定层名、掩码名是否存在，只能用精确值改）",
        [
            new(2, 1, "位 2：命中即杀死粒子"),
            new(3, 1, "位 3：用 Radius 球扫掠"),
        ]);
        return t;
    }
}
