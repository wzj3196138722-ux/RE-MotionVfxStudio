using System.Numerics;
using System.Runtime.Versioning;
using Hexa.NET.ImGui;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using VfxStudio.App.Camera;
using VfxStudio.App.ImGui;
using VfxStudio.App.UI;
using VfxStudio.Re4Render;
using VfxStudio.Re4Preview;
using VfxStudio.Renderer.Framebuffer;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

[assembly: SupportedOSPlatform("windows")]

var options = new Dictionary<string, string>();
for (int i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) continue;
    string key = args[i][2..];
    options[key] = i + 1 < args.Length && !args[i+1].StartsWith("--") ? args[++i] : "true";
}
string FindRepository()
{
    if (options.TryGetValue("repo", out string? explicitRoot)) return Path.GetFullPath(explicitRoot);
    if (File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "tools", "Re4Native", "re4host.py")))
        return Directory.GetCurrentDirectory();
    for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        if (File.Exists(Path.Combine(folder.FullName, "tools", "Re4Native", "re4host.py"))) return folder.FullName;
    return Directory.GetCurrentDirectory();
}
string repo = FindRepository();
string script = Path.Combine(repo, "tools", "Re4Native", "re4host.py");
if (!File.Exists(script)) throw new FileNotFoundException("Run from the repository root or pass --repo", script);
string exe = options.GetValueOrDefault("exe", Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe");
using var client = new NativePreviewClient(options.GetValueOrDefault("python", "python"), script, exe, options.ContainsKey("test-faults"));
string gameDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!;
string resourceRoot = Path.GetFullPath(options.GetValueOrDefault("resources",Path.Combine(gameDirectory,"RE4_EXTRACT")));
using var resources = new Re4ResourceSource(gameDirectory,resourceRoot);
int stress = int.Parse(options.GetValueOrDefault("stress", "0"));
if(!int.TryParse(options.GetValueOrDefault("manifest-poll-ms", "25"),System.Globalization.NumberStyles.Integer,
    System.Globalization.CultureInfo.InvariantCulture,out int manifestPollMs) || manifestPollMs is < 1 or > 60000)
{
    Console.Error.WriteLine("--manifest-poll-ms 必须为 1–60000 的整数（ms）。");
    Environment.ExitCode=2; return;
}
PreviewScene? scene = null;
// Entries left out per document (by entry position) after the native worker failed on them.
var exclusions = new Dictionary<string,Dictionary<int,string>>(StringComparer.Ordinal);
// Stress and benchmark runs measure the selected entry alone.
bool singleEntry = options.ContainsKey("single-entry") || options.ContainsKey("benchmark") || stress != 0;
bool soloSelected = options.ContainsKey("solo");
using var editor = new PreviewEditorSession(repo,options.GetValueOrDefault("session"),options.GetValueOrDefault("editor-backend"),[gameDirectory,resourceRoot]);
var latency = options.ContainsKey("measure-latency") || options.ContainsKey("latency-benchmark") || options.ContainsKey("idle-benchmark")
    ? new PreviewLatencyRecorder(editor.Directory) : null;
var latencyBenchmark = options.ContainsKey("latency-benchmark")
    ? new PreviewLatencyBenchmark(latency!,editor,int.Parse(options.GetValueOrDefault("latency-repeats","20"))) : null;
var idleBenchmark = options.TryGetValue("idle-benchmark",out string? idleSeconds)
    ? new PreviewIdleBenchmark(latency!,editor.Directory,int.Parse(idleSeconds,System.Globalization.CultureInfo.InvariantCulture)) : null;
if(idleBenchmark!=null && (latencyBenchmark!=null || options.ContainsKey("benchmark") || options.ContainsKey("smoke")))
    throw new ArgumentException("空闲测量不能与其他自动测量模式同时启用。");
// Material references for the property panel come through the same lookup as textures (document root, loose files, paks).
PreviewMaterialReference LoadMaterial(string path)=>PreviewMaterialReference.FromResource(resources,path);
var editPanel = new PreviewEditPanel(LoadMaterial);
PreviewGraphPanel? graphPanel = null;
var curvePanel = new PreviewCurvePanel();
var catalogPanel = new PreviewCatalogPanel();
// After a structural edit from the window: the attribute to open (by type, or the index the backend reports).
string? focusAttributeType = null; int focusAttributeIndex = -1;
string? lastAttributeList = null;
JsonObject? lastStructureReply = null;   // backend reply of the last structural edit (selection, document)
string? openConfirmation = null;         // opened at the top level: popup ids depend on the window that opens them
string? structureNote = null;        // a remark on the last structural edit (for example: new entry not previewable)
PreviewStructureRequest? confirmRemoveEntry = null, confirmPaste = null, chooseScript = null;
bool dockCatalog = false;            // a saved layout from before the catalogue existed: dock it once next to the main panel
// A file dropped on the window: waiting (pendingOpen), or waiting for the user to discard unsaved edits (dropConfirm).
string? pendingOpen = null, dropConfirm = null;
bool dropDiscardAccepted = false;
PreviewEditorState? editorState = null;
Task<PreviewEditorState>? editTask = null;
string? editAction = null, editError = null;
OfflinePreviewManifest? desiredManifest = null, displayedManifest = null;
Task<NativeLoadResult>? loading = null;
Task? faultTask = null;
PreparedPreview? preparedPreview = null;
double manifestPoll = 0;
string? lastLoadError = null, lastFrameError = null;
PreviewControlServer? control = null;
PreviewControlRequest? activeControl = null, captureRequest = null;
bool closeRequested = false;
JsonObject? copyBuffer = null;

var wo = WindowOptions.Default;
wo.Size = new Vector2D<int>(1280, 800);
wo.Title = "RE4 EFX — Native preview P2";
if(options.ContainsKey("benchmark")) { wo.Size = new(1920,1080); wo.VSync=false; wo.FramesPerSecond=0; wo.UpdatesPerSecond=0; }
wo.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4,6));
using var window = Window.Create(wo);
GL gl = null!;
IInputContext input = null!;
ImGuiController gui = null!;
ViewportGridRenderer grid = null!;
ParticlePointRenderer points = null!;
PreviewSceneRenderer? billboards = null;
BillboardGpuCache gpuCache = null!;
PreviewHdrPipeline hdr = null!;
var composition = new CompositionConfig();
ParticleLayer particleLayer=null!;
int quality=options.GetValueOrDefault("quality","auto") switch { "full"=>1,"half"=>2,"quarter"=>3,_=>0 };
int particleDivisor=1;
uint presentFbo=0;
bool showPoints=false;
// Docking: panels dock to the window edges (and float again when dragged off); the scene renders into the
// dockspace's central node. Automated runs keep the old full-window layout so their measurements stay comparable.
bool docking=!(options.ContainsKey("smoke") || options.ContainsKey("benchmark") || latencyBenchmark!=null || idleBenchmark!=null);
bool resetLayout=false;
int selectGraphTab=0;   // frames left to bring the node graph tab to the front after a layout reset
// v2: bottom row layout. A new name applies the new default once and leaves the old file as it was.
string layoutFile=Path.Combine(repo,"runtime","re4-preview","imgui-layout-v2.ini");
IntPtr layoutFileName=IntPtr.Zero;
(int X,int Y,int W,int H) sceneRect=(0,0,1,1);   // framebuffer pixels, GL origin (bottom left)
bool demoMotion=false;
void SetDemoMotion(bool on)
{
    if(demoMotion==on) return;
    demoMotion=on;
    client.WorldTrack=on ? PreviewMotion.Swing(PreviewPlaybackController.LastTick) : null;
    Reload();   // the worker applies the matrix track on the next load
}
float exposure=1f, bloomIntensity=.8f;
var benchmarkClock=new Stopwatch();
var intervals=new List<double>();
var nativeCosts=new List<double>();
var decodeCosts=new List<double>();
var uploadCosts=new List<double>();
var particleCounts=new List<int>();
int lastBenchTick=-1;
double lastBenchTime=0;
float OptionFloat(string name,float fallback)=>options.TryGetValue(name,out var text) && float.TryParse(text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var v) ? v : fallback;
var camera = new OrbitCamera { Target = new Vector3(0, OptionFloat("camera-target-y",.4f), 0), Distance = OptionFloat("camera-distance",4f) };
// --smoke stops on this tick (default 45).
int smokeTick = (int)OptionFloat("smoke-tick",45);
PreviewFrame? frame = null;
Task<PreviewFrame>? pending = null;
string status = "Loading native worker…";
var playback = new PreviewPlaybackController();
PreviewFrameRequest pendingRequest = default;
bool reloadQueued = false;
int selection = 0, rawOffset = 0, completedFrames = 0;
Vector2 previousMouse = default;


void QueuePreview(OfflinePreviewManifest manifest, bool? autoplay=null, bool force=false, bool restart=false)
{
    if(!force && desiredManifest?.Key==manifest.Key) return;
    if(!force && !restart && desiredManifest?.SceneKey==manifest.SceneKey)
    {
        // Only the edited entry changed: the published file is the same, so nothing reloads.
        desiredManifest=manifest;
        if(displayedManifest?.SceneKey==manifest.SceneKey) displayedManifest=manifest;
        if(preparedPreview?.Manifest.SceneKey==manifest.SceneKey) preparedPreview=preparedPreview with {Manifest=manifest};
        return;
    }
    // A new revision of the same document keeps the timeline position; another document, or an
    // explicit replay, starts from tick 0.
    bool sameTarget=desiredManifest!=null && desiredManifest.DocumentId==manifest.DocumentId;
    desiredManifest=manifest;
    playback.BeginLoad(restart:restart || !sameTarget,autoplay:autoplay);
    preparedPreview?.Renderer.Dispose(); preparedPreview=null;
    reloadQueued=true;
    status=$"正在加载文档 r{manifest.Revision}…";
}
void Reload()
{
    if(desiredManifest!=null) QueuePreview(desiredManifest,true,force:true,restart:true);
}
void StartReload()
{
    var manifest=desiredManifest!;
    latency?.LoadStarted(manifest.Key);
    reloadQueued=false;
    lastLoadError=null;
    var known=new Dictionary<int,string>(Exclusions(manifest.DocumentId));
    loading=Task.Run(async () =>
    {
        manifest.ValidateFile();
        resources.UseDocument(manifest.Source);   // the original file's own natives folder first (not the revision copy's)
        if(singleEntry)
        {
            var all=PreviewScene.Read(manifest.Path,resources,known);
            foreach(var other in all.Entries) if(other.Position!=manifest.EntryPosition) known.TryAdd(other.Position,"单 entry 模式。");
        }
        var loaded=await PreviewSceneLoader.LoadAsync(client,manifest.Path,resources,known,stress,manifest.EntryPosition);
        return new NativeLoadResult(manifest,loaded,client.LastLoadWallMs,known);
    });
    status=$"正在加载文档 r{manifest.Revision}…";
}
Dictionary<int,string> Exclusions(string documentId)
{
    if(!exclusions.TryGetValue(documentId,out var known)) exclusions[documentId]=known=new();
    return known;
}
// Which entry-table position a particle belongs to.
int TableOfParticle(PreviewFrame f,int index)
{
    var states=f.EntryStates!;
    for(int t=0;t<states.Count;t++) if(index>=states[t].First && index<states[t].First+states[t].Count) return t;
    return -1;
}
int SelectedTable() => scene==null || displayedManifest==null ? -1 : scene.TableOf(displayedManifest.EntryPosition);
Task<PreviewEditorState> OpenOriginal()
{
    return Task.Run(async () =>
    {
        var location=options.TryGetValue("efx",out string? file)
            ? (Path.GetFullPath(file),Convert.ToInt32(options.GetValueOrDefault("offset","0"),16))
            : PreviewDocument.Fixture(gameDirectory,options.GetValueOrDefault("fixture","bb2"));
        // The original is decoded only to identify the requested native entry ID.
        int entryId=PreviewDocument.ReadEntryId(location.Item1,location.Item2);
        return await editor.OpenAsync(location.Item1,entryId);
    });
}
/// <summary>Why a dropped file cannot be opened, or null.</summary>
static string? DropProblem(string file)
{
    if(!File.Exists(file)) return "只能拖入文件：" + file;
    string name=Path.GetFileName(file);
    int at=name.LastIndexOf(".efx.",StringComparison.OrdinalIgnoreCase);
    if(at<0) return "不是 EFX 文件：" + name;
    if(name[(at+5)..]!="3539837") return $"不是 RE4 的 EFX（版本 {name[(at+5)..]}）：{name}";
    return null;
}
/// <summary>Opens a dropped file at its first entry the preview draws.</summary>
void StartOpenFile(string file)
{
    editAction="open";editError=null;
    editTask=Task.Run(async()=>
    {
        var graph=PreviewEffectGraph.Read(file);
        // Open the first entry the preview draws; a file with none (meshes only, say) still opens for editing.
        var emitters=graph.Nodes.Where(n=>n.Kind==PreviewGraphNodeKind.Emitter && n.Editable).ToArray();
        int position=(emitters.FirstOrDefault(n=>n.Drawn)??emitters.FirstOrDefault())?.EntryPosition
            ?? throw new InvalidDataException("文件里没有 entry："+Path.GetFileName(file));
        return await editor.OpenAtAsync(file,position);
    });
}
void HandleDroppedFile()
{
    if(pendingOpen is {} file && dropConfirm==null)
    {
        if(DropProblem(file) is {} problem) {editError=problem;pendingOpen=null;}
        else if(editPanel.HasUnappliedChanges) {editError="属性面板有未应用的草稿，先应用或放弃，再拖入文件。";pendingOpen=null;}
        else if(editorState?.Dirty==true && !dropDiscardAccepted) {dropConfirm=file;pendingOpen=null;ImGui.OpenPopup("打开拖入的文件");}
        else if(editTask==null) {pendingOpen=null;dropDiscardAccepted=false;StartOpenFile(file);}
        // Otherwise the editor is busy: keep the file until it is free.
    }
    if(ImGui.BeginPopupModal("打开拖入的文件",ImGuiWindowFlags.AlwaysAutoResize))
    {
        ImGui.Text("当前文档有未保存的修改。放弃这些修改并打开：");
        ImGui.Text(Path.GetFileName(dropConfirm??""));
        if(ImGui.Button("放弃修改并打开")) {pendingOpen=dropConfirm;dropConfirm=null;dropDiscardAccepted=true;ImGui.CloseCurrentPopup();}
        ImGui.SameLine();
        if(ImGui.Button("取消")) {dropConfirm=null;ImGui.CloseCurrentPopup();}
        ImGui.EndPopup();
    }
}
async Task<PreviewEditorState> SendCurveKey(PreviewEditCommand command)
{
    try { return (await editor.SendRawAsync("curve_key",command.Body!,command.Revision)).State; }
    catch(PreviewBackendException ex) { throw new InvalidOperationException("曲线修改失败："+ex.Message); }
}
JsonArray DefaultFields(string type) => new(PreviewAttributeCatalog.Re4.Defaults(type)
    .Select(f => (JsonNode?)new JsonObject{["path"]=f.Key,["value"]=f.Value}).ToArray());
