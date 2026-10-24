using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReeLib;
using ReeLib.Pfb;

namespace VfxStudio.LiveEditor;

// Edits are committed only after rebuilding the RSZ graph, resources and object
// reference table, then reading the result with the exact same checked schema.
internal sealed class PfbWorkspace
{
    internal sealed class Clipboard { internal RszInstance? Element; }
    private sealed record Slot(string Key, string Kind, string Label, RszInstance Value, PfbGameObject Owner,
        IList? List, object? Item, string? ResourceField, Action Remove);
    private sealed record Field(string Path, string Label, object Value, RszField Schema, Action<object> Assign);
    private readonly RszFileOption format;
    private readonly Clipboard clipboard;
    private readonly byte[] baseline;
    private byte[] content;
    private readonly Stack<byte[]> undo = new(), redo = new();
    private long revision;
    private readonly string originalPath;
    internal string SourcePath { get; }
    internal string SourceKind { get; }
    internal long Revision => revision;
    internal static string NormalizePrefabPath(string path) => NormalizeResource(path, "effectPfb");

    internal PfbWorkspace(Options options, string path, string sourceKind = "local_file", Clipboard? clipboard = null)
    {
        this.clipboard = clipboard ?? new();
        SourceKind = sourceKind;
        if (sourceKind == "local_file")
        {
            SourcePath = Path.GetFullPath(path);
            if (!Regex.IsMatch(SourcePath, @"\.pfb(?:\.18)?$", RegexOptions.IgnoreCase)) throw new ArgumentException("请选择 .pfb 或 .pfb.18 文件。");
            var info = new FileInfo(SourcePath);
            if (!info.Exists || info.Length < 56 || info.Length > 128 * 1024 * 1024) throw new InvalidDataException("PFB 文件不存在或大小无效。");
            baseline = File.ReadAllBytes(SourcePath);
            string normalized = SourcePath.Replace('\\', '/');
            int index = normalized.IndexOf("/natives/stm/", StringComparison.OrdinalIgnoreCase);
            originalPath = index < 0 ? "" : NormalizeResource(normalized[(index + 13)..], "effectPfb");
        }
        else if (sourceKind == "game_files")
        {
            originalPath = NormalizeResource(path, "effectPfb"); SourcePath = originalPath;
            baseline = new GameInstallResourceReader(options).ReadPrefabResource(originalPath).Content;
        }
        else throw new ArgumentException("未知 PFB 文件来源。");
        content = baseline;
        string schema = Path.Combine(options.Assets, "Lists", "rsz_pfb_classes.json");
        if (!File.Exists(schema)) schema = Path.Combine(AppContext.BaseDirectory, "formats", "rsz_pfb_classes.json");
        format = new RszFileOption(GameName.mhwilds, RszParser.GetInstance(schema));
        using var parsed = Read(content);
    }

    internal object Info()
    {
        using var file = Read(content);
        string type = TypeOf(file);
        return new {
            revision, type, path = SourcePath, name = Path.GetFileName(SourcePath), sourceKind = SourceKind,
            dirty = !content.AsSpan().SequenceEqual(baseline), canUndo = undo.Count > 0, canRedo = redo.Count > 0,
            canPaste = clipboard.Element != null && EffectContainers(file).Any(c => ElementClass(c.Component) == clipboard.Element.RszClass.name),
            slots = Slots(file).Select(s => new { key = s.Key, kind = s.Kind, label = s.Label, resourcePath = Resource(s),
                fields = Fields(s.Value).Select(f => new { path = f.Path, label = f.Label, type = FieldType(f.Value),
                    value = DisplayValue(f.Value), editable = Editable(f) }).ToArray() }).ToArray(),
            addKinds = type == "equipment" ? new[] { new { kind = "effectPfb", label = "添加特效 PFB 槽位" },
                new { kind = "chain", label = "添加物理槽位（Chain / Chain2）" }, new { kind = "clsp", label = "添加物理碰撞槽位（CLSP）" },
                new { kind = "jcns", label = "添加关节约束槽位（JCNS）" } } : [],
            targets = Walk(file.GameObjects).Select((g, i) => new { key = "g" + i, label = g.GetHierarchyPath() }).ToArray(),
            previewMetadata = new { originalPath, sourceKind = SourceKind, pfbType = type, sourceHash = Hash(baseline), revision,
                resourcePaths = file.ResourceInfoList.Select(r => r.Path).ToArray(),
                equipmentComponents = type == "equipment" ? EquipmentComponents(file).ToArray() : [],
                expectedElements = EffectContainers(file).SelectMany(c => Array(c.Component,"Elements").Cast<RszInstance>().Select(e => new {
                    providerPath=c.Owner.GetHierarchyPath(),providerClass=c.Component.RszClass.name,
                    container=int.TryParse((c.Owner.Name??"").Split('_')[0],out int id)?id:-1,
                    id=e.GetFieldValue("ID"),resources=(e.GetFieldValue("Resources") as IList)?.Cast<object>().Select(v=>v.ToString()).ToArray(),
                    relationChildId=e.GetFieldValue("RelationChildId"),jointName=e.GetFieldValue("JointName") })).ToArray() }
        };
    }

