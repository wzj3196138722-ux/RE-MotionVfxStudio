using System.Numerics;
using ReeLib.Efx.Enums;
using ReeLib.Efx.Structs.Common;
using ReeLib.InternalAttributes;
using ReeLib.via;

namespace ReeLib.Efx.Structs.Misc;

public enum DistortionType
{
	Blur = 0,
	Refract = 1,
	BlurTexture = 2
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.DepthOperator, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeDepthOperator : ReeLib.Efx.EFXAttribute
{
    public EFXAttributeDepthOperator() : base(EfxAttributeType.DepthOperator) { }

    public uint unkn0;
    public float unkn1;
    public float unkn2;
    [RszVersion(EfxVersion.RE8)]
    public float unkn3;

    [RszVersion(EfxVersion.RE4, EndAt = nameof(unkn12))]
    public float unkn4;
    public UndeterminedFieldType unkn5;
    [RszVersion(EfxVersion.MHWilds)] public float mhws_unkn1;
    public float unkn6;
    public UndeterminedFieldType unkn7;
    public float unkn8;
    public UndeterminedFieldType unkn9;
    public float radians10;
    [RszVersion(EfxVersion.MHWilds)] public UndeterminedFieldType mhws_unkn2;
    public float unkn11;
    public UndeterminedFieldType unkn12;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Distortion, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4)]
public partial class EFXAttributeDistortion : EFXAttribute
{
	public EFXAttributeDistortion() : base(EfxAttributeType.Distortion) { }

	// 名字来自 re4.exe（加载器 3d28ad0 读 UniqueID + 0x14 字节）。MHWI 的 REFRACTION 就是这个属性，
	// MHWI 的 alphaBlend 实际是 Influence。
	public DistortionType DistortionType;
	// 公式读自 primitivevfx_2/_3 的像素着色器；Gpu* 类型的绘制着色器没有场景颜色贴图，放在 Gpu* 上无效。
	// 强度，先乘粒子 alpha α。Refract：偏移 = Influence·α·(2·贴图RG − 1)，视空间米；
	// Blur：水平 9 点高斯，间距 Influence·α 像素；BlurTexture：沿贴图 RG 方向单侧 9 点拖影，步长 Influence·α 像素。
	// 最终颜色 = 场景采样 × lerp(1, 顶点色.rgb + 高光, α)。
	public float Influence;
	// 高光 = SpecularColor.rgb·SpecularColor.a · 2^SpecularIntensity · B^(2.2·SpecularPower)，B = 贴图蓝通道；
	// SpecularPower < 1e-6 时关闭。三种类型都会加。

	[RszVersion(EfxVersion.RE3, EndAt = nameof(SpecularColor))]
	public float SpecularPower;
	public float SpecularIntensity;
	[RszVersion(EfxVersion.RE8)]
	public via.Color SpecularColor;

	[RszVersion(EfxVersion.DD2, EndAt = nameof(FadeScreenEdge))]
	public float AlphaBlend;
	public bool FadeScreenEdge;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.DistortionExpression, EfxVersion.DD2)]
public partial class EFXAttributeDistortionExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeDistortionExpression() : base(EfxAttributeType.DistortionExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(5);
	public ExpressionAssignType unkn1;
	public ExpressionAssignType unkn2;
	public ExpressionAssignType unkn3;
	public ExpressionAssignType unkn4;
	public ExpressionAssignType unkn5;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ProceduralDistortion, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeProceduralDistortion : ReeLib.Efx.EFXAttribute
{
    public EFXAttributeProceduralDistortion() : base(EfxAttributeType.ProceduralDistortion) { }

    [RszVersion(EfxVersion.RE8)]//TODO Verify older versions
	public uint Flags;
	public float UScale;
	public float VScale;
	public float WaveFrequency;
	public float WaveAmplitude;
	public float WaveAmplitudeCoef;

    [RszVersion(EfxVersion.RE8, EndAt = nameof(RadialOscillationAmplitudeNoiseAmplitude))]
	public float WaveOffset;
	public float WaveTimer;
	public float SpinInit;
	public float SpinMax;
	public float SpinTimer;
	public float SpinTimerCoef;
	public float RadialOscillationFrequency;
	public float RadialOscillationAmplitude;
	public float RadialOscillationFrequencyNoiseFrequency;
	public float RadialOscillationFrequencyNoiseAmplitude;
	public float RadialOscillationAmplitudeNoiseFrequency;
	public float RadialOscillationAmplitudeNoiseAmplitude;
	public float RadialOscillationTimer;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ProceduralDistortionDelayFrame, EfxVersion.RE4)]
public partial class EFXAttributeProceduralDistortionDelayFrame : EFXAttribute
{
	public EFXAttributeProceduralDistortionDelayFrame() : base(EfxAttributeType.ProceduralDistortionDelayFrame) { }

