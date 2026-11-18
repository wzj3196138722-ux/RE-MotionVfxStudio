using System.Numerics;
using System.Runtime.InteropServices;
using ReeLib.Common;
using ReeLib.InternalAttributes;

namespace ReeLib.Efx.Structs.Common;

public enum ExpressionAssignType
{
	Add = 0,
	Subtract = 1,
	Multiply = 2,
	Divide = 3,
	Assign = 4,
	ForceWord = -1
}

public enum MaterialParameterType : short
{
	None = 0,
	Float = 1, // float4（分量数 4，颜色 / 向量）；分量数 1 时语料形如 (x, x, 0, 0)
	Range = 2, // 16 字节 = (X, Y, 下限, 上限)：取值区间在后 8 字节（RangeValue）；X、Y 在语料里恒相等、基本为 0
	Texture = 3,
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class MdfProperty : BaseModel
{
    [RszIgnore] public EfxVersion Version;

    public MdfProperty() { }
    public MdfProperty(EfxVersion version)
    {
        Version = version;
    }

    public uint PropertyNameUTF8Hash;
    [RszVersion(EfxVersion.RE3)]
	public int mdfPropertyIndex;
    public ushort mdfParameterValueCount;
	public MaterialParameterType parameterType;
	/// <summary>非 0 时把结果钳到 [0, 1]（re4.exe 3d334e0：Range 钳抽到的值，Float 逐分量钳）。</summary>
	public int flags;
	[RszFixedSizeArray(16)] private byte[] rawValue = new byte[16];

    [RszIgnore] public string? texturePath;

    public object Value {
        get => parameterType switch {
            MaterialParameterType.Texture => TextureValue,
            _ => VectorValue,
        };
        set {
            switch (parameterType) {
                case MaterialParameterType.Texture:
                    TextureValue = (MdfPropertyTextureValue)value;
                    break;
                default:
                    VectorValue = (Vector4)value;
                    break;
            }
        }
    }

	public Vector4 VectorValue {
        get => MemoryMarshal.Read<Vector4>(rawValue);
        set => MemoryMarshal.Write<Vector4>(rawValue, value);
    }

	/// <summary>Range 参数的抽样区间：rawValue 的后 8 字节（Z、W）。re4.exe：出生时 Y = Z + u·(W − Z)，送进材质的值 = X + Y，
	/// 所以 X 是固定偏移（REasy 的 base），Y 是抽到的随机值（random），见 RE4-EFX-RUNTIME §4.3.11。RE4 语料 4141 条 Range 的前 8 字节全部 X == Y、基本为 0，
	/// 取值都在 Z、W，例如 (0, 0, 0.25, 0.25)、(0, 0, 1, 3)；与 .mdf2 的核对见 MHWs-EFX-Editor mdf_catalog.py。</summary>
	public ReeLib.via.Range RangeValue {
        get => MemoryMarshal.Read<ReeLib.via.Range>(rawValue.AsSpan(8));
        set => MemoryMarshal.Write<ReeLib.via.Range>(rawValue.AsSpan(8), value);
    }

	public MdfPropertyTextureValue TextureValue {
        get => MemoryMarshal.Read<MdfPropertyTextureValue>(rawValue);
        set => MemoryMarshal.Write<MdfPropertyTextureValue>(rawValue, value);
    }

    public string? Name => HashToName(PropertyNameUTF8Hash);

    public static int GetSize(EfxVersion version) => version >= EfxVersion.RE3 ? 32 : 28;

    public override string ToString() => $"[{HashToName(PropertyNameUTF8Hash)}] {parameterType}";

    public static Dictionary<uint, string> ParameterNameHashes = new() {
        { 3072153074, "BaseMap" },
    };

    public static string HashToName(uint hash) => ParameterNameHashes.TryGetValue(hash, out var str) ? str : hash.ToString();

    protected override bool DoRead(FileHandler handler)
    {
        return DefaultRead(handler);
    }

    protected override bool DoWrite(FileHandler handler)
    {
        if (parameterType == MaterialParameterType.Texture) {
            mdfPropertyIndex = -1;
        }
        return DefaultWrite(handler);
    }
}

public struct MdfPropertyTextureValue
{
    public int pathLength;
    public int textureIndex;
    public int uknInt;
    public int _padding;

