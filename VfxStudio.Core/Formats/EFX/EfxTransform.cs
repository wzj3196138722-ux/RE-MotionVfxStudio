using System.Numerics;
using ReeLib.Efx.Structs.Common;
using ReeLib.Efx.Enums;
using ReeLib.InternalAttributes;
using ReeLib.via;

namespace ReeLib.Efx.Structs.Transforms;

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Transform2D, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeTransform2D : EFXAttribute
{
	public Vector2 LocalPosition;
	public float LocalRotation;
	public Vector2 LocalScale;

	public EFXAttributeTransform2D() : base(EfxAttributeType.Transform2D) { }

    public override string ToString() => $"Translate {LocalPosition} rotate {LocalRotation} scale {LocalScale}";
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Transform2DClip, EfxVersion.RERT)]
public partial class EFXAttributeTransform2DClip : EFXAttribute
{
	public EFXAttributeTransform2DClip() : base(EfxAttributeType.Transform2DClip) { }

    public uint ukn0;
    public uint ukn1;
    public uint ukn2;
    public uint ukn3;
    public uint ukn4;
    public uint ukn5_0;
    public uint ukn5_1;
    public uint ukn5_2;
    public uint ukn5_3;
    public uint ukn5_4;
    public uint ukn5_5;
    public uint ukn5_6;
    public uint ukn5_7;
    public uint ukn5_8;
    public uint ukn5_9;
    public uint ukn5_10;
    public uint ukn5_11;
    public uint ukn5_12;
    public uint ukn5_13;
    public uint ukn5_14;
    public uint ukn5_15;
    public uint ukn5_16;
    public uint ukn5_17;
    public uint ukn5_18;
    public uint ukn5_19;
    public uint ukn5_20;
    public uint ukn5_21;
    public uint ukn5_22;
    public uint ukn5_23;
    public uint ukn5_24;
    public uint ukn5_25;
    public uint ukn5_26;
    public uint ukn5_27;
    public uint ukn5_28;
    public uint ukn5_29;
    public uint ukn5_30;
    public uint ukn5_31;
    public uint ukn5_32;
    public uint ukn5_33;
    public byte ukn6;
    public uint ukn7_0;
    public uint ukn7_1;
    public uint ukn7_2;
    public uint ukn7_3;
    public uint ukn7_4;
    public uint ukn7_5;
    public uint ukn7_6;
    public byte ukn8_0;
    public byte ukn8_1;
    public uint ukn9;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Transform2DExpression, EfxVersion.RE2, EfxVersion.RERT, EfxVersion.RE4)]
public partial class EFXAttributeTransform2DExpression : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeTransform2DExpression() : base(EfxAttributeType.Transform2DExpression) { }

	// re4.exe 槽位（A 类：引擎没有槽位名，按 applier 写回的宿主字段命名，bit i = 槽位 i）。运算 = op(文件原值, 表达式)，每帧重算；旋转槽按角度。
	[RszClassInstance] public BitSet expressionBits = new BitSet(5) { BitNames = [ nameof(LocalPositionX), nameof(LocalPositionY), nameof(LocalRotation), nameof(LocalScaleX), nameof(LocalScaleY) ] };
	public ExpressionAssignType LocalPositionX;
	public ExpressionAssignType LocalPositionY;
	public ExpressionAssignType LocalRotation;
	public ExpressionAssignType LocalScaleX;
	public ExpressionAssignType LocalScaleY;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Transform2DModifierDelayFrame, EfxVersion.RE4)]
public partial class EFXAttributeTransform2DModifierDelayFrame : EFXAttribute
{
	public EFXAttributeTransform2DModifierDelayFrame() : base(EfxAttributeType.Transform2DModifierDelayFrame) { }

	public uint frameDelay;
	public uint unkn1;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Transform3D, EfxVersion.DMC5, EfxVersion.RE4, EfxVersion.Pragmata)]
public partial class EFXAttributeTransform3D : EFXAttribute
{
	public EFXAttributeTransform3D() : base(EfxAttributeType.Transform3D) { }

	public Vector3 LocalPosition;
	public Vector3 LocalRotation;
	public Vector3 LocalScale;
	public RotationOrder RotationOrder;
	[RszVersion(EfxVersion.Pragmata)] public uint prag_unkn;

    public override string ToString() => $"Translate {LocalPosition} rotate {LocalRotation} scale {LocalScale}";
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Transform3DExpression, EfxVersion.DMC5, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeTransform3DExpression : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeTransform3DExpression() : base(EfxAttributeType.Transform3DExpression) { }