	public uint frameDelay;
	public UndeterminedFieldType unkn;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ProceduralDistortionClip, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeProceduralDistortionClip : EFXAttribute, IClipAttribute
{
    public EfxClipData Clip => clipData;
    public BitSet ClipBits => clipBits;

    public EFXAttributeProceduralDistortionClip() : base(EfxAttributeType.ProceduralDistortionClip) { }

	[RszClassInstance] public BitSet clipBits = new BitSet(7);
    public UndeterminedFieldType unkn1;
	[RszClassInstance] public EfxClipData clipData = new();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ProceduralDistortionExpression, EfxVersion.DD2)]
public partial class EFXAttributeProceduralDistortionExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeProceduralDistortionExpression() : base(EfxAttributeType.ProceduralDistortionExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(18);
	public ExpressionAssignType unkn1;
	public ExpressionAssignType unkn2;
	public ExpressionAssignType unkn3;
	public ExpressionAssignType unkn4;
	public ExpressionAssignType unkn5;
	public ExpressionAssignType unkn6;
	public ExpressionAssignType unkn7;
	public ExpressionAssignType unkn8;
	public ExpressionAssignType unkn9;
	public ExpressionAssignType unkn10;
	public ExpressionAssignType unkn11;
	public ExpressionAssignType unkn12;
	public ExpressionAssignType unkn13;
	public ExpressionAssignType unkn14;
	public ExpressionAssignType unkn15;
	public ExpressionAssignType unkn16;
	public ExpressionAssignType unkn17;
	public ExpressionAssignType unkn18;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FakeDoF, EfxVersion.DMC5)]
public partial class EFXAttributeFakeDoF : ReeLib.Efx.EFXAttribute
{
    public EFXAttributeFakeDoF() : base(EfxAttributeType.FakeDoF) { }

    public float NearDistance;
    public float MaxScale;
    public float MinAlpha;
    public uint UsingMipLevel;
    public float VanishDistance;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.WindInfluence3D, EfxVersion.RE8, EfxVersion.RE4)]
public partial class EFXAttributeWindInfluence3D : EFXAttribute
{
	public EFXAttributeWindInfluence3D() : base(EfxAttributeType.WindInfluence3D) { }

	public via.Range InfluenceRate;
	public via.Range InfluenceCoef;
	public via.RangeI InfluenceFrame;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.WindInfluence3DDelayFrame, EfxVersion.DD2)]
public partial class EFXAttributeWindInfluence3DDelayFrame : EFXAttribute
{
	public EFXAttributeWindInfluence3DDelayFrame() : base(EfxAttributeType.WindInfluence3DDelayFrame) { }

	public uint frameDelay;
	public uint unkn2;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.DepthOcclusion, EfxVersion.RE8)]
public partial class EFXAttributeDepthOcclusion : EFXAttribute
{
	public EFXAttributeDepthOcclusion() : base(EfxAttributeType.DepthOcclusion) { }

	public uint Flags;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PlaneCollider, EfxVersion.RE8, EfxVersion.RE4)]
public partial class EFXAttributePlaneCollider : EFXAttribute
{
	public EFXAttributePlaneCollider() : base(EfxAttributeType.PlaneCollider) { }

    public uint Flags;
	public Vector3 Position;
    public Vector3 Rotation;
    public via.RangeI BounceNum;
    public via.Range BounceRate;
    public Vector3 FaceNormalRotation;
    public via.RangeI IdleTime;

}


[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FixRandomGenerator, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeFixRandomGenerator : EFXAttribute
{
	public EFXAttributeFixRandomGenerator() : base(EfxAttributeType.FixRandomGenerator) { }

	// re4.exe 里没有这个属性的字段名字符串（vtable 7a12670，加载器 3d88390 读 UniqueID + 0x28 字节，构建器 3d88440），
	// 名字是按作用起的。发射器构建时从种子表里挑一个当发射器的随机种子（E+0x110）：
	// 槽位 = hash % useRandomSeedTableCount；种子为 -1 表示改用新的随机种子。
	// 候选槽位数（不是抽取次数）。count > 1 且没有分组时，各发射器独立挑槽，层与层可能对不上。
	public int useRandomSeedTableCount;
	public int randomSeedTable0;//TODO Make this into a fixed array of 8?
	public int randomSeedTable1;
	public int randomSeedTable2;
	public int randomSeedTable3;
	[RszVersion(EfxVersion.RE3, EndAt = nameof(tableSelectionGroup))]
	public int randomSeedTable4;
	[RszVersion(">", EfxVersion.RE3, EndAt = nameof(tableSelectionGroup))]
	public int randomSeedTable5;
	public int randomSeedTable6;
	public int randomSeedTable7;
	// 分组哈希键（-1 = 不分组），引擎把它加进挑槽用的哈希；同组的发射器挑到同一槽位。不是位掩码。
	public int tableSelectionGroup;//This value may be incorrect for older games, could be in a different spot
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.EffectShader, EfxVersion.DD2)]
public partial class EFXAttributeEffectOptimizeShader : EFXAttribute
{
	public EFXAttributeEffectOptimizeShader() : base(EfxAttributeType.EffectShader) { }

	public uint unknShaderCRCHash0;
	public uint unknShaderCRCHash1;
	public uint unknShaderCRCHash2;
    public uint unknShaderCRCHash3;
    public uint unknShaderCRCHash4;
    public uint unknShaderCRCHash5;
    public UndeterminedFieldType unkn3;

