using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using VfxStudio.Re4Render;

[assembly: SupportedOSPlatform("windows")]
try
{
string repo = Directory.GetCurrentDirectory();
string exe = Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
string python = Environment.GetEnvironmentVariable("RE4_PYTHON") ?? "python";
string script = Path.Combine(repo,"tools/Re4Native/re4host.py");
var results = new List<object>();
void Check(bool value, string message) { if (!value) throw new Exception(message); }

foreach (var test in new[] { ("bb", "60", false), ("bb", "var", false), ("bb2", "60", false), ("bb2", "var", false), ("bb", "60", true) })
{
    var (scene, clock, globalRandom) = test;
    string scenario = globalRandom ? "bb_nofrg_g" : $"{scene}_{clock}";
    var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (string arg in new[] { "-I", "-X", "utf8", "-u", Path.Combine(repo,"tools/Re4Native/exp_emitter.py"), "run", scenario }) info.ArgumentList.Add(arg);
    info.Environment["RE4_EXE"] = exe;
    using var direct = Process.Start(info)!;
    var stderr = direct.StandardError.ReadToEndAsync();
    string stdout = await direct.StandardOutput.ReadToEndAsync();
    await direct.WaitForExitAsync();
    Check(direct.ExitCode == 0, "Direct baseline crashed: " + await stderr);
    using var baseline = JsonDocument.Parse(stdout);
    Check(baseline.RootElement.GetProperty("status").GetString() == "RAN", "Direct baseline did not run");
    using var client = new NativePreviewClient(python,script,exe,testFaults:true);
    await client.LoadAsync(new { command = "load", fixture = scene,
        drop_types = globalRandom ? new[] { 1 } : Array.Empty<int>(), global_seed = new[] { 1, 2, 3, 4 } });
    var reference = baseline.RootElement.GetProperty("frames").EnumerateArray().ToArray();
    await client.SetTrackAsync(reference.Select(f => f.GetProperty("dt").GetSingle()).ToArray());
    var hashes = new List<string>();
    var costs = new List<double>();
    var decodeCosts = new List<double>();
    for (int tick = 1; tick <= reference.Length; tick++)
    {
        using var frame = await client.FrameAsync(tick);
        var expected = reference[tick-1].GetProperty("particles").EnumerateArray().Where(p => (p.GetProperty("flags").GetUInt32() & 1) != 0).ToArray();
        Check(frame.Particles.Count == expected.Length,$"{scene}/{clock} tick {tick}: count mismatch");
        for (int i = 0; i < expected.Length; i++)
        {
            var actual = frame.Particles[i]; var p = expected[i];
            Check(actual.Id == p.GetProperty("id").GetUInt32() && actual.Slot == p.GetProperty("slot").GetInt32(), "ID/slot mismatch");
            Check(actual.Flags == p.GetProperty("flags").GetUInt32() && actual.LifeAlpha == p.GetProperty("life_alpha").GetByte(), "Flags/life mismatch");
            Check(actual.Age == p.GetProperty("age").GetSingle(), "Age mismatch");
            var xyz = p.GetProperty("pos").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
            for (int k=0;k<3;k++) Check(FrameDecoder.F32(actual.Raw.Span,0x28+k*4) == xyz[k], "Position mismatch");
            // Decoded world position = local x the particle's reference matrix P (+0x100).
            var P = ReferenceMatrix(actual.Raw);
            Check(P.M44 == 1 && actual.Position == System.Numerics.Vector3.Transform(new System.Numerics.Vector3(xyz[0],xyz[1],xyz[2]), P), "Decoded position mismatch");
        }
        hashes.Add(Convert.ToHexString(SHA256.HashData(frame.Arrays[0].Span)));
        costs.Add(frame.SimulationMs);
        decodeCosts.Add(frame.DecodeMs);
    }
    // Replay twice, including tick 0 and all slots, on the same native process.
    double restore = 0;
    for (int pass=0;pass<2;pass++)
    {
        using var zero = await client.FrameAsync(0); restore = Math.Max(restore,zero.RestoreMs);
        Check(zero.Particles.Count == 0,"Tick 0 should be empty");
        for (int tick=1;tick<=reference.Length;tick++)
        {
            using var f = await client.FrameAsync(tick);
            Check(Convert.ToHexString(SHA256.HashData(f.Arrays[0].Span)) == hashes[tick-1],$"{scene}/{clock} replay {pass} tick {tick}: digest mismatch");
        }
    }
    if (scene == "bb2" && clock == "60")
    {
        foreach (bool exit in new[] { false, true })
        {
            await client.InjectFaultAsync(exit);
            Check(client.LastRecovery.Length > 0,"Fault was not reported");
            if (!exit) Check(client.LastRecovery.Contains("NativeFault") && client.LastRecovery.Contains("rva") && client.LastRecovery.Contains("backtrace"), "Missing native diagnostics");
            using var f = await client.FrameAsync(45);
            Check(Convert.ToHexString(SHA256.HashData(f.Arrays[0].Span)) == hashes[44],"Recovery changed particle state");
        }
    }
    results.Add(new { scene,clock,globalRandom,ticks=reference.Length,replays=2,client.LoadMs,client.SnapshotBytes,restore_ms=restore,mean_tick_ms=costs.Average(),mean_read_decode_ms=decodeCosts.Average(),max_tick_ms=costs.Max(),status="PASS" });
    Console.WriteLine($"PASS {scenario}: direct comparison + two complete replays");
}
// Same-process reload: a document loaded into a reused worker (base snapshot restore) must give
// the same per-tick particle arrays as the same document in a fresh worker.
{
    async Task<List<string>> Digests(NativePreviewClient c, int ticks)
    {
        var list = new List<string>();
        for (int tick = 1; tick <= ticks; tick++)
        {
            using var f = await c.FrameAsync(tick);
            list.Add(Convert.ToHexString(SHA256.HashData(f.Arrays[0].Span)));
        }
        return list;
    }
    object Load(string fixture, bool noFrg) => new { command = "load", fixture,
        drop_types = noFrg ? new[] { 1 } : Array.Empty<int>(), global_seed = noFrg ? new[] { 1, 2, 3, 4 } : null };
    var fresh = new Dictionary<string, List<string>>();
    foreach (var (fixture, noFrg) in new[] { ("bb2", false), ("bb", false), ("bb", true) })
    {
        using var c = new NativePreviewClient(python, script, exe);
        await c.LoadAsync(Load(fixture, noFrg));
        Check(!c.LastLoadReused, "First load must start a worker");
        fresh[fixture + noFrg] = await Digests(c, 120);
    }
    using var reused = new NativePreviewClient(python, script, exe);
    await reused.LoadAsync(Load("bb2", false));
    var reloads = new List<double>();
    foreach (var (fixture, noFrg) in new[] { ("bb", false), ("bb", true), ("bb2", false), ("bb2", false), ("bb", false) })
    {
        await reused.LoadAsync(Load(fixture, noFrg));
        Check(reused.LastLoadReused, $"{fixture}: reload did not reuse the worker ({reused.LastRecovery})");
        reloads.Add(reused.LastLoadWallMs);
        var digests = await Digests(reused, 120);
        Check(digests.SequenceEqual(fresh[fixture + noFrg]), $"{fixture} nofrg={noFrg}: reused-worker load differs from a fresh worker");
    }
    results.Add(new { check = "same_process_reload", reloads = reloads.Count, mean_reload_ms = reloads.Average(), max_reload_ms = reloads.Max(), status = "PASS" });
    Console.WriteLine($"PASS same-process reload = fresh worker for bb/bb2/no-FRG; reload {reloads.Average():F0} ms mean, {reloads.Max():F0} ms max");
}
// [p+0] bit 12 belongs to the UV sequence number (bits 10-17). Sequences 4-7, 12-15, ... set it;
// the decoded billboard position must still be +0x28 x emitter matrix, without the +0x34 offset.
{
    var packet = new byte[64 + FrameDecoder.EntrySize + 2*0x140 + 0x2f8];
    void W32(int at, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(at), v);
    void WF(int at, float v) => W32(at, (uint)BitConverter.SingleToInt32Bits(v));
    W32(0, 5); W32(4, 1); WF(16, 1f/60);
    int e = 64, particles = 64 + FrameDecoder.EntrySize, block = particles + 2*0x140;
    W32(e+8, 2); W32(e+12, 0x140); W32(e+40, (uint)particles); W32(e+44, 2*0x140); W32(e+56, (uint)block); W32(e+60, 0x2f8);
    float[] world = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 5,6,7,1 };
    for (int i = 0; i < 16; i++) WF(e+64+i*4, world[i]);
    foreach (var (slot, sequence) in new[] { (0, 4u), (1, 13u) })
    {
        int p = particles + slot*0x140;
        W32(p, 1u | (sequence << 10)); W32(p+4, 0x1000);
        WF(p+0x28, 1); WF(p+0x2c, 2); WF(p+0x30, 3);
        WF(p+0x34, 10); WF(p+0x38, 10); WF(p+0x3c, 10);
    }
    using var synthetic = FrameDecoder.Decode(packet, 0, 0, 0);
    Check(synthetic.Particles.Count == 2, "Synthetic frame particle count");
    foreach (var p in synthetic.Particles)
    {
        Check(((p.Flags >> 10) & 255) is 4 or 13 && (p.Flags & 0x1000) != 0, "Synthetic UV sequence did not set [p+0] bit 12");
        Check(p.Position == new System.Numerics.Vector3(6, 8, 10), $"UV sequence {(p.Flags >> 10) & 255} moved the decoded position to {p.Position}");
    }
}
Console.WriteLine("PASS non-zero UV sequence does not offset decoded position");
// World position = local (+0x28) x the particle's reference matrix P (+0x100); a record without a
// valid P (w != 1) keeps the entry matrix, as above.
{
    var packet = new byte[64 + FrameDecoder.EntrySize + 0x140 + 0x2f8];
    void W32(int at, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(at), v);
    void WF(int at, float v) => W32(at, (uint)BitConverter.SingleToInt32Bits(v));
    W32(0, 5); W32(4, 1);
    int e = 64, particle = 64 + FrameDecoder.EntrySize, block = particle + 0x140;
    W32(e+8, 1); W32(e+12, 0x140); W32(e+40, (uint)particle); W32(e+44, 0x140); W32(e+56, (uint)block); W32(e+60, 0x2f8);
    float[] world = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 5,6,7,1 };
    for (int i = 0; i < 16; i++) WF(e+64+i*4, world[i]);
    W32(particle, 1); WF(particle+0x28, 1); WF(particle+0x2c, 2); WF(particle+0x30, 3);
    // P: 90 degrees about Z (x -> y), then translate (0, -0.5, 0)
    float[] P = { 0,1,0,0, -1,0,0,0, 0,0,1,0, 0,-0.5f,0,1 };
    for (int i = 0; i < 16; i++) WF(particle+0x100+i*4, P[i]);
    using var f = FrameDecoder.Decode(packet, 0, 0, 0);
    var pos = f.Particles[0].Position;
    Check(System.Numerics.Vector3.Distance(pos, new System.Numerics.Vector3(-2, 0.5f, 3)) < 1e-5f, $"Position must be local x P, got {pos}");
}
Console.WriteLine("PASS decoded position uses the particle reference matrix P");
// Decoder rejects corrupted/truncated frames instead of interpreting arbitrary memory.
try { FrameDecoder.Decode(new byte[64],0,0,0); throw new Exception("Corrupt frame accepted"); }
catch (InvalidDataException) { }
Directory.CreateDirectory("test-output/re4-preview");
File.WriteAllText("test-output/re4-preview/results.json",JsonSerializer.Serialize(results,new JsonSerializerOptions { WriteIndented=true }));
Console.WriteLine("PASS decoder bounds; native access violation and abrupt exit recovery");

}
catch(Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode=1; }

static System.Numerics.Matrix4x4 ReferenceMatrix(ReadOnlyMemory<byte> record)
{
    var raw = record.Span;
    return new System.Numerics.Matrix4x4(
        FrameDecoder.F32(raw,0x100),FrameDecoder.F32(raw,0x104),FrameDecoder.F32(raw,0x108),FrameDecoder.F32(raw,0x10c),
        FrameDecoder.F32(raw,0x110),FrameDecoder.F32(raw,0x114),FrameDecoder.F32(raw,0x118),FrameDecoder.F32(raw,0x11c),
        FrameDecoder.F32(raw,0x120),FrameDecoder.F32(raw,0x124),FrameDecoder.F32(raw,0x128),FrameDecoder.F32(raw,0x12c),
        FrameDecoder.F32(raw,0x130),FrameDecoder.F32(raw,0x134),FrameDecoder.F32(raw,0x138),FrameDecoder.F32(raw,0x13c));
}
