using System.Numerics;
using ReeLib.Efx.Structs.Common;
using ReeLib.InternalAttributes;

namespace ReeLib.Efx.Structs.Main;

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeStrainRibbon, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3)]
public partial class EFXAttributeTypeStrainRibbonV1 : ReeLib.Efx.EFXAttribute
{
    public EFXAttributeTypeStrainRibbonV1() : base(EfxAttributeType.TypeStrainRibbon) { }

    uint ukn1;
	public via.Color color1;
	public via.Color color2;
	public float unkn1_3;
    public float ukn2_0;
    public float ukn2_1;
    public float ukn2_2;
    public float ukn2_3;
    public float ukn2_4;
    public float ukn2_5;
    public float ukn2_6;
    public float ukn2_7;
    public uint ukn2_8;
    public float ukn2_9;
    public float ukn2_10;
    public float ukn2_11;
    public float ukn2_12;
    public float ukn2_13;
    public float ukn2_14;
    public float ukn2_15;
    public float ukn2_16;
    public float ukn2_17;
    public float ukn2_18;
    public uint ukn2_19;
    public uint ukn2_20;
    public uint ukn2_21;
    public float ukn2_22;
    public float ukn2_23;
    public float ukn2_24;
    public uint ukn2_25;
    public float ukn2_26;
    public uint ukn2_27;
    [RszVersion(EfxVersion.RE2, EndAt = nameof(ukn3_7))]
    public uint ukn3_0;
    public uint ukn3_1;
    public float ukn3_2;
    public via.Color color3;
    public via.Color color4;
    public via.Color color5;
    public float ukn3_6;
    public float ukn3_7;
    [RszVersion(EfxVersion.RE3, EndAt = nameof(ukn4_1))]
    public float ukn4_0;
    public float ukn4_1;
    [RszInlineWString(ByteSize = true)] public string? boneName;

    public override string ToString() => !string.IsNullOrEmpty(boneName) ? boneName : type.ToString();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeStrainRibbon, EfxVersion.RE8, EfxVersion.RERT)]
public partial class EFXAttributeTypeStrainRibbonV2 : EFXAttribute, IBoneRelationAttribute
{
	public EFXAttributeTypeStrainRibbonV2() : base(EfxAttributeType.TypeStrainRibbon) { }

	public uint unkn1_0;
	public via.Color color0;
	public via.Color color1;
	public float unkn1_3;

	public float unkn1_4;
	public uint unkn1_5;
	public float unkn1_6;
	public float unkn1_7;
	public float unkn1_8;
	public float unkn1_9;
	public float unkn1_10;
	public float unkn1_11;
	public uint unkn1_12;
	public float unkn1_13;
	public float unkn1_14;
	public float unkn1_15;
	public float unkn1_16;
	public float unkn1_17;
    [RszVersion(EfxVersion.RERT)]
	public float sb_unkn0;

	public uint unkn1_18;
	public uint unkn1_19;
    [RszVersion(EfxVersion.RERT)]
	public uint unkn1_20;
    [RszVersion('<', EfxVersion.RERT)]
	public float unkn1_20_re8;
	public float unkn1_21;

	public via.Color color2;
	public via.Color color3;
	public via.Color color4;
	public float unkn1_25;
	public float unkn1_26;
	public float unkn1_27;
	public float unkn1_28;
	public float unkn1_29;
	public float unkn1_30;
	public float unkn1_31;
	public float unkn1_32;
	public uint unkn1_33;
	public uint unkn1_34;
	public float unkn1_35;
	public float unkn1_36;
	public float unkn1_37;
	public float unkn1_38;
	public float unkn1_39;
	public float unkn1_40;
	public uint unkn1_41;
	public float unkn1_42;
	public float unkn1_43;
	public float unkn1_44;
	public float unkn1_45;
	public float unkn1_46;
	public float unkn1_47;
	public uint unkn1_48;
	public uint unkn1_49;
	public uint unkn1_50;
	public uint unkn1_51;
	public uint unkn1_52;
	public float unkn1_53;
	public float unkn1_54;
	public float unkn1_55;
	public uint unkn1_56;
	public byte unkn2;
	public float unkn3_0;
	public float sb_unkn1;