    public uint unknShaderCRCHash6;
    public uint unknShaderCRCHash7;
    public uint unknShaderCRCHash8;
    public uint unknShaderCRCHash9;
    public uint unknShaderCRCHash10;
    public uint unknShaderCRCHash11;
    public UndeterminedFieldType unkn10;

    public uint unknShaderCRCHash12;
    public uint unknShaderCRCHash13;
    public uint unknShaderCRCHash14;
    public uint unknShaderCRCHash15;
    public uint unknShaderCRCHash16;
    public uint unknShaderCRCHash17;
    public UndeterminedFieldType unkn18;

	[RszVersion(EfxVersion.MHWilds, EndAt = nameof(unknShaderCRCHash20))]
    public uint unknShaderCRCHash18;
    public uint unknShaderCRCHash19;
    public uint unknShaderCRCHash20;

    public uint unknShaderCRCHash21;
    public ByteSet unkn20;

	[RszInlineWString(ByteSize = true)] public string? shaderPath;

    public override string ToString() => !string.IsNullOrEmpty(shaderPath) ? shaderPath : type.ToString();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Attractor, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeAttractor : EFXAttribute, IBoneRelationAttribute
{
	public EFXAttributeAttractor() : base(EfxAttributeType.Attractor) { }

	public uint Flags;
	// MHWs-EFX-Editor 本地补丁：原名 AttractPosition。实机确认只有 Flags bit1(数值1)
	// 打开时这个 Vector3 才会作为世界/固定参考系目标点分量生效（bit1=0 时改任意值都测不出
	// 效果）；bit1=1 时和下面的 AttractPositionLocal 矢量相加，共同决定最终吸引目标点。
	// 改名加 World 后缀跟 Local 分量对称，见 tools/vendor-patches/README.md #0007。
	public Vector3 AttractPositionWorld;

	// MHWs-EFX-Editor 本地补丁：vendor 原声明是 `via.Range ForceScale` 全部 + `via.Range
	// ReversalForceScale` 的前半（`.s`/`.r`+`.s`），三个字段边界切错了位置——这连续 12 字节
	// 实际是一个 Vector3。实机测试单独改 X/Y/Z 任意一维都能让粒子群朝对应坐标轴偏移吸引
	// 目标点，效果和 AttractPositionWorld 相同、可矢量叠加，而且**恒常开**（不受 Flags
	// 门控），跟随发射器局部变换。见 0007 patch。
	public Vector3 AttractPositionLocal;

	// MHWs-EFX-Editor 本地补丁：原 ReversalForceScale.r。实机振荡周期测试确认是弹簧力常数
	// （数值越大，120 帧生命周期内粒子往返吸引点的振荡次数越多）；"Reversal" 前缀是 vendor
	// 原错误分组遗留，这个值本身没有"反向"含义。跟下面 ForceBiRand 不能合并成一个
	// via.Range——两者版本可用性不同（这个恒可用，那个只在 DD2+），合并会在 RE7RT/RE4 文件
	// 上错误多读/少读字节，故拆成两个独立 float。
	public float ForceStatic;

	[RszVersion(EfxVersion.DD2, EndAt = nameof(ForceResist))]//TODO Verify older game fields
	// MHWs-EFX-Editor 本地补丁：原 ReversalDistance.s。实机确认公式是双向对称随机
	// `[ForceStatic - x, ForceStatic + x]`，不是标准 via.Range 的 Static+Random
	// `[Static, Static+Random]`——取 x 和 -x 效果完全相同，只有取绝对值参与计算才能解释。
	// 详见 docs/PITFALLS.md #29、model.is_static_birand_node()。
	public float ForceBiRand;
	// 原 ReversalDistance.r，语义未知——"ReversalDistance" 这个分组名已经证明是错的，这个
	// float 自己也不属于任何一对，先不强行归类，占位 unkn1。
	public float unkn1;
	public float ForceResist;

	[RszVersion(EfxVersion.MHWilds, EndAt = nameof(ShapeRotation))]
	// MHWs-EFX-Editor 本地补丁：全语料 1105 个实例的取值分布证实这两个字段是 float，不是
	// 未定型的裸 int——非零取值按 IEEE754 位模式解出来全是干净的十进制数（原 unknWild1: 0.1/
	// 0.2/0.3/0.8/1.0/8.0/10.0；unknWild10 仅在 Flags==8 时非零，落在 -0.68~2.0 弧度），
	// 按裸 int 读会是 1036831949 这种没有意义的天文数字。见 tools/vendor-patches/README.md。
	// 原 unknWild1 已实机确认是阻尼/阻力系数（0.5=运动明显变慢，1.0=看不出运动），MHWilds 下
	// 老字段 ForceResist 实测全语料 1104/1105 恒为 0（已废弃），这个字段接管了它的角色，
	// 故按其真实作用重命名为 ForceResistWilds；unknWild10 语义仍未知，字段名不动。
	public float ForceResistWilds;
	public UndeterminedFieldType unknWild2;

	// MHWs-EFX-Editor 本地补丁：原 unknWild3 是一个 via.Range，但 `.s`/`.r` 是两个互不相关的
	// 独立量，不是 Static/Random 一对，拆成两个 float——**声明顺序必须保持 s 在前、r 在后**
	// （`via.Range` 自身是 `{float s; float r;}`，字节序不因为拆分改变）。
	// 原 unknWild3.s：实机观测到会让粒子群从球壳收缩成正八面体、再收缩成十字（越偏离 XYZ 轴
	// 的粒子受力越强），单粒子追踪确认轨迹是直线段（不是旋转/切向力，之前的"风车状"是多粒子
	// 不同振荡相位叠加出的拖影）。用户推导：疑似让 XYZ 分量各自独立收缩的力，负值会让粒子
	// 经过吸引点附近后直接沿某个 XYZ 方向高速飞出——这是推导结论，不是逐条单独实机验证过，
	// 按此定名 AttractAxisBias，具体模型仍有待用不同 ES3D 形状/吸引点位置继续验证。
	public float AttractAxisBias;
	// 原 unknWild3.r：实机确认是吸引力的最大作用距离阈值，粒子与目标点距离超过这个值就完全
	// 不受力；-1 是"无限制"哨兵值（语料里 84% 取 -1；此前"非 -1 就全体静止"的现象正是因为
	// 超出了有限阈值导致完全无力）。
	public float MaxAttractDistance;

	// MHWs-EFX-Editor 本地补丁：原 unknWild4/unknWild5/unknWild6 三个 via.Range 共 6 个
	// float，真实边界比 vendor 声明的晚 4 字节（同一种错位模式，见 ForceStatic/ForceBiRand
	// 那对的说明），重新切成"1 个独立 float + 2 对 via.Range + 1 个独立 float"：
	// 原 unknWild4.s：实机确认——粒子与目标点距离在这个半径以内（含球面）时进入一个特殊区域，
	// 使用下面 ZoneVelocityMultiplier 的乘数逻辑，而不是正常的弹簧力。
	public float MultZoneRadius;
	// 原 unknWild4.r + unknWild5.s：区域内逐帧对粒子速度做乘数——Static≈1 时区域效果被完全
	// 忽略（等价于不受影响的正常弹簧力），<1 明显减速，>1 明显加速。是否遵守标准
	// Static+Random 公式（vs ForceBiRand 那种双向对称）尚未验证。
	public via.Range ZoneVelocityMultiplier;
	// 原 unknWild5.r + unknWild6.s：疑似吸引效果的生成延迟，Static=基准延迟、Random=随机
	// 浮动，公式未验证，单位未知。
	public via.Range SpawnDelay;
	// MHWs-EFX-Editor 本地补丁：原 `unknWild6.r`~`endwilds`（1 个独立 float + 3 个 via.Range +
	// 1 个独立 float，共 8 个 float）实机测试确认是跟 `MultZoneRadius` 定义的区域配套的一套
	// "形状"参数，边界同样比 vendor 声明的晚半格——真实的三对 Range 是
	// `unknWild6.r`+`unknWild7.s`、`unknWild7.r`+`unknWild8.s`、`unknWild8.r`+`unknWild9.s`，
	// 最后 `unknWild9.r`+`unknWild10`+`endwilds` 三个连续 float 全部实机确认是角度
	// （`unknWild10` 绕全局 Y），命名上直接对齐 `EFXAttributeEmitterShape3D` 的
	// `RangeX/Y/Z`+`Vector3 LocalRotation` 这套惯例，加 `Shape` 前缀区分（这里不是 ES3D 本身，
	// 只是疑似复用了同一套参数化思路）。
	//
	// 三对 Range 各自的公式（Static+Random、min/max 还是 `ForceBiRand` 那种双向对称）尚未
	// 单独验证，`ShapeRotation` 三轴各自的旋转顺序/是否有欧拉角万向锁问题也没验证，先只按
	// "这三个字段在一起、语义是角度"这个已确认的部分改结构，公式细节留到确认后再补充。
	//
	// ⚠ 已知异常：给 `ShapeRangeX.s`+`ShapeRangeY.s`+`ShapeRangeZ.s` 各设 1、`Flags=8` 时，
	// 实机看到的是一个**始终正对摄像机**的正方形边框，角色自转不会带着它转——这不是典型的
	// 世界/局部空间几何体该有的行为，可能这套参数根本不是定义"物理空间的一个立体"，语义
	// 还没有最终定论，命名先按结构对齐 ES3D，不代表已经确认是"形状"本身。另外语料里
	// `Flags=8` 从不单独出现（只见于 8/24/26，即总带着数值 16 那一位），疑似 16 是这套参数的
	// 形状类型开关（类似 `ShapeType`），待验证。
	public via.Range ShapeRangeX;
	public via.Range ShapeRangeY;
	public via.Range ShapeRangeZ;
	public Vector3 ShapeRotation;

	[RszInlineWString(ByteSize = true)] public string? boneName;

    public string? ParentBone { get; set; }

    public override string ToString() => !string.IsNullOrEmpty(boneName) ? boneName : type.ToString();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.AttractorClip, EfxVersion.RE8)]
