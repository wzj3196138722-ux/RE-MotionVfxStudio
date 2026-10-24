using System.Security.Cryptography;
using System.Text.Json;
using ReeLib;
using ReeLib.Clip;
using ReeLib.Mot;
using ReeLib.Motlist;
using VfxStudio.Core.Services;

if (args.Length != 2) throw new ArgumentException("Usage: MotAudit <assets-dir> <output-csv>");
string assets = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var manager = new MotionVfxSlotManager();
var seen = new HashSet<string>(StringComparer.Ordinal);
var rows = new List<string> { Csv("motlist", "motion", "clip", "track", "track name", "track start", "track end", "property", "property start", "property end", "key", "key frame", "key type", "value") };
var resolutionRows = new List<string> { Csv("motlist", "motion", "clip", "track", "source key", "start frame", "end frame", "effect id", "container", "element", "provider", "source object", "resource path", "resolved EFX", "status", "candidate providers") };
var weaponCandidateRows = new List<string> { Csv("weapon", "provider", "provider class", "source object", "element ID", "fixed ID", "activate ID", "raw resource", "local EFX", "status") };
var motionCandidateRows = new List<string> { Csv("motlist", "motion", "weapon", "direct event count", "weapon PFB resource candidates", "status") };
var dependencyEdges = new List<object>();
var providerCache = new Dictionary<string, (PfbEpvService Service, string[] EfxFiles)>(StringComparer.OrdinalIgnoreCase);
int files = 0, motions = 0, tracks = 0, keys = 0;
int verifiedChangedKeys = 0, verifiedClosingKeys = 0;
int verifiedContainerChanges = 0;
foreach (string path in Directory.EnumerateFiles(assets, "*.motlist.992", SearchOption.AllDirectories).OrderBy(p => p))
{
    string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    if (!seen.Add(hash)) continue;
    files++;
    try
    {
        var motlist = manager.LoadMotlist(path);
        string weaponCode = Path.GetFileName(path)[..4].ToLowerInvariant();
        if (!providerCache.TryGetValue(weaponCode, out var context))
        {
            string weaponDir = Path.Combine(assets, "WeaponBases", weaponCode);
            string[] efxFiles = Directory.Exists(weaponDir)
                ? Directory.GetFiles(weaponDir, "*.efx.5571972", SearchOption.AllDirectories)
                : Array.Empty<string>();
            var service = new PfbEpvService(assets);
            service.LoadWeaponPfbs(weaponCode, efxFiles);
            context = (service, efxFiles);
            providerCache[weaponCode] = context;
        }
        for (int m = 0; m < motlist.Motions.Count; m++)
        {
            if (motlist.Motions[m].MotFile is not MotFile mot) continue;
            var motionId = motlist.Motions[m].motNumber;
            motions++;
            if (weaponCode == "wp10" && motionId == 660)
            {
                int counter = 0;
                var events = manager.ExtractSlotsFromMot(mot, ref counter)
                    .Where(s => s.ClipIndex == 2 && s.TrackIndex == 8).OrderBy(s => s.StartFrame).ToArray();
                if (events.Length != 3 || events[0].StartFrame != 2 || events[1].StartFrame != 37 ||
                    events[2].StartFrame != 65 || events[0].ContainerId != 10 ||
                    events[1].ContainerId != 8 || events[2].ContainerId != 10)
                    throw new InvalidDataException("Motion 660 container keys were not sampled at event boundaries");
                verifiedContainerChanges++;
            }
            int slotCounter = 0;
            var motionSlots = manager.ExtractSlotsFromMot(mot, ref slotCounter);
            motionCandidateRows.Add(Csv(path, motionId, weaponCode, motionSlots.Count,
                context.Service.WeaponResourceCandidates.Select(c => c.LocalEfxPath ?? c.RawResourcePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "Weapon PFB resources indexed; action trigger unverified"));
            foreach (var slot in motionSlots)
            {
                var resolved = context.Service.ResolveSlotDetailed(slot, context.EfxFiles);
                var sources = resolved.Candidates.Count == 0 ? new PfbElementEntry?[] { null }
                    : resolved.Candidates.Cast<PfbElementEntry?>().ToArray();
                foreach (var source in sources)
                {
                    string[] paths = resolved.LocalEfxPaths.Count == 0 ? new[] { "" } : resolved.LocalEfxPaths.ToArray();
                    foreach (string efxPath in paths)
                    {
                        resolutionRows.Add(Csv(path, motionId, slot.ClipIndex, slot.TrackIndex, slot.SourceKeyIndex,
                            slot.StartFrame, slot.EndFrame, slot.EffectId, slot.ContainerId, slot.ElementId,
                            source?.SourcePfbPath, source?.ContainerGameObjectName,
                            source == null ? "" : string.Join('|', source.ResourceEfxPaths),
                            efxPath, resolved.Status, resolved.Candidates.Count));
                        dependencyEdges.Add(new { motlist = path, motion = motionId,
                            source = new { slot.ClipIndex, slot.TrackIndex, slot.SourceKeyIndex, slot.StartFrame, slot.EndFrame, slot.EffectId },
                            provider = source?.SourcePfbPath, sourceObject = source?.ContainerGameObjectName,
                            container = slot.ContainerId, element = slot.ElementId,
                            resources = source?.ResourceEfxPaths, efx = efxPath, status = resolved.Status });
                    }
                }
            }
            if (Path.GetFileName(Path.GetDirectoryName(path)) == "Samples" && motionId is 101 or 267)
            {
                int counter = 0;
                var extracted = manager.ExtractSlotsFromMot(mot, ref counter);
                if (motionId == 267)
                {
                    var events = extracted.Where(s => s.ClipIndex == 1 && s.TrackIndex == 5).ToArray();
                    if (events.Length != 2 || events[0].EffectId != "10-18" || events[1].EffectId != "10-6")
                        throw new InvalidDataException("Motion 267 track 5 changed EffectId keys were not preserved");
                    verifiedChangedKeys++;
                }
                else
                {
                    var events = extracted.Where(s => s.ClipIndex == 2 && s.TrackIndex == 8).ToArray();
                    if (events.Length != 1 || events[0].EffectId != "0-203")
                        throw new InvalidDataException("Motion 101 closing endpoint created a false event");
                    verifiedClosingKeys++;
                }
            }
            for (int c = 0; c < mot.Clips.Count; c++)
            {
                var clip = mot.Clips[c];
                if (clip.ClipEntry?.Tracks == null) continue;
                for (int t = 0; t < clip.ClipEntry.Tracks.Count; t++)
                {
                    var track = clip.ClipEntry.Tracks[t];
                    tracks++;
                    for (int p = 0; p < track.Properties.Count; p++)
                    {
                        var prop = track.Properties[p];
                        if (prop.Keys == null || prop.Keys.Count == 0)
                        {
                            rows.Add(Csv(path, motionId, c, t, track.Name, track.startFrame, track.endFrame,
                                prop.Info.FunctionName, prop.Info.startFrame, prop.Info.endFrame, "", "", "", ""));
                            continue;
                        }
                        for (int k = 0; k < prop.Keys.Count; k++)
                        {
                            var key = prop.Keys[k];
                            keys++;
                            object? value = key switch
                            {
                                NoHermiteKey noHermite => noHermite.Value,
                                BoolKey boolean => boolean.value,
                                _ => key.ToString()
                            };
                            rows.Add(Csv(path, motionId, c, t, track.Name, track.startFrame, track.endFrame,
                                prop.Info.FunctionName, prop.Info.startFrame, prop.Info.endFrame,
                                k, key.frame, key.GetType().Name, value));
                        }
                    }
                }
            }
        }
    }
    catch (Exception ex) { rows.Add(Csv(path, "ERROR", "", "", "", "", "", "", "", "", "", "", "", ex.Message)); }
}
foreach (var (weapon, context) in providerCache.OrderBy(pair => pair.Key))
    foreach (var candidate in context.Service.WeaponResourceCandidates)
        weaponCandidateRows.Add(Csv(weapon, candidate.Source.SourcePfbPath,
            candidate.Source.ProviderClassName, candidate.Source.ContainerGameObjectName,
            candidate.Source.ElementId, candidate.Source.ElementIdFixed, candidate.Source.ActivateId,
            candidate.RawResourcePath, candidate.LocalEfxPath,
            candidate.LocalEfxPath == null ? "missing or ambiguous local EFX" : "provider resource; action trigger unverified"));
for (int weaponIndex = 0; weaponIndex < 14; weaponIndex++)
{
    string code = $"wp{weaponIndex:00}";
    if (!providerCache.TryGetValue(code, out var context) || context.Service.WeaponResourceCandidates.Count == 0)
        throw new InvalidDataException($"No weapon PFB resources were indexed for {code}");
}
if (providerCache["wp03"].Service.WeaponResourceCandidates.Any(c =>
        c.RawResourcePath.EndsWith("11_it03_000.efx", StringComparison.OrdinalIgnoreCase) && c.LocalEfxPath == null))
    throw new InvalidDataException("wp03 exact PFB resource was lost due to unrelated local basename copy");
var wp02Candidates = providerCache["wp02"].Service.WeaponResourceCandidates
    .Where(c => c.LocalEfxPath != null)
    .Select(c => Path.GetFileName(c.LocalEfxPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i <= 4; i++)
    if (!wp02Candidates.Contains($"11_it02_{i:000}.efx.5571972"))
        throw new InvalidDataException($"wp02 weapon PFB resource 11_it02_{i:000} was not indexed");
File.WriteAllLines(output, rows);
string resolutionOutput = Path.Combine(Path.GetDirectoryName(output)!, "motion-efx-resolution.csv");
File.WriteAllLines(resolutionOutput, resolutionRows);
File.WriteAllLines(Path.Combine(Path.GetDirectoryName(output)!, "weapon-pfb-resource-candidates.csv"), weaponCandidateRows);
File.WriteAllLines(Path.Combine(Path.GetDirectoryName(output)!, "motion-weapon-candidate-summary.csv"), motionCandidateRows);
File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!, "motion-efx-dependency-graph.json"),
    JsonSerializer.Serialize(dependencyEdges, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"motlists={files} motions={motions} tracks={tracks} keys={keys} rows={rows.Count-1} call rows={resolutionRows.Count-1} weapon candidate rows={weaponCandidateRows.Count-1} action summaries={motionCandidateRows.Count-1} changed-key samples={verifiedChangedKeys} closing-key samples={verifiedClosingKeys} container-change samples={verifiedContainerChanges} output={output}");

static string Csv(params object?[] values) => string.Join(',', values.Select(value =>
    "\"" + (value?.ToString() ?? "").Replace("\"", "\"\"").Replace('\r', ' ').Replace('\n', ' ') + "\""));
