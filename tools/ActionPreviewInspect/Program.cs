using System.Collections;
using System.Text.Json;
using ReeLib;
using ReeLib.Clip;
using ReeLib.Mot;
using ReeLib.Common;

// Read-only inspection of a published preview. Never loads or changes game state.
if (args.Length != 3) throw new ArgumentException("Usage: ActionPreviewInspect <assets> <preview directory> <motion id>");
var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
object? Value(object? value) => value switch {
    RszInstance r => r.Fields.Select((f,i)=>(f.name, val:Value(r.Values[i]))).ToDictionary(x=>x.name,x=>x.val),
    string s => s,
    IEnumerable list => list.Cast<object?>().Select(Value).ToArray(),
    _ => value
};
foreach(var path in Directory.EnumerateFiles(args[1],"*.motlist.992")) {
    using var file = new MotlistFile(new FileHandler(new MemoryStream(File.ReadAllBytes(path)),path));
    if(!file.Read()) throw new Exception("MOTLIST read failed");
    var row=file.Motions.Single(m=>m.motNumber==int.Parse(args[2]));
    var motion=(MotFile)row.MotFile!;
    var nativeProperties=file.Motions.SelectMany(m=>(m.MotFile as MotFile)?.Clips ?? []).SelectMany(c=>c.ClipEntry.Properties).ToArray();
    var mismatches=nativeProperties.Where(p=>p.Info.wildsNameHash!=MurMur3HashUtils.GetAsciiHash(p.Info.FunctionName)).ToArray();
    Console.Error.WriteLine($"Native field hashes: {nativeProperties.Length} checked; {mismatches.Length} mismatches.");
    if(mismatches.Length>0) throw new Exception("Native field hash algorithm differs; inspect before writing.");
    object Clip(MotClip c) => new {c.category,c.TrackData,c.ClipEntry.FrameCount,c.ClipEntry.Header,c.ClipEntry.Guid,tracks=c.ClipEntry.Tracks.Select(t=>new {
        t.Name,t.startFrame,t.endFrame,t.nameHash1,t.nameUtf16Hash,properties=t.Properties.Select(p=>new {p.Info,
        keys=p.Keys?.Select(k=>new {k.frame,value=k is NoHermiteKey n?n.Value:k is BoolKey b?(object)b.value:null})})})};
    Console.Error.WriteLine("Native VFX clip metadata: " + JsonSerializer.Serialize(file.Motions.SelectMany(m=>(m.MotFile as MotFile)?.Clips ?? [])
        .Where(c=>c.ClipEntry.Tracks.Any(t=>t.Name=="via.effect.script.VFXRangeTrack"))
        .Take(3).Select(c=>new {c.category,c.TrackData,tracks=c.ClipEntry.Tracks.Select(t=>t.Name)}),options));
    Console.Error.WriteLine("Native VFX properties: " + JsonSerializer.Serialize(file.Motions.SelectMany(m=>(m.MotFile as MotFile)?.Clips ?? [])
        .SelectMany(c=>c.ClipEntry.Tracks).Where(t=>t.Name=="via.effect.script.VFXRangeTrack").SelectMany(t=>t.Properties)
        .GroupBy(p=>p.Info.FunctionName).Select(g=>new {name=g.Key,schemas=g.Select(p=>new {p.Info.DataType,p.Info.wildsNameHash,p.Info.nameUtf16Hash}).Distinct(),
            example=g.First().Keys?.Take(1).Select(k=>new {k.frame,value=k is NoHermiteKey n?n.Value:k is BoolKey b?(object)b.value:null})}),options));
    Console.WriteLine(JsonSerializer.Serialize(new {file=path,row.motNumber,motion.Header.frameCount,row.flags,
        overrides=row.OverrideClips.Select(Clip),original=motion.Clips.Select(Clip)},options));
}
var rsz=new RszFileOption(GameName.mhwilds,RszParser.GetInstance(Path.Combine(args[0],"Lists","rsz_pfb_classes.json")));
foreach(var path in Directory.EnumerateFiles(args[1],"*.pfb.18")) {
    using var file=new PfbFile(rsz,new FileHandler(new MemoryStream(File.ReadAllBytes(path)),path));
    if(!file.Read()) throw new Exception("PFB read failed");
    Console.WriteLine(JsonSerializer.Serialize(new {file=path, elements=file.RSZ.InstanceList
        .Where(r=>r.RszClass.name=="via.effect.script.EPVStandardData.Element").Select(r=>new {r.Index,fields=Value(r)})},options));
}
