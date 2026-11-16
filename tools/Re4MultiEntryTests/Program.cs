// Multi-entry native preview: a whole EFX file in one worker.
// Usage: dotnet run -c Release --project tools/Re4MultiEntryTests [-- --corpus]
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VfxStudio.Re4Render;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
string exe = Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
string game = Path.GetDirectoryName(exe)!;
string repo = FindRepository();
string script = Path.Combine(repo, "tools", "Re4Native", "re4host.py");
string stm = Path.Combine(game, "RE4_EXTRACT", "natives", "stm");
using var resources = new Re4ResourceSource(game, Path.Combine(game, "RE4_EXTRACT"));
int checks = 0, failures = 0;
var results = new Dictionary<string, object>();
void Check(bool ok, string name, object? detail = null)
{
    checks++;
    Console.WriteLine((ok ? "ok   " : "FAIL ") + name + (detail == null ? "" : " :: " + detail));
    if (!ok) failures++;
}
static string Digest(ReadOnlyMemory<byte> data) => Convert.ToHexString(SHA256.HashData(data.Span))[..16];
static bool Finite(PreviewFrame frame) => frame.Particles.All(p => float.IsFinite(p.Position.X) && float.IsFinite(p.Position.Y) && float.IsFinite(p.Position.Z));

var bb2 = PreviewDocument.Fixture(game, "bb2");
using var client = new NativePreviewClient("python", script, exe);

// 1. The whole sm82_320_00 file.
var excluded = new Dictionary<int, string>();
var watch = Stopwatch.StartNew();
var scene = await PreviewSceneLoader.LoadAsync(client, bb2.Path, resources, excluded);
double firstLoad = watch.Elapsed.TotalMilliseconds;
// Same handling as the window: an entry failing while advancing is excluded and the file reloads.
for (int tick = 1; tick <= 600; tick++)
{
    try { using var warm = await client.FrameAsync(tick); }
    catch (Exception error) when (PreviewSceneLoader.ExcludeFailedEntry(scene, error, excluded, tick) != null)
    { scene = await PreviewSceneLoader.LoadAsync(client, bb2.Path, resources, excluded); tick = 0; }
}
// 25 Billboard3D + 2 RibbonFollow entries are drawable; entry 20 faults in the emulator (47ce9e4) when
// loading, and any entry failing while advancing is excluded as above.
Check(scene.Entries.Count == 31 && scene.Shown.Count + excluded.Count == 27 && scene.Shown.Count >= 24 && excluded.Values.All(v => v.StartsWith("原生模拟器")),
    $"sm82_320_00: {scene.Shown.Count} of 31 entries are simulated, native exclusions {excluded.Count}",
    string.Join("; ", scene.Entries.Where(e => !e.Shown).Select(e => $"[{e.Position}] {e.Type}: {e.Excluded}")));
int table13 = scene.TableOf(13);
var sim = new List<double>(); int maxAlive = 0; bool finite = true, tableOk = true;
var perEntryPeak = new int[scene.Shown.Count];
for (int tick = 1; tick <= 600; tick++)
{
    using var frame = await client.FrameAsync(tick);
    sim.Add(frame.SimulationMs);
    maxAlive = Math.Max(maxAlive, frame.Particles.Count);
    finite &= Finite(frame);
    var states = frame.EntryStates!;
    tableOk &= states.Count == scene.Shown.Count && states.Select((s, i) => s.Index == scene.Shown[i].Index).All(x => x) &&
        states.Sum(s => s.Count) == frame.Particles.Count && states.All(s => frame.Particles.Skip(s.First).Take(s.Count).All(p => p.Entry == s.Index));
    for (int i = 0; i < states.Count; i++) perEntryPeak[i] = Math.Max(perEntryPeak[i], states[i].Count);
}
Check(tableOk, "every frame's entry table follows the shown order and particle ranges match their entry");
Check(finite, "600 ticks without non-finite particle positions");
Check(perEntryPeak.Count(n => n > 0) >= scene.Shown.Count - 2, "most entries emit particles within 10 s", string.Join(",", perEntryPeak));
sim.Sort();
Console.WriteLine($"  first load {firstLoad:F0} ms (native {client.LoadMs:F0} ms), snapshot {client.SnapshotBytes / 1048576.0:F1} MB, per-tick sim median {sim[sim.Count / 2]:F2} ms p95 {sim[(int)(sim.Count * .95)]:F2} ms, peak alive {maxAlive}");
results["sm82_320"] = new { firstLoadMs = firstLoad, nativeLoadMs = client.LoadMs, snapshotBytes = client.SnapshotBytes, simMedianMs = sim[sim.Count / 2], simP95Ms = sim[(int)(sim.Count * .95)], maxAlive, perEntryPeak };

