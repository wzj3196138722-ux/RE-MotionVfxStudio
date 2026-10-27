using System.Text.Json;
using System.Text.Json.Nodes;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;

namespace VfxStudio.LiveEditor;

internal sealed partial class EditorEngine
{
    // One immutable, session-only snapshot. Switching documents never turns a
    // copied selection into a reference to whichever object now has that index.
    private sealed record EfxClipboard(string Id, byte[] Bytes, string Source, Target Target,
        string Kind, string Name, string? AttributeType, EfxVersion Version);
    private EfxClipboard? efxClipboard;

    private JsonNode? ClipboardInfo() => efxClipboard is { } c ? JsonSerializer.SerializeToNode(new
    {
        c.Id, c.Kind, c.Name, c.AttributeType, version = (int)c.Version, sourceName = Path.GetFileName(c.Source)
    }, Host.Json) : null;

    private void CopyEfx(Target target)
    {
        var value = Resolve(target);
        if (value is not EFXEntry && value is not EFXAttribute)
            throw new InvalidOperationException("请选择一个特效块或属性再复制；整个 Action 不作为特效块复制。");
        if (bytes!.Length > 128 * 1024 * 1024)
            throw new InvalidOperationException("此文件超过复制缓冲区的 128 MiB 上限，未替换已复制内容。");
        var attribute = value as EFXAttribute;
        efxClipboard = new(Guid.NewGuid().ToString("N"), bytes.ToArray(), source!, target,
            attribute == null ? "entry" : "attribute", attribute?.type.ToString() ?? ((EFXEntry)value).name ?? "未命名特效块",
            attribute?.type.ToString(), ResolveTargetFile(target).Header.Version);
    }

    private void PasteEfx(JsonObject request, JsonObject response)
    {
        var copy = efxClipboard ?? throw new InvalidOperationException("还没有复制内容，请先选择来源并点击复制。");
        if (Text(request, "clipboardId") != copy.Id)
            throw new InvalidOperationException("已复制内容已变化，请重新确认粘贴目标。");
        var target = Target.From(request);
        var destination = ResolveTargetFile(target);
        if (destination.Header.Version != copy.Version)
            throw new InvalidOperationException("来源和目标 EFX 的格式版本不同，不能直接粘贴。");
        var snapshot = service.Load(copy.Bytes, copy.Source);
        try
        {
            var sourceFile = ResolveTargetFile(copy.Target, snapshot);
            var payload = Resolve(copy.Target, snapshot);
            EnsureClipboardParameters(sourceFile, destination);
            Target? inserted = null;
            string expected = ClipboardFingerprint(payload, copy.Version);
            Mutate(() =>
            {
                if (copy.Kind == "entry")
                {
                    if (payload is not EFXEntry entry) throw new InvalidDataException("复制内容不是特效块。");
                    var memberships = new List<int>();
                    if (target.Kind == "group")
                    {
                        if (target.Index < 0 || target.Index >= destination.EffectGroups.Count) throw new ArgumentOutOfRangeException("group");
                        memberships.Add(target.Index);
                    }
                    else if (target.Kind == "entry")
                    {
                        Resolve(target); // Validate the whole selected target, including any attribute.
                        for (int i = 0; i < destination.EffectGroups.Count; i++)
                            memberships.AddRange(Enumerable.Repeat(i, (destination.EffectGroups[i].efxEntryIndexes ?? []).Count(p => p == target.Index)));
                    }
                    else if (target.Kind != "document" || target.Index != 0 || target.AttributeIndex != null)
                        throw new InvalidOperationException("请选择目标 Group、特效块或文件；不能将特效块粘贴成 Action 属性。");
                    entry.name = UniqueCopyName(destination, entry.name ?? "Effect");
                    entry.Groups.Clear();
                    foreach (int group in memberships) entry.Groups.Add(destination.EffectGroups[group].groupName);
                    entry.index = ReferenceEquals(destination, Require()) ? AllocateEntryId() : checked(destination.Entries.Select(e => e.index).DefaultIfEmpty(-1).Max() + 1);
                    foreach (var attribute in entry.Attributes) PrepareClipboardAttribute(attribute, sourceFile, destination);
                    entry.ReorderEntries();
                    int index = destination.Entries.Count;
                    destination.Entries.Add(entry);
                    foreach (int group in memberships)
                    {
                        var g = destination.EffectGroups[group];
                        g.efxEntryIndexes = [.. g.efxEntryIndexes ?? [], index]; g.valueCount = g.efxEntryIndexes.Length;
                    }
                    service.SynchronizeEffectGroups(destination);
                    inserted = new("entry", index, null, target.EmbeddedPath);
                }
                else
                {
                    if (target.Kind is not ("entry" or "action")) throw new InvalidOperationException("请选择接收属性的特效块或 Action。");
                    Resolve(target);
                    var ownerTarget = target with { AttributeIndex = null };
                    var owner = (EFXEntryBase)Resolve(ownerTarget);
                    var attribute = (EFXAttribute)payload;
                    var matches = owner.Attributes.Where(a => a.type == attribute.type).ToArray();
                    EFXAttribute? existing = null;
                    if (matches.Length > 0)
                    {
                        if (request["replace"]?.GetValue<bool>() != true)
                            throw new InvalidOperationException("目标已有同类型属性，请确认替换后再粘贴；原属性未改动。");
                        int index = Int(request, "replaceAttributeIndex");
                        if (index < 0 || index >= owner.Attributes.Count || owner.Attributes[index].type != attribute.type)
                            throw new InvalidOperationException("待替换属性已变化，请重新选择目标。");
                        existing = owner.Attributes[index];
                    }
                    else if (request["replace"]?.GetValue<bool>() == true)
                        throw new InvalidOperationException("待替换属性已不存在，请重新选择目标。");
                    PrepareClipboardAttribute(attribute, sourceFile, destination, existing?.UniqueID);
                    if (existing != null) owner.Attributes[owner.Attributes.IndexOf(existing)] = attribute;
                    else if (!service.AddAttributeToEntry(owner, attribute))
                        throw new InvalidOperationException("无法添加此属性：请先添加对应的基础属性，并检查是否已有其他 Type 类型或目标块不支持此属性。");
                    owner.ReorderEntries();
                    inserted = ownerTarget with { AttributeIndex = owner.Attributes.IndexOf(attribute) };
                }
            }, reopened =>
            {
                var actual = Resolve(inserted!, reopened);
                if (ClipboardFingerprint(actual, copy.Version) != expected)
                    throw new InvalidDataException("粘贴后的参数回读不一致，已撤回本次粘贴。");
            });
            response["selection"] = JsonSerializer.SerializeToNode(inserted, Host.Json);
            response["message"] = copy.Kind == "entry" ? "已粘贴新特效块并选中，可立即编辑；可用撤销恢复。" : "属性已粘贴并选中，可立即编辑；可用撤销恢复。";
        }
        finally { DisposeFile(snapshot); }
    }

