using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VfxStudio.LiveEditor;

string root=Path.GetFullPath(args[0]),game=Path.GetFullPath(args[1]);
string output=Path.Combine(root,"tools/LiveEditor/test-output/pfb-load-080");Directory.CreateDirectory(output);
var options=new Options(Path.Combine(root,"Assets"),output,output,game);
var reader=new GameInstallResourceReader(options);
string privateRoot="art/MotionVfxLive/Validation/Pfb080/"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..8];
var manifest=new JsonObject{["privateRoot"]=privateRoot,["caseCount"]=7,["instantiationAllowed"]=false,["cases"]=new JsonArray(),["dependencies"]=new JsonArray()};
JsonObject Info(PfbWorkspace w)=>JsonSerializer.SerializeToNode(w.Info(),Host.Json)!.AsObject();
JsonObject Request(PfbWorkspace w,params (string Key,object Value)[] fields)
{var r=new JsonObject{["pfbRevision"]=w.Revision};foreach(var f in fields)r[f.Key]=JsonSerializer.SerializeToNode(f.Value);return r;}
void Save(string name,PfbWorkspace w,string original,string description)
{
    byte[] bytes=w.ExportBytes();string path=privateRoot+"/"+name+".pfb";
    string target=Path.Combine(output,"natives","stm",path.Replace('/',Path.DirectorySeparatorChar)+".18");
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.WriteAllBytes(target,bytes);
    var reopened=new PfbWorkspace(options,target);var info=Info(reopened);
    if(info["type"]!.GetValue<string>()!=Info(w)["type"]!.GetValue<string>() || info["slots"]!.AsArray().Count!=Info(w)["slots"]!.AsArray().Count)
        throw new InvalidDataException("Saved fixture readback differs: "+name);
    manifest["cases"]!.AsArray().Add(new JsonObject{["name"]=name,["path"]=path,["sourcePath"]=original,["description"]=description,
        ["bytes"]=bytes.Length,["sha256"]=Convert.ToHexString(SHA256.HashData(bytes)),["type"]=info["type"]!.DeepClone(),["slotCount"]=info["slots"]!.AsArray().Count});
}
string effectPath="art/vfx/effectprovider/weapon/it02/epvr_it02.pfb";
var effect=new PfbWorkspace(options,effectPath,"game_files");
Save("effect_original",effect,effectPath,"Original installed PFB control");
string first=Info(effect)["slots"]![0]!["key"]!.GetValue<string>();
effect.Copy(Request(effect,("slotKey",first)));effect.Paste(Request(effect,("slotKey",first)));
Save("effect_copy",effect,effectPath,"One complete EPV element copied and pasted through PfbWorkspace");
string equipmentPath="gamedesign/equip/_prefab/armor/female/001/000/body/ch03_001_0002.pfb";
var equipment=new PfbWorkspace(options,equipmentPath,"game_files");
Save("equipment_original",equipment,equipmentPath,"Original installed equipment PFB control");
var initial=Info(equipment);
var resourceFields=initial["previewMetadata"]!["equipmentComponents"]!.AsArray().SelectMany(c=>c!["resources"]!.AsArray()).ToArray();
string Existing(string extension)=>resourceFields.Select(r=>r!["path"]!.GetValue<string>()).First(p=>p.EndsWith(extension,StringComparison.OrdinalIgnoreCase)).TrimStart('@');
var resources=new[]{("effectPfb","art/vfx/effectprovider/weapon/it02/epvr_it02_wep_L.pfb"),("chain",Existing(".chain2")),("clsp",Existing(".clsp")),("jcns",Existing(".jcns"))};
var fileList=File.ReadLines(Path.Combine(root,"Assets/Lists/MHWS_STM.list")).ToArray();
foreach(var(kind,path)in resources)
{
    var verified=new JsonArray();
    if(kind=="effectPfb")verified.Add(reader.ReadPrefabResource(path).Provenance());
    else
    {
        string prefix="natives/stm/"+path.ToLowerInvariant()+".";
        string[] names=fileList.Where(p=>p.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(names.Length==0)throw new InvalidDataException("Dependency is absent from the native file list: "+path);
        // This utility invokes the existing exact installed-file reader; it
        // never changes production visibility or substitutes guessed files.
        var method=typeof(GameInstallResourceReader).GetMethod("ReadNative",BindingFlags.Instance|BindingFlags.NonPublic)!;
        foreach(string native in names)
        {
            try{verified.Add(((GameFileSnapshot)method.Invoke(reader,[native,null])!).Provenance());}
            catch(TargetInvocationException ex)when(ex.InnerException is CacheSourceException {Code:"game_resource_not_found"}){}
        }
        if(verified.Count==0)throw new InvalidDataException("No installed version of the actual dependency was found: "+path);
    }
    manifest["dependencies"]!.AsArray().Add(new JsonObject{["kind"]=kind,["path"]=path,["installedVersions"]=verified});
    equipment.Add(Request(equipment,("kind",kind),("resourcePath",path)));
    if(!Info(equipment)["slots"]!.AsArray().Any(s=>s!["kind"]!.GetValue<string>()==kind&&string.Equals(s["resourcePath"]!.GetValue<string>(),path,StringComparison.OrdinalIgnoreCase)))
        throw new InvalidDataException("Added dependency was not serialized: "+kind);
    Save("equipment_add_"+kind.ToLowerInvariant(),equipment,equipmentPath,"Cumulative Add of verified existing "+kind+" resource; never instantiated");
}
File.WriteAllText(Path.Combine(output,"pfb-load-manifest.json"),manifest.ToJsonString(new(){WriteIndented=true}));
Console.WriteLine("Prepared 7 standalone load fixtures: "+output);
Console.WriteLine("Private game prefix: "+privateRoot);

namespace VfxStudio.LiveEditor
{
    internal sealed record Options(string Assets,string Mailbox,string Output,string? GameRoot);
    internal sealed record CacheSourceDescriptor(string ActualPath);
    internal sealed class CacheSourceException(string code,string message):InvalidOperationException(message){public string Code=>code;}
    internal static class ResourceResolver{internal static string Normalize(string value)=>value;}
    internal static class Host{internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};}
}
