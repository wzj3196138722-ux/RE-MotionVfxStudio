using System.Diagnostics;
using System.Text.Json;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

// Same visible, vsynced window for both polling intervals; process CPU includes rendering.
internal sealed class PreviewIdleBenchmark(PreviewLatencyRecorder recorder, string directory, int seconds)
{
    private readonly int duration=seconds is >= 1 and <= 120?seconds:throw new ArgumentOutOfRangeException(nameof(seconds));
    private readonly Stopwatch warmup=new(), measurement=new(), timeout=Stopwatch.StartNew();
    private TimeSpan cpuStart;
    private long pollsStart;
    private double pollMsStart;

    public bool Update(PreviewPlaybackController playback, PreviewFrame? frame, bool busy)
    {
        if(timeout.Elapsed>TimeSpan.FromSeconds(duration+45)) throw new TimeoutException("Idle benchmark did not finish.");
        if(busy || !playback.IsReady || playback.IsPlaying || playback.IsBusy || playback.HasQueuedRequest || frame?.Tick!=0)
            return false;
        if(!warmup.IsRunning) warmup.Start();
        if(warmup.Elapsed<TimeSpan.FromSeconds(1)) return false;
        if(!measurement.IsRunning)
        {
            using var process=Process.GetCurrentProcess();
            cpuStart=process.TotalProcessorTime;
            pollsStart=recorder.PollCount; pollMsStart=recorder.PollTotalMs;
            measurement.Start(); return false;
        }
        if(measurement.Elapsed<TimeSpan.FromSeconds(duration)) return false;
        using var current=Process.GetCurrentProcess();
        double cpuMs=(current.TotalProcessorTime-cpuStart).TotalMilliseconds;
        double wallMs=measurement.Elapsed.TotalMilliseconds;
        long polls=recorder.PollCount-pollsStart;
        double pollMs=recorder.PollTotalMs-pollMsStart;
        var result=new {durationMs=wallMs,processCpuMs=cpuMs,oneCoreCpuPercent=100*cpuMs/wallMs,
            pollCount=polls,pollsPerSecond=1000*polls/wallMs,pollTotalWallMs=pollMs,
            pollMeanWallMs=polls==0?0:pollMs/polls,pollWallMsPerSecond=1000*pollMs/wallMs,
            note="Visible window paused at tick 0; CPU includes rendering, excludes child processes. Poll time is wall time."};
        File.WriteAllText(Path.Combine(directory,"idle-benchmark.json"),JsonSerializer.Serialize(result));
        Console.WriteLine("IDLE "+JsonSerializer.Serialize(result));
        return true;
    }
}
