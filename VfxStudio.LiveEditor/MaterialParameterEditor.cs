using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using ReeLib.Efx;
using ReeLib.Efx.Enums;
using ReeLib.Efx.Structs.Common;
using ReeLib.Efx.Structs.Main;

namespace VfxStudio.LiveEditor;

/// <summary>Adds or removes one entry of an EFX material property table (the sparse list of material parameter
/// overrides on TypeMesh and on material Types). The window builds the entry from a reference .mdf2
/// (PreviewMaterialReference): name hash, parameter index and component count must come from that material.
///
/// Only owners whose writer rebuilds the texture path table from the entries are editable: TypeMeshV2 and
/// EfxMaterialStructV2. EfxMaterialStructV1 also stores a maxPropertyIndex this editor does not maintain, and the
/// GPU mesh trail does not rebuild its texture table; neither occurs in the RE4 corpus.</summary>
internal static class MaterialParameterEditor
{
    /// <summary>Applies the request to <paramref name="attribute"/>. Returns the hashes the list must hold afterwards,
    /// in order, for the serialization check.</summary>
    public static uint[] Apply(EFXAttribute attribute, IEnumerable<EFXAttribute> siblings, EfxVersion version, JsonObject request)
    {
        string listPath = request["listPath"]?.GetValue<string>() ?? "properties";
        var (owner, list) = Locate(attribute, listPath);
        if (owner is not (EFXAttributeTypeMeshV2 or EfxMaterialStructV2))
            throw new InvalidOperationException($"{owner.GetType().Name} 的材质参数表暂不支持增删（它的写出逻辑不重建贴图表或另有计数字段）。");
        string op = request["op"]?.GetValue<string>() ?? throw new ArgumentException("op is required (add or remove).");
        switch (op)
        {
            case "add":
            {
                uint hash = request["hash"]?.GetValue<uint>() ?? throw new ArgumentException("hash is required.");
                if (list.Any(p => p.PropertyNameUTF8Hash == hash)) throw new InvalidOperationException("这个参数已经有覆盖项了。");
                var type = Enum.Parse<MaterialParameterType>(request["type"]?.GetValue<string>() ?? throw new ArgumentException("type is required."));
                int count = request["count"]?.GetValue<int>() ?? 1;
                if (count is not (1 or 4)) throw new ArgumentException("count must be 1 or 4.");
                var property = new MdfProperty(version)
                {
                    PropertyNameUTF8Hash = hash,
                    mdfParameterValueCount = (ushort)count,
                    parameterType = type,
                    flags = 0,
                };
                if (type == MaterialParameterType.Texture)
                {
                    string path = request["texturePath"]?.GetValue<string>() ?? "";
                    if (path.Length == 0) throw new ArgumentException("贴图覆盖需要贴图路径。");
                    // Index -1 and uknInt 1 are what every texture override in the RE4 and Wilds corpora holds;
                    // pathLength / textureIndex are recomputed by the owner's writer.
                    property.mdfPropertyIndex = -1;
                    property.TextureValue = new MdfPropertyTextureValue { uknInt = 1 };
                    property.texturePath = path;
                }
                else if (type is MaterialParameterType.Float or MaterialParameterType.Range)
                {
                    int index = request["mdfIndex"]?.GetValue<int>() ?? throw new ArgumentException("mdfIndex is required.");
                    if (index < 0) throw new ArgumentException("A parameter override needs the parameter's index in the material.");
                    property.mdfPropertyIndex = index;
                    var v = request["value"] as JsonArray ?? throw new ArgumentException("value must be four numbers.");
                    if (v.Count != 4) throw new ArgumentException("value must be four numbers.");
                    property.VectorValue = new Vector4(v[0]!.GetValue<float>(), v[1]!.GetValue<float>(), v[2]!.GetValue<float>(), v[3]!.GetValue<float>());
                }
                else throw new ArgumentException("type must be Float, Range or Texture.");
                list.Add(property);
                break;
            }
            case "remove":
            {
                int index = request["index"]?.GetValue<int>() ?? throw new ArgumentException("index is required.");
                if (index < 0 || index >= list.Count) throw new ArgumentOutOfRangeException(nameof(index), "参数序号无效。");
                uint hash = list[index].PropertyNameUTF8Hash;
                if (Users(siblings, hash) is { Count: > 0 } users)
                    throw new InvalidOperationException("这个参数还被" + string.Join("、", users) + "引用，先删掉对应的曲线或表达式。");
                list.RemoveAt(index);
                break;
            }
            default: throw new ArgumentException("op must be add or remove.");
        }
        return list.Select(p => p.PropertyNameUTF8Hash).ToArray();
    }

    /// <summary>The property hashes at <paramref name="listPath"/> (for checking a reopened file).</summary>
    public static uint[] Hashes(EFXAttribute attribute, string listPath) =>
        Locate(attribute, listPath).List.Select(p => p.PropertyNameUTF8Hash).ToArray();

    /// <summary>Material clips and expressions of the same entry that drive the parameter with this hash.</summary>
    private static List<string> Users(IEnumerable<EFXAttribute> siblings, uint hash)
    {
        var users = new List<string>();
        foreach (var attribute in siblings)
        {
            if (attribute is IMaterialClipAttribute clip && clip.MaterialClip.mdfProperties?.Any(p => p.mdfPropertyHash == hash) == true)
                users.Add(attribute.type + " 的曲线");
            if (attribute is IMaterialExpressionAttribute expression &&
                expression.MaterialExpressions?.expressions.Any(e => e.mdfPropertyHash == hash) == true)
                users.Add(attribute.type + " 的表达式");
        }
        return users;
    }

    private static (object Owner, List<MdfProperty> List) Locate(object root, string listPath)
    {
        object owner = root;
        var parts = listPath.Split('.');
        for (int i = 0; i < parts.Length - 1; i++)
            owner = Member(owner, parts[i]) ?? throw new ArgumentException("Path not present: " + listPath);
        return (owner, Member(owner, parts[^1]) as List<MdfProperty> ?? throw new ArgumentException("Not a material property list: " + listPath));
    }

    private static object? Member(object owner, string name)
    {
        var type = owner.GetType();
        var member = (MemberInfo?)type.GetField(name, BindingFlags.Public | BindingFlags.Instance) ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return member switch
        {
            FieldInfo f => f.GetValue(owner),
            PropertyInfo p => p.GetValue(owner),
            _ => null
        };
    }
}
