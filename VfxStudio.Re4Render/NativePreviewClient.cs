using System.Diagnostics;
using System.Numerics;
using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text.Json;

namespace VfxStudio.Re4Render;

/// <summary>The worker rejected one command without ending (for example a RecoveredError while
/// advancing, after which it rewound to tick 0). <see cref="Entry"/> is the load-order position of
/// the entry involved, when the worker reported one.</summary>
public sealed class NativeCommandException(string message, int? entry = null) : InvalidOperationException(message)
{
    public int? Entry { get; } = entry;
}

/// <summary>The worker failed fatally. <see cref="Entry"/> is the load-order position of the entry
/// being built or advanced when it failed, when the worker reported one.</summary>
public sealed class HostFaultException(string message, int? entry = null) : IOException(message)
{
    public int? Entry { get; } = entry;
}

/// <summary>One command in flight. A slot is copied before the next request can reuse it.
/// The renderer owns managed frame copies, never a view that the worker can overwrite.</summary>
[SupportedOSPlatform("windows")]
public sealed class NativePreviewClient : IDisposable
{
    private readonly string python, script, exe;
    private readonly bool testFaults;
    private Process? process;
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private int sequence, slot, slotBytes, inputOffset, trackCapacity;
    private JsonElement loadCommand;
    private float[]? track;
    private readonly Queue<string> logs = new();
    private readonly SemaphoreSlim gate = new(1);
    public string LastRecovery { get; private set; } = "";
    public double LoadMs { get; private set; }
    /// <summary>True when the last load reused the running worker (base snapshot restore) instead of
    /// starting a new process.</summary>
    public bool LastLoadReused { get; private set; }
    /// <summary>Wall time of the last LoadAsync as seen by the caller, including mapping and bind.</summary>
    public double LastLoadWallMs { get; private set; }
    /// <summary>How long one command may take before the worker is treated as hung and stopped.</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public long SnapshotBytes { get; private set; }
    /// <summary>Entries the last load left out without ending the worker (Python-level refusals such as
    /// an attribute with no native factory), by load-order position, with the worker's message.</summary>
    public IReadOnlyList<(int Entry, string Message)> LastLoadSkipped { get; private set; } = [];
    /// <summary>Entries of child Actions the worker could not build, by Action and position in the embedded file.</summary>
    public IReadOnlyList<(int Action, int Child, string Message)> LastLoadSkippedChildren { get; private set; } = [];

    public NativePreviewClient(string python, string script, string exe, bool testFaults = false)
    { this.python = python; this.script = Path.GetFullPath(script); this.exe = exe; this.testFaults = testFaults; }

