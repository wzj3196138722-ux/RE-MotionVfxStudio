using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReeLib;
using ReeLib.Common;
using ReeLib.Efx;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal sealed partial class EditorEngine : IDisposable
{
    private readonly Options options;
    private readonly IGameCacheResourceReader cacheReader;
    private readonly GroupNoteStore groupNotes;
    private readonly EfxEditorService service = new();
    private readonly Dictionary<string, (string Payload, JsonObject Response)> responses = new();
    private readonly HashSet<string> processedIds = new(StringComparer.Ordinal);
    private readonly List<byte[]> undo = [], redo = [];
    private readonly string session = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8];
    private EfxFile? document;
    private string? source;
    private string sourceKind = "local_file";
    private CacheSourceDescriptor? cacheSource;
    private JsonObject? gameSource;
    private byte[]? bytes;
    private string? savedHash;
    private string? warning;
    private long revision;
    private int publishCount;
    private PrecacheStore? precache;
    private PrecacheStore Precache => precache ??= new(options);
    private DesktopBridge? desktop;
    private ActionSlotWorkspace? actionWorkspace;
    private ActionSlotWorkspace.WorkingSet actionWorkingSet = new();
    public bool ExitRequested { get; private set; }

    public EditorEngine(Options options, IGameCacheResourceReader? cacheReader = null)
    {
        this.options = options;
        this.cacheReader = cacheReader ?? new UnavailableGameCacheResourceReader();
        groupNotes = new GroupNoteStore(options.Mailbox);
        // A restarted mailbox must never execute the last mutation again merely because request.json remains present.
        try
        {
            string responsePath = Path.Combine(options.Mailbox, "response.json");
            string requestPath = Path.Combine(options.Mailbox, "request.json");
            if (File.Exists(responsePath) && File.Exists(requestPath))
            {
                var oldRequest = JsonNode.Parse(File.ReadAllText(requestPath))!.AsObject();
                var oldResponse = JsonNode.Parse(File.ReadAllText(responsePath))!.AsObject();
                string id = Text(oldRequest, "id");
                if (oldResponse["id"]?.GetValue<string>() == id)
                    responses[id] = (oldRequest.ToJsonString(), new JsonObject
                    {
                        ["id"] = id, ["ok"] = false, ["revision"] = 0,
                        ["error"] = "Backend restarted. Previous request was already processed and has not been repeated. Reopen the source file or saved copy."
                    });
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
    }

    public JsonObject Handle(JsonObject request)
    {
        string id;
        try { id = Text(request, "id"); if (id.Length is < 1 or > 200) throw new ArgumentException("Invalid request id."); }
        catch (Exception ex) { return new JsonObject { ["id"] = "", ["ok"] = false, ["error"] = ex.Message }; }
        string payload = request.ToJsonString();
        if (responses.TryGetValue(id, out var previous))
        {
            if (previous.Payload != payload) return new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = "Request id was reused with different content." };
            return (JsonObject)previous.Response.DeepClone();
        }
        if (processedIds.Contains(id)) return new JsonObject { ["id"] = id, ["ok"] = false, ["revision"] = revision, ["error"] = "Request id has already been processed; its cached response has expired. Use a new id." };
        var response = new JsonObject { ["id"] = id, ["ok"] = true };
        try
        {
            string action = Text(request, "action").ToLowerInvariant();
            if (action is "duplicate_entry" or "remove_entry" or "restore_entry" or "add_entry" or "rename_group" or "set_group_note") RejectEmbeddedRootOperation(request);
            if (!action.StartsWith("pfb_", StringComparison.Ordinal) && action is not ("catalog" or "resolve_resource" or "open" or "open_motion" or "open_cached_resource" or "open_game_resource" or "status" or "inspect" or "shutdown" or
                "cache_list" or "cache_import" or "cache_reload" or "cache_rename" or "cache_materialize" or "desktop_start" or "desktop_stop" or "file_browse" or "file_stat" or "open_action" or "action_effect_groups" or "add_action_slot" or "remove_action_slot" or "reset_action_slots" or "publish_action_preview" or "begin_export" or "prepare_export" or "finish_export")) CheckRevision(request);
            switch (action)
            {
                case "status": break;
                case "file_browse": response["fileBrowser"] = JsonSerializer.SerializeToNode(FileBrowser.Browse(options, request), Host.Json); break;
                case "file_stat": response["fileStat"] = JsonSerializer.SerializeToNode(FileBrowser.Stat(Text(request, "path")), Host.Json); break;
                case "pfb_open": case "pfb_add": case "pfb_copy": case "pfb_paste": case "pfb_set":
                case "pfb_remove": case "pfb_undo": case "pfb_redo": case "pfb_reset": case "pfb_publish":
                    PrefabOperation(action, request, response); break;
                case "desktop_start":
                    response["desktop"] = JsonSerializer.SerializeToNode((desktop ??= new(options)).Start(request), Host.Json); break;
                case "desktop_stop":
                    response["desktop"] = JsonSerializer.SerializeToNode((desktop ??= new(options)).Stop(), Host.Json); break;
                case "cache_list":
                    response["cache"] = JsonSerializer.SerializeToNode(Precache.List(), Host.Json); break;
                case "cache_import":
                    response["cacheEntry"] = JsonSerializer.SerializeToNode(Precache.Import(Text(request, "path"), OptionalText(request, "alias")), Host.Json);
                    response["cache"] = JsonSerializer.SerializeToNode(Precache.List(), Host.Json); break;
                case "cache_reload":
                    response["cacheEntry"] = JsonSerializer.SerializeToNode(Precache.Reload(Text(request, "entryId")), Host.Json);
                    response["cache"] = JsonSerializer.SerializeToNode(Precache.List(), Host.Json); break;
                case "cache_rename":
                    response["cacheEntry"] = JsonSerializer.SerializeToNode(Precache.Rename(Text(request, "entryId"), Text(request, "alias")), Host.Json);
                    response["cache"] = JsonSerializer.SerializeToNode(Precache.List(), Host.Json); break;
                case "cache_materialize":
                    response["cachePaths"] = JsonSerializer.SerializeToNode(Precache.Materialize(), Host.Json);
                    response["cache"] = JsonSerializer.SerializeToNode(Precache.List(), Host.Json); break;
                case "shutdown":
                    if (request.ContainsKey("revision")) CheckRevision(request);
                    ExitRequested = true;
                    break;
                case "catalog": response["catalog"] = JsonSerializer.SerializeToNode(Catalog(), Host.Json); break;
                case "resolve_resource":
                {
                    if (OptionalText(request, "sourceKind") != "local_file")
                        throw new CacheSourceException("local_resolver_only", "此接口仅查找本地文件，不能读取游戏当前缓存；请选择实际创建实例的缓存来源。");
                    var resolution = ResourceResolver.Resolve(options, Text(request, "resourcePath"));
                    response["sourceKind"] = "local_file";
                    response["resourcePath"] = resolution.ResourcePath;
                    response["candidates"] = JsonSerializer.SerializeToNode(resolution.Candidates, Host.Json);
                    response["resolvedPath"] = resolution.ResolvedPath;
                    response["message"] = resolution.Message;
                    break;
                }
                case "open": Open(Text(request, "path")); break;
                case "open_motion": response["motionDocument"] = JsonSerializer.SerializeToNode(LocalMotionReader.Open(Text(request, "path")), Host.Json); break;
                case "action_effect_groups": response["effectGroups"] = JsonSerializer.SerializeToNode(ActionEffectGroups(request), Host.Json); break;
                case "open_action":
                {
                    var proof = request["source"]?.AsObject() ?? throw new ArgumentException("Missing action source.");
                    VerifyActionSource(proof);
                    var candidate = new ActionSlotWorkspace(options, proof, Int(request, "prefabIndex"), actionWorkingSet);
                    actionWorkspace = candidate;
                    response["actionDocument"] = JsonSerializer.SerializeToNode(candidate.Info(), Host.Json);
                    break;
                }
                case "add_action_slot":
                {
                    if (actionWorkspace == null) throw new InvalidOperationException("请先打开来源已核实的动作。");
                    var slotRequest=(JsonObject)request.DeepClone();
                    if (Precache.List().Length>0)
                    {
                        var mapped = Precache.Materialize();
                        if (mapped.TryGetValue(ResourceResolver.Normalize(Text(request,"resourcePath")),out var actual)) slotRequest["resourcePath"]=actual;
                    }
                    actionWorkspace.Add(slotRequest);
                    response["actionDocument"] = JsonSerializer.SerializeToNode(actionWorkspace.Info(), Host.Json);
                    break;
                }
                case "remove_action_slot":
                case "reset_action_slots":
                {
                    if(actionWorkspace==null)throw new InvalidOperationException("请先打开动作槽位文档。");
                    var liveStatusPath=Path.Combine(options.Mailbox,"ui-status.json");
                    if(File.Exists(liveStatusPath))
                    {
                        var live=JsonNode.Parse(File.ReadAllText(liveStatusPath))?["actionPreview"];
                        if(live?["busy"]?.GetValue<bool>()==true || live?["requiresRestore"]?.GetValue<bool>()==true)
                            throw new InvalidOperationException("请先完成游戏中动作槽位的还原，再删除文档中的槽位。");
                    }
                    long expected=request["actionRevision"]!.GetValue<long>();
                    if(action=="remove_action_slot")actionWorkspace.Remove(expected,Text(request,"slotId"));
                    else actionWorkspace.Reset(expected);
                    response["actionDocument"]=JsonSerializer.SerializeToNode(actionWorkspace.Info(),Host.Json);
                    break;
                }
                case "publish_action_preview":
                {
                    if(actionWorkspace==null)throw new InvalidOperationException("请先打开来源已核实的动作。");
                    var proof=request["source"]?.AsObject() ?? throw new ArgumentException("缺少当前动作来源。");
                    VerifyActionSource(proof);
                    long actionRevision=request["actionRevision"]!.GetValue<long>();
                    var files=actionWorkspace.PreparePreview(actionRevision,proof);
                    response["actionPreview"]=ActionPreviewPublisher.Publish(options,actionRevision,proof,files);
                    break;
                }
                case "open_cached_resource": OpenCachedResource(request); break;
                case "open_game_resource": OpenGameResource(request); break;
                case "restore_game_checkpoint": RestoreGameCheckpoint(request); break;
                case "inspect":
                {
                    var target = Target.From(request);
                    if (target.Kind == "group")
                    {
                        if (target.AttributeIndex != null) throw new ArgumentException("Group targets do not contain attributes.");
                        response["fields"] = JsonSerializer.SerializeToNode(new[]
                        {
                            new FieldDescription("note", "分组备注", typeof(string).FullName!, GroupNote(target.Index), true)
                        }, Host.Json);
                        response["curves"] = new JsonArray();
                        response["expressionCurves"] = new JsonArray();
                        response["colorGroups"] = new JsonArray();
                        response["target"] = request["target"]?.DeepClone();
                        break;
                    }
                    WriteInspectionSnapshot(target, response);
                    response["target"] = request["target"]?.DeepClone();
                    break;
                }
                case "set":
                {
                    var target = Target.From(request);
                    Set(target, Text(request, "path"), Text(request, "value"));
                    if (target.Kind == "group") NoteSaved(response);
                    break;
                }
                case "set_color":
                {
                    var target = Target.From(request);
                    var plan = ColorEditor.Prepare(Resolve(target), ResolveTargetFile(target).Header.Version, Text(request, "path"),
                        request["values"] as JsonArray ?? throw new ArgumentException("Color values must be a four-channel array."));
                    Mutate(plan.Apply, reopened => plan.Verify(Resolve(target, reopened), ResolveTargetFile(target, reopened).Header.Version));
                    break;
                }
                case "set_fields":
                    SetFields(Target.From(request), request["fields"] as JsonArray ?? throw new ArgumentException("fields must be an array."));
                    break;
                case "rename_group":
                    SetGroupNote(Int(request, "groupIndex"), Text(request, "name")); NoteSaved(response); break;
                case "set_group_note":
                    SetGroupNote(Int(request, "groupIndex"), Text(request, "note")); NoteSaved(response); break;
                case "curve_key": EditCurve(Target.From(request), Text(request, "path"), request); break;
                case "duplicate_entry":
                    Mutate(() =>
                    {
                        var original = Entry(Int(request, "index"));
                        int nextId = NextAttributeId();
                        var clone = service.DeepCloneEntry(Require(), original);
                        clone.index = AllocateEntryId();
                        foreach (var attribute in clone.Attributes) attribute.UniqueID = checked(nextId++);
                    });
                    break;
                case "remove_entry":
                    RemoveEntry(Int(request, "index"), response);
                    break;
                case "restore_entry":
                    RestoreEntry(Int(request, "index"));
                    response["message"] = "已还原当前特效块；其他特效块保持不变。请应用到角色资源以更新游戏。";
                    break;
                case "add_entry":
                    Mutate(() => { var entry = new EFXEntry { Version = Require().Header.Version, name = OptionalText(request, "name") ?? "New effect", entryAssignment = EfxEntryEnum.NoAssignment }; service.AddEntry(Require(), entry); entry.index = AllocateEntryId(); });
                    break;
                case "add_attribute":
                    Mutate(() =>
                    {
                        var target = Target.From(request) with { AttributeIndex = null };
                        var container = (EFXEntryBase)Resolve(target);
                        string name = Text(request, "type");
                        if (!Enum.TryParse<EfxAttributeType>(name, ignoreCase: false, out var type) || !Enum.IsDefined(type))
                            throw new ArgumentException("Unknown attribute type: " + name);
                        var version = ResolveTargetFile(target).Header.Version;
                        var attribute = EFXAttribute.Create(version, type);
                        attribute.Version = version;
                        attribute.UniqueID = NextAttributeId();
                        if (!service.AddAttributeToEntry(container, attribute))
                            throw new InvalidOperationException("Attribute cannot be added: it may already exist, require a matching base attribute, or be invalid for this entry kind.");
                    });
                    break;
                case "remove_attribute":
                    RemoveAttribute(Target.From(request), response);
                    break;
                case "undo": RestoreHistory(undo, redo); break;
                case "redo": RestoreHistory(redo, undo); break;
                case "save": response["outputPath"] = Save(OptionalText(request, "path")); break;
                case "begin_export": response["desktop"] = BeginExport(request); break;
                case "prepare_export": response["exportRequest"] = PrepareExport(request); break;
                case "finish_export": response["exportedFiles"] = FinishExport(request); break;
                case "publish":
                {
                    var published = Publish();
                    response["resourcePath"] = published.Resource;
                    response["outputPath"] = published.Output;
                    break;
                }
                default: throw new ArgumentException("Unknown action: " + action);
            }
        }
        catch (Exception ex)
        {
            response["ok"] = false;
            response["error"] = ex.GetBaseException().Message;
            if (ex is CacheSourceException cacheError) response["errorCode"] = cacheError.Code;
        }
        response["revision"] = revision;
        try { response["document"] = DocumentInfo(); }
        catch (Exception ex) { response["ok"] = false; response["error"] = ex.GetBaseException().Message; }
        processedIds.Add(id);
        responses[id] = (payload, (JsonObject)response.DeepClone());
        if (responses.Count > 512) responses.Remove(responses.Keys.First());
        return response;
    }

    private IEnumerable<object> Catalog()
    {
        if (!Directory.Exists(options.Assets)) throw new DirectoryNotFoundException("Assets directory not found: " + options.Assets);
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(options.Assets, "*.efx.*", enumeration)
            .Where(IsEfxPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => (object)new { path = Path.GetFullPath(p), name = Path.GetRelativePath(options.Assets, p), sourceKind = "local_file" }).ToArray();
    }

    private static bool IsEfxPath(string path) => Regex.IsMatch(path, @"\.efx(?:\.\d+)?$", RegexOptions.IgnoreCase);

    private void Open(string path)
    {
        path = Path.GetFullPath(path);
        if (!IsEfxPath(path)) throw new ArgumentException("请选择 .efx 或 .efx.<版本号> 文件。");
        if (new FileInfo(path).Length is < 16 or > 128 * 1024 * 1024) throw new InvalidDataException("EFX 文件为空、已截断或超过 128 MiB 限制。");
        byte[] original = File.ReadAllBytes(path);
        if (BitConverter.ToUInt32(original) != EfxFile.Magic) throw new InvalidDataException("文件头不是 EFX；当前编辑文档保持不变。");
        OpenContent(original, path, "local_file", null);
    }

    private void OpenCachedResource(JsonObject request)
    {
        if (document != null) CheckRevision(request);
        var descriptor = CacheSourceDescriptor.Parse(request);
        long currentEpoch = CacheSourceDescriptor.CurrentEpoch(request, descriptor);
        var snapshot = cacheReader.Read(descriptor, currentEpoch);
        if (snapshot == null || snapshot.Source != descriptor || snapshot.CurrentSceneEpoch != currentEpoch ||
            string.IsNullOrWhiteSpace(snapshot.ProcessIdentity) || string.IsNullOrWhiteSpace(snapshot.ReaderName) || snapshot.Content == null)
            throw new CacheSourceException("cache_snapshot_unverified", "缓存读取结果未通过来源与场景核验；未打开本地文件，现有编辑文档保持不变。");
        if (snapshot.Content.Length > 128 * 1024 * 1024)
            throw new CacheSourceException("cache_snapshot_invalid", "缓存 EFX 内容超出大小限制；现有编辑文档保持不变。");
        string logical = ResourceResolver.Normalize(descriptor.ActualPath);
        string virtualPath = "game-cache://" + Uri.EscapeDataString(descriptor.SourceId) + "/" + logical.Split('/')[^1] + ".5571972";
        // The constructor-injected reader is the only byte source. There is no
        // path lookup, same-name fallback or request-provided file/byte payload.
        OpenContent(snapshot.Content.ToArray(), virtualPath, "game_cache", descriptor);
    }

    private void OpenGameResource(JsonObject request)
    {
        if (document != null) CheckRevision(request);
        var descriptor = CacheSourceDescriptor.Parse(request);
        CacheSourceDescriptor.CurrentEpoch(request, descriptor);
        var snapshot = new GameInstallResourceReader(options).Read(descriptor);
        string virtualPath = "game-files://" + Uri.EscapeDataString(descriptor.SourceId) + "/" + snapshot.NativePath.Split('/')[^1];
        OpenContent(snapshot.Content, virtualPath, "game_files", null, snapshot.Provenance());
    }

    private void RestoreGameCheckpoint(JsonObject request)
    {
        string checkpoint = Path.GetFullPath(Text(request, "checkpointPath"));
        if (sourceKind != "local_file" || !string.Equals(source, checkpoint, StringComparison.OrdinalIgnoreCase))
            throw new CacheSourceException("checkpoint_source_mismatch", "请先明确打开该检查点文件；没有给其他文档附加游戏来源。");
        string expected = Text(request, "expectedContentHash");
        if (!Regex.IsMatch(expected, @"\A[0-9A-Fa-f]{64}\z"))
            throw new ArgumentException("检查点原始安装来源的 SHA256 无效。");
        var descriptor = CacheSourceDescriptor.Parse(request);
        CacheSourceDescriptor.CurrentEpoch(request, descriptor);
        var snapshot = new GameInstallResourceReader(options).Read(descriptor);
        if (!string.Equals(snapshot.ContentHash, expected, StringComparison.OrdinalIgnoreCase))
            throw new CacheSourceException("checkpoint_origin_changed", "游戏安装来源已变化，不能恢复旧检查点的角色绑定；已打开的检查点修改保持不变。");
        string virtualPath = "game-files://" + Uri.EscapeDataString(descriptor.SourceId) + "/" + snapshot.NativePath.Split('/')[^1];
        // The checkpoint is already open at the checked revision. Keep its
        // edited bytes, rather than replacing them with the installation read.
        OpenContent(bytes!.ToArray(), virtualPath, "game_files", null, snapshot.Provenance());
        var originalDocument = service.Load(snapshot.Content, virtualPath);
        try { baseline = Validate(originalDocument, virtualPath); }
        finally { DisposeFile(originalDocument); }
    }

    private void OpenContent(byte[] original, string path, string kind, CacheSourceDescriptor? descriptor, JsonObject? installedSource = null)
    {
        StashDocument();
        string logicalKey = GroupNoteStore.ResourceKey(kind, path, descriptor, installedSource);
        string key = logicalKey + "|" + Hash(original);
        workingDocuments.TryGetValue(key, out var working);
        if (working == null && kind == "game_files" &&
            installedSource?["actualPath"]?.GetValue<string>().Contains("/MotionVfxLive/",StringComparison.OrdinalIgnoreCase)==true)
        {
            // Our published revisions keep the same original baseline. A new
            // unrelated installed mod is a separate version, never overwritten
            // by a stale working copy with the same filename.
            var previous = workingDocuments.FirstOrDefault(p => p.Key.StartsWith(logicalKey+"|",StringComparison.OrdinalIgnoreCase) && p.Value.Bytes.AsSpan().SequenceEqual(original));
            if (previous.Value != null) { key=previous.Key; working=previous.Value; }
        }
        byte[] opening = working?.Bytes ?? original;
        if (original.Length > 128 * 1024 * 1024) throw new InvalidDataException("EFX exceeds the 128 MiB editor limit.");
        var candidate = service.Load(opening, path);
        try
        {
            byte[] canonical = Validate(candidate, path);
            DisposeFile(document);
            document = candidate;
            source = path;
            sourceKind = kind;
            cacheSource = descriptor;
            gameSource = installedSource;
            bytes = canonical;
            savedHash = working?.SavedHash ?? Hash(canonical);
            baseline = working?.Baseline ?? canonical.ToArray();
            documentSessionKey = key;
            entryIdHighWater = working?.EntryHighWater ?? candidate.Entries.Select(e => e.index).DefaultIfEmpty(-1).Max();
        attributeIdHighWater = working?.AttributeHighWater ?? AllEmbeddedAttributes(candidate).Select(a => a.UniqueID).DefaultIfEmpty(-1).Max();
            revision++;
            undo.Clear(); redo.Clear();
            if (working != null) { undo.AddRange(working.Undo); redo.AddRange(working.Redo); }
            warning = original.AsSpan().SequenceEqual(canonical) ? null : "This parser normalizes the file when saving; the original file is preserved. Unsupported layouts are rejected rather than converted to empty attributes.";
        }
        catch { DisposeFile(candidate); throw; }
    }

    private void Set(Target target, string path, string text)
    {
        if (target.Kind == "group")
        {
            if (path != "note" || target.AttributeIndex != null)
                throw new ArgumentException("原生分组名称与标识不可修改；显示名称请使用分组备注（note）。");
            SetGroupNote(target.Index, text);
            return;
        }
        var field = ContentInspector.Describe(Resolve(target), ResolveTargetFile(target).Header.Version).SingleOrDefault(f => f.Description.Path == path)
            ?? throw new ArgumentException("Field path is not present in the selected object: " + path);
        if (!field.Description.Editable || field.Set == null) throw new InvalidOperationException(field.Description.Reason ?? "Field is read-only.");
        object? parsed = ContentInspector.Parse(text, field.ValueType);
        string expected = ContentInspector.Format(parsed);
        Mutate(() => field.Set(parsed), reopened =>
        {
            var readback = ContentInspector.Describe(Resolve(target, reopened), ResolveTargetFile(target, reopened).Header.Version)
                .SingleOrDefault(f => f.Description.Path == path);
            if (readback == null || readback.Description.Value != expected)
                throw new InvalidDataException("The serializer did not preserve this value. The change was rolled back; this member may be derived or inactive for the current layout.");
        });
    }

    private static void VerifyCompact(EfxFile file)
    {
        if (file.FileHandler.Tell() != file.FileHandler.FileSize())
            throw new InvalidDataException("Deleted content left unread trailing bytes; the operation was rolled back.");
        if (EnumerateEmbeddedFiles(file).Any(local => local.Header.entryCount != local.Entries.Count || local.Header.actionCount != local.Actions.Count || local.Header.effectGroupsCount != local.EffectGroups.Count ||
            local.EffectGroups.Any(g => g.valueCount != (g.efxEntryIndexes?.Length ?? 0) ||
                (g.efxEntryIndexes ?? []).Any(index => index < 0 || index >= local.Entries.Count))))
            throw new InvalidDataException("Deleted content left inconsistent group references or counts; rolled back.");
    }

    private static string GroupsFingerprint(EfxFile file) => string.Join('|', file.EffectGroups.Select(g =>
        g.groupName + ":" + string.Join(',', g.efxEntryIndexes ?? [])));

    private static string EmbeddedGroupsFingerprint(EfxFile file, EFXAttribute? removed = null) => string.Join("\n", EnumerateEmbeddedFiles(file, removed).Select(GroupsFingerprint));

    private void RemoveEntry(int index, JsonObject response)
    {
        var file = Require();
        if (index < 0 || index >= file.Entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var remaining = file.Entries.Where((_, i) => i != index).Select(e => e.index).ToArray();
        string expectedGroups = string.Join('|', file.EffectGroups.Select(g => g.groupName + ":" + string.Join(',',
            (g.efxEntryIndexes ?? []).Where(i => i != index).Select(i => i > index ? i - 1 : i))));
        int references = file.EffectGroups.Sum(g => (g.efxEntryIndexes ?? []).Count(i => i == index));
        int before = bytes!.Length;
        Mutate(() => { if (!service.RemoveEntry(file, index)) throw new InvalidOperationException("Entry was not found."); }, reopened =>
        {
            VerifyCompact(reopened);
            if (!reopened.Entries.Select(e => e.index).SequenceEqual(remaining) || GroupsFingerprint(reopened) != expectedGroups)
                throw new InvalidDataException("Entry deletion changed surviving IDs or ordered group references; rolled back.");
        });
        response["deletion"] = new JsonObject { ["kind"] = "entry", ["removedGroupReferences"] = references,
            ["bytesBefore"] = before, ["bytesAfter"] = bytes!.Length, ["trailingBytes"] = 0, ["groupSlotsPreserved"] = true };
    }

    private void RemoveAttribute(Target target, JsonObject response)
    {
        if (target.AttributeIndex == null) throw new ArgumentException("attributeIndex is required.");
        var attribute = (EFXAttribute)Resolve(target);
        var owner = target with { AttributeIndex = null };
        var container = (EFXEntryBase)Resolve(owner);
        var expected = container.Attributes.Where((_, i) => i != target.AttributeIndex.Value).Select(a => (a.type, a.UniqueID)).ToArray();
        string groups = EmbeddedGroupsFingerprint(Require(), attribute);
        int before = bytes!.Length;
        Mutate(() => { if (!service.RemoveAttributeFromEntry(container, attribute)) throw new InvalidOperationException("Attribute was not found."); }, reopened =>
        {
            VerifyCompact(reopened);
            if (!((EFXEntryBase)Resolve(owner, reopened)).Attributes.Select(a => (a.type, a.UniqueID)).SequenceEqual(expected) || EmbeddedGroupsFingerprint(reopened) != groups)
                throw new InvalidDataException("Attribute deletion changed unrelated attributes or groups; rolled back.");
        });
        response["deletion"] = new JsonObject { ["kind"] = "attribute", ["bytesBefore"] = before,
            ["bytesAfter"] = bytes!.Length, ["trailingBytes"] = 0, ["groupSlotsPreserved"] = true };
    }

    private void SetFields(Target target, JsonArray edits)
    {
        if (target.Kind == "group" || edits.Count is < 1 or > 64) throw new ArgumentException("A parameter transaction requires 1–64 fields on one EFX object.");
        var fields = ContentInspector.Describe(Resolve(target), ResolveTargetFile(target).Header.Version).ToDictionary(f => f.Description.Path);
        var plan = new List<(EditableField Field, object? Value, string Expected)>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in edits)
        {
            var edit = item as JsonObject ?? throw new ArgumentException("Each field needs path and value.");
            string path = Text(edit, "path");
            if (!paths.Add(path)) throw new ArgumentException("Duplicate field: " + path);
            if (!fields.TryGetValue(path, out var field)) throw new ArgumentException("Field is not present: " + path);
            if (!field.Description.Editable || field.Set == null) throw new InvalidOperationException(field.Description.Reason ?? "Field is read-only.");
            object? value = ContentInspector.Parse(Text(edit, "value"), field.ValueType);
            plan.Add((field, value, ContentInspector.Format(value)));
        }
        // Parse every input before writing. All components share one undo step,
        // revision and serializer verification; any failure restores the whole EFX.
        Mutate(() => { foreach (var edit in plan) edit.Field.Set!(edit.Value); }, reopened =>
        {
            var actual = ContentInspector.Describe(Resolve(target, reopened), ResolveTargetFile(target, reopened).Header.Version).ToDictionary(f => f.Description.Path);
            foreach (var edit in plan)
                if (!actual.TryGetValue(edit.Field.Description.Path, out var field) || field.Description.Value != edit.Expected)
                    throw new InvalidDataException("Parameter group was not preserved by serialization; all changes were rolled back.");
        });
    }

    private EffectGroup Group(int index)
    {
        var file = Require();
        if (index < 0 || index >= file.EffectGroups.Count) throw new ArgumentOutOfRangeException(nameof(index), "分组编号无效。");
        return file.EffectGroups[index];
    }

    private string NoteResourceKey() => GroupNoteStore.ResourceKey(sourceKind, source!, cacheSource, gameSource);

    private string GroupNote(int index) => groupNotes.Get(NoteResourceKey(), GroupNoteStore.Identify(Require(), index), out _);

    private void SetGroupNote(int index, string note) => groupNotes.Set(NoteResourceKey(), GroupNoteStore.Identify(Require(), index), note);

    private static void NoteSaved(JsonObject response)
    {
        response["noteSaved"] = true;
        response["noteStorage"] = "editor_sidecar";
        response["noteMessage"] = "备注已独立保存，不写入 EFX；不属于特效撤销记录。";
    }

    private void EditCurve(Target target, string path, JsonObject request)
    {
        var curves = ContentInspector.Curves(Resolve(target), ResolveTargetFile(target).Header.Version);
        var curve = curves.SingleOrDefault(c => c.Path == path) ?? throw new ArgumentException("No active curve at this path.");
        Dictionary<string, string>? expected = null;
        Mutate(() =>
        {
            CurveEditor.Edit(curve.Data, request);
            expected = curves.ToDictionary(c => c.Path, c => CurveEditor.Fingerprint(c.Data));
        }, reopened =>
        {
            var actual = ContentInspector.Curves(Resolve(target, reopened), ResolveTargetFile(target, reopened).Header.Version)
                .ToDictionary(c => c.Path, c => CurveEditor.Fingerprint(c.Data));
            if (actual.Count != expected!.Count || expected.Any(e => !actual.TryGetValue(e.Key, out var value) || value != e.Value))
                throw new InvalidDataException("Curve keys, channel mapping or tangents changed during serialization; the edit was rolled back.");
        });
    }

    private void Mutate(Action change, Action<EfxFile>? verify = null)
    {
        Require();
        byte[] before = bytes!.ToArray();
        try
        {
            change();
            byte[] after = Validate(Require(), source!, verify);
            if (before.AsSpan().SequenceEqual(after)) return;
            var normalized = service.Load(after, source!);
            DisposeFile(document); document = normalized;
            Push(undo, before); redo.Clear(); bytes = after; revision++;
        }
        catch
        {
            var restored = service.Load(before, source!);
            DisposeFile(document); document = restored; bytes = before;
            throw;
        }
    }

    private byte[] Validate(EfxFile candidate, string path, Action<EfxFile>? verify = null)
    {
        string structure = Structure(candidate);
        byte[] serialized = service.SaveToBytes(candidate);
        var reopened = service.Load(serialized, path);
        try
        {
            if (Structure(reopened) != structure) throw new InvalidDataException("Serialization changed the entry/attribute structure.");
            verify?.Invoke(reopened);
            if (!serialized.AsSpan().SequenceEqual(service.SaveToBytes(reopened)))
                throw new InvalidDataException("The parser does not produce a stable round trip for this file. No output was written.");
            return serialized;
        }
        finally { DisposeFile(reopened); }
    }

    private static string Structure(EfxFile file) => string.Join("\n", EnumerateEmbeddedFiles(file).Select(local => (int)local.Header.Version + ":" +
        string.Join("|", local.Entries.Select(e => e.name + ":" + string.Join(',', e.Attributes.Select(a => a.type)))) + "::" +
        string.Join("|", local.Actions.Select(e => e.name + ":" + string.Join(',', e.Attributes.Select(a => a.type))))));

    private void RestoreHistory(List<byte[]> from, List<byte[]> to)
    {
        Require();
        if (from.Count == 0) throw new InvalidOperationException("No history is available.");
        byte[] previous = from[^1];
        var restored = service.Load(previous, source!);
        from.RemoveAt(from.Count - 1); Push(to, bytes!);
        DisposeFile(document); document = restored; bytes = previous; revision++;
    }

    private static void Push(List<byte[]> stack, byte[] value)
    {
        stack.Add(value);
        while (stack.Count > 64 || (stack.Count > 1 && stack.Sum(b => (long)b.Length) > 256 * 1024 * 1024)) stack.RemoveAt(0);
    }

    private string Save(string? requestedPath)
    {
        Require();
        string stem = Path.GetFileName(source!);
        int extensionStart = stem.IndexOf(".efx", StringComparison.OrdinalIgnoreCase);
        if (extensionStart >= 0) stem = stem[..extensionStart];
        string path = requestedPath == null
            ? Path.Combine(options.Output, session, stem + $"_edit_r{revision}_{Guid.NewGuid():N}.efx.{(int)document!.Header.Version}")
            : Path.GetFullPath(requestedPath);
        if (string.Equals(path, source, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The imported source is protected. Save to a new path.");
        if (!path.EndsWith($".efx.{(int)document!.Header.Version}", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output suffix must match the loaded EFX version.");
        WriteNewFile(path, ResolvePrecache(Validate(Require(), path), path));
        savedHash = Hash(bytes!);
        return path;
    }

    private (string Resource, string Output) Publish()
    {
        Require();
        if (options.GameRoot == null) throw new InvalidOperationException("Publishing requires an explicit --game-root configuration.");
        if (document!.Header.Version != EfxVersion.MHWilds) throw new InvalidOperationException("Only Monster Hunter Wilds EFX files can be published to this preview.");
        string gameRoot = Path.GetFullPath(options.GameRoot);
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException("Configured game directory was not found.");
        string logical = $"Art/VFX/EffectEditor/MotionVfxLive/{session}/rev_{revision}_{++publishCount}.efx";
        string output = Path.GetFullPath(Path.Combine(gameRoot, "natives", "STM", logical.Replace('/', Path.DirectorySeparatorChar) + ".5571972"));
        string allowed = Path.GetFullPath(Path.Combine(gameRoot, "natives", "STM", "Art", "VFX", "EffectEditor", "MotionVfxLive")) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Publish destination escaped the dedicated preview directory.");
        RejectReparsePoints(gameRoot, Path.GetDirectoryName(output)!);
        WriteNewFile(output, ResolvePrecache(Validate(Require(), output), output));
        return (logical, output);
    }

    private byte[] ResolvePrecache(byte[] content, string path)
    {
        var catalog = Precache.List();
        if (catalog.Length == 0) return content;
        var missing = Precache.MissingDependencies();
        if (missing.Length > 0) throw new InvalidDataException("预缓存依赖未导入：" + string.Join(", ", missing));
        return PrecacheStore.RewriteEfx(content, path, Precache.Materialize());
    }

    private static void RejectReparsePoints(string root, string path)
    {
        var info = new DirectoryInfo(path);
        while (info != null && info.FullName.Length >= root.Length)
        {
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Publish refuses junction/symlink directories: " + info.FullName);
            if (string.Equals(info.FullName, root, StringComparison.OrdinalIgnoreCase)) break;
            info = info.Parent;
        }
    }

    private static void WriteNewFile(string path, byte[] content)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllBytes(temporary, content); File.Move(temporary, path, overwrite: false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private JsonNode? BuildDocumentInfo()
    {
        if (document == null) return null;
        var tree = DescribeEmbeddedTree(document);
        var grouped = document.EffectGroups.SelectMany(group => group.efxEntryIndexes ?? []).ToHashSet();
        var noteResource = NoteResourceKey();
        var groups = document.EffectGroups.Select((group, index) =>
        {
            string note = groupNotes.Get(noteResource, GroupNoteStore.Identify(document, index), out string? noteWarning);
            return new { index, name = group.groupName, note, displayName = note.Length > 0 ? note : group.groupName,
                noteWarning, entries = group.efxEntryIndexes ?? [] };
        });
        return JsonSerializer.SerializeToNode(new
        {
            source, sourceKind, cacheSource, gameSource, name = Path.GetFileName(source), revision, dirty = savedHash != Hash(bytes!), version = (int)document.Header.Version,
            canUndo = undo.Count > 0, canRedo = redo.Count > 0, warning,
            sessionRetained = true, canRestoreEntry = baseline != null,
            entries = tree.Entries, actions = tree.Actions,
            groups, groupNotesWarning = groupNotes.Warning,
            ungrouped = Enumerable.Range(0, document.Entries.Count).Where(index => !grouped.Contains(index)),
            attributeTypes = EfxAttributeTypeRemapper.GetAllTypes(document.Header.Version).Values.Select(t => t.ToString()).Distinct().OrderBy(x => x),
            curveKeyEditing = true,
            limitations = new[] { "Edits are validated by serialization and reopening before commit.", "Existing curve channels support key insertion, removal, values and tangent rebuilding. Adding/removing mapped channels, null complex nodes and other collection resizing remain separate structural operations.", "Unsupported EFX layouts are rejected. Unknown numeric fields retain their exact numeric representation." }
        }, Host.Json);
    }

    private void CheckRevision(JsonObject request)
    {
        if (request["revision"] is not JsonNode value) throw new ArgumentException("A document revision is required for this operation.");
        if (value.GetValue<long>() != revision)
            throw new InvalidOperationException("Document revision changed. Refresh the document before editing.");
    }

    private object Resolve(Target target, EfxFile? file = null)
    {
        file = ResolveTargetFile(target, file);
        if (target.Kind == "document") return file;
        EFXEntryBase owner = ResolveContainer(file, target.Kind, target.Index);
        if (target.AttributeIndex is not int index) return owner;
        if (index < 0 || index >= owner.Attributes.Count) throw new ArgumentOutOfRangeException("attributeIndex");
        return owner.Attributes[index];
    }

    private EFXEntry Entry(int index) => (EFXEntry)Resolve(new("entry", index, null));
    private EfxFile Require() => document ?? throw new InvalidOperationException("Open an EFX document first.");
    private int NextAttributeId() => attributeIdHighWater = checked(Math.Max(attributeIdHighWater, AllEmbeddedAttributes(Require()).Select(a => a.UniqueID).DefaultIfEmpty(-1).Max()) + 1);
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    internal static string Text(JsonObject obj, string key) => obj[key]?.GetValue<string>() ?? throw new ArgumentException("Missing string: " + key);
    private static string? OptionalText(JsonObject obj, string key) => obj[key]?.GetValue<string>();
    private static int Int(JsonObject obj, string key) => obj[key]?.GetValue<int>() ?? throw new ArgumentException("Missing integer: " + key);
    private static void DisposeFile(EfxFile? file) => file?.FileHandler.Dispose();
    public void Dispose() { desktop?.Dispose(); DisposeFile(document); }

}
