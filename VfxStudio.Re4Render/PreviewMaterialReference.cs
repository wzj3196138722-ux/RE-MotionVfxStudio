using System.Numerics;
using System.Text.Json.Nodes;
using ReeLib;
using ReeLib.Common;

namespace VfxStudio.Re4Render;

/// <summary>One parameter or texture slot of a reference material that an EFX material property can override.</summary>
/// <param name="Index">Position in the .mdf2 parameter table (the property's mdfPropertyIndex); -1 for textures.</param>
/// <param name="Value">The material's own value (parameters).</param>
/// <param name="TexturePath">The material's own texture (texture slots).</param>
public sealed record PreviewMaterialSlot(bool Texture, string Name, uint Hash, int Index, int ComponentCount, Vector4 Value, string TexturePath)
{
    public string Kind => Texture ? "贴图" : ComponentCount == 4 ? "float4" : "float";
}

/// <summary>A reference .mdf2 for an EFX material property table (TypeMesh <c>properties</c>, material Types'
/// <c>material.properties</c>). The table is a sparse override list: each entry names a material parameter by the
/// UTF-8 MurMur3 hash of its name and repeats its index and component count, so the .mdf2 is the only source of
/// names and of what can be added.
///
/// Checked on the RE4 corpus: of the 5065 parameter overrides whose name is in the referenced .mdf2, all have
/// mdfPropertyIndex = the parameter's index and mdfParameterValueCount = its component count; all 298 texture
/// overrides have index -1. The same holds for Wilds (MHWs-EFX-Editor mdf_catalog.py).</summary>
public sealed class PreviewMaterialReference
{
    /// <summary>Where the material was read from (a disk path or "pak :: path").</summary>
    public string Source { get; }
    public string MaterialName { get; }
    public IReadOnlyList<PreviewMaterialSlot> Slots { get; }
    private readonly Dictionary<uint, PreviewMaterialSlot> byHash;

    private PreviewMaterialReference(string source, string materialName, IReadOnlyList<PreviewMaterialSlot> slots)
    {
        Source = source; MaterialName = materialName; Slots = slots;
        byHash = new();
        foreach (var slot in slots) byHash.TryAdd(slot.Hash, slot);
    }

    /// <summary>Reads <paramref name="bytes"/> as an .mdf2 whose file name carries its version
    /// (<paramref name="versionedName"/>, e.g. "x.mdf2.32"). Refuses a file with more than one material: an EFX
    /// property table has one material path and no material index, so which table the indices count in is unknown.</summary>
    public static PreviewMaterialReference Parse(byte[] bytes, string versionedName, string source)
    {
        var mdf = new MdfFile(new FileHandler(new MemoryStream(bytes, writable: false), versionedName));
        if (!mdf.Read()) throw new InvalidDataException("读不出这个 .mdf2：" + source);
        if (mdf.Materials.Count == 0) throw new InvalidDataException("这个 .mdf2 里一个材质都没有：" + source);
        if (mdf.Materials.Count > 1)
            throw new InvalidDataException($"这个 .mdf2 有 {mdf.Materials.Count} 个材质，参数下标该按哪一个算无法确定：" + source);
        var material = mdf.Materials[0];
        var slots = new List<PreviewMaterialSlot>();
        for (int i = 0; i < material.Parameters.Count; i++)
        {
            var p = material.Parameters[i];
            slots.Add(new(false, p.paramName, MurMur3HashUtils.GetUTF8Hash(p.paramName), i, p.componentCount, p.parameter, ""));
        }
        foreach (var t in material.Textures)
            slots.Add(new(true, t.texType, MurMur3HashUtils.GetUTF8Hash(t.texType), -1, 1, Vector4.Zero, t.texPath));
        return new(source, material.Header.matName, slots);
    }

    /// <summary>The material at an EFX material path (RE4: <c>.mdf2.32</c>), through the preview's resource lookup
    /// (document root, loose files, extracted paks, paks).</summary>
    public static PreviewMaterialReference FromResource(Re4ResourceSource resources, string materialPath, string version = ".32")
    {
        var read = resources.Read(materialPath, version);
        if (read.Placeholder) throw new FileNotFoundException("找不到材质：" + materialPath);
        return Parse(read.Bytes, "material.mdf2" + version, read.Source);
    }

    /// <summary>A material chosen by hand; the file name must end in .mdf2.&lt;version&gt;.</summary>
    public static PreviewMaterialReference FromFile(string path)
    {
        string full = Path.GetFullPath(path.Trim().Trim('"'));
        if (!System.Text.RegularExpressions.Regex.IsMatch(full, @"\.mdf2\.\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("需要带版本号的 .mdf2 文件（例如 xxx.mdf2.32）。");
        return Parse(File.ReadAllBytes(full), full, full);
    }

    public PreviewMaterialSlot? Find(uint hash) => byHash.GetValueOrDefault(hash);

    /// <summary>Why an existing property does not match this material, or null when it does.</summary>
    public string? Mismatch(uint hash, int index, int componentCount, bool texture)
    {
        if (Find(hash) is not { } slot) return "参考材质里没有这个参数（覆盖不会生效）";
        if (slot.Texture != texture) return slot.Texture ? "参考材质里它是贴图槽" : "参考材质里它是数值参数";
        if (!slot.Texture && slot.Index != index) return $"下标 {index} 与参考材质的 {slot.Index} 不一致";
        if (!slot.Texture && slot.ComponentCount != componentCount) return $"分量数 {componentCount} 与参考材质的 {slot.ComponentCount} 不一致";
        return null;
    }

    /// <summary>The backend request body that adds <paramref name="slot"/> to the property list at
    /// <paramref name="listPath"/>, starting from the material's own value. A one-component parameter becomes a
    /// Range (0, 0, v, v): the engine draws Y from [Z, W] and sends X + Y, so this is v with no randomness.</summary>
    public static JsonObject AddBody(PreviewMaterialSlot slot, string listPath)
    {
        var body = new JsonObject { ["op"] = "add", ["listPath"] = listPath, ["hash"] = slot.Hash, ["name"] = slot.Name };
        if (slot.Texture)
        {
            body["type"] = "Texture"; body["mdfIndex"] = -1; body["count"] = 1; body["texturePath"] = slot.TexturePath;
            return body;
        }
        var v = slot.Value;
        bool vector = slot.ComponentCount == 4;
        body["type"] = vector ? "Float" : "Range";
        body["mdfIndex"] = slot.Index;
        body["count"] = slot.ComponentCount;
        body["value"] = vector ? new JsonArray(v.X, v.Y, v.Z, v.W) : new JsonArray(0f, 0f, v.X, v.X);
        return body;
    }
}