	/// <summary>
	/// Up to 9 bits, in order: pos X/Y/Z, rot X/Y/Z, scale X/Y/Z
	/// </summary>
	// re4.exe 槽位（A 类：引擎没有槽位名，按 applier 写回的宿主字段命名，bit i = 槽位 i）。运算 = op(文件原值, 表达式)，每帧重算；旋转槽按角度。
	[RszClassInstance] public BitSet expressionBits = new BitSet(9) { BitNames = [ nameof(LocalPositionX), nameof(LocalPositionY), nameof(LocalPositionZ), nameof(LocalRotationX), nameof(LocalRotationY), nameof(LocalRotationZ), nameof(LocalScaleX), nameof(LocalScaleY), nameof(LocalScaleZ) ] };

	public ExpressionAssignType LocalPositionX;
	public ExpressionAssignType LocalPositionY;
	public ExpressionAssignType LocalPositionZ;
	public ExpressionAssignType LocalRotationX;
	public ExpressionAssignType LocalRotationY;
	public ExpressionAssignType LocalRotationZ;
	public ExpressionAssignType LocalScaleX;
	public ExpressionAssignType LocalScaleY;
	public ExpressionAssignType LocalScaleZ;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Transform3DClip, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeTransform3DClip : EFXAttribute, IClipAttribute
{
    public EfxClipData Clip => clipData;
    public BitSet ClipBits => clipBits;

    public EFXAttributeTransform3DClip() : base(EfxAttributeType.Transform3DClip) { }

	[RszClassInstance] public BitSet clipBits = new BitSet(9);
    uint unkn1;
	[RszClassInstance] public EfxClipData clipData = new();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Transform3DModifierDelayFrame, EfxVersion.RE4)]
public partial class EFXAttributeTransform3DModifierDelayFrame : EFXAttribute
{
	public EFXAttributeTransform3DModifierDelayFrame() : base(EfxAttributeType.Transform3DModifierDelayFrame) { }

	public uint frameDelay;
	public uint unkn1;

}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Transform3DModifier, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeTransform3DModifier : ReeLib.Efx.EFXAttribute
{
    public EFXAttributeTransform3DModifier() : base(EfxAttributeType.Transform3DModifier) { }

    // re4.exe：vtable 7a14100，加载器 3d9a850 读 UniqueID + 0xdc 字节，每帧 3d9b300。解析器是空壳，没有引擎名，
    // 名字按作用起（旧名 unkn0..unkn54）。发射器级：每帧改写同一 entry 的 Transform3D。字节布局只在 RE4 上核对过。
    // 所有区间对都是 center + (2u−1)·spread；旋转是弧度。
    // Flags：bit0-2 = 平移/旋转/缩放启用速度，bit3-5 = 速度随机正负号（需 bit0-2），
    // bit6-8 = 用 *Start 替换 Transform3D 的平移/旋转/缩放（三个轴一起替换）。
    public uint Flags;

    // 起始值：对应组的 bit6-8 置位时替换 Transform3D 的值。
    public CenterSpread TranslationStartX;
    public CenterSpread TranslationStartY;
    public CenterSpread TranslationStartZ;
    public CenterSpread RotationStartX;
    public CenterSpread RotationStartY;
    public CenterSpread RotationStartZ;
    public CenterSpread ScaleStartX;
    public CenterSpread ScaleStartY;
    public CenterSpread ScaleStartZ;

    // 每轴 {速度, 系数}：每 1/60 秒一帧 值 += n·v，然后 v *= Coef^n（n = dt·60）。速度单位是每帧，不是每秒；缩放不夹到 0。
    public CenterSpread TranslationVelocityX;
    public CenterSpread TranslationVelocityXCoef;
    public CenterSpread TranslationVelocityY;
    public CenterSpread TranslationVelocityYCoef;
    public CenterSpread TranslationVelocityZ;
    public CenterSpread TranslationVelocityZCoef;
    public CenterSpread RotationVelocityX;
    public CenterSpread RotationVelocityXCoef;
    public CenterSpread RotationVelocityY;
    public CenterSpread RotationVelocityYCoef;
    public CenterSpread RotationVelocityZ;
    public CenterSpread RotationVelocityZCoef;
    public CenterSpread ScaleVelocityX;
    public CenterSpread ScaleVelocityXCoef;
    public CenterSpread ScaleVelocityY;
    public CenterSpread ScaleVelocityYCoef;
    public CenterSpread ScaleVelocityZ;
    public CenterSpread ScaleVelocityZCoef;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtTransform2D, EfxVersion.DMC5, EfxVersion.RE8)]
public partial class EFXAttributePtTransform2D : ReeLib.Efx.EFXAttribute
{
    public EFXAttributePtTransform2D() : base(EfxAttributeType.PtTransform2D) { }

