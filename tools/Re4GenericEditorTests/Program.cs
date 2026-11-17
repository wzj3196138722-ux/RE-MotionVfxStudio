using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VfxStudio.Re4Render;

[assembly: SupportedOSPlatform("windows")]
try
{
    void Check(bool value,string message) {if(!value)throw new Exception(message);}
    async Task Reject(Func<Task> action,string reason)
    {
        try {await action();}
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or InvalidDataException)
        {Console.WriteLine("REJECT "+reason+": "+ex.Message);return;}
        throw new Exception("Expected rejection: "+reason);
    }
    var synthetic=new PreviewInspection("synthetic",1,0,0,[
        new("large","large","System.UInt64","18446744073709551614",true),
        new("enabled","enabled","System.Boolean","false",true,Choices:["false","true"]),
        new("mode","mode","Test.Enum","One",true,Choices:["One","Two"]),
        new("name","name","System.String","原值",true),
        new("readonly","readonly","System.Int32","7",false,"test metadata")],[],0);
    var simple=new PreviewPropertyDraft(synthetic);
    simple.Set("large","18446744073709551615");simple.Set("enabled","true");simple.Set("mode","Two");simple.Set("name","新值");
    Check(simple.Changes().Length==4 && simple.Value("large")=="18446744073709551615","Exact scalar draft lost values");
    try {simple.Set("readonly","8");throw new Exception("Read-only draft accepted edit");}catch(InvalidOperationException){}
    simple.Reset();Check(!simple.Dirty && simple.Value("name")=="原值","Discard did not restore the draft");
    Console.WriteLine("PASS exact UInt64, bool/enum/text drafts, read-only protection and discard");

    string repo=Directory.GetCurrentDirectory();
    string exe=Environment.GetEnvironmentVariable("RE4_EXE")??@"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
    string root=Path.Combine(repo,"test-output","re4-generic-"+Guid.NewGuid().ToString("N"));
    var fixture=PreviewDocument.Fixture(Path.GetDirectoryName(exe)!,"bb2");
    using var editor=new PreviewEditorSession(repo,root,protectedRoots:[Path.GetDirectoryName(exe)!]);
    var state=await editor.OpenAsync(fixture.Path,11);
    var initial=state.Manifest!;
    Check(state.Attributes?.Any(a=>a.Type=="TypeBillboard3D")==true && state.Attributes.Any(a=>a.Type=="Life"),"No generic attribute list");
    var inspections=new List<(PreviewAttributeInfo Attribute,PreviewInspection Inspection)>();
    foreach(var a in state.Attributes!)
    {
        var s=await editor.InspectAsync(initial.DocumentId,state.Revision,a.Index);
        Check(s.Revision==initial.Revision && !s.CanUndo && s.Inspection?.AttributeIndex==a.Index,"Inspection changed history or target");
        inspections.Add((a,s.Inspection!));
        Console.WriteLine("INSPECT "+a.Type+": "+s.Inspection!.Fields.Length+" fields; choices="+string.Join(",",s.Inspection.Fields.Where(f=>f.Choices!=null).Select(f=>f.Path))+
            "; bool="+string.Join(",",s.Inspection.Fields.Where(f=>f.Type=="System.Boolean" && f.Editable).Select(f=>f.Path)));
    }
    // Field semantics (LiveEditor FieldSemantics.re4.json) reach the inspect output: help, unit, bit layout, pair type.
    var lifeFields=inspections.Single(x=>x.Attribute.Type=="Life").Inspection.Fields;
    var appear=lifeFields.Single(f=>f.Path=="AppearFrame.min");
    Check(appear.Semantic?.Unit=="帧" && appear.Semantic.Help?.Contains("淡入")==true,"Life.AppearFrame semantic missing from inspect");
    Check(PreviewPairKind.For(appear.OwnerType)?.Label=="最小 ~ 最大","Life.AppearFrame is not reported as MinMaxI");
    var holdMode=lifeFields.Single(f=>f.Path=="Flags").Semantic?.Bits?.Groups.SingleOrDefault();
    Check(holdMode is {Shift:0,Width:2,Options.Length:4},"Life.Flags hold mode bits missing from inspect");
    var blend=inspections.Single(x=>x.Attribute.Type=="TypeBillboard3D").Inspection.Fields.Single(f=>f.Path=="Flags").Semantic?.Bits;
    Check(blend?.Groups.Any(g=>g.Shift==4 && g.IsSwitch)==true,"Billboard3D colour header bits missing from inspect");
    // Every key in the table names a real RE4 field path (a typo would silently show nothing).
    var missing=new List<string>();
    foreach(string type in VfxStudio.LiveEditor.FieldSemantics.AttributeTypes)
    {
        var attribute=ReeLib.Efx.EFXAttribute.Create(ReeLib.Efx.EfxVersion.RE4,Enum.Parse<ReeLib.Efx.EfxAttributeType>(type));
        var paths=VfxStudio.LiveEditor.EfxFieldValues.Paths(attribute,ReeLib.Efx.EfxVersion.RE4);
        foreach(string key in VfxStudio.LiveEditor.FieldSemantics.Fields(type).Keys)
            if(!paths.Any(p=>p==key || p.StartsWith(key+".",StringComparison.Ordinal) || p.StartsWith(key+"[",StringComparison.Ordinal))) missing.Add(type+"."+key);
    }
    Check(missing.Count==0,"Field semantics keys without an RE4 field: "+string.Join(", ",missing));
    Console.WriteLine($"PASS field semantics reach inspect ({VfxStudio.LiveEditor.FieldSemantics.AttributeTypes.Count} attribute types, every key resolves)");
    if(args.Contains("--describe")) {Console.WriteLine("SESSION "+root);return;}
    using var resources=new Re4ResourceSource(Path.GetDirectoryName(exe)!);
    using var native=new NativePreviewClient("python",Path.Combine(repo,"tools/Re4Native/re4host.py"),exe);
    async Task<PreviewFrame> Frame(OfflinePreviewManifest manifest)
    {
        manifest.ValidateFile();
        var doc=PreviewDocument.Read(manifest.Path,manifest.Offset,resources);
        await native.LoadAsync(doc.LoadCommand());
        return await native.FrameAsync(45);
    }
    var billboard=inspections.Single(x=>x.Attribute.Type=="TypeBillboard3D").Inspection;
    var draft=new PreviewPropertyDraft(billboard);
    float oldSize=float.Parse(draft.Value("SizeScalar.center"),CultureInfo.InvariantCulture);
    draft.Set("SizeScalar.center",(oldSize*2).ToString("R",CultureInfo.InvariantCulture));
    draft.Set("ColorRate","1");
    var color=billboard.ColorGroups.Single(c=>c.Path=="Color");
    draft.SetColor(color,[1,1,1,1]);
    var fields=draft.Changes();
    Check(fields.Length>=3 && !fields.Any(f=>f.Path=="Color.rgba"),"Color aliases were submitted twice");
    var colorDraft=new PreviewPropertyDraft(billboard);
    try {colorDraft.SetColor(color,[1,1,1,float.NaN]);throw new Exception("Invalid color accepted");}catch(ArgumentException){}
    Check(!colorDraft.Dirty,"Invalid color partially changed the draft");
    using var baseline=await Frame(initial);
    string digest=Convert.ToHexString(SHA256.HashData(baseline.Arrays[0].Span));
    state=await editor.ApplyFieldsAsync(new(billboard,fields));
    Check(state.Revision==initial.Revision+1 && state.Inspection==null,"Generic batch did not commit one revision/invalidate inspection");
    using(var changed=await Frame(state.Manifest!))
    {
        Check(native.LastLoadReused,"Generic edit restarted the native worker");
        Check(changed.Particles.Count==baseline.Particles.Count,"Generic size edit changed births");
        for(int i=0;i<changed.Particles.Count;i++)
        {
            var old=BillboardInstance.Decode(baseline.Particles[i],baseline.EntryStates!.Single(),0);
            var now=BillboardInstance.Decode(changed.Particles[i],changed.EntryStates!.Single(),0);
            Check(now.Size.X>old.Size.X && now.Size.Y>old.Size.Y,"Generic size edit did not reach native geometry");
            Check(FrameDecoder.U32(changed.Particles[i].Raw.Span,0x4c)==uint.MaxValue,"Generic color did not reach native particles");
        }
    }
    state=await editor.CommandAsync("undo");
    Check(state.Manifest!.Sha256==initial.Sha256,"One undo did not restore all fields");
    using(var undone=await Frame(state.Manifest)) Check(Convert.ToHexString(SHA256.HashData(undone.Arrays[0].Span))==digest,"Generic undo did not restore same-tick particles");
    state=await editor.CommandAsync("redo");
    Check(!draft.Matches(state) && draft.Dirty,"External revision silently rebased a dirty draft");
    long before=state.Revision;string sha=state.Manifest!.Sha256;
    await Reject(()=>editor.ApplyFieldsAsync(new(billboard,fields)),"old revision");
    await Reject(()=>editor.InspectAsync("wrong-document",before,billboard.AttributeIndex),"wrong document");
    state=await editor.InspectAsync(initial.DocumentId,before,billboard.AttributeIndex);
    var current=state.Inspection!;
    await Reject(()=>editor.ApplyFieldsAsync(new(current,[new("ColorRate",".4"),new("SizeScalar.center","NaN")])),"non-finite batch rollback");
    Check(editor.State!.Revision==before && editor.State.Manifest!.Sha256==sha,"Rejected numeric batch changed the document");
    await Reject(()=>editor.ApplyFieldsAsync(new(current,[new("Flags","4294967296")])),"UInt32 overflow");
    var readOnly=current.Fields.First(f=>!f.Editable);
    await Reject(()=>editor.ApplyFieldsAsync(new(current,[new(readOnly.Path,readOnly.Value)])),"read-only client guard");
    await Reject(()=>editor.SendAsync("set_fields",new(){
        ["target"]=new JsonObject{["kind"]="entry",["index"]=current.EntryPosition,["attributeIndex"]=current.AttributeIndex},
        ["fields"]=new JsonArray(new JsonObject{["path"]=readOnly.Path,["value"]=readOnly.Value})},before),"read-only backend guard");
    Check(editor.State!.Revision==before && editor.State.Manifest!.Sha256==sha,"Read-only edit changed history");
    uint flags=uint.Parse(current.Fields.Single(f=>f.Path=="Flags").Value,CultureInfo.InvariantCulture);
    // BlendType 0-4 exist; 5 is outside the enum and must be refused by the preview.
    state=await editor.ApplyFieldsAsync(new(current,[new("Flags",((flags&~15u)|5u).ToString(CultureInfo.InvariantCulture))]));
    try {PreviewDocument.Read(state.Manifest!.Path,state.Manifest.Offset,resources);throw new Exception("Unsupported blend was loaded");}
    catch(NotSupportedException ex) when(ex.Message.Contains("混合方式 5")) {}
    state=await editor.CommandAsync("undo");
    Check(state.Manifest!.Sha256==sha,"Undo after unsupported preview did not restore the editable document");
    before=state.Revision;
    var lifeIndex=state.Attributes!.Single(a=>a.Type=="Life").Index;
    state=await editor.InspectAsync(initial.DocumentId,before,lifeIndex);
    var life=state.Inspection!;
    await Reject(()=>editor.ApplyFieldsAsync(new(life,[new("AppearFrame.min","20"),new("AppearFrame.max","10")])),"inverted Life interval");
    Check(editor.State!.Revision==before && editor.State.Manifest!.Sha256==sha,"Rejected interval changed history");
    state=await editor.ApplyFieldsAsync(new(life,[new("AppearFrame.min","10"),new("AppearFrame.max","20")]));
    state=await editor.InspectAsync(initial.DocumentId,state.Revision,lifeIndex);
    Check(state.Inspection!.Fields.Single(f=>f.Path=="AppearFrame.min").Value=="10","Generic integer readback failed");
    state=await editor.InspectAsync(initial.DocumentId,state.Revision,billboard.AttributeIndex);
    var scalarSnapshot=state.Inspection!;
    var toggle=scalarSnapshot.Fields.Single(f=>f.Path=="EnableGroupColor");
    string nextBool=toggle.Value=="true"?"false":"true";
    state=await editor.ApplyFieldsAsync(new(scalarSnapshot,[new(toggle.Path,nextBool)]));
    state=await editor.InspectAsync(initial.DocumentId,state.Revision,billboard.AttributeIndex);
    Check(state.Inspection!.Fields.Single(f=>f.Path==toggle.Path).Value==nextBool,"Boolean field readback failed");
    int enumIndex=state.Attributes!.Single(a=>a.Type=="Transform3D").Index;
    state=await editor.InspectAsync(initial.DocumentId,state.Revision,enumIndex);
    var enumSnapshot=state.Inspection!;
    var enumField=enumSnapshot.Fields.Single(f=>f.Path=="RotationOrder");
    string nextEnum=enumField.Choices!.First(c=>c!=enumField.Value);
    state=await editor.ApplyFieldsAsync(new(enumSnapshot,[new(enumField.Path,nextEnum)]));
    state=await editor.InspectAsync(initial.DocumentId,state.Revision,enumIndex);
    Check(state.Inspection!.Fields.Single(f=>f.Path==enumField.Path).Value==nextEnum,"Enum field readback failed");
    var uvIndex=state.Attributes!.Single(a=>a.Type=="UVSequence").Index;
    state=await editor.InspectAsync(initial.DocumentId,state.Revision,uvIndex);
    var uvSnapshot=state.Inspection!;
    var pathField=uvSnapshot.Fields.Single(f=>f.Path=="UVSPath");
    string nextPath=pathField.Value.ToUpperInvariant();
    state=await editor.ApplyFieldsAsync(new(uvSnapshot,[new(pathField.Path,nextPath)]));
    state=await editor.CommandAsync("save");
    string saved=state.SavedPath!;
    state=await editor.OpenSavedAsync();
    state=await editor.InspectAsync(state.Manifest!.DocumentId,state.Revision,lifeIndex);
    Check(state.Inspection!.Fields.Single(f=>f.Path=="AppearFrame.max").Value=="20","Saved generic fields did not survive reopen");
    state=await editor.InspectAsync(state.Manifest!.DocumentId,state.Revision,billboard.AttributeIndex);
    Check(state.Inspection!.Fields.Single(f=>f.Path==toggle.Path).Value==nextBool,"Saved Boolean field did not survive reopen");
    state=await editor.InspectAsync(state.Manifest!.DocumentId,state.Revision,enumIndex);
    Check(state.Inspection!.Fields.Single(f=>f.Path==enumField.Path).Value==nextEnum,"Saved enum field did not survive reopen");
    state=await editor.InspectAsync(state.Manifest!.DocumentId,state.Revision,uvIndex);
    Check(state.Inspection!.Fields.Single(f=>f.Path==pathField.Path).Value==nextPath,"Saved resource string did not survive reopen");
    await Reject(()=>editor.ApplyFieldsAsync(new(life,[new("AppearFrame.min","11")])),"old document after reopen");
    File.WriteAllText(Path.Combine(root,"generic-result.json"),JsonSerializer.Serialize(new{status="PASS",saved,attributes=inspections.Count,nativeDigest=digest}));
    Console.WriteLine("PASS attribute inspection, atomic generic color/size/Life, native geometry/digest, stale document/revision, read-only, rollback, save/reopen; "+root);
}
catch(Exception ex) {Console.Error.WriteLine(ex);Environment.ExitCode=1;}
