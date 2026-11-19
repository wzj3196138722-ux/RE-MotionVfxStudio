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

    // Mode conditions (FieldSemantics "when"): enum names, bit groups, non-zero, Any / All, and a missing field.
    {
        PreviewFieldSemantic When(PreviewFieldCondition c)=>new(null,null,null,null,c);
        var shape=new PreviewFieldCondition("ShapeType",null,null,["Shape3DType_Box"],null,null,null,null,"只有 Box 读它");
        var axis=new PreviewFieldCondition(null,null,null,null,null,null,null,
            [shape,new("RangeDivideNum",null,null,null,null,true,null,null,"RangeDivideNum 为 0")],null);
        var mode=new PreviewFieldCondition("Flags",0,2,null,["0"],null,null,null,"PlayMode 为静止");
        var colorRule=new PreviewFieldCondition(null,null,null,null,null,null,
            [new("Flags",4,1,["1"],null,null,null,null,null),new("Flags",0,4,["3"],null,null,null,null,null)],null,"不随机也不是 EdgeBlend");
        var nowhere=new PreviewFieldCondition("Nowhere",null,null,["1"],null,null,null,null,"x");
        var modes=new PreviewInspection("modes",1,0,0,[
            new("ShapeType","ShapeType","Shape3DType","Shape3DType_Sphere",true,Choices:["Shape3DType_Box","Shape3DType_Sphere"]),
            new("RangeDivideNum","RangeDivideNum","System.UInt32","0",true,Semantic:When(shape)),
            new("RangeDivideAxis","RangeDivideAxis","System.UInt32","0",true,Semantic:When(axis)),
            new("Flags","Flags","System.UInt32","0",true),
            new("PlaySpeed","PlaySpeed","System.Single","1",true,Semantic:When(mode)),
            new("ColorRange","ColorRange","System.UInt32","0",true,Semantic:When(colorRule)),
            new("Unknown","Unknown","System.Single","1",true,Semantic:When(nowhere))],[],0);
        var md=new PreviewPropertyDraft(modes);
        var paths=modes.Fields.ToDictionary(f=>f.Path);
        string? V(string p)=>paths.ContainsKey(p)?md.Value(p):null;
        string? Off(string p)=>PreviewFieldActivity.Inactive(paths[p],V);
        Check(Off("RangeDivideNum")=="只有 Box 读它" && Off("RangeDivideAxis")!=null && Off("PlaySpeed")=="PlayMode 为静止" &&
            Off("ColorRange")!=null && Off("Unknown")==null,"Mode conditions: wrong inactive set for Sphere / Flags 0");
        md.Set("ShapeType","Shape3DType_Box");md.Set("Flags","1");
        Check(Off("RangeDivideNum")==null && Off("RangeDivideAxis")!=null && Off("PlaySpeed")==null && Off("ColorRange")!=null,
            "Mode conditions do not follow the draft (Box, Flags 1)");
        md.Set("RangeDivideNum","3");md.Set("Flags","3");
        Check(Off("RangeDivideAxis")==null && Off("ColorRange")==null,"All / Any conditions (RangeDivideNum 3, EdgeBlend)");
        md.Set("Flags","16");
        Check(Off("ColorRange")==null && Off("PlaySpeed")=="PlayMode 为静止","Bit 4 alone keeps ColorRange; PlayMode 0 again");
        var ctl=PreviewFieldActivity.Controls(modes.Fields);
        Check(ctl["ShapeType"].SequenceEqual(["RangeDivideNum","RangeDivideAxis"]) && ctl["Flags"].SequenceEqual(["PlaySpeed","ColorRange"]) &&
            ctl["RangeDivideNum"].SequenceEqual(["RangeDivideAxis"]) && !ctl.ContainsKey("Nowhere"),"Mode fields and their dependants");
        Console.WriteLine("PASS mode conditions: enum, bits, non-zero, Any / All, draft values, missing field counts as met");
    }

    // Edit targets: a top-level entry, an entry inside an Action's PlayEmitter, and how they are found in the backend's
    // document tree. Equality is by value (the path is a list), so a target read again from JSON matches the old one.
    {
        var top=PreviewEditTarget.Root(3);
        var nested=new PreviewEditTarget("entry",2,[new("action",1,0)]);
        var again=PreviewEditTarget.Parse("entry",2,JsonNode.Parse("""[{"kind":"action","index":1,"attributeIndex":0}]"""));
        Check(top.IsRoot && !nested.IsRoot && nested.Equals(again) && nested.GetHashCode()==again.GetHashCode() && !nested.Equals(top),"Edit target equality");
        Check(top.ToJson(4).ToJsonString()=="{\"kind\":\"entry\",\"index\":3,\"attributeIndex\":4}" &&
            nested.ToJson(5)["embeddedPath"]![0]!["attributeIndex"]!.GetValue<int>()==0 && nested.Label=="Action 1 › 属性 0 › [2]","Edit target JSON and label");
        var doc=JsonNode.Parse("""
            {"entries":[{"index":0,"attributes":[]}],"actions":[{"index":0,"attributes":[]},{"index":1,"attributes":[
            {"index":0,"type":"PlayEmitter","embedded":{"entries":[{"index":0,"attributes":[]},{"index":1,"attributes":[]},{"index":2,"attributes":[{"index":0,"type":"Life"}]}],"actions":[]}}]}]}
            """);
        Check(nested.Find(doc)?["attributes"]?[0]?["type"]?.GetValue<string>()=="Life" && top.Find(doc)==null &&
            new PreviewEditTarget("entry",3,[new("action",1,0)]).Find(doc)==null && new PreviewEditTarget("entry",0,[new("action",0,0)]).Find(doc)==null,
            "Edit target lookup in the document tree");
        foreach(var bad in new[]{"[{\"index\":0}]","[{\"kind\":\"group\",\"index\":0,\"attributeIndex\":0}]","{}"})
            await Reject(()=>Task.FromResult(PreviewEditTarget.Parse("entry",0,JsonNode.Parse(bad))),"malformed embeddedPath "+bad);
        Console.WriteLine("PASS edit targets: equality, JSON, label, document lookup, malformed paths");
    }

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
    // Every field a "when" condition tests exists in the same RE4 attribute.
    var badModes=new List<string>();
    int conditions=0;
    static IEnumerable<string> Tested(VfxStudio.LiveEditor.FieldCondition c)=>
        (c.Field==null?[]:new[]{c.Field}).Concat((c.Any??[]).Concat(c.All??[]).SelectMany(Tested));
    foreach(string type in VfxStudio.LiveEditor.FieldSemantics.AttributeTypes)
    {
        var attribute=ReeLib.Efx.EFXAttribute.Create(ReeLib.Efx.EfxVersion.RE4,Enum.Parse<ReeLib.Efx.EfxAttributeType>(type));
        var paths=VfxStudio.LiveEditor.EfxFieldValues.Paths(attribute,ReeLib.Efx.EfxVersion.RE4);
        foreach(var (key,semantic) in VfxStudio.LiveEditor.FieldSemantics.Fields(type))
            if(semantic.When is {} when)
            {
                conditions++;
                foreach(string field in Tested(when)) if(!paths.Contains(field)) badModes.Add($"{type}.{key} -> {field}");
                if(when.Why==null && when.Any==null && when.All==null) badModes.Add($"{type}.{key}: no why");
            }
    }
    Check(conditions>0 && badModes.Count==0,"Mode conditions testing a missing field: "+string.Join(", ",badModes));
    // On the fixture: Life.KeepHoldFrame is read only with a hold mode, and Flags is reported as its mode field.
    var lifeInspection=inspections.Single(x=>x.Attribute.Type=="Life").Inspection;
    var lifeDraft=new PreviewPropertyDraft(lifeInspection);
    var lifeByPath=lifeInspection.Fields.ToDictionary(f=>f.Path);
    string? LifeValue(string p)=>lifeByPath.ContainsKey(p)?lifeDraft.Value(p):null;
    var hold=lifeInspection.Fields.First(f=>f.Path.StartsWith("KeepHoldFrame",StringComparison.Ordinal));
    uint lifeFlags=uint.Parse(lifeDraft.Value("Flags"),CultureInfo.InvariantCulture);
    Check(hold.Semantic?.When!=null && (PreviewFieldActivity.Inactive(hold,LifeValue)!=null)==((lifeFlags&3)==0),"Life.KeepHoldFrame activity vs Flags "+lifeFlags);
    lifeDraft.Set("Flags",((lifeFlags&~3u)|((lifeFlags&3)==0?1u:0u)).ToString(CultureInfo.InvariantCulture));
    Check((PreviewFieldActivity.Inactive(hold,LifeValue)!=null)!=((lifeFlags&3)==0),"Life.KeepHoldFrame activity does not follow the draft's Flags");
    Check(PreviewFieldActivity.Controls(lifeInspection.Fields).TryGetValue("Flags",out var holdDeps) && holdDeps.Any(p=>p.StartsWith("KeepHoldFrame",StringComparison.Ordinal)),
        "Life.Flags is not reported as KeepHoldFrame's mode field");
    Console.WriteLine($"PASS mode conditions reach inspect ({conditions} conditions, every tested field exists; Life.KeepHoldFrame follows Flags)");
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

    // Material overrides on a TypeMesh entry (not drawn, but selectable): every existing override matches the
    // referenced .mdf2; one parameter and one texture are added from it and removed again (each one revision, undoable);
    // a duplicate is refused, and so is removing a parameter a material curve drives.
    {
        string stm=Path.Combine(Path.GetDirectoryName(exe)!,"RE4_EXTRACT","natives","stm");
        int IdAt(string path,long start)
        {
            var efx=new ReeLib.EfxFile(new ReeLib.FileHandler(new MemoryStream(File.ReadAllBytes(path),false),path));efx.Read();
            return efx.Entries.Single(e=>e.Start==start).index;
        }
        string meshFile=Path.Combine(stm,@"_anotherorder\vfx\effecteditor\efd_character\efd_ao_chc3\efd_0015_ao_chc3_hold_dead_0000.efx.3539837");
        using var meshEditor=new PreviewEditorSession(repo,Path.Combine(root,"mesh"),protectedRoots:[Path.GetDirectoryName(exe)!]);
        var ms=await meshEditor.OpenAsync(meshFile,IdAt(meshFile,24102));
        int mesh=ms.Attributes!.Single(a=>a.Type=="TypeMesh").Index;
        async Task<Dictionary<string,PreviewFieldInfo>> MeshFieldsAsync()
        {
            ms=await meshEditor.InspectAsync(ms.Manifest!.DocumentId,ms.Revision,mesh);
            return ms.Inspection!.Fields.ToDictionary(f=>f.Path);
        }
        int Count(Dictionary<string,PreviewFieldInfo> f){int n=0;while(f.ContainsKey($"properties[{n}].PropertyNameUTF8Hash")) n++;return n;}
        JsonObject Body(JsonObject b){b["target"]=new JsonObject{["kind"]="entry",["index"]=ms.Manifest!.EntryPosition,["attributeIndex"]=mesh};return b;}
        var mf=await MeshFieldsAsync();
        var reference=PreviewMaterialReference.FromResource(resources,mf["MaterialPath"].Value);
        int before0=Count(mf);
        var present=new HashSet<uint>();
        for(int i=0;i<before0;i++)
        {
            string F(string leaf)=>mf[$"properties[{i}].{leaf}"].Value;
            uint hash=uint.Parse(F("PropertyNameUTF8Hash"),CultureInfo.InvariantCulture);present.Add(hash);
            string? why=reference.Mismatch(hash,int.Parse(F("mdfPropertyIndex"),CultureInfo.InvariantCulture),int.Parse(F("mdfParameterValueCount"),CultureInfo.InvariantCulture),F("parameterType")=="Texture");
            Check(why==null,$"Existing override {hash} does not match {reference.Source}: {why}");
        }
        var slot=reference.Slots.First(s=>!s.Texture && !present.Contains(s.Hash));
        long rev=ms.Revision;
        ms=(await meshEditor.SendRawAsync("material_parameter",Body(PreviewMaterialReference.AddBody(slot,"properties")),rev)).State;
        Check(ms.Revision==rev+1 && ms.CanUndo,"Adding a material override is not one undoable revision");
        mf=await MeshFieldsAsync();
        string A(string leaf)=>mf[$"properties[{before0}].{leaf}"].Value;
        Check(Count(mf)==before0+1 && A("PropertyNameUTF8Hash")==slot.Hash.ToString(CultureInfo.InvariantCulture) &&
            A("mdfPropertyIndex")==slot.Index.ToString(CultureInfo.InvariantCulture) &&
            A("parameterType")==(slot.ComponentCount==4?"Float":"Range"),"Added override does not carry the material's name, index and type");
        if(slot.ComponentCount==1)
            Check(float.Parse(A("VectorValue.Z"),CultureInfo.InvariantCulture)==slot.Value.X && float.Parse(A("VectorValue.W"),CultureInfo.InvariantCulture)==slot.Value.X &&
                float.Parse(A("VectorValue.X"),CultureInfo.InvariantCulture)==0,"A one-component override must start as (0, 0, v, v)");
        await Reject(()=>meshEditor.SendRawAsync("material_parameter",Body(PreviewMaterialReference.AddBody(slot,"properties")),ms.Revision),"duplicate material override");
        if(reference.Slots.FirstOrDefault(s=>s.Texture && !present.Contains(s.Hash)) is {} tex)
        {
            ms=(await meshEditor.SendRawAsync("material_parameter",Body(PreviewMaterialReference.AddBody(tex,"properties")),ms.Revision)).State;
            mf=await MeshFieldsAsync();
            Check(mf[$"properties[{before0+1}].texturePath"].Value==tex.TexturePath && mf[$"properties[{before0+1}].mdfPropertyIndex"].Value=="-1",
                "Added texture override lost its path or index -1");
            ms=(await meshEditor.SendRawAsync("material_parameter",Body(new(){["op"]="remove",["listPath"]="properties",["index"]=before0+1}),ms.Revision)).State;
        }
        ms=(await meshEditor.SendRawAsync("material_parameter",Body(new(){["op"]="remove",["listPath"]="properties",["index"]=before0}),ms.Revision)).State;
        mf=await MeshFieldsAsync();
        Check(Count(mf)==before0,"Removing the added override did not restore the table");
        ms=await meshEditor.CommandAsync("undo");
        mf=await MeshFieldsAsync();
        Check(Count(mf)==before0+1,"Undo did not bring the removed override back");

        // A parameter a TypeMeshClip curve drives cannot be removed.
        string rippleFile=Path.Combine(stm,@"_anotherorder\vfx\effecteditor\efd_event\cs\efd_cse340_s00\efd_0015_cse340_s00_ripple_0000.efx.3539837");
        ms=await meshEditor.OpenAsync(rippleFile,IdAt(rippleFile,57));
        mesh=ms.Attributes!.Single(a=>a.Type=="TypeMesh").Index;
        mf=await MeshFieldsAsync();
        var efxRipple=new ReeLib.EfxFile(new ReeLib.FileHandler(new MemoryStream(File.ReadAllBytes(rippleFile),false),rippleFile));efxRipple.Read();
        var curveHashes=efxRipple.Entries.Single(e=>e.Start==57).Attributes.OfType<ReeLib.Efx.IMaterialClipAttribute>()
            .SelectMany(c=>c.MaterialClip.mdfProperties??Array.Empty<ReeLib.Efx.Structs.Common.EfxMaterialClip_Struct4>()).Select(p=>p.mdfPropertyHash).ToHashSet();
        int driven=Enumerable.Range(0,Count(mf)).First(i=>curveHashes.Contains(uint.Parse(mf[$"properties[{i}].PropertyNameUTF8Hash"].Value,CultureInfo.InvariantCulture)));
        long rippleRevision=ms.Revision;
        await Reject(()=>meshEditor.SendRawAsync("material_parameter",Body(new(){["op"]="remove",["listPath"]="properties",["index"]=driven}),rippleRevision),"remove a curve-driven material override");
        Check(meshEditor.State!.Revision==rippleRevision,"Refused removal changed the document");
        Console.WriteLine($"PASS material overrides: {before0} existing match {Path.GetFileName(reference.Source.Split(" :: ")[^1])}; add/duplicate/texture/remove/undo; " +
            $"curve-driven parameter kept (hash {mf[$"properties[{driven}].PropertyNameUTF8Hash"].Value})");
    }

    // Entries embedded in an Action's PlayEmitter are edited through the same session calls as top-level ones; the
    // preview manifest stays on its top-level entry. A draft read from one target is refused on another.
    {
        string stm=Path.Combine(Path.GetDirectoryName(exe)!,"RE4_EXTRACT","natives","stm");
        string damage=Path.Combine(stm,@"_anotherorder\vfx\effecteditor\efd_character\efd_ao_chfa\efd_0015_ao_chfa_0000_damage_0000.efx.3539837");
        using var ee=new PreviewEditorSession(repo,Path.Combine(root,"embedded"),protectedRoots:[Path.GetDirectoryName(exe)!]);
        var es=await ee.OpenAtAsync(damage,0);
        int top=es.Manifest!.EntryPosition;
        var tree=(await ee.SendRawAsync("status",new())).Reply["document"]!;
        var hop=tree["actions"]!.AsArray().SelectMany(a=>a!["attributes"]!.AsArray().Where(x=>x!["embedded"]?["entries"]?.AsArray().Count>0)
            .Select(x=>new PreviewEditHop("action",a!["index"]!.GetValue<int>(),x!["index"]!.GetValue<int>()))).First();
        var nested=new PreviewEditTarget("entry",0,[hop]);
        int expected=nested.Find(tree)!["attributes"]!.AsArray().Count;
        es=await ee.SelectTargetAsync(nested);
        Check(nested.Equals(es.EditTarget) && es.Attributes!.Length==expected && es.Manifest!.EntryPosition==top,"Selecting an embedded entry lists its attributes and keeps the preview entry");
        // The first attribute with an editable number.
        PreviewFieldInfo? number=null;int attribute=-1;
        foreach(var a in es.Attributes!)
        {
            es=await ee.InspectAsync(es.Manifest!.DocumentId,es.Revision,a.Index);
            number=es.Inspection!.Fields.FirstOrDefault(f=>f.Editable && f.Type=="System.Single" && float.TryParse(f.Value,CultureInfo.InvariantCulture,out _));
            if(number!=null) {attribute=a.Index;break;}
        }
        Check(number!=null && nested.Equals(es.Inspection!.EditTarget),"An embedded attribute can be inspected");
        string original=number!.Value,next=(float.Parse(original,CultureInfo.InvariantCulture)+0.25f).ToString("R",CultureInfo.InvariantCulture);
        long rev=es.Revision;
        es=await ee.ApplyFieldsAsync(new(es.Inspection!,[new(number.Path,next)]));
        Check(es.Revision==rev+1 && nested.Equals(es.EditTarget) && es.Manifest!.Revision==es.Revision,"Editing an embedded field is one revision and republishes the file");
        es=await ee.InspectAsync(es.Manifest!.DocumentId,es.Revision,attribute);
        Check(es.Inspection!.Fields.Single(f=>f.Path==number.Path).Value==next,"Embedded field edit did not stick");
        var embeddedDraft=es.Inspection!;
        es=await ee.SelectEntryAsync(top);
        Check(es.Target==null && es.EditTarget!.IsRoot,"Selecting a top-level entry leaves the embedded one");
        await Reject(()=>ee.ApplyFieldsAsync(new(embeddedDraft,[new(number.Path,original)])),"embedded draft applied to a top-level entry");
        await Reject(()=>ee.SelectTargetAsync(new("entry",9999,[hop])),"embedded entry that does not exist");
        Check(ee.State!.EditTarget!.IsRoot && ee.State.Revision==es.Revision,"A refused selection keeps the current target");
        es=await ee.CommandAsync("undo");
        es=await ee.SelectTargetAsync(nested);
        es=await ee.InspectAsync(es.Manifest!.DocumentId,es.Revision,attribute);
        Check(es.Inspection!.Fields.Single(f=>f.Path==number.Path).Value==original,"Undo did not restore the embedded field");
        Console.WriteLine($"PASS embedded entry: {nested.Label} ({expected} attributes) select/inspect/set/undo, preview stays on [{top}], stale draft refused");

        // The Action itself: its PlayEmitter is inspected and edited like an entry attribute, the catalogue offers only
        // PlayEmitter / PlayEfx, and swapping the PlayEmitter for a PlayEfx is two undoable revisions.
        var action=new PreviewEditTarget("action",hop.Index,[]);
        es=await ee.SelectTargetAsync(action);
        Check(action.Equals(es.EditTarget) && es.Manifest!.EntryPosition==top && es.Attributes!.Any(a=>a.Type=="PlayEmitter"),"Selecting an Action lists its attributes");
        int play=es.Attributes!.Single(a=>a.Type=="PlayEmitter").Index;
        es=await ee.InspectAsync(es.Manifest!.DocumentId,es.Revision,play);
        var playFields=es.Inspection!.Fields;
        Console.WriteLine($"INSPECT PlayEmitter: {playFields.Length} fields; own: "+string.Join(",",playFields.Where(f=>!f.Path.StartsWith("efxrData",StringComparison.Ordinal)).Select(f=>f.Path+(f.Editable?"":"(ro)")+"="+f.Value)));
        var playNumber=playFields.FirstOrDefault(f=>f.Editable && f.Type is "System.Single" or "System.Int32" or "System.UInt32" &&
            !f.Path.StartsWith("efxrData",StringComparison.Ordinal) && !f.Path.Contains("Size",StringComparison.Ordinal));
        if(playNumber!=null)
        {
            string was=playNumber.Value,now=playNumber.Type=="System.Single"?(float.Parse(was,CultureInfo.InvariantCulture)+1).ToString("R",CultureInfo.InvariantCulture):(long.Parse(was,CultureInfo.InvariantCulture)+1).ToString(CultureInfo.InvariantCulture);
            long r0=es.Revision;
            es=await ee.ApplyFieldsAsync(new(es.Inspection!,[new(playNumber.Path,now)]));
            es=await ee.InspectAsync(es.Manifest!.DocumentId,es.Revision,play);
            Check(es.Revision==r0+1 && es.Inspection!.Fields.Single(f=>f.Path==playNumber.Path).Value==now,"An Action field edit did not stick");
            es=await ee.CommandAsync("undo");
        }
        var types=es.Attributes!.Select(a=>a.Type).ToArray();
        var catalogue=PreviewAttributeCatalog.Re4.For(null,types,action:true).ToDictionary(i=>i.Type);
        Check(catalogue["PlayEmitter"].Status==PreviewCatalogStatus.Present && catalogue["PlayEfx"].CanAdd && catalogue["PlayEfx"].Uses>0 &&
            !catalogue["Life"].CanAdd && catalogue["Life"].Status==PreviewCatalogStatus.EntryOnly &&
            PreviewAttributeCatalog.Re4.For("TypeBillboard3D",[]).Single(i=>i.Type=="PlayEfx").Status==PreviewCatalogStatus.ActionOnly,"Action catalogue");
        long r1=es.Revision;
        es=(await ee.SendRawAsync("remove_attribute",new(){["target"]=action.ToJson(play)},r1)).State;
        Check(es.Revision==r1+1 && action.Equals(es.EditTarget) && es.Attributes!.All(a=>a.Type!="PlayEmitter"),"Removing the PlayEmitter keeps the Action selected");
        var efxFields=new JsonArray(PreviewAttributeCatalog.Re4.Defaults("PlayEfx").Select(f=>(JsonNode?)new JsonObject{["path"]=f.Key,["value"]=f.Value}).ToArray());
        es=(await ee.SendRawAsync("add_attribute",new(){["target"]=action.ToJson(),["type"]="PlayEfx",["fields"]=efxFields},es.Revision)).State;
        Check(es.Revision==r1+2 && es.Attributes!.Any(a=>a.Type=="PlayEfx"),"Adding a PlayEfx to the Action");
        es=await ee.CommandAsync("undo");es=await ee.CommandAsync("undo");
        Check(es.Revision==r1+4 && es.Attributes!.Any(a=>a.Type=="PlayEmitter") && action.Equals(es.EditTarget),"Undo brings the PlayEmitter back");
        es=await ee.SelectTargetAsync(nested);
        Check(nested.Equals(es.EditTarget),"The embedded entry is reachable again after undo");
        Console.WriteLine($"PASS Action: {action.Label} select/inspect{(playNumber==null?"":"/set "+playNumber.Path)}, catalogue, PlayEmitter → PlayEfx and undo");

        // Entry operations inside the embedded file: add, duplicate, copy/paste and remove change only that file, the
        // selection carries the path, a path inside target is refused, the last embedded entry stays, and undo restores.
        {
            var path=new JsonArray(new JsonObject{["kind"]=hop.Kind,["index"]=hop.Index,["attributeIndex"]=hop.AttributeIndex});
            JsonArray Path()=>(JsonArray)path.DeepClone();
            async Task<(JsonNode Tree,JsonArray Embedded)> Tree()
            {
                var t=(await ee.SendRawAsync("status",new())).Reply["document"]!;
                return (t,(JsonArray)new PreviewEditTarget("entry",0,[hop]).Find(t)!.Parent!);
            }
            var (tree0,embedded0)=await Tree();
            int count0=embedded0.Count;string rootTree=tree0["entries"]!.ToJsonString();
            long r2=ee.State!.Revision;
            var reply=(await ee.SendRawAsync("add_entry",new(){["embeddedPath"]=Path(),["name"]="New_embedded",["attributes"]=new JsonArray(new JsonObject{["type"]="Life"})},r2)).Reply;
            Check(reply["selection"]?["index"]?.GetValue<int>()==count0 && reply["selection"]?["embeddedPath"]?.AsArray().Count==1,"Embedded add_entry reports the new entry with its path");
            var added=PreviewEditTarget.Parse("entry",count0,reply["selection"]!["embeddedPath"]);
            es=await ee.SelectTargetAsync(added);
            Check(added.Equals(es.EditTarget) && es.Attributes!.Any(a=>a.Type=="Life"),"The new embedded entry can be selected");
            reply=(await ee.SendRawAsync("duplicate_entry",new(){["embeddedPath"]=Path(),["index"]=0},ee.State!.Revision)).Reply;
            Check(reply["selection"]?["index"]?.GetValue<int>()==count0+1,"Embedded duplicate_entry reports the copy");
            await ee.SendRawAsync("copy_efx",new(){["target"]=new PreviewEditTarget("entry",1,[hop]).ToJson()});
            reply=(await ee.SendRawAsync("paste_efx",new(){["target"]=new JsonObject{["kind"]="document",["index"]=0,["embeddedPath"]=Path()},
                ["clipboardId"]=(await ee.SendRawAsync("status",new())).Reply["copyBuffer"]?["id"]?.GetValue<string>()},ee.State!.Revision)).Reply;
            Check(reply["selection"]?["index"]?.GetValue<int>()==count0+2 && reply["selection"]?["embeddedPath"]?.AsArray().Count==1,"Pasting an entry into the embedded file reports its path");
            var (tree1,embedded1)=await Tree();
            Check(embedded1.Count==count0+3 && tree1["entries"]!.ToJsonString()==rootTree,"Embedded entry operations change only the embedded file");
            await Reject(()=>ee.SendRawAsync("duplicate_entry",new(){["index"]=0,["target"]=new PreviewEditTarget("entry",0,[hop]).ToJson()},ee.State!.Revision),"embeddedPath inside target for an entry operation");
            // Embedded positions shift on removal and the session does not follow embedded entries by id, so the window
            // leaves an embedded target in the same file before removing (as here).
            es=await ee.SelectEntryAsync(top);
            long r3=ee.State!.Revision;
            es=(await ee.SendRawAsync("remove_entry",new(){["embeddedPath"]=Path(),["index"]=count0},r3)).State;
            Check(es.Revision==r3+1 && es.EditTarget!.IsRoot,"Embedded remove_entry is one revision");
            var (_,embedded2)=await Tree();
            Check(embedded2.Count==count0+2 && embedded2.All(e=>e!["name"]?.GetValue<string>()!="New_embedded"),"Embedded remove_entry removes that entry");
            for(int i=0;i<4;i++) es=await ee.CommandAsync("undo");
            var (tree3,embedded3)=await Tree();
            Check(es.Revision==r3+5 && embedded3.Count==count0 && tree3["entries"]!.ToJsonString()==rootTree,"Undo restores the embedded file");
            // The last entry of an embedded file stays (remove the PlayEmitter instead).
            long r4=ee.State!.Revision;
            for(int i=count0-1;i>0;i--) await ee.SendRawAsync("remove_entry",new(){["embeddedPath"]=Path(),["index"]=i},ee.State!.Revision);
            await Reject(()=>ee.SendRawAsync("remove_entry",new(){["embeddedPath"]=Path(),["index"]=0},ee.State!.Revision),"removing the last embedded entry");
            while(ee.State!.Revision<r4+2*(count0-1)) await ee.CommandAsync("undo");
            Console.WriteLine($"PASS embedded entry operations: add/select/duplicate/paste/remove in {nested.Label[..nested.Label.LastIndexOf('›')].Trim()}, root untouched, undo, last entry kept");
        }
    }
}
catch(Exception ex) {Console.Error.WriteLine(ex);Environment.ExitCode=1;}
