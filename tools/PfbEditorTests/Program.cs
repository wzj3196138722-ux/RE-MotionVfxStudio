using System.Text.Json;
using System.Text.Json.Nodes;
using VfxStudio.LiveEditor;
using System.Text;
using ReeLib;

string root=Path.GetFullPath(args[0]);
string destination=Path.Combine(root,"tools/LiveEditor/test-output/pfb-editor-fixtures");
Directory.CreateDirectory(destination);
var options=new Options(Path.Combine(root,"Assets"),destination,destination,args[1]);
var reader=new GameInstallResourceReader(options);
if(args.Contains("--extract-representatives"))
{
    foreach(string path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(destination,"representative-paths.json")))!)
    {
        string output=Path.Combine(destination,Path.GetFileName(path)+".18");
        if(File.Exists(output))continue;
        var snapshot=reader.ReadPrefabResource(path);File.WriteAllBytes(output,snapshot.Content);
        File.WriteAllText(output+".source.json",snapshot.Provenance().ToJsonString());
        Console.WriteLine(Path.GetFileName(output)+" "+snapshot.Content.Length);
    }
    return;
}
foreach(string path in new[]{"gamedesign/equip/_prefab/weapon/wp02/00/0000/it0200_0000_0.pfb","gamedesign/equip/_prefab/armor/female/000/000/body/ch03_000_0002.pfb"})
{
    string output=Path.Combine(destination,Path.GetFileName(path)+".18");
    if(File.Exists(output)) continue;
    var snapshot=reader.ReadPrefabResource(path);
    File.WriteAllBytes(output,snapshot.Content);
    Console.WriteLine(output+" "+snapshot.Content.Length+" "+snapshot.ContentHash);
}

