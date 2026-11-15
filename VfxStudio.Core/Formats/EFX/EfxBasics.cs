using ReeLib.Efx.Structs.Common;
using ReeLib.InternalAttributes;
using ReeLib.via;

namespace ReeLib.Efx.Structs.Basic;

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Spawn, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeSpawn : EFXAttribute
{
	public EFXAttributeSpawn() : base(EfxAttributeType.Spawn) { }

	public uint MaxParticles;
	[RszVersion('>', EfxVersion.RE3)]
	public uint ParticleInterval;
	public MinMaxI SpawnNum;
	public MinMaxI IntervalFrame;
	[RszVersion("<=", EfxVersion.RE3)]
	public via.RangeI LoopNumDmc5;

	[RszVersion('>', EfxVersion.RE3, EndAt = nameof(UseSpawnFrame))]
	[RszVersion('<', EfxVersion.DD2)]
	public uint UseSpawnFrameInt;
	[RszVersion(EfxVersion.DD2)] public bool UseSpawnFrame;

	public MinExtraI SpawnFrame;

	[RszVersion(">", EfxVersion.RE3, EndAt = nameof(OriginalMaxParticles))]
	public MinExtraI LoopNum;

	public MinMaxI EmitterDelayFrame;

	// RE4 起就有这一段（re4.exe 加载器 3d15720 读 UniqueID + 0x58 字节，tick 3d14c20 使用），字段顺序与 DD2 相同；
	// RE4 的开关是 4 字节，DD2 起是 1 字节 bool，所以 RE4 用 *Int 版本（同 UseSpawnFrameInt 的约定）。
	// RingBufferMode：仅 GPU 类型，写入位置在粒子池内循环、覆盖最旧粒子。
	// UseLoopNumRatio：LoopNum 改为 r + s·t，t 由 SpawnExpression 给出。
	// Interpolate：开启按距离发射（DistancePerSpawn 米一个），并把本帧粒子沿发射器路径摊开。
	// RevivalNum：额外的轮数 = r + randint[0, s]，{0,0} = 无限；RevivalInterval：下一轮前等待的帧数 = r + randint[0, s]。
	[RszVersion(EfxVersion.RE4, EndAt = nameof(RevivalInterval))]
	[RszVersion('<', EfxVersion.DD2)] public uint RingBufferModeInt;
	[RszVersion(EfxVersion.DD2)] public bool RingBufferMode;
	[RszVersion('<', EfxVersion.DD2)] public uint UseLoopNumRatioInt;
	[RszVersion(EfxVersion.DD2)] public bool UseLoopNumRatio;
	[RszVersion('<', EfxVersion.DD2)] public uint InterpolateInt;
	[RszVersion(EfxVersion.DD2)] public bool Interpolate;
	public float DistancePerSpawn;
	[RszVersion('<', EfxVersion.DD2)] public uint UseRevivalInt;
	[RszVersion(EfxVersion.DD2)] public bool UseRevival;
	public MinExtraI RevivalNum;
	public MinExtraI RevivalInterval;

	[RszVersion(EfxVersion.DD2)] public bool TrySpawnAllParticles;

	// RE4 文件里没有下面两个字段（该位置是 RevivalInterval）；其他版本保持原样，未核对。
	[RszVersion(EfxVersion.RE8, EndAt = nameof(SpawnChanceFrame))]
	[RszVersion("!=", EfxVersion.RE4)]
	public uint SpawnChanceFrame;
    [RszVersion(EfxVersion.DD2)] public bool InitializeFull;
    [RszVersion(EfxVersion.RERT, EndAt = nameof(OriginalMaxParticles))]
    [RszVersion("!=", EfxVersion.RE4)]
    public uint OriginalMaxParticles;

    /*
	[RszVersion(EfxVersion.RE4, EndAt = nameof(re4_unkn6))]

	[RszVersion(EfxVersion.DD2)] public byte dd2_unkn5;
	public uint re4_unkn5;
	[RszVersion("<", EfxVersion.DD2, EndAt = nameof(re4_unkn6))]
	public uint re4_unkn6;
    */
    [RszVersion(EfxVersion.MHWilds)] public byte mhws_unkn_toggle;

    public override string ToString() => $"Count = {SpawnNum}";
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.SpawnExpression, EfxVersion.RE3, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeSpawnExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeSpawnExpression() : base(EfxAttributeType.SpawnExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(7) { BitNames = [nameof(spawnNum), nameof(spawnNumRange), nameof(intervalFrame), nameof(intervalFrameRange), nameof(emitterDelayFrame), nameof(emitterDelayFrameRange), nameof(speed) ] };
	public ExpressionAssignType spawnNum;
	public ExpressionAssignType spawnNumRange;
	public ExpressionAssignType intervalFrame;
	public ExpressionAssignType intervalFrameRange;
	[RszVersion(EfxVersion.RE3, EndAt = nameof(emitterDelayFrameRange))]
	public ExpressionAssignType emitterDelayFrame;
	public ExpressionAssignType emitterDelayFrameRange;
    [RszVersion(EfxVersion.RERT)]
	public ExpressionAssignType speed;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ParentOptions, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeParentOptions : EFXAttribute, IBoneRelationAttribute
{
	public EFXAttributeParentOptions() : base(EfxAttributeType.ParentOptions) { }

	public via.Int3 RelationPos;//TODO ENUM - ParentConstraint
	public via.Int3 RelationRot;
	public via.Int3 RelationScl;
	[RszVersion('<', EfxVersion.RE8)]
	public uint ParticleUseLocal_re7;
	[RszVersion(EfxVersion.RE8, EndAt = nameof(ConstInheritReleaseRate))]
	public byte ParticleUseLocal;
	public CenterSpread ConstInheritRate;
	public MinExtraI ConstFrame;
    public MinExtraI ConstReleaseFrame;
	public float ConstInheritReleaseRate;

	[RszVersion(EfxVersion.Pragmata)] public uint PragUkn1;
	[RszVersion(EfxVersion.Pragmata)] public uint PragUkn2;

	[RszInlineWString(ByteSize = true)] public string? BoneName;

    public string? ParentBone { get; set; }

    public override string ToString() => !string.IsNullOrEmpty(BoneName) ? BoneName : type.ToString();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ParentOptionsExpression, EfxVersion.DD2)]
public partial class EFXAttributeParentOptionsExpression : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeParentOptionsExpression() : base(EfxAttributeType.ParentOptionsExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(8);
    public ExpressionAssignType unkn1_1;
    public ExpressionAssignType unkn1_2;
    public ExpressionAssignType unkn1_3;
    public ExpressionAssignType unkn1_4;
    public ExpressionAssignType unkn1_5;
    public ExpressionAssignType unkn1_6;
    public ExpressionAssignType unkn1_7;

	[RszVersion(EfxVersion.MHWilds)]
    public ExpressionAssignType unkn1_8;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.Life, EfxVersion.RE7, EfxVersion.DMC5, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeLife : EFXAttribute
{
	public MinMaxI AppearFrame;
	public MinMaxI KeepFrame;
	public MinMaxI VanishFrame;
	public MinMaxI KeepHoldFrame;
	// 低 2 位是保持模式（re4.exe 3d1be80）：0 不保持（忽略 KeepHoldFrame）；1 Keep 后满 alpha 保持 KeepHoldFrame 帧，
	// {0,0} = 直到发射器停止；2 同 1，但释放时再播一遍 Keep；3 CPU 每帧把 alpha 置 0（不要用）。
	// 四个 *Frame 在 [r, s] 内均匀取整（不是 s±r），s = r − 1 会除以零。
	[RszVersion(EfxVersion.RE2)]
	public uint Flags;

	public EFXAttributeLife() : base(EfxAttributeType.Life) { }

    public override string ToString() => $"Appear {AppearFrame}, Keep {KeepFrame}, Vanish {VanishFrame}";
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.LifeExpression, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeLifeExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeLifeExpression() : base(EfxAttributeType.LifeExpression) { }

	// re7 and dd2 have value + rand pairs: 1-2 = appear, 3-4 = keep, 5-6 = vanish
	// rest have just singular values
	// TODO bitset versioning
	[RszClassInstance] public BitSet expressionBits = new BitSet(6)  { BitNameDict = new () {
		[1] = nameof(appearLife),
		[2] = nameof(keepLife),
		[3] = nameof(vanishLife),
	} };

	public ExpressionAssignType appearLife;
	[RszVersion(nameof(Version), ">=", EfxVersion.DD2, "||", nameof(Version), "==", EfxVersion.RE7)]
	public ExpressionAssignType appearLifeRand;
	public ExpressionAssignType keepLife;
	[RszVersion(nameof(Version), ">=", EfxVersion.DD2, "||", nameof(Version), "==", EfxVersion.RE7)]
	public ExpressionAssignType keepLifeRand;
	public ExpressionAssignType vanishLife;
	[RszVersion(nameof(Version), ">=", EfxVersion.DD2, "||", nameof(Version), "==", EfxVersion.RE7)]
	public ExpressionAssignType vanishLifeRand;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion))]