    internal byte[] ExportBytes() => content.ToArray();
    internal void Add(JsonObject request) => Mutate(request, file =>
    {
        if (TypeOf(file) != "equipment") throw new InvalidOperationException("此 PFB 不是装备类型；特效 PFB 请使用复制、粘贴槽位。");
        string kind = Text(request, "kind"), path = NormalizeResource(Text(request, "resourcePath"), kind);
        var owners = Walk(file.GameObjects).ToArray();
        string key = request["targetKey"]?.GetValue<string>() ?? "g0";
        int index = key.StartsWith('g') && int.TryParse(key.AsSpan(1), out int n) ? n : -1;
        if (index < 0 || index >= owners.Length) throw new ArgumentException("目标对象已变化，请重新选择。");
        var owner = owners[index];
        if (kind == "effectPfb")
        {
            var manager = Component(owner, "via.effect.script.ObjectEffectManager2");
            var list = Array(manager, "ExternalDataContainers");
            var entry = Create("via.effect.script.ObjectEffectManager2.ExternalDataContainer");
            var prefab = Create("via.Prefab"); Set(prefab, "Path", path); Set(prefab, "Standby", true);
            Set(entry, "DataContainer", prefab);
            Set(entry, "DataContainerId", checked(list.Cast<RszInstance>().Select(v => Convert.ToInt32(v.GetFieldValue("DataContainerId"))).DefaultIfEmpty(0).Max() + 1));
            list.Add(entry);
        }
        else if (kind == "chain")
        {
            string cls = path.EndsWith(".chain2", StringComparison.OrdinalIgnoreCase) ? "via.motion.Chain2" : "via.motion.Chain";
            // A native component owns one chain resource. Multiple components are
            // preserved as separate slots, matching the game PFB representation.
            var existing = owner.Components.FirstOrDefault(c => c.RszClass.name == cls);
            var chain = existing?.Clone() ?? Create(cls);
            Set(chain, "ChainAsset", path); Set(chain, "Enabled", true);
            if (existing == null) {
                if (Has(chain, "BlendRate")) Set(chain, "BlendRate", 1f);
                Set(chain, "EnabledCollision", true); Set(chain, "EnabledAngleLimit", true);
            }
            owner.Components.Add(chain);
        }
        else if (kind == "clsp")
        {
            var component = Component(owner, "via.character.CollisionShapePreset");
            var item = Create("via.character.CollisionShapePresetInfo");
            Set(item, "Resource", path); Set(item, "Enabled", true);
            Array(component, "CollisionShapePresetInfos").Add(item);
        }
        else if (kind == "jcns")
        {
            var component = Component(owner, "via.motion.JointConstraints");
            var item = Create("via.motion.JointConstraintsLayer");
            Set(item, "JointConstraintsAsset", path); Set(item, "BlendRate", 1f);
            Array(component, "Layer").Add(item);
        }
        else throw new ArgumentException("不支持的 PFB 槽位类型。");
    });