// 2. Seek back, reload in the same worker: identical arrays. Also time the replay cost to late ticks.
string[] Digests(PreviewFrame f) => f.Arrays.Select(a => Digest(a)).ToArray();
string[] at300;
using (var f = await client.FrameAsync(300)) at300 = Digests(f);
watch.Restart();
await client.LoadAsync(scene.LoadCommand());
double reload = watch.Elapsed.TotalMilliseconds;
string[] again; double replay300;
watch.Restart();
using (var f = await client.FrameAsync(300)) { again = Digests(f); replay300 = watch.Elapsed.TotalMilliseconds; }
Check(client.LastLoadReused && at300.SequenceEqual(again), "same-worker reload replays tick 300 to identical arrays for every entry");
watch.Restart();
using (var f = await client.FrameAsync(1800)) { }
double replay1800 = watch.Elapsed.TotalMilliseconds;
Console.WriteLine($"  same-worker reload {reload:F0} ms (native {client.LoadMs:F0} ms); replay to tick 300 {replay300:F0} ms, then on to 1800 {replay1800:F0} ms");
results["reload"] = new { reloadMs = reload, nativeLoadMs = client.LoadMs, replay300Ms = replay300, replay300To1800Ms = replay1800 };

// 3. The bb2 entry inside the file equals a single-entry load (FixRandomGenerator keeps it independent).
string multi13; using (var f = await client.FrameAsync(45)) multi13 = Digest(f.Arrays[table13]);
var single = PreviewDocument.Read(bb2.Path, bb2.Offset, resources);
await client.LoadAsync(single.LoadCommand());
string single13; using (var f = await client.FrameAsync(45)) single13 = Digest(f.Arrays[0]);
Check(table13 >= 0 && multi13 == single13, "entry 13 simulated with the others matches its single-entry load at tick 45", $"{multi13} vs {single13}");

// 4. An entry the emulator cannot run is excluded, and the rest of the file still loads.
string muzzle = Path.Combine(stm, @"_anotherorder\vfx\effecteditor\efd_event\cs\efd_cse010_s00\efd_0015_cse010_s00_muzzleflash_0000.efx.3539837");
var muzzleExcluded = new Dictionary<int, string>();
watch.Restart();
var muzzleScene = await PreviewSceneLoader.LoadAsync(client, muzzle, resources, muzzleExcluded);
Check(muzzleExcluded.ContainsKey(4) && muzzleExcluded[4].Contains("access violation") && muzzleScene.Shown.Count > 0 && muzzleScene.TableOf(4) < 0,
    "a natively failing entry is excluded and the other entries load", $"{watch.Elapsed.TotalSeconds:F1} s; " + string.Join("; ", muzzleExcluded.Select(p => $"[{p.Key}] {p.Value}")));
using (var f = await client.FrameAsync(60)) Check(f.EntryStates!.Count == muzzleScene.Shown.Count, "the remaining entries run", $"{muzzleScene.Shown.Count} shown, alive {f.Particles.Count}");