public partial class TextureUnitData : BaseModel
{
	public uint unkn1;
	public via.Color color;
	public float unkn3;
	public float unkn4;
	public UndeterminedFieldType unkn5;
	public float unkn6;
	public float unkn7;
	public float unkn8;
	public float unkn9;
	public float unkn10;
	public float unkn11;
	public float unkn12;
	public UndeterminedFieldType unkn13;
	public float unkn14;
	public UndeterminedFieldType unkn15;
	public float unkn16;
	public float unkn17;
	public float unkn18;
	public float unkn19;
	public float unkn20;
	public UndeterminedFieldType unkn21;
	public float unkn22;
	public float unkn23;
	public float unkn24;
	public float unkn25;
	public float unkn26;
	public UndeterminedFieldType unkn27;
	public float unkn28;
	public UndeterminedFieldType unkn29;
	public float unkn30;
	public UndeterminedFieldType unkn31;
	public float unkn32;
	public UndeterminedFieldType unkn33;
	public uint unkn34;
	public uint unkn35;
	public UndeterminedFieldType unkn36;
	public UndeterminedFieldType unkn37;
	public float unkn38;
	public float unkn39;

    public override string ToString() => $"TextureUnitData: Color = {color}";
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TextureUnit, EfxVersion.DMC5, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeTextureUnit : EFXAttribute
{
	public EFXAttributeTextureUnit() : base(EfxAttributeType.TextureUnit) { }

	public uint unkn1;
	[RszClassInstance] public TextureUnitData? texUnit1;
	[RszClassInstance] public TextureUnitData? texUnit2;
	[RszClassInstance] public TextureUnitData? texUnit3;

	[RszStringLengthField(nameof(uvs0Path))] public int uvs0PathCharCount;
	[RszStringLengthField(nameof(uvs1Path))] public int uvs1PathCharCount;
	[RszStringLengthField(nameof(uvs2Path))] public int uvs2PathCharCount;
	[RszInlineWString(nameof(uvs0PathCharCount))] public string? uvs0Path;
	[RszInlineWString(nameof(uvs1PathCharCount))] public string? uvs1Path;
	[RszInlineWString(nameof(uvs2PathCharCount))] public string? uvs2Path;

    public override string ToString() => $"UVS path: {uvs0Path}, {uvs1Path}, {uvs2Path}";
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TextureUnitExpression, EfxVersion.DD2)]
public partial class EFXAttributeTextureUnitExpression : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeTextureUnitExpression() : base(EfxAttributeType.TextureUnitExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(114);
	[RszFixedSizeArray(114)] public readonly ExpressionAssignType[] assignTypes = new ExpressionAssignType[114];
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TextureFilter, EfxVersion.RE4)]
public partial class EFXAttributeTextureFilter : EFXAttribute
{
	public EFXAttributeTextureFilter() : base(EfxAttributeType.TextureFilter) { }

