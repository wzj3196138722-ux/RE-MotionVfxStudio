using System.Numerics;
using ReeLib.Efx.Enums;
using ReeLib.Efx.Structs.Common;
using ReeLib.InternalAttributes;

namespace ReeLib.Efx.Structs.Main;

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeNoDraw, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributeTypeNoDraw : EFXAttribute
{
	public EFXAttributeTypeNoDraw() : base(EfxAttributeType.TypeNoDraw) { }

	// 不绘制；出生时写入粒子的旋转、尺寸、颜色，供子特效 / PtBehavior 使用（re4.exe 3d6bf40 / 3d6c0c0）。
	// Flags 位 0 = 出生时颜色在 Color 与 ColorRange 间随机。
	public uint Flags;
	public via.Color Color;
	public via.Color ColorRange;
	public RotationOrder RotationOrder;

	// 文件里是 6 个交错的 {s, r} 范围（旧代码误读为 Rotation / RotationRandom / Size / SizeRandom 四个 Vector3）。
	// 旋转为弧度，出生时 s ± r 抽样。
	public via.Range RotationX;
	public via.Range RotationY;
	public via.Range RotationZ;
	public via.Range SizeX;
	public via.Range SizeY;
	public via.Range SizeZ;
	[RszVersion(EfxVersion.MHWilds, EndAt = nameof(unkn15))]
	public float unkn14;
	public float unkn15;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.TypeNoDrawExpression, EfxVersion.RE3, EfxVersion.RE4)]
public partial class EFXAttributeTypeNoDrawExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeTypeNoDrawExpression() : base(EfxAttributeType.TypeNoDrawExpression) { }

	// TODO bitset versioning
	// re3: 2 = alpha
	// re3rt: 3 = alpha
	// [RszClassInstance] public BitSet expressionBits = new BitSet(16) { BitNameDict = new () {
	// 	[1] = nameof(color),
	// 	[2] = nameof(colorRand),
	// 	[3] = nameof(alpha),
	// 	[4] = nameof(alphaRand),
	// } };
	[RszClassInstance] public BitSet expressionBits = new BitSet(16) { BitNameDict = new () {
		[1] = nameof(color),
		[2] = nameof(alpha),
	} };
    public ExpressionAssignType color;
	[RszVersion('>', EfxVersion.RE3)]
    public ExpressionAssignType colorRand;
    public ExpressionAssignType alpha;
	[RszVersion('>', EfxVersion.RE3)]
    public ExpressionAssignType alphaRand;
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
	[RszVersion(EfxVersion.MHWilds, EndAt = nameof(unkn18))]
    public ExpressionAssignType unkn17;
    public ExpressionAssignType unkn18;

	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;

}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.UnitCulling, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RERT)]
public partial class EFXAttributeUnitCulling : EFXAttribute
{
	public EFXAttributeUnitCulling() : base(EfxAttributeType.UnitCulling) { }

	public uint Flags;
	public Vector3 Center;
	public Vector3 Size;
	public Vector3 Rotation;
    [RszVersion(EfxVersion.RE8)]
	public float DrawDistance;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.UnitCullingExpression, EfxVersion.DD2)]
public partial class EFXAttributeUnitCullingExpression : EFXAttribute, IExpressionAttribute
{
    public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributeUnitCullingExpression() : base(EfxAttributeType.UnitCullingExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(11) { BitNameDict = new() {
        [7] = nameof(cullingRadius),
        [11] = nameof(cullingDistance),
    } };
    public ExpressionAssignType unkn1;
    public ExpressionAssignType unkn2;
    public ExpressionAssignType unkn3;
    public ExpressionAssignType unkn4;
    public ExpressionAssignType unkn5;
    public ExpressionAssignType unkn6;
    public ExpressionAssignType cullingRadius;
    public ExpressionAssignType unkn8;
    public ExpressionAssignType unkn9;
    public ExpressionAssignType unkn10;
    public ExpressionAssignType cullingDistance;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}
