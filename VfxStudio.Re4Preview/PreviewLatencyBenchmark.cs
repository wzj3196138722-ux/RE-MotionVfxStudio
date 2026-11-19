using System.Diagnostics;
using System.Numerics;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

// Drives real window rendering; this is a repeatable latency run, separate from mouse acceptance.
internal sealed class PreviewLatencyBenchmark(PreviewLatencyRecorder recorder, PreviewEditorSession editor, int repeats)
{
    private readonly int[] ticks=[45,300,900];
    private readonly int sampleCount=repeats>0 && repeats<=10000?repeats:throw new ArgumentOutOfRangeException(nameof(repeats));
    private int group, iteration, awaitingCount;
    private bool seeking, awaiting;
    private Task<PreviewEditorState>? external;
    private readonly Stopwatch timeout=Stopwatch.StartNew();

    public bool Update(PreviewPlaybackController playback, PreviewFrame? frame, PreviewEditorState? state,
        OfflinePreviewManifest? displayed, bool busy, Action<PreviewEditCommand,bool> submit)
    {
        if (timeout.Elapsed>TimeSpan.FromSeconds(45)) throw new TimeoutException("Window latency benchmark made no progress for 45 seconds.");
        if (external?.IsCompleted==true)
        {
            recorder.Responded(external.GetAwaiter().GetResult());
            external=null;
        }
        if (awaiting)
        {
            if (recorder.Count==awaitingCount) return false;
            if (recorder.LastOutcome!="completed") throw new InvalidOperationException("Window latency sample failed: "+recorder.LastOutcome);
            if (recorder.IsActive) throw new InvalidOperationException("Unexpected benchmark measurement overlap");
            awaiting=false; timeout.Restart(); iteration++;
            if (iteration==sampleCount+2) { iteration=0; group++; seeking=false; }
        }
        if (group==ticks.Length*2) { recorder.WriteSummary(); return true; }
        if (busy || external!=null || !playback.IsReady || playback.IsBusy || playback.HasQueuedRequest ||
            state?.Parameters==null || state.Manifest?.Key!=displayed?.Key || frame==null) return false;
        int tick=ticks[group%ticks.Length];
        if (!seeking) {playback.Seek(tick);seeking=true;return false;}
        if (frame.Tick!=tick) return false;
        var p=state.Parameters;
        Vector4 Color(int[] c)=>new(c[0]/255f,c[1]/255f,c[2]/255f,c[3]/255f);
        var edit=new BillboardEdit(Color(p.Color),Color(p.ColorRange),iteration%2==0?.75f:1.25f,
            new(p.SizeScalar[0],p.SizeScalar[1]),new(p.SizeX[0],p.SizeX[1]),new(p.SizeY[0],p.SizeY[1]));
        awaiting=true; awaitingCount=recorder.Count;
        bool warmup=iteration<2;
        if (group<ticks.Length) submit(new("billboard",state.Revision,edit),warmup);
        else
        {
            // Only the writer task sees this reply; the window must discover its revision via
            // the regular manifest polling/status path, just as for another mailbox client.
            recorder.Begin("external_watch","set_fields",playback.DisplayedTick,playback.IsPlaying,warmup,
                state.Manifest!.DocumentId,state.Revision+1);
            external=Task.Run(()=>editor.ApplyBillboardAsync(edit,state.Revision));
        }
        return false;
    }
}