// One attribute for add_attribute (with target) or add_entry's list: RE4 default fields; a PtBehavior also gets a script
// class with its RE4 parameter template (EffectDecal unless another is named), since an empty one does nothing in game.
JsonObject AddAttributeBody(JsonObject? target,string type,string? script)
{
    var body=new JsonObject{["type"]=type,["fields"]=DefaultFields(type)};
    if(target!=null) body["target"]=target;
    if(type=="PtBehavior")
        body["behavior"]=(PreviewAttributeCatalog.Re4.Behaviors.Find(script??PreviewBehaviorTemplates.Decal)
            ?? throw new InvalidOperationException("RE4 里没有这个脚本类的模板："+script)).Request();
    return body;
}
// Structural edits from the window. Each runs as one editor task; edits that should open another entry select
// it first (the backend's preview target then follows that entry by ID while positions shift).
async Task<PreviewEditorState> Structure(PreviewEditCommand command)
{
    var request=(PreviewStructureRequest)command.Tag!;
    int entry=request.EntryPosition;
    var owner=request.Owner;
    if(!owner.IsRoot && request.Op is not ("add_attribute" or "remove_attribute" or "copy_attribute" or "paste_attribute" or "material_parameter"))
        throw new InvalidOperationException("内嵌 entry 只支持属性的增删、复制粘贴和字段编辑。");
    // Attribute operations address the owner, which may be an embedded entry; entry operations the top-level entry.
    JsonObject Target(int? attribute=null)=>owner.ToJson(attribute);
    long Revision()=>request.ExpectedRevision??editor.State?.Revision??command.Revision;
    async Task Select() { if(!owner.Equals(editor.State?.EditTarget)) await editor.SelectTargetAsync(owner); }
    async Task<PreviewEditorState> Send(string action,JsonObject body,bool mutation=true)
    {
        var (state,reply)=await editor.SendRawAsync(action,body,mutation?Revision():null);
        if(reply["copyBuffer"] is JsonObject buffer) copyBuffer=(JsonObject)buffer.DeepClone(); else if(reply.ContainsKey("copyBuffer")) copyBuffer=null;
        lastStructureReply=reply;
        return state;
    }
    // A new entry becomes the edited one when the preview can draw it.
    async Task<PreviewEditorState> SelectNew(PreviewEditorState state)
    {
        int count=lastStructureReply?["document"]?["entries"]?.AsArray().Count??0;
        int position=lastStructureReply?["selection"]?["index"]?.GetValue<int>()??count-1;
        try {return await editor.SelectEntryAsync(position);}
        catch(InvalidOperationException ex) {structureNote=$"新 entry [{position}] 已加入，但没能选中：{ex.Message}";return state;}
    }
    string label=request.Op switch
    {
        "add_attribute"=>"添加 "+request.Type,"remove_attribute"=>"删除属性","copy_attribute"=>"复制属性","paste_attribute"=>"粘贴属性",
        "copy_entry"=>"复制 entry","paste_entry"=>"粘贴 entry","add_entry"=>"新建 entry","duplicate_entry"=>"创建副本","remove_entry"=>"删除 entry",
        "emitter_priority"=>request.AttributeIndex>=0?"关闭 EmitterPriority":"开启 EmitterPriority",
        "material_parameter"=>request.Label??"修改材质参数",_=>request.Op
    };
    try
    {
        switch(request.Op)
        {
            case "add_attribute":
                await Select();
                return await Send("add_attribute",AddAttributeBody(Target(),request.Type!,request.Script));
            case "remove_attribute":
                await Select();
                return await Send("remove_attribute",new(){["target"]=Target(request.AttributeIndex)});
            case "material_parameter":
            {
                await Select();
                var body=(JsonObject)request.Body!.DeepClone();
                body["target"]=Target(request.AttributeIndex);
                return await Send("material_parameter",body);
            }
            case "copy_attribute":
                return await Send("copy_efx",new(){["target"]=Target(request.AttributeIndex)},mutation:false);
            case "emitter_priority":
                // On the Root entry, which has no Type and cannot be the edited entry: no selection change.
                return request.AttributeIndex>=0
                    ? await Send("remove_attribute",new(){["target"]=Target(request.AttributeIndex)})
                    : await Send("add_attribute",new(){["target"]=Target(),["type"]="EmitterPriority",["fields"]=new JsonArray()});
            case "copy_entry":
                return await Send("copy_efx",new(){["target"]=Target()},mutation:false);
            case "paste_attribute":
            {
                await Select();
                var body=new JsonObject{["target"]=Target(),["clipboardId"]=copyBuffer?["id"]?.GetValue<string>()};
                if(request.AttributeIndex>=0) {body["replace"]=true;body["replaceAttributeIndex"]=request.AttributeIndex;}
                var state=await Send("paste_efx",body);
                if(lastStructureReply?["selection"]?["attributeIndex"]?.GetValue<int>() is int pasted) focusAttributeIndex=pasted;
                return state;
            }
            case "paste_entry":
                return await SelectNew(await Send("paste_efx",new(){["target"]=new JsonObject{["kind"]="document",["index"]=0},["clipboardId"]=copyBuffer?["id"]?.GetValue<string>()}));
            case "add_entry":
            {
                // A script carrier takes RE4's usual attribute list for that script; other entries the Type's template.
                var script=request.Script==null?null:PreviewAttributeCatalog.Re4.Behaviors.Find(request.Script)
                    ?? throw new InvalidOperationException("RE4 里没有这个脚本类的模板："+request.Script);
                var types=script?.Carrier ?? PreviewAttributeCatalog.Re4.Template(request.Type!);
                var attributes=new JsonArray(types.Select(t=>(JsonNode?)AddAttributeBody(null,t,script?.Name)).ToArray());
                return await SelectNew(await Send("add_entry",new(){["name"]="New_"+(script?.ShortName ?? request.Type![4..]),["attributes"]=attributes}));
            }
            case "duplicate_entry":
                return await SelectNew(await Send("duplicate_entry",new(){["index"]=entry}));
            case "remove_entry":
            {
                // The edited entry cannot be the one removed: move to the fallback (chosen on the main thread) first.
                if(editor.State?.Manifest?.EntryPosition==entry)
                    await editor.SelectEntryAsync(request.AttributeIndex);
                return await Send("remove_entry",new(){["index"]=entry});
            }
            default: throw new InvalidOperationException("未知的结构操作："+request.Op);
        }
    }
    catch(PreviewBackendException ex) { throw new InvalidOperationException(label+"失败："+ex.Message); }
}
// Checks a structural request against the current state, asks for confirmation where needed, then starts it.
void RequestStructure(PreviewStructureRequest request)
{
    if(editorState?.Manifest is not {} manifest || editTask!=null || editPanel.HasUnappliedChanges || stress!=0) return;
    structureNote=null;
    switch(request.Op)
    {
        case "remove_entry" when confirmRemoveEntry==null:
            confirmRemoveEntry=request;openConfirmation="删除 entry";return;
        case "paste_attribute" when request.AttributeIndex<0:
        {
            // The same type already there: ask before replacing it (the backend refuses otherwise).
            string? type=copyBuffer?["attributeType"]?.GetValue<string>();
            var existing=request.Owner.Equals(editorState.EditTarget)
                ? editorState.Attributes?.FirstOrDefault(a=>a.Type==type)?.Index
                : scene?.Graph.Nodes.FirstOrDefault(n=>request.Owner.Equals(n.Target))?.AttributeTypes?.ToList().IndexOf(type??"") is int i and >=0 ? i : null;
            if(existing is int index) {confirmPaste=request with {AttributeIndex=index};openConfirmation="替换属性";return;}
            break;
        }
        // A PtBehavior needs a script class: ask which one before adding (catalogue, graph and panel drops all come here).
        case "add_attribute" when request.Type=="PtBehavior" && request.Script==null:
            chooseScript=request;openConfirmation="选择脚本类";return;
        case "add_attribute": focusAttributeType=request.Type; break;
    }
    StartEditorCommand(new PreviewEditCommand("structure",editorState.Revision,EntryPosition:request.EntryPosition,Tag:request));
}
// The entry to edit after removing one: the current entry, or when that is the one removed, the nearest other entry (-1: none).
int RemoveFallback(int removed)
{
    int current=editorState?.Manifest?.EntryPosition??-1;
    if(removed!=current) return current;
    return scene?.Graph.Nodes.Where(n=>n.Kind==PreviewGraphNodeKind.Emitter && n.Editable && n.EntryPosition!=removed)
        .OrderBy(n=>Math.Abs(n.EntryPosition!.Value-removed)).Select(n=>n.EntryPosition!.Value).DefaultIfEmpty(-1).First()??-1;
}
void RequestAdd(PreviewAddRequest request) => RequestStructure(new PreviewStructureRequest("add_attribute",request.EntryPosition,Type:request.Type,Target:request.Target));
// Confirmation popups for structural edits; drawn every frame at the top level.
void DrawStructureConfirmations()
{
    if(openConfirmation!=null) {ImGui.OpenPopup(openConfirmation);openConfirmation=null;}
    if(ImGui.BeginPopupModal("删除 entry",ImGuiWindowFlags.AlwaysAutoResize))
    {
        var request=confirmRemoveEntry;
        var node=request==null?null:scene?.Graph.EmitterAt(request.EntryPosition);
        ImGui.Text($"删除 {node?.Title ?? "entry"}？可以撤销。");
        // Removing the edited entry needs another entry to move to.
        int current=editorState?.Manifest?.EntryPosition??-1;
        int fallback=request==null?-1:RemoveFallback(request.EntryPosition);
        if(fallback<0) ImGui.TextColored(new Vector4(1,.6f,.35f,1),"这是文件里唯一的 entry，删除后没有可编辑的目标，不能删除。");
        else if(request!=null && request.EntryPosition==current) ImGui.TextDisabled($"删除后改为编辑 [{fallback}]。");
        ImGui.BeginDisabled(fallback<0 || request==null);
        if(ImGui.Button("删除"))
        {
            ImGui.CloseCurrentPopup();
            if(request!=null && editorState!=null && editTask==null)
                StartEditorCommand(new PreviewEditCommand("structure",editorState.Revision,EntryPosition:request.EntryPosition,Tag:request with {AttributeIndex=fallback}));
            confirmRemoveEntry=null;
        }
        ImGui.EndDisabled();ImGui.SameLine();
        if(ImGui.Button("取消")) {confirmRemoveEntry=null;ImGui.CloseCurrentPopup();}
        ImGui.EndPopup();
    }
    if(ImGui.BeginPopupModal("选择脚本类",ImGuiWindowFlags.AlwaysAutoResize))
    {
        ImGui.TextUnformatted("PtBehavior 调用哪个游戏脚本？参数按 RE4 的常见写法填好，之后可以在属性面板里改。");
        ImGui.TextDisabled("预览不运行游戏脚本，画面上看不到它的效果。");
        foreach(var script in PreviewAttributeCatalog.Re4.Behaviors.Classes)
            if(ImGui.Selectable($"{script.ShortName}（RE4 {script.Uses} 处，{script.Parameters.Count} 个参数）##{script.Name}"))
            {
                var request=chooseScript;chooseScript=null;ImGui.CloseCurrentPopup();
                if(request!=null) RequestStructure(request with {Script=script.Name});
            }
        if(ImGui.Button("取消")) {chooseScript=null;ImGui.CloseCurrentPopup();}
        ImGui.EndPopup();
    }
    if(ImGui.BeginPopupModal("替换属性",ImGuiWindowFlags.AlwaysAutoResize))
    {
        ImGui.Text($"目标 entry 已有 {copyBuffer?["attributeType"]}。用复制的属性替换它？可以撤销。");
        if(ImGui.Button("替换"))
        {
            ImGui.CloseCurrentPopup();
            if(confirmPaste!=null && editorState!=null && editTask==null)
                StartEditorCommand(new PreviewEditCommand("structure",editorState.Revision,EntryPosition:confirmPaste.EntryPosition,Tag:confirmPaste));
            confirmPaste=null;
        }
        ImGui.SameLine();
        if(ImGui.Button("取消")) {confirmPaste=null;ImGui.CloseCurrentPopup();}
        ImGui.EndPopup();
    }
}
void StartEditorCommand(PreviewEditCommand command, bool warmup=false)
{
    if(editTask!=null) return;
    if(command.Action is "billboard" or "life" or "fields" or "undo" or "redo" or "publish_offline_preview")
        latency?.Begin("window",command.Action,playback.DisplayedTick,playback.IsPlaying,warmup);
    editAction=command.Action;editError=null;
    editTask=Task.Run(() => command.Action switch
    {
        "billboard" => editor.ApplyBillboardAsync(command.Billboard!,command.Revision),
        "life" => editor.ApplyLifeAsync(command.Life!,command.Revision),
        "fields" => editor.ApplyFieldsAsync(command.Batch!),
        "inspect" => editor.InspectAsync(command.DocumentId!,command.Revision,command.AttributeIndex),
        "open_saved" => editor.OpenSavedAsync(),
        "select_entry" => editor.SelectEntryAsync(command.EntryPosition),
        "select_target" => editor.SelectTargetAsync((PreviewEditTarget)command.Tag!),
        "curve_key" => SendCurveKey(command),
        "structure" => Structure(command),
        "open_source" => OpenOriginal(),
        _ => editor.CommandAsync(command.Action,command.SavePath)
    });
}