// 5. RibbonFollow: engine point pools exported per frame.
{
    string explosion = Path.Combine(stm, @"_chainsaw\vfx\effecteditor\efd_weapon\efd_wp5837\efd_0015_wp5837_explosion_0000.efx.3539837");
    var ex = new Dictionary<int, string>();
    var s = await PreviewSceneLoader.LoadAsync(client, explosion, resources, ex);
    int table = s.TableOf(5);
    Check(table >= 0 && s.Shown[table].Document!.Kind == PreviewTypeKind.RibbonFollow && s.Shown[table].Document!.RibbonPointsPerParticle == 13,
        "explosion entry 5 is a RibbonFollow with (5-1)*2+5 = 13 points per particle");
    var r = new RibbonCheck();
    var previous = new Dictionary<int, System.Numerics.Vector3>();
    for (int tick = 1; tick <= 40; tick++)
    {
        using var f = await client.FrameAsync(tick);
        previous = r.Frame(f, table, previous);
    }
    bool shape = r.Shape, pointsFinite = r.Finite, monotonic = r.Monotonic, follows = r.Follows; int strips = r.Strips, full = r.Full; float worstHead = r.WorstHead;
    Check(shape && strips > 50 && full > 0, "ribbon pool layout and valid point counts", $"{strips} strips, {full} full");
    Check(pointsFinite && monotonic, "ribbon points are finite and V runs from head to tail in [0, 1]");
    Check(follows, "each ribbon head stays within one tick of its particle's world position (P applied)", $"worst head offset / step {worstHead:F2}");
}

// 5b. RibbonLength: the engine lays out ShapeDivision points per particle in the same pool format.
{
    string spark = Path.Combine(stm, "re_engine_library", "vfx_library", "effecteditor", "efd_spark", "efd_capcom_spark_loop00_l_0000.efx.3539837");
    var s = await PreviewSceneLoader.LoadAsync(client, spark, resources, new Dictionary<int, string>());
    var tables = Enumerable.Range(0, s.Shown.Count).Where(t => s.Shown[t].Document!.Kind == PreviewTypeKind.RibbonLength).ToArray();
    Check(tables.Length == 4 && tables.All(t => s.Shown[t].Document!.RibbonPointsPerParticle == 2), "spark file: 4 RibbonLength entries with 2 points per particle");
    var check = new LengthCheck();
    for (int tick = 2; tick <= 12; tick += 2)
    {
        using var f = await client.FrameAsync(tick);
        foreach (int t in tables) check.Frame(f, t);
    }
    Check(check.Strips > 0 && check.Ok, "RibbonLength strips: 2 valid points, tail V 1 and head V 0, finite, non-zero length", $"{check.Strips} strips; {check.Problem}");
}