	public float unkn3_1;

    public float unkn3_2;
    public float unkn3_3;
	[RszInlineWString(ByteSize = true)] public string? boneName;

    public string? ParentBone { get; set; }

    public override string ToString() => !string.IsNullOrEmpty(boneName) ? boneName : type.ToString();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeStrainRibbon, EfxVersion.RE4)]
public partial class EFXAttributeTypeStrainRibbonV3 : EFXAttribute, IBoneRelationAttribute
{
	// structure looks vaguely similar to TypeStrainRibbon, but big reodering
	public EFXAttributeTypeStrainRibbonV3() : base(EfxAttributeType.TypeStrainRibbon) { }

	// re4.exe（vtable 7a0f480，加载器 3d5cf70，块 3d5d1b0，逐粒子 3d60310）。粗体意义的名字来自文本解析器 3d59480：
	// InitialPosition、TerminalPosition、Width、Length（别名 Sag）、FrontDirectionType、AxisRotation、EnableCutoff、Cutoff*、
	// RangeX/Y/Z、RangeDivideNum、LocalRotation、ColorRange、FadeByTwistMin/Max。其余名字按作用起。
	// 每个粒子一根从粒子拉到骨骼（boneName）或发射器的"绳/光束"，SegmentNum + 1 个点。
	// Flags：0x2 = 用内嵌发射形状给终点加抖动；0x4 = 起点只用 InitialPosition；0x100 = 总用悬链线；0x400 = EnableCutoff；
	// 0x1 = 每段 UV 重复一次；0x2000/0x4000/0x8000 = UV 按世界长度平铺。
    public uint Flags;
	// 起点 = 粒子世界位置 + InitialPosition（按粒子旋转）。
    public Vector3 InitialPosition;
	// 终点：在 boneName 骨骼（找不到时为发射器）坐标系里。
    public Vector3 TerminalPosition;
    public int SegmentNum;
    public via.Range Width;
	// 绳长；两端距离 < Length × 尺寸且 SegmentNum ≥ 2 时沿 SagAxis 下垂成悬链线（3d5d3c0）。
    public via.Range Length;
    public float unkn3_4;
    public uint FrontDirectionType;
	// 下垂轴：{+X, +Y, +Z, −X, −Y, −Z} 的索引，4 = 向下。
    public uint SagAxis;
	// 度（×π/180，3d60608）。
    public via.Range AxisRotation;
	// 渐变色块（3cffac0 读取；名字按 TypeRibbonLength 的同类字段推断）。
    public via.Color HeadColor;
    public via.Color ColorPlace1;
    public via.Color ColorPlace2;
	public float ColorPlace1Ratio;
    public float ColorPlace2Ratio;
    public float FadeByTwistMin;
    public float FadeByTwistMax;
	// 断绳（Flags 0x400）：拉伸超过 EnduranceLength 并持续超过 EnduranceFrame 帧后断开；断开后的逻辑 3d5fb30 未追。
    public via.Range CutoffRatio;
    public via.Range CutoffEnduranceLength;
    public via.RangeI CutoffEnduranceFrame;
    public via.Range CutoffGravity;
    public via.Range CutoffBendScaleCoef;
	// Wilds 多出的两个字段，原先放在 CutoffBendScaleCoef 的 s 与 r 之间（位置本来就是猜的），现移到其后；Wilds 读取的字节不变。
	[RszVersion(EfxVersion.MHWilds, EndAt = nameof(wilds_Ukn2))]
	public uint wilds_Ukn1;
	public uint wilds_Ukn2;
    public via.Range CutoffBendSmooth;
	// Flags 0x2000 时按长度平铺 UV 用的系数（名字为推测）。
    public float LengthUVScale;