// ---- AI control pipe (see PreviewControlServer). Every request runs on this thread, one at a time.
// Phases: 1 wait for the editor slot, 2 wait for the backend, 3 wait to settle before acting on the
// timeline, 4 wait to settle and report, 5 wait for captured pixels, 6 wait for PNG encoding.
JsonObject PreviewReport() => new()
{
    ["displayedRevision"]=displayedManifest?.Revision,
    ["loadingRevision"]=desiredManifest!=null && desiredManifest.Key!=displayedManifest?.Key && lastLoadError==null ? desiredManifest.Revision : null,
    ["loadError"]=lastLoadError,
    ["frameError"]=lastFrameError,
    ["unsupported"]=billboards?.Unsupported,
    ["ready"]=playback.IsReady,
    ["playing"]=playback.IsPlaying,
    ["targetTick"]=playback.TargetTick,
    ["displayedTick"]=frame?.Tick,
    ["lastTick"]=PreviewPlaybackController.LastTick,
    ["alive"]=frame?.Particles.Count,
    ["shownEntries"]=scene?.Shown.Count,
    ["entries"]=scene==null?null:PreviewControlReports.SceneEntries(scene,displayedManifest?.EntryPosition),
    ["childActionEntries"]=scene==null?null:PreviewControlReports.SceneChildren(scene),
    ["actions"]=scene==null?null:PreviewControlReports.Actions(scene.Graph),
    ["embeddedEntries"]=scene==null?null:PreviewControlReports.EmbeddedEntries(scene.Graph),
    ["soloSelected"]=soloSelected,
    ["view"]=ViewReport()
};
JsonObject CompositionReport() => new()
{
    ["fades"]=composition.Fades,["alphaCorrection"]=composition.AlphaCorrection,["rgbLayers"]=composition.RgbLayers,
    ["softParticles"]=composition.SoftParticles,["depthBias"]=composition.DepthBias,["detonemap"]=composition.Detonemap,
    ["alphaRate"]=composition.AlphaRate,["sortEntries"]=composition.SortEntries,["sortWithinEntry"]=composition.SortWithinEntry,
    ["decals"]=composition.Decals,["decalScale"]=composition.DecalScale,["decalScaleXYRandom"]=composition.DecalScaleXYRandom,
    ["physicalGain"]=composition.PhysicalGain,
    ["textureFilter"]=composition.TextureFilter,["texelChannel"]=composition.TexelChannel,["frameBlend"]=composition.FrameBlend,
    ["lighting"]=composition.Lighting,["distortion"]=composition.Distortion,["ambient"]=composition.Ambient.X,["keyLight"]=composition.KeyLight,
    ["blends"]=new JsonArray(composition.Blends.Select((b,i)=>(JsonNode?)new JsonObject{["blend"]=i,["source"]=b.Source.ToString(),["destination"]=b.Destination.ToString()}).ToArray()),
    ["drawOrder"]=billboards==null||scene==null?null:new JsonArray(billboards.LastOrder.Select(t=>(JsonNode?)scene.AtTable(t)?.Position).ToArray())
};
JsonObject ViewReport() => new()
{
    ["target"]=PreviewControlReports.Vector(camera.Target),["yaw"]=camera.Yaw,["pitch"]=camera.Pitch,["distance"]=camera.Distance,
    ["exposure"]=exposure,["bloom"]=bloomIntensity,["showPoints"]=showPoints,["soloSelected"]=soloSelected,["demoMotion"]=demoMotion,
    ["quality"]=quality switch {1=>"full",2=>"half",3=>"quarter",_=>"auto"},["particleDivisor"]=particleDivisor
};
JsonObject StatusReport() => new()
{
    ["window"]=new JsonObject
    {
        ["pid"]=Environment.ProcessId,["pipe"]=control?.PipeName,["sessionDirectory"]=editor.Directory,["status"]=status,
        ["editError"]=editError,["lastRecovery"]=client.LastRecovery.Length>0?client.LastRecovery:null,
        ["uiDraftPending"]=editPanel.HasUnappliedChanges,["editorBusy"]=editTask!=null
    },
    ["copyBuffer"]=copyBuffer?.DeepClone(),
    ["document"]=PreviewControlReports.Document(editorState),
    ["preview"]=PreviewReport()
};
// True once nothing is loading or rendering towards a newer state. error explains a settled state
// that cannot show the current revision (load failure, unsupported blend, failed frame).
bool Settled(out string? error)
{
    error=null;
    if(editTask!=null || loading!=null || faultTask!=null || reloadQueued || pending!=null) return false;
    if(desiredManifest==null) {error="尚未打开文档。";return true;}
    if(lastLoadError!=null && desiredManifest.Key!=displayedManifest?.Key) {error="预览加载失败："+lastLoadError;return true;}
    if(displayedManifest?.Key!=desiredManifest.Key || !playback.IsReady || playback.HasQueuedRequest || frame==null) return false;
    if(!playback.IsPlaying && frame.Tick!=playback.TargetTick) return false;
    // A settled scene can still miss the edited entry (excluded, or skipped by the renderer).
    var selected=scene?.Entries.FirstOrDefault(e=>e.Position==desiredManifest.EntryPosition);
    int selectedTable=scene?.TableOf(desiredManifest.EntryPosition)??-1;
    if(lastFrameError!=null) error="预览运行失败："+lastFrameError;
    else if(selected?.Excluded is {} excludedReason) error="所选 entry 未显示："+excludedReason;
    else if(billboards?.FailureAt(selectedTable) is {} failure) error="所选 entry 未绘制："+failure;
    else if(billboards?.ForTable(selectedTable)?.Unsupported is {} unsupported) error="所选 entry 未绘制："+unsupported;
    else if(billboards!=null && billboards.Drawn==0 && billboards.Unsupported!=null) error=billboards.Unsupported;
    return true;
}
bool IsMutation(string action) => action is "set_fields" or "undo" or "redo" or "open" or "select_entry" or "add_attribute" or "remove_attribute" or "curve_key" or "paste_attribute"
    or "add_entry" or "duplicate_entry" or "remove_entry" or "paste_entry";