// 6. Node graph (structure only, no native worker).
{
    static bool PinsResolve(PreviewEffectGraph g)
    {
        var inputs = g.Nodes.Where(n => n.InputPin != null).ToDictionary(n => n.InputPin!.Value, n => n.Id);
        var outputs = g.Nodes.SelectMany(n => n.Rows.Where(r => r.OutputPin != null).Select(r => (Pin: r.OutputPin!.Value, Node: n.Id))).ToDictionary(p => p.Pin, p => p.Node);
        bool unique = g.Nodes.Select(n => n.Id).Distinct().Count() == g.Nodes.Count && g.Links.Select(l => l.Id).Distinct().Count() == g.Links.Count &&
            inputs.Keys.Concat(outputs.Keys).Distinct().Count() == inputs.Count + outputs.Count;
        return unique && g.Links.All(l => outputs.TryGetValue(l.FromPin, out int from) && from == l.FromNode && inputs.TryGetValue(l.ToPin, out int to) && to == l.ToNode);
    }
    string gimmick = Path.Combine(stm, @"_anotherorder\vfx\effecteditor\efd_character\efd_ao_chd7\efd_0015_ao_chd7_gimmick_mot_0011.efx.3539837");
    var g1 = PreviewEffectGraph.Read(gimmick);
    var emitters = g1.Nodes.Where(n => n.Kind == PreviewGraphNodeKind.Emitter).ToArray();
    var actions = g1.Nodes.Where(n => n.Kind == PreviewGraphNodeKind.Action).ToArray();
    var triggerLinks = g1.Links.Where(l => g1.Find(l.ToNode)?.Kind == PreviewGraphNodeKind.Action).ToArray();
    Check(emitters.Length == 20 && actions.Length == 3 && g1.Links.Count(l => l.FromNode == PreviewEffectGraph.RootId) == 20 &&
        triggerLinks.Length == 3 && PinsResolve(g1) && g1.Warnings.Count == 0,
        "graph: gimmick_mot_0011 has Root → 20 emitters, 3 Actions, 3 trigger links, every link pin resolves",
        string.Join("; ", triggerLinks.Select(l => $"{g1.Find(l.FromNode)!.Title} → {g1.Find(l.ToNode)!.Title} ({l.Label})")));
    var layout = g1.Layout();
    var collapsedLayout = g1.Layout(n => n.Rows.Count(r => r.OutputPin != null));
    Check(layout.Count == g1.Nodes.Count && layout.Select(p => p.Node).SequenceEqual(g1.Nodes.Select(n => n.Id)) &&
        collapsedLayout.Max(p => p.Row) < layout.Max(p => p.Row),
        "graph layout places every node once; the collapsed layout is shorter");

    string rage = Path.Combine(stm, @"_anotherorder\vfx\effecteditor\efd_character\efd_ao_chd7\efd_0015_ao_chd7_rage_0000.efx.3539837");
    var g2 = PreviewEffectGraph.Read(rage);
    var embedded = g2.Nodes.Where(n => n.Kind == PreviewGraphNodeKind.EmbeddedEmitter).GroupBy(n => n.ActionIndex).ToDictionary(x => x.Key!.Value, x => x.Count());
    Check(embedded.GetValueOrDefault(0) == 4 && embedded.GetValueOrDefault(1) == 8 && PinsResolve(g2),
        "graph: rage_0000 PlayEmitter nodes link to their 4 and 8 embedded emitters");

    // Every official file builds. No trigger points past its Action list (the two "-1" indices mean "never").
    int files = 0, warnings = 0, broken = 0; string? firstBroken = null;
    foreach (var path in Directory.EnumerateFiles(stm, "*.efx.3539837", SearchOption.AllDirectories))
    {
        ReeLib.EfxFile file;
        try
        {
            using var stream = File.OpenRead(path);
            file = new ReeLib.EfxFile(new ReeLib.FileHandler(stream, path) { FileVersion = (int)ReeLib.Efx.EfxVersion.RE4 });
            if (!file.Read()) continue;
        }
        catch (Exception) { continue; }   // files the EFX reader cannot read are not the graph's concern
        try { var g = PreviewEffectGraph.Build(file, Path.GetFileName(path)); files++; warnings += g.Warnings.Count; if (!PinsResolve(g)) { broken++; firstBroken ??= path; } }
        catch (Exception e) { broken++; firstBroken ??= path + ": " + e.GetBaseException().Message; }
    }
    Check(files > 3900 && broken == 0 && warnings == 0, $"graph builds for {files} official files with consistent pins", $"warnings {warnings}, broken {broken} {firstBroken}");
}