public partial class EFXAttributeAttractorClip : EFXAttribute, IClipAttribute
{
    public EfxClipData Clip => clipData;
    public BitSet ClipBits => clipBits;

	public EFXAttributeAttractorClip() : base(EfxAttributeType.AttractorClip) { }

	[RszClassInstance] public BitSet clipBits = new BitSet(7);
	public uint unkn1;
	[RszClassInstance] public EfxClipData clipData = new();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.AttractorExpression, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeAttractorExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeAttractorExpression() : base(EfxAttributeType.AttractorExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(11) { BitNameDict = new () {
		[1] = nameof(posX),
		[2] = nameof(posY),
		[3] = nameof(posZ),
		[4] = nameof(posX_2),
		[5] = nameof(posY_2),
		[6] = nameof(posZ_2),
		[7] = nameof(attractionForce),
	} };
	public ExpressionAssignType posX;
	public ExpressionAssignType posY;
	public ExpressionAssignType posZ;
	public ExpressionAssignType posX_2;
	public ExpressionAssignType posY_2;
	public ExpressionAssignType posZ_2;
	public ExpressionAssignType attractionForce;
	public ExpressionAssignType unkn8;
	public ExpressionAssignType unkn9;
	public ExpressionAssignType unkn10;

	[RszVersion(EfxVersion.MHWilds)]
	public ExpressionAssignType unkn11_Wilds;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Blink, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeBlink : EFXAttribute
{
	public EFXAttributeBlink() : base(EfxAttributeType.Blink) { }

	// 名字来自 re4.exe（vtable 7a16c68，加载器读 UniqueID + 0x28 字节，RE4 没有 Flags；逐粒子函数 3dc7a20）。
	// 两个通道都是三角波（0 → 宽度 → 0，永不为负），不是正弦。结果 = clamp(低频 + 高频, MinRate, MaxRate)，
	// 乘进粒子最终 alpha（与 Life 渐隐相乘，不逐帧累积）。初相位每粒子随机 [0,1) 周期。四个区间对都在出生时按
	// center + (2u-1)·spread 抽一次。
	[RszVersion(EfxVersion.DD2)]
	public uint Flags;
	public float MinRate;
	public float MaxRate;
	// 每秒周期数（按真实时间 dt 推进，没有 60fps 假设）。0 = 冻结在随机初相位（alpha 恒定，不是 0）。
	public CenterSpread LowFrequency;
	// 低频通道峰值幅度；0 关闭该通道。
	public CenterSpread LowFrequencyWidth;
	// 不是时间频率：每个低频周期里的波纹数（高频相位 = fract(HighFrequency · 低频相位)）。0 = 该通道恒为 0。
	public CenterSpread HighFrequency;
	// 高频通道峰值幅度。
	public CenterSpread HighFrequencyWidth;

}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ColorGrading, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeColorGrading : EFXAttribute
{
	public EFXAttributeColorGrading() : base(EfxAttributeType.ColorGrading) { }

	public via.Color HighBrightnessColor;
	public via.Color LowBrightnessColor;
	public float HighBrightnessIntensity;
	public float LowBrightnessIntensity;
	public float HighGradingBorder;
	public float LowGradingBorder;

}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ContrastHighlighter, EfxVersion.RE4)]
public partial class EFXAttributeContrastHighlighter : EFXAttribute
{
	public EFXAttributeContrastHighlighter() : base(EfxAttributeType.ContrastHighlighter) { }

