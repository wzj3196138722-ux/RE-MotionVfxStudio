using System.Diagnostics;
using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text.Json;

namespace VfxStudio.Re4Render;

public sealed class HostFaultException(string message) : IOException(message);

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
    /// <summary>How long one command may take before the worker is treated as hung and stopped.</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public long SnapshotBytes { get; private set; }

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
                string error = reply.GetProperty("error").GetRawText();
                if (reply.TryGetProperty("fatal", out var fatal) && fatal.GetBoolean()) throw new HostFaultException(error);
                throw new InvalidOperationException(error);
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
            loadCommand = JsonSerializer.SerializeToElement(command);
            track = null;
            await Reload();
        }
        catch { Stop(); throw; }
        finally { gate.Release(); }
    }

    private async Task Reload()
    {
        Stop();
        Start();
        var metadata = await Send(loadCommand);
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
        if (!header.AsSpan(0,4).SequenceEqual("R4FX"u8) || FrameDecoder.U32(header,4) != 2 ||
            FrameDecoder.U32(header,8) != 0x69A8F652 || FrameDecoder.U32(header,12) != 3 ||
            FrameDecoder.U32(header,16) != slotBytes || FrameDecoder.U32(header,28) != bytes)
            throw new InvalidDataException("Worker protocol/build mismatch");
        slot = 0;
        if (track != null) await UploadTrack();
    }

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
        view!.WriteArray(inputOffset, track!, 0, track!.Length);
        await Send(new { command = "track", length = track.Length });
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
                throw new HostFaultException($"Worker restarted; playback paused. {ex.Message}");
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
