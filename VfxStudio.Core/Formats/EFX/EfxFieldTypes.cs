using System.Numerics;
using ReeLib.Efx.Structs.Common;
using ReeLib.InternalAttributes;

namespace ReeLib.Efx.Structs.Field;

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.VectorFieldParameter, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeVectorFieldParameter : EFXAttribute
{
	public EFXAttributeVectorFieldParameter() : base(EfxAttributeType.VectorFieldParameter) { }

	public Vector3 Parameter;
	public float Lacunarity;
	public float Gain;
	public float Blend;
	public float lfoRange;
	public float lfoTime;
	public Vector3 Rotation;
    public Vector3 Scale;
    public via.Range Coefficient;
	[RszVersion(EfxVersion.RE8, EndAt = nameof(Flags))]
	public float EdgeOffset;
	public float Falloff;
	public uint Flags;
	public int FieldIndex;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.VectorFieldParameterClip, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeVectorFieldParameterClip : EFXAttribute, IClipAttribute
{
    public EfxClipData Clip => clipData;
    public BitSet ClipBits => clipBits;

    public EFXAttributeVectorFieldParameterClip() : base(EfxAttributeType.VectorFieldParameterClip) { }

	[RszClassInstance] public BitSet clipBits = new BitSet(13);
    uint unkn1;
	[RszClassInstance] public EfxClipData clipData = new();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.VectorFieldParameterExpression, EfxVersion.DMC5, EfxVersion.RE8, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeVectorFieldParameterExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeVectorFieldParameterExpression() : base(EfxAttributeType.VectorFieldParameterExpression) { }

	// re4.exe 槽位（A 类：引擎没有槽位名，按 applier 写回的宿主字段命名，bit i = 槽位 i）。运算 = op(文件原值, 表达式)，每帧重算；Rotation 按角度；Range/Detail 槽是解析器别名推断，写的位置与 Frequency/Amplitude 相同。
	[RszClassInstance] public BitSet expressionBits = new BitSet(20) { BitNames = [ nameof(Frequency), nameof(Amplitude), nameof(Lacunarity), nameof(Gain), nameof(VelocityBlend), nameof(OscillationRange), nameof(OscillationTime), nameof(Coefficient), nameof(CoefficientRange), nameof(Range), nameof(Detail), nameof(RotationX), nameof(RotationY), nameof(RotationZ), nameof(ScaleX), nameof(ScaleY), nameof(ScaleZ), nameof(EdgeOffset), nameof(Falloff), nameof(SmoothBlend) ] };
	public ExpressionAssignType Frequency;
	public ExpressionAssignType Amplitude;
	public ExpressionAssignType Lacunarity;
	public ExpressionAssignType Gain;
	public ExpressionAssignType VelocityBlend;
	public ExpressionAssignType OscillationRange;
	public ExpressionAssignType OscillationTime;
	public ExpressionAssignType Coefficient;
	public ExpressionAssignType CoefficientRange;
	public ExpressionAssignType Range;
	public ExpressionAssignType Detail;
	[RszVersion('>', EfxVersion.DMC5, EndAt = nameof(SmoothBlend))]
	public ExpressionAssignType RotationX;
	public ExpressionAssignType RotationY;
	public ExpressionAssignType RotationZ;
	public ExpressionAssignType ScaleX;
	public ExpressionAssignType ScaleY;
	public ExpressionAssignType ScaleZ;
	public ExpressionAssignType EdgeOffset;
	public ExpressionAssignType Falloff;
	public ExpressionAssignType SmoothBlend;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.GlobalVectorField, EfxVersion.RE8)]
public partial class EFXAttributeGlobalVectorField : EFXAttribute
{
	public EFXAttributeGlobalVectorField() : base(EfxAttributeType.GlobalVectorField) { }

	public uint TargetCount;
	public float LocalToGlobalBlend;
	public float VelocityBlend;
	public Vector4 Weight;
    public Vector4 InfluenceFrame;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.DirectionalFieldParameter, EfxVersion.RE8)]
public partial class EFXAttributeDirectionalFieldParameter : EFXAttribute
{
	public EFXAttributeDirectionalFieldParameter() : base(EfxAttributeType.DirectionalFieldParameter) { }

	public uint Flags;
    public via.Range Speed;
    public via.Range SpeedCoef;
    public Vector2 SourcePointUV;
    public Vector2 SourceRadius;
    public Vector2 SinkPointUV;
    public Vector2 SinkRadius;
    public int FieldIndex;
}
