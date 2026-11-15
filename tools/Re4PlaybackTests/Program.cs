using System.Security.Cryptography;
using System.Runtime.Versioning;
using VfxStudio.Re4Render;

[assembly: SupportedOSPlatform("windows")]

try
{
    void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    PreviewFrameRequest Begin(PreviewPlaybackController state)
    {
        Check(state.TryBeginRequest(out var request), "Expected a queued request");
        return request;
    }
    var playback = new PreviewPlaybackController();
    playback.BeginLoad(); playback.FinishLoad();
    Check(playback.CompleteRequest(Begin(playback)), "Initial frame rejected");
    playback.TogglePlayback(); playback.Advance(1.0 / 60);
    var running = Begin(playback);
    Check(running.Tick == 1, "Playback did not request next frame");
    playback.Pause();
    Check(!playback.IsPlaying && playback.TargetTick == 0, "Pause did not retain visible frame");
    Check(!playback.CompleteRequest(running), "Pause accepted a stale running result");
    var paused = Begin(playback);
    Check(paused.Tick == 0 && playback.CompleteRequest(paused), "Pause did not restore its frame");
    playback.Advance(10);
    Check(!playback.HasQueuedRequest, "Paused clock advanced");

    playback.Step();
    var step = Begin(playback);
    Check(step.Tick == 1, "Step did not move exactly one tick");
    playback.Seek(900); playback.Seek(0);
    Check(!playback.CompleteRequest(step), "Reset accepted a stale step result");
    var reset = Begin(playback);
    Check(reset.Tick == 0 && playback.CompleteRequest(reset), "Reset was lost while busy");

    playback.Seek(40); var seek = Begin(playback);
    playback.Seek(120); playback.Seek(200); playback.Seek(70);
    Check(!playback.CompleteRequest(seek), "Slider accepted an obsolete frame");
    var latest = Begin(playback);
    Check(latest.Tick == 70 && playback.CompleteRequest(latest), "Slider did not retain latest target");
    playback.Step(); playback.Step(); playback.Step();
    var repeated = Begin(playback);
    Check(repeated.Tick == 73 && playback.CompleteRequest(repeated), "Rapid steps lost clicks");

    playback.TogglePlayback(); playback.Advance(1.0 / 60);
    var beforeReload = Begin(playback);
    playback.BeginLoad();
    Check(!playback.CompleteRequest(beforeReload), "Reload accepted old document data");
    Check(!playback.TryBeginRequest(out _), "Request started during reload");
    playback.FinishLoad();
    Check(Begin(playback).Tick == 73, "Reload lost displayed timeline position");
    Console.WriteLine("PASS busy pause/reset/seek, stale result rejection, rapid steps, reload ordering");

    // Failure and recovery paths used by the preview window.
    {
        var c = new PreviewPlaybackController();
        c.BeginLoad(restart:true); c.FinishLoad(); Check(c.CompleteRequest(Begin(c)), "Initial frame rejected");
        c.Seek(90); Check(c.CompleteRequest(Begin(c)), "Seek 90 rejected");
        // A failed frame (worker already restarted by the client) pauses on the displayed tick and stays usable.
        c.TogglePlayback(); c.Advance(1.0 / 60); var failing = Begin(c);
        c.FailRequest(failing);
        Check(c.IsReady && !c.IsPlaying && c.TargetTick == 90 && !c.HasQueuedRequest, "Frame failure lost the timeline or left the controller unusable");
        c.Step(); var afterFailure = Begin(c);
        Check(afterFailure.Tick == 91 && c.CompleteRequest(afterFailure), "Controller did not resume after a failed frame");
        // Fault-test path: BeginLoad without restart, FinishLoad after the client's own reload.
        c.BeginLoad(); Check(!c.TryBeginRequest(out _), "Request started while the worker restarts");
        c.FinishLoad(); var resumed = Begin(c);
        Check(resumed.Tick == 91 && c.CompleteRequest(resumed), "Worker restart did not keep the displayed tick");
        // New revision of the same document keeps the tick; a different document restarts at 0.
        c.BeginLoad(restart:false); c.FinishLoad(); Check(Begin(c).Tick == 91, "Same-document reload lost the tick");
        var d = new PreviewPlaybackController(); d.BeginLoad(); d.FinishLoad(); d.CompleteRequest(Begin(d));
        d.Seek(50); d.CompleteRequest(Begin(d)); d.BeginLoad(restart:true); d.FinishLoad();
        Check(Begin(d).Tick == 0, "Document switch did not restart at tick 0");
        Console.WriteLine("PASS frame failure pause/resume, worker restart keeps tick, restart only on document switch");
    }

    if (args.Contains("--native"))
    {
        string repo = Directory.GetCurrentDirectory();
        string exe = Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
        using var client = new NativePreviewClient("python", Path.Combine(repo,"tools/Re4Native/re4host.py"), exe);
        await client.LoadAsync(new { command="load", fixture="bb2" });
        var state = new PreviewPlaybackController(); state.BeginLoad(); state.FinishLoad();
        var initial = Begin(state); using (var f=await client.FrameAsync(initial.Tick)) state.CompleteRequest(initial);
        state.Seek(45); var at45=Begin(state);
        string hash45;
        using(var f=await client.FrameAsync(at45.Tick))
        {
            Check(state.CompleteRequest(at45), "Native seek rejected");
            hash45=Convert.ToHexString(SHA256.HashData(f.Arrays[0].Span));
        }
        state.TogglePlayback(); state.Advance(1.0 / 60);
        var next=Begin(state); var pending=client.FrameAsync(next.Tick);
        state.Pause();
        using(var obsolete=await pending) Check(!state.CompleteRequest(next),"In-flight native frame escaped pause");
        var restore=Begin(state);
        using(var f=await client.FrameAsync(restore.Tick))
        {
            Check(state.CompleteRequest(restore) && f.Tick==45,"Native pause did not keep tick 45");
            Check(Convert.ToHexString(SHA256.HashData(f.Arrays[0].Span))==hash45,"Paused restore changed particles");
        }
        state.Seek(200); var forward=Begin(state); pending=client.FrameAsync(forward.Tick);
        state.Seek(0);
        using(var obsolete=await pending) Check(!state.CompleteRequest(forward),"In-flight seek escaped reset");
        var zero=Begin(state);
        using(var f=await client.FrameAsync(zero.Tick)) Check(state.CompleteRequest(zero) && f.Tick==0 && f.Particles.Count==0,"Reset did not clear visible particles");
        state.Step(); var one=Begin(state);
        using(var f=await client.FrameAsync(one.Tick)) Check(state.CompleteRequest(one) && f.Tick==1,"Step after reset failed");
        Console.WriteLine("PASS real native in-flight pause, same-tick digest, reset to empty tick 0, step after reset");
        // A timed-out or broken exchange must not leave the client unusable: after the worker is
        // killed underneath it, the next frame request restarts it and reports the recovery.
        using (var broken = new NativePreviewClient("python", Path.Combine(repo,"tools/Re4Native/re4host.py"), exe, testFaults:true))
        {
            await broken.LoadAsync(new { command="load", fixture="bb2" });
            string digest;
            using (var f = await broken.FrameAsync(45)) digest = Convert.ToHexString(SHA256.HashData(f.Arrays[0].Span));
            await broken.InjectFaultAsync(exit:true);           // worker exits; client reloads it
            Check(broken.LastRecovery.Length > 0, "Abrupt exit not reported");
            using (var f = await broken.FrameAsync(45))
                Check(Convert.ToHexString(SHA256.HashData(f.Arrays[0].Span)) == digest, "Recovered worker differs at the kept tick");
            await broken.SetTrackAsync(Enumerable.Repeat(1f/60, 120).ToArray());
            using (var f = await broken.FrameAsync(45))
                Check(Convert.ToHexString(SHA256.HashData(f.Arrays[0].Span)) == digest, "Track upload after recovery changed the frame");
            // Timeout: the read is cancelled mid-command. The hung worker is stopped, the request
            // reports a restart, and the following request works on a fresh worker.
            broken.ReplyTimeout = TimeSpan.FromMilliseconds(1);
            try { using var _ = await broken.FrameAsync(100); throw new Exception("1 ms timeout did not fire"); }
            catch (IOException ex) when (ex.Message.Contains("timed out")) { }   // HostFaultException is an IOException
            broken.ReplyTimeout = TimeSpan.FromSeconds(90);
            // The next request either finds the worker already reloaded or reloads it (reported once,
            // without repeating the tick); the request after that returns the frame.
            PreviewFrame? recovered = null;
            for (int attempt = 0; attempt < 2 && recovered == null; attempt++)
                try { recovered = await broken.FrameAsync(45); } catch (HostFaultException) { }
            using (recovered)
                Check(recovered != null && Convert.ToHexString(SHA256.HashData(recovered.Arrays[0].Span)) == digest, "Client unusable or different after a timeout");
        }
        Console.WriteLine("PASS native client stays usable after worker exit, timeout and track upload");
    }
}
catch(Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode=1; }
