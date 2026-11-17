using ReeLib.Efx.Structs.Common;
using ReeLib.Efx.Enums;
using ReeLib.InternalAttributes;
using System.Numerics;

namespace ReeLib.Efx.Structs.Transforms;

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FadeByAngle, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeFadeByAngle : EFXAttribute
{
	public EFXAttributeFadeByAngle() : base(EfxAttributeType.FadeByAngle) { }

	// 名字来自 re4.exe（加载器读 UniqueID + 0x1c 字节，解析器 3d3ecb0）。CPU 只把值抄进发射器常量缓冲
	// （CPU 类型 = primitivevfx 的 Polygon3DConstant，偏移逐项相同；Gpu* = GpuParticleVS），
	// 公式在顶点着色器里，两族代码相同：在 cos 空间里线性过渡，不是按角度线性。
	// Flags 只读低 3 位：bit0 = 双向锥（取 |cos|），bit1 = 反转（锥内 AlphaRate、锥外 1），
	// bit2 = 视线从粒子/段中心算（默认从发射器位置算）。Mesh / Gpu* 的打包没追到，按同样位序推断。
	public uint Flags;
	// 内锥半角，弧度。默认锥内完全可见（与 MHWI 文档的方向相反）。
	public float Cone;
	// 过渡宽度，弧度；外沿 = Cone + Spread（夹到 π）。
	public float Spread;
	// 被淡出一侧的 alpha 系数（0 = 消失，1 = 不起作用）。
	public float AlphaRate;
	// 发射器局部空间的单位方向向量（不是旋转），发射器创建时按发射器矩阵转到世界并重新归一。旧名 ConeDirection。
	public Vector3 ConeDir;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FadeByAngleExpression, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeFadeByAngleExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeFadeByAngleExpression() : base(EfxAttributeType.FadeByAngleExpression) { }

	// re4.exe 槽位（A 类：引擎没有槽位名，按 applier 写回的宿主字段命名，bit i = 槽位 i）。运算 = op(文件原值, 表达式)，每帧重算；Cone/Spread 按角度。
	[RszClassInstance] public BitSet expressionBits = new BitSet(2) { BitNames = [ nameof(Cone), nameof(Spread) ] };
	// 第一个写 Cone（块 +0x2c），第二个写 Spread（块 +0x30），见 re4.exe 3d4e430。旧名 minAngle / maxAngle。
    public ExpressionAssignType Cone;
    public ExpressionAssignType Spread;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}
[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FadeByDepth, EfxVersion.DMC5, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeFadeByDepth : EFXAttribute
{
	public EFXAttributeFadeByDepth() : base(EfxAttributeType.FadeByDepth) { }

	// 名字来自 re4.exe（加载器读 UniqueID + 0x10 字节，块 = 着色器 fadeDepth.xyzw）。四个独立 float，不是区间对。
	// 单位是米、视空间深度（裁剪 w），不是到相机的直线距离（primitivevfx 与 vfx/*draw 着色器相同）。系数：d < NearStart 或 d > FarEnd 时为 0，
	// NearStart→NearEnd 线性升到 1，FarStart→FarEnd 线性降到 0，中间为 1。Start == End 为硬切；近处 0/0 = 不做近处淡入。
	// 顶点的总 alpha 系数小于 2^-23 时直接剔除。
	public float NearStart;
	public float NearEnd;
	public float FarStart;
	public float FarEnd;

    public override string ToString() => $"{NearStart} - {NearEnd} - {FarStart} - {FarEnd}";
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FadeByDepthExpression, EfxVersion.DMC5, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeFadeByDepthExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeFadeByDepthExpression() : base(EfxAttributeType.FadeByDepthExpression) { }

	// re4.exe 槽位（A 类：引擎没有槽位名，按 applier 写回的宿主字段命名，bit i = 槽位 i）。运算 = op(文件原值, 表达式)，每帧重算；
	[RszClassInstance] public BitSet expressionBits = new BitSet(4) { BitNames = [ nameof(NearStart), nameof(NearEnd), nameof(FarStart), nameof(FarEnd) ] };
	public ExpressionAssignType NearStart;
	public ExpressionAssignType NearEnd;
	public ExpressionAssignType FarStart;
	public ExpressionAssignType FarEnd;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FadeByEmitterAngle, EfxVersion.DMC5, EfxVersion.RE4, EfxVersion.MHWilds)]
public partial class EFXAttributeFadeByEmitterAngle : EFXAttribute
{
	public EFXAttributeFadeByEmitterAngle() : base(EfxAttributeType.FadeByEmitterAngle) { }

	public uint Flags;
	public float Cone;
	public float Spread;
	public float AlphaRate;
	public float FadeInStart;
	public float FadeInEnd;
	[RszVersion(EfxVersion.DD2)]
	public uint FadeBlend;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FadeByOcclusion, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributeFadeByOcclusion : EFXAttribute
{
	public EFXAttributeFadeByOcclusion() : base(EfxAttributeType.FadeByOcclusion) { }

	// 名字取自 MonsterHunterWilds.exe 文本解析器（vtable e0ff390）；MinSize 槽另有别名 MinAlpha。
	public float CullingRadius;
	public Vector2 SphereOffset;
	public float MinSize;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.FadeByRootCulling, EfxVersion.DD2, EfxVersion.MHWilds)]
public partial class EFXAttributeFadeByRootCulling : EFXAttribute
{
	public EFXAttributeFadeByRootCulling() : base(EfxAttributeType.FadeByRootCulling) { }

	public EffectBoundsType ShapeType;
	public Vector3 Center;
	public float InnerRadius;
	public float OuterRadius;
    public Vector3 InnerSize;
    public Vector3 OuterSize;
    public Vector3 LocalRotation;
    public RotationOrder RotationOrder;
}