// Entry-level AI actions run the same code as the window's menus.
PreviewStructureRequest EntryRequest(PreviewControlRequest job)
{
    var a=job.Arguments;
    long? revision=a["expectedRevision"]==null?null:PreviewControlReports.Long(a,"expectedRevision");
    int Position()=>PreviewControlReports.Int(a,"entryPosition",0,1<<20);
    return job.Action switch
    {
        "add_entry"=>PreviewControlReports.String(a,"script") is {} script
            ? new("add_entry",-1,Type:"TypeNoDraw",ExpectedRevision:revision,Script:script)
            : new("add_entry",-1,Type:PreviewControlReports.String(a,"type"),ExpectedRevision:revision),
        "duplicate_entry"=>new("duplicate_entry",Position(),ExpectedRevision:revision),
        "copy_entry"=>new("copy_entry",Position()),
        "paste_entry"=>new("paste_entry",-1,ExpectedRevision:revision),
        "remove_entry"=>new("remove_entry",Position(),RemoveFallback(Position()),ExpectedRevision:revision),
        _=>throw new InvalidOperationException("内部错误：未知的 entry 操作。")
    };
}
Task<PreviewEditorState> StartEntryAction(PreviewControlRequest job)
{
    var request=EntryRequest(job);
    return Task.Run(async()=>
    {
        var state=await Structure(new PreviewEditCommand("structure",request.ExpectedRevision??0,EntryPosition:request.EntryPosition,Tag:request));
        job.BackendReply=lastStructureReply;
        return state;
    });
}
void RequireNoDraft()
{
    if(editPanel.HasUnappliedChanges) throw new InvalidOperationException("窗口里有未应用的属性草稿。请先在窗口中应用或放弃草稿，AI 不会覆盖它。");
}
// The entry the property panel edits: top-level, or embedded (select_entry with embeddedPath).
JsonObject AttributeTarget(int attribute)=>(editorState?.EditTarget??throw new InvalidOperationException("尚未打开文档。")).ToJson(attribute);
JsonObject EntryTarget()=>(editorState?.EditTarget??throw new InvalidOperationException("尚未打开文档。")).ToJson();
// select_entry: entryPosition alone is a top-level entry, actionIndex an Action; with embeddedPath either is taken in
// the embedded file the path leads to.
PreviewEditTarget SelectArgument(JsonObject a)
{
    if((a["entryPosition"]==null)==(a["actionIndex"]==null)) throw new ArgumentException("select_entry 需要 entryPosition 或 actionIndex 其中之一。");
    return a["actionIndex"]!=null
        ? PreviewEditTarget.Parse("action",PreviewControlReports.Int(a,"actionIndex",0,1<<20),a["embeddedPath"])
        : PreviewEditTarget.Parse("entry",PreviewControlReports.Int(a,"entryPosition",0,1<<20),a["embeddedPath"]);
}
JsonObject PasteBody(JsonObject a)
{
    var body=new JsonObject{["target"]=EntryTarget(),["clipboardId"]=PreviewControlReports.String(a,"clipboardId")};
    if(a["replaceAttributeIndex"]!=null) {body["replace"]=true;body["replaceAttributeIndex"]=PreviewControlReports.Int(a,"replaceAttributeIndex",0,4096);}
    return body;
}
JsonObject CurveBody(JsonObject a)
{
    var body=new JsonObject{["target"]=AttributeTarget(PreviewControlReports.Int(a,"attributeIndex",0,4096)),["path"]=PreviewControlReports.String(a,"path"),
        ["channel"]=PreviewControlReports.Int(a,"channel",0,4096),["operation"]=PreviewControlReports.String(a,"operation")};
    if(a["keyIndex"]!=null) body["keyIndex"]=PreviewControlReports.Int(a,"keyIndex",0,1<<20);
    foreach(var name in new[]{"time","value","interpolation","outX","outY","inX","inY"})
        if(PreviewControlReports.String(a,name) is {} text) body[name]=text;
    return body;
}
void UpdateControl()
{
    if(control==null) return;
    if(activeControl==null && control.TryDequeue(out var next))
    {
        activeControl=next;
        RunControlStep(next,BeginControl);
    }
    if(activeControl is {} job)
    {
        if(!job.Reply.Task.IsCompleted) RunControlStep(job,ContinueControl);
        if(!job.Reply.Task.IsCompleted && DateTime.UtcNow>job.Deadline)
            job.Fail("timeout","预览窗口等待超时。已提交的修改可能已生效；请先调用 status 查看修订号和预览状态，不要直接重试。",StatusReport());
        if(job.Reply.Task.IsCompleted) {activeControl=null;if(captureRequest==job) captureRequest=null;}
    }
}
void RunControlStep(PreviewControlRequest job,Action<PreviewControlRequest> step)
{
    try {step(job);}
    catch(PreviewBackendException ex) {job.Fail("backend_rejected",ex.Message);}
    catch(ArgumentException ex) {job.Fail("invalid_arguments",ex.Message);}
    catch(InvalidOperationException ex) {job.Fail("rejected",ex.Message);}
    catch(Exception ex) when(ex is IOException or TimeoutException or NotSupportedException or InvalidDataException) {job.Fail("failed",ex.Message);}
}
void BeginControl(PreviewControlRequest job)
{
    var a=job.Arguments;
    switch(job.Action)
    {
        case "status": job.Succeed(StatusReport()); return;
        case "frame":
            PreviewControlReports.Int(a,"particles",0,256,0);
            if(a["entryPosition"]!=null) PreviewControlReports.Int(a,"entryPosition",0,1<<20);
            job.Tick=a["tick"]==null?null:PreviewControlReports.Int(a,"tick",0,PreviewPlaybackController.LastTick);
            job.Phase=3; return;
        case "screenshot":
            PreviewControlReports.Int(a,"maxWidth",64,4096,960);
            job.Tick=a["tick"]==null?null:PreviewControlReports.Int(a,"tick",0,PreviewPlaybackController.LastTick);
            job.Phase=3; return;
        case "playback":
        {
            string operation=PreviewControlReports.String(a,"operation")??throw new ArgumentException("缺少 operation。");
            if(operation is not ("play" or "pause" or "seek" or "step" or "replay")) throw new ArgumentException("operation 应为 play、pause、seek、step 或 replay。");
            if(operation=="seek") job.Tick=PreviewControlReports.Int(a,"tick",0,PreviewPlaybackController.LastTick);
            job.Phase=3; return;
        }
        case "view":
        {
            if(PreviewControlReports.Bool(a,"reset")) {camera.Target=new Vector3(0,.4f,0);camera.Distance=4f;camera.Yaw=0;camera.Pitch=.12f;exposure=1f;bloomIntensity=.8f;}
            if(a["target"] is JsonArray target)
            {
                if(target.Count!=3) throw new ArgumentException("target 需要 3 个数值。");
                var t=target.Select(v=>v is JsonValue n && n.TryGetValue(out double d) && double.IsFinite(d) && Math.Abs(d)<1e4 ? (float)d : throw new ArgumentException("target 必须是有限数值。")).ToArray();
                camera.Target=new Vector3(t[0],t[1],t[2]);
            }
            else if(a["target"]!=null) throw new ArgumentException("target 必须是数组。");
            if(PreviewControlReports.Float(a,"distance",OrbitCamera.MinDistance,OrbitCamera.MaxDistance) is float distance) camera.Distance=distance;
            if(PreviewControlReports.Float(a,"yaw",-100,100) is float yaw) camera.Yaw=yaw;
            if(PreviewControlReports.Float(a,"pitch",OrbitCamera.MinPitch,OrbitCamera.MaxPitch) is float pitch) camera.Pitch=pitch;
            if(PreviewControlReports.Float(a,"exposure",.1f,5f) is float e) exposure=e;
            if(PreviewControlReports.Float(a,"bloom",0,2) is float b) bloomIntensity=b;
            if(a["showPoints"]!=null) showPoints=PreviewControlReports.Bool(a,"showPoints");
            if(a["soloSelected"]!=null) soloSelected=PreviewControlReports.Bool(a,"soloSelected");
            if(a["demoMotion"]!=null) SetDemoMotion(PreviewControlReports.Bool(a,"demoMotion"));
            if(PreviewControlReports.String(a,"quality") is {} q)
                quality=q switch {"auto"=>0,"full"=>1,"half"=>2,"quarter"=>3,_=>throw new ArgumentException("quality 应为 auto、full、half 或 quarter。")};
            job.Succeed(ViewReport()); return;
        }
        case "inspect":
            if(a["attributeIndex"]==null) {job.Succeed(new JsonObject{["document"]=PreviewControlReports.Document(editorState)});return;}
            PreviewControlReports.Int(a,"attributeIndex",0,4096);
            AttributeTarget(0);
            job.Phase=1; return;
        case "set_fields":
        {
            PreviewControlReports.Int(a,"attributeIndex",0,4096);PreviewControlReports.Long(a,"expectedRevision");
            if(a["fields"] is not JsonArray fields || fields.Count is <1 or >64) throw new ArgumentException("fields 需要 1–64 项。");
            var paths=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in fields)
            {
                if(field is not JsonObject f || PreviewControlReports.String(f,"path") is not {Length:>0} path || PreviewControlReports.String(f,"value") is null || f.Count!=2)
                    throw new ArgumentException("fields 的每项只能包含字符串 path 与 value。");
                if(!paths.Add(path)) throw new ArgumentException("字段路径重复："+path);
            }
            RequireNoDraft();AttributeTarget(0);
            job.Phase=1; return;
        }
        case "undo": case "redo":
            PreviewControlReports.Long(a,"expectedRevision");RequireNoDraft();
            job.Phase=1; return;
        case "attribute_types":
            AttributeTarget(0);
            job.Phase=1; return;
        case "composition":
        {
            foreach(var (name,setter) in new (string,Action<bool>)[]{("fades",v=>composition.Fades=v),("alphaCorrection",v=>composition.AlphaCorrection=v),
                ("rgbLayers",v=>composition.RgbLayers=v),("softParticles",v=>composition.SoftParticles=v),("depthBias",v=>composition.DepthBias=v),
                ("detonemap",v=>composition.Detonemap=v),("alphaRate",v=>composition.AlphaRate=v),("sortEntries",v=>composition.SortEntries=v),
                ("sortWithinEntry",v=>composition.SortWithinEntry=v),("textureFilter",v=>composition.TextureFilter=v),
                ("texelChannel",v=>composition.TexelChannel=v),("frameBlend",v=>composition.FrameBlend=v),("lighting",v=>composition.Lighting=v),("distortion",v=>composition.Distortion=v),
                ("decals",v=>composition.Decals=v),("decalScaleXYRandom",v=>composition.DecalScaleXYRandom=v)})
                if(a[name]!=null) setter(PreviewControlReports.Bool(a,name));
            if(PreviewControlReports.Float(a,"physicalGain",0,16) is float g) composition.PhysicalGain=g;
            if(PreviewControlReports.Float(a,"ambient",0,4) is float amb) composition.Ambient=new(amb);
            if(PreviewControlReports.Float(a,"keyLight",0,4) is float key) composition.KeyLight=key;
            if(PreviewControlReports.Float(a,"decalScale",.01f,100) is float ds) composition.DecalScale=ds;
            if(a["blends"] is JsonArray blends)
                foreach(var item in blends)
                {
                    if(item is not JsonObject o) throw new ArgumentException("blends 的每项需要 blend、source、destination。");
                    int b=PreviewControlReports.Int(o,"blend",0,4);
                    BlendFactor Parse(string key)=>Enum.TryParse<BlendFactor>(PreviewControlReports.String(o,key)??"",out var f)?f:throw new ArgumentException($"{key} 应为 {string.Join("、",Enum.GetNames<BlendFactor>())} 之一。");
                    if(o["source"]!=null) composition.Blends[b].Source=Parse("source");
                    if(o["destination"]!=null) composition.Blends[b].Destination=Parse("destination");
                }
            else if(a["blends"]!=null) throw new ArgumentException("blends 必须是数组。");
            job.Succeed(CompositionReport()); return;
        }
        case "select_entry":
            SelectArgument(a);RequireNoDraft();AttributeTarget(0);
            job.Phase=1; return;
        case "add_attribute":
            if(PreviewControlReports.String(a,"type") is not {Length:>0}) throw new ArgumentException("缺少属性类型 type。");
            if(PreviewControlReports.String(a,"script") is {} named && PreviewAttributeCatalog.Re4.Behaviors.Find(named)==null)
                throw new ArgumentException("script 应为 RE4 用过的 PtBehavior 脚本类之一："+string.Join("、",PreviewAttributeCatalog.Re4.Behaviors.Classes.Select(c=>c.Name)));
            PreviewControlReports.Long(a,"expectedRevision");RequireNoDraft();AttributeTarget(0);
            job.Phase=1; return;
        case "remove_attribute":
            PreviewControlReports.Int(a,"attributeIndex",0,4096);PreviewControlReports.Long(a,"expectedRevision");RequireNoDraft();AttributeTarget(0);
            job.Phase=1; return;
        case "copy_attribute":
            PreviewControlReports.Int(a,"attributeIndex",0,4096);AttributeTarget(0);
            job.Phase=1; return;
        case "paste_attribute":
            if(PreviewControlReports.String(a,"clipboardId") is not {Length:>0}) throw new ArgumentException("缺少 clipboardId（来自 copy_attribute 的结果）。");
            if(a["replaceAttributeIndex"]!=null) PreviewControlReports.Int(a,"replaceAttributeIndex",0,4096);
            PreviewControlReports.Long(a,"expectedRevision");RequireNoDraft();AttributeTarget(0);
            job.Phase=1; return;
        case "curve_key":
        {
            PreviewControlReports.Int(a,"attributeIndex",0,4096);PreviewControlReports.Int(a,"channel",0,4096);PreviewControlReports.Long(a,"expectedRevision");
            if(PreviewControlReports.String(a,"path") is not {Length:>0}) throw new ArgumentException("缺少曲线路径 path。");
            string operation=PreviewControlReports.String(a,"operation")??"";
            if(operation is not ("add" or "update" or "remove")) throw new ArgumentException("operation 应为 add、update 或 remove。");
            if(operation!="add") PreviewControlReports.Int(a,"keyIndex",0,1<<20);
            if(operation!="remove" && (PreviewControlReports.String(a,"time")==null || PreviewControlReports.String(a,"value")==null))
                throw new ArgumentException("add 与 update 需要字符串形式的 time 和 value。");
            foreach(var name in new[]{"interpolation","outX","outY","inX","inY"}) PreviewControlReports.String(a,name);
            RequireNoDraft();AttributeTarget(0);
            job.Phase=1; return;
        }
        case "add_entry": case "duplicate_entry": case "remove_entry": case "copy_entry": case "paste_entry":
        {
            if(editorState?.Manifest==null) throw new InvalidOperationException("尚未打开文档。");
            if(job.Action!="copy_entry") {PreviewControlReports.Long(a,"expectedRevision");RequireNoDraft();}
            if(job.Action is "duplicate_entry" or "remove_entry" or "copy_entry")
            {
                int position=PreviewControlReports.Int(a,"entryPosition",0,1<<20);
                if(scene?.Graph.EmitterAt(position)==null) throw new ArgumentException($"没有位置为 {position} 的 entry。");
                if(job.Action=="remove_entry" && RemoveFallback(position)<0) throw new InvalidOperationException("这是文件里唯一能预览的 entry，删除后没有可编辑的目标。");
            }
            if(job.Action=="add_entry" && PreviewControlReports.String(a,"script") is {} script)
            {
                if(PreviewAttributeCatalog.Re4.Behaviors.Find(script) is not {Carrier.Count:>0})
                    throw new ArgumentException("script 应为 RE4 用过的 PtBehavior 脚本类之一："+string.Join("、",PreviewAttributeCatalog.Re4.Behaviors.Classes.Where(c=>c.Carrier.Count>0).Select(c=>c.Name)));
            }
            else if(job.Action=="add_entry" && !PreviewStructureMenu.NewEntryTypes.Contains(PreviewControlReports.String(a,"type")))
                throw new ArgumentException("type 应为 "+string.Join("、",PreviewStructureMenu.NewEntryTypes)+" 之一，或用 script 新建脚本载体。");
            if(job.Action=="paste_entry" && (copyBuffer?["kind"]?.GetValue<string>()!="entry" || copyBuffer["id"]?.GetValue<string>()!=PreviewControlReports.String(a,"clipboardId")))
                throw new InvalidOperationException("复制缓冲区里不是这个 entry；先 copy_entry，再用结果里的 copyBuffer.id。");
            job.Phase=1; return;
        }
        case "save_copy":
            if(PreviewControlReports.String(a,"path") is {} savePath && !Path.IsPathFullyQualified(savePath)) throw new ArgumentException("path 必须是完整的绝对路径；省略时保存到会话目录。");
            if(editorState?.Manifest==null) throw new InvalidOperationException("尚未打开文档。");
            job.Phase=1; return;
        case "open":
        {
            int choices=new[]{"fixture","path"}.Count(k=>a[k]!=null);
            if(choices!=1) throw new ArgumentException("open 需要 fixture 或 path 其中之一。");
            if(a["path"]!=null)
            {
                if(new[]{"entryPosition","entryId","offset"}.Count(k=>a[k]!=null)!=1) throw new ArgumentException("path 需要配合 entryPosition、entryId 或 offset 其中之一。");
                if(!Path.IsPathFullyQualified(PreviewControlReports.String(a,"path")!)) throw new ArgumentException("path 必须是完整的绝对路径。");
            }
            RequireNoDraft();
            if(editorState?.Dirty==true && !PreviewControlReports.Bool(a,"discardUnsaved"))
                throw new InvalidOperationException("当前文档有未保存的修改。先 save_copy，或传 discardUnsaved=true 放弃这些修改。");
            job.Deadline=DateTime.UtcNow.AddSeconds(120);
            job.Phase=1; return;
        }
        case "close":
            if(editorState?.Dirty==true && !PreviewControlReports.Bool(a,"discardUnsaved"))
                throw new InvalidOperationException("当前文档有未保存的修改。先 save_copy，或传 discardUnsaved=true 放弃这些修改。");
            RequireNoDraft();
            job.Succeed(new JsonObject{["closing"]=true});closeRequested=true; return;
        default: job.Fail("unknown_action","未知操作："+job.Action); return;
    }
}
Task<PreviewEditorState> StartBackend(PreviewControlRequest job,string action,JsonObject body,long? revision)
    => Task.Run(async()=>{var r=await editor.SendRawAsync(action,body,revision);job.BackendReply=r.Reply;copyBuffer=r.Reply["copyBuffer"] as JsonObject;return r.State;});
