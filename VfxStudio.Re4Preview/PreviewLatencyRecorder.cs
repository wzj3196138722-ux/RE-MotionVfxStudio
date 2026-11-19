using System.Diagnostics;
using System.Text.Json;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

// Opt-in diagnostics. All timestamps use the same monotonic clock on the UI thread.
internal sealed class PreviewLatencyRecorder(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
    private readonly List<LatencySample> samples = [];
    private LatencySample? active;
    public bool IsActive => active != null;
    public int Count => samples.Count;
    public string? LastResult { get; private set; }
    public string? LastOutcome { get; private set; }
    public long PollCount { get; private set; }
    public double PollTotalMs { get; private set; }
    private double pollMaxMs;

    public void ManifestPolled(double milliseconds)
    {
        PollCount++; PollTotalMs+=milliseconds; pollMaxMs=Math.Max(pollMaxMs,milliseconds);
    }

    public void Configure(object configuration)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"latency-environment.json"),JsonSerializer.Serialize(configuration,Json));
    }

    public void Begin(string route, string action, int tick, bool playing, bool warmup = false,
        string? expectedDocument = null, long? expectedRevision = null)
    {
        if (active != null) Fail("superseded");
        active = new() { Route=route, Action=action, RequestedTick=tick, WasPlaying=playing, Warmup=warmup,
            ExpectedDocument=expectedDocument,ExpectedRevision=expectedRevision };
    }

    public void Published(OfflinePreviewManifest manifest)
    {
        if(active?.ExpectedDocument!=manifest.DocumentId || active.ExpectedRevision!=manifest.Revision) return;
        active.ManifestObservedMs??=active.Elapsed;
        active.Key=manifest.Key;active.Revision=manifest.Revision;active.DocumentId=manifest.DocumentId;
    }

    public void Responded(PreviewEditorState state)
    {
        if (active == null) return;
        active.ResponseMs = active.Elapsed;
        if (state.PublicationError != null || state.Manifest == null || state.Manifest.Revision != state.Revision)
        { Fail("publication_failed"); return; }
        if(active.Key!=null && active.Key!=state.Manifest.Key) {Fail("response_revision_mismatch");return;}
        active.Key=state.Manifest.Key; active.Revision=state.Revision; active.DocumentId=state.Manifest.DocumentId;
        TryComplete();
    }

    public void LoadStarted(string key) { if (active?.Key == key) active.LoadStartedMs=active.Elapsed; }
    public void Loaded(string key, double nativeMs)
    {
        if (active?.Key != key) return;
        active.LoadFinishedMs=active.Elapsed; active.NativeLoadMs=nativeMs;
    }
    public void FrameReady(string key)
    {
        if (active?.Key == key && active.FrameReadyMs == null) active.FrameReadyMs=active.Elapsed;
    }
    public void Rendered(string key, int tick, bool playing, Action waitForGpu)
    {
        if (active?.Key != key || active.FrameReadyMs == null || active.GpuCompleteMs != null) return;
        active.RenderSubmittedMs=active.Elapsed;
        // Once per measured revision, never in normal rendering. This includes diagnostic
        // synchronization cost and stops before buffer swap/compositor/monitor scanout.
        waitForGpu();
        active.GpuCompleteMs=active.Elapsed; active.DisplayedTick=tick; active.IsPlaying=playing;
        active.Outcome="completed";
        LastResult=$"{active.Route} r{active.Revision}: {active.GpuCompleteMs:F1} ms（GPU 完成）";
        TryComplete();
    }
    private void TryComplete()
    {
        // The immutable publication can reach the GPU before the writer receives its reply.
        // Preserve the earlier GPU timestamp, but wait for the reply to finish the log record.
        if(active?.GpuCompleteMs!=null && active.ResponseMs!=null) Save();
    }
    public void Fail(string reason)
    {
        if (active == null) return;
        active.Outcome=reason; Save();
    }
    private void Save()
    {
        LastOutcome=active!.Outcome;
        samples.Add(active!);
        File.AppendAllText(Path.Combine(directory,"latency.jsonl"),JsonSerializer.Serialize(active,Json)+Environment.NewLine);
        Console.WriteLine("LATENCY "+JsonSerializer.Serialize(active,Json));
        active=null;
    }
    public void WriteSummary()
    {
        var summary=samples.Where(s=>!s.Warmup && s.Outcome=="completed")
            .GroupBy(s=>new{s.Route,s.RequestedTick})
            .Select(g=>
            {
                var values=g.Select(s=>s.GpuCompleteMs!.Value).Order().ToArray();
                double median=values.Length%2==0?(values[values.Length/2-1]+values[values.Length/2])/2:values[values.Length/2];
                return new {g.Key.Route,g.Key.RequestedTick,count=values.Length,medianMs=median,
                    p95Ms=values[(int)Math.Ceiling(values.Length*.95)-1],maxMs=values[^1],
                    responseMeanMs=g.Average(s=>s.ResponseMs),nativeLoadMeanMs=g.Average(s=>s.NativeLoadMs),
                    frameReadyMeanMs=g.Average(s=>s.FrameReadyMs),
                    stateRetained=g.All(s=>s.WasPlaying==s.IsPlaying && (s.WasPlaying || s.RequestedTick==s.DisplayedTick))};
            }).ToArray();
        File.WriteAllText(Path.Combine(directory,"latency-summary.json"),JsonSerializer.Serialize(summary,Json));
        File.WriteAllText(Path.Combine(directory,"manifest-poll-stats.json"),JsonSerializer.Serialize(new {
            count=PollCount,totalWallMs=PollTotalMs,meanWallMs=PollCount==0?0:PollTotalMs/PollCount,maxWallMs=pollMaxMs },Json));
    }
}

internal sealed class LatencySample
{
    [System.Text.Json.Serialization.JsonIgnore] public long Start { get; }=Stopwatch.GetTimestamp();
    [System.Text.Json.Serialization.JsonIgnore] public double Elapsed=>Stopwatch.GetElapsedTime(Start).TotalMilliseconds;
    [System.Text.Json.Serialization.JsonIgnore] public string? ExpectedDocument { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public long? ExpectedRevision { get; init; }
    public string Route { get; init; }="";
    public string Action { get; init; }="";
    public bool Warmup { get; init; }
    public int RequestedTick { get; init; }
    public bool WasPlaying { get; init; }
    public string? Key { get; set; }
    public string? DocumentId { get; set; }
    public long Revision { get; set; }
    public double? ResponseMs { get; set; }
    public double? ManifestObservedMs { get; set; }
    public double? LoadStartedMs { get; set; }
    public double? LoadFinishedMs { get; set; }
    public double? NativeLoadMs { get; set; }
    public double? FrameReadyMs { get; set; }
    public double? RenderSubmittedMs { get; set; }
    public double? GpuCompleteMs { get; set; }
    public int DisplayedTick { get; set; }
    public bool IsPlaying { get; set; }
    public string Outcome { get; set; }="pending";
}