	// 内嵌的 EmitterShape3D（加载器读 UniqueID + 0x54 字节），只在 Flags 0x2 时用于终点抖动；字段同 EmitterShape3D。
    public uint ShapeUniqueID;
    public via.Range ShapeRangeX;
    public via.Range ShapeRangeY;
    public via.Range ShapeRangeZ;
	// Wilds 多出的两个颜色，原先放在 ShapeRangeY 的 s 与 r 之间，现移到 ShapeRangeZ 之后；Wilds 读取的字节不变。
	[RszVersion(EfxVersion.MHWilds, EndAt = nameof(wilds_Color2))]
	public via.Color wilds_Color1;
	public via.Color wilds_Color2;
    public uint ShapeType;
    public uint ShapeRangeDivideNum;
    public uint ShapeRangeDivideAxis;
    public uint ShapeRangeDivideHorizontalNum;
    public uint ShapeRangeDivideVerticalNum;
    public Vector3 ShapeLocalRotation;

    public int ShapeRotationOrder;
	public uint ShapeRotationCorrect;
	[RszVersion(EfxVersion.MHWilds)]
	public uint wilds_Ukn2_1;
    public bool ShapeDivideEquidistant;
    public bool ShapeDivideEquidistantCalcOuterCurve;
    public bool ShapeDivideEquidistantRecalcEveryFrame;
    public bool ShapeUseExtension;
    public via.Range ShapeScaleHorizontal;
    public via.Range ShapeScaleVertical;
	// past this is wild territory for Wilds
	[RszVersion(EfxVersion.MHWilds, EndAt = nameof(wilds_UknLast))]
	public uint wilds_Ukn3_1;
	public UndeterminedFieldType wilds_Ukn3_2;
	public UndeterminedFieldType wilds_Ukn3_3;
	public UndeterminedFieldType wilds_Ukn3_4;
	public float wilds_Ukn3_5;
	public float wilds_Ukn3_6;
	public UndeterminedFieldType wilds_UknLast;
	[RszInlineWString(ByteSize = true)] public string? boneName;

	// 共用颜色头部放在文件末尾（与其他 Type 不同）。
	[RszVersion('<', EfxVersion.MHWilds, EndAt = nameof(AlphaRate))]
	public uint BlendFlags;
	public via.Color Color;
	public via.Color ColorRange;
	public float ColorRate;
	public float Intensity;
	public float EdgeBlendRange;
	public float AlphaRate;

    // MHWs-EFX-Editor 本地补丁：这个类型在 MHWilds 下确实会消费一个 BoneRelations 槽位（全语料
    // 9175 个文件实测：不把它当消费者时，220 个作用域的索引流整体错位、导致 1158 处
    // ParentBone 被解析成别的骨骼；两个类型（TypeStrainRibbonV3 + FluidParticle2DSimulator）都算上后降到 1 处）。
    // 见 KNOWN_UPSTREAM_ISSUES.md #9 / tools/vendor-patches/README.md。
    public string? ParentBone { get; set; }

    public override string ToString() => !string.IsNullOrEmpty(boneName) ? boneName : type.ToString();
}


[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeStrainRibbonExpression, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.MHWilds)]
public partial class EFXAttributeTypeStrainRibbonExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value!; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeTypeStrainRibbonExpression() : base(EfxAttributeType.TypeStrainRibbonExpression) { }

	// TODO bitset versioning
	[RszClassInstance] public BitSet expressionBits = new BitSet(24) { BitNameDict = new () {
		[3] = nameof(posX), // rert: moved up by 2 (xyz = 567)
		[4] = nameof(posY),
		[5] = nameof(posZ),
	} };
	public ExpressionAssignType unkn1;
	public ExpressionAssignType unkn2;
	public ExpressionAssignType posX;
	public ExpressionAssignType posY;
	public ExpressionAssignType posZ;
	public ExpressionAssignType unkn6;
	public ExpressionAssignType unkn7;
	public ExpressionAssignType unkn8;
	public ExpressionAssignType unkn9;
	public ExpressionAssignType unkn10;
	public ExpressionAssignType unkn11;
	public ExpressionAssignType unkn12;
    [RszVersion(EfxVersion.RE8, EndAt = nameof(unkn14))]
	public ExpressionAssignType unkn13;
	public ExpressionAssignType unkn14;
	public ExpressionAssignType unkn15;

    [RszVersion(EfxVersion.MHWilds, EndAt = nameof(unkn24))]
	public ExpressionAssignType unkn16;
	public ExpressionAssignType unkn17;
	public ExpressionAssignType unkn18;
	public ExpressionAssignType unkn19;
	public ExpressionAssignType unkn20;
	public ExpressionAssignType unkn21;
	public ExpressionAssignType unkn22;
	public ExpressionAssignType unkn23;
	public ExpressionAssignType unkn24;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}


