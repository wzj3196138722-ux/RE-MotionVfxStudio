using System.Numerics;
using ReeLib.Efx.Enums;
using ReeLib.Efx.Structs.Common;
using ReeLib.InternalAttributes;

namespace ReeLib.Efx.Structs.Transforms;

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Velocity2D, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeVelocity2D : EFXAttribute
{
	public EFXAttributeVelocity2D() : base(EfxAttributeType.Velocity2D) { }

	// 字段名取自 MonsterHunterWilds.exe 的文本解析器（vtable e0fa910 槽 20），偏移由块构建器（槽 16）对回文件。
	// Angle / Speed / SpeedCoef / GravityRate 在引擎里各是一对 float，采样方式没读，所以用 via.Range。
	// 引擎的 Offset、Size 也各是两个 float；这里拆成 X / Y，与 Velocity2DExpression 的槽名一致。
	[RszVersion(EfxVersion.RE4)]
	public uint Flags;

	public via.Range Angle;
	public via.Range Speed;
	public via.Range SpeedCoef;

	public float unkn1_6;
    [RszVersion(EfxVersion.RE4)]
	public uint re4_unkn0;
	public float OffsetX;
    [RszVersion(EfxVersion.RE4)]
	public float OffsetY;

	public float SizeX;
	public float SizeY;
	// 解析器按枚举名 Direction / Normal / Radial 读入。
	public uint VelocityType;
	public via.Range GravityRate;
    [RszVersion(EfxVersion.RE4, EndAt = nameof(re4_unkn2_1))]
	public uint re4_unkn2_0;
	public uint re4_unkn2_1;
}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Velocity2DDelayFrame, EfxVersion.MHWilds)]
public partial class EFXAttributeVelocity2DDelayFrame : EFXAttribute
{
	public EFXAttributeVelocity2DDelayFrame() : base(EfxAttributeType.Velocity2DDelayFrame) { }

	public uint DelayFrames;
	public uint Unkn1;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Velocity2DExpression, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeVelocity2DExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeVelocity2DExpression() : base(EfxAttributeType.Velocity2DExpression) { }

	// re4.exe 槽位（A 类：引擎没有槽位名，按 applier 写回的宿主字段命名，bit i = 槽位 i）。运算 = op(文件原值, 表达式)，每帧重算；Angle 槽按角度。
	[RszClassInstance] public BitSet expressionBits = new BitSet(13) { BitNames = [ nameof(Speed), nameof(SpeedRange), nameof(SpeedCoef), nameof(SpeedCoefRange), nameof(GravityRate), nameof(GravityRateRange), nameof(Angle), nameof(AngleRange), nameof(OffsetX), nameof(OffsetY), nameof(SizeX), nameof(SizeY), nameof(VelocityType) ] };
    public ExpressionAssignType Speed;
    public ExpressionAssignType SpeedRange;
    public ExpressionAssignType SpeedCoef;
    public ExpressionAssignType SpeedCoefRange;
    public ExpressionAssignType GravityRate;
    public ExpressionAssignType GravityRateRange;
    public ExpressionAssignType Angle;
    public ExpressionAssignType AngleRange;
    [RszVersion(EfxVersion.RE4, EndAt = nameof(VelocityType))]
    public ExpressionAssignType OffsetX;
    public ExpressionAssignType OffsetY;
    public ExpressionAssignType SizeX;
    public ExpressionAssignType SizeY;
    public ExpressionAssignType VelocityType;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Velocity3D, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4)]
public partial class EFXAttributeVelocity3D : EFXAttribute
{
	public EFXAttributeVelocity3D() : base(EfxAttributeType.Velocity3D) { }

	[RszVersion(EfxVersion.RE2)]
	public uint Flags;
	public CenterSpread DirectionVectorX;
    public CenterSpread DirectionVectorY;
    public CenterSpread DirectionVectorZ;
    public CenterSpread Speed;
    public CenterSpread SpeedCoef;
    [RszVersion(EfxVersion.RE4)]
    public CenterSpreadI SpeedDelayFrame;

    public Vector3 Offset;
    public Vector3 Size;
    public VelocityType VelocityType;
    public CenterSpread GravityRate;
    [RszVersion(EfxVersion.RE4)]
    public CenterSpreadI GravityDelayFrame;