int checks=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
JsonObject Info(PfbWorkspace workspace)=>JsonSerializer.SerializeToNode(workspace.Info(),Host.Json)!.AsObject();
JsonObject Request(PfbWorkspace w,params (string Key,object Value)[] values)
{
    var result=new JsonObject{["pfbRevision"]=w.Revision};
    foreach(var pair in values)result[pair.Key]=JsonSerializer.SerializeToNode(pair.Value);
    return result;
}
JsonObject[] Slots(PfbWorkspace w)=>Info(w)["slots"]!.AsArray().Select(s=>s!.AsObject()).ToArray();
JsonObject[] Fields(JsonObject slot)=>slot["fields"]!.AsArray().Select(s=>s!.AsObject()).ToArray();
string Key(JsonObject slot)=>slot["key"]!.GetValue<string>();
void Reject(PfbWorkspace workspace,Action action,string label)
{
    byte[] before=workspace.ExportBytes();long revision=workspace.Revision;bool failed=false;
    try{action();}catch(ArgumentException){failed=true;}catch(InvalidOperationException){failed=true;}
    Check(failed&&revision==workspace.Revision&&before.SequenceEqual(workspace.ExportBytes()),label);
}
var clipboard=new PfbWorkspace.Clipboard();
var equipment=new PfbWorkspace(options,Path.Combine(destination,"it0200_0000_0.pfb.18"),clipboard:clipboard);
Check(Info(equipment)["type"]!.GetValue<string>()=="equipment","real dualblade PFB recognized from components");
Check(!Info(equipment)["dirty"]!.GetValue<bool>(),"opening PFB leaves original bytes untouched");
File.WriteAllText(Path.Combine(destination,"equipment-before.json"),Info(equipment).ToJsonString(new(){WriteIndented=true}));
foreach(var (kind,path) in new[]{("effectPfb","art/vfx/effectprovider/weapon/wp02/epvr_test.pfb"),("chain","art/model/test/test.chain2.13"),("clsp","art/model/test/test.clsp.3"),("jcns","art/model/test/test.jcns.20")})
{
    byte[] before=equipment.ExportBytes();
    equipment.Add(Request(equipment,("kind",kind),("resourcePath",path)));
    var slot=Slots(equipment).Last(s=>s["kind"]!.GetValue<string>()==kind);
    Check(slot["resourcePath"]!.GetValue<string>().Contains("test"),"add "+kind+" links actual resource field");
    Check(equipment.ExportBytes().Length>before.Length,"add "+kind+" rebuilds native PFB");
    equipment.Remove(Request(equipment,("slotKey",Key(slot))));
    string removedPath=slot["resourcePath"]!.GetValue<string>();
    Check(!Encoding.Unicode.GetString(equipment.ExportBytes()).Contains(removedPath),"delete "+kind+" removes unreferenced resource bytes");
    equipment.Reset(Request(equipment));
    Check(equipment.ExportBytes().SequenceEqual(File.ReadAllBytes(equipment.SourcePath)),"reset "+kind+" restores byte-exact baseline");
}
var armor=new PfbWorkspace(options,Path.Combine(destination,"ch03_000_0002.pfb.18"));
Check(Info(armor)["type"]!.GetValue<string>()=="equipment","real armor PFB recognized independently of filename");
foreach(var kind in new[]{"effectPfb","chain","clsp","jcns"})
{
    string ext=kind=="effectPfb"?"pfb":kind=="chain"?"chain2":kind;
    armor.Add(Request(armor,("kind",kind),("resourcePath","art/test/new."+ext)));
    Check(Slots(armor).Any(s=>s["kind"]!.GetValue<string>()==kind&&s["resourcePath"]!.GetValue<string>()=="art/test/new."+ext),"create missing armor component "+kind);
}
var chain=Slots(armor).First(s=>s["kind"]!.GetValue<string>()=="chain");
var enabled=Fields(chain).Single(f=>f["label"]!.GetValue<string>()=="Enabled");
armor.Set(Request(armor,("slotKey",Key(chain)),("fieldPath",enabled["path"]!.GetValue<string>()),("value",false)));
Check(!Fields(Slots(armor).First(s=>Key(s)==Key(chain))).Single(f=>f["label"]!.GetValue<string>()=="Enabled")["value"]!.GetValue<bool>(),"boolean edit roundtrips");
var blend=Fields(chain).Single(f=>f["label"]!.GetValue<string>()=="BlendRate");
armor.Set(Request(armor,("slotKey",Key(chain)),("fieldPath",blend["path"]!.GetValue<string>()),("value","0.75")));
Check(Fields(Slots(armor).First(s=>Key(s)==Key(chain))).Single(f=>f["label"]!.GetValue<string>()=="BlendRate")["value"]!.GetValue<float>()==0.75f,"numeric string uses invariant float conversion");
Reject(armor,()=>armor.Set(Request(armor,("slotKey",Key(chain)),("fieldPath",blend["path"]!.GetValue<string>()),("value","NaN"))),"nonfinite numeric edit rejects without mutation");
var stale=Request(armor);armor.Undo(Request(armor));Reject(armor,()=>armor.Redo(stale),"stale revision rejects without losing redo");armor.Redo(Request(armor));
Check(Fields(Slots(armor).First(s=>Key(s)==Key(chain))).Single(f=>f["label"]!.GetValue<string>()=="BlendRate")["value"]!.GetValue<float>()==0.75f,"undo/redo preserve typed values");
Reject(armor,()=>armor.Add(Request(armor,("kind","chain"),("resourcePath","C:/wrong.chain2"))),"reject local disk path as game resource");
Reject(armor,()=>armor.Add(Request(armor,("kind","chain"),("resourcePath","art/../bad.chain2"))),"reject resource traversal");
Reject(armor,()=>armor.Add(Request(armor,("kind","clsp"),("resourcePath","art/bad.tex"))),"reject wrong resource type");
var chainResource=Fields(chain).Single(f=>f["label"]!.GetValue<string>()=="ChainAsset");
Reject(armor,()=>armor.Set(Request(armor,("slotKey",Key(chain)),("fieldPath",chainResource["path"]!.GetValue<string>()),("value","art/test/bad.chain"))),"Chain2 component rejects legacy Chain resource");