// 7. FrameBlend: the blend weight (+0xe4, acc/T) stays in [0, 1] and moves, and the previous frame is
// frame - 1 (wrapping) for forward playback. The emulator has no .uvs resource, so the engine's UV rects
// at +0xc4 / +0xd4 stay zero and cannot be compared here.
{
    var blendScene = await PreviewSceneLoader.LoadAsync(client, bb2.Path, resources, new Dictionary<int, string>(excluded));
    var tables = Enumerable.Range(0, blendScene.Shown.Count).Where(t => blendScene.Shown[t].Document!.FrameBlend).ToArray();
    int samples = 0, prevMismatch = 0; float minW = 1, maxW = 0; string? firstBad = null;
    for (int tick = 1; tick <= 120 && tables.Length > 0; tick++)
    {
        using var f = await client.FrameAsync(tick);
        foreach (int t in tables)
        {
            var doc = blendScene.Shown[t].Document!; var st = f.EntryStates![t];
            for (int i = st.First; i < st.First + st.Count; i++)
            {
                var p = f.Particles[i];
                var seq = doc.Sequences[Math.Min((int)((p.Flags >> 10) & 255), doc.Sequences.Count - 1)];
                int frameIndex = Math.Min((int)((p.Flags >> 18) & 255), seq.Length - 1);
                uint word = BillboardRenderer.FrameBlendWord(p, frameIndex, seq.Length);
                float w = (word >> 16) / 65535f;
                minW = MathF.Min(minW, w); maxW = MathF.Max(maxW, w);
                samples++;
                bool backward = (FrameDecoder.U32(p.Raw.Span, 4) & 0x80) != 0;
                int expected = backward ? (frameIndex + 1) % seq.Length : (frameIndex - 1 + seq.Length) % seq.Length;
                if ((word & 255) != expected) { prevMismatch++; firstBad ??= $"tick {tick} frame {frameIndex}: previous {word & 255}"; }
            }
        }
    }
    Check(tables.Length > 0 && samples > 100 && prevMismatch == 0 && minW >= 0 && maxW <= 1 && maxW - minW > .5f,
        $"FrameBlend: {tables.Length} sm82_320 entries, weight acc/T in [0, 1] and moving, previous frame follows the play direction",
        $"{samples} samples, weight {minW:F3}..{maxW:F3}; {firstBad}");
}

// 8. Optional: large official files.
if (args.Contains("--corpus"))
{
    var corpus = new[]
    {
        @"_anotherorder\vfx\effecteditor\efd_event\cs\efd_cse460_s00\efd_0015_cse460_s00_laser_0001.efx.3539837",
        @"_anotherorder\vfx\effecteditor\efd_setmodel\efd_sm84_335_00\efd_0015_sm84_335_00_0000.efx.3539837",
        @"_anotherorder\vfx\effecteditor\efd_event\cs\efd_cse455_s00\efd_0015_cse455_s00_laserejection_0000.efx.3539837",
        @"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm82_614_00\efd_0015_sm82_614_00_0000.efx.3539837",
        @"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm84_785_00\efd_0015_sm84_785_00_0001.efx.3539837",
    };
    var rows = new List<object>();
    foreach (var relative in corpus)
    {
        var ex = new Dictionary<int, string>();
        watch.Restart();
        try
        {
            var s = await PreviewSceneLoader.LoadAsync(client, Path.Combine(stm, relative), resources, ex);
            double load = watch.Elapsed.TotalMilliseconds;
            bool ok = true; int peak = 0; var simMs = new List<double>();
            for (int tick = 1; tick <= 600; tick++)
            {
                PreviewFrame f;
                try { f = await client.FrameAsync(tick); }
                catch (Exception error) when (PreviewSceneLoader.ExcludeFailedEntry(s, error, ex, tick) != null)
                {
                    // Same handling as the window: drop the failing entry, reload, replay to this tick.
                    s = await PreviewSceneLoader.LoadAsync(client, Path.Combine(stm, relative), resources, ex);
                    tick--; continue;
                }
                using (f) { ok &= Finite(f); peak = Math.Max(peak, f.Particles.Count); simMs.Add(f.SimulationMs); }
            }
            simMs.Sort();
            Check(ok, $"{Path.GetFileName(relative)}: {s.Shown.Count}/{s.Entries.Count} entries run 600 ticks finite", $"load {load:F0} ms, sim median {simMs[300]:F2} ms, peak alive {peak}, native exclusions {ex.Count}" + string.Concat(ex.Select(p => $"; [{p.Key}] {p.Value}")));
            rows.Add(new { file = relative, entries = s.Entries.Count, shown = s.Shown.Count, nativeExclusions = ex, loadMs = load, simMedianMs = simMs[300], peak });
        }
        catch (Exception e) { Check(false, Path.GetFileName(relative), e.GetBaseException().Message); }
    }
    results["corpus"] = rows;
}