    public override string ToString() => $"Texture {textureIndex} (len={pathLength})";
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public abstract partial class EfxMaterialStructBase : BaseModel
{
    [RszIgnore] public EfxVersion Version;

    public EfxMaterialStructBase() { }
    public EfxMaterialStructBase(EfxVersion version)
    {
        Version = version;
    }
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class EfxMaterialStructV1 : EfxMaterialStructBase
{
    public EfxMaterialStructV1() { }
    public EfxMaterialStructV1(EfxVersion version) : base(version) {}

	public int maxPropertyIndex;
	public int ukn1;
    [RszArraySizeField(nameof(properties))] public int propertyCount;
	[RszArraySizeField(nameof(texPaths))] public int texCount;
    public int ukn2;
	[RszStringLengthField(nameof(mdfPath))] public int mdfPathLength;
    [RszStringLengthField(nameof(mmtrPath))] public int mmtrPathLength;
	public int texBlockSize;

    [RszClassInstance, RszList(nameof(propertyCount)), RszConstructorParams(nameof(Version))]
    public List<MdfProperty> properties = new();
    [RszInlineWString(nameof(mdfPathLength))] public string? mdfPath;
    [RszInlineWString(nameof(mmtrPathLength))] public string? mmtrPath;
	[RszInlineWString, RszList(nameof(texCount))] public string[]? texPaths;

    public override string ToString() => $"Material: {mdfPath}";

    protected override bool DoRead(FileHandler handler)
    {
        DefaultRead(handler);
        foreach (var p in properties) {
            if (p.parameterType == MaterialParameterType.Texture && p.TextureValue.textureIndex < texPaths?.Length) {
                p.texturePath = texPaths![p.TextureValue.textureIndex];
            }
        }
        return true;
    }

    protected override bool DoWrite(FileHandler handler)
    {
        texBlockSize = 0;
        var pathlist = new List<string>();
        foreach (var p in properties) {
            if (p.parameterType == MaterialParameterType.Texture) {
                p.TextureValue = p.TextureValue with { pathLength = (p.texturePath!).Length + 1, textureIndex = pathlist.Count };
                pathlist.Add(p.texturePath);
                texBlockSize += p.TextureValue.pathLength;
            }
        }
        texPaths = pathlist.ToArray();
        return DefaultWrite(handler);
    }
}

[RszGenerate, RszVersionedObject(typeof(EfxVersion))]
public partial class EfxMaterialStructV2 : EfxMaterialStructBase
{
    public EfxMaterialStructV2() { }
    public EfxMaterialStructV2(EfxVersion version) : base(version) { }

	public uint ukn1;
	[RszVersion(EfxVersion.MHWilds)] public float mhws_unkn;
    [RszArraySizeField(nameof(properties))] public int propertyCount;
	[RszArraySizeField(nameof(texPaths))] public int texCount;
	public uint ukn2;
    public uint ukn3;
	public uint ukn4;
	[RszInlineWString(ByteSize = true)] public string? mdfPath;
	[RszInlineWString(ByteSize = true)] public string? mmtrPath;
	[RszByteSizeField(nameof(properties))] public uint propDataSize;

    [RszClassInstance, RszList(nameof(propertyCount)), RszConstructorParams(nameof(Version))]
    public List<MdfProperty> properties = new();
	public int texBlockSize;
	[RszInlineWString, RszList(nameof(texCount))] public string[]? texPaths;

    public override string ToString() => $"Material: {mdfPath}";

    protected override bool DoRead(FileHandler handler)
    {
        DefaultRead(handler);
        foreach (var p in properties) {
            if (p.parameterType == MaterialParameterType.Texture && p.TextureValue.textureIndex < texPaths?.Length) {
                p.texturePath = texPaths![p.TextureValue.textureIndex];
            }
        }
        return true;
    }

    protected override bool DoWrite(FileHandler handler)
    {
        texBlockSize = 0;
        var pathlist = new List<string>();
        foreach (var p in properties) {
            if (p.parameterType == MaterialParameterType.Texture) {
                p.TextureValue = p.TextureValue with { pathLength = ((p.texturePath!).Length + 1), textureIndex = pathlist.Count };
                pathlist.Add(p.texturePath);
                texBlockSize += p.TextureValue.pathLength * 2;
            }
        }
        texPaths = pathlist.ToArray();
        return DefaultWrite(handler);
    }
}

public struct ByteSet
{
    public byte b1;
    public byte b2;
    public byte b3;
    public byte b4;

    public override string ToString() => $"{b1} {b2} {b3} {b4}";
}

public struct SByteSet
{
    public sbyte b1;
    public sbyte b2;
    public sbyte b3;
    public sbyte b4;

    public override string ToString() => $"{b1} {b2} {b3} {b4}";
}