    [RszVersion(EfxVersion.RE2, EndAt = nameof(Spread))]
    public CenterSpread InheritRate;
    public ClampRange InheritDistance;
    [RszVersion(EfxVersion.RE8)]
    public MinExtra Spread;
}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Velocity3DDelayFrame, EfxVersion.RE4)]
public partial class EFXAttributeVelocity3DDelayFrame : EFXAttribute
{
	public EFXAttributeVelocity3DDelayFrame() : base(EfxAttributeType.Velocity3DDelayFrame) { }

	public uint frameDelay;
	public uint unkn2;

}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Velocity3DExpression, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4)]
public partial class EFXAttributeVelocity3DExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
    public BitSet ExpressionBits => expressionBits;

	public EFXAttributeVelocity3DExpression() : base(EfxAttributeType.Velocity3DExpression) { }

    [RszVersion(EfxVersion.RE8, EndAt = nameof(range3))]
	public uint rert_unkn0;
	public uint rert_unkn1;
	public Vector2 range1;
	public Vector2 range2;
	public Vector2 range3;

	// re4.exe 槽位（A 类：引擎没有槽位名，按 applier 写回的宿主字段命名，bit i = 槽位 i）。运算 = op(文件原值, 表达式)，每帧重算；
	[RszClassInstance] public BitSet expressionBits = new BitSet(19) { BitNames = [ nameof(Speed), nameof(SpeedRange), nameof(SpeedCoef), nameof(SpeedCoefRange), nameof(GravityRate), nameof(GravityRateRange), nameof(DirX), nameof(DirXRange), nameof(DirY), nameof(DirYRange), nameof(DirZ), nameof(DirZRange), nameof(OffsetX), nameof(OffsetY), nameof(OffsetZ), nameof(SizeX), nameof(SizeY), nameof(SizeZ), nameof(VelocityType) ] };
	public ExpressionAssignType Speed;
	public ExpressionAssignType SpeedRange;
	public ExpressionAssignType SpeedCoef;
	public ExpressionAssignType SpeedCoefRange;
	public ExpressionAssignType GravityRate;
	public ExpressionAssignType GravityRateRange;
	public ExpressionAssignType DirX;
	public ExpressionAssignType DirXRange;
	public ExpressionAssignType DirY;
	public ExpressionAssignType DirYRange;
	public ExpressionAssignType DirZ;
	public ExpressionAssignType DirZRange;

    [RszVersion(">", EfxVersion.RERT, EndAt = nameof(OffsetY))]
	public ExpressionAssignType OffsetX;
	public ExpressionAssignType OffsetY;

    [RszVersion(EfxVersion.RE4, EndAt = nameof(VelocityType))]
	public ExpressionAssignType OffsetZ;
	public ExpressionAssignType SizeX;
	public ExpressionAssignType SizeY;
	public ExpressionAssignType SizeZ;
	public ExpressionAssignType VelocityType;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;

}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.AngularVelocity2D, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeAngularVelocity2D : EFXAttribute
{
	public EFXAttributeAngularVelocity2D() : base(EfxAttributeType.AngularVelocity2D) { }

	public uint unkn0;
	public float unkn1;
	public float unkn2;
	public float unkn3;
	public float unkn4;
	public float AddRadiusCoef;   // MonsterHunterWilds.exe 解析器 +20；与 AngularVelocity3D 的 AddRadiusCoef 同序
	public float unkn6;
	public float unkn7;
	public float unkn8;
	public float unkn9;
	public float unkn10;
	public float unkn11;
	public float radians12;
	public float radians13;
	public float radians14;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.AngularVelocity2DDelayFrame, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeAngularVelocity2DDelayFrame : EFXAttribute
{
	public EFXAttributeAngularVelocity2DDelayFrame() : base(EfxAttributeType.AngularVelocity2DDelayFrame) { }

	public uint DelayFrames;
	public uint Unkn1;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.AngularVelocity3D, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeAngularVelocity3D : EFXAttribute
{
	public EFXAttributeAngularVelocity3D() : base(EfxAttributeType.AngularVelocity3D) { }

	public uint Flags;
	public RotationOrder RotationOrder;
	public via.Range Radius;
	public via.Range AddRadius;
	public float AddRadiusCoef;
	public via.Range RotationAxisX;
    public via.Range RotationAxisY;
    public via.Range RotationAxisZ;
    public via.Range AddRotationAxisX;
    public via.Range AddRotationAxisY;
    public via.Range AddRotationAxisZ;
    public float AddRotationCoef;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.AngularVelocity3DDelayFrame, EfxVersion.RERT, EfxVersion.RE4)]
public partial class EFXAttributeAngularVelocity3DDelayFrame : EFXAttribute
{
	public EFXAttributeAngularVelocity3DDelayFrame() : base(EfxAttributeType.AngularVelocity3DDelayFrame) { }

	public uint frameDelay;
	public uint unkn1;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtVelocity2D, EfxVersion.RERT)]