	public float TexelAlphaRate;
	public float TexelAlphaHPThreshold;
	public float TexelAlphaHPMinValue;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.UVScroll, EfxVersion.RE4)]
public partial class EFXAttributeUVScroll : EFXAttribute
{
	public EFXAttributeUVScroll() : base(EfxAttributeType.UVScroll) { }

	public uint Flags;
    public via.Range uScrollAdd;
    public via.Range vScrollAdd;
    public via.Range uScrollAddCoef;
    public via.Range vScrollAddCoef;
    public via.Range uScrollOffset;
    public via.Range vScrollOffset;

}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.UVSequence, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeUVSequence : EFXAttribute
{
	public EFXAttributeUVSequence() : base(EfxAttributeType.UVSequence) { }

	// re4.exe（vtable 7a0b668，加载器 3d1e030）。引擎名只有 PatternNo、PlaySpeed（文本解析器）；其余沿用原名。
	// .uvs 里第几个序列：在 [r, s) 内随机（s 不含），只存 8 位。RE4 官方全是 {0,1}。
	public MinMaxExclusiveI SequenceNo;
	// 起始格：在 [r, s) 内随机（s 不含），只存 8 位。不要超过序列的格数。
    public MinMaxExclusiveI PatternNo;
	// 在 [s, r] 内均匀取（r 是最大值，不是幅度）；单位：每 1/60 秒前进的格数，1.0 = 60 fps 序列帧。
    public MinMax PlaySpeed;
	// 位 0-1 播放模式：0 只显示起始格（忽略 PlaySpeed）、1 循环（从第 0 格重开）、2 播一遍后粒子死亡、3 播一遍停在最后一格。
	// 位 2-3 水平翻转、位 4-5 垂直翻转、位 6-7 倒放、位 8-9 旋转 90°：各为 0 关、1 总是、2 每粒子随机（3 未定义，别用）。
	// 位 10 = 相邻格交叉淡入；位 11 写入发射器标志（用途未追）；更高位不读。例：21 = 循环 + 固定水平翻转 + 固定垂直翻转。
	public uint Flags;
	[RszInlineWString] public string? UVSPath;

    public override string ToString() => $"UVS path: {UVSPath}";
}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.UVSequenceModifier, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeUVSequenceModifier : EFXAttribute
{
	public EFXAttributeUVSequenceModifier() : base(EfxAttributeType.UVSequenceModifier) { }

	public uint Flags;
	public via.Range PlaySpeedInit;
    public via.Range PlaySpeedFinal;
    public via.Range PlaySpeedChangeTimeCoef;
}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.UVSequenceExpression, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeUVSequenceExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeUVSequenceExpression() : base(EfxAttributeType.UVSequenceExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(6) { BitNameDict = new () {
		[1] = nameof(speed),
		[2] = nameof(speedRand),
		// [3] = nameof(unkn3),
		// [4] = nameof(unkn4),
		// [5] = nameof(unkn5),
		// [6] = nameof(unkn6),
	} };
	public ExpressionAssignType speed;
	public ExpressionAssignType speedRand;
	public ExpressionAssignType unkn3;
	public ExpressionAssignType unkn4;
	public ExpressionAssignType unkn5;
	public ExpressionAssignType unkn6;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.AlphaCorrection, EfxVersion.RE7, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeAlphaCorrection : EFXAttribute
{
	public EFXAttributeAlphaCorrection() : base(EfxAttributeType.AlphaCorrection) { }

	// re4.exe：加载器 3d25d60 读 UniqueID + 0x10 字节，解析器 3d25b10。CPU 只把每粒子 4 字节打包给着色器
	// （Hi、Lo、Curve、模式），重映射公式在像素着色器里，未读到。
	// 无引擎名（按作用起名）：低 2 位 = 模式 0 Disable / 1 AlphaCurve / 2 AlphaThreshold / 3 LifePower。
	// 模式 3 会让 CPU 顶点 alpha 不再乘 Life 渐隐（发射器标志 0x1000）。
	[RszVersion(EfxVersion.RE2)]
	public uint Flags;
	// 引擎名 HiPass（别名 Max、LifePow0）。旧名 LowPass（名字原来是反的）。模式 1/2：重映射上限，取 [0,1]，
	// 打包成 trunc(v·255.5) 的 8 位，超出会回绕；模式 3：[0,8) 的指数。
	public float HiPass;
	// 引擎名 LoPass（别名 Min、LifePow1）。旧名 HighPass。模式 1/2：重映射下限（语料里模式 2 时总有 Hi > Lo）；模式 3：指数。
	public float LoPass;
	// 无引擎名：带符号曲线项，-3..+3（打包成 c/3·127+127.5）；模式 2 时恒为 0。对应 MHWI 的 unkn3。
	public float CurveConst;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ShaderSettings, EfxVersion.RE7, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeShaderSettings : EFXAttribute
{
	public EFXAttributeShaderSettings() : base(EfxAttributeType.ShaderSettings) { }

	// 名字来自 re4.exe（vtable 7a0bd38，加载器 3d27990 读 UniqueID + 0xa4 字节，块 3d27b60，文本解析器 3d26dd0，
	// 每发射器更新 3d281e0）。没有引擎名的字段按作用起名并注明。语义只在 RE4 上核对过。
	// 软粒子距离（米，旧名 Saturation）；只在 Flags 位 2 置位时生效。
	public float SoftParticleDistance;
	// 无引擎名（旧名 unkn2）：0 = 不受光，1–4 = 受光、逐粒子光照分辨率 1/2/4/8（门控已证实，档位含义为推测；
	// 低光照画质时 2–4 被压成 1）。对应 MHWI 的 particleLighting。
	public uint ParticleLighting;
    [RszVersion(EfxVersion.RE8, EndAt = nameof(PriorityBias))]
	// 排序距离偏移（旧名 re8_unkn1）。
	public float DistanceBias;
	// 排序优先级偏移（旧名 re8_unkn2；exe 按整数读，旧代码按 float）。
	public int PriorityBias;

	// 加到着色器 depthBias 上的深度偏移（旧名 LayerNegative）。
	public float DepthBias;
    [RszVersion('<', EfxVersion.RE8)]
	public uint LayerPositive;
	[RszVersionExact(EfxVersion.RE7)]
	public uint toggle_re7;
	// 无引擎名（旧名 re8_unkn000）：位 2 = 软粒子（官方 99.8% 打开），位 13 = 启用 StencilTarget，位 16 = 分配逐粒子光照缓冲。
    [RszVersion(EfxVersion.RE8)]
	public uint Flags;

	// 阴影颜色，只用 RGB（旧名 color0）。
	public via.Color ShadowColor;
	// 体积散射块（旧名 unkn6..12）：ScatteringMode = 0 时引擎忽略这些值、改用默认 0.15 / 0.15 / 0.2；RE4 从不打开。
	public float RayleighCoeff;
	public float MieCoeff;
	public float MieDropoff;
	public float AlphaScale;
	public float DensityScale;
	// 百分比，运行时 ×0.01。
	public float DensityMax;
	public float DensityMin;
	// 只用位 0，写入发射器标志位 13（含义未追）。
	public uint unkn13;
	// 无引擎名（旧名 unkn14）：0 = 不用散射参数。
	public uint ScatteringMode;
	// 受光块（旧名 unkn15、unkn16、unkn17）。BackfaceLightRatio 运行时 ×8。
	public float LightShadowRatio;
	public float BackfaceLightRatio;
	[RszVersion(EfxVersion.RE2)]
	public float MieShadowRatio;

	// 光照采样偏移（旧名 re4_unkn1..3）。
	[RszVersion(EfxVersion.RE4, EndAt = nameof(LightingOffsetZ))]
	public float LightingOffsetX;
	public float LightingOffsetY;
	public float LightingOffsetZ;

	// 旧名 unkn19；exe 按 float 读（旧代码按 uint）。
    [RszVersion('<', EfxVersion.DD2)]
	public float LightingVelocityOffset;

	// 进出阴影的过渡时间，毫秒（旧名 sb_unkn2..5）。
    [RszVersion(EfxVersion.RERT, EndAt = nameof(TimeToNoShadow))]
	public float InShadowDelay;
	public float TimeToFullShadow;
	public float OutShadowDelay;
	public float TimeToNoShadow;

	// 引擎按 "== 1" 判断（值 2 等于关闭），含义未追。
    [RszVersionExact(EfxVersion.RE4)]
	public uint toggle_re4;
    [RszVersion(EfxVersion.DD2)]
	public float dd2_unkn1;
	// 着色器 detonemapRate：粒子颜色绕过色调映射的比例（旧名 ColorBrightness，**不是亮度**）。
    [RszVersion(EfxVersion.RE2)]
	public float DetonemapRatio;

    [RszVersion(EfxVersion.DD2)] public uint toggle_dd2;
    [RszVersion(EfxVersion.RE3, EndAt = nameof(StochasticShadingRate))]
	public float VolumeDepth;

	// 旧名 sb_unkn9..12；AlphaCoverage、SSRAlphaThreshold 与着色器常量名对得上。
    [RszVersion(">", EfxVersion.RERT, EndAt = nameof(SSRAlphaThreshold))]
	public float VolumetricIntensityBlend;
	public float DeinterlaceDistance;
	public float AlphaCoverage;
	public float SSRAlphaThreshold;

    [RszVersion(EfxVersion.DD2)]
	public float dd2_unkn2;
	// 只用低字节；Flags 位 13 打开时生效（旧名 unkn22）。
	public uint StencilTarget;

    [RszVersionExact(EfxVersion.RE8, EndAt = nameof(re8_unkn5))]
	public float re8_unkn3;
	public float re8_unkn4;
	public float re8_unkn5;

	// 含义未追。
    [RszVersion(EfxVersion.RE8)]
	public uint unkn24;

	// 可变速率着色（旧名 unkn25..29）：只用低 3 位，255 = 默认。
    [RszVersion(nameof(Version), ">=", EfxVersion.RERT, "&&", nameof(Version), "<", EfxVersion.MHWilds, EndAt = nameof(StochasticShadingRate))]
	public uint ParticleShadingRate;
	public uint LightingShadingRate;
	public uint ShadowShadingRate;
	public uint ZPrepassShadingRate;
	public uint StochasticShadingRate;

	[RszVersion(EfxVersion.MHWilds, EndAt = nameof(mhws_unkn_short))] public float mhws_unkn1;
	public float mhws_unkn2;
	public short mhws_unkn_short;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.ShaderSettingsExpression, EfxVersion.DMC5, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.DD2)]
public partial class EFXAttributeShaderSettingsExpression : ReeLib.Efx.EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

    public EFXAttributeShaderSettingsExpression() : base(EfxAttributeType.ShaderSettingsExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(12) { BitNameDict = new () {
		// [1] = nameof(volume), // re7/rt/dmc5/re8 - volume/volumeblend (alpha?); color - dd2
		// [2] = nameof(alpha), // alpha - dd2
		// [3] = nameof(translationZ), LightShadowRatio
		// [4] = nameof(rotationX), BackFaceLightRatio
		// [5] = nameof(rotationY),
		// [6] = nameof(rotationZ), 6,7,8 - dd2 vectorXYZ
		// [7] = nameof(scaleX),
		// [8] = nameof(scaleY),
		// [9] = nameof(scaleZ),
	} };
    public ExpressionAssignType ukn1;
    [RszVersion(EfxVersion.RE8, EndAt = nameof(ukn7))]
    public ExpressionAssignType ukn2;
    public ExpressionAssignType ukn3;
    public ExpressionAssignType ukn4;
    public ExpressionAssignType ukn5;
    public ExpressionAssignType ukn6;
    public ExpressionAssignType ukn7;
    [RszVersion(EfxVersion.DD2, EndAt = nameof(ukn12))]
    public ExpressionAssignType ukn8;
    public ExpressionAssignType ukn9;
    public ExpressionAssignType ukn10;
    public ExpressionAssignType ukn11;
    public ExpressionAssignType ukn12;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PlayEfx, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributePlayEfx : EFXAttribute
{
	public EFXAttributePlayEfx() : base(EfxAttributeType.PlayEfx) { }

	// [RszVersion(EfxVersion.MHWilds)] public uint mhws_unkn;
	[RszInlineWString(ByteSize = true)] public string? efxPath;

    protected override bool DoRead(FileHandler handler)
    {
        if (Version != EfxVersion.MHWilds) return DefaultRead(handler);
        int length = handler.Read<int>();
        if (length < 1 || length > 2048)
            throw new InvalidDataException("Invalid MHWilds PlayEfx UTF-16 length.");
        // Wilds stores bytes, including the UTF-16 terminator. Accept the old
        // editor's character count on read so existing edited checkpoints can
        // be recovered without discarding their parameter changes.
        efxPath = handler.ReadWString(-1, Math.Max(1, (length + 1) / 2), false);
        int characters = efxPath.Length + 1;
        if (length != characters * 2 && length != characters)
            throw new InvalidDataException("MHWilds PlayEfx length does not match its UTF-16 payload.");
        return true;
    }

    protected override bool DoWrite(FileHandler handler)
    {
        if (Version != EfxVersion.MHWilds) return DefaultWrite(handler);
        efxPath ??= string.Empty;
        handler.Write<int>(checked((efxPath.Length + 1) * 2));
        handler.WriteWString(efxPath);
        return true;
    }

    public override string ToString() => $"Efx path: {efxPath}";
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PlayEmitter, EfxVersion.DMC5, EfxVersion.RE4)]
public sealed partial class EFXAttributePlayEmitter : EFXAttribute, IDisposable
{
	public EFXAttributePlayEmitter() : base(EfxAttributeType.PlayEmitter) { }

	public uint efxrSize;
	[RszIgnore] public EfxFile? efxrData;

    protected override bool DoRead(FileHandler handler)
    {
		handler.Read(ref efxrSize);
		var end = handler.Position + efxrSize;
		efxrData = new EfxFile(handler.WithOffset(handler.Position));
		efxrData.Embedded = true;
		efxrData.Read();
		handler.Seek(end);
		return true;
    }

    protected override bool DoWrite(FileHandler handler)
    {
		var sizeMarker = handler.Position;
		handler.Skip(sizeof(uint));
		var start = handler.Position;
		efxrData?.WriteTo(handler.WithOffset(start), false);
		handler.Write(sizeMarker, (uint)(handler.Position - start));
		return true;
    }

    public override EFXAttribute Clone()
    {
        return base.Clone();
    }

    public void Dispose()
    {
		efxrData?.FileHandler.Dispose();
    }
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.RenderTarget, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeRenderTarget : EFXAttribute
{
	public EFXAttributeRenderTarget() : base(EfxAttributeType.RenderTarget) { }

	public uint unkn_toggle;
	[RszInlineWString(ByteSize = true)] public string? rtexPath;

    public override string ToString() => $"RenderTarget: {rtexPath}";
}