	public float Threshold;
	public float PeakMultiplier;
	public float EdgeLimiter;
	public float LuminanceScale;
	public uint HighlightColor;
	public float HighlightIntensity;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.DrawOverlay, EfxVersion.DMC5, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.MHWilds)]
public partial class EFXAttributeDrawOverlay : EFXAttribute
{
	public EFXAttributeDrawOverlay() : base(EfxAttributeType.DrawOverlay) { }

	public uint Segment;
	public uint Priority;
	public uint CameraID;

	[RszVersion(EfxVersion.MHWilds)]
	public UndeterminedFieldType UnknWilds;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.EmitMask, EfxVersion.RE4)]
public partial class EFXAttributeEmitMask : EFXAttribute
{
	public EFXAttributeEmitMask() : base(EfxAttributeType.EmitMask) { }

	public uint mask;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.IgnorePlayerColor, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RERT)]
public partial class EFXAttributeIgnorePlayerColor : EFXAttribute
{
	public EFXAttributeIgnorePlayerColor() : base(EfxAttributeType.IgnorePlayerColor) { }
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.LuminanceBleed, EfxVersion.RERT, EfxVersion.RE4)]
public partial class EFXAttributeLuminanceBleed : EFXAttribute
{
	public EFXAttributeLuminanceBleed() : base(EfxAttributeType.LuminanceBleed) { }

    public LuminanceBleedType BleedType;
	[RszVersion(EfxVersion.RE3)]
    public LuminanceBleedSamplingType SamplingType;
    public float Bleed;