[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeStrainRibbonExpression, EfxVersion.DD2)]
public partial class EFXAttributeTypeStrainRibbonExpressionV2 : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeTypeStrainRibbonExpressionV2() : base(EfxAttributeType.TypeStrainRibbonExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(24) { BitNameDict = new () {
		[13] = nameof(posX),
		[14] = nameof(posY),
		[15] = nameof(posZ),
		[16] = nameof(terminalPosX),
		[17] = nameof(terminalPosY),
		[18] = nameof(terminalPosZ),
	} };
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
    public ExpressionAssignType unkn12;
    public ExpressionAssignType posX;
    public ExpressionAssignType posY;
    public ExpressionAssignType posZ;
    public ExpressionAssignType terminalPosX;
    public ExpressionAssignType terminalPosY;
    public ExpressionAssignType terminalPosZ;
    public ExpressionAssignType unkn19;
    public ExpressionAssignType unkn20;
    public ExpressionAssignType unkn21;
    public ExpressionAssignType unkn22;
    public ExpressionAssignType unkn23;
    public ExpressionAssignType unkn24;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeStrainRibbonMaterial, EfxVersion.DD2, EfxVersion.MHWilds)]
public partial class EFXAttributeTypeStrainRibbonMaterial : EFXAttribute
{
	public EFXAttributeTypeStrainRibbonMaterial() : base(EfxAttributeType.TypeStrainRibbonMaterial) { }

	public uint unkn0;
	public UndeterminedFieldType unkn1;
	public uint unkn2;
	public UndeterminedFieldType unkn3;
	public UndeterminedFieldType unkn4;
	public UndeterminedFieldType unkn5;
	public float unkn6;
	public float unkn7;
	public float unkn8;
	public uint unkn9;
	public float unkn10;
	public float unkn11;
	public float unkn12;
	public float unkn13;
	public float unkn14;
	public uint unkn15;
	public uint	 unkn16;
	public float unkn17;
	public float unkn18;

	public via.Color color3;
	public via.Color color4;
	public via.Color color5;

	public float unkn22;
	public float unkn23;
	public float unkn24;
	public float unkn25;
	public float unkn26;
	public float unkn27;
	public float unkn28;
	public float unkn29;
	public float unkn30;
	public float unkn31;

	public float unkn32;
	public float unkn33;
	public float unkn34;
	[RszVersion(EfxVersion.MHWilds)]
	public uint wildsUnkn0;
	public uint unkn35;
	public float unkn36;
	public UndeterminedFieldType unkn37;
	public float unkn38;
	public UndeterminedFieldType unkn39;
	public float unkn40;
	public float unkn41;
	public float unkn42;
	[RszVersion(EfxVersion.MHWilds)]
	public uint wildsUnkn1;
	[RszVersion(EfxVersion.MHWilds, EndAt = nameof(wildsColor2))]
	public via.Color wildsColor1;
	public via.Color wildsColor2;
	public float unkn43;
	[RszVersion(EfxVersion.MHWilds)]
	public uint wildsUnkn4;

	public float unkn44;
	public float unkn45;