public partial class EFXAttributePtVelocity2D : EFXAttribute
{
	public EFXAttributePtVelocity2D() : base(EfxAttributeType.PtVelocity2D) { }

	// 字段名取自 MonsterHunterWilds.exe 文本解析器（vtable e100ca0）。
	public Vector2 Direction;
	public float Speed;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtVelocity2DClip, EfxVersion.RERT)]
public partial class EFXAttributePtVelocity2DClip : EFXAttribute
{
	public EFXAttributePtVelocity2DClip() : base(EfxAttributeType.PtVelocity2DClip) { }

    public uint ukn0;
    public float ukn1;
    public float ukn2;
    public uint ukn3;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtVelocity3D, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4)]
public partial class EFXAttributePtVelocity3D : EFXAttribute
{
	public EFXAttributePtVelocity3D() : base(EfxAttributeType.PtVelocity3D) { }

	// 字段名取自 MonsterHunterWilds.exe 文本解析器（vtable e100a70）。
	public Vector3 Direction;
	public float Speed;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtVelocity3DClip, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributePtVelocity3DClip : EFXAttribute, IClipAttribute
{
    public EfxClipData Clip => clipData;
    public BitSet ClipBits => clipBits;

    public EFXAttributePtVelocity3DClip() : base(EfxAttributeType.PtVelocity3DClip) { }

	[RszClassInstance] public BitSet clipBits = new BitSet(4);
    uint unkn1;
	[RszClassInstance] public EfxClipData clipData = new();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtAngularVelocity3D, EfxVersion.RE8)]
public partial class EFXAttributePtAngularVelocity3D : EFXAttribute
{
	public EFXAttributePtAngularVelocity3D() : base(EfxAttributeType.PtAngularVelocity3D) { }

	// 0x04 起的字段名取自 MonsterHunterWilds.exe 文本解析器（vtable e104c40）；解析器不写第一个 dword。
	public UndeterminedFieldType unkn1;
	public float AddRadius;
	public float AddRadiusCoef;
	public float AddRotationAxisX;
	public float AddRotationAxisY;
	public float AddRotationAxisZ;
	public float AddRotationRate;
	public float AddRotationCoef;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtAngularVelocity3DExpression, EfxVersion.DD2)]
public partial class EFXAttributePtAngularVelocity3DExpression : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributePtAngularVelocity3DExpression() : base(EfxAttributeType.PtAngularVelocity3DExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(7);
	public ExpressionAssignType unkn1;
	public ExpressionAssignType unkn2;
	public ExpressionAssignType unkn3;
	public ExpressionAssignType unkn4;
	public ExpressionAssignType unkn5;
	public ExpressionAssignType unkn6;
	public ExpressionAssignType unkn7;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtAngularVelocity2D, EfxVersion.DD2)]
public partial class EFXAttributePtAngularVelocity2D : EFXAttribute
{
	public EFXAttributePtAngularVelocity2D() : base(EfxAttributeType.PtAngularVelocity2D) { }

	// 字段名取自 MonsterHunterWilds.exe 文本解析器（vtable e104f60）。
	public float AddRadius;
	public float AddRadiusCoef;
	public float RotationScaleRateX;
	public float RotationScaleRateY;
	public float RotationSpeed;
	public float RotationSpeedCoef;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtAngularVelocity2DExpression, EfxVersion.DD2)]
public partial class EFXAttributePtAngularVelocity2DExpression : EFXAttribute
{
	public EFXAttributePtAngularVelocity2DExpression() : base(EfxAttributeType.PtAngularVelocity2DExpression) { }

	public uint unkn1;
	public uint unkn2;
	public uint unkn3;
	public uint unkn4;
	public uint unkn5;
	public uint unkn6;
	public uint unkn7;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}