Task<PreviewEditorState> StartOpen(JsonObject a)
{
    string? fixture=PreviewControlReports.String(a,"fixture");
    string? path=PreviewControlReports.String(a,"path");
    int? position=a["entryPosition"]==null?null:PreviewControlReports.Int(a,"entryPosition",0,1<<20);
    int? entryId=a["entryId"]==null?null:PreviewControlReports.Int(a,"entryId",0,int.MaxValue);
    int? offset=a["offset"] switch
    {
        null=>null,
        JsonValue v when v.TryGetValue(out long n) && n is >=0 and <=int.MaxValue=>(int)n,
        JsonValue v when v.TryGetValue(out string? s) && int.TryParse(s.StartsWith("0x",StringComparison.OrdinalIgnoreCase)?s[2..]:s,System.Globalization.NumberStyles.HexNumber,null,out int h)=>h,
        _=>throw new ArgumentException("offset 应为整数或十六进制字符串（如 \"0x2e29\"）。")
    };
    return Task.Run(async()=>
    {
        if(fixture!=null)
        {
            var location=PreviewDocument.Fixture(gameDirectory,fixture);
            return await editor.OpenAsync(location.Path,PreviewDocument.ReadEntryId(location.Path,location.Offset));
        }
        if(position is int p) return await editor.OpenAtAsync(path!,p);
        return await editor.OpenAsync(path!,entryId??PreviewDocument.ReadEntryId(path!,offset!.Value));
    });
}
void ContinueControl(PreviewControlRequest job)
{
    var a=job.Arguments;
    switch(job.Phase)
    {
        case 1:
        {
            if(editTask!=null) return;
            if(IsMutation(job.Action)) RequireNoDraft();
            Task<PreviewEditorState> task=job.Action switch
            {
                "inspect"=>StartBackend(job,"inspect",new(){["target"]=AttributeTarget(PreviewControlReports.Int(a,"attributeIndex",0,4096))},null),
                "set_fields"=>StartBackend(job,"set_fields",new(){["target"]=AttributeTarget(PreviewControlReports.Int(a,"attributeIndex",0,4096)),["fields"]=a["fields"]!.DeepClone()},
                    PreviewControlReports.Long(a,"expectedRevision")),
                "undo" or "redo"=>StartBackend(job,job.Action,new(),PreviewControlReports.Long(a,"expectedRevision")),
                "attribute_types"=>StartBackend(job,"status",new(),null),
                "select_entry"=>SelectArgument(a) is {IsRoot:false} embedded ? Task.Run(()=>editor.SelectTargetAsync(embedded))
                    : StartBackend(job,"configure_offline_preview",new(){["entryPosition"]=PreviewControlReports.Int(a,"entryPosition",0,1<<20)},null),
                "add_attribute"=>StartBackend(job,"add_attribute",AddAttributeBody(EntryTarget(),PreviewControlReports.String(a,"type")!,PreviewControlReports.String(a,"script")),
                    PreviewControlReports.Long(a,"expectedRevision")),
                "remove_attribute"=>StartBackend(job,"remove_attribute",new(){["target"]=AttributeTarget(PreviewControlReports.Int(a,"attributeIndex",0,4096))},PreviewControlReports.Long(a,"expectedRevision")),
                "copy_attribute"=>StartBackend(job,"copy_efx",new(){["target"]=AttributeTarget(PreviewControlReports.Int(a,"attributeIndex",0,4096))},null),
                "paste_attribute"=>StartBackend(job,"paste_efx",PasteBody(a),PreviewControlReports.Long(a,"expectedRevision")),
                "curve_key"=>StartBackend(job,"curve_key",CurveBody(a),PreviewControlReports.Long(a,"expectedRevision")),
                "add_entry" or "duplicate_entry" or "remove_entry" or "copy_entry" or "paste_entry"=>StartEntryAction(job),
                "save_copy"=>Task.Run(()=>editor.CommandAsync("save",PreviewControlReports.String(a,"path"))),
                "open"=>StartOpen(a),
                _=>throw new InvalidOperationException("内部错误：未知的编辑操作。")
            };
            editAction="ai:"+job.Action;editError=null;
            editTask=task;job.EditTask=task;job.Phase=2;
            return;
        }
        case 2:
        {
            if(!job.EditTask!.IsCompleted || ReferenceEquals(editTask,job.EditTask)) return;
            if(job.EditTask.IsFaulted)
            {
                var ex=job.EditTask.Exception!.GetBaseException();
                job.Fail(ex switch {PreviewBackendException=>"backend_rejected",ArgumentException=>"invalid_arguments",TimeoutException=>"outcome_uncertain",_=>"editor_error"},
                    ex.Message,new JsonObject{["document"]=PreviewControlReports.Document(editor.State)});
                return;
            }
            var state=job.EditTask.Result;
            if(job.Action=="inspect")
            {
                int inspectedAttribute=PreviewControlReports.Int(a,"attributeIndex",0,4096);
                var result=PreviewControlReports.Inspection(job.BackendReply!,inspectedAttribute);
                result["document"]=PreviewControlReports.Document(state);
                // The window's property panel and curve timeline follow what the AI looks at (never over a draft).
                if(state.EditTarget is {} t) result["windowFocused"]=editPanel.Focus(new PreviewGraphPick(t,inspectedAttribute));
                job.Succeed(result);
            }
            else if(job.Action=="save_copy") job.Succeed(new JsonObject{["savedPath"]=state.SavedPath,["document"]=PreviewControlReports.Document(state)});
            else if(job.Action=="attribute_types")
                job.Succeed(new JsonObject{["types"]=job.BackendReply?["document"]?["attributeTypes"]?.DeepClone()??new JsonArray(),
                    ["catalog"]=PreviewControlReports.Catalog(state),["document"]=PreviewControlReports.Document(state)});
            else if(job.Action is "copy_attribute" or "copy_entry")
                job.Succeed(new JsonObject{["copyBuffer"]=job.BackendReply?["copyBuffer"]?.DeepClone(),["document"]=PreviewControlReports.Document(state)});
            else job.Phase=4;
            return;
        }
        case 3:
        {
            if(!Settled(out string? error)) return;
            bool loadFailed=lastLoadError!=null && desiredManifest?.Key!=displayedManifest?.Key;
            if(error!=null && (frame==null || loadFailed)) {job.Fail("preview_unavailable",error,StatusReport());return;}
            switch(job.Action)
            {
                case "frame": case "screenshot":
                    if(job.Tick is int t && (playback.IsPlaying || frame!.Tick!=t)) playback.Seek(t);
                    break;
                case "playback":
                    switch(PreviewControlReports.String(a,"operation"))
                    {
                        case "play": if(!playback.IsPlaying) playback.TogglePlayback(); job.Succeed(PreviewReport()); return;
                        case "pause": playback.Pause(); break;
                        case "seek": playback.Seek(job.Tick!.Value); break;
                        case "step": playback.Step(); break;
                        case "replay": QueuePreview(desiredManifest!,autoplay:false,force:true,restart:true); break;
                    }
                    break;
            }
            job.Phase=4;
            return;
        }
        case 4:
        {
            if(!Settled(out string? error)) return;
            // An entry excluded on the way (worker failure) reloads at the last shown tick: seek again.
            if(job.Action is "frame" or "screenshot" && job.Tick is int wanted && frame!=null && !playback.IsPlaying && frame.Tick!=wanted)
            { playback.Seek(wanted); return; }
            switch(job.Action)
            {
                case "screenshot":
                    if(error!=null && (billboards==null || billboards.Drawn==0 || soloSelected)) {job.Fail("preview_unavailable",error,StatusReport());return;}
                    job.Tick=frame!.Tick;captureRequest=job;job.Phase=5;return;
                case "frame":
                {
                    if(frame==null) {job.Fail("preview_unavailable",error??"没有可读取的帧。",StatusReport());return;}
                    int? onlyEntry=a["entryPosition"]==null?null:PreviewControlReports.Int(a,"entryPosition",0,1<<20);
                    var result=PreviewControlReports.Frame(frame,scene,displayedManifest,PreviewControlReports.Int(a,"particles",0,256,0),onlyEntry);
                    if(error!=null) result["warning"]=error;
                    job.Succeed(result);return;
                }
                case "playback": job.Succeed(PreviewReport()); return;
                default:
                    // The edit is committed; a preview that cannot show it is reported, not an error.
                {
                    var result=new JsonObject{["document"]=PreviewControlReports.Document(editorState),["preview"]=PreviewReport(),["previewError"]=error};
                    foreach(var key in new[]{"selection","deletion","message"})
                        if(job.BackendReply?[key] is {} extra) result[key]=extra.DeepClone();
                    job.Succeed(result);
                    return;
                }
            }
        }
        case 5:
        {
            if(job.Pixels==null) return;
            int maxWidth=PreviewControlReports.Int(a,"maxWidth",64,4096,960);
            byte[] pixels=job.Pixels;int width=job.PixelWidth,height=job.PixelHeight,tick=job.Tick!.Value;
            long? revision=displayedManifest?.Revision;
            string directory=Path.Combine(editor.Directory,"ai-captures");
            job.Encoding=Task.Run(()=>
            {
                using var image=Image.LoadPixelData<Rgba32>(pixels,width,height);
                image.Mutate(x=>x.Flip(FlipMode.Vertical));
                if(image.Width>maxWidth) image.Mutate(x=>x.Resize(maxWidth,0));
                Directory.CreateDirectory(directory);
                string path=Path.Combine(directory,$"r{revision}_t{tick}_{DateTime.Now:HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.png");
                using var memory=new MemoryStream();
                image.SaveAsPng(memory);
                File.WriteAllBytes(path,memory.ToArray());
                return new JsonObject{["path"]=path,["width"]=image.Width,["height"]=image.Height,["tick"]=tick,["revision"]=revision,
                    ["mimeType"]="image/png",["data"]=Convert.ToBase64String(memory.ToArray())};
            });
            captureRequest=null;job.Phase=6;
            return;
        }
        case 6:
            if(!job.Encoding!.IsCompleted) return;
            if(job.Encoding.IsFaulted) job.Fail("failed","截图保存失败："+job.Encoding.Exception!.GetBaseException().Message);
            else job.Succeed(job.Encoding.Result);
            return;
    }
}

void DrawComposition()
{
    if(!ImGui.CollapsingHeader("合成配置")) return;
    ImGui.TextWrapped("各项为折中实现，可单独关闭以对照效果。");
    bool Toggle(string label,bool value) {ImGui.Checkbox(label,ref value);return value;}
    composition.Fades=Toggle("深度 / 角度淡出",composition.Fades);
    composition.AlphaCorrection=Toggle("AlphaCorrection",composition.AlphaCorrection);
    composition.RgbLayers=Toggle("RgbCommon / RgbWater",composition.RgbLayers);
    composition.SoftParticles=Toggle("软粒子",composition.SoftParticles);
    composition.DepthBias=Toggle("深度偏移",composition.DepthBias);
    composition.Detonemap=Toggle("反色调映射（DetonemapRatio）",composition.Detonemap);
    composition.AlphaRate=Toggle("alpha 乘颜色头 AlphaRate",composition.AlphaRate);
    composition.SortEntries=Toggle(scene?.Graph.EmitterPriority==true?"entry 排序（EmitterPriority：按文件顺序）":"entry 按距离排序",composition.SortEntries);
    if(ImGui.IsItemHovered()) ImGui.SetTooltip("文件的 Root entry 带 EmitterPriority 时按 entry 顺序绘制（靠后的在上面），否则按到相机的距离从远到近。关闭则一律按文件顺序。");
    composition.SortWithinEntry=Toggle("entry 内按深度排序",composition.SortWithinEntry);
    composition.TextureFilter=Toggle("TextureFilter（纹素 alpha 整形）",composition.TextureFilter);
    composition.TexelChannel=Toggle("TexelChannelOperator（黑体色）",composition.TexelChannel);
    composition.FrameBlend=Toggle("FrameBlend（翻页交叉淡化）",composition.FrameBlend);
    composition.Distortion=Toggle("Distortion（屏幕扭曲）",composition.Distortion);
    composition.Decals=Toggle("贴花（EffectDecal 近似）",composition.Decals);
    if(ImGui.IsItemHovered()) ImGui.SetTooltip("游戏脚本把贴花投到地形上；预览只有 y = 0 的地面：载体粒子在地面以上 SearchTerrainDistance 以内时，\n"+
        "在它正下方铺一张贴图（预制体的 ALBA），颜色乘 BaseColorMod，透明度随粒子 Life 淡出，按 RandomRollRange 随机旋转。\n尺寸规则是推断的，用下面的系数对照游戏校准。");
    ImGui.BeginDisabled(!composition.Decals);
    float decalScale=composition.DecalScale;
    if(ImGui.SliderFloat("贴花尺寸系数",ref decalScale,.05f,10f,"%.3f",ImGuiSliderFlags.Logarithmic)) composition.DecalScale=decalScale;
    if(ImGui.IsItemHovered()) ImGui.SetTooltip("宽 = 2 × Extent.X × 缩放 × 系数，深 = 2 × Extent.Z × 缩放 × 系数（米，整宽；Extent 是半边长）。\n"+
        "已在游戏里对照过：Extent 1、ScaleXY (1, 0) 的贴花约为 1 m Polygon 的 2 倍，所以系数 1 就是游戏的大小。");
    bool random=composition.DecalScaleXYRandom;
    if(ImGui.Checkbox("ScaleXY 当作随机缩放（X + 随机 × Y）",ref random)) composition.DecalScaleXYRandom=random;
    if(ImGui.IsItemHovered()) ImGui.SetTooltip("关：ScaleXY 是宽、深两个方向的缩放。开：ScaleXY.X 是基础缩放，ScaleXY.Y 是随机增加的范围，宽深同比。");
    ImGui.EndDisabled();
    composition.Lighting=Toggle("受光粒子（预览光照）",composition.Lighting);
    ImGui.BeginDisabled(!composition.Lighting);
    float ambient=composition.Ambient.X,key=composition.KeyLight;
    if(ImGui.SliderFloat("环境光",ref ambient,0,2)) composition.Ambient=new(ambient);
    if(ImGui.SliderFloat("主光",ref key,0,2)) composition.KeyLight=key;
    ImGui.EndDisabled();
    if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("预览没有场景光源：受光粒子按 环境光 + 主光 × 半 Lambert 着色（主光从右上前方照来）。");
    float gain=composition.PhysicalGain;
    if(ImGui.SliderFloat("Physical 受光增益",ref gain,0,4)) composition.PhysicalGain=gain;
    string[] blendNames=["0 AlphaBlend","1 Physical","2 AddContrast","3 EdgeBlend","4 Multiply"];
    string factors=string.Join("\0",Enum.GetNames<BlendFactor>())+"\0";
    for(int b=0;b<5;b++)
    {
        int src=(int)composition.Blends[b].Source,dst=(int)composition.Blends[b].Destination;
        ImGui.PushID(b);
        ImGui.Text(blendNames[b]);
        ImGui.SetNextItemWidth(150);if(ImGui.Combo("源",ref src,factors)) composition.Blends[b].Source=(BlendFactor)src;
        ImGui.SameLine();ImGui.SetNextItemWidth(150);if(ImGui.Combo("目标",ref dst,factors)) composition.Blends[b].Destination=(BlendFactor)dst;
        ImGui.PopID();
    }
    if(scene?.AtTable(SelectedTable())?.Document is {} doc)
    {
        var a=EntryAppearance.From(doc.Entry);
        ImGui.TextWrapped($"所选 entry：软粒子 {a.SoftParticleDistance:G3} m · 深度偏移 {a.DepthBias:G3} · Detonemap {a.DetonemapRatio:G3}"+
            (a.FadeDepth!=null?" · FadeByDepth":"")+(a.FadeAngle!=null?" · FadeByAngle":"")+(a.AlphaCorrection is {} ac?$" · AlphaCorrection 模式 {ac.Mode}":"")+
            (a.RgbCommon!=null?" · RgbCommon":"")+(a.RgbWater!=null?" · RgbWater":""));
    }
}

