using System.Runtime.InteropServices;

namespace ReeLib.Efx.Structs.Common;

// 成对数值的语义类型。
// 引擎文件里的成对值统一存成 via.Range（两个 float，先 s 后 r）、via.RangeI（两个 int，先 s 后 r）或 via.Int2（x, y），
// 但不同属性对这两个数的用法不同：有的是"中心 ± 幅度"，有的是"最小–最大"，有的根本不随机。
// 下面这些类型与对应的 via.* 逐字节相同，只是字段按语义命名；字段顺序 = 文件里的存储顺序。
// 语义是从 re4.exe 读码得到的，见各属性字段上的注释。没核对过语义的字段仍用 via.Range / via.RangeI。

/// <summary>中心 ± 幅度：每个粒子取 center + (2u−1)·spread（spread ≤ 1e-6 时直接取 center）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CenterSpread
{
    public float center;
    public float spread;

    public CenterSpread(float center, float spread) { this.center = center; this.spread = spread; }
    public override readonly string ToString() => $"{center} ± {spread}";
}

/// <summary>整数中心 ± 幅度：center + randint[−spread, +spread]。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CenterSpreadI
{
    public int center;
    public int spread;

    public CenterSpreadI(int center, int spread) { this.center = center; this.spread = spread; }
    public override readonly string ToString() => $"{center} ± {spread}";
}

/// <summary>在 [min, max] 内均匀取值（两端都含）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MinMax
{
    public float min;
    public float max;

    public MinMax(float min, float max) { this.min = min; this.max = max; }
    public override readonly string ToString() => $"[{min}, {max}]";
}

/// <summary>在 [min, max] 内均匀取整数（两端都含）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MinMaxI
{
    public int min;
    public int max;

    public MinMaxI(int min, int max) { this.min = min; this.max = max; }
    public override readonly string ToString() => $"[{min}, {max}]";
}

/// <summary>在 [min, maxExclusive) 内取整数（上限不含）；maxExclusive ≤ min 时固定取 min。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MinMaxExclusiveI
{
    public int min;
    public int maxExclusive;

    public MinMaxExclusiveI(int min, int maxExclusive) { this.min = min; this.maxExclusive = maxExclusive; }
    public override readonly string ToString() => $"[{min}, {maxExclusive})";
}

/// <summary>下限 + 单侧随机：min + u·extra，即落在 [min, min + extra]。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MinExtra
{
    public float min;
    public float extra;

    public MinExtra(float min, float extra) { this.min = min; this.extra = extra; }
    public override readonly string ToString() => $"{min} + [0, {extra}]";
}

/// <summary>整数下限 + 单侧随机：min + randint[0, extra]，即落在 [min, min + extra]。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MinExtraI
{
    public int min;
    public int extra;

    public MinExtraI(int min, int extra) { this.min = min; this.extra = extra; }
    public override readonly string ToString() => $"{min} + [0, {extra}]";
}

/// <summary>内 / 外：在 inner 与 outer 之间插值（发射形状的内空心与外边界，圆柱高度的起点与终点）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct InnerOuter
{
    public float inner;
    public float outer;

    public InnerOuter(float inner, float outer) { this.inner = inner; this.outer = outer; }
    public override readonly string ToString() => $"{inner} → {outer}";
}

/// <summary>钳制范围：结果被夹在 [min, max] 内，不随机。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ClampRange
{
    public float min;
    public float max;

    public ClampRange(float min, float max) { this.min = min; this.max = max; }
    public override readonly string ToString() => $"clamp[{min}, {max}]";
}

/// <summary>起点 + 比例 × 跨度：start + t·span（t 是 0..1 的位置或随机数）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct StartSpan
{
    public float start;
    public float span;

    public StartSpan(float start, float span) { this.start = start; this.span = span; }
    public override readonly string ToString() => $"{start} + t·{span}";
}

/// <summary>条带锚点：width = 横向锚点（0.5 居中），length = 沿长度的锚点（0 全在粒子后，1 全在粒子前）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AnchorPair
{
    public float width;
    public float length;

    public AnchorPair(float width, float length) { this.width = width; this.length = length; }
    public override readonly string ToString() => $"width {width}, length {length}";
}
