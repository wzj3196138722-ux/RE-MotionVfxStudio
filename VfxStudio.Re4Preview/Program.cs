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
PreviewDocument? document = null;
using var editor = new PreviewEditorSession(repo,options.GetValueOrDefault("session"),options.GetValueOrDefault("editor-backend"),[gameDirectory,resourceRoot]);
var editPanel = new PreviewEditPanel();
PreviewEditorState? editorState = null;
Task<PreviewEditorState>? editTask = null;
string? editAction = null, editError = null;
OfflinePreviewManifest? desiredManifest = null, displayedManifest = null;
Task<NativeLoadResult>? loading = null;
Task? faultTask = null;
PreparedPreview? preparedPreview = null;
bool playAfterLoad = false;
double manifestPoll = 0;

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
BillboardRenderer? billboards = null;
HdrBloomPipeline hdr = null!;
ParticleLayer particleLayer=null!;
int quality=options.GetValueOrDefault("quality","auto") switch { "full"=>1,"half"=>2,"quarter"=>3,_=>0 };
int particleDivisor=1;
uint presentFbo=0;
bool showPoints=false;
float exposure=1f, bloomIntensity=.8f;
var benchmarkClock=new Stopwatch();
var intervals=new List<double>();
var nativeCosts=new List<double>();
var decodeCosts=new List<double>();
var uploadCosts=new List<double>();
var particleCounts=new List<int>();
int lastBenchTick=-1;
double lastBenchTime=0;
var camera = new OrbitCamera { Target = new Vector3(0, .4f, 0), Distance = 4f };
PreviewFrame? frame = null;
Task<PreviewFrame>? pending = null;
string status = "Loading native worker…";
var playback = new PreviewPlaybackController();
PreviewFrameRequest pendingRequest = default;
bool reloadQueued = false;
int selection = 0, rawOffset = 0, completedFrames = 0;
Vector2 previousMouse = default;


void QueuePreview(OfflinePreviewManifest manifest, bool autoplay, bool force=false, bool restart=false)
{
    if(!force && desiredManifest?.Key==manifest.Key) return;
    // A new revision of the same document and emitter keeps the timeline position; a different
    // document or emitter, or an explicit replay, starts from tick 0.
    bool sameTarget=desiredManifest!=null && desiredManifest.DocumentId==manifest.DocumentId && desiredManifest.EntryPosition==manifest.EntryPosition;
    desiredManifest=manifest;
    playback.BeginLoad(restart:restart || !sameTarget);
    preparedPreview?.Renderer.Dispose(); preparedPreview=null;
    playAfterLoad=autoplay;
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
    reloadQueued=false;
    loading=Task.Run(async () =>
    {
        manifest.ValidateFile();
        var loaded=PreviewDocument.Read(manifest.Path,manifest.Offset,resources);
        await client.LoadAsync(loaded.LoadCommand(stress));
        return new NativeLoadResult(manifest,loaded);
    });
    status=$"正在加载文档 r{manifest.Revision}…";
}
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
void StartEditorCommand(PreviewEditCommand command)
{
    if(editTask!=null) return;
    editAction=command.Action;editError=null;
    editTask=Task.Run(() => command.Action switch
    {
        "billboard" => editor.ApplyBillboardAsync(command.Billboard!,command.Revision),
        "life" => editor.ApplyLifeAsync(command.Life!,command.Revision),
        "open_saved" => editor.OpenSavedAsync(),
        "open_source" => OpenOriginal(),
        _ => editor.CommandAsync(command.Action,command.SavePath)
    });
}

