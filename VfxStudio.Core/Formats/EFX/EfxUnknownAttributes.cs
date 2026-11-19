using ReeLib.InternalAttributes;

namespace ReeLib.Efx.Structs.Misc;

// MHWs-EFX-Editor 本地补丁：这几个类型在 MHWilds 的 itemType 映射表里存在，但 vendor 没有
// 给它们配读写实现类——EfxAttributeTypeRemapper.Create() 找不到类型就直接抛
// "Unsupported EFX attribute type"，导致带这些 attribute 的文件整个读不进来。
//
// 我们不认识这些类型的字段结构（vendor 自己也不认识——AttributeTypeIDs 里能查到 itemType
// 数值，但没有任何字段布局线索），所以不去猜字段，照抄社区 010 模板对同类情况的处理
// （`ubyte unkn[unknSeqNum - 4]`）：整个 attribute body 当一段不透明字节数组读写，不解释、
// 不暴露给用户编辑，只保证能原样导入、原样导出。
//
// 这几个类要用到 EFXAttribute.ExpectedByteSize（同一次补丁加在 EfxFile.cs 里）：MHWilds
// 起每个 attribute 前面都有一个"声明长度"字段，之前只用来读完之后做校验，没有传给 attribute
// 自己——不透明类必须知道这个长度才能知道该读多少字节。
public abstract class EFXAttributeOpaqueUnknown : EFXAttribute
{
    protected EFXAttributeOpaqueUnknown(EfxAttributeType type) : base(type)
    {
    }

    public byte[] unknownData = [];

    protected override bool DoRead(FileHandler handler)
    {
        var len = ExpectedByteSize >= 4 ? ExpectedByteSize - 4 : 0;
        unknownData = handler.ReadArray<byte>(len);
        return true;
    }

    protected override bool DoWrite(FileHandler handler)
    {
        handler.WriteArray(unknownData);
        return true;
    }
}

[EfxStruct(EfxAttributeType.PtColorMixerClip, EfxVersion.MHWilds)]
public sealed class EFXAttributePtColorMixerClip : EFXAttributeOpaqueUnknown
{
    public EFXAttributePtColorMixerClip() : base(EfxAttributeType.PtColorMixerClip) { }
}

[EfxStruct(EfxAttributeType.FixRandomGeneratorExpression, EfxVersion.MHWilds)]
public sealed class EFXAttributeFixRandomGeneratorExpression : EFXAttributeOpaqueUnknown
{
    public EFXAttributeFixRandomGeneratorExpression() : base(EfxAttributeType.FixRandomGeneratorExpression) { }
}

[EfxStruct(EfxAttributeType.FluidParticle2DSimulatorExpression, EfxVersion.MHWilds)]
public sealed class EFXAttributeFluidParticle2DSimulatorExpression : EFXAttributeOpaqueUnknown
{
    public EFXAttributeFluidParticle2DSimulatorExpression() : base(EfxAttributeType.FluidParticle2DSimulatorExpression) { }
}
