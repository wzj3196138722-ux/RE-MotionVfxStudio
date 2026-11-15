using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VfxStudio.Re4Render;

public sealed record OfflinePreviewManifest(int Schema, string DocumentId, long Revision, string Path,
    string Sha256, int EntryPosition, int EntryId, int Offset, string Source, bool Restart)
{
    public string Key => $"{DocumentId}:{Revision}:{Sha256}:{EntryPosition}";
    public void ValidateFile()
    {
        if (Schema != 1 || !Restart || Offset < 0 || EntryPosition < 0) throw new InvalidDataException("无效预览清单。");
        using var file = File.OpenRead(Path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(file)), Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("预览文件已变化，请重新发布当前文档。");
    }
}

public sealed record PreviewEditorParameters(int EntryPosition, int BillboardAttribute, int LifeAttribute,
    int[] Color, int[] ColorRange, float ColorRate, float[] SizeScalar, float[] SizeX, float[] SizeY,
    int[]? Appear, int[]? Keep, int[]? Vanish);

public sealed record PreviewEditorState(long Revision, bool Dirty, bool CanUndo, bool CanRedo,
    OfflinePreviewManifest? Manifest, PreviewEditorParameters? Parameters, string? PublicationError,
    string? SavedPath, string? Warning);

public sealed record BillboardEdit(Vector4 Color, Vector4 ColorRange, float ColorRate,
    Vector2 SizeScalar, Vector2 SizeX, Vector2 SizeY);
public sealed record LifeEdit(int AppearMin, int AppearMax, int KeepMin, int KeepMax, int VanishMin, int VanishMax);