    private void Start()
    {
        var info = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-I", "-X", "utf8", "-u", script, "--exe", exe }) info.ArgumentList.Add(arg);
        if (testFaults) info.ArgumentList.Add("--allow-test-faults");
        process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (logs) { logs.Enqueue(e.Data); while (logs.Count > 32) logs.Dequeue(); } };
        process.Start();
        ChildProcessJob.Assign(process);
        process.BeginErrorReadLine();
    }

    private async Task<JsonElement> Send(object command)
    {
        if (process == null) throw new IOException("Worker has not started");
        var node = JsonSerializer.SerializeToNode(command)!.AsObject();
        int id = ++sequence;
        node["id"] = id;
        // A timed-out or garbled exchange leaves an abandoned read on the pipe and an unknown worker
        // state. Such a worker is stopped here, so every caller sees an IOException and recovers
        // through Reload instead of reusing the broken stream.
        try
        {
            // The redirected pipes are synchronous handles: a cancellation token is checked only before
            // the read starts and cannot interrupt a blocked read. WaitAsync bounds the wait; Stop()
            // then kills the worker, which completes the abandoned read with end of stream.
            var deadline = Stopwatch.StartNew();
            await process.StandardInput.WriteLineAsync(node.ToJsonString()).WaitAsync(ReplyTimeout);
            await process.StandardInput.FlushAsync().WaitAsync(Remaining(deadline));
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(Remaining(deadline));
            if (line == null) throw new IOException("Native worker exited. " + string.Join("\n", GetLogs()));
            JsonElement reply;
            try
            {
                using var doc = JsonDocument.Parse(line);
                reply = doc.RootElement.Clone();
                if (reply.GetProperty("id").GetInt32() != id) throw new IOException("Mismatched worker response");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            { throw new IOException("Malformed worker response: " + line); }
            if (!reply.GetProperty("ok").GetBoolean())
            {
                var errorElement = reply.GetProperty("error");
                string error = errorElement.GetRawText();
                int? entry = errorElement.TryGetProperty("entry", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;
                if (reply.TryGetProperty("fatal", out var fatal) && fatal.GetBoolean()) throw new HostFaultException(error, entry);
                throw new NativeCommandException(error, entry);
            }
            return reply;
        }
        catch (TimeoutException) { Stop(); throw new IOException($"Native worker timed out after {ReplyTimeout.TotalSeconds:G4} seconds"); }
        catch (IOException) { Stop(); throw; }
        catch (InvalidOperationException ex) when (process?.HasExited != false)
        { Stop(); throw new IOException("Native worker stream is unusable: " + ex.Message); }
    }

    private TimeSpan Remaining(Stopwatch elapsed)
    {
        var left = ReplyTimeout - elapsed.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    public string[] GetLogs() { lock (logs) return logs.ToArray(); }

    public async Task LoadAsync(object command)
    {
        await gate.WaitAsync();
        try
        {
            var watch = Stopwatch.StartNew();
            loadCommand = JsonSerializer.SerializeToElement(command);
            track = null;
            if (process is { HasExited: false } && view != null)
            {
                // Same worker: it restores its base snapshot and builds the new document in place.
                try { await Reload(restart: false); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
                {
                    // A load error ends the worker; retry once in a fresh process so a problem left
                    // by the reused image cannot be mistaken for a problem in the document.
                    LastRecovery = "同进程重载失败，已改用新进程：" + ex.Message;
                    await Reload(restart: true);
                }
            }
            else await Reload(restart: true);
            LastLoadWallMs = watch.Elapsed.TotalMilliseconds;
        }
        catch { Stop(); throw; }
        finally { gate.Release(); }
    }

    private async Task Reload(bool restart = true)
    {
        if (restart) { Stop(); Start(); }
        else { view?.Dispose(); view = null; mapping?.Dispose(); mapping = null; }
        var metadata = await Send(loadCommand);
        LastLoadReused = metadata.TryGetProperty("reused", out var reused) && reused.GetBoolean();
        LastLoadSkippedChildren = metadata.TryGetProperty("skipped", out var skippedChildren) && skippedChildren.ValueKind == JsonValueKind.Array
            ? skippedChildren.EnumerateArray().Where(x => x.TryGetProperty("action", out _))
                .Select(x => (x.GetProperty("action").GetInt32(), x.GetProperty("child").GetInt32(), x.GetProperty("message").GetString() ?? "")).ToArray()
            : [];
        LastLoadSkipped = metadata.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.Array
            ? skipped.EnumerateArray().Where(x => !x.TryGetProperty("action", out _)).Select(x => (x.GetProperty("entry").GetInt32(), x.GetProperty("message").GetString() ?? "")).ToArray()
            : [];
        slotBytes = metadata.GetProperty("slot_bytes").GetInt32();
        int bytes = metadata.GetProperty("mapping_bytes").GetInt32();
        inputOffset = metadata.GetProperty("input_offset").GetInt32();
        trackCapacity = metadata.GetProperty("track_capacity").GetInt32();
        LoadMs = metadata.GetProperty("load_ms").GetDouble();
        SnapshotBytes = metadata.GetProperty("snapshot_bytes").GetInt64();
        if (slotBytes <= 0 || slotBytes > 512 * 1024 * 1024 || bytes < inputOffset || bytes > 1024 * 1024 * 1024)
            throw new InvalidDataException("Invalid mapping dimensions");
        string name = $"Local\\Re4EfxPreview_{Environment.ProcessId}_{Guid.NewGuid():N}";
        mapping = MemoryMappedFile.CreateNew(name, bytes);
        view = mapping.CreateViewAccessor();
        await Send(new { command = "bind", name });
        var header = new byte[32];
        view.ReadArray(0, header, 0, header.Length);
        if (!header.AsSpan(0,4).SequenceEqual("R4FX"u8) || FrameDecoder.U32(header,4) != FrameDecoder.ProtocolVersion ||
            FrameDecoder.U32(header,8) != 0x69A8F652 || FrameDecoder.U32(header,12) != 3 ||
            FrameDecoder.U32(header,16) != slotBytes || FrameDecoder.U32(header,28) != bytes)
            throw new InvalidDataException("Worker protocol/build mismatch");
        slot = 0;
        if (track != null || WorldTrack != null) await UploadTrack();
    }

    /// <summary>Emitter world matrix per tick (tick t uses element t-1), kept across loads and applied on the next
    /// load or reload; null keeps the identity matrix. Uploaded with the dt track (1/60 per tick unless set).</summary>
    public Matrix4x4[]? WorldTrack { get; set; }

    public async Task SetTrackAsync(float[] values)
    {
        await gate.WaitAsync();
        try
        {
            if (values.Length < 1 || values.Length > trackCapacity || values.Any(v => !float.IsFinite(v) || v <= 0 || v > 1))
                throw new ArgumentException("Invalid dt track");
            track = (float[])values.Clone();
            try { await UploadTrack(); }
            catch (IOException ex) { LastRecovery = ex.Message; await Reload(); }   // Reload uploads the new track
        }
        finally { gate.Release(); }
    }

    private async Task UploadTrack()
    {
        var worlds = WorldTrack;
        if (worlds != null && (worlds.Length < 1 || worlds.Length > trackCapacity))
            throw new ArgumentException("Invalid world matrix track length");
        var dts = track ?? Enumerable.Repeat(1f / 60, worlds!.Length).ToArray();
        if (worlds != null && worlds.Length != dts.Length)
            dts = Enumerable.Range(0, worlds.Length).Select(i => i < dts.Length ? dts[i] : 1f / 60).ToArray();
        view!.WriteArray(inputOffset, dts, 0, dts.Length);
        if (worlds != null)
        {
            // Row-major 4x4 per tick after the dt array (PROTOCOL.md "Input").
            var values = new float[worlds.Length * 16];
            for (int i = 0; i < worlds.Length; i++)
            {
                var m = worlds[i];
                new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }
                    .CopyTo(values, i * 16);
            }
            view.WriteArray(inputOffset + trackCapacity * 4L, values, 0, values.Length);
        }
        await Send(new { command = "track", length = dts.Length, matrices = worlds != null });
    }

    public async Task<PreviewFrame> FrameAsync(int tick)
    {
        await gate.WaitAsync();
        try
        {
            try { return await ReadFrame(tick); }
            catch (IOException ex)
            {
                LastRecovery = ex.Message;
                // Recreate the worker, but do not automatically repeat a faulting tick.
                // The UI pauses and retains its previous frame for inspection.
                await Reload();
                throw new HostFaultException($"Worker restarted; playback paused. {ex.Message}", (ex as HostFaultException)?.Entry);
            }
        }
        finally { gate.Release(); }
    }

    private async Task<PreviewFrame> ReadFrame(int tick)
    {
        int writeSlot = slot;
        var response = await Send(new { command = "frame", tick, slot = writeSlot });
        if (response.GetProperty("tick").GetInt32() != tick || response.GetProperty("slot").GetInt32() != writeSlot)
            throw new InvalidDataException("Unexpected frame response");
        var decodeWatch = Stopwatch.StartNew();
        var bytes = ArrayPool<byte>.Shared.Rent(slotBytes);
        PreviewFrame frame;
        try
        {
            view!.ReadArray(FrameDecoder.HeaderSize + (long)writeSlot * slotBytes, bytes, 0, slotBytes);
            frame = FrameDecoder.Decode(bytes, response.GetProperty("sim_ms").GetDouble(),
                response.GetProperty("restore_ms").GetDouble(), response.GetProperty("total_ms").GetDouble(),pooledPacket:true,byteCount:slotBytes);
        }
        catch { ArrayPool<byte>.Shared.Return(bytes); throw; }
        if (frame.Tick != tick) {frame.Dispose();throw new InvalidDataException("Stale frame slot");}
        slot = (slot + 1) % 3;
        return frame with { DecodeMs = decodeWatch.Elapsed.TotalMilliseconds };
    }

    public async Task InjectFaultAsync(bool exit)
    {
        if (!testFaults) throw new InvalidOperationException("Test faults were not enabled");
        await gate.WaitAsync();
        try
        {
            try { await Send(new { command = "test_fault", exit }); }
            catch (IOException ex) { LastRecovery = ex.Message; await Reload(); }
        }
        finally { gate.Release(); }
    }

    private void Stop()
    {
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try { process.StandardInput.WriteLine("{\"command\":\"shutdown\"}"); process.StandardInput.Flush(); }
                    catch (IOException) { }
                    if (!process.WaitForExit(500)) process.Kill(entireProcessTree: true);
                }
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException) { }
            process.Dispose(); process = null;
        }
        view?.Dispose(); view = null;
        mapping?.Dispose(); mapping = null;
    }
    public void Dispose() => Stop();
}
