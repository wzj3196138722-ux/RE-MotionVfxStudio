using System.Collections;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReeLib;
using ReeLib.Common;
using ReeLib.Mot;
using ReeLib.Motlist;
using ReeLib.Clip;
using ReeLib.Pfb;
using VfxStudio.Core.Models;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal sealed class ActionSlotWorkspace
{
    internal sealed class WorkingSet
    {
        internal readonly Dictionary<string,byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string,byte[]> Baselines = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<SlotEdit> Edits = [];
        internal readonly Dictionary<string,List<object>> Additions = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string,Dictionary<string,PreviewProvider>> PreviewProviders = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string,List<object>> PreviewElements = new(StringComparer.OrdinalIgnoreCase);
        internal long Revision;
    }
    internal sealed record PreviewProvider(string Key, string Path, int DataContainerId, int ExternalIndex);
    internal sealed record SlotEdit(string Id, string ActionKey, string MotionKey, string PfbKey, string PfbPath,
        int MotionId, int DataContainerId, int ExternalIndex, string ProviderPath, int Container, int ElementId,
        string ResourcePath, MotClip Clip, RszInstance Element, object Info);
    internal sealed record PreviewFile(string Kind, string OriginalPath, byte[] Content, JsonObject? Provider);
    internal sealed record Output(string Key, string Name, string Label, byte[] Content);
    private readonly RszFileOption rsz;
    private readonly Options options;
    private readonly JsonObject source;
    private readonly string motionPath, pfbPath;
    private readonly int motionId, dataContainerId, externalIndex;
    private byte[] motionBytes, pfbBytes;
    private readonly WorkingSet working;
    private readonly string motionKey, pfbKey, actionKey;
    private long revision => working.Revision;
    private readonly string? auditOutput;
    private readonly List<object> added;

    internal ActionSlotWorkspace(Options options, JsonObject proof, int prefabIndex, WorkingSet? session = null)
    {
        auditOutput = options.SelfTest ? options.Output : null;
        this.options = options;
        if (proof["proof"]?.GetValue<string>() != "manager_motion_and_prefab" ||
            string.IsNullOrWhiteSpace(proof["managerIdentity"]?.GetValue<string>()) ||
            string.IsNullOrWhiteSpace(proof["motionIdentity"]?.GetValue<string>())) throw new InvalidOperationException("缺少角色动作与 PFB 的运行时关联证据。");
        var prefabs = proof["prefabs"]?.AsArray() ?? throw new ArgumentException("Missing verified prefabs.");
        if (prefabIndex < 0 || prefabIndex >= prefabs.Count) throw new ArgumentOutOfRangeException(nameof(prefabIndex));
        var prefab = prefabs[prefabIndex]!;
        if (prefab["proof"]?.GetValue<string>() != "live_prefab_path" || string.IsNullOrEmpty(prefab["prefabIdentity"]?.GetValue<string>()))
            throw new InvalidOperationException("PFB 路径未经游戏对象核实。");
        source = (JsonObject)proof.DeepClone();
        motionPath = EditorEngine.Text(proof, "bankPath"); pfbPath = prefab["path"]!.GetValue<string>();
        motionId = proof["motionId"]!.GetValue<int>(); dataContainerId = prefab["dataContainerId"]!.GetValue<int>();
        externalIndex=prefab["externalIndex"]!.GetValue<int>();
        string schema=Path.Combine(options.Assets,"Lists","rsz_pfb_classes.json");
        if (!File.Exists(schema)) schema=Path.Combine(AppContext.BaseDirectory,"formats","rsz_pfb_classes.json");
        rsz = new RszFileOption(GameName.mhwilds, RszParser.GetInstance(schema));
        var reader = new GameInstallResourceReader(options);
        var motionSource = reader.ReadActionResource(motionPath, "motlist");
        var pfbSource = reader.ReadActionResource(pfbPath, "pfb");
        source["motionOrigin"] = motionSource.Provenance(); source["pfbOrigin"] = pfbSource.Provenance();
        motionBytes = motionSource.Content; pfbBytes = pfbSource.Content;
        working = session ?? new();
        motionKey = motionSource.NativePath + "|" + motionSource.ContentHash;
        pfbKey = pfbSource.NativePath + "|" + pfbSource.ContentHash;
        working.Files.TryAdd(motionKey, motionBytes); working.Files.TryAdd(pfbKey, pfbBytes);
        working.Baselines.TryAdd(motionKey,motionBytes); working.Baselines.TryAdd(pfbKey,pfbBytes);
        actionKey = motionKey + "|" + pfbKey + "|" + motionId;
        if (!working.Additions.TryGetValue(actionKey,out added!)) working.Additions[actionKey] = added = [];
        Refresh();
        using var list = ReadMotion(motionBytes);
        var selected = Select(list);
        if (selected.MotFile is not MotFile) throw new InvalidOperationException("此动作是外链或混合树，尚不能确认独立帧范围；没有修改任何文件。");
        using var pfb = ReadPfb(pfbBytes);
        if (!Providers(pfb).Any()) throw new InvalidOperationException("该 PFB 没有可写入动作槽位的标准特效容器。");
    }
    private void Refresh() { motionBytes=working.Files[motionKey]; pfbBytes=working.Files[pfbKey]; }

    private MotlistFile ReadMotion(byte[] bytes)
    {
        var value = new MotlistFile(new FileHandler(new MemoryStream(bytes), motionPath + ".992"));
        if (!value.Read() || value.Header.version != MotlistVersion.MHWILDS) { value.Dispose(); throw new InvalidDataException("动作表格式不受支持。"); }
        return value;
    }
    private PfbFile ReadPfb(byte[] bytes)
    {
        var value = new PfbFile(rsz, new FileHandler(new MemoryStream(bytes), pfbPath + ".18"));
        try
        {
            if (!value.Read()) throw new InvalidDataException("PFB 解析失败。");
            foreach (var instance in value.RSZ.InstanceInfoList.Where(i=>i.typeId!=0))
            {
                var definition=rsz.RszParser.GetRSZClass(instance.typeId);
                if (definition==null || definition.crc!=instance.CRC)
                    throw new InvalidDataException("PFB 类型表与文件版本不一致："+(definition?.name ?? instance.typeId.ToString())+"。请更新类型表后再编辑；未修改文件。");
            }
            value.SetupGameObjects(); return value;
        }
        catch { value.Dispose(); throw; }
    }
    private MotIndex Select(MotlistFile list) => list.Motions.Single(m => m.motNumber == motionId);
    private static IEnumerable<(string Path, int Container, RszInstance Component)> Providers(PfbFile pfb)
    {
        IEnumerable<PfbGameObject> Walk(IEnumerable<PfbGameObject> roots)
        { foreach (var go in roots) { yield return go; foreach (var child in Walk(go.Children)) yield return child; } }
        foreach (var go in Walk(pfb.GameObjects))
        {
            var match = Regex.Match(go.Name ?? "", @"^(\d+)(?:_|$)");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int container)) continue;
            foreach (var component in go.Components.Where(c => c.RszClass.name == "via.effect.script.EPVStandardData"))
                yield return (go.GetHierarchyPath()!, container, component);
        }
    }
    private static IList Elements(RszInstance component) => component.GetFieldValue("Elements") as IList ?? throw new InvalidDataException("标准特效容器没有元素列表。");
    private static int Id(RszInstance element) => checked(Convert.ToInt32(element.GetFieldValue("ID")));
    private static void Set(RszInstance element, string field, object value)
    {
        object current = element.GetFieldValue(field) ?? throw new InvalidDataException("缺少 PFB 字段：" + field);
        if (!element.SetFieldValue(field, current is IConvertible && value is IConvertible ? Convert.ChangeType(value, current.GetType()) : value))
            throw new InvalidDataException("无法写入 PFB 字段：" + field);
    }
    private static byte[] Serialize(BaseFile file, string path)
    {
        using var stream = new MemoryStream(); using var handler = new FileHandler(stream, path);
        if (!file.WriteTo(handler, false)) throw new InvalidDataException("文件重建失败，修改已取消。");
        return stream.ToArray();
    }
    internal object Info()
    {
        Refresh();
        using var list = ReadMotion(motionBytes); var motion = (MotFile)Select(list).MotFile!;
        using var pfb = ReadPfb(pfbBytes);
        int slotCounter=0;
        return new { revision, motionId, motionPath, pfbPath, dataContainerId, name = motion.Name,
            frameCount = motion.Header.frameCount, source, added,
            independentLifetimeAvailable=source["effectEndTypes"]?["Invalid"]!=null && source["effectEndTypes"]?["Kill"]!=null,
            previewSlotCount=working.Edits.Count(e=>e.MotionKey==motionKey), totalAddedSlots=working.Edits.Count,
            existingSlots = new MotionVfxSlotManager().ExtractSlotsFromMot(motion,ref slotCounter),
            templates = Providers(pfb).SelectMany(p => Elements(p.Component).Cast<RszInstance>().Select(e => new {
                key = p.Path + "#" + Id(e), container = p.Container, element = Id(e), name = p.Path,
                resources = (e.GetFieldValue("Resources") as IList)?.Cast<object>().Select(v => v.ToString()).ToArray(),
                joint = e.GetFieldValue("JointName")?.ToString(), relationChildId = e.GetFieldValue("RelationChildId"),
                relationType = e.GetFieldValue("RelationType"), delayFrame = e.GetFieldValue("DelayFrame"), lifeFrame = e.GetFieldValue("LifeFrame") })).ToArray() };
    }
    internal void Add(JsonObject request)
    {
        Refresh();
        if (request["actionRevision"]?.GetValue<long>() != revision) throw new InvalidOperationException("动作文档已变化，请刷新后重试。");
        float start = request["startFrame"]!.GetValue<float>(), end = request["endFrame"]!.GetValue<float>();
        using var list = ReadMotion(motionBytes); var selected = Select(list); var motion = (MotFile)selected.MotFile!;
        if (!float.IsFinite(start) || !float.IsFinite(end) || start < 0 || end <= start || end > motion.Header.frameCount)
            throw new ArgumentException("起止帧必须在动作范围内，且结束帧大于出现帧。");
        string lifetimeMode=request["lifetimeMode"]?.GetValue<string>() ?? "action";
        if(lifetimeMode is not ("action" or "independent"))throw new ArgumentException("未知特效寿命方式。");
        bool independent=lifetimeMode=="independent";
        float lifetimeFrames=independent ? request["lifetimeFrames"]?.GetValue<float>() ?? 0 : -1;
        if(independent && (!float.IsFinite(lifetimeFrames) || lifetimeFrames<=0))
            throw new ArgumentException("独立寿命必须是大于 0 的有限帧数，与动作长度无关。");
        int killEndType=source["killEndType"]?.GetValue<int>() ?? throw new InvalidOperationException("缺少引擎特效结束类型，请重新核实动作来源。");
        int trackEndType=killEndType;
        if(independent)
        {
            var types=source["effectEndTypes"];
            if(types?["Invalid"]==null || types["Kill"]?.GetValue<int>()!=killEndType)
                throw new InvalidOperationException("尚未读取引擎的独立寿命类型，请重新核实并打开动作 PFB。");
            trackEndType=types["Invalid"]!.GetValue<int>();
            if(trackEndType==killEndType)throw new InvalidDataException("引擎结束类型冲突，未创建槽位。");
        }
        string resource = ResourceResolver.Normalize(EditorEngine.Text(request, "resourcePath"));
        string templateKey = EditorEngine.Text(request, "templateKey");
        string group = request["groupName"]?.GetValue<string>() ?? "";
        string joint = request["jointName"]?.GetValue<string>() ?? "";
        JsonNode? attachment = null;
        if (request["attachmentTargetKey"] is JsonNode attachmentKey)
        {
            attachment = source["attachmentTargets"]?.AsArray().SingleOrDefault(t => t?["key"]?.GetValue<string>() == attachmentKey.GetValue<string>())
                ?? throw new InvalidOperationException("挂载对象不属于已核实的当前角色或武器，请重新核实动作来源。");
            string kind = attachment["kind"]?.GetValue<string>() ?? "";
            int childId = attachment["relationChildId"]?.GetValue<int>() ?? throw new InvalidDataException("挂载对象缺少 RelationChildId。");
            if (kind is not ("character" or "weapon") || (kind == "character" && childId != -1) || (kind == "weapon" && childId < 0) ||
                string.IsNullOrWhiteSpace(attachment["gameObjectIdentity"]?.GetValue<string>()))
                throw new InvalidDataException("挂载对象关系未核实，不创建槽位。");
            if (joint.Length > 0 && attachment["joints"]?.AsArray().Any(j => j?.GetValue<string>() == joint) != true)
                throw new InvalidOperationException("所选骨骼不属于此挂载对象，请从当前骨骼列表选择。");
        }
        var efxSource = new GameInstallResourceReader(options).ReadActionResource(resource,"efx");
        var efx = new EfxEditorService().Load(efxSource.Content,resource+".5571972");
        try
        {
            if (group.Length>0 && !efx.EffectGroups.Any(g=>g.groupName==group))
                throw new ArgumentException("指定的 Group 名称不在调用的 EFX 中，请填写文件原名，而不是编辑器备注。");
        }
        finally { efx.FileHandler.Dispose(); }
        using var pfb = ReadPfb(pfbBytes);
        byte[] motionBefore = motionBytes;
        pfb.RebuildInfoTables();
        byte[] pfbBefore = Serialize(pfb, pfbPath + ".18");
        var choices = Providers(pfb).SelectMany(p => Elements(p.Component).Cast<RszInstance>()
            .Select(e => (Provider:p, Element:e))).Where(t => t.Provider.Path + "#" + Id(t.Element) == templateKey).ToArray();
        if (choices.Length != 1) throw new InvalidOperationException("请选择当前已核实 PFB 中唯一的挂接模板。");
        var choice = choices[0]; var elements = Elements(choice.Provider.Component);
        var clone = choice.Element.Clone();
        int nextId = checked(elements.Cast<RszInstance>().Select(Id).DefaultIfEmpty(-1).Max() + 1);
        Set(clone, "ID", nextId); Set(clone, "GUID", Guid.NewGuid());
        var resources = (IList)clone.GetFieldValue("Resources")!; resources.Clear(); resources.Add(resource);
        Set(clone, "JointName", joint);
        if (attachment != null)
        {
            Set(clone, "RelationChildId", attachment["relationChildId"]!.GetValue<int>());
            Set(clone, "ParentDegree", attachment["parentDegree"]?.GetValue<int>() ?? 0);
            string attachmentMode = request["attachmentMode"]?.GetValue<string>() ?? (independent ? "initial" : "follow");
            string relationName = attachmentMode switch { "initial" => "InitializationParent", "follow" => "FollowParent", _ => throw new ArgumentException("未知挂载方式。") };
            int relationType = source["attachmentRelationTypes"]?[relationName]?.GetValue<int>()
                ?? throw new InvalidOperationException("未读取到所选挂载方式的引擎类型，请重新核实来源。");
            Set(clone, "RelationType", relationType);
        }
        // The chosen template supplies attachment/render semantics; requests
        // cannot silently inherit cached particles or timed lifetime overrides.
        Set(clone, "EffectCache", false); Set(clone, "IgnoreEffectCache", true);
        Set(clone, "LifeFrame", lifetimeFrames); Set(clone, "IsLanding", false); Set(clone, "IsUseTerrainNormal", false);
        if(independent)
        {
            // Each invocation owns a finite lifetime in the exported PFB.
            // Disable template-level respawn and motion-dependent aging.
            Set(clone,"EndType",killEndType);Set(clone,"LoopFrame",0);Set(clone,"LoopDispersionFrame",0);
            Set(clone,"RelationMotionSpeed",false);
        }
        var names = (IList)clone.GetFieldValue("GroupNameParameters")!; names.Clear();
        if (group.Length > 0)
        {
            var groupInstance = pfb.RSZ.CreateInstance("via.effect.script.GroupNameParameter");
            Set(groupInstance, "Enable", true); Set(groupInstance, "GroupNameLv0", group); Set(groupInstance, "GroupNameLv1", "");
            names.Add(groupInstance);
        }
        elements.Add(clone); pfb.RebuildInfoTables();
        string effectId = choice.Provider.Container + "-" + nextId;
        // MOTLIST-specific additional clips avoid changing a shared MOT used
        // by another action. DataContainerId is the external provider selector,
        // and is deliberately distinct from the first component of EffectId.
        // Reuse the native VFX category from this verified bank. The default
        // category 0 belongs to other motion events (this bank uses 2 for VFX).
        var categories = list.Motions.SelectMany(m => (m.MotFile as MotFile)?.Clips ?? [])
            .Where(c => c.ClipEntry.Tracks.Any(t => t.Name == "via.effect.script.VFXRangeTrack"))
            .Select(c => c.category).Distinct().ToArray();
        if (categories.Length != 1) throw new InvalidDataException("无法从当前动作表唯一核实原生特效轨道类别，未添加槽位。");
        var clip = new MotClip { category = categories[0] }; clip.ClipEntry.Version = ClipVersion.MHWilds; clip.ClipEntry.FrameCount = motion.Header.frameCount; clip.ClipEntry.Guid = Guid.NewGuid();
        // The first track is the native hierarchy root, not an executable
        // motion event. A lone VFX track was being serialized as that root,
        // with no child event and no MotClip TrackData entry for the engine.
        clip.ClipEntry.Tracks.Add(new ClipTrack(ClipVersion.MHWilds) { Name = "Root" });
        using var shell = new MotFile(new FileHandler(new MemoryStream(), "slot.mot.932")); shell.Clips.Add(clip);
        var slot = new VfxSlot { EffectId = effectId, ContainerId = dataContainerId, ElementId = nextId, StartFrame = start, EndFrame = end, JointName = joint };
        new MotionVfxSlotManager().AddSlot(shell, slot);
        var endType=new Property(ClipVersion.MHWilds) { Info={FunctionName="EndType",DataType=PropertyType.S32,KeyType=3,startFrame=start,endFrame=end},
            Keys=[new NoHermiteKey(ClipVersion.MHWilds) {PropertyType=PropertyType.S32,frame=start,Value=trackEndType}] };
        clip.ClipEntry.Tracks.Single(t => t.Name == "via.effect.script.VFXRangeTrack").Properties.Add(endType);
        if(independent)clip.ClipEntry.Tracks.Single(t=>t.Name=="via.effect.script.VFXRangeTrack").Properties.Add(
            new Property(ClipVersion.MHWilds){Info={FunctionName="IsRequestEndOfMotion",DataType=PropertyType.Bool,KeyType=1,startFrame=start,endFrame=end},
                Keys=[new BoolKey(ClipVersion.MHWilds){PropertyType=PropertyType.Bool,frame=start,value=0}]});
        clip.TrackData.Add(new TracksData { Version = ClipVersion.MHWilds });
        // Also sets the version of the trailing property data. Leaving it at
        // zero writes a 16-byte older header where Wilds expects 24 bytes.
        clip.ChangeVersion(ClipVersion.MHWilds);
        if (selected.OverrideClips.Count >= 255) throw new InvalidOperationException("动作追加轨道已达格式上限。");
        byte[] nextMotion = AppendOverrideClip(motionBytes, selected, clip), nextPfb = Serialize(pfb, pfbPath + ".18");
        using var checkMotion = ReadMotion(nextMotion); using var checkPfb = ReadPfb(nextPfb);
        var newClip = Select(checkMotion).OverrideClips.Last();
        var track = newClip.ClipEntry.Tracks.Single(t => t.Name == "via.effect.script.VFXRangeTrack");
        if (newClip.category != categories[0] || newClip.ClipEntry.Tracks.Count != 2 || newClip.ClipEntry.Tracks[0].Name != "Root" ||
            !newClip.ClipEntry.Tracks[0].ChildTracks.Contains(track) || newClip.TrackData.Count != 1 ||
            newClip.TrackData[0].Version != ClipVersion.MHWilds || newClip.ClipEntry.ExtraPropertyData.Version != ClipVersion.MHWilds)
            throw new InvalidDataException("新增动作槽位的根轨道、子轨道或版本结构无效，修改已取消。");
        var effect = track.Properties.Single(p => p.Info.FunctionName == "EffectId");
        var selector = track.Properties.Single(p => p.Info.FunctionName == "DataContainerId");
        var endCheck = track.Properties.Single(p => p.Info.FunctionName == "EndType");
        if (track.Properties.Any(p => p.Info.wildsNameHash == 0 || p.Info.wildsNameHash != MurMur3HashUtils.GetAsciiHash(p.Info.FunctionName)))
            throw new InvalidDataException("新增轨道的原生字段标识无效，修改已取消。");
        if (((NoHermiteKey)effect.Keys![0]).Value.ToString() != effectId || effect.Info.startFrame != start || effect.Info.endFrame != end ||
            Convert.ToInt32(((NoHermiteKey)selector.Keys![0]).Value) != dataContainerId ||
            Convert.ToInt32(((NoHermiteKey)endCheck.Keys![0]).Value) != trackEndType) throw new InvalidDataException("动作槽位回读不一致。");
        var providerCheck = Providers(checkPfb).Single(p => p.Path == choice.Provider.Path);
        var newElement = Elements(providerCheck.Component).Cast<RszInstance>().Single(e => Id(e) == nextId);
        if (((IList)newElement.GetFieldValue("Resources")!)[0]?.ToString() != resource) throw new InvalidDataException("PFB 资源回读不一致。");
        if (attachment != null && (Convert.ToInt32(newElement.GetFieldValue("RelationChildId")) != attachment["relationChildId"]!.GetValue<int>() ||
            newElement.GetFieldValue("JointName")?.ToString() != joint)) throw new InvalidDataException("挂载对象或骨骼回读不一致，未提交新增槽位。");
        if(independent && (Convert.ToSingle(newElement.GetFieldValue("LifeFrame"))!=lifetimeFrames ||
            Convert.ToInt32(newElement.GetFieldValue("EndType"))!=killEndType ||
            Convert.ToUInt32(newElement.GetFieldValue("LoopFrame"))!=0 || Convert.ToUInt32(newElement.GetFieldValue("LoopDispersionFrame"))!=0 ||
            Convert.ToBoolean(newElement.GetFieldValue("RelationMotionSpeed")) ||
            ((BoolKey)track.Properties.Single(p=>p.Info.FunctionName=="IsRequestEndOfMotion").Keys![0]).value!=0))
            throw new InvalidDataException("独立寿命/动作结束设置回读不一致。");
        // Remove only the additions from reopened copies, then compare complete
        // canonical files. This detects changes to any other action, provider,
        // animation data, metadata or reference, rather than checking counts.
        Elements(providerCheck.Component).Remove(newElement); checkPfb.RebuildInfoTables();
        byte[] motionRestored = nextMotion[..motionBefore.Length];
        Array.Copy(motionBefore, selected.Start, motionRestored, selected.Start, 8);
        motionRestored[checked((int)selected.Start + 23)] = motionBefore[checked((int)selected.Start + 23)];
        bool motionSame = motionBefore.AsSpan().SequenceEqual(motionRestored);
        bool pfbSame = pfbBefore.AsSpan().SequenceEqual(Serialize(checkPfb, pfbPath + ".18"));
        if (!motionSame || !pfbSame)
        {
            if (auditOutput != null)
            {
                File.WriteAllBytes(Path.Combine(auditOutput,"motion-before.motlist.992"),motionBefore);
                File.WriteAllBytes(Path.Combine(auditOutput,"motion-restored.motlist.992"),motionRestored);
            }
            throw new InvalidDataException($"移除新增内容后的文件与原文件不一致（动作={motionSame}, PFB={pfbSame}），修改已取消，未导出部分结果。");
        }
        // Commit both only after both can be reopened and cross-referenced.
        motionBytes = nextMotion; pfbBytes = nextPfb;
        working.Files[motionKey]=nextMotion; working.Files[pfbKey]=nextPfb; working.Revision++;
        if (!working.PreviewProviders.TryGetValue(motionKey,out var providers))
            working.PreviewProviders[motionKey]=providers=new(StringComparer.OrdinalIgnoreCase);
        providers[pfbKey+"|"+dataContainerId+"|"+externalIndex]=new(pfbKey,pfbPath,dataContainerId,externalIndex);
        if (!working.PreviewElements.TryGetValue(pfbKey,out var previewElements)) working.PreviewElements[pfbKey]=previewElements=[];
        previewElements.Add(new {container=choice.Provider.Container,element=nextId,resourcePath=resource});
        string slotId=Guid.NewGuid().ToString("N");
        var slotInfo=new { id=slotId, effectId, templateKey, resourcePath = resource, groupName = group, jointName = joint, startFrame = start, endFrame = end,
            lifetimeMode,lifetimeFrames,trackEndType,attachmentTargetKey=request["attachmentTargetKey"]?.GetValue<string>(),
            relationChildId=Convert.ToInt32(clone.GetFieldValue("RelationChildId")) };
        added.Add(slotInfo);
        working.Edits.Add(new(slotId,actionKey,motionKey,pfbKey,pfbPath,motionId,dataContainerId,externalIndex,
            choice.Provider.Path,choice.Provider.Container,nextId,resource,clip,clone,slotInfo));
    }
    internal void Remove(long expectedRevision,string slotId)
    {
        if(expectedRevision!=revision)throw new InvalidOperationException("动作文档已变化，请刷新后重试。");
        var removed=working.Edits.SingleOrDefault(e=>e.Id==slotId && e.ActionKey==actionKey)
            ?? throw new InvalidOperationException("新增槽位不存在或不属于当前动作文档。");
        Rebuild(working.Edits.Where(e=>e!=removed).ToArray());
    }
    internal void Reset(long expectedRevision)
    {
        if(expectedRevision!=revision)throw new InvalidOperationException("动作文档已变化，请刷新后重试。");
        Rebuild([]);
    }
    private void Rebuild(SlotEdit[] survivors)
    {
        // Replay only retained additions onto exact baselines. This physically
        // removes deleted CLIP/PFB data, including orphan strings/RSZ records,
        // without re-encoding existing animation transforms or renumbering IDs.
        var files=new Dictionary<string,byte[]>(working.Baselines,StringComparer.OrdinalIgnoreCase);
        foreach(var edit in survivors)
        {
            using var list=ReadMotion(files[edit.MotionKey]);
            var selected=list.Motions.Single(m=>m.motNumber==edit.MotionId);
            files[edit.MotionKey]=AppendOverrideClip(files[edit.MotionKey],selected,edit.Clip);
            using var pfb=ReadPfb(files[edit.PfbKey]);
            var provider=Providers(pfb).Single(p=>p.Path==edit.ProviderPath);
            if(Elements(provider.Component).Cast<RszInstance>().Any(e=>Id(e)==edit.ElementId))
                throw new InvalidDataException("保留槽位编号与原始 PFB 冲突，删除已取消。");
            Elements(provider.Component).Add(edit.Element.Clone());pfb.RebuildInfoTables();
            files[edit.PfbKey]=Serialize(pfb,edit.PfbPath+".18");
            using var checkMotion=ReadMotion(files[edit.MotionKey]);
            using var checkPfb=ReadPfb(files[edit.PfbKey]);
            if(checkMotion.Motions.Single(m=>m.motNumber==edit.MotionId).OverrideClips.Count!=selected.OverrideClips.Count+1 ||
                !Elements(Providers(checkPfb).Single(p=>p.Path==edit.ProviderPath).Component).Cast<RszInstance>().Any(e=>Id(e)==edit.ElementId))
                throw new InvalidDataException("删除后的动作与 PFB 回读失败，原编辑已保留。");
        }
        // Commit the entire working set only after every dependent file reads.
        working.Files.Clear();foreach(var file in files)working.Files.Add(file.Key,file.Value);
        foreach(var group in working.Additions.Values)group.Clear();
        working.PreviewProviders.Clear();working.PreviewElements.Clear();
        working.Edits.Clear();working.Edits.AddRange(survivors);
        foreach(var edit in survivors)
        {
            working.Additions[edit.ActionKey].Add(edit.Info);
            if(!working.PreviewProviders.TryGetValue(edit.MotionKey,out var providers))working.PreviewProviders[edit.MotionKey]=providers=new(StringComparer.OrdinalIgnoreCase);
            providers[edit.PfbKey+"|"+edit.DataContainerId+"|"+edit.ExternalIndex]=new(edit.PfbKey,edit.PfbPath,edit.DataContainerId,edit.ExternalIndex);
            if(!working.PreviewElements.TryGetValue(edit.PfbKey,out var elements))working.PreviewElements[edit.PfbKey]=elements=[];
            elements.Add(new {container=edit.Container,element=edit.ElementId,resourcePath=edit.ResourcePath});
        }
        working.Revision++;Refresh();
    }
    internal PreviewFile[] PreparePreview(long expectedRevision, JsonObject observed)
    {
        Refresh();
        if (expectedRevision!=revision) throw new InvalidOperationException("动作文档已变化，请刷新后再热重载。");
        foreach (string field in new[]{"sceneEpoch","actorIdentity","managerIdentity","motionIdentity","motionId"})
            if (source[field]?.ToJsonString()!=observed[field]?.ToJsonString())
                throw new InvalidOperationException("角色、动作或管理器已变化，请重新核实动作来源。");
        if (!string.Equals(motionPath,EditorEngine.Text(observed,"bankPath"),StringComparison.OrdinalIgnoreCase) || observed["bankId"]==null)
            throw new InvalidOperationException("动作表或动作库编号未经当前游戏核实。");
        if (!working.PreviewProviders.TryGetValue(motionKey,out var providers) || providers.Count==0)
            throw new InvalidOperationException("还没有可预览的新增槽位。");
        var files=new List<PreviewFile>{new("motlist",motionPath,motionBytes.ToArray(),null)};
        // A MOTLIST may contain additions targeting both the character and a
        // weapon PFB. Publish every modified provider needed by this bank.
        foreach(var provider in providers.Values.OrderBy(p=>p.Path,StringComparer.OrdinalIgnoreCase))
        {
            var matches=observed["prefabs"]!.AsArray().Where(p=>p?["proof"]?.GetValue<string>()=="live_prefab_path"
                && string.Equals(p["path"]?.GetValue<string>(),provider.Path,StringComparison.OrdinalIgnoreCase)
                && p["dataContainerId"]?.GetValue<int>()==provider.DataContainerId
                && p["externalIndex"]?.GetValue<int>()==provider.ExternalIndex).ToArray();
            if(matches.Length!=1)throw new InvalidOperationException("当前角色已不再引用所编辑的 PFB："+provider.Path);
            var liveProvider=(JsonObject)matches[0]!.DeepClone();
            liveProvider["expectedElements"]=JsonSerializer.SerializeToNode(working.PreviewElements[provider.Key]);
            files.Add(new("pfb",provider.Path,working.Files[provider.Key].ToArray(),liveProvider));
        }
        return files.ToArray();
    }
    internal Output[] Export()
    {
        Refresh();
        if (!working.PreviewProviders.ContainsKey(motionKey)) throw new InvalidOperationException("还没有添加动作特效槽位。");
        var outputs=new List<Output>{new("motion",Path.GetFileName(motionPath)+".992","动作文件",motionBytes.ToArray())};
        // One bank can reference additions in both character and weapon PFBs.
        // Offer each modified dependency its own destination dialog.
        var providers=working.PreviewProviders[motionKey].Values.DistinctBy(p=>p.Key,StringComparer.OrdinalIgnoreCase)
            .OrderBy(p=>p.Path,StringComparer.OrdinalIgnoreCase).ToArray();
        for(int i=0;i<providers.Length;i++)
        {
            var provider=providers[i];
            outputs.Add(new(providers.Length==1?"pfb":"pfb_"+i,Path.GetFileName(provider.Path)+".18",
                "对应 PFB · "+Path.GetFileName(provider.Path),working.Files[provider.Key].ToArray()));
        }
        return outputs.ToArray();
    }
    private static byte[] AppendOverrideClip(byte[] original, MotIndex selected, MotClip clip)
    {
        // Re-encoding MOT transforms introduces quantization drift. Preserve the
        // whole existing file and update only this MOTLIST row's pointer/count.
        // The original additional clips are referenced verbatim in their order.
        int row = checked((int)selected.Start), count = selected.OverrideClips.Count;
        if (row < 0 || row + 24 > original.Length || original[row + 23] != count)
            throw new InvalidDataException("动作索引位置或轨道数量不一致。");
        long oldArray = BitConverter.ToInt64(original, row);
        if (count > 0 && (oldArray < 0 || oldArray + count * 8L > original.Length)) throw new InvalidDataException("原动作轨道指针超出文件范围。");
        using var stream = new MemoryStream(); stream.Write(original);
        using var handler = new FileHandler(stream, "action.motlist.992");
        handler.Seek(original.Length); handler.Align(16);
        long array = handler.Tell();
        for (int i = 0; i < count; i++) handler.Write(BitConverter.ToInt64(original, checked((int)oldArray + i * 8)));
        handler.Write(0L); handler.Align(16);
        long offset = handler.Tell();
        if (!clip.Write(handler.WithOffset(offset))) throw new InvalidDataException("新增动作轨道写入失败。");
        handler.Write(array + count * 8L, offset);
        handler.Write(row, array); handler.Write(row + 23, checked((byte)(count + 1)));
        return stream.ToArray();
    }
}
