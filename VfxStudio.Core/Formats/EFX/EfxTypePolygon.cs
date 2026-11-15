using ReeLib.Efx.Enums;
using ReeLib.Efx.Structs.Common;
using ReeLib.InternalAttributes;

namespace ReeLib.Efx.Structs.Main;

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypePolygon, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeTypePolygon : EFXAttribute
{
	public EFXAttributeTypePolygon() : base(EfxAttributeType.TypePolygon) { }

	public uint Flags;
	public via.Color Color;
	public via.Color ColorRange;
    public float ColorRate;
    public float Intensity;

	public float EdgeBlendRange;

    [RszVersion(EfxVersion.RE4, EndAt = nameof(Flags2))]
    public float AlphaRate;
	// 世界朝向的四边形（re4.exe 3d6f5e0 用欧拉角 + RotationOrder 构造四元数），不朝相机。
	// Flags2 0x40 = 朝向跟随运动方向（以 OrientDirectionUpVector 选上方向），0x80 = 锁定（中等置信）。
	public uint Flags2;
    // [RszVersion("!=", EfxVersion.RE8, EndAt = nameof(RotationZ))]
	public RotationOrder RotationOrder;
    public CenterSpread RotationX;
    public CenterSpread RotationY;
    public CenterSpread RotationZ;
    public CenterSpread SizeScalar;
    public CenterSpread Width;
    public CenterSpread Height;
    public via.Range Offset;
    [RszVersion('<', EfxVersion.RE4)]
	public float AlphaRateLegacy;
    [RszVersion(EfxVersion.RERT)]
	public float ShadowMultiplier;
    [RszVersion(EfxVersion.RE4)]
	public AxisXYZ OrientDirectionUpVector;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypePolygonClip, EfxVersion.DMC5)]
public partial class EFXAttributeTypePolygonClip : EFXAttribute, IClipAttribute
{
    public EfxClipData Clip => clipData;
    public BitSet ClipBits => clipBits;

    public EFXAttributeTypePolygonClip() : base(EfxAttributeType.TypePolygonClip) { }

	[RszClassInstance] public BitSet clipBits = new BitSet(9);
    public uint unkn1;
	[RszClassInstance] public EfxClipData clipData = new();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypePolygonExpression, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeTypePolygonExpression : ReeLib.Efx.EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

    public EFXAttributeTypePolygonExpression() : base(EfxAttributeType.TypePolygonExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(19) { BitNameDict = new () {
		[1] = nameof(color),
		[2] = nameof(alpha),
		[3] = nameof(emissive),
		[4] = nameof(colorStrength),
		[12] = nameof(scale),
		[14] = nameof(size),
	} };
    public ExpressionAssignType color;
    public ExpressionAssignType alpha;
    public ExpressionAssignType emissive;
    public ExpressionAssignType colorStrength;
    public ExpressionAssignType unkn5; // rert DistortionRate
    public ExpressionAssignType unkn6;
    public ExpressionAssignType unkn7;
    public ExpressionAssignType unkn8;
    public ExpressionAssignType unkn9;
    public ExpressionAssignType unkn10;
    public ExpressionAssignType unkn11;
    public ExpressionAssignType scale;
    public ExpressionAssignType unkn13; // dd2: emit size / range
    public ExpressionAssignType size; // dmc5: size / width
    public ExpressionAssignType unkn15;
    public ExpressionAssignType unkn16; // dmc5: (spect + 2) / length
    public ExpressionAssignType unkn17;
    public ExpressionAssignType unkn18;
	[RszVersion(EfxVersion.RE4)]
	public ExpressionAssignType unkn19;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypePolygonMaterial, EfxVersion.RE8, EfxVersion.DD2)]
public partial class EFXAttributeTypePolygonMaterial : EFXAttribute
{
	public EFXAttributeTypePolygonMaterial() : base(EfxAttributeType.TypePolygonMaterial) { }

	public uint Flags;
	public via.Color Color;
	public via.Color ColorRange;
	public RotationOrder RotationOrder;
    public via.Range RotationX;
    public via.Range RotationY;
    public via.Range RotationZ;
    public via.Range SizeScalar;
    public via.Range Width;
    public via.Range Height;
    public via.Range Offset;

    [RszConstructorParams(nameof(Version)), RszSwitch(
		nameof(Version), ">=", EfxVersion.DD2, typeof(EfxMaterialStructV2),
		typeof(EfxMaterialStructV1)
	)]
	public EfxMaterialStructBase? material;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypePolygonTrail, EfxVersion.RE2, EfxVersion.RERT)]
public partial class EFXAttributeTypePolygonTrail : EFXAttribute
{
	public EFXAttributeTypePolygonTrail() : base(EfxAttributeType.TypePolygonTrail) { }

	public uint Flags;
	public via.Color Color;
	public via.Color ColorRange;
	public float ColorRate;