    // 字段名取自 MonsterHunterWilds.exe 文本解析器（vtable e100830），布局与 Transform2D 相同。
    public Vector2 LocalPosition;
    public float LocalRotation;
    public Vector2 LocalScale;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtTransform2DClip, EfxVersion.RE8)]
public partial class EFXAttributePtTransform2DClip : EFXAttribute, IClipAttribute
{
    public EfxClipData Clip => clipData;
    public BitSet ClipBits => clipBits;

	public EFXAttributePtTransform2DClip() : base(EfxAttributeType.PtTransform2DClip) { }

	[RszClassInstance] public BitSet clipBits = new BitSet(5);
    public UndeterminedFieldType unkn1;
	[RszClassInstance] public EfxClipData clipData = new();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtTransform3D, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributePtTransform3D : EFXAttribute
{
	public EFXAttributePtTransform3D() : base(EfxAttributeType.PtTransform3D) { }

	public Vector3 position;
	public Vector3 rotation;
	public Vector3 scale;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtTransform3DClip, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4)]
public partial class EFXAttributePtTransform3DClip : EFXAttribute, IClipAttribute
{
    public EfxClipData Clip => clipData;
    public BitSet ClipBits => clipBits;

    public EFXAttributePtTransform3DClip() : base(EfxAttributeType.PtTransform3DClip) { }

	[RszClassInstance] public BitSet clipBits = new BitSet(9);
    public uint unkn1;
	[RszClassInstance] public EfxClipData clipData = new();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtTransform3DExpression, EfxVersion.DD2)]
public partial class EFXAttributePtTransform3DExpression : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributePtTransform3DExpression() : base(EfxAttributeType.PtTransform3DExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(9) { BitNameDict = new () {
		[1] = nameof(posX),
		[2] = nameof(posY),
		[3] = nameof(posZ),
		[4] = nameof(rotationX),
		[5] = nameof(rotationY),
		[6] = nameof(rotationZ),
		[7] = nameof(scaleX),
		[8] = nameof(scaleY),
		[9] = nameof(scaleZ),
	} };
	public ExpressionAssignType posX;
	public ExpressionAssignType posY;
	public ExpressionAssignType posZ;
	public ExpressionAssignType rotationX;
	public ExpressionAssignType rotationY;
	public ExpressionAssignType rotationZ;
	public ExpressionAssignType scaleX;
	public ExpressionAssignType scaleY;
	public ExpressionAssignType scaleZ;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.RotateAnim, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeRotateAnim : EFXAttribute
{
	public EFXAttributeRotateAnim() : base(EfxAttributeType.RotateAnim) { }

	// re4.exe（vtable 7a0b3a0，加载器 3d1c8e0，逐帧 3d1cb60）。字段名与文本解析器一致。
	// 出生时每轴抽一次：v = Add.s ± Add.r（弧度/帧，1/60 秒一帧），c = Coef.s ± Coef.r。
	// 每帧（n = dt·60）：角度 += n·v，再 v *= c^n；**c 为 0 或 1 时都不衰减**（3d1cc71 显式跳过 0）。
	// Billboard3D 的滚转角 = X + Y（Z 不起作用）；Mesh 用三轴欧拉角。
	// Flags 只用位 0：每轴各自 50% 概率把 Add 取反（随机方向）；其余位忽略。
	public uint Flags;
    public CenterSpread RotationAddX;
    public CenterSpread RotationAddY;
    public CenterSpread RotationAddZ;
    public CenterSpread RotationCoefX;
    public CenterSpread RotationCoefY;
    public CenterSpread RotationCoefZ;
    [RszVersion(EfxVersion.DD2, EndAt = nameof(RotationDelayFrame))]
	public via.RangeI RotationDelayFrame;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.RotateAnimDelayFrame, EfxVersion.RE4)]
public partial class EFXAttributeRotateAnimDelayFrame : EFXAttribute
{
	public EFXAttributeRotateAnimDelayFrame() : base(EfxAttributeType.RotateAnimDelayFrame) { }

	public uint frameDelay;
	public uint unkn2;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.RotateAnimExpression, EfxVersion.RE7, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeRotateAnimExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeRotateAnimExpression() : base(EfxAttributeType.RotateAnimExpression) { }