    private static string UniqueCopyName(EfxFile file, string name)
    {
        string stem = name + "_copy", candidate = stem; int suffix = 2;
        while (file.Entries.Any(e => e.name == candidate)) candidate = stem + "_" + suffix++;
        return candidate;
    }

    private static void EnsureClipboardParameters(EfxFile from, EfxFile to)
    {
        // Global expression/field/UVAR tables can define the meaning of payload
        // values. Never silently reinterpret them in a different document.
        if (from.ExpressionParameters.Count == 0 && from.FieldParameterValues.Count == 0 && from.UvarGroups.Count == 0) return;
        string Tables(EfxFile f) => JsonSerializer.Serialize(new { f.ExpressionParameters, f.FieldParameterValues, f.UvarGroups }, EfxJsonTypeResolver.jsonOptions);
        if (Tables(from) != Tables(to))
            throw new InvalidOperationException("来源与目标的文件级表达式／变量表不同，不能直接跨文件粘贴这份内容。原文件与已复制内容均保留。");
    }

    private void PrepareClipboardAttribute(EFXAttribute attribute, EfxFile from, EfxFile to, int? keepId = null)
    {
        attribute.UniqueID = keepId ?? NextAttributeId();
        if (attribute is IBoneRelationAttribute bone && !string.IsNullOrEmpty(bone.ParentBone))
        {
            var sourceBone = from.Bones.FirstOrDefault(b => b.name == bone.ParentBone)
                ?? throw new InvalidDataException("复制属性的骨骼引用未能解析，未粘贴。");
            var old = to.Bones.FirstOrDefault(b => b.name == sourceBone.name);
            if (old == null) to.Bones.Add(sourceBone);
            else if (old.value != sourceBone.value) throw new InvalidOperationException("目标存在同名但数据不同的骨骼引用，未覆盖。");
        }
        if (attribute is EFXAttributePlayEmitter { efxrData: { } embedded })
            foreach (var nested in AllEmbeddedAttributes(embedded)) nested.UniqueID = NextAttributeId();
    }

    private static string ClipboardFingerprint(object value, EfxVersion version)
    {
        IEnumerable<EFXAttribute> attributes = value is EFXEntry entry ? entry.Attributes : [(EFXAttribute)value];
        return string.Join("\n", attributes.Select(a => a.type + ":" + string.Join("|", ContentInspector.Describe(a, version)
            .Where(f => f.Description.Path != "UniqueID" && !f.Description.Path.EndsWith(".UniqueID", StringComparison.Ordinal))
            .Select(f => f.Description.Path + "=" + f.Description.Value))));
    }
}