string output = Path.Combine(repo, "test-output", "re4-multi-entry-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"{(failures == 0 ? "PASS" : "FAIL")}: {checks - failures}/{checks}; {output}");
return failures == 0 ? 0 : 1;

static string FindRepository()
{
    for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        if (File.Exists(Path.Combine(folder.FullName, "tools", "Re4Native", "re4host.py"))) return folder.FullName;
    return Directory.GetCurrentDirectory();
}

sealed class RibbonCheck
{
    public bool Shape = true, Finite = true, Monotonic = true, Follows = true;
    public int Strips, Full;
    public float WorstHead;
    public Dictionary<int, System.Numerics.Vector3> Frame(PreviewFrame f, int table, Dictionary<int, System.Numerics.Vector3> previous)
    {
        var st = f.EntryStates![table];
        Shape &= st.Type == 31 && st.StripPointsPerParticle == 13;
        var now = new Dictionary<int, System.Numerics.Vector3>();
        for (int i = st.First; i < st.First + st.Count; i++)
        {
            var p = f.Particles[i];
            now[p.Slot] = p.Position;
            int valid = st.ValidPoints(p.Slot);
            Shape &= valid is >= 0 and <= 13;
            if (valid < 2) continue;
            Strips++;
            var pts = st.PointsOf(p.Slot);
            float lastV = -1;
            for (int k = 0; k < valid; k++)
            {
                float x = FrameDecoder.F32(pts, k * 0x54), y = FrameDecoder.F32(pts, k * 0x54 + 4), z = FrameDecoder.F32(pts, k * 0x54 + 8), v = FrameDecoder.F32(pts, k * 0x54 + 0x40);
                Finite &= float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && float.IsFinite(v);
                Monotonic &= v >= lastV - 1e-5f && v <= 1 + 1e-5f;
                lastV = v;
            }
            if (valid == 13) Full++;
            // The head is sampled before this tick's move: it lies near the previous or current world position.
            if (previous.TryGetValue(p.Slot, out var before))
            {
                var head = new System.Numerics.Vector3(FrameDecoder.F32(pts, 0), FrameDecoder.F32(pts, 4), FrameDecoder.F32(pts, 8));
                float step = System.Numerics.Vector3.Distance(before, p.Position);
                float d = MathF.Min(System.Numerics.Vector3.Distance(head, before), System.Numerics.Vector3.Distance(head, p.Position));
                WorstHead = MathF.Max(WorstHead, d / MathF.Max(step, 1e-3f));
                Follows &= d <= step + 1e-3f;
            }
        }
        return now;
    }
}

sealed class LengthCheck
{
    public bool Ok = true; public int Strips; public string? Problem;
    public void Frame(PreviewFrame f, int table)
    {
        var st = f.EntryStates![table];
        if (st.Type != 32 || st.StripPointsPerParticle != 2) { Ok = false; Problem ??= $"type {st.Type} per {st.StripPointsPerParticle}"; return; }
        for (int i = st.First; i < st.First + st.Count; i++)
        {
            var p = f.Particles[i];
            int valid = st.ValidPoints(p.Slot);
            var pts = st.PointsOf(p.Slot);
            var a = new System.Numerics.Vector3(FrameDecoder.F32(pts, 0), FrameDecoder.F32(pts, 4), FrameDecoder.F32(pts, 8));
            var b = new System.Numerics.Vector3(FrameDecoder.F32(pts, 0x54), FrameDecoder.F32(pts, 0x58), FrameDecoder.F32(pts, 0x5c));
            float v0 = FrameDecoder.F32(pts, 0x40), v1 = FrameDecoder.F32(pts, 0x54 + 0x40);
            bool good = valid == 2 && float.IsFinite(a.X + a.Y + a.Z + b.X + b.Y + b.Z) && v0 == 1 && v1 == 0 && System.Numerics.Vector3.Distance(a, b) > 1e-3f;
            if (!good) { Ok = false; Problem ??= $"slot {p.Slot}: valid {valid} V {v0},{v1} a {a} b {b}"; }
            Strips++;
        }
    }
}