	// re4.exe 槽位（A 类：引擎没有槽位名，按 applier 写回的宿主字段命名，bit i = 槽位 i）。运算 = op(文件原值, 表达式)，每帧重算；RotationAdd 槽按角度（每帧）。
	[RszClassInstance] public BitSet expressionBits = new BitSet(12) { BitNames = [ nameof(RotationAddX), nameof(RotationAddXRange), nameof(RotationAddY), nameof(RotationAddYRange), nameof(RotationAddZ), nameof(RotationAddZRange), nameof(RotationCoefX), nameof(RotationCoefXRange), nameof(RotationCoefY), nameof(RotationCoefYRange), nameof(RotationCoefZ), nameof(RotationCoefZRange) ] };
	public ExpressionAssignType RotationAddX;
	public ExpressionAssignType RotationAddXRange;
	public ExpressionAssignType RotationAddY;
	public ExpressionAssignType RotationAddYRange;
	public ExpressionAssignType RotationAddZ;
	public ExpressionAssignType RotationAddZRange;
	public ExpressionAssignType RotationCoefX;
	public ExpressionAssignType RotationCoefXRange;
	public ExpressionAssignType RotationCoefY;
	public ExpressionAssignType RotationCoefYRange;
	public ExpressionAssignType RotationCoefZ;
	public ExpressionAssignType RotationCoefZRange;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ScaleAnim, EfxVersion.DMC5, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeScaleAnim : EFXAttribute
{
	public EFXAttributeScaleAnim() : base(EfxAttributeType.ScaleAnim) { }

	// re4.exe（vtable 7a0b508，加载器 3d1d520，逐帧 3d1d830）。字段名与文本解析器一致。
	// 出生时每个范围抽一次 s ± r；以 Type 初始化后的尺寸为基准（统一尺寸 +0x80、X/Y/Z +0x84/+0x88/+0x8c）。
	// 每帧按粒子年龄 t（帧，dt·60 累加）重算：Coef == 1 时 尺寸 = 基准 + Add·t；
	// 否则 尺寸 = 基准 + Add·Coef·(1 − Coef^t)/(1 − Coef)，Coef < 1 时总增量收敛到 Add·Coef/(1 − Coef)。结果夹在 ≥ 0。
	// **Coef = 0 时该通道冻结、Add 不起作用**（与 RotateAnim 不同）。统一尺寸与各轴在渲染时相乘。
	// TypeRibbonLength 的 Y 槽就是条带长度，SizeYAdd 会拉长条带。
    public CenterSpread SizeScalarAdd;
    public CenterSpread SizeScalarCoef;
    public CenterSpread SizeXAdd;
    public CenterSpread SizeXAddCoef;
    public CenterSpread SizeYAdd;
    public CenterSpread SizeYAddCoef;
    public CenterSpread SizeZAdd;
    public CenterSpread SizeZAddCoef;
    [RszVersion(EfxVersion.DD2, EndAt = nameof(SizeDelayFrame))]
    public via.RangeI SizeDelayFrame;
}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ScaleAnimDelayFrame, EfxVersion.RE4)]
public partial class EFXAttributeScaleAnimDelayFrame : EFXAttribute
{
	public EFXAttributeScaleAnimDelayFrame() : base(EfxAttributeType.ScaleAnimDelayFrame) { }

	public uint frameDelay;
	public uint unkn2;

}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ScaleAnimExpression, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeScaleAnimExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeScaleAnimExpression() : base(EfxAttributeType.ScaleAnimExpression) { }

	// re4.exe 槽位（A 类：引擎没有槽位名，按 applier 写回的宿主字段命名，bit i = 槽位 i）。运算 = op(文件原值, 表达式)，每帧重算；
	[RszClassInstance] public BitSet expressionBits = new BitSet(8) { BitNames = [ nameof(SizeScalarAdd), nameof(SizeScalarAddRange), nameof(SizeXAdd), nameof(SizeXAddRange), nameof(SizeYAdd), nameof(SizeYAddRange), nameof(SizeZAdd), nameof(SizeZAddRange) ] };
	public ExpressionAssignType SizeScalarAdd;
	public ExpressionAssignType SizeScalarAddRange;
    [RszVersion(EfxVersion.RERT, EndAt = nameof(SizeZAddRange))]
	public ExpressionAssignType SizeXAdd;
	public ExpressionAssignType SizeXAddRange;
	public ExpressionAssignType SizeYAdd;
	public ExpressionAssignType SizeYAddRange;
	public ExpressionAssignType SizeZAdd;
	public ExpressionAssignType SizeZAddRange;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ScaleByDepth, EfxVersion.RE4)]
public partial class EFXAttributeScaleByDepth : EFXAttribute
{
	public EFXAttributeScaleByDepth() : base(EfxAttributeType.ScaleByDepth) { }

	public uint Flags;
	public float NearStart;
	public float NearEnd;
	public float NearScale;
	public float FarStart;
	public float FarEnd;
	public float FarScale;

}