/// <summary>Owns a private, offline EditorEngine host. All mutations use its existing mailbox
/// protocol; UI parameter buffers are drafts, never a second document or undo stack.</summary>
public sealed class PreviewEditorSession : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly string repo, backend;
    private readonly string[] protectedRoots;
    private readonly SemaphoreSlim gate = new(1);
    private Process? process;
    private readonly Queue<string> logs = new();
    public string Directory { get; }
    public string Mailbox => Path.Combine(Directory, "mailbox");
    public string ManifestPath => Path.Combine(Mailbox, "offline-preview", "preview.json");
    public PreviewEditorState? State { get; private set; }

    /// <param name="protectedRoots">Directories "save" must never write into (the game directory
    /// and the unpacked resources): a file there would be loaded by the game as a loose file.
    /// The backend enforces the same list.</param>
    public PreviewEditorSession(string repo, string? sessionDirectory = null, string? backendPath = null, IEnumerable<string>? protectedRoots = null)
    {
        this.protectedRoots = (protectedRoots ?? []).Select(Path.GetFullPath).ToArray();
        this.repo = Path.GetFullPath(repo);
        Directory = sessionDirectory == null
            ? Path.Combine(this.repo,"runtime","re4-preview",DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(sessionDirectory);
        backend = backendPath ?? Path.Combine(AppContext.BaseDirectory,"VfxStudio.LiveEditor.dll");
    }

    private async Task Start()
    {
        if (process != null) return;
        if (!File.Exists(backend)) throw new FileNotFoundException("缺少 LiveEditor 后台，请重新构建预览项目。",backend);
        System.IO.Directory.CreateDirectory(Mailbox);
        var info = new ProcessStartInfo("dotnet") { UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardOutput=true, RedirectStandardError=true, RedirectStandardInput=true };
        foreach (var arg in new[] { backend,"--game","re4","--assets",Path.Combine(repo,"Assets"),
            "--mailbox",Mailbox,"--output",Path.Combine(Directory,"saved") }) info.ArgumentList.Add(arg);
        foreach (var root in protectedRoots) { info.ArgumentList.Add("--protect"); info.ArgumentList.Add(root); }
        // No --game-root: this host never polls the game or publishes into its directory.
        process = new Process { StartInfo=info };
        void Log(string? value) { if(value!=null) lock(logs) {logs.Enqueue(value);while(logs.Count>20) logs.Dequeue();} }
        process.OutputDataReceived += (_,e)=>Log(e.Data);
        process.ErrorDataReceived += (_,e)=>Log(e.Data);
        process.Start();ChildProcessJob.Assign(process);process.BeginOutputReadLine();process.BeginErrorReadLine();
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(20))
        {
            if(process.HasExited)
            {
                string[] output;lock(logs) output=logs.ToArray();
                throw new IOException("LiveEditor 启动失败："+string.Join("\n",output));
            }
            var status=ReadJson(Path.Combine(Mailbox,"status.json"));
            if(status?["ready"]?.GetValue<bool>()==true && status["pid"]?.GetValue<int>()==process.Id) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("等待 LiveEditor 启动超时。");
    }

    private static JsonObject? ReadJson(string path)
    {
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            return JsonNode.Parse(stream)?.AsObject();
        }
        catch(Exception ex) when(ex is IOException or JsonException) {return null;}
    }

    private async Task<PreviewEditorState> Call(string action, JsonObject? body=null, long? expectedRevision=null)
    {
        body ??= new();
        string id=Guid.NewGuid().ToString("N");
        body["id"]=id;body["action"]=action;body["revision"]=expectedRevision??State?.Revision??0;
        string temporary=Path.Combine(Mailbox,"request_"+id+".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary,body.ToJsonString());
            File.Move(temporary,Path.Combine(Mailbox,"request.json"),overwrite:true);
        }
        finally {if(File.Exists(temporary)) File.Delete(temporary);}
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(30))
        {
            if(process?.HasExited==true) throw new IOException("LiveEditor 已退出，已发布的修订文件保留在会话目录。");
            var reply=ReadJson(Path.Combine(Mailbox,"response.json"));
            if(reply?["id"]?.GetValue<string>()==id)
            {
                var doc=reply["document"];
                // "open" replaces the document: the previous manifest and attribute indices must not
                // survive it. Otherwise a failed configure_offline_preview would leave set_fields
                // addressing the old document's attributes.
                var previous=action=="open"?null:State;
                State=new(reply["revision"]?.GetValue<long>()??0,
                    doc?["dirty"]?.GetValue<bool>()??false,doc?["canUndo"]?.GetValue<bool>()??false,doc?["canRedo"]?.GetValue<bool>()??false,
                    reply["offlinePreview"]?.Deserialize<OfflinePreviewManifest>(Json)??previous?.Manifest,
                    reply["offlinePreviewParameters"]?.Deserialize<PreviewEditorParameters>(Json)??previous?.Parameters,
                    reply["offlinePreviewError"]?.GetValue<string>(),reply["outputPath"]?.GetValue<string>()??previous?.SavedPath,
                    doc?["warning"]?.GetValue<string>());
                if(reply["ok"]?.GetValue<bool>()!=true) throw new InvalidOperationException(reply["error"]?.GetValue<string>()??"编辑操作失败。");
                return State;
            }
            await Task.Delay(15);
        }
        // Do not retry a timed-out mutation with another id: it may already have committed.
        throw new TimeoutException("编辑操作响应超时，请刷新文档状态后再继续。");
    }

    public async Task<PreviewEditorState> OpenAsync(string path, int entryId)
    {
        await gate.WaitAsync();
        try
        {
            await Start();
            await Call("open",new(){["path"]=Path.GetFullPath(path)});
            return await Call("configure_offline_preview",new(){["entryId"]=entryId});
        }
        finally {gate.Release();}
    }

    public async Task<PreviewEditorState> OpenSavedAsync()
    {
        await gate.WaitAsync();
        try
        {
            string saved=State?.SavedPath??throw new InvalidOperationException("请先保存副本。");
            int position=State?.Manifest?.EntryPosition??throw new InvalidOperationException("未选择预览目标。");
            await Call("open",new(){["path"]=saved});
            return await Call("configure_offline_preview",new(){["entryPosition"]=position});
        }
        finally {gate.Release();}
    }

    public async Task<PreviewEditorState> CommandAsync(string action, string? savePath=null)
    {
        if(action is not ("undo" or "redo" or "save" or "status" or "publish_offline_preview")) throw new ArgumentException("未知编辑操作。");
        if(action=="save" && savePath!=null)
        {
            if(!Path.IsPathFullyQualified(savePath)) throw new ArgumentException("另存路径必须是完整的绝对路径。");
            string full=Path.GetFullPath(savePath);
            foreach(var root in protectedRoots)
                if(full.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("不能保存到游戏目录或解包目录："+root);
        }
        await gate.WaitAsync();
        try {return await Call(action,savePath==null?null:new(){["path"]=savePath});}
        finally {gate.Release();}
    }

    /// <summary>Sends one mailbox request as is, without this class's input checks. The backend is
    /// the authority; tests use this to confirm it rejects what the form would already refuse.</summary>
    public async Task<PreviewEditorState> SendAsync(string action, JsonObject body, long? expectedRevision = null)
    {
        await gate.WaitAsync();
        try {return await Call(action,body,expectedRevision);}
        finally {gate.Release();}
    }

    public OfflinePreviewManifest? ReadPublishedManifest()
        => ReadJson(ManifestPath)?.Deserialize<OfflinePreviewManifest>(Json);

    private static string Number(float value)
    {
        if(!float.IsFinite(value)) throw new ArgumentException("参数必须为有限数值。");
        return value.ToString("R",CultureInfo.InvariantCulture);
    }
    private static JsonObject Field(string path,string value)=>new(){["path"]=path,["value"]=value};
    private static void AddColor(JsonArray fields,string path,Vector4 color)
    {
        float[] values=[color.X,color.Y,color.Z,color.W];string[] channels=["R","G","B","A"];
        for(int i=0;i<4;i++)
        {
            if(!float.IsFinite(values[i]) || values[i]<0 || values[i]>1) throw new ArgumentException("颜色通道应在 0–1 之间。");
            fields.Add(Field(path+"."+channels[i],((int)MathF.Round(values[i]*255)).ToString(CultureInfo.InvariantCulture)));
        }
    }

    public async Task<PreviewEditorState> ApplyBillboardAsync(BillboardEdit edit,long draftRevision)
    {
        var fields=new JsonArray();AddColor(fields,"Color",edit.Color);AddColor(fields,"ColorRange",edit.ColorRange);
        fields.Add(Field("ColorRate",Number(edit.ColorRate)));
        foreach(var pair in new[]{("SizeScalar",edit.SizeScalar),("SizeX",edit.SizeX),("SizeY",edit.SizeY)})
        {
            fields.Add(Field(pair.Item1+".center",Number(pair.Item2.X)));
            fields.Add(Field(pair.Item1+".spread",Number(pair.Item2.Y)));
        }
        await gate.WaitAsync();
        try
        {
            var p=State?.Parameters??throw new InvalidOperationException("编辑文档未就绪。");
            return await Call("set_fields",new(){["target"]=new JsonObject{["kind"]="entry",["index"]=p.EntryPosition,["attributeIndex"]=p.BillboardAttribute},["fields"]=fields},draftRevision);
        }
        finally {gate.Release();}
    }

    public async Task<PreviewEditorState> ApplyLifeAsync(LifeEdit edit,long draftRevision)
    {
        var fields=new JsonArray();
        foreach(var pair in new[]{("AppearFrame",edit.AppearMin,edit.AppearMax),("KeepFrame",edit.KeepMin,edit.KeepMax),("VanishFrame",edit.VanishMin,edit.VanishMax)})
        {
            if(pair.Item2<0 || pair.Item3<pair.Item2 || pair.Item3>32767) throw new ArgumentException("Life 区间应满足 0 ≤ 最小值 ≤ 最大值 ≤ 32767。");
            fields.Add(Field(pair.Item1+".min",pair.Item2.ToString(CultureInfo.InvariantCulture)));
            fields.Add(Field(pair.Item1+".max",pair.Item3.ToString(CultureInfo.InvariantCulture)));
        }
        await gate.WaitAsync();
        try
        {
            var p=State?.Parameters??throw new InvalidOperationException("编辑文档未就绪。");
            if(p.LifeAttribute<0) throw new InvalidOperationException("此发射器没有 Life 属性。");
            return await Call("set_fields",new(){["target"]=new JsonObject{["kind"]="entry",["index"]=p.EntryPosition,["attributeIndex"]=p.LifeAttribute},["fields"]=fields},draftRevision);
        }
        finally {gate.Release();}
    }

    public void Dispose()
    {
        if(process==null) return;
        try
        {
            if(!process.HasExited)
            {
                var body=new JsonObject{["id"]=Guid.NewGuid().ToString("N"),["action"]="shutdown"};
                File.WriteAllText(Path.Combine(Mailbox,"request.json"),body.ToJsonString());
                if(!process.WaitForExit(1500)) {process.Kill(entireProcessTree:true);process.WaitForExit(3000);}
            }
        }
        catch(Exception ex) when(ex is IOException or InvalidOperationException) { }
        process.Dispose();process=null;
    }
}