void DrawEntrySelector()
{
    var current=scene!.Entries.FirstOrDefault(e=>e.Position==displayedManifest?.EntryPosition);
    string Label(PreviewSceneEntry e)=>$"[{e.Position}] {e.Name} · {e.Type}{(e.Shown?"":" · 未显示")}{(e.Missing.Count>0?" · 缺资源（占位贴图）":"")}";
    int topShown=scene.Entries.Count(e=>e.Shown);
    ImGui.Text($"显示 {topShown} / {scene.Entries.Count} 个 entry");
    if(scene.Children.Count>0)
    {
        ImGui.SameLine();ImGui.TextDisabled($"· 子 Action 运行 {scene.Children.Count(c=>c.Shown)} / {scene.Children.Count} 个内嵌 entry");
        if(ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n",scene.Children.Select(c=>$"{c.Label} {c.Name} · {c.Type}"+(c.Excluded==null?"":" · "+c.Excluded))));
    }
    ImGui.BeginDisabled(editTask!=null || editPanel.HasUnappliedChanges);
    if(ImGui.BeginCombo("编辑 entry",current==null?"未选择":Label(current)))
    {
        foreach(var e in scene.Entries)
        {
            // Every entry can be edited; ones the preview does not draw are listed with the reason in the tooltip.
            bool selectable=scene.Graph.EmitterAt(e.Position)?.Editable==true;
            if(ImGui.Selectable(Label(e),e.Position==current?.Position,selectable?ImGuiSelectableFlags.None:ImGuiSelectableFlags.Disabled) && selectable && (e.Position!=current?.Position || editorState?.Target!=null) && editorState!=null)
                StartEditorCommand(new PreviewEditCommand("select_entry",editorState.Revision,EntryPosition:e.Position));
            if(e.Excluded!=null && ImGui.IsItemHovered()) ImGui.SetTooltip(e.Excluded);
            else if(e.Missing.Count>0 && ImGui.IsItemHovered()) ImGui.SetTooltip("找不到这些资源，用黄白格子占位贴图代替：\n"+string.Join("\n",e.Missing));
        }
        ImGui.EndCombo();
    }
    ImGui.EndDisabled();
    if(editPanel.HasUnappliedChanges && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("先应用或放弃属性草稿，再切换 entry。");
    ImGui.BeginDisabled(editTask!=null || editPanel.HasUnappliedChanges || stress!=0 || editorState?.Manifest==null);
    if(ImGui.SmallButton("entry 操作…")) ImGui.OpenPopup("entry 操作");
    ImGui.EndDisabled();
    if(ImGui.BeginPopup("entry 操作"))
    {
        string? kind=copyBuffer?["kind"]?.GetValue<string>();
        var chosen=PreviewStructureMenu.FileItems(kind,copyBuffer?["name"]?.GetValue<string>());
        if(current!=null)
        {
            ImGui.Separator();ImGui.TextDisabled($"当前 [{current.Position}] {current.Name}");
            chosen??=PreviewStructureMenu.EntryItems(current.Position,kind,copyBuffer?["attributeType"]?.GetValue<string>(),true);
        }
        ImGui.EndPopup();
        if(chosen!=null) RequestStructure(chosen);
    }
    if(copyBuffer?["kind"] is {} copiedKind)
    {
        ImGui.SameLine();ImGui.TextDisabled($"已复制：{(copiedKind.GetValue<string>()=="entry"?"entry ":"")}{copyBuffer["attributeType"]??copyBuffer["name"]}");
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("来自 "+copyBuffer["sourceName"]+"。打开其他文件后仍可粘贴。");
    }
    ImGui.Checkbox("只显示所选 entry",ref soloSelected);
    if(graphPanel!=null) {ImGui.SameLine();ImGui.Checkbox("节点图",ref graphPanel.Visible);}
    ImGui.SameLine();ImGui.Checkbox("曲线时间轴",ref curvePanel.Visible);
    ImGui.SameLine();ImGui.Checkbox("属性目录",ref catalogPanel.Visible);
    if(docking)
    {
        if(ImGui.SmallButton("重置布局")) {resetLayout=true;curvePanel.Visible=true;catalogPanel.Visible=true;if(graphPanel!=null) graphPanel.Visible=true;}
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("面板可拖到窗口四边停靠，拖动标题栏离开即可浮动；这里恢复默认停靠布局。");
    }
    ImGui.TextDisabled("把 .efx 文件拖进窗口即可打开");
    // Editing does not depend on drawing: a selected entry the preview cannot draw is still edited normally.
    if(current?.Excluded is {} reason) ImGui.TextDisabled("所选 entry 不绘制（"+reason.TrimEnd('。')+"），属性照常编辑。");
    var placeholders=scene.Entries.Concat(scene.Children).Where(e=>e.Shown && e.Missing.Count>0).ToArray();
    if(placeholders.Length>0)
    {
        ImGui.TextColored(new Vector4(1,.75f,.35f,1),$"{placeholders.Length} 个 entry 缺资源，用黄白格子代替");
        if(ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n",placeholders.Select(e=>$"{e.Label} {e.Name}: {string.Join(", ",e.Missing)}")));
    }
    if(current?.Decal is {} decal)
    {
        var size=PreviewDecalRenderer.Footprint(decal,composition);
        string sizeText=composition.DecalScaleXYRandom
            ? $"{size.X:F3} × {size.Y:F3} ~ {PreviewDecalRenderer.Footprint(decal,composition,1).X:F3} × {PreviewDecalRenderer.Footprint(decal,composition,1).Y:F3} m"
            : $"{size.X:F3} × {size.Y:F3} m";
        ImGui.TextUnformatted($"贴花 {Path.GetFileNameWithoutExtension(decal.Prefab)}：{sizeText}（系数 {composition.DecalScale:G3}）");
        if(ImGui.IsItemHovered()) ImGui.SetTooltip($"{decal.Prefab}\nExtent {decal.Extent.X:G4}, {decal.Extent.Y:G4}, {decal.Extent.Z:G4} · ScaleXY {decal.ScaleXY.X:G4}, {decal.ScaleXY.Y:G4}"+
            $"\n只在载体粒子离地面 0 ~ {decal.SearchDistance:G3} m 时出现 · 随机旋转 ±{decal.RollRange:G3}°");
        if(decal.TextureError!=null) ImGui.TextColored(new Vector4(1,.75f,.35f,1),"贴花贴图读取失败："+decal.TextureError);
    }
    var native=scene.Entries.Where(e=>e.Excluded?.StartsWith("原生模拟器")==true).ToArray();
    if(native.Length>0 && ImGui.TreeNode($"原生模拟器排除的 entry（{native.Length}）"))
    {
        foreach(var e in native) ImGui.TextWrapped($"[{e.Position}] {e.Name}：{e.Excluded}");
        ImGui.TreePop();
    }
}

window.Load += () =>
{
    gl = GL.GetApi(window);
    input = window.CreateInput();
    // Runs before the controller's first NewFrame: docking and the layout file must be set by then.
    unsafe void ConfigureImGui()
    {
        var io = ImGui.GetIO();
        io.IniFilename = null;
        if(!docking) return;
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        Directory.CreateDirectory(Path.GetDirectoryName(layoutFile)!);
        resetLayout=!File.Exists(layoutFile);
        dockCatalog=!resetLayout && !File.ReadAllText(layoutFile).Contains("[Window][属性目录]");
        // ImGui keeps the pointer: allocated once for the window's lifetime.
        layoutFileName=System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(layoutFile);
        io.IniFilename=(byte*)layoutFileName;
    }
    gui = new ImGuiController(gl, window, input, ConfigureImGui);
    graphPanel = new PreviewGraphPanel();
    window.FileDrop += files => { if (files.Length > 0) { pendingOpen = files[0]; dropDiscardAccepted = false; } };

    grid = new ViewportGridRenderer(gl);
    points = new ParticlePointRenderer(gl);
    hdr=new PreviewHdrPipeline(gl,window.FramebufferSize.X,window.FramebufferSize.Y);
    presentFbo=gl.GenFramebuffer();
    particleLayer=new ParticleLayer(gl);
    gpuCache=new BillboardGpuCache(gl);
    latency?.Configure(new { fixture=options.GetValueOrDefault("fixture"),width=window.FramebufferSize.X,height=window.FramebufferSize.Y,
        vsync=window.VSync,quality,gpu=gl.GetStringS(StringName.Renderer),
        manifestPollMs,
        endpoint="first matching revision rendered + glFinish; before buffer swap, compositor and scanout",
        externalRoute=$"same private backend writer; {manifestPollMs} ms manifest polling; snapshot load overlaps status; not MCP",warmupsPerGroup=2 });
    foreach (var mouse in input.Mice) mouse.Scroll += (_, scroll) => { if (!ImGui.GetIO().WantCaptureMouse) camera.Zoom(scroll.Y); };
    editAction="open";
    playback.BeginLoad(restart:true);
    editTask=OpenOriginal();
    if(!options.ContainsKey("no-ai-control") && !options.ContainsKey("benchmark") && !options.ContainsKey("smoke"))
        control=new PreviewControlServer(options.GetValueOrDefault("ai-pipe",PreviewControlServer.DefaultPipe));
};
window.Update += dt =>
{
    gui.Update((float)dt);
    if(latencyBenchmark?.Update(playback,frame,editorState,displayedManifest,editTask!=null,StartEditorCommand)==true)
    { window.Close(); return; }
    if(idleBenchmark?.Update(playback,frame,editTask!=null || loading!=null || faultTask!=null || reloadQueued)==true)
    { window.Close(); return; }
    if(editTask?.IsCompleted==true)
    {
        try
        {
            editorState=editTask.GetAwaiter().GetResult();
            if(editAction is "billboard" or "life" or "fields" or "undo" or "redo" or "publish_offline_preview") latency?.Responded(editorState);
            // AI edits ("ai:*") never discard the panel's draft; mutations are refused while one exists.
            // Structural edits shift attribute indices, so the panel starts over on the new list.
            // Structural edits (and their undo/redo) shift attribute indices: the panel starts over on the new list.
            string attributeList=$"{editorState.Manifest?.DocumentId}:{editorState.EditTarget?.Key}:{string.Join(",",editorState.Attributes?.Select(a=>a.Type)??[])}";
            if(editAction is "ai:add_attribute" or "ai:remove_attribute" or "ai:paste_attribute" ||
                attributeList!=lastAttributeList && !editPanel.HasUnappliedChanges &&
                editAction is not ("select_entry" or "select_target" or "open" or "open_source" or "open_saved" or "ai:open" or "ai:select_entry" or "inspect")) editPanel=new PreviewEditPanel(LoadMaterial);
            lastAttributeList=attributeList;
            // An attribute added or pasted from the window opens in the property panel.
            if(editAction=="structure" && editorState.EditTarget is {} edited)
            {
                int open=focusAttributeIndex>=0?focusAttributeIndex:editorState.Attributes?.FirstOrDefault(a=>a.Type==focusAttributeType)?.Index??-1;
                if(open>=0) editPanel.Focus(new PreviewGraphPick(edited,open));
            }
            focusAttributeType=null;focusAttributeIndex=-1;
            editPanel.Reset(editorState,discardDraft:editAction is "fields" or "billboard" or "life" or "open" or "open_source" or "open_saved" or "ai:open" or "curve_key");
            if(editorState.Manifest is { } manifest && manifest.Revision==editorState.Revision)
                QueuePreview(manifest,editAction switch { "open" or "open_source" or "ai:open" => false, "open_saved" => true, _ => (bool?)null },force:editAction is "publish_offline_preview" or "open_source",restart:editAction is "open" or "open_source" or "open_saved" or "ai:open");
            if(editorState.PublicationError!=null) editError="文档已保留，预览发布失败，可重试或撤销。";
        }
        catch(Exception ex)
        {
            latency?.Fail("editor_error: "+ex.GetBaseException().Message);
            editorState=editor.State;
            if(editorState?.Manifest is {} latest && latest.Revision==editorState.Revision) QueuePreview(latest);
            editError=ex.GetBaseException().Message;
            if(options.ContainsKey("smoke") || options.ContainsKey("benchmark")) {Console.Error.WriteLine(ex);Environment.ExitCode=1;window.Close();}
        }
        editTask=null;
    }
    if(faultTask?.IsCompleted==true)
    {
        try {faultTask.GetAwaiter().GetResult();playback.FinishLoad();status="辅助进程已重启，时间轴保持在 tick "+playback.TargetTick;}
        catch(Exception ex) {status=ex.Message;if(desiredManifest!=null) {playback.BeginLoad(autoplay:false);reloadQueued=true;}}
        faultTask=null;
    }
    if (loading?.IsCompleted == true)
    {
        try
        {
            var loaded=loading.GetAwaiter().GetResult();
            if(loaded.Manifest.SceneKey==desiredManifest?.SceneKey && !reloadQueued)
            {
                var known=Exclusions(loaded.Manifest.DocumentId);
                foreach(var pair in loaded.Excluded) if(pair.Value.StartsWith("原生模拟器")) known[pair.Key]=pair.Value;
                latency?.Loaded(loaded.Manifest.Key,loaded.NativeLoadMs);
                preparedPreview=new(desiredManifest,loaded.Scene,new PreviewSceneRenderer(gl,loaded.Scene,gpuCache));
                playback.FinishLoad();
                status=$"文档 r{loaded.Manifest.Revision} 已加载，tick {playback.TargetTick}";
            }
        }
        catch(Exception ex)
        {
            latency?.Fail("load_error: "+ex.GetBaseException().Message);
            lastLoadError=ex.GetBaseException().Message;
            status="预览加载失败，可重试或撤销："+ex.GetBaseException().Message;
            preparedPreview?.Renderer.Dispose();preparedPreview=null;
            if(options.ContainsKey("smoke") || options.ContainsKey("benchmark")) {Console.Error.WriteLine(ex);Environment.ExitCode=1;window.Close();}
        }
        loading=null;
    }
    if (pending?.IsCompleted == true)
    {
        try
        {
            var nextFrame=pending.GetAwaiter().GetResult();
            if (playback.CompleteRequest(pendingRequest))
            {
                if(preparedPreview!=null)
                {
                    billboards?.Dispose();billboards=preparedPreview.Renderer;
                    scene=preparedPreview.Scene;displayedManifest=preparedPreview.Manifest;
                    preparedPreview=null;
                }
                frame?.Dispose(); frame = nextFrame;
                lastFrameError=null;
                if(displayedManifest!=null) latency?.FrameReady(displayedManifest.Key);
                completedFrames++;
            }
            else nextFrame.Dispose();
        }
        catch (Exception ex)
        {
            latency?.Fail("frame_error: "+ex.GetBaseException().Message);
            // The client has already restarted and reloaded the worker for a native fault; any other
            // error leaves the loaded document intact. Either way playback pauses on the last frame
            // shown and keeps its timeline position; the next request replays from the restored state.
            status = "预览运行失败，已暂停在当前帧，可继续播放、重试或撤销："+ex.Message;
            lastFrameError=ex.Message;
            if(playback.IsBusy) playback.FailRequest(pendingRequest);
            // One entry failing inside the worker (also on the first frame of a newly loaded document)
            // is excluded and the rest reload at the same tick; only other errors keep the old handling.
            var failedScene=preparedPreview?.Scene ?? scene;
            var failedManifest=preparedPreview?.Manifest ?? displayedManifest;
            bool entryExcluded=false;
            if(failedScene!=null && failedManifest!=null &&
                PreviewSceneLoader.ExcludeFailedEntry(failedScene,ex,Exclusions(failedManifest.DocumentId),pendingRequest.Tick) is {} failed)
            {
                entryExcluded=true;
                status=$"entry [{failed.Position}] 在 tick {pendingRequest.Tick} 运行失败，已排除并重新加载其余 entry。";
                preparedPreview?.Renderer.Dispose();preparedPreview=null;
                if(desiredManifest!=null) QueuePreview(desiredManifest,autoplay:false,force:true);
            }
            else if(preparedPreview!=null)
            {
                // The failed request was the first frame of a newly loaded document: reload it.
                preparedPreview.Renderer.Dispose();preparedPreview=null;
                if(desiredManifest!=null) {playback.BeginLoad(autoplay:false);reloadQueued=true;}
            }
            if(!entryExcluded && (options.ContainsKey("smoke") || options.ContainsKey("benchmark"))) { Console.Error.WriteLine(ex); Environment.ExitCode=1; window.Close(); }
        }
        pending = null;
    }
    if (reloadQueued && pending == null && loading == null && faultTask==null) StartReload();
    manifestPoll+=dt;
    if(manifestPoll>=manifestPollMs/1000.0 && editTask==null && editorState?.Manifest!=null)
    {
        // At most one read per update, even after a long frame; never poll without a document.
        manifestPoll=0;
        long pollStarted=latency==null?0:Stopwatch.GetTimestamp();
        try
        {
            var published=editor.ReadPublishedManifest();
            if(published!=null && published.Key!=editorState.Manifest?.Key)
            {
                // The atomic manifest already identifies an immutable, hash-checked snapshot.
                // Load it immediately; the editable form still waits for authoritative status.
                latency?.Published(published);
                QueuePreview(published);
                editAction="watch";
                editTask=Task.Run(()=>editor.CommandAsync("status"));
            }
        }
        catch(Exception ex) {editError=ex.Message;}
        finally {latency?.ManifestPolled(Stopwatch.GetElapsedTime(pollStarted).TotalMilliseconds);}
    }
    if (input.Mice.Count > 0)
    {
        var mouse = input.Mice[0];
        var delta = mouse.Position - previousMouse; previousMouse = mouse.Position;
        if (!ImGui.GetIO().WantCaptureMouse)
        {
            if (mouse.IsButtonPressed(MouseButton.Right)) camera.Rotate(-delta.X * .006f, delta.Y * .006f);
            if (mouse.IsButtonPressed(MouseButton.Middle)) camera.Pan(delta.X, delta.Y);
        }
    }
    playback.Advance(dt);
    if (options.ContainsKey("smoke") && frame != null && playback.TargetTick < smokeTick &&
        !playback.IsBusy && !playback.HasQueuedRequest) playback.Seek(playback.TargetTick + 1);
    if (options.ContainsKey("benchmark") && frame != null && playback.TargetTick < 310 &&
        !playback.IsBusy && !playback.HasQueuedRequest) playback.Seek(playback.TargetTick + 1);
    if (loading == null && faultTask==null && !reloadQueued && playback.TryBeginRequest(out var request))
    {
        pendingRequest = request;
        // Capture this request, not a mutable timeline field.
        pending = Task.Run(() => client.FrameAsync(request.Tick));
    }
    UpdateControl();
    if(closeRequested) window.Close();
};
unsafe void UpdateDockspace()
{
    var viewport=ImGui.GetMainViewport();
    uint dockspace=ImGui.GetID("Re4PreviewDockspace");
    if(resetLayout)
    {
        resetLayout=false;
        // Default: main panel top left beside the scene; the attribute catalogue, node graph (curve timeline as a
        // tab behind it) and property panel share the whole bottom.
        ImGuiP.DockBuilderRemoveNode(dockspace);
        ImGuiP.DockBuilderAddNode(dockspace,(ImGuiDockNodeFlags)(1<<10)|ImGuiDockNodeFlags.PassthruCentralNode);   // 1<<10 = DockSpace
        ImGuiP.DockBuilderSetNodeSize(dockspace,viewport.Size);
        uint upper,bottom,left,center,catalog,rest,property,graph;
        ImGuiP.DockBuilderSplitNode(dockspace,ImGuiDir.Down,.46f,&bottom,&upper);
        ImGuiP.DockBuilderSplitNode(upper,ImGuiDir.Left,.24f,&left,&center);
        ImGuiP.DockBuilderSplitNode(bottom,ImGuiDir.Left,.22f,&catalog,&rest);
        ImGuiP.DockBuilderSplitNode(rest,ImGuiDir.Right,.45f,&property,&graph);
        ImGuiP.DockBuilderDockWindow("RE4 原生预览 · P2",left);
        ImGuiP.DockBuilderDockWindow("属性目录",catalog);
        ImGuiP.DockBuilderDockWindow("EFX 属性编辑",property);
        ImGuiP.DockBuilderDockWindow("EFX 节点图",graph);
        ImGuiP.DockBuilderDockWindow("曲线时间轴",graph);
        ImGuiP.DockBuilderFinish(dockspace);
        selectGraphTab=10;
        dockCatalog=false;
    }
    else if(dockCatalog && ImGuiP.FindWindowByName("RE4 原生预览 · P2") is {IsNull:false} main && main.DockId!=0)
    {
        // Same tab bar as the main panel; dragging it elsewhere is saved as usual.
        ImGuiP.DockBuilderDockWindow("属性目录",main.DockId);
        dockCatalog=false;
    }
    // After a reset the node graph, not the curve timeline, is the tab in front. Selecting it while building is
    // overridden when the panels first appear, so ask the tab bar once both exist.
    if(selectGraphTab>0)
    {
        selectGraphTab--;
        var graphWindow=ImGuiP.FindWindowByName("EFX 节点图");
        if(!graphWindow.IsNull && !graphWindow.DockNode.IsNull && !graphWindow.DockNode.TabBar.IsNull)
        {graphWindow.DockNode.TabBar.NextSelectedTabId=graphWindow.TabId;selectGraphTab=0;}
    }
    ImGui.DockSpaceOverViewport(dockspace,viewport,ImGuiDockNodeFlags.PassthruCentralNode);
    // The scene fills the central node (what docked panels leave free), in framebuffer pixels.
    var central=ImGuiP.DockBuilderGetCentralNode(dockspace);
    var scale=ImGui.GetIO().DisplayFramebufferScale;
    int fbW=Math.Max(1,window.FramebufferSize.X), fbH=Math.Max(1,window.FramebufferSize.Y);
    if(central.IsNull || central.Size.X<16 || central.Size.Y<16) {sceneRect=(0,0,fbW,fbH);return;}
    var pos=central.Pos-viewport.Pos;
    int x=Math.Clamp((int)MathF.Round(pos.X*scale.X),0,fbW-1), top=Math.Clamp((int)MathF.Round(pos.Y*scale.Y),0,fbH-1);
    int w=Math.Clamp((int)MathF.Round(central.Size.X*scale.X),1,fbW-x), h=Math.Clamp((int)MathF.Round(central.Size.Y*scale.Y),1,fbH-top);
    sceneRect=(x,fbH-top-h,w,h);
}
window.Render += _ =>
{
    if(docking) UpdateDockspace();
    else sceneRect=(0,0,Math.Max(1,window.FramebufferSize.X),Math.Max(1,window.FramebufferSize.Y));
    gl.Viewport(0, 0, (uint)window.FramebufferSize.X, (uint)window.FramebufferSize.Y);
    hdr.Resize(sceneRect.W,sceneRect.H);
    hdr.BeginScene(.0025f,.0035f,.006f,1);
    gl.Enable(EnableCap.DepthTest);
    var view = camera.GetViewMatrix();
    var projection = camera.GetProjectionMatrix(sceneRect.W / (float)Math.Max(1, sceneRect.H));
    grid.Render(view, projection);
    // Soft particles sample the opaque scene's depth; particles still depth-test against the attachment.
    hdr.CopyOpaqueDepth();
    bool inspectPixels=options.ContainsKey("smoke") && frame?.Tick==smokeTick;
    unsafe float[] ReadScene()
    {
        var pixels=new float[hdr.Width*hdr.Height*4];
        gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        fixed(float* ptr=pixels) gl.ReadPixels(0,0,(uint)hdr.Width,(uint)hdr.Height,PixelFormat.Rgba,PixelType.Float,ptr);
        return pixels;
    }
    float[]? before=inspectPixels?ReadScene():null;
    bool sceneRendered=true;
    if (frame != null)
    {
        if(showPoints) points.Render(frame,view*projection);
        else
        {
            particleDivisor=quality switch {1=>1,2=>2,3=>4,_=>frame.Particles.Count>=2000?4:frame.Particles.Count>=500?2:1};
            // The reduced-resolution layer composites with one blend mode; mixed blends draw at full resolution.
            int? commonBlend=null;
            try {commonBlend=billboards?.CommonBlend(frame);} catch(InvalidDataException) {}
            if(commonBlend==null) particleDivisor=1;
            int? solo=soloSelected && SelectedTable()>=0 ? SelectedTable() : null;
            // Decals lie on the ground under the particles, in the full-resolution scene.
            billboards?.Decals.Render(frame,view*projection,composition,solo);
            if(particleDivisor>1) particleLayer.Begin(hdr.Width,hdr.Height,particleDivisor);
            var renderContext=new PreviewRenderContext(composition,exposure,.05f,100f,hdr.OpaqueDepthTexture,hdr.CaptureSceneColor);
            try {billboards?.Render(frame,view,projection,renderContext,solo);}
            catch(Exception ex) when(ex is InvalidDataException or TimeoutException) {sceneRendered=false;status="绘制失败："+ex.Message;}
            if(billboards==null || billboards.Drawn==0 && billboards.Decals.Drawn==0) sceneRendered=false;
            if(particleDivisor>1) particleLayer.End(hdr.Width,hdr.Height,commonBlend??0);
        }
    }
    if(inspectPixels)
    {
        var after=ReadScene();
        int changed=0;float maxDelta=0;
        for(int i=0;i<after.Length;i+=4)
        {
            float delta=Math.Max(Math.Abs(after[i]-before![i]),Math.Max(Math.Abs(after[i+1]-before[i+1]),Math.Abs(after[i+2]-before[i+2])));
            if(delta>1e-6f) changed++;
            maxDelta=Math.Max(maxDelta,delta);
        }
        var error=gl.GetError();
        Console.WriteLine($"PIXELS changed={changed} max_delta={maxDelta:G6} textures={billboards?.TextureDescription} gl_error={error} entries={frame!.EntryStates!.Count} blend={frame.EntryStates![Math.Max(0,SelectedTable())].Blend} colorrate={frame.EntryStates![Math.Max(0,SelectedTable())].ColorRate}");
        if(changed==0 || error!=GLEnum.NoError) throw new InvalidOperationException("Billboard smoke produced no pixels or a GL error");
    }
    hdr.EndSceneAndProcessBloom(bloomIntensity:bloomIntensity,exposure:exposure);
    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer,presentFbo);
    gl.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,hdr.FinalColorTexture,0);
    gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
    gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer,0);
    if(docking)
    {
        // Behind docked panels (and any gap) the window shows the scene's background colour.
        gl.ClearColor(.03f,.035f,.05f,1); gl.Clear(ClearBufferMask.ColorBufferBit);
    }
    gl.BlitFramebuffer(0,0,hdr.Width,hdr.Height,sceneRect.X,sceneRect.Y,sceneRect.X+sceneRect.W,sceneRect.Y+sceneRect.H,ClearBufferMask.ColorBufferBit,BlitFramebufferFilter.Linear);
    if(captureRequest is {Pixels:null} shot && frame!=null && sceneRendered && frame.Tick==shot.Tick)
    {
        // The tone-mapped scene without the UI; presentFbo is still bound for reading.
        unsafe
        {
            var pixels=new byte[hdr.Width*hdr.Height*4];
            gl.PixelStore(PixelStoreParameter.PackAlignment,1);
            fixed(byte* ptr=pixels) gl.ReadPixels(0,0,(uint)hdr.Width,(uint)hdr.Height,PixelFormat.Rgba,PixelType.UnsignedByte,ptr);
            shot.PixelWidth=hdr.Width;shot.PixelHeight=hdr.Height;shot.Pixels=pixels;
        }
    }
    gl.BindFramebuffer(FramebufferTarget.Framebuffer,0);
    ImGui.SetNextWindowPos(new Vector2(12,12), ImGuiCond.FirstUseEver);
    ImGui.SetNextWindowSize(new Vector2(420,720), ImGuiCond.FirstUseEver);
    ImGui.Begin("RE4 原生预览 · P2");
    ImGui.TextWrapped(status);
    if (client.LastRecovery.Length > 0) ImGui.TextWrapped("已重启辅助进程：" + client.LastRecovery);
    if(control?.Error is {} controlError) ImGui.TextColored(new Vector4(1,.45f,.35f,1),controlError);
    else if(control!=null && activeControl!=null) ImGui.TextDisabled("AI 正在执行："+activeControl.Action);
    // Frame work must never disable transport controls between mouse-down and mouse-up.
    ImGui.BeginDisabled(!playback.IsReady);
    if (ImGui.Button(playback.IsPlaying ? "暂停" : "播放")) playback.TogglePlayback();
    ImGui.SameLine();
    if (ImGui.Button("前进一步")) playback.Step();
    ImGui.SameLine();
    if (ImGui.Button("回到起点")) playback.Seek(0);
    ImGui.EndDisabled();
    ImGui.SameLine();
    ImGui.BeginDisabled(desiredManifest==null || faultTask!=null);
    if (ImGui.Button("重载并重播")) Reload();
    ImGui.EndDisabled();
    ImGui.BeginDisabled(!playback.IsReady);
    int seek = playback.TargetTick;
    if (ImGui.SliderInt("tick", ref seek, 0, PreviewPlaybackController.LastTick)) playback.Seek(seek);
    ImGui.EndDisabled();
    if(frame!=null) ImGui.TextDisabled($"存活 {frame.Particles.Count} · 时间 {frame.Time:F3} s · 右键旋转 · 中键平移 · 滚轮缩放");
    ImGui.BeginDisabled(!playback.IsReady || playback.IsBusy || playback.HasQueuedRequest || faultTask!=null);
    if (options.ContainsKey("test-faults"))
    {
        if (ImGui.Button("测试原生故障")) { playback.BeginLoad(autoplay:false); faultTask = Task.Run(() => client.InjectFaultAsync(false)); }
        ImGui.SameLine();
        if (ImGui.Button("测试进程退出")) { playback.BeginLoad(autoplay:false); faultTask = Task.Run(() => client.InjectFaultAsync(true)); }
    }
    ImGui.EndDisabled();
    bool motion=demoMotion;
    if(ImGui.Checkbox("演示运动（发射器摆动）",ref motion)) SetDemoMotion(motion);
    if(ImGui.IsItemHovered()) ImGui.SetTooltip("发射器绕竖直轴摆动（1 m 臂长、±60°、每秒一次）。PolygonTrail 在游戏里跟随骨骼挥动，发射器静止时不会形成拖尾。");
    if(!demoMotion && scene?.Shown.Any(e=>e.Document?.Kind==PreviewTypeKind.PolygonTrail)==true)
        ImGui.TextColored(new Vector4(1,.75f,.35f,1),"有 PolygonTrail：勾选演示运动才会形成拖尾。");
    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X*.5f);
    ImGui.SliderFloat("曝光",ref exposure,.1f,5f);
    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X*.5f);
    ImGui.SliderFloat("辉光",ref bloomIntensity,0,2);
    DrawComposition();
    if(scene!=null)
    {
        DrawEntrySelector();
        if(billboards?.Unsupported is { } unsupported) ImGui.TextColored(new Vector4(1,.45f,.35f,1),unsupported);
    }
    // Timings, render quality and the particle inspector: for debugging, closed by default.
    if(ImGui.CollapsingHeader("调试信息"))
    {
        ImGui.Text($"已显示 tick: {frame?.Tick ?? 0} / 请求: {playback.TargetTick}");
        ImGui.Text($"加载 {client.LoadMs:F1} ms · 快照 {client.SnapshotBytes / 1048576.0:F1} MB");
        if(latency!=null) ImGui.TextWrapped("最近修改耗时："+(latency.LastResult??"等待测量"));
        if(control!=null && control.Error==null) ImGui.TextDisabled("AI 控制通道："+control.PipeName);
        ImGui.Checkbox("粒子点对照",ref showPoints);
        ImGui.Combo("粒子精度",ref quality,"自动\0完整\0一半\0四分之一\0");
        ImGui.Text($"当前粒子分辨率：1/{particleDivisor}");
        if(scene!=null)
        {
            if(scene.AtTable(SelectedTable())?.Document is {} selectedDocument)
                ImGui.Text($"所选 entry 的 UV 帧数：{string.Join(", ",selectedDocument.Sequences.Select(s=>s.Length))}");
            ImGui.Text($"贴图 {billboards?.TextureCount} · 绘制 {billboards?.DrawCalls} 次 · 实例上传 {billboards?.UploadMs:F3} ms");
        }
        if (frame != null)
        {
            ImGui.Text($"原生 {frame.SimulationMs:F3} ms · 恢复 {frame.RestoreMs:F3} ms");
            ImGui.Text($"辅助进程总计 {frame.TotalMs:F3} ms · 读取/解码 {frame.DecodeMs:F3} ms");
            if (frame.Particles.Count > 0 && ImGui.TreeNode("粒子细则"))
            {
                selection = Math.Clamp(selection, 0, frame.Particles.Count - 1);
                ImGui.SliderInt("粒子", ref selection, 0, frame.Particles.Count - 1);
                var particle = frame.Particles[selection];
                ImGui.Text($"entry {particle.Entry} · 槽 {particle.Slot} · 编号 {particle.Id}");
                ImGui.Text($"年龄 {particle.Age:F3} · life alpha {particle.LifeAlpha}");
                int particleTable=TableOfParticle(frame,selection);
                var particleEntry=scene?.AtTable(particleTable);
                if(particleEntry!=null) ImGui.Text($"entry 位置 [{particleEntry.Position}] {particleEntry.Name}");
                var instance=BillboardInstance.Decode(particle,frame.EntryStates![Math.Max(0,particleTable)],particleEntry?.Document?.UvFlags??0,particleEntry?.Document?.Kind??PreviewTypeKind.Billboard3D);
                ImGui.Text($"宽高 {instance.Size.X:F4} × {instance.Size.Y:F4} · 滚转 {instance.PositionRoll.W:F3}");
                ImGui.Text($"UV 序列 {instance.Uv&255} / 帧 {(instance.Uv>>8)&255} · 顶点 alpha {instance.Color>>24}");
                ImGui.Text($"位置 {particle.Position.X:F5}, {particle.Position.Y:F5}, {particle.Position.Z:F5}");
                if(ImGui.TreeNode("原始记录"))
                {
                    ImGui.SliderInt("原始记录偏移", ref rawOffset, 0, 0x13c);
                    ImGui.Text($"+0x{rawOffset:X3}: u32={FrameDecoder.U32(particle.Raw.Span, rawOffset)} float={FrameDecoder.F32(particle.Raw.Span, rawOffset):G9}");
                    for (int row = 0; row < 20; row++)
                        ImGui.Text($"{row*16:X3}  {Convert.ToHexString(particle.Raw.Span.Slice(row*16,16))}");
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
        }
    }
    ImGui.End();
    HandleDroppedFile();
    var editCommand=editPanel.Draw(editorState,displayedManifest,editTask!=null,stress!=0,editor.Directory,editError??structureNote,
        copyBuffer?["kind"]?.GetValue<string>(),copyBuffer?["attributeType"]?.GetValue<string>());
    if(editCommand?.Tag is PreviewStructureRequest fromPanel) RequestStructure(fromPanel);
    else if(editCommand!=null) StartEditorCommand(editCommand);
    DrawStructureConfirmations();
    string? graphLock=editTask!=null?"编辑器忙，稍候再选":editPanel.HasUnappliedChanges?"先应用或放弃属性草稿，再从节点图选择":stress!=0?"压力模式下不能编辑":null;
    var manifest=editorState?.Manifest;
    var inspected=editorState?.Inspection;
    if(inspected!=null && (manifest==null || inspected.DocumentId!=manifest.DocumentId || !inspected.EditTarget.Equals(editorState!.EditTarget) ||
        inspected.AttributeIndex!=editPanel.SelectedAttribute || inspected.Revision!=editorState!.Revision)) inspected=null;
    string? curveAttributeType=editorState?.Attributes?.FirstOrDefault(a=>a.Index==editPanel.SelectedAttribute)?.Type;
    if(curvePanel.Draw(inspected,curveAttributeType,playback.DisplayedTick,graphLock) is {} curveBody && editorState!=null)
    {
        curveBody["target"]=AttributeTarget(editPanel.SelectedAttribute);
        StartEditorCommand(new PreviewEditCommand("curve_key",editorState.Revision,Body:curveBody));
    }
    if(graphPanel!=null) {graphPanel.ClipboardKind=copyBuffer?["kind"]?.GetValue<string>();graphPanel.ClipboardName=copyBuffer?["attributeType"]?.GetValue<string>()??copyBuffer?["name"]?.GetValue<string>();}
    if(graphPanel?.Draw(scene?.Graph,manifest?.DocumentId,manifest==null?null:Path.GetFileName(manifest.Source),manifest?.EntryPosition,
        editorState?.EditTarget,editPanel.SelectedAttribute,graphLock) is {} pick && editorState!=null && editPanel.Focus(pick) && !pick.Target.Equals(editorState.EditTarget))
        StartEditorCommand(pick.Target.IsRoot ? new PreviewEditCommand("select_entry",editorState.Revision,EntryPosition:pick.Target.Index)
            : new PreviewEditCommand("select_target",editorState.Revision,Tag:pick.Target));
    if(graphPanel?.Dropped is {} dropOnGraph) RequestAdd(dropOnGraph);
    if(graphPanel?.Requested is {} fromGraph) RequestStructure(fromGraph);
    var catalogTypes=manifest==null?null:editorState?.Attributes?.Select(a=>a.Type).ToArray();
    string? addLock=editTask!=null?"编辑器忙，稍候再添加":editPanel.HasUnappliedChanges?"先应用或放弃属性草稿，再添加属性":stress!=0?"压力模式下不能编辑":null;
    var editTarget=manifest==null?null:editorState?.EditTarget;
    string? editName=editTarget==null?null:editTarget.IsRoot?scene?.Entries.FirstOrDefault(e=>e.Position==editTarget.Index)?.Name
        :scene?.Graph.Nodes.FirstOrDefault(n=>editTarget.Equals(n.Target))?.Title;
    if(catalogPanel.Draw(editTarget,editName,
        catalogTypes?.FirstOrDefault(PreviewAttributeCatalog.IsTypeAttribute),catalogTypes,addLock) is {} doubleClicked)
        RequestAdd(doubleClicked);
    gl.Viewport(0,0,(uint)Math.Max(1,window.FramebufferSize.X),(uint)Math.Max(1,window.FramebufferSize.Y));   // the scene left it at the central node
    gui.Render();
    if(frame!=null && displayedManifest!=null && sceneRendered)
        latency?.Rendered(displayedManifest.Key,frame.Tick,playback.IsPlaying,()=>gl.Finish());
    if(options.ContainsKey("benchmark") && frame!=null && frame.Tick>=10 && frame.Tick!=lastBenchTick)
    {
        gl.Finish();
        if(!benchmarkClock.IsRunning) benchmarkClock.Start();
        double now=benchmarkClock.Elapsed.TotalMilliseconds;
        if(lastBenchTick>=0) intervals.Add(now-lastBenchTime);
        lastBenchTick=frame.Tick;lastBenchTime=now;
        nativeCosts.Add(frame.SimulationMs);decodeCosts.Add(frame.DecodeMs);uploadCosts.Add(billboards!.UploadMs);particleCounts.Add(frame.Particles.Count);
        if(frame.Tick==310)
        {
            var sorted=intervals.Order().ToArray();
            var result=new { width=hdr.Width,height=hdr.Height,particles=frame.Particles.Count,min_particles=particleCounts.Min(),max_particles=particleCounts.Max(),particle_resolution_divisor=particleDivisor,frames=intervals.Count,
                fps=1000/intervals.Average(),mean_frame_ms=intervals.Average(),p95_frame_ms=sorted[(int)(sorted.Length*.95)],max_frame_ms=sorted[^1],
                native_ms=nativeCosts.Average(),read_decode_ms=decodeCosts.Average(),upload_ms=uploadCosts.Average(),
                draw_calls=billboards.DrawCalls,gpu=gl.GetStringS(StringName.Renderer),mode="native simulation + RGBA16F billboards + bloom + UI, GPU completion per new tick" };
            Directory.CreateDirectory(Path.Combine(repo,"test-output/re4-preview"));
            File.WriteAllText(Path.Combine(repo,"test-output/re4-preview",$"p1-benchmark-{options.GetValueOrDefault("quality","auto")}.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine(JsonSerializer.Serialize(result));
            window.Close();
        }
    }
    if (options.ContainsKey("smoke") && frame?.Tick == smokeTick && completedFrames > 1)
    {
        if (options.TryGetValue("capture", out string? capture))
        {
            unsafe
            {
                int w = window.FramebufferSize.X, h = window.FramebufferSize.Y;
                byte[] pixels = new byte[w*h*4];
                fixed (byte* ptr = pixels) gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
                using var image = Image.LoadPixelData<Rgba32>(pixels,w,h);
                image.Mutate(x => x.Flip(FlipMode.Vertical));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
                image.SaveAsPng(capture);
            }
        }
        Console.WriteLine($"SMOKE PASS tick={frame.Tick} alive={frame.Particles.Count}");
        window.Close();
    }
};
window.Closing += () =>
{
    latency?.Fail("window_closed");
    latency?.WriteSummary();
    activeControl?.Fail("window_closing","预览窗口已关闭，请求结果不确定。");
    control?.Dispose();
    // Wait for the one outstanding worker operation before disposing its mapping.
    try {pending?.GetAwaiter().GetResult().Dispose();} catch { }
    try {loading?.GetAwaiter().GetResult();} catch { }
    try {editTask?.GetAwaiter().GetResult();} catch { }
    try {faultTask?.GetAwaiter().GetResult();} catch { }
    preparedPreview?.Renderer.Dispose();
    frame?.Dispose();billboards?.Dispose();gpuCache.Dispose(); points.Dispose(); grid.Dispose(); particleLayer.Dispose(); hdr.Dispose(); gl.DeleteFramebuffer(presentFbo); graphPanel?.Dispose(); gui.Dispose(); input.Dispose(); gl.Dispose();
};
try { window.Run(); }
catch(Exception ex) {Console.Error.WriteLine(ex);Environment.ExitCode=1;}




internal sealed record NativeLoadResult(OfflinePreviewManifest Manifest,PreviewScene Scene,double NativeLoadMs,Dictionary<int,string> Excluded);
internal sealed record PreparedPreview(OfflinePreviewManifest Manifest,PreviewScene Scene,PreviewSceneRenderer Renderer);
