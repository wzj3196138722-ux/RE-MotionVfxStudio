using ReeLib.Efx.Structs.Common;
using ReeLib.InternalAttributes;

namespace ReeLib.Efx.Structs.Fluid;

public struct FloatWithColor
{
    public float value;
    public via.Color color;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FluidEmitter2D, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE8)]
public partial class EFXAttributeFluidEmitter2D : ReeLib.Efx.EFXAttribute
{
    public EFXAttributeFluidEmitter2D() : base(EfxAttributeType.FluidEmitter2D) { }

    public uint unkn0;
    [RszVersion(EfxVersion.DD2)]
    public uint dd2_unkn0;
    public float unkn1;
    public float unkn2;
    public float unkn3;
    public float unkn4;
    public float unkn5;
    public float unkn6;
    public float unkn7;
    [RszVersion(EfxVersion.RE8, EndAt = nameof(unkn9))]
    public float unkn8;
    public float unkn9;
    [RszVersion(EfxVersion.DD2, EndAt = nameof(unkn11))]
	public float unkn10;
	public float unkn11;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FluidEmitter2DExpression, EfxVersion.DD2)]
public partial class EFXAttributeFluidEmitter2DExpression : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeFluidEmitter2DExpression() : base(EfxAttributeType.FluidEmitter2DExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(2);
	public ExpressionAssignType unkn2;
	public ExpressionAssignType unkn3;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FluidSimulator2D, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE8, EfxVersion.RERT)]
public partial class EFXAttributeFluidSimulator2D : ReeLib.Efx.EFXAttribute
{
    public EFXAttributeFluidSimulator2D() : base(EfxAttributeType.FluidSimulator2D) { }

    // 已命名字段取自 re4.exe 文本解析器（slot 18），文件偏移由原生标记实验（加载器 slot 5 → 构建器 slot 15）对上。
    public byte unkn0_1;
    public byte unkn0_2;
    public byte unkn0_3;
    public byte unkn0_4;
    public uint unkn1;
    public uint unkn2;
    public uint unkn3;
    public uint unkn4;

    public float unkn5;
    public float unkn6;
    public float PostEdgeDropoff;
    public float unkn8;

    public uint unkn9;
    public uint unkn10;
    public uint unkn11;
    public uint unkn12;

    public float unkn13;
    public float unkn14;
    public float DensityColorMin;
    public float DensityColorMax;
    public float DensityAlphaMin;
    public float DensityAlphaMax;

    public uint unkn19;
    public uint unkn20;
    public uint unkn21;
    public uint unkn22;
    [RszVersion(EfxVersion.RE4, EndAt = nameof(re4_unkn2))]
    public float re4_unkn1;
    public float re4_unkn2;

    public float unkn1_23;
    public float unkn1_24;
    public float unkn1_25;
    public float InternalInherit;
    public float unkn1_27;
    // NOTE: the fields here seem to have gotten order swapped around past this point
    // TODO figure out correct re-ordering, match up names and read/write procedures
    [RszVersion(EfxVersion.RE8, EndAt = nameof(ProjectionAdjustment))]
    public float FlowStrength;
    public float ProjectionAdjustment;
    [RszVersion('<', EfxVersion.RE8, EndAt = nameof(unkn1_29_re3))] // else
    public int unkn1_28_re3;
    public int unkn1_29_re3;
    public float ProjectionTimeScale;
    public float unkn1_31;
    public float unkn1_32;
    public float unkn1_33;

    [RszVersion("<", EfxVersion.DD2)]
    public float unkn1_34_v1;
    [RszVersion(EfxVersion.DD2)] // else
    public uint unkn1_34_v2;
    public float unkn1_35;

    [RszVersion(EfxVersion.RE4, EndAt = nameof(re4_unk2_10))]
    public float re4_unk2_0;
    public float re4_unk2_1;
    public float re4_unk2_2;
    public float re4_unk2_3;
    public float re4_unk2_4;
    public float re4_unk2_5;
    public float re4_unk2_6;
    public float re4_unk2_7;
    public float re4_unk2_8;
    public float re4_unk2_9;
    public float re4_unk2_10;

    [RszVersion("<", EfxVersion.DD2)]
    public uint unkn1_36_v1;
    [RszVersion(EfxVersion.DD2)] // else
    public float unkn1_36_v2;

    public float unkn1_37;
    public float unkn1_38;
    [RszVersion(EfxVersion.RE8)]
    public float unkn1_39_re8;
    [RszVersion("<", EfxVersion.RE8)] // else
    public int unkn1_39_re3;
    public float unkn1_40;

    [RszVersion("<", EfxVersion.RE4)]
    public float unkn1_41;
    [RszVersion(EfxVersion.RE8)]
    public int unkn1_42_re8;
    [RszVersion("<", EfxVersion.RE8)] // else
    public float unkn1_42_re3;

    [RszVersion(EfxVersion.DD2, EndAt = nameof(dd2_unk2))]
    public float dd2_unk1;
    public uint dd2_unk2;

    [RszVersion("<", EfxVersion.RERT)]
    public UndeterminedFieldType unkn2_0;
    [RszVersion("!=", EfxVersion.RE8, EndAt = nameof(unkn2_2))]
    public uint unkn2_1;
    public uint unkn2_2;
    [RszVersion(EfxVersion.RE4, EndAt = nameof(re4_unk3_3))]
    public float re4_unk3_1;
    public float re4_unk3_2;
    public uint re4_unk3_3;