window.Load += () =>
{
    gl = GL.GetApi(window);
    input = window.CreateInput();
    gui = new ImGuiController(gl, window, input);
    unsafe { var io = ImGui.GetIO(); io.IniFilename = null; }
    grid = new ViewportGridRenderer(gl);
    points = new ParticlePointRenderer(gl);
    hdr=new HdrBloomPipeline(gl,window.FramebufferSize.X,window.FramebufferSize.Y);
    presentFbo=gl.GenFramebuffer();
    particleLayer=new ParticleLayer(gl);
    foreach (var mouse in input.Mice) mouse.Scroll += (_, scroll) => { if (!ImGui.GetIO().WantCaptureMouse) camera.Zoom(scroll.Y); };
    editAction="open";
    playback.BeginLoad(restart:true);
    editTask=OpenOriginal();
};
window.Update += dt =>
{
    gui.Update((float)dt);
    if(editTask?.IsCompleted==true)
    {
        try
        {
            editorState=editTask.GetAwaiter().GetResult();
            if(editAction is not ("watch" or "save") || (editAction=="watch" && !editPanel.HasUnappliedChanges)) editPanel.Reset(editorState);
            if(editorState.Manifest is { } manifest && manifest.Revision==editorState.Revision)
                QueuePreview(manifest,editAction is not ("open" or "open_source"),force:editAction is "publish_offline_preview" or "open_source",restart:editAction is "open" or "open_source" or "open_saved");
            if(editorState.PublicationError!=null) editError="文档已保留，预览发布失败，可重试或撤销。";
        }
        catch(Exception ex)
        {
            editorState=editor.State;
            editError=ex.GetBaseException().Message;
            if(options.ContainsKey("smoke") || options.ContainsKey("benchmark")) {Console.Error.WriteLine(ex);Environment.ExitCode=1;window.Close();}
        }
        editTask=null;
    }
    if(faultTask?.IsCompleted==true)
    {
        try {faultTask.GetAwaiter().GetResult();playback.FinishLoad();status="辅助进程已重启，时间轴保持在 tick "+playback.TargetTick;}
        catch(Exception ex) {status=ex.Message;if(desiredManifest!=null) {playback.BeginLoad();playAfterLoad=false;reloadQueued=true;}}
        faultTask=null;
    }
    if (loading?.IsCompleted == true)
    {
        try
        {
            var loaded=loading.GetAwaiter().GetResult();
            if(loaded.Manifest.Key==desiredManifest?.Key && !reloadQueued)
            {
                preparedPreview=new(loaded.Manifest,loaded.Document,new BillboardRenderer(gl,loaded.Document));
                playback.FinishLoad();
                if(playAfterLoad) playback.TogglePlayback();
                status=$"文档 r{loaded.Manifest.Revision} 已加载，tick {playback.TargetTick}";
            }
        }
        catch(Exception ex)
        {
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
                    document=preparedPreview.Document;displayedManifest=preparedPreview.Manifest;
                    preparedPreview=null;
                }
                frame?.Dispose(); frame = nextFrame;
                completedFrames++;
            }
            else nextFrame.Dispose();
        }
        catch (Exception ex)
        {
            // The client has already restarted and reloaded the worker for a native fault; any other
            // error leaves the loaded document intact. Either way playback pauses on the last frame
            // shown and keeps its timeline position; the next request replays from the restored state.
            status = "预览运行失败，已暂停在当前帧，可继续播放、重试或撤销："+ex.Message;
            if(playback.IsBusy) playback.FailRequest(pendingRequest);
            if(preparedPreview!=null)
            {
                // The failed request was the first frame of a newly loaded document: reload it.
                preparedPreview.Renderer.Dispose();preparedPreview=null;
                if(desiredManifest!=null) {playback.BeginLoad();playAfterLoad=false;reloadQueued=true;}
            }
            if(options.ContainsKey("smoke") || options.ContainsKey("benchmark")) { Console.Error.WriteLine(ex); Environment.ExitCode=1; window.Close(); }
        }
        pending = null;
    }
    if (reloadQueued && pending == null && loading == null && faultTask==null) StartReload();
    manifestPoll+=dt;
    if(manifestPoll>=.25 && editTask==null && editorState?.Manifest!=null)
    {
        manifestPoll=0;
        try
        {
            var published=editor.ReadPublishedManifest();
            if(published!=null && published.Key!=editorState.Manifest?.Key)
            {
                editAction="watch";
                editTask=Task.Run(()=>editor.CommandAsync("status"));
            }
        }
        catch(Exception ex) {editError=ex.Message;}
    }
    if (input.Mice.Count > 0)
    {
        var mouse = input.Mice[0];
        var delta = mouse.Position - previousMouse; previousMouse = mouse.Position;
        if (!ImGui.GetIO().WantCaptureMouse)
        {
            if (mouse.IsButtonPressed(MouseButton.Right)) camera.Rotate(-delta.X * .006f, -delta.Y * .006f);
            if (mouse.IsButtonPressed(MouseButton.Middle)) camera.Pan(delta.X, delta.Y);
        }
    }
    playback.Advance(dt);
    if (options.ContainsKey("smoke") && frame != null && playback.TargetTick < 45 &&
        !playback.IsBusy && !playback.HasQueuedRequest) playback.Seek(playback.TargetTick + 1);
    if (options.ContainsKey("benchmark") && frame != null && playback.TargetTick < 310 &&
        !playback.IsBusy && !playback.HasQueuedRequest) playback.Seek(playback.TargetTick + 1);
    if (loading == null && faultTask==null && !reloadQueued && playback.TryBeginRequest(out var request))
    {
        pendingRequest = request;
        // Capture this request, not a mutable timeline field.
        pending = Task.Run(() => client.FrameAsync(request.Tick));
    }
};
window.Render += _ =>
{
    gl.Viewport(0, 0, (uint)window.FramebufferSize.X, (uint)window.FramebufferSize.Y);
    hdr.Resize(Math.Max(1,window.FramebufferSize.X),Math.Max(1,window.FramebufferSize.Y));
    hdr.BeginScene(.0025f,.0035f,.006f,1);
    gl.Enable(EnableCap.DepthTest);
    var view = camera.GetViewMatrix();
    var projection = camera.GetProjectionMatrix(window.FramebufferSize.X / (float)Math.Max(1, window.FramebufferSize.Y));
    grid.Render(view, projection);
    bool inspectPixels=options.ContainsKey("smoke") && frame?.Tick==45;
    unsafe float[] ReadScene()
    {
        var pixels=new float[hdr.Width*hdr.Height*4];
        gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        fixed(float* ptr=pixels) gl.ReadPixels(0,0,(uint)hdr.Width,(uint)hdr.Height,PixelFormat.Rgba,PixelType.Float,ptr);
        return pixels;
    }
    float[]? before=inspectPixels?ReadScene():null;
    if (frame != null)
    {
        if(showPoints) points.Render(frame,view*projection);
        else
        {
            particleDivisor=quality switch {1=>1,2=>2,3=>4,_=>frame.Particles.Count>=2000?4:frame.Particles.Count>=500?2:1};
            if(particleDivisor>1) particleLayer.Begin(hdr.Width,hdr.Height,particleDivisor);
            try {billboards?.Render(frame,view,projection);}
            catch(Exception ex) when(ex is InvalidDataException or TimeoutException) {status="绘制失败："+ex.Message;}
            if(particleDivisor>1) particleLayer.End(hdr.Width,hdr.Height,BillboardRenderer.SupportsBlend(frame.EntryStates!.Single().Blend)?frame.EntryStates!.Single().Blend:0);
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
        Console.WriteLine($"PIXELS changed={changed} max_delta={maxDelta:G6} textures={billboards?.TextureDescription} gl_error={error} blend={frame!.EntryStates!.Single().Blend} colorrate={frame.EntryStates!.Single().ColorRate}");
        if(changed==0 || error!=GLEnum.NoError) throw new InvalidOperationException("Billboard smoke produced no pixels or a GL error");
    }
    hdr.EndSceneAndProcessBloom(bloomIntensity:bloomIntensity,exposure:exposure);
    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer,presentFbo);
    gl.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,hdr.FinalColorTexture,0);
    gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
    gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer,0);
    gl.BlitFramebuffer(0,0,hdr.Width,hdr.Height,0,0,window.FramebufferSize.X,window.FramebufferSize.Y,ClearBufferMask.ColorBufferBit,BlitFramebufferFilter.Linear);
    gl.BindFramebuffer(FramebufferTarget.Framebuffer,0);
    ImGui.SetNextWindowPos(new Vector2(12,12), ImGuiCond.FirstUseEver);
    ImGui.SetNextWindowSize(new Vector2(420,720), ImGuiCond.FirstUseEver);
    ImGui.Begin("RE4 原生预览 · P2");
    ImGui.TextWrapped(status);
    if (client.LastRecovery.Length > 0) ImGui.TextWrapped("已重启辅助进程：" + client.LastRecovery);
    ImGui.Text($"已显示 tick: {frame?.Tick ?? 0} / 请求: {playback.TargetTick}");
    ImGui.Text($"加载 {client.LoadMs:F1} ms · 快照 {client.SnapshotBytes / 1048576.0:F1} MB");
    // Frame work must never disable transport controls between mouse-down and mouse-up.
    ImGui.BeginDisabled(!playback.IsReady);
    if (ImGui.Button(playback.IsPlaying ? "暂停" : "播放")) playback.TogglePlayback();
    ImGui.SameLine();
    if (ImGui.Button("前进一步")) playback.Step();
    ImGui.SameLine();
    if (ImGui.Button("回到起点")) playback.Seek(0);
    int seek = playback.TargetTick;
    if (ImGui.SliderInt("tick", ref seek, 0, PreviewPlaybackController.LastTick)) playback.Seek(seek);
    ImGui.EndDisabled();
    ImGui.BeginDisabled(desiredManifest==null || faultTask!=null);
    if (ImGui.Button("重载并重播")) Reload();
    ImGui.EndDisabled();
    ImGui.BeginDisabled(!playback.IsReady || playback.IsBusy || playback.HasQueuedRequest || faultTask!=null);
    if (options.ContainsKey("test-faults"))
    {
        if (ImGui.Button("测试原生故障")) { playback.BeginLoad(); faultTask = Task.Run(() => client.InjectFaultAsync(false)); }
        ImGui.SameLine();
        if (ImGui.Button("测试进程退出")) { playback.BeginLoad(); faultTask = Task.Run(() => client.InjectFaultAsync(true)); }
    }
    ImGui.EndDisabled();
    ImGui.Checkbox("粒子点对照",ref showPoints);
    ImGui.Combo("粒子精度",ref quality,"自动\0完整\0一半\0四分之一\0");
    ImGui.Text($"当前粒子分辨率：1/{particleDivisor}");
    ImGui.SliderFloat("曝光",ref exposure,.1f,5f);
    ImGui.SliderFloat("辉光",ref bloomIntensity,0,2);
    if(document!=null)
    {
        ImGui.Text($"真实 UV 帧数：{string.Join(", ",document.Sequences.Select(s=>s.Length))}");
        ImGui.Text($"贴图 {billboards?.TextureCount} · 绘制 {billboards?.DrawCalls} 次");
        ImGui.Text($"实例上传 {billboards?.UploadMs:F3} ms");
        if(billboards?.Unsupported is { } unsupported) ImGui.TextColored(new Vector4(1,.45f,.35f,1),unsupported);
    }
    ImGui.TextWrapped("右键旋转 · 中键平移 · 滚轮缩放");
    if (frame != null)
    {
        ImGui.Separator();
        ImGui.Text($"存活 {frame.Particles.Count} · 时间 {frame.Time:F4} s");
        ImGui.Text($"原生 {frame.SimulationMs:F3} ms · 恢复 {frame.RestoreMs:F3} ms");
        ImGui.Text($"辅助进程总计 {frame.TotalMs:F3} ms · 读取/解码 {frame.DecodeMs:F3} ms");
        if (frame.Particles.Count > 0)
        {
            selection = Math.Clamp(selection, 0, frame.Particles.Count - 1);
            ImGui.SliderInt("粒子", ref selection, 0, frame.Particles.Count - 1);
            var particle = frame.Particles[selection];
            ImGui.Text($"entry {particle.Entry} · 槽 {particle.Slot} · 编号 {particle.Id}");
            ImGui.Text($"年龄 {particle.Age:F3} · life alpha {particle.LifeAlpha}");
            var instance=BillboardInstance.Decode(particle,frame.EntryStates!.Single(),document!.UvFlags);
            ImGui.Text($"宽高 {instance.Size.X:F4} × {instance.Size.Y:F4} · 滚转 {instance.PositionRoll.W:F3}");
            ImGui.Text($"UV 序列 {instance.Uv&255} / 帧 {(instance.Uv>>8)&255} · 顶点 alpha {instance.Color>>24}");
            ImGui.Text($"位置 {particle.Position.X:F5}, {particle.Position.Y:F5}, {particle.Position.Z:F5}");
            ImGui.SliderInt("原始记录偏移", ref rawOffset, 0, 0x13c);
            ImGui.Text($"+0x{rawOffset:X3}: u32={FrameDecoder.U32(particle.Raw.Span, rawOffset)} float={FrameDecoder.F32(particle.Raw.Span, rawOffset):G9}");
            for (int row = 0; row < 20; row++)
                ImGui.Text($"{row*16:X3}  {Convert.ToHexString(particle.Raw.Span.Slice(row*16,16))}");
        }
    }
    ImGui.End();
    var editCommand=editPanel.Draw(editorState,displayedManifest,editTask!=null,stress!=0,editor.Directory,editError);
    if(editCommand!=null) StartEditorCommand(editCommand);
    gui.Render();
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
    if (options.ContainsKey("smoke") && frame?.Tick == 45 && completedFrames > 1)
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
    // Wait for the one outstanding worker operation before disposing its mapping.
    try {pending?.GetAwaiter().GetResult().Dispose();} catch { }
    try {loading?.GetAwaiter().GetResult();} catch { }
    try {editTask?.GetAwaiter().GetResult();} catch { }
    try {faultTask?.GetAwaiter().GetResult();} catch { }
    preparedPreview?.Renderer.Dispose();
    frame?.Dispose();billboards?.Dispose(); points.Dispose(); grid.Dispose(); particleLayer.Dispose(); hdr.Dispose(); gl.DeleteFramebuffer(presentFbo); gui.Dispose(); input.Dispose(); gl.Dispose();
};
try { window.Run(); }
catch(Exception ex) {Console.Error.WriteLine(ex);Environment.ExitCode=1;}




internal sealed record NativeLoadResult(OfflinePreviewManifest Manifest,PreviewDocument Document);
internal sealed record PreparedPreview(OfflinePreviewManifest Manifest,PreviewDocument Document,BillboardRenderer Renderer);
