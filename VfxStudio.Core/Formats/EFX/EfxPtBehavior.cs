using System.Numerics;
using ReeLib.Efx.Enums;
using ReeLib.Efx.Structs.Common;
using ReeLib.InternalAttributes;

namespace ReeLib.Efx.Structs.Pt;

// Named after the engine type of the script field the parameter sets: every parameter in the RE4 corpus was matched to
// its field in the type database inside re4.exe (class = the PtBehavior's script name, field = the parameter name), and
// each id maps to exactly one field type. 18 and 22 are both enums: 18 the script's own (DecalDirectionEnum,
// Priority, ...), 22 the engine's via.effect.lensflare.RotationOrder / AxisType.
public enum PtBehaviorPropType
{
    PropInt = 3,            // System.Int32
    PropUint = 4,           // System.UInt32
    PropFloat = 9,          // System.Single
    PropVec2 = 10,          // via.vec2 (x, y), not a random range: EffectDecal UVOffset, UVScale, Alpha0
    PropVec3 = 11,          // via.vec3
    PropBool = 14,          // System.Boolean, stored as a 4-byte 0 / 1
    PropColor = 15,         // via.Color
    PropString = 16,        // System.String
    PropPrefab = 17,        // via.Prefab (path)
    PropEnum = 18,          // script enum
    PropRange = 19,         // via.Range (s, r): EffectDecal ScaleXY, OBBScalarAdd, PyramidAngle
    PropRangeI = 20,        // via.RangeI (s, r): ShakeCamera Period
    PropTexture = 21,       // via.render.TextureResourceHolder (path): EffectDecal FlowTexture
    PropNativeEnum = 22,    // engine enum
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariableDataBase : BaseModel
{
	[RszIgnore] public EfxVersion Version;

	public PtBehaviorVariableDataBase(EfxVersion version) { Version = version; }

	public int unkn;
	public int size;
	[RszVersion(EfxVersion.RE4, EndAt = nameof(re4_unkn1))]
	public short re4_unkn0;
	public short re4_unkn1;

	protected bool EnsureMinimumSize(int minSize)
	{
		if (size < minSize) size = minSize;
		return true;
	}
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariableDataColor : PtBehaviorVariableDataBase
{
	public PtBehaviorVariableDataColor(EfxVersion version) : base(version) { }

	public via.Color color;
	[RszFixedSizeArray(nameof(size), - 4)] public byte[]? restData;

    protected override bool DoRead(FileHandler handler) => base.DoRead(handler) && DefaultRead(handler);
    protected override bool DoWrite(FileHandler handler) => EnsureMinimumSize(4) && base.DoWrite(handler) && DefaultWrite(handler);
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariableInteger : PtBehaviorVariableDataBase
{
	public PtBehaviorVariableInteger(EfxVersion version) : base(version) { }

	public int value;
	[RszFixedSizeArray(nameof(size), - 4)] public byte[]? restData;

    protected override bool DoRead(FileHandler handler) => base.DoRead(handler) && DefaultRead(handler);
    protected override bool DoWrite(FileHandler handler) => EnsureMinimumSize(4) && base.DoWrite(handler) && DefaultWrite(handler);
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariableUInt : PtBehaviorVariableDataBase
{
	public PtBehaviorVariableUInt(EfxVersion version) : base(version) { }

	public uint value;
	[RszFixedSizeArray(nameof(size), - 4)] public byte[]? restData;

    protected override bool DoRead(FileHandler handler) => base.DoRead(handler) && DefaultRead(handler);
    protected override bool DoWrite(FileHandler handler) => EnsureMinimumSize(4) && base.DoWrite(handler) && DefaultWrite(handler);
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariableVec2 : PtBehaviorVariableDataBase
{
	public PtBehaviorVariableVec2(EfxVersion version) : base(version) { }

	public Vector2 vec;
	[RszFixedSizeArray(nameof(size), - 8)] public byte[]? restData;

    protected override bool DoRead(FileHandler handler) => base.DoRead(handler) && DefaultRead(handler);
    protected override bool DoWrite(FileHandler handler) => EnsureMinimumSize(8) && base.DoWrite(handler) && DefaultWrite(handler);
}

/// <summary>A bool parameter: the payload is a 4-byte 0 / 1.</summary>
public partial class PtBehaviorVariableBool : PtBehaviorVariableInteger
{
	public PtBehaviorVariableBool(EfxVersion version) : base(version) { }
}

public partial class PtBehaviorVariableEnum : PtBehaviorVariableInteger
{
	public PtBehaviorVariableEnum(EfxVersion version) : base(version) { }
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariableFloat : PtBehaviorVariableDataBase
{
	public PtBehaviorVariableFloat(EfxVersion version) : base(version) { }

	public float value;
	[RszFixedSizeArray(nameof(size), - 4)] public byte[]? restData;

    protected override bool DoRead(FileHandler handler) => base.DoRead(handler) && DefaultRead(handler);
    protected override bool DoWrite(FileHandler handler) => EnsureMinimumSize(4) && base.DoWrite(handler) && DefaultWrite(handler);
}
[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariableRange : PtBehaviorVariableDataBase
{
	public PtBehaviorVariableRange(EfxVersion version) : base(version) { }

	public via.Range range;
	[RszFixedSizeArray(nameof(size), - 8)] public byte[]? restData;

    protected override bool DoRead(FileHandler handler) => base.DoRead(handler) && DefaultRead(handler);
    protected override bool DoWrite(FileHandler handler) => EnsureMinimumSize(8) && base.DoWrite(handler) && DefaultWrite(handler);
}
[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariableRangeI : PtBehaviorVariableDataBase
{
	public PtBehaviorVariableRangeI(EfxVersion version) : base(version) { }

	public via.RangeI range;
	[RszFixedSizeArray(nameof(size), - 8)] public byte[]? restData;

    protected override bool DoRead(FileHandler handler) => base.DoRead(handler) && DefaultRead(handler);
    protected override bool DoWrite(FileHandler handler) => EnsureMinimumSize(8) && base.DoWrite(handler) && DefaultWrite(handler);
}
[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariableVec3 : PtBehaviorVariableDataBase
{
	public PtBehaviorVariableVec3(EfxVersion version) : base(version) { }

	public Vector3 vec;
	[RszFixedSizeArray(nameof(size), - 12)] public byte[]? restData;

    protected override bool DoRead(FileHandler handler) => base.DoRead(handler) && DefaultRead(handler);
    protected override bool DoWrite(FileHandler handler) => EnsureMinimumSize(12) && base.DoWrite(handler) && DefaultWrite(handler);
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariableDataWString : PtBehaviorVariableDataBase
{
	public PtBehaviorVariableDataWString(EfxVersion version) : base(version) { }

	[RszInlineWString(nameof(size))] public string? str;

    protected override bool DoRead(FileHandler handler) => base.DoRead(handler) && DefaultRead(handler);
    protected override bool DoWrite(FileHandler handler) => EnsureMinimumSize(str?.Length ?? 2) && base.DoWrite(handler) && DefaultWrite(handler);
}

public partial class PtBehaviorVariableDataPrefabPath : PtBehaviorVariableDataWString
{
	public PtBehaviorVariableDataPrefabPath(EfxVersion version) : base(version) { }
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariableRaw : PtBehaviorVariableDataBase
{
	public PtBehaviorVariableRaw(EfxVersion version) : base(version) { }

	[RszFixedSizeArray(nameof(size))] public byte[]? data;

    protected override bool DoRead(FileHandler handler) => base.DoRead(handler) && DefaultRead(handler);
    protected override bool DoWrite(FileHandler handler) => EnsureMinimumSize(data?.Length ?? 0) && base.DoWrite(handler) && DefaultWrite(handler);
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class PtBehaviorVariable : BaseModel
{
	[RszIgnore] public EfxVersion Version;

	public PtBehaviorVariable(EfxVersion version) { Version = version; }

	public int varSize;
	public PtBehaviorPropType dataType;
	// public uint unkn2;
	// public int dataSize;
	// [RszVersion(EfxVersion.RE4, EndAt = nameof(re4_unkn1))]
	// public short re4_unkn0;
	// public short re4_unkn1;

	// TODO - possibly replaces re4_unkn0, re4_unkn1
	// if(varID == 0x0F){
	//     spos[i] = FTell();
	//     i++;
	//     via.Color small_color;
	//     char var[varSize-16];// <name="Variable", bgcolor=0xA3BECC, open=suppress>;
	// }else{
	//     char  variable[varSize-12];// <name="Vairable", bgcolor=0xD6D6D6, open=suppress>;
	// }
	[RszClassInstance, RszConstructorParams(nameof(Version)), RszSwitch(
		nameof(dataType), "==", PtBehaviorPropType.PropColor, typeof(PtBehaviorVariableDataColor),
		nameof(dataType), "==", PtBehaviorPropType.PropPrefab, typeof(PtBehaviorVariableDataPrefabPath),
		nameof(dataType), "==", PtBehaviorPropType.PropInt, typeof(PtBehaviorVariableInteger),
		nameof(dataType), "==", PtBehaviorPropType.PropBool, typeof(PtBehaviorVariableBool),
		nameof(dataType), "==", PtBehaviorPropType.PropEnum, typeof(PtBehaviorVariableEnum),
		nameof(dataType), "==", PtBehaviorPropType.PropNativeEnum, typeof(PtBehaviorVariableEnum),
		nameof(dataType), "==", PtBehaviorPropType.PropFloat, typeof(PtBehaviorVariableFloat),
		nameof(dataType), "==", PtBehaviorPropType.PropVec2, typeof(PtBehaviorVariableVec2),
		nameof(dataType), "==", PtBehaviorPropType.PropVec3, typeof(PtBehaviorVariableVec3),
		nameof(dataType), "==", PtBehaviorPropType.PropRange, typeof(PtBehaviorVariableRange),
		nameof(dataType), "==", PtBehaviorPropType.PropRangeI, typeof(PtBehaviorVariableRangeI),
		nameof(dataType), "==", PtBehaviorPropType.PropString, typeof(PtBehaviorVariableDataWString),
		nameof(dataType), "==", PtBehaviorPropType.PropTexture, typeof(PtBehaviorVariableDataWString),
		nameof(dataType), "==", PtBehaviorPropType.PropUint, typeof(PtBehaviorVariableUInt),
		typeof(PtBehaviorVariableRaw)
	)]
	public PtBehaviorVariableDataBase? variable;

	[RszVersion(EfxVersion.MHWilds)] public uint varHash;
	[RszInlineString(-1)] public string? behaviorProperty;

	protected override bool DoRead(FileHandler handler)
	{
		DefaultRead(handler);
		return true;
	}

	protected override bool DoWrite(FileHandler handler)
	{
		// The stored lengths follow the content, so string values can change (RE4 corpus, every parameter type):
		// a string takes (length + 1) * 2 bytes with its NUL, and varSize counts what follows it
		// (dataType 4 + data header 12 + payload + name + NUL) = name length + 17 + size.
		if (Version == EfxVersion.RE4 && variable != null)
		{
			if (variable is PtBehaviorVariableDataWString text) text.size = ((text.str?.Length ?? 0) + 1) * 2;
			varSize = (behaviorProperty?.Length ?? 0) + 17 + variable.size;
		}
		DefaultWrite(handler);
		return true;
	}

	/// <summary>The value as <see cref="Create"/> reads it.</summary>
	public string ValueText()
	{
		var inv = System.Globalization.CultureInfo.InvariantCulture;
		return variable switch
		{
			PtBehaviorVariableFloat f => f.value.ToString("R", inv),
			PtBehaviorVariableVec2 v2 => string.Join(",", v2.vec.X.ToString("R", inv), v2.vec.Y.ToString("R", inv)),
			PtBehaviorVariableVec3 v3 => string.Join(",", v3.vec.X.ToString("R", inv), v3.vec.Y.ToString("R", inv), v3.vec.Z.ToString("R", inv)),
			PtBehaviorVariableRange r => string.Join(",", r.range.s.ToString("R", inv), r.range.r.ToString("R", inv)),
			PtBehaviorVariableRangeI ri => string.Join(",", ri.range.s.ToString(inv), ri.range.r.ToString(inv)),
			PtBehaviorVariableInteger i => i.value.ToString(inv),
			PtBehaviorVariableUInt u => u.value.ToString(inv),
			PtBehaviorVariableDataColor c => $"{c.color.R},{c.color.G},{c.color.B},{c.color.A}",
			PtBehaviorVariableDataWString s => s.str ?? "",
			PtBehaviorVariableRaw raw => Convert.ToHexString(raw.data ?? []),
			_ => "",
		};
	}

	/// <summary>A new RE4 parameter from text: Float, Int, Uint, Bool (0 / 1) and the two enums take one number; Vec2,
	/// Vec3, Range, RangeI and Color take comma-separated numbers (Range: s,r; Color: R,G,B,A); String, Prefab and
	/// Texture take the text; other types take hex bytes. <paramref name="index"/> is the parameter's position in its
	/// PtBehavior.</summary>
	public static PtBehaviorVariable Create(EfxVersion version, int index, string name, PtBehaviorPropType type, string value)
	{
		var inv = System.Globalization.CultureInfo.InvariantCulture;
		float[] Floats(int n)
		{
			var parts = value.Split(',').Select(p => float.Parse(p.Trim(), inv)).ToArray();
			if (parts.Length != n) throw new ArgumentException($"{name}: {type} needs {n} comma-separated numbers.");
			return parts;
		}
		PtBehaviorVariableDataBase data = type switch
		{
			PtBehaviorPropType.PropFloat => new PtBehaviorVariableFloat(version) { value = float.Parse(value, inv), size = 4 },
			PtBehaviorPropType.PropVec2 => Floats(2) is var v2 ? new PtBehaviorVariableVec2(version) { vec = new(v2[0], v2[1]), size = 8 } : null!,
			PtBehaviorPropType.PropVec3 => Floats(3) is var v3 ? new PtBehaviorVariableVec3(version) { vec = new(v3[0], v3[1], v3[2]), size = 12 } : null!,
			PtBehaviorPropType.PropRange => Floats(2) is var r ? new PtBehaviorVariableRange(version) { range = new via.Range { s = r[0], r = r[1] }, size = 8 } : null!,
			PtBehaviorPropType.PropRangeI => value.Split(',').Select(p => int.Parse(p.Trim(), inv)).ToArray() is { Length: 2 } ri
				? new PtBehaviorVariableRangeI(version) { range = new via.RangeI { s = ri[0], r = ri[1] }, size = 8 }
				: throw new ArgumentException($"{name}: RangeI needs s,r."),
			PtBehaviorPropType.PropInt => new PtBehaviorVariableInteger(version) { value = int.Parse(value, inv), size = 4 },
			PtBehaviorPropType.PropBool => new PtBehaviorVariableBool(version) { value = int.Parse(value, inv), size = 4 },
			PtBehaviorPropType.PropEnum or PtBehaviorPropType.PropNativeEnum =>
				new PtBehaviorVariableEnum(version) { value = int.Parse(value, inv), size = 4 },
			PtBehaviorPropType.PropUint => new PtBehaviorVariableUInt(version) { value = uint.Parse(value, inv), size = 4 },
			PtBehaviorPropType.PropColor => value.Split(',').Select(p => byte.Parse(p.Trim(), inv)).ToArray() is { Length: 4 } c
				? new PtBehaviorVariableDataColor(version) { color = new via.Color { R = c[0], G = c[1], B = c[2], A = c[3] }, size = 4 }
				: throw new ArgumentException($"{name}: Color needs R,G,B,A."),
			PtBehaviorPropType.PropString or PtBehaviorPropType.PropTexture => new PtBehaviorVariableDataWString(version) { str = value },
			PtBehaviorPropType.PropPrefab => new PtBehaviorVariableDataPrefabPath(version) { str = value },
			_ => Convert.FromHexString(value) is var bytes ? new PtBehaviorVariableRaw(version) { data = bytes, size = bytes.Length } : null!,
		};
		if (data is PtBehaviorVariableDataWString w) w.size = ((w.str?.Length ?? 0) + 1) * 2;
		// Header as in every RE4 parameter: unkn 2 for Range and RangeI, else 1; then -1 and the parameter index.
		data.unkn = type is PtBehaviorPropType.PropRange or PtBehaviorPropType.PropRangeI ? 2 : 1;
		data.re4_unkn0 = -1;
		data.re4_unkn1 = (short)index;
		return new PtBehaviorVariable(version)
		{
			dataType = type, variable = data, behaviorProperty = name,
			varSize = name.Length + 17 + data.size,
		};
	}
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtBehavior, EfxVersion.RE4)]
public partial class EFXAttributePtBehavior : EFXAttribute
{
    public EFXAttributePtBehavior() : base(EfxAttributeType.PtBehavior) { }

    public uint unkn1;
    [RszStringLengthField(nameof(behaviorString))] public int behaviorStringLength;
	[RszVersion('<', EfxVersion.MHWilds)] [RszArraySizeField(nameof(properties))] public int varCount;
    [RszInlineString(nameof(behaviorStringLength))]
	public string? behaviorString;

	[RszVersion(EfxVersion.MHWilds)] [RszArraySizeField(nameof(properties))] public int varCount_mhws;
	[RszClassInstance, RszList(nameof(Version), '<', EfxVersion.MHWilds, '?', nameof(varCount), ':', nameof(varCount_mhws)), RszConstructorParams(nameof(Version))]
	public List<PtBehaviorVariable> properties = new();

    public override string ToString() => $"{behaviorString}";
}


[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtColliderAction, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributePtColliderAction : EFXAttribute
{
    public EFXAttributePtColliderAction() : base(EfxAttributeType.PtColliderAction) { }

    // re4.exe：逐粒子步骤 3d62430，解析器只有 Radius 一个名字。与 PtCollision 独立的另一次碰撞检测：每次碰撞都在碰撞点
    // 启动 linkedAction 指向的 Action（+Y 朝法线），没有计数。
    // 无引擎名（旧名 dataFlags）：bit0 有碰撞层字符串，bit1 有掩码字符串，bit2 碰撞时杀死粒子，bit3 用 Radius 做球投射。
    public uint Flags;

    // 引擎名 Radius，lerp(min, max, u)；只在 Flags bit3 时用。旧名 unkn1 / unkn2。
    public MinMax Radius;
    // EFX 文件 Action 列表的下标；0xFFFFFFFF = 不触发。
    public uint linkedAction;

	// found in RE2RT（RE4 的加载器不读这一段，语料里也没有 bit10）
	[RszConditional("(Flags & (1 << 10)) != 0", EndAt = nameof(ukn_flag2_11))]
	public uint ukn_flag2_0; // likely some sort of shape type + data
    public float ukn_flag2_2;
    public float ukn_flag2_3;
    public float ukn_flag2_4;
    public float ukn_flag2_5;
	public uint ukn_flag2_6;
    public float ukn_flag2_7;
    public float ukn_flag2_8;
    public float ukn_flag2_9;
    public float ukn_flag2_10;
    public float ukn_flag2_11;

	[RszVersion(EfxVersion.DD2, EndAt = nameof(dd2_unkn2))]
	public uint dd2_unkn0;
	public uint dd2_unkn1;
	[RszVersionExact(EfxVersion.DD2)]
	public uint dd2_unkn2;

	// 引擎先读 bit0 的字符串（碰撞层），再读 bit1 的（掩码）；旧代码顺序反了，两个都有时会互换。
	// 只在 RE4 上核对过顺序。旧名 unknString_flag1 / unknString_flag2。
	[RszConditional("(Flags & (1 << 0)) != 0"), RszInlineWString(ByteSize = true)]
	public string? LayerName;

	[RszConditional("(Flags & (1 << 1)) != 0"), RszInlineWString(ByteSize = true)]
	public string? MaskName;

	[RszVersionExact(EfxVersion.MHWilds)]
	public uint wilds_unkn0;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtCollision, EfxVersion.RE7, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributePtCollision : EFXAttribute
{
    public EFXAttributePtCollision() : base(EfxAttributeType.PtCollision) { }

    // re4.exe：vtable 7a0f980，加载器读 UniqueID + 0x44 字节，解析器 3d63320，逐粒子步骤 3d64580。名字来自解析器。
    // 不是深度缓冲碰撞：CPU 每帧从上一位置到当前位置投射线/球（异步，晚一帧），对物理世界按碰撞层/掩码过滤。
    // Gpu* 类型上不起作用。
    // 无引擎名（按作用起名，旧名 stringBitFlag + unkn0_1..3）：bit0 有碰撞层字符串，bit1 有掩码字符串，bit2 用 Radius 做球投射
    // （否则射线），bit3 最后一次碰撞时冻结动画器，bit4 投影模式，bit5-6 速度重定向，bit7-8 旋转重定向，bit9-11 欧拉顺序，
    // bit12 对齐法线，bit13 冻结在碰撞点。
    public uint Flags;

    // 球投射半径（米），lerp(min, max, u)；只在 Flags bit2 时用。
    public MinMax Radius;
    // 碰撞次数 [min, max)，包含结束那一次：2 = 弹一次。
    public MinMaxExclusiveI BounceNum;
    // 反射后速度倍率，lerp(min, max, u)。反射按真实碰撞法线，速度大小保持，再乘倍率。
    public MinMax BounceRate;
    // ×BounceRate，作用在世界 Y 上（不是法线方向）。
    public float VerticalBounce;
    // ×BounceRate，作用在世界 X/Z 上。
    public float HorizontalBounce;
    // 碰撞次数用完时：None 穿过、Kill 杀死、Freeze 停在碰撞点。
    public Finish FinishType;
	[RszVersion('>', EfxVersion.RE7, EndAt = nameof(DelayFrameCollision))]
    // 以下三个只在投影模式（Flags bit4，出生时从发射器平面沿局部 -Y 投射）用到。
    public float ProjectionOffset;
    public float ProjectionDist;
    // 引擎里是一个区间对 ProjectionCorrectDist（first + u·second），RE4 存两个 float；旧名 unkn12 / unkn13。
    public float ProjectionCorrectDist;
	[RszVersion('<', EfxVersion.MHWilds, EndAt = nameof(DelayFrameCollision))]
    public float ProjectionCorrectDistExtra;
	[RszVersion('>', EfxVersion.RE3, EndAt = nameof(DelayFrameCollision))]
    // Freeze 时沿碰撞法线的偏移。
    public float LookNormalDirectionOffset;
	[RszVersion('>', EfxVersion.RERT, EndAt = nameof(DelayFrameCollision))]
    // 无引擎名。粒子年龄达到 center + randint[-spread, spread] 帧之前不做碰撞。
    public CenterSpreadI DelayFrameCollision;
	// 碰撞层名（如 "Bullet"、"Character"），在全局 CollisionFilter 里按名字查。旧名 unknString0。
	[RszConditional("(Flags & (1 << 0)) != 0"), RszInlineWString(ByteSize = true)]
	public string? LayerName;

	// 碰撞掩码名（如 "TcCameraHit"）。旧名 unknString1。
	[RszConditional("(Flags & (1 << 1)) != 0"), RszInlineWString(ByteSize = true)]
	public string? MaskName;
}


[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtProjection, EfxVersion.MHWilds)]
public partial class EFXAttributePtProjection : EFXAttribute
{
    public EFXAttributePtProjection() : base(EfxAttributeType.PtProjection) { }

    public uint flags;
    public float unkn2;
    public float unkn3;
    public float unkn4;
    public float unkn5;
    public UndeterminedFieldType unkn6;
    public UndeterminedFieldType unkn7;
	[RszConditional("(flags & 2) != 0")] // TODO either 64 or 2 for string (or both); 128 is NOT string; see: 11_em0156_00_011.efx.5571972
	[RszInlineWString(ByteSize = true)] public string? unknString0;
}


[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtColor, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4)]
public partial class EFXAttributePtColor : EFXAttribute
{
	public EFXAttributePtColor() : base(EfxAttributeType.PtColor) { }

	public PtColorOperator ColorOperator;
	public PtColorOperator AlphaOperator;
	public via.Color Color;

	public override string ToString() => $"PtColor: {Color}";
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtColorClip, EfxVersion.DMC5)]
public partial class EFXAttributePtColorClip : ReeLib.Efx.EFXAttribute, IClipAttribute
{
    public EfxClipData Clip => clipData;
    public BitSet ClipBits => clipBits;

    public EFXAttributePtColorClip() : base(EfxAttributeType.PtColorClip) { }

    /// <summary>
    /// This flag tells us which color channels have a clip:
    /// 1 = R, 2 = G, 4 = B, 8 = A
    /// </summary>
    // public uint colorClipBits;
	[RszClassInstance] public BitSet clipBits = new BitSet(4) { BitNames = ["R", "G", "B", "A"] };
    public uint unkn1;
	[RszClassInstance] public EfxClipData clipData = new();

    public override string ToString() => $"PtColorClip: {clipBits}";
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtColorMixer, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4)]
public partial class EFXAttributePtColorMixer : EFXAttribute
{
	public EFXAttributePtColorMixer() : base(EfxAttributeType.PtColorMixer) { }

	public uint Unkn0;
	[RszArraySizeField(nameof(Colors))] public int colorCount;
	public uint Unkn2;
	public float Unkn3;
	[RszFixedSizeArray(nameof(colorCount))] public via.Color[]? Colors;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtLife, EfxVersion.RE7, EfxVersion.RE2, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE8, EfxVersion.RERT, EfxVersion.RE4)]
public partial class EFXAttributePtLife : EFXAttribute
{
	public EFXAttributePtLife() : base(EfxAttributeType.PtLife) { }

	// re4.exe：vtable 7a0ce00，加载器 3d3d3d0 读 UniqueID + 0x0c 字节，触发函数 3d3d110。解析器是空壳，没有引擎名。
	// 子 Action 出生在粒子当前的位置/尺寸，只拿粒子的变换，不继承速度、颜色、寿命；父粒子死后子特效继续跑，矩阵冻结。
	// 每个 PtLife 有 2 × Spawn.MaxParticles 个 Action 实例池，满了覆盖最旧的。
	// 位的作用已读到，名字是描述性的：bit0 = 发射器停止或粒子死亡时释放子特效；bit1 = Mesh 粒子的旋转也继承（语料未用）；
	// bit2/bit3/bit4 = 子特效每帧跟随粒子的位置/旋转/缩放（不置位则冻结在出生时的快照）。
	public uint Flags;
	// 按生命阶段边沿触发，每个粒子每个阶段只触发一次：0 出生帧 / 1 进入 Appear / 2 进入 Keep / 3 进入 Vanish / 4 死亡。
	// 对应阶段帧数为 0 时该阶段不会触发（Appear 0 → 1 不触发；Vanish 0 → 3 不触发，改用 4）。
	// -1 不是"未知"：更新循环在无变化的帧传 -1，所以 Status = -1 会每帧触发（语料里没有）。
	public PtLifeStatus Status;
	// EFX 文件 Action 列表的下标（[file+0x58] + idx·0x38）；-1 或越界 = 不触发。
	public int ActionIndex;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtSort, EfxVersion.RE8, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributePtSort : EFXAttribute
{
	public EFXAttributePtSort() : base(EfxAttributeType.PtSort) { }

	public SortType SortType;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtUvSequence, EfxVersion.DMC5, EfxVersion.RE3, EfxVersion.RE4)]
public partial class EFXAttributePtUvSequence : EFXAttribute
{
	public EFXAttributePtUvSequence() : base(EfxAttributeType.PtUvSequence) { }

	public uint Flags;
	public uint PatternNo;
	[RszVersion("<=", EfxVersion.RE3)]
	public uint unkn1_2;
	public float PlaySpeed;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtUvSequenceClip, EfxVersion.DMC5, EfxVersion.RE4)]
public partial class EFXAttributePtUvSequenceClip : EFXAttribute, IClipAttribute
{
    public EfxClipData Clip => clipData;
    public BitSet ClipBits => clipBits;

    public EFXAttributePtUvSequenceClip() : base(EfxAttributeType.PtUvSequenceClip) { }

	[RszClassInstance] public BitSet clipBits = new BitSet(3);
    uint unkn1;
	[RszClassInstance] public EfxClipData clipData = new();
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtFreezer, EfxVersion.DD2, EfxVersion.MHWilds)]
public partial class EFXAttributePtFreezer : EFXAttribute
{
	public EFXAttributePtFreezer() : base(EfxAttributeType.PtFreezer) { }

	public uint unkn0;
	public uint unkn1;
	public uint unkn2;
	public uint unkn3;
	public uint unkn4;
	public uint unkn5;
	public UndeterminedFieldType unkn6;
	public UndeterminedFieldType unkn7;
	public uint unkn8;
	public UndeterminedFieldType unkn9;
}

[RszGenerate, RszAutoReadWrite]
public partial class PtPathTranslateName : BaseModel
{
	[RszInlineWString(ByteSize = true)] public string name = "";

    public override string ToString() => name;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtPathTranslate, EfxVersion.RE4, EfxVersion.DD2)]
public partial class EFXAttributePtPathTranslate : EFXAttribute
{
	public EFXAttributePtPathTranslate() : base(EfxAttributeType.PtPathTranslate) { }

	public uint unkn1;
	public uint unkn2;
	public float unkn3;
	public int dataSize;
	public float unkn4;
	public float unkn5;
	public float unkn6;
	public float unkn7;
	public float unkn8;
	public float unkn9;
	public float unkn10;
	public float unkn11;
	public float unkn12;
	public Vector3 rotation;
	public Vector3 scale;
	[RszFixedSizeArray(nameof(dataSize))] public PtPathTranslateSubstruct[]? substruct2;

	[RszVersion(EfxVersion.MHWilds)]
	public uint nameFlags;
	[RszVersion(EfxVersion.MHWilds)]
	[RszList(nameof(dataSize)), RszClassInstance] public List<PtPathTranslateName>? names;

    public struct PtPathTranslateSubstruct
    {
        public float unkn0;
        public float unkn1;
        public float unkn2;
        public float unkn3;
        public float unkn4;
        public float unkn5;
        public float unkn6;
    }
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtPathTranslateExpression, EfxVersion.MHWilds)]
public partial class EFXAttributePtPathTranslateExpression : EFXAttribute, IExpressionAttribute
{
	public EFXExpressionList? Expression { get => expressions; set => expressions = value; }
	public BitSet ExpressionBits => expressionBits;

	public EFXAttributePtPathTranslateExpression() : base(EfxAttributeType.PtPathTranslateExpression) { }

	[RszClassInstance] public BitSet expressionBits = new BitSet(4);

	public ExpressionAssignType Field1;
	public ExpressionAssignType Field2;
	public ExpressionAssignType Field3;
	public ExpressionAssignType Field4;
	[RszClassInstance, RszConstructorParams(nameof(Version))] public EFXExpressionList? expressions;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtLightningColliderAction, EfxVersion.MHWilds)]
public partial class EFXAttributePtLightningColliderAction : EFXAttribute
{
	public EFXAttributePtLightningColliderAction() : base(EfxAttributeType.PtLightningColliderAction) { }

	public uint Flags;
	public float unkn2;
	public uint unkn3;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtVortexelHeatSource, EfxVersion.MHWilds)]
public partial class EFXAttributePtVortexelHeatSource : EFXAttribute
{
	public EFXAttributePtVortexelHeatSource() : base(EfxAttributeType.PtVortexelHeatSource) { }

	public float Unkn0;
	public int unkn1;
	public short unkn2;
	public uint unkn3;
}

[RszGenerate, RszAutoReadWrite, RszVersionedObject(typeof(EfxVersion)), EfxStruct(EfxAttributeType.PtCollisionInfluence, EfxVersion.MHWilds)]
public partial class EFXAttributePtCollisionInfluence : EFXAttribute
{
	public EFXAttributePtCollisionInfluence() : base(EfxAttributeType.PtLightningColliderAction) { }

	public struct PtCollisionInfluenceData
	{
		public uint Hash;
		public float Influence1;
		public float Influence2;
		public float Influence3;
	}

	public uint Flags;
	public uint Unkn1;
	public uint Unkn2;
	public uint Unkn3;
	public uint Unkn4;
	public uint Unkn5;
	public uint Unkn6;
	public float Unkn7;
	public uint Unkn8;
	[RszFixedSizeArray] public PtCollisionInfluenceData[]? Data;
}