	[RszVersionExact(EfxVersion.DD2)]
	public uint unkn46;
	public float unkn47;
	public uint unkn48;
	[RszVersion(EfxVersion.MHWilds)]
	public UndeterminedFieldType unkn49;
	public UndeterminedFieldType unkn50;
	public float unkn51;
	public float unkn52;
	public uint unkn53;
	public uint unkn54;
	[RszVersionExact(EfxVersion.DD2)]
	public uint dd2Unkn;
	public UndeterminedFieldType unkn55;
	public UndeterminedFieldType unkn56;
	public float unkn57;
	public float unkn58;
	public float unkn59;
	public UndeterminedFieldType unkn60;
	[RszInlineWString] public string? propName;
	public UndeterminedFieldType unkn61;
	public via.Color unkn62;
	public via.Color unkn63;
	public float unkn64;
	public UndeterminedFieldType unkn65;
	public UndeterminedFieldType unkn66;
	public UndeterminedFieldType unkn67;

	[RszConstructorParams(nameof(Version)), RszSwitch(
		nameof(Version), ">=", EfxVersion.DD2, typeof(EfxMaterialStructV2),
		typeof(EfxMaterialStructV1)
	)]
	public EfxMaterialStructBase? material;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeStrainRibbonMaterialClip, EfxVersion.DD2)]
public partial class EFXAttributeTypeStrainRibbonMaterialClip : EFXAttribute, IMaterialClipAttribute
{
    public EfxMaterialClipData MaterialClip => clipData;
    public BitSet ClipBits => clipBits;

    public EFXAttributeTypeStrainRibbonMaterialClip() : base(EfxAttributeType.TypeStrainRibbonMaterialClip) { }

	[RszClassInstance] public BitSet clipBits = new BitSet(1);
    public uint unkn1;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public readonly EfxMaterialClipData clipData = new();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeStrainRibbonMaterialExpression, EfxVersion.DD2, EfxVersion.MHWilds)]
public partial class EFXAttributeTypeStrainRibbonMaterialExpression : EFXAttribute, IExpressionAttribute, IMaterialExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
    public EFXMaterialExpressionList? MaterialExpressions { get => materialExpressions; set => materialExpressions = value; }

	public BitSet ExpressionBits => expressionBits;

    public EFXAttributeTypeStrainRibbonMaterialExpression() : base(EfxAttributeType.TypeStrainRibbonMaterialExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(22) { BitNameDict = new () {
		[1] = nameof(color),
		[5] = nameof(posX),
		[6] = nameof(posY),
		[7] = nameof(posZ),
		[8] = nameof(terminalPosX),
		[9] = nameof(terminalPosY),
		[10] = nameof(terminalPosZ),
		[15] = nameof(color2),
		[17] = nameof(color3),
		[19] = nameof(color4),
	} };
	public ExpressionAssignType color;
	public ExpressionAssignType unkn2;
	public ExpressionAssignType unkn3;
	public ExpressionAssignType unkn4;
	public ExpressionAssignType posX;
	public ExpressionAssignType posY;
	public ExpressionAssignType posZ;
	public ExpressionAssignType terminalPosX;
	public ExpressionAssignType terminalPosY;
	public ExpressionAssignType terminalPosZ;
	public ExpressionAssignType unkn11;
	public ExpressionAssignType unkn12;
	public ExpressionAssignType unkn13;
	public ExpressionAssignType unkn14;
	public ExpressionAssignType color2;
	public ExpressionAssignType unkn16;
	public ExpressionAssignType color3;
	public ExpressionAssignType unkn18;
	public ExpressionAssignType color4;
	public ExpressionAssignType unkn20;
	[RszVersion(EfxVersion.MHWilds)]
	public ExpressionAssignType unkn21;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
	[RszVersionExact(EfxVersion.DD2)] // TODO probably shouldn't be here, need to fully resolve MHWilds struct
	public ExpressionAssignType unkn22;
	[RszArraySizeField(nameof(materialExpressions))] public int materialExpressionCount;
	[RszVersion(EfxVersion.DD2), RszClassInstance, RszConstructorParams(nameof(Version))]
	public EFXMaterialExpressionList? materialExpressions;
}
