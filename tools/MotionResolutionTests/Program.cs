using ReeLib;
using ReeLib.Clip;
using ReeLib.Mot;
using VfxStudio.Core.Models;
using VfxStudio.Core.Services;

string assets = Path.GetFullPath(args.Length > 0 ? args[0] : "Assets");
var manager = new MotionVfxSlotManager();
var motions = manager.LoadMotlist(Path.Combine(assets, "WeaponBases", "wp10", "wp10_00.motlist.992"));
var motion = (MotFile)motions.Motions.Single(m => m.motNumber == 660).MotFile!;
var track = motion.Clips[2].ClipEntry.Tracks[8];
foreach (var property in track.Properties)
    foreach (var key in property.Keys ?? new())
        Console.WriteLine($"{property.Info.FunctionName} frame={key.frame} interpolation={(key is NoHermiteKey n ? n.interpolation.ToString() : "bool")} value={(key is NoHermiteKey value ? value.Value : null)}");
var interpolationCounts = motions.Motions.Where(m => m.MotFile is MotFile)
    .SelectMany(m => ((MotFile)m.MotFile!).Clips).SelectMany(c => c.ClipEntry.Tracks)
    .Where(t => t.Name == "via.effect.script.VFXRangeTrack")
    .SelectMany(t => t.Properties.Where(p => p.Info.FunctionName == "EffectId"))
    .SelectMany(p => p.Keys ?? new()).OfType<NoHermiteKey>()
    .GroupBy(k => k.interpolation).Select(g => $"{g.Key}={g.Count()}");
Console.WriteLine("wp10 EffectId interpolation: " + string.Join(", ", interpolationCounts));
foreach (var sample in motions.Motions.Where(m => m.MotFile is MotFile)
    .SelectMany(m => ((MotFile)m.MotFile!).Clips.SelectMany(c => c.ClipEntry.Tracks)
        .Select(t => (m.motNumber, Track: t)))
    .Where(s => s.Track.Properties.Any(p => p.Info.FunctionName == "EffectId" &&
        p.Keys?.OfType<NoHermiteKey>().Any(k => k.interpolation == InterpolationType.Event) == true)).Take(3))
{
    Console.WriteLine($"Event sample motion={sample.motNumber} track={sample.Track.Name}");
    foreach (var property in sample.Track.Properties)
        foreach (var key in property.Keys ?? new())
            Console.WriteLine($"{property.Info.FunctionName} {property.Info.startFrame}-{property.Info.endFrame} frame={key.frame} interpolation={(key is NoHermiteKey n ? n.interpolation.ToString() : "bool")} value={(key is NoHermiteKey value ? value.Value : null)}");
}
