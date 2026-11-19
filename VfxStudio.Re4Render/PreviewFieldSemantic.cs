using System.Globalization;

namespace VfxStudio.Re4Render;

/// <summary>One group of bits inside a RE4 u32 flag field. Width 1 without options is an independent
/// switch; any group with options is exclusive (one value of the group at a time). Locked bits decide which
/// fields exist in the file and are changed through the exact value only.</summary>
public sealed record PreviewBitGroup(int Shift, int Width, string Label, string[]? Options = null, bool Locked = false)
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

/// <summary>When a field takes effect: a test on another field of the same attribute (LiveEditor FieldCondition).
/// Shift / Width pick a bit group; In / Not list values; NonZero means "not 0 / false"; Any / All combine tests.</summary>
public sealed record PreviewFieldCondition(string? Field, int? Shift, int? Width, string[]? In, string[]? Not, bool? NonZero,
    PreviewFieldCondition[]? Any, PreviewFieldCondition[]? All, string? Why);

/// <summary>A field's meaning as the backend inspector reports it (LiveEditor FieldSemantics, read from re4.exe).</summary>
/// <param name="Status">What about the field is not settled, or null.</param>
/// <param name="When">The mode in which the engine reads the field, or null when it always does.</param>
public sealed record PreviewFieldSemantic(string? Help, string? Unit, string? Status, PreviewBitField? Bits, PreviewFieldCondition? When = null)
{
    /// <summary>Tooltip text: meaning, unit, then the part of it that is not settled.</summary>
    public string Text => string.Join("\n", new[] { Help, Unit == null ? null : "单位：" + Unit, Status == null ? null : "未确定：" + Status }
        .Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>How a pair struct is sampled, from its CLR type (VfxStudio.Core EfxPairTypes.cs). Pairs still declared
/// as via.Range / via.RangeI have not been checked against re4.exe and are marked as such.</summary>
/// <param name="Label">Shown after the field name.</param>
/// <param name="Prefix">Text before the first value, Separator between them, Suffix after the second.</param>
/// <param name="Formula">How the engine uses the two numbers.</param>
/// <param name="Parts">Names of the two values, in file order.</param>
public sealed record PreviewPairKind(string Label, string Prefix, string Separator, string Suffix, string Formula, string[] Parts, bool Unconfirmed = false)
{
    private const string Common = "ReeLib.Efx.Structs.Common.";
    private static readonly Dictionary<string, PreviewPairKind> Kinds = new(StringComparer.Ordinal)
    {
        [Common + "CenterSpread"] = new("中心 ± 幅度", "", "±", "", "每粒子出生时 center + (2u−1)·spread，u ∈ [0,1)。", ["中心", "幅度"]),
        [Common + "CenterSpreadI"] = new("中心 ± 幅度", "", "±", "", "center + 随机整数 [−spread, +spread]。", ["中心", "幅度"]),
        [Common + "MinMax"] = new("最小 ~ 最大", "", "~", "", "在 [min, max] 内均匀取值。", ["最小", "最大"]),
        [Common + "MinMaxI"] = new("最小 ~ 最大", "", "~", "", "在 [min, max] 内均匀取整，两端都含；max = min − 1 会让游戏除以零。", ["最小", "最大"]),
        [Common + "MinMaxExclusiveI"] = new("[最小, 上限)", "[", ",", ")", "在 [min, maxExclusive) 内取整，上限不含；固定值写 {n, n+1}。", ["最小", "上限（不含）"]),
        [Common + "MinExtra"] = new("最小 + [0, 增量]", "", "+ [0,", "]", "min + u·extra，只往上加。", ["最小", "增量"]),
        [Common + "MinExtraI"] = new("最小 + [0, 增量]", "", "+ [0,", "]", "min + 随机整数 [0, extra]；extra = −1 会让游戏除以零。", ["最小", "增量"]),
        [Common + "InnerOuter"] = new("内 → 外", "", "→", "", "按形状采样的 t 在 inner 与 outer 之间插值。", ["内", "外"]),
        [Common + "StartSpan"] = new("起点 + t × 跨度", "", "+ t ×", "", "start + t·span，t 为随机数或均分位置。", ["起点", "跨度"]),
        [Common + "ClampRange"] = new("钳制 [最小, 最大]", "[", ",", "]", "不随机：把结果夹在 [min, max]。", ["最小", "最大"]),
        [Common + "AnchorPair"] = new("宽向 / 长向锚点", "", "/", "", "两个独立的锚点，不随机。", ["宽向锚点", "长向锚点"]),
        ["ReeLib.via.Range"] = new("s / r（用法未确认）", "", "/", "", "文件里先 s 后 r。这个字段的用法还没从 re4.exe 核对，不要假设是最小/最大或中心/幅度。", ["s", "r"], true),
        ["ReeLib.via.RangeI"] = new("s / r（用法未确认）", "", "/", "", "文件里先 s 后 r（整数）。这个字段的用法还没从 re4.exe 核对。", ["s", "r"], true),
    };

    public static PreviewPairKind? For(string? ownerType) => ownerType != null && Kinds.TryGetValue(ownerType, out var kind) ? kind : null;
}