    public float Slide;
    public float ColorScaler;
    public float ColorBias;
    public float TexelScaler;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Noise, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeNoise : EFXAttribute
{
	public EFXAttributeNoise() : base(EfxAttributeType.Noise) { }

	public via.Range LowFrequency;
	public via.Range LowFrequencyWidth;
	public via.Range HighFrequency;
	public via.Range HighFrequencyWidth;

}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.NoiseExpression, EfxVersion.RE4)]
public partial class EFXAttributeNoiseExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeNoiseExpression() : base(EfxAttributeType.NoiseExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(8);
    public ExpressionAssignType unkn1;
    public ExpressionAssignType unkn2;
    public ExpressionAssignType unkn3;
    public ExpressionAssignType unkn4;
    public ExpressionAssignType unkn5;
    public ExpressionAssignType unkn6;
    public ExpressionAssignType unkn7;
    public ExpressionAssignType unkn8;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.StretchBlur, EfxVersion.RE4)]
public partial class EFXAttributeStretchBlur : EFXAttribute, IBoneRelationAttribute
{
	public EFXAttributeStretchBlur() : base(EfxAttributeType.StretchBlur) { }

    public uint Flags;
    public uint TargetStencil;//<comment="Doesn't seem to work when this value isn't two">;
    public float StrengthBlur;
    public Vector2 ScreenFadeRange;

    public Vector2 SamplingSize;
    public float AlphaIntensity;
    public float AdjustRotationBySpeed;
    public Vector3 SamplingOffset;

    // RE-MotionVfxStudio：RE4 下 StretchBlur 也按出现顺序消费一个 BoneRelations 槽位。
    // 证据（EfxCorpusGate bonealign，RE4 3946 个文件）：不加时 27 个作用域数量对不上、110 处骨骼名不一致，加上后都是 0。
    // 不带 Rsz 标注，不参与读写，字节布局不变。
    public string? ParentBone { get; set; }
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TexelChannelOperator, EfxVersion.RE4)]
public partial class EFXAttributeTexelChannelOperator : EFXAttribute
{
	public EFXAttributeTexelChannelOperator() : base(EfxAttributeType.TexelChannelOperator) { }

	public float HueShift;
	public float HueIntensity;
	public via.Color ShadeColor;
	public float ShadeAlphaBlendRate;
	public float Desaturate;
	public float AmbientColorIntensity;
	public float EmissiveRate;
	public float EmissivePower;
	public float Opacity;
	public float HueIntensityCurve;
	public uint UseIntEnvelope;
	public via.RangeI Appear;
    public via.RangeI Keep;
    public via.RangeI Vanish;

}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.VanishArea3D, EfxVersion.RE4)]
public partial class EFXAttributeVanishArea3D : EFXAttribute, IBoneRelationAttribute
{
	public EFXAttributeVanishArea3D() : base(EfxAttributeType.VanishArea3D) { }

	public uint Flags;
    [RszVersion(EfxVersion.MHWilds)]
    public Vector3 wilds_unkn0;
    public Vector3 AreaPosition;
    public Vector3 AreaScale;
    public Vector3 AreaAngle;
	public via.Range VanishFrame;
	[RszInlineWString(ByteSize = true)] public string? JointName;

    public string? ParentBone { get; set; }

    public override string ToString() => !string.IsNullOrEmpty(JointName) ? JointName : type.ToString();
}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.VanishArea3DExpression, EfxVersion.RE4)]
public partial class EFXAttributeVanishArea3DExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeVanishArea3DExpression() : base(EfxAttributeType.VanishArea3DExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(14) { BitNameDict = new() {
		[1] = nameof(positionX),
		[2] = nameof(positionY),
		[3] = nameof(positionZ),

		// [8] = nameof(ceilingPos),
	} };
	public ExpressionAssignType positionX;
	public ExpressionAssignType positionY;
	public ExpressionAssignType positionZ;
	public ExpressionAssignType unkn4;
	public ExpressionAssignType unkn5;
	public ExpressionAssignType unkn6;
	public ExpressionAssignType unkn7;
	public ExpressionAssignType unkn8;
	public ExpressionAssignType unkn9;
	public ExpressionAssignType unkn10;
	public ExpressionAssignType unkn11;
	public ExpressionAssignType unkn12;
	public ExpressionAssignType unkn13;
	public ExpressionAssignType unkn14;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TexelChannelOperatorClip, EfxVersion.RE4)]
public partial class EFXAttributeTexelChannelOperatorClip : EFXAttribute, IClipAttribute
{
    public EfxClipData Clip => clipData;
    public BitSet ClipBits => clipBits;

    public EFXAttributeTexelChannelOperatorClip() : base(EfxAttributeType.TexelChannelOperatorClip) { }

	[RszClassInstance] public BitSet clipBits = new BitSet(2);
    uint unkn1;
	[RszClassInstance] public EfxClipData clipData = new();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.RgbCommon, EfxVersion.RE4, EfxVersion.DD2, EfxVersion.MHWilds)]
public partial class EFXAttributeRgbCommon : EFXAttribute
{
	public EFXAttributeRgbCommon() : base(EfxAttributeType.RgbCommon) { }