	public float Intensity;
	// 引擎名 EdgeBlendRange（旧名 EdgeBlendRate）。
	public float EdgeBlendRange;
	public float AlphaRate;
	// re4.exe（加载器 3d73330 读 0x5c 字节，每帧 3d75aa0）：沿 Axis 伸出、长 Length 的线段随运动扫出的面（挥击残影）。
	// 位 3 = 定长：路径按等弧长重采样，最长弧 ≈ Length × 缩放 × StretchDistance，不读 IntervalFrame；
	// 位 3 关 = 每 IntervalFrame 帧压一个样本。0x60 = 用 HeadColor/Place1/Place2 给顶点上色。名字不是引擎名（旧名 re4_unkn）。
    [RszVersion(EfxVersion.RE4)]
	public uint TrailFlags;
	// 传给头部着色器参数构造，解析器名 ShadowMultiplier（旧名 sb_unkn0）。
    [RszVersion(EfxVersion.RERT)]
	public float ShadowMultiplier;

	public AxisType Axis;
	public CenterSpread Length;
	public float StretchDistance;
	public uint NumTrailDivision;
	public uint NumVerticalDivision;
	public uint NumSplineDivision;
	public uint IntervalFrame;
	public via.Color HeadColor;
	public via.Color Place1;
    public via.Color Place2;
	public float Place1Ratio;
	public float Place2Ratio;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypePolygonTrailMaterial, EfxVersion.DD2)]
public partial class EFXAttributeTypePolygonTrailMaterial : EFXAttribute
{
	public EFXAttributeTypePolygonTrailMaterial() : base(EfxAttributeType.TypePolygonTrailMaterial) { }

    public uint Flags;
    public via.Color Color;
    public via.Color ColorRange;
    public AxisType Axis;
    public via.Range Length;
    public float StretchDistance;
    public uint NumTrailDivision;
    public uint NumVerticalDivision;
    public uint NumSplineDivision;
    public uint IntervalFrame;
    public via.Color HeadColor;
    public via.Color Place1;
    public via.Color Place2;
    public float Place1Ratio;
    public float Place2Ratio;

    [RszConstructorParams(nameof(Version)), RszSwitch(
		nameof(Version), ">=", EfxVersion.DD2, typeof(EfxMaterialStructV2),
		typeof(EfxMaterialStructV1)
	)]
	public EfxMaterialStructBase? material;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypePolygonTrailMaterialExpression, EfxVersion.DD2)]
public partial class EFXAttributeTypePolygonTrailMaterialExpression : EFXAttribute, IExpressionAttribute, IMaterialExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
    public EFXMaterialExpressionList? MaterialExpressions { get => materialExpressions; set => materialExpressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeTypePolygonTrailMaterialExpression() : base(EfxAttributeType.TypePolygonTrailMaterialExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(7) { BitNameDict = new () {
		[1] = nameof(color),
	} };
    public ExpressionAssignType color;
    public ExpressionAssignType unkn2;
    public ExpressionAssignType unkn3;
    public ExpressionAssignType unkn4;
    public ExpressionAssignType unkn5;
    public ExpressionAssignType unkn6;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;

	public ExpressionAssignType unkn7;
	public int materialExpressionCount;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXMaterialExpressionList? materialExpressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeGpuPolygon, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT)]
public partial class EFXAttributeTypeGpuPolygon : EFXAttribute
{
	public EFXAttributeTypeGpuPolygon() : base(EfxAttributeType.TypeGpuPolygon) { }


    //TODO Fix this for older games
    public uint BlendFlags;
	[RszVersionExact(EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT)]
	public uint unkn1;
	[RszVersionExact(EfxVersion.RE8, EfxVersion.RERT)]
	public uint unkn2;
    public via.Color Color;
    public via.Color ColorRange;
    public float ColorRate;
    public float Intensity;
	// 引擎名 EdgeBlendRange（旧名 EdgeBlendRate）。
    public float EdgeBlendRange;
    public float AlphaRate;
	// re4.exe（加载器 3db8d20 读 0x5c 字节）：头部之后是三个标量再接 6 个范围，与 GpuBillboard 相同的顺序：
	// ParticleNum（传给 GPU 初始化 3e1bbc0）、Flags（2 位写入发射器标志）、re4Ukn（也传给 GPU 初始化，含义未知）。
	// 旧代码把前两个名字写反，并把 re4Ukn 当成 RotationX.s，之后所有范围错开半个、最后一个 float 被当成 OrientDirectionUpVector。
	// RE4 起没有 OrientDirectionUpVector。
    [RszVersion(EfxVersion.RE4, EndAt = nameof(re4Ukn))]
    public uint ParticleNum;
    public uint Flags;
    public uint re4Ukn;

    public via.Range RotationX;
    public via.Range RotationY;
    public via.Range RotationZ;
    public via.Range SizeScalar;
    public via.Range Width;
    public via.Range Height;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeGpuPolygonExpression, EfxVersion.DD2)]
public partial class EFXAttributeTypeGpuPolygonExpression : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeTypeGpuPolygonExpression() : base(EfxAttributeType.TypeGpuPolygonExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(19);
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
    public ExpressionAssignType unkn19;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}