string effectPath=Path.Combine(root,"tools/LiveEditor/test-output/action-character-audit/epvr_hm_cm.pfb.18");
var effect=new PfbWorkspace(options,effectPath,clipboard:clipboard);
Check(Info(effect)["type"]!.GetValue<string>()=="effect","real EPV PFB recognized as effect");
int initial=Slots(effect).Length;
var first=Slots(effect).First(s=>Fields(s).Any(f=>f["label"]!.GetValue<string>()=="LifeFrame"));
effect.Copy(Request(effect,("slotKey",Key(first))));
effect.Paste(Request(effect,("slotKey",Key(first))));
Check(Slots(effect).Length==initial+1,"effect slot deep copy adds one whole element");
var ids=Slots(effect).Where(s=>s["label"]!.GetValue<string>().StartsWith(first["label"]!.GetValue<string>().Split(" · ")[0]+" · ")).SelectMany(s=>Fields(s).Where(f=>f["label"]!.GetValue<string>()=="ID")).Select(f=>f["value"]!.ToJsonString()).ToArray();
Check(ids.Length==ids.Distinct().Count(),"pasted effect element gets unique ID");
var lifetime=Fields(first).Single(f=>f["label"]!.GetValue<string>()=="LifeFrame");
effect.Set(Request(effect,("slotKey",Key(first)),("fieldPath",lifetime["path"]!.GetValue<string>()),("value","123")));
Check(Fields(Slots(effect).First(s=>Key(s)==Key(first))).Single(f=>f["label"]!.GetValue<string>()=="LifeFrame")["value"]!.GetValue<float>()==123,"effect lifetime edit retained");
var offset=Fields(first).Single(f=>f["label"]!.GetValue<string>()=="Offset");
effect.Set(Request(effect,("slotKey",Key(first)),("fieldPath",offset["path"]!.GetValue<string>()),("value",new[]{1f,2f,3f})));
Check(Fields(Slots(effect).First(s=>Key(s)==Key(first))).Single(f=>f["label"]!.GetValue<string>()=="Offset")["value"]![2]!.GetValue<float>()==3,"vector edit roundtrips all components");
string guidPath=Fields(first).Single(f=>f["label"]!.GetValue<string>()=="GUID")["path"]!.GetValue<string>();
Reject(effect,()=>effect.Set(Request(effect,("slotKey",Key(first)),("fieldPath",guidPath),("value",Guid.NewGuid().ToString()))),"structural GUID is read-only");
var second=new PfbWorkspace(options,Path.Combine(root,"tools/LiveEditor/test-output/action-037-root-regression/epvr_it02.pfb.18"),clipboard:clipboard);
int secondCount=Slots(second).Length;second.Paste(Request(second));
Check(Slots(second).Length==secondCount+1,"clipboard deep copy remains usable across documents");
effect.Remove(Request(effect,("slotKey",Key(first))));
Check(Slots(effect).Length==initial,"removing effect slot rebuilds elements and counts");
effect.Reset(Request(effect));Check(effect.ExportBytes().SequenceEqual(File.ReadAllBytes(effectPath)),"effect reset restores exact source bytes");
Check(!equipment.ExportBytes().SequenceEqual(armor.ExportBytes()),"independent PFB documents do not share content");
byte[] corrupt=equipment.ExportBytes();int rsz=(int)BitConverter.ToInt64(corrupt,48),table=rsz+(int)BitConverter.ToInt64(corrupt,rsz+24);corrupt[table+12]^=1;
string invalid=Path.Combine(destination,"crc-mismatch.pfb.18");File.WriteAllBytes(invalid,corrupt);bool refused=false;try{_ =new PfbWorkspace(options,invalid);}catch(InvalidDataException){refused=true;}
Check(refused,"CRC mismatch rejects before parsing instance data");