	public uint Flags;
	public via.Color GreenChColor;
	public via.Color GreenChColorRange;
	public float GreenChIntensity;
	public float GreenChSaturate;
	public float GreenChCurve;
	public via.Color RedChColor;
	public via.Color RedChColorRange;

	public float RedChIntensity;
	public float RedChAlphaToB;
	public float Alpha;

	public bool UseGreenChLife;
    public via.RangeI GreenChAppearFrame;
    public via.RangeI GreenChKeepFrame;
    public via.RangeI GreenChVanishFrame;

    public bool GreenChLighting;
	public LifeType GreenChLifeType;

	public byte UseRedChLife;
    public via.RangeI RedChAppearFrame;
    public via.RangeI RedChKeepFrame;
    public via.RangeI RedChVanishFrame;

    public bool RedChLighting;
	public LifeType RedChLifeType;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.RgbCommonExpression, EfxVersion.DD2)]
public partial class EFXAttributeRgbCommonExpression : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeRgbCommonExpression() : base(EfxAttributeType.RgbCommonExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(22) { BitNameDict = new() {
		[1] = "GreenChColor",
		[3] = "GreenChIntensity",
		[4] = "GreenChSaturate",
		[6] = "RedChColor",
		[8] = "RedChIntensity",
		[9] = "Alpha1",
		[10] = "Alpha2",
	} };
	public ExpressionAssignType particleColor;
	public ExpressionAssignType unkn2;
	public ExpressionAssignType colorIntensityGreen;
	public ExpressionAssignType colorSaturate;
	public ExpressionAssignType unkn5;
	public ExpressionAssignType particleColor2;
	public ExpressionAssignType unkn7;
	public ExpressionAssignType colorIntensityRed;
	public ExpressionAssignType alpha1;
	public ExpressionAssignType alpha2;
	public ExpressionAssignType unkn11;
	public ExpressionAssignType unkn12;
	public ExpressionAssignType unkn13;
	public ExpressionAssignType unkn14;
	public ExpressionAssignType unkn15;
	public ExpressionAssignType unkn16;
	public ExpressionAssignType unkn17;
	public ExpressionAssignType unkn18;
	public ExpressionAssignType unkn19;
	public ExpressionAssignType unkn20;
	public ExpressionAssignType unkn21;
	public ExpressionAssignType unkn22;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.RgbWater, EfxVersion.DD2, EfxVersion.MHWilds)]
public partial class EFXAttributeRgbWater : EFXAttribute
{
	public EFXAttributeRgbWater() : base(EfxAttributeType.RgbWater) { }

    public via.Color SpecularColor;
    [RszVersion(EfxVersion.MHWilds)]
    public float mhws_unkn;
    public float SpecularIntensity;

	public via.Color SheetColor;
	public float SheetIntensity;
	public float GtoB;
	public float Alpha;

	public bool UseSpecularLife;
	public via.RangeI SpecularAppearFrame;
    public via.RangeI SpecularKeepFrame;
    public via.RangeI SpecularVanishFrame;

    public LifeType SpecularLifeType;

    public byte UseSheetLife;
    public via.RangeI SheetAppearFrame;
    public via.RangeI SheetKeepFrame;
    public via.RangeI SheetVanishFrame;
    public LifeType SheetLifeType;

	public byte UseGtoBLife;
    public via.RangeI GtoBAppearFrame;
    public via.RangeI GtoBKeepFrame;
    public via.RangeI GtoBVanishFrame;
    public LifeType GtoBLifeType;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.RgbWaterExpression, EfxVersion.MHWilds)]
public partial class EFXAttributeRgbWaterExpression : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeRgbWaterExpression() : base(EfxAttributeType.RgbWaterExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(13) { BitNameDict = new() {
		[1] = "GreenChColor",
		[3] = "GreenChIntensity",
		[4] = "GreenChSaturate",
		[6] = "RedChColor",
		[8] = "RedChIntensity",
		[9] = "Alpha1",
		[10] = "Alpha2",
	} };
	public ExpressionAssignType particleColor;
	public ExpressionAssignType unkn2;
	public ExpressionAssignType colorIntensityGreen;
	public ExpressionAssignType colorSaturate;
	public ExpressionAssignType unkn5;
	public ExpressionAssignType particleColor2;
	public ExpressionAssignType unkn7;
	public ExpressionAssignType colorIntensityRed;
	public ExpressionAssignType alpha1;
	public ExpressionAssignType alpha2;
	public ExpressionAssignType unkn11;
	public ExpressionAssignType unkn12;
	public ExpressionAssignType unkn13;
	public ExpressionAssignType unkn14;
	public ExpressionAssignType unkn15;
	public ExpressionAssignType unkn16;
	public ExpressionAssignType unkn17;
	public ExpressionAssignType unkn18;
	public ExpressionAssignType unkn19;
	public ExpressionAssignType unkn20;
	public ExpressionAssignType unkn21;
	public ExpressionAssignType unkn22;
	public ExpressionAssignType unkn23;
	public ExpressionAssignType unkn24;
	public ExpressionAssignType unkn25;
	public ExpressionAssignType unkn26;
	public ExpressionAssignType unkn27;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FlowMap, EfxVersion.MHWilds)]