    public uint extraByteCount;
    public uint path1Size;
    public uint path2Size;
    public uint path3Size;
    public uint path4Size;
    [RszVersion(nameof(Version), "<=", EfxVersion.RE3, "||", nameof(Version), ">=", EfxVersion.RE4)]
    public uint path5Size;
    [RszInlineWString(nameof(path1Size))] public string? uvsPath1; // turbulence
    [RszInlineWString(nameof(path2Size))] public string? uvsPath2; // source density map
    [RszInlineWString(nameof(path3Size))] public string? uvsPath3;
    [RszInlineWString(nameof(path4Size))] public string? uvsPath4; // density colormap
    [RszVersion(nameof(Version), "<=", EfxVersion.RE3, "||", nameof(Version), ">=", EfxVersion.RE4), RszInlineWString(nameof(path5Size))]
    public string? uvsPath5; // projection diffuse map
    [RszFixedSizeArray(nameof(extraByteCount))] public byte[]? extraBytes;

    // may be an array - count in re4_unk3_3?
    [RszVersion(EfxVersion.DD2), RszFixedSizeArray(nameof(re4_unk3_3))]
    public FloatWithColor[]? gradient;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FluidParticle2DSimulator, EfxVersion.MHWilds)]
public partial class EFXAttributeFluidParticle2DSimulator : ReeLib.Efx.EFXAttribute, IBoneRelationAttribute
{
    public EFXAttributeFluidParticle2DSimulator() : base(EfxAttributeType.FluidParticle2DSimulator) { }

    // 命名字段取自 MonsterHunterWilds.exe 文本解析器（vtable e100240），文件偏移经构建器（slot 16）对上；
    // 加载器先读 168 字节定长段（Flags .. dataCount），其余字符串与数组另读。
    public uint Flags;
    public uint unkn1;
    public uint unkn2;
    public uint unkn3;
    public uint unkn4;

    public float unkn5;
    public float VortexelWindTransitionRate;
    public float VortexelWindTransitionThreshold;
    public float VortexelWindMaxSpeed;

    public UndeterminedFieldType unkn9;
    public UndeterminedFieldType unkn10;
    public float unkn11;
    public float PostEdgeDropoff;

    public float DensityColorMin;
    public float DensityColorMax;
    public float DensityAlphaMin;
    public float DensityAlphaMax;
    public float unkn17;
    public float unkn18;

    public float unkn19;
    public float SourceDensityScale;
    public float SourceVelocityScale;
    public UndeterminedFieldType unkn22;
    public float unkn23;
    public float WaterVelocityScale;

    public float WaterVelocityAddRate;
    public UndeterminedFieldType unkn26;
    public UndeterminedFieldType unkn27;
    public UndeterminedFieldType unkn28;
    public UndeterminedFieldType unkn29;
    public float unkn30;
    public float unkn31;
    public UndeterminedFieldType unkn32;
    public float InternalInherit;

    public float Buoyancy;
    public float unkn35;
    public int unkn36;
    public int unkn37;
    public int unkn38;
    public UndeterminedFieldType unkn39;
    public UndeterminedFieldType unkn40;
    public int dataCount;
    [RszInlineWString(ByteSize = true)] public string? str1;
    [RszInlineWString(ByteSize = true)] public string? str2;
    [RszInlineWString(ByteSize = true)] public string? str3;
    [RszInlineWString(ByteSize = true)] public string? str4;
    [RszInlineWString(ByteSize = true)] public string? str5;

    [RszFixedSizeArray(nameof(dataCount))] public HashValue[]? data;

    // MHWs-EFX-Editor 本地补丁：这个类型在 MHWilds 下确实会消费一个 BoneRelations 槽位（全语料
    // 9175 个文件实测：不把它当消费者时，220 个作用域的索引流整体错位、导致 1158 处
    // ParentBone 被解析成别的骨骼；两个类型（TypeStrainRibbonV3 + FluidParticle2DSimulator）都算上后降到 1 处）。
    // 见 KNOWN_UPSTREAM_ISSUES.md #9 / tools/vendor-patches/README.md。
    public string? ParentBone { get; set; }

    public struct HashValue
    {
        public float Value;
        public uint Hash;
    }
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FluidParticleEmitter, EfxVersion.MHWilds)]
public partial class EFXAttributeFluidParticleEmitter : EFXAttribute
{
	public EFXAttributeFluidParticleEmitter() : base(EfxAttributeType.FluidParticleEmitter) { }

	// 名字取自 MonsterHunterWilds.exe 文本解析器（vtable e1088f0）；Flags 不在解析器里。
	public uint Flags;
	public float DensityScale;
	public float VelocityScale;
	public float SizeScale;
	public float PressureScale;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FluidParticleEmitterTarget, EfxVersion.MHWilds)]
public partial class EFXAttributeFluidParticleEmitterTarget : EFXAttribute
{
	public EFXAttributeFluidParticleEmitterTarget() : base(EfxAttributeType.FluidParticleEmitterTarget) { }

	public uint Flags;
	public uint Unkn1;
	public uint Unkn2;
	public uint Unkn3;
	public uint Unkn4;
	[RszInlineWString(ByteSize = true)] public string? UserDataFilePath;
}
