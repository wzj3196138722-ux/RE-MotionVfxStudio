using System.Numerics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using ReeLib;
using ReeLib.Efx.Structs.Basic;
using VfxStudio.Core.Services;
using VfxStudio.Re4Render;

[assembly: SupportedOSPlatform("windows")]
try
{
    string repo=Directory.GetCurrentDirectory();
    string exe=Environment.GetEnvironmentVariable("RE4_EXE")??@"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
    var fixture=PreviewDocument.Fixture(Path.GetDirectoryName(exe)!,"bb2");
    string root=Path.Combine(repo,"test-output/re4-preview/p2-"+Guid.NewGuid().ToString("N"));
    // A stand-in protected root inside test-output: the backend refusal is tested by writing there,
    // never by attempting a write into the real game directory.
    string protectedRoot=Path.Combine(root,"protected-game");
    Directory.CreateDirectory(protectedRoot);
    string gameDirectory=Path.GetDirectoryName(exe)!;
    using var editor=new PreviewEditorSession(repo,root,protectedRoots:[gameDirectory,protectedRoot]);
    using var native=new NativePreviewClient("python",Path.Combine(repo,"tools/Re4Native/re4host.py"),exe);
    using var resources=new Re4ResourceSource(Path.GetDirectoryName(exe)!);
    var reports=new List<object>();
    void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
    string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    Vector4 Color(int[] c)=>new(c[0]/255f,c[1]/255f,c[2]/255f,c[3]/255f);
    BillboardEdit Edit(PreviewEditorParameters p)=>new(Color(p.Color),Color(p.ColorRange),p.ColorRate,new(p.SizeScalar[0],p.SizeScalar[1]),new(p.SizeX[0],p.SizeX[1]),new(p.SizeY[0],p.SizeY[1]));
    async Task<PreviewFrame> Frame(OfflinePreviewManifest manifest,int tick)
    {
        manifest.ValidateFile();
        var doc=PreviewDocument.Read(manifest.Path,manifest.Offset,resources);
        await native.LoadAsync(doc.LoadCommand());
        return await native.FrameAsync(tick);
    }
    byte[] EntryBytes(ReeLib.Efx.EFXEntry entry)
    {
        using var stream=new MemoryStream();
        using var handler=new FileHandler(stream,"memory.efx.3539837"){FileVersion=3539837};
        entry.Write(handler);return stream.ToArray();
    }
    string sourceHash=Hash(fixture.Path);
    var state=await editor.OpenAsync(fixture.Path,11);
    Check(state.Manifest!=null && state.Parameters!=null && !state.Dirty,"Open did not produce authoritative preview state");
    var initial=state.Manifest!;
    initial.ValidateFile();
    var originalParameters=state.Parameters!;
    using var baseline=await Frame(initial,45);
    string baselineHash=Convert.ToHexString(SHA256.HashData(baseline.Arrays[0].Span));
    var input=Edit(originalParameters) with {Color=Vector4.One,ColorRange=Vector4.One,ColorRate=1f,SizeScalar=new(originalParameters.SizeScalar[0]*2,originalParameters.SizeScalar[1]*2)};
    var watch=System.Diagnostics.Stopwatch.StartNew();
    state=await editor.ApplyBillboardAsync(input,state.Revision);
    Check(state.Revision==initial.Revision+1 && state.CanUndo && state.Dirty && state.Manifest!.Revision==state.Revision,"Billboard transaction revision/history mismatch");
    using var changed=await Frame(state.Manifest!,45);
    Check(changed.Particles.Count==baseline.Particles.Count,"Size/color edit changed births");
    for(int i=0;i<changed.Particles.Count;i++)
    {
        var old=BillboardInstance.Decode(baseline.Particles[i],baseline.EntryStates!.Single(),0);
        var now=BillboardInstance.Decode(changed.Particles[i],changed.EntryStates!.Single(),0);
        Check(now.Size.X>old.Size.X && now.Size.Y>old.Size.Y,"Edited birth size did not increase native dimensions");
        Check(FrameDecoder.U32(changed.Particles[i].Raw.Span,0x4c)==uint.MaxValue,"Native birth color did not change to white");
    }
    Check(changed.EntryStates!.Single().ColorRate==1,"Native live ColorRate did not update");
    reports.Add(new{check="color_size_native",status="PASS",edit_to_frame_ms=watch.Elapsed.TotalMilliseconds});
    var service=new EfxEditorService("3539837");
    using(var before=service.Load(initial.Path))
    using(var after=service.Load(state.Manifest!.Path))
    {
        Check(before.Entries.Count==after.Entries.Count,"Whole-document entry count changed");
        for(int i=0;i<before.Entries.Count;i++)
            if(i!=initial.EntryPosition) Check(EntryBytes(before.Entries[i]).SequenceEqual(EntryBytes(after.Entries[i])),"Unselected entry changed");
    }
    Console.WriteLine("PASS full EFX publication, color/size native result, other entries preserved");
    state=await editor.CommandAsync("undo");
    Check(state.Manifest!.Sha256==initial.Sha256,"Undo did not restore full serialized EFX");
    using(var undo=await Frame(state.Manifest!,45)) Check(Convert.ToHexString(SHA256.HashData(undo.Arrays[0].Span))==baselineHash,"Undo did not restore native particle digest");
    state=await editor.CommandAsync("redo");
    long beforeInvalid=state.Revision;string beforeInvalidHash=state.Manifest!.Sha256;
    try{await editor.ApplyLifeAsync(new(20,10,200,200,20,20),state.Revision);throw new Exception("Invalid Life accepted by the form");}
    catch(ArgumentException){}
    Check(editor.State!.Revision==beforeInvalid && editor.State.Manifest!.Sha256==beforeInvalidHash,"Invalid Life changed document");
    // The backend must reject the same inverted range when the form is bypassed.
    {
        var p=editor.State.Parameters!;
        var raw=new System.Text.Json.Nodes.JsonObject{
            ["target"]=new System.Text.Json.Nodes.JsonObject{["kind"]="entry",["index"]=p.EntryPosition,["attributeIndex"]=p.LifeAttribute},
            ["fields"]=new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject{["path"]="AppearFrame.min",["value"]="20"},
                new System.Text.Json.Nodes.JsonObject{["path"]="AppearFrame.max",["value"]="10"})};
        try{await editor.SendAsync("set_fields",raw,beforeInvalid);throw new Exception("Backend accepted an inverted Life range");}
        catch(InvalidOperationException ex) when(ex.Message.Contains("AppearFrame") && ex.Message.Contains("min ≤ max")){}
        state=await editor.CommandAsync("status");
        Check(state.Revision==beforeInvalid && state.Manifest!.Sha256==beforeInvalidHash && state.Parameters!.Appear![0]==p.Appear![0] && state.Parameters.Appear[1]==p.Appear[1],
            "Backend Life rejection did not roll back the document");
    }
    state=await editor.ApplyLifeAsync(new(0,0,200,200,20,20),state.Revision);
    using(var life=await Frame(state.Manifest!,120))
    {
        Check(life.Particles.Count==4 && life.Particles.Any(p=>p.Id==0),"Native life edit did not extend first particle");
        Check(life.Particles.All(p=>p.LifeAlpha==255),"Edited Keep phase should have full life alpha");
    }
    Console.WriteLine("PASS undo native digest, redo, Life extension, inverted Life range rejected by form and backend");
    state=await editor.CommandAsync("save");
    Check(!state.Dirty && state.SavedPath!=null && File.Exists(state.SavedPath),"Save did not create a clean copy");
    string saved=state.SavedPath!;
    using(var reread=service.Load(saved))
    {
        var entry=reread.Entries[state.Manifest!.EntryPosition];
        Check(entry.Attributes.OfType<EFXAttributeLife>().Single().KeepFrame.min==200,"Saved Life readback differs");
    }
    state=await editor.OpenSavedAsync();
    Check(!state.Dirty && state.Parameters!.Keep![0]==200 && state.Manifest!.Source==saved,"Opening saved copy lost edits");
    long afterOpen=state.Revision;
    string savedHash=Hash(saved);
    // The reopened copy is now the imported source: saving onto it must fail for that reason.
    try{await editor.CommandAsync("save",saved);throw new Exception("Overwrite of the imported source accepted");}
    catch(InvalidOperationException ex) when(ex.Message.Contains("imported source is protected")){}
    Check(editor.State!.Revision==afterOpen && Hash(saved)==savedHash,"Failed save changed the imported source or revision");
    // Paths inside the game or unpacked-resource directories: the client refuses before sending ...
    string intoGame=Path.Combine(gameDirectory,"natives","stm","p2_test_never_written.efx.3539837");
    try{await editor.CommandAsync("save",intoGame);throw new Exception("Save into the game directory accepted by the client");}
    catch(ArgumentException){}
    Check(!File.Exists(intoGame),"Save wrote into the game directory");
    // ... and the backend refuses independently (stand-in root, raw request).
    string intoProtected=Path.Combine(protectedRoot,"natives","stm","copy.efx.3539837");
    try{await editor.SendAsync("save",new System.Text.Json.Nodes.JsonObject{["path"]=intoProtected});throw new Exception("Backend saved into a protected root");}
    catch(InvalidOperationException ex) when(ex.Message.Contains("受保护目录")){}
    Check(!File.Exists(intoProtected) && editor.State!.Revision==afterOpen,"Backend protected-root refusal wrote a file or changed revision");
    Check(Hash(fixture.Path)==sourceHash,"Game source was modified");
    state=await editor.CommandAsync("status");
    try{await editor.ApplyBillboardAsync(input,initial.Revision);throw new Exception("Stale draft accepted");}
    catch(InvalidOperationException){}
    Check(editor.State!.Revision==afterOpen,"Stale revision mutated document");
    state=await editor.CommandAsync("status");
    var prior=state.Manifest!;
    using(var locked=new FileStream(editor.ManifestPath,FileMode.Open,FileAccess.Read,FileShare.None))
    {
        state=await editor.ApplyBillboardAsync(input with{ColorRate=2},state.Revision);
        Check(state.Revision==afterOpen+1 && state.PublicationError!=null && state.Manifest!.Key==prior.Key,"Publication failure lost committed edit or exposed incomplete revision");
    }
    long committed=state.Revision;
    state=await editor.CommandAsync("publish_offline_preview");
    Check(state.Revision==committed && state.PublicationError==null && state.Manifest!.Revision==committed,"Publication retry repeated mutation or failed recovery");
    state.Manifest!.ValidateFile();
    File.AppendAllText(state.Manifest.Path,"tampered");
    try{state.Manifest.ValidateFile();throw new Exception("Corrupted published bytes accepted");}
    catch(InvalidDataException){}
    state=await editor.CommandAsync("publish_offline_preview");
    state.Manifest!.ValidateFile();
    using(var recovered=await Frame(state.Manifest,45)) Check(recovered.EntryStates!.Single().ColorRate==2,"Native preview did not recover from bad publication");
    Check(Hash(fixture.Path)==sourceHash,"Game source was modified");
    Console.WriteLine("PASS save/readback/reopen, imported-source and game/unpacked-directory save refusal (client + backend), stale revision guard, publication retry, corrupt snapshot recovery");
    reports.Add(new{check="undo_life_save_recovery",status="PASS",original_source_unchanged=true});
    File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("RESULT "+Path.Combine(root,"results.json"));
}
catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}