    internal void Copy(JsonObject request)
    {
        Check(request); using var file = Read(content); var slot = Find(file, Text(request, "slotKey"));
        if (slot.Kind != "effect") throw new InvalidOperationException("复制槽位用于特效类型的 PFB。");
        clipboard.Element = slot.Value.Clone();
    }
    internal void Paste(JsonObject request) => Mutate(request, file =>
    {
        var copy = clipboard.Element?.Clone() ?? throw new InvalidOperationException("请先复制一个特效槽位。");
        IList? target = null;
        if (request["slotKey"]?.GetValue<string>() is string key && key.Length > 0)
        {
            var slot = Find(file, key);
            if (slot.Kind != "effect" || slot.Value.RszClass.name != copy.RszClass.name)
                throw new ArgumentException("目标与复制槽位的特效容器类型不同。");
            target = slot.List;
        }
        else target = EffectContainers(file).Where(c => ElementClass(c.Component) == copy.RszClass.name).Select(c => Array(c.Component, "Elements")).FirstOrDefault();
        if (target == null) throw new InvalidOperationException("当前文件没有与复制槽位兼容的容器。");
        if (Has(copy, "ID")) Set(copy, "ID", checked(target.Cast<RszInstance>().Where(v => Has(v,"ID")).Select(v => Convert.ToInt64(v.GetFieldValue("ID"))).DefaultIfEmpty(-1).Max() + 1));
        if (Has(copy, "GUID")) Set(copy, "GUID", Guid.NewGuid());
        target.Add(copy);
    });
    internal void Remove(JsonObject request) => Mutate(request, file => Find(file, Text(request, "slotKey")).Remove());
    internal void Set(JsonObject request) => Mutate(request, file =>
    {
        var slot = Find(file, Text(request, "slotKey"));
        var field = Fields(slot.Value).SingleOrDefault(f => f.Path == Text(request, "fieldPath")) ?? throw new ArgumentException("参数已变化，请重新选择。");
        if (!Editable(field)) throw new ArgumentException("此参数是文件结构或只读数据，不能直接改写。");
        object value = ConvertValue(request["value"] ?? throw new ArgumentException("缺少参数值。"), field.Value);
        if (field.Schema.type == RszFieldType.Resource && value is string path && path.Length > 0)
        {
            string kind = field.Schema.original_type switch {
                "via.PrefabResourceHolder" => "effectPfb", "via.motion.ChainResourceHolder" or "via.motion.Chain2ResourceHolder" => "chain",
                "via.motion.JointConstraintsResourceHolder" => "jcns", "via.character.CollisionShapePresetResourceHolder" => "clsp",
                "via.effect.EffectResourceHolder" => "efx", _ => ""
            };
            if (kind.Length > 0) value = NormalizeResource(path, kind);
            if (field.Schema.original_type == "via.motion.ChainResourceHolder" && !((string)value).EndsWith(".chain",StringComparison.OrdinalIgnoreCase) ||
                field.Schema.original_type == "via.motion.Chain2ResourceHolder" && !((string)value).EndsWith(".chain2",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Chain 与 Chain2 使用不同组件，请通过添加对应槽位切换格式。");
        }
        field.Assign(value);
        if (field.Schema.name is "ID" or "DataContainerId" && slot.List != null)
        {
            var ids = slot.List.Cast<RszInstance>().Where(v => Has(v, field.Schema.name)).Select(v => v.GetFieldValue(field.Schema.name)).ToArray();
            if (ids.Distinct().Count() != ids.Length) throw new ArgumentException("槽位编号重复；修改已取消。");
        }
    });
    internal void Undo(JsonObject request) { Check(request); if (undo.Count == 0) throw new InvalidOperationException("没有可撤销的修改。"); redo.Push(content); content = undo.Pop(); revision++; }
    internal void Redo(JsonObject request) { Check(request); if (redo.Count == 0) throw new InvalidOperationException("没有可重做的修改。"); undo.Push(content); content = redo.Pop(); revision++; }
    internal void Reset(JsonObject request) { Check(request); if (content.AsSpan().SequenceEqual(baseline)) return; undo.Push(content); content = baseline; redo.Clear(); revision++; }

    private void Mutate(JsonObject request, Action<PfbFile> action)
    {
        Check(request); using var file = Read(content);
        var references = file.GameObjectRefInfoList.Select(r => (Info: r, Source: Object(file, r.objectId), Target: r.targetId < 0 ? null : Object(file, r.targetId))).ToArray();
        action(file); file.RebuildInfoTables();
        file.GameObjectRefInfoList.Clear();
        foreach (var reference in references)
        {
            int from = file.RSZ.ObjectList.IndexOf(reference.Source);
            if (from < 0) continue;
            int to = reference.Target == null ? reference.Info.targetId : file.RSZ.ObjectList.IndexOf(reference.Target);
            if (reference.Target != null && to < 0) throw new InvalidOperationException("该槽位仍被其他组件引用，不能删除。");
            var info = reference.Info; info.objectId = from; info.targetId = to; file.GameObjectRefInfoList.Add(info);
        }
        using var stream = new MemoryStream(); using var writer = new FileHandler(stream, "edited.pfb.18");
        if (!file.WriteTo(writer, false)) throw new InvalidDataException("PFB 重建失败，原文档未改变。");
        byte[] candidate = stream.ToArray(); using var verify = Read(candidate);
        if (verify.RSZ.ObjectList.Count != file.RSZ.ObjectList.Count || verify.ResourceInfoList.Count != file.ResourceInfoList.Count)
            throw new InvalidDataException("PFB 回读校验失败，原文档未改变。");
        undo.Push(content); content = candidate; redo.Clear(); revision++;
    }
    private void Check(JsonObject request)
    {
        if (request["pfbRevision"]?.GetValue<long>() != revision) throw new InvalidOperationException("PFB 文档已变化，请刷新后重试。");
    }
    private PfbFile Read(byte[] bytes)
    {
        CheckSchema(bytes);
        var file = new PfbFile(format, new FileHandler(new MemoryStream(bytes), "document.pfb.18"));
        try { if (!file.Read()) throw new InvalidDataException("无法解析 PFB。"); file.SetupGameObjects(); return file; }
        catch { file.Dispose(); throw; }
    }
    private void CheckSchema(byte[] bytes)
    {
        if (bytes.Length < 104 || BitConverter.ToUInt32(bytes) != PfbFile.Magic) throw new InvalidDataException("不是有效的 Wilds PFB 文件。");
        long offset = BitConverter.ToInt64(bytes,48);
        if (offset < 56 || offset > bytes.Length - 48 || BitConverter.ToUInt32(bytes,(int)offset) != RSZFile.Magic) throw new InvalidDataException("PFB 数据偏移无效。");
        int count = BitConverter.ToInt32(bytes,(int)offset+12); long table = offset + BitConverter.ToInt64(bytes,(int)offset+24);
        if (count < 1 || count > 262144 || table < offset || table > bytes.Length - count * 8L) throw new InvalidDataException("PFB 类型表范围无效。");
        for (int i=0; i<count; i++)
        {
            uint id = BitConverter.ToUInt32(bytes,(int)table+i*8), crc = BitConverter.ToUInt32(bytes,(int)table+i*8+4);
            if (id == 0) continue;
            var cls = format.RszParser.GetRSZClass(id);
            if (cls == null || cls.crc != crc) throw new InvalidDataException("PFB 类型表与文件版本不一致："+(cls?.name ?? id.ToString("x8"))+"；未修改文件。");
        }
    }
    private static RszInstance Object(PfbFile file, int index) => index >= 0 && index < file.RSZ.ObjectList.Count ? file.RSZ.ObjectList[index] : throw new InvalidDataException("PFB 对象引用越界。");
    private RszInstance Create(string cls)
    {
        var definition = format.RszParser.GetRSZClass(cls) ?? throw new InvalidDataException("缺少 PFB 类型："+cls);
        var value = RszInstance.CreateInstance(format.RszParser, definition, createChildren:false);
        for (int i=0; i<value.Values.Length; i++) if (value.Values[i] == null) value.Values[i] = RszInstance.NULL;
        return value;
    }
    private RszInstance Component(PfbGameObject owner,string cls)
    {
        var component = owner.Components.FirstOrDefault(c=>c.RszClass.name==cls);
        if (component != null) return component;
        component=Create(cls); if(Has(component,"Enabled")) Set(component,"Enabled",true);
        if(cls=="via.effect.script.ObjectEffectManager2") Set(component,"DataContainer",Create("via.Prefab"));
        owner.Components.Add(component); return component;
    }
    private static string TypeOf(PfbFile file)
    {
        var names = Walk(file.GameObjects).SelectMany(g=>g.Components).Select(c=>c.RszClass.name).ToHashSet();
        if(names.Contains("app.Weapon") || names.Contains("app.CharacterEditRegion")) return "equipment";
        if(EffectContainers(file).Any()) return "effect";
        return "unknown";
    }
    private static IEnumerable<PfbGameObject> Walk(IEnumerable<PfbGameObject> roots)
    { foreach(var node in roots) { yield return node; foreach(var child in Walk(node.Children)) yield return child; } }
    private static IEnumerable<object> EquipmentComponents(PfbFile file)
    {
        int objectIndex=0;
        foreach(var owner in Walk(file.GameObjects))
        {
            var names=new List<string>();
            for(var current=owner;current!=null;current=current.Parent)names.Add(current.Name??"");
            names.Reverse();
            var classCounts=new Dictionary<string,int>(StringComparer.Ordinal);
            for(int componentIndex=0;componentIndex<owner.Components.Count;componentIndex++)
            {
                var component=owner.Components[componentIndex];string cls=component.RszClass.name;
                int classIndex=classCounts.GetValueOrDefault(cls);classCounts[cls]=classIndex+1;
                var resources=new List<object>();
                void Resource(string fieldName,string path,int index=-1,int? dataContainerId=null) =>
                    resources.Add(new {fieldName,path,index,dataContainerId});
                string PathOf(RszInstance item,string field)=>item.GetFieldValue(field)?.ToString()??"";
                if(cls is "via.motion.Chain" or "via.motion.Chain2")Resource("ChainAsset",PathOf(component,"ChainAsset"));
                else if(cls=="via.character.CollisionShapePreset")
                {
                    var list=Array(component,"CollisionShapePresetInfos");
                    for(int i=0;i<list.Count;i++)if(list[i] is RszInstance item)Resource("CollisionShapePresetInfos",PathOf(item,"Resource"),i);
                }
                else if(cls=="via.motion.JointConstraints")
                {
                    var list=Array(component,"Layer");
                    for(int i=0;i<list.Count;i++)if(list[i] is RszInstance item)Resource("Layer",PathOf(item,"JointConstraintsAsset"),i);
                }
                else if(cls=="via.effect.script.ObjectEffectManager2")
                {
                    string PrefabPath(RszInstance item,string field)=>item.GetFieldValue(field) is RszInstance prefab && prefab!=RszInstance.NULL?PathOf(prefab,"Path"):"";
                    Resource("DataContainer",PrefabPath(component,"DataContainer"));
                    var list=Array(component,"ExternalDataContainers");
                    for(int i=0;i<list.Count;i++)if(list[i] is RszInstance item)
                        Resource("ExternalDataContainers",PrefabPath(item,"DataContainer"),i,Convert.ToInt32(item.GetFieldValue("DataContainerId")));
                }
                else continue;
                yield return new {gameObjectPath=owner.GetHierarchyPath(),gameObjectNames=names.ToArray(),gameObjectIndex=objectIndex,
                    className=cls,componentIndex,classIndex,enabled=component.GetFieldValue("Enabled"),resources};
            }
            objectIndex++;
        }
    }
    private static IEnumerable<(PfbGameObject Owner,RszInstance Component)> EffectContainers(PfbFile file) =>
        Walk(file.GameObjects).SelectMany(g=>g.Components.Where(c=>c.RszClass.name.StartsWith("via.effect.script.EPV",StringComparison.Ordinal)
            && c.GetFieldValue("Elements") is IList).Select(c=>(g,c)));
    private static string ElementClass(RszInstance component) => component.Fields.First(f=>f.name=="Elements").original_type;
    private static IList Array(RszInstance value,string field) => value.GetFieldValue(field) as IList ?? throw new InvalidDataException("缺少槽位数组："+field);
    private static bool Has(RszInstance value,string field) => value.Fields.Any(f=>f.name==field);
    private static void Set(RszInstance value,string field,object data)
    {
        object? previous=value.GetFieldValue(field);
        if(previous is IConvertible && data is IConvertible) data=Convert.ChangeType(data,previous.GetType(),CultureInfo.InvariantCulture);
        if(!value.SetFieldValue(field,data)) throw new InvalidDataException("缺少 PFB 字段："+field);
    }
    private static string Text(JsonObject request,string key) => request[key]?.GetValue<string>() ?? throw new ArgumentException("缺少参数："+key);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string NormalizeResource(string value,string kind)
    {
        value=value.Replace('\\','/').Trim(); value=Regex.Replace(value,@"\.(\d+)$","");
        string suffix=kind switch { "effectPfb"=>@"pfb", "chain"=>@"chain2?", "clsp"=>"clsp", "jcns"=>"jcns", "efx"=>"efx", _=>throw new ArgumentException("未知资源类型。") };
        if(!Regex.IsMatch(value,@"\A(?:art|gamedesign)/[\w ./-]+\."+suffix+@"\z",RegexOptions.IgnoreCase)||value.Split('/').Any(s=>s is ".." or "." or ""))
            throw new ArgumentException("请填写对应类型的完整游戏资源路径（art/ 或 gamedesign/ 开头，不是本机文件路径）。");
        return value.ToLowerInvariant();
    }

    private static IEnumerable<Slot> Slots(PfbFile file)
    {
        int gi=0;
        foreach(var go in Walk(file.GameObjects))
        {
            int ci=0;
            foreach(var component in go.Components)
            {
                string key="g"+gi+"/c"+ci++, cls=component.RszClass.name, label=(go.GetHierarchyPath()??go.Name)+" · ";
                if(cls.StartsWith("via.effect.script.EPV",StringComparison.Ordinal) && component.GetFieldValue("Elements") is IList elements)
                {
                    for(int i=0;i<elements.Count;i++) if(elements[i] is RszInstance element)
                    { var item=element; yield return new(key+"/e"+i,"effect",label+(element.GetFieldValue("ID")?.ToString()??i.ToString()),element,go,elements,element,null,()=>elements.Remove(item)); }
                }
                else if(cls is "via.motion.Chain" or "via.motion.Chain2")
                { var c=component; yield return new(key,"chain",label+cls,component,go,null,component,"ChainAsset",()=>go.Components.Remove(c)); }
                else if(cls=="via.effect.script.ObjectEffectManager2")
                {
                    if(component.GetFieldValue("DataContainer") is RszInstance prefab && prefab!=RszInstance.NULL && prefab.GetFieldValue("Path") is string p && p.Length>0)
                        yield return new(key+"/primary","effectPfb",label+"主特效容器",prefab,go,null,prefab,"Path",()=>Set(prefab,"Path",""));
                    var list=Array(component,"ExternalDataContainers");
                    for(int i=0;i<list.Count;i++) if(list[i] is RszInstance item)
                    {var selected=item; yield return new(key+"/external"+i,"effectPfb",label+"外部特效容器 "+item.GetFieldValue("DataContainerId"),item,go,list,item,null,()=>list.Remove(selected));}
                }
                else if(cls is "via.character.CollisionShapePreset" or "via.motion.JointConstraints")
                {
                    bool collision=cls=="via.character.CollisionShapePreset";
                    var list=Array(component,collision?"CollisionShapePresetInfos":"Layer");
                    for(int i=0;i<list.Count;i++) if(list[i] is RszInstance item)
                    {var selected=item;yield return new(key+"/item"+i,collision?"clsp":"jcns",label+(collision?"碰撞 ":"关节约束 ")+i,item,go,list,item,collision?"Resource":"JointConstraintsAsset",()=>list.Remove(selected));}
                }
            }
            gi++;
        }
    }
    private static Slot Find(PfbFile file,string key)=>Slots(file).SingleOrDefault(s=>s.Key==key)??throw new ArgumentException("槽位已变化，请重新选择。");
    private static string Resource(Slot slot)=>slot.ResourceField!=null?slot.Value.GetFieldValue(slot.ResourceField)?.ToString()??"":Fields(slot.Value).FirstOrDefault(f=>f.Schema.type==RszFieldType.Resource)?.Value.ToString()??"";
    private static IEnumerable<Field> Fields(RszInstance value)
    {
        var seen=new HashSet<RszInstance>();
        IEnumerable<Field> WalkValue(RszInstance item,string path,string label,int depth)
        {
            if(depth>16 || item==RszInstance.NULL || item.RSZUserData!=null || !seen.Add(item))yield break;
            for(int i=0;i<item.Values.Length;i++)
            {
                int fieldIndex=i;var schema=item.Fields[i];string fieldPath=path+"f"+i, fieldLabel=label+schema.name;object data=item.Values[i];
                if(data is RszInstance nested) { foreach(var f in WalkValue(nested,fieldPath+"/",fieldLabel+".",depth+1))yield return f; }
                else if(data is IList list && data is not byte[])
                {
                    for(int j=0;j<list.Count;j++)
                    {
                        int index=j;string p=fieldPath+"/i"+j,l=fieldLabel+"["+j+"]";
                        if(list[j] is RszInstance child){foreach(var f in WalkValue(child,p+"/",l+".",depth+1))yield return f;}
                        else if(list[j]!=null)yield return new(p,l,list[j]!,schema,v=>list[index]=v);
                    }
                }
                else if(data!=null)yield return new(fieldPath,fieldLabel,data,schema,v=>item.SetFieldValue(fieldIndex,v));
            }
        }
        return WalkValue(value,"","",0);
    }
    private static string FieldType(object value)=>value switch {
        bool=>"bool",string=>"string",float or double=>"float",byte or sbyte or short or ushort or int or uint or long or ulong=>"int",
        Vector2=>"vec2",Vector3=>"vec3",Vector4 or Quaternion=>"vec4",ReeLib.via.Color=>"color",_=>"readonly"
    };
    private static bool Editable(Field field)=>FieldType(field.Value)!="readonly" && field.Schema.type!=RszFieldType.GameObjectRef;
    private static object DisplayValue(object value)=>value switch {
        Vector2 v=>new[]{v.X,v.Y},Vector3 v=>new[]{v.X,v.Y,v.Z},Vector4 v=>new[]{v.X,v.Y,v.Z,v.W},Quaternion v=>new[]{v.X,v.Y,v.Z,v.W},
        ReeLib.via.Color c=>new[]{c.R,c.G,c.B,c.A},Guid g=>g.ToString(),byte[] b=>Convert.ToHexString(b),
        bool or string or float or double or byte or sbyte or short or ushort or int or uint or long or ulong=>value,_=>value.ToString()??""
    };
    private static object ConvertValue(JsonNode input,object current)
    {
        if(current is Vector2 or Vector3 or Vector4 or Quaternion or ReeLib.via.Color)
        {
            var a=input.AsArray().Select(v=>float.Parse(v!.ToString(),CultureInfo.InvariantCulture)).ToArray();
            int count=current is Vector2?2:current is Vector3?3:4;
            if(a.Length!=count || a.Any(v=>!float.IsFinite(v)))throw new ArgumentException("向量分量数量或数值无效。");
            return current switch { Vector2=>new Vector2(a[0],a[1]),Vector3=>new Vector3(a[0],a[1],a[2]),Vector4=>new Vector4(a[0],a[1],a[2],a[3]),
                Quaternion=>new Quaternion(a[0],a[1],a[2],a[3]),_=>a.Any(v=>v<0||v>255||v!=MathF.Truncate(v))?throw new ArgumentException("RGBA 值必须为 0–255 整数。"):new ReeLib.via.Color((byte)a[0],(byte)a[1],(byte)a[2],(byte)a[3]) };
        }
        object converted=Convert.ChangeType(input.ToString(),current.GetType(),CultureInfo.InvariantCulture);
        if(converted is float f && !float.IsFinite(f) || converted is double d && !double.IsFinite(d))throw new ArgumentException("数值必须有限。");
        return converted;
    }
}