var format=new RszFileOption(GameName.mhwilds,RszParser.GetInstance(Path.Combine(root,"Assets/Lists/rsz_pfb_classes.json")));
string referenceFile=Path.Combine(destination,"object-reference.pfb.18");
using(var file=new PfbFile(format,new FileHandler(new MemoryStream(equipment.ExportBytes()),"fixture.pfb.18")))
{
    file.Read();file.SetupGameObjects();
    int managerIndex=file.RSZ.ObjectList.FindIndex(v=>v.RszClass.name=="via.effect.script.ObjectEffectManager2");
    Check(managerIndex>0,"real fixture includes manager after chain component");
    file.GameObjectRefInfoList.Add(new ReeLib.Pfb.PfbGameObjectRefInfo{objectId=managerIndex,targetId=0,propertyId=12345,arrayIndex=-1});
    using var stream=new MemoryStream();using var writer=new FileHandler(stream,"reference.pfb.18");file.WriteTo(writer,false);File.WriteAllBytes(referenceFile,stream.ToArray());
}
var references=new PfbWorkspace(options,referenceFile);var referenceChain=Slots(references).First(s=>s["kind"]!.GetValue<string>()=="chain");
references.Remove(Request(references,("slotKey",Key(referenceChain))));
using(var rebuilt=new PfbFile(format,new FileHandler(new MemoryStream(references.ExportBytes()),"rebuilt.pfb.18")))
{
    rebuilt.Read();var link=rebuilt.GameObjectRefInfoList.Single(r=>r.propertyId==12345);
    Check(rebuilt.RSZ.ObjectList[link.objectId].RszClass.name=="via.effect.script.ObjectEffectManager2"&&link.targetId==0,"component deletion remaps object references instead of retaining stale indices");
}
File.WriteAllText(Path.Combine(destination,"equipment-edited.json"),Info(armor).ToJsonString(new(){WriteIndented=true}));
File.WriteAllBytes(Path.Combine(destination,"equipment-edited.pfb.18"),armor.ExportBytes());
JsonObject[] Components(PfbWorkspace w)=>Info(w)["previewMetadata"]!["equipmentComponents"]!.AsArray().Select(c=>c!.AsObject()).ToArray();
var verification=Components(armor);
Check(verification.Any(c=>c["className"]!.GetValue<string>()=="via.motion.Chain2"&&c["resources"]![0]!["path"]!.GetValue<string>()=="art/test/new.chain2"),"equipment metadata includes actual Chain2 component resource");
Check(verification.Where(c=>c["className"]!.GetValue<string>()=="via.motion.JointConstraints").Any(c=>c["resources"]!.AsArray().Any(r=>r!["fieldName"]!.GetValue<string>()=="Layer"&&r["path"]!.GetValue<string>()=="art/test/new.jcns")),"equipment metadata includes JCNS layer indices and paths");
Check(verification.Where(c=>c["className"]!.GetValue<string>()=="via.character.CollisionShapePreset").Any(c=>c["resources"]!.AsArray().Any(r=>r!["path"]!.GetValue<string>()=="art/test/new.clsp")),"equipment metadata includes CLSP component resources");
var managerMetadata=verification.Single(c=>c["className"]!.GetValue<string>()=="via.effect.script.ObjectEffectManager2");
Check(managerMetadata["resources"]!.AsArray().Any(r=>r!["fieldName"]!.GetValue<string>()=="ExternalDataContainers"&&r["path"]!.GetValue<string>()=="art/test/new.pfb"&&r["dataContainerId"]!.GetValue<int>()==1),"equipment metadata retains external PFB path and DataContainerId");
Check(managerMetadata["resources"]!.AsArray().Any(r=>r!["fieldName"]!.GetValue<string>()=="DataContainer"&&r["path"]!.GetValue<string>()==""),"equipment metadata retains empty primary prefab reference");
Check(verification.All(c=>c["gameObjectNames"]!.AsArray().Count>0&&c["gameObjectIndex"]!.GetValue<int>()>=0&&c["componentIndex"]!.GetValue<int>()>=0&&c["classIndex"]!.GetValue<int>()>=0),"equipment component locators include hierarchy and class ordinal");
Check(Components(effect).Length==0,"effect PFB does not pretend to be equipment");
var duplicateComponent=new PfbWorkspace(options,equipment.SourcePath);
duplicateComponent.Add(Request(duplicateComponent,("kind","chain"),("resourcePath","art/test/second.chain2")));
Check(Components(duplicateComponent).Where(c=>c["className"]!.GetValue<string>()=="via.motion.Chain2").Select(c=>c["classIndex"]!.GetValue<int>()).SequenceEqual(new[]{0,1}),"duplicate chain components receive separate class ordinals");
var deleteDuplicate=Slots(duplicateComponent).Single(s=>s["resourcePath"]!.GetValue<string>()=="art/test/second.chain2");
duplicateComponent.Remove(Request(duplicateComponent,("slotKey",Key(deleteDuplicate))));
Check(Components(duplicateComponent).Where(c=>c["className"]!.GetValue<string>()=="via.motion.Chain2").Count()==1,"deleted component disappears from preview metadata");
Console.WriteLine($"PFB editor: {checks} checks passed; real weapon, armor and two EPV files.");
if(File.Exists(Path.Combine(destination,"representative-paths.json")))
{
    foreach(string path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(destination,"representative-paths.json")))!)
    {
        string sample=Path.Combine(destination,Path.GetFileName(path)+".18");
        if(!File.Exists(sample))continue;
        var representative=new PfbWorkspace(options,sample);
        Check(Info(representative)["type"]!.GetValue<string>()=="equipment","representative equipment "+Path.GetFileName(sample));
        representative.Add(Request(representative,("kind","jcns"),("resourcePath","art/test/verified.jcns")));
        var last=Slots(representative).Single(s=>s["kind"]!.GetValue<string>()=="jcns"&&s["resourcePath"]!.GetValue<string>()=="art/test/verified.jcns");
        representative.Remove(Request(representative,("slotKey",Key(last))));
        Check(!Encoding.Unicode.GetString(representative.ExportBytes()).Contains("art/test/verified.jcns"),"representative rebuilt without stale JCNS bytes "+Path.GetFileName(sample));
    }
}
Console.WriteLine($"Total PFB checks: {checks}");

namespace VfxStudio.LiveEditor
{
    internal sealed record Options(string Assets,string Mailbox,string Output,string? GameRoot);
    internal sealed record CacheSourceDescriptor(string ActualPath);
    internal sealed class CacheSourceException(string code,string message):InvalidOperationException(message) { public string Code=>code; }
    internal static class ResourceResolver {internal static string Normalize(string value)=>value;}
    internal static class Host {internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};}
}