public partial class EFXAttributeFlowMap : EFXAttribute
{
	public EFXAttributeFlowMap() : base(EfxAttributeType.FlowMap) { }

	public via.Range Strength;
	public via.Range StrengthCoef;
    public via.Range Speed;
    public via.Range SpeedCoef;

    [RszInlineWString(ByteSize = true)] public string? flowmapMaskPath;

    public override string ToString() => !string.IsNullOrEmpty(flowmapMaskPath) ? flowmapMaskPath : type.ToString();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.AssignCSV, EfxVersion.RE4)]
public partial class EFXAttributeAssignCSV : EFXAttribute
{
	public EFXAttributeAssignCSV() : base(EfxAttributeType.AssignCSV) { }

    public bool RandomizeIndexOrder;
    public bool UseTableRange;
    public via.RangeI mTableRange;
    [RszInlineWString(ByteSize = true)] public string? efcsvPostionListPath;
    [RszInlineWString(ByteSize = true)] public string? efcsvRotationListPath;
    [RszInlineWString(ByteSize = true)] public string? efcsvVelocityListPath;
    [RszInlineWString(ByteSize = true)] public string? efcsvColorListPath;

    public override string ToString() => !string.IsNullOrEmpty(efcsvPostionListPath) ? efcsvPostionListPath : type.ToString();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.DestinationCSV, EfxVersion.DD2)]
public partial class EFXAttributeDestinationCSV : EFXAttribute
{
	public EFXAttributeDestinationCSV() : base(EfxAttributeType.DestinationCSV) { }

	public uint unkn1;
	public UndeterminedFieldType unkn2;
	public UndeterminedFieldType unkn3;
	public uint unkn4;
	public UndeterminedFieldType unkn5;
	public UndeterminedFieldType unkn6;
	public UndeterminedFieldType unkn7;
	[RszInlineWString(ByteSize = true)] public string? efcsvPath;
	[RszInlineWString(ByteSize = true)] public string? unknPath2;
	[RszInlineWString(ByteSize = true)] public string? unknPath3;
	[RszInlineWString(ByteSize = true)] public string? unknPath4;

    public override string ToString() => !string.IsNullOrEmpty(efcsvPath) ? efcsvPath : type.ToString();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TerrainSnap, EfxVersion.DD2, EfxVersion.MHWilds)]
public partial class EFXAttributeTerrainSnap : EFXAttribute
{
	public EFXAttributeTerrainSnap() : base(EfxAttributeType.TerrainSnap) { }

	public TerrainSnapType SnapType;
	public bool InitSnap;
	public via.Range HorizontalBounceRate;
    public via.Range VerticalBounceRate;
    public via.Range Offset;
    public bool IgnoreScale;
    public bool FinishParticleAngleApplyEmitterAngle;
    public float FinishParticleAngleMaxRad;
	public float FinishParticleAngleMinRad;
    public via.Range FinishParticleAngleFrame;
    public float KillParticleHeight;
	[RszVersionExact(EfxVersion.MHWilds)]
	public byte uknByte;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Trigger, EfxVersion.DD2)]
public partial class EFXAttributeTrigger : EFXAttribute
{
	public EFXAttributeTrigger() : base(EfxAttributeType.Trigger) { }

	public uint unkn1;
	public UndeterminedFieldType unkn2;
	public float unkn3;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.DrawSubscene, EfxVersion.MHWilds)]
public partial class EFXAttributeDrawSubscene : EFXAttribute
{
	public EFXAttributeDrawSubscene() : base(EfxAttributeType.DrawSubscene) { }
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.IgnoreSettings, EfxVersion.MHWilds)]
public partial class EFXAttributeIgnoreSettings : EFXAttribute
{
	public EFXAttributeIgnoreSettings() : base(EfxAttributeType.IgnoreSettings) { }

	public uint Flags;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.RepeatArea, EfxVersion.MHWilds)]
public partial class EFXAttributeRepeatArea : EFXAttribute
{
	public EFXAttributeRepeatArea() : base(EfxAttributeType.RepeatArea) { }

	public uint Flags;
	public Vector3 Area;
	public float Unkn;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Listener, EfxVersion.MHWilds)]
public partial class EFXAttributeListener : EFXAttribute
{
	public EFXAttributeListener() : base(EfxAttributeType.Listener) { }

	public uint Unkn1;
	public uint Unkn2;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Layout, EfxVersion.MHWilds)]
public partial class EFXAttributeLayout : EFXAttribute
{
	public EFXAttributeLayout() : base(EfxAttributeType.Layout) { }

	public uint flags1;
	public uint flags2;
	public uint Unkn3;

	// note: not 100% on the flag bits but most Wilds files seem to work with this
	[RszConditional("((flags1 & (8 | 4 | 16)) != 0)", EndAt = nameof(layoutDataFloats))]
	[RszInlineWString(ByteSize = true)]
	public string? layoutName;
	[RszFixedSizeArray]
	public byte[]? layoutDataFloats;
}
