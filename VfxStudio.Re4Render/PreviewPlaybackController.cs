namespace VfxStudio.Re4Render;

public readonly record struct PreviewFrameRequest(int Tick, long Revision);

/// <summary>
/// UI-thread playback state. A native request cannot be interrupted, so user commands
/// replace the queued target and invalidate its result instead of waiting for it.
/// </summary>
public sealed class PreviewPlaybackController
{
    public const int LastTick = 3600;
    private const double TickSeconds = 1.0 / 60;
    private long revision;
    private int? queuedTick;
    private PreviewFrameRequest? inFlight;
    private double accumulator;
    private bool playAfterLoad;

    public bool IsReady { get; private set; }
    public bool IsPlaying { get; private set; }
    public int TargetTick { get; private set; }
    public int DisplayedTick { get; private set; }
    public bool IsBusy => inFlight.HasValue;
    public bool HasQueuedRequest => queuedTick.HasValue;

    public void BeginLoad(bool restart = false, bool? autoplay = null)
    {
        // Revisions queued during a load retain the original intent, rather than the
        // temporarily paused IsPlaying value. Explicit opens/replays/faults may override it.
        playAfterLoad = autoplay ?? (IsReady ? IsPlaying : playAfterLoad);
        if (restart) TargetTick = 0;
        else if (IsReady) TargetTick = DisplayedTick;
        IsReady = false;
        IsPlaying = false;
        accumulator = 0;
        queuedTick = null;
        revision++;
        // Keep ownership of the running request until its result is drained.
    }

    public void FinishLoad()
    {
        IsReady = true;
        IsPlaying = playAfterLoad;
        Queue(TargetTick);
    }

    public void TogglePlayback()
    {
        if (!IsReady) return;
        if (IsPlaying) Pause();
        else
        {
            IsPlaying = true;
            accumulator = 0;
            if (TargetTick == LastTick) Queue(0);
        }
    }

    public void Pause()
    {
        if (!IsReady) return;
        IsPlaying = false;
        accumulator = 0;
        // Freeze the frame the user sees, even if the worker has advanced already.
        if (IsBusy || HasQueuedRequest || TargetTick != DisplayedTick) Queue(DisplayedTick);
    }

    public void Seek(int tick)
    {
        if (!IsReady) return;
        IsPlaying = false;
        accumulator = 0;
        Queue(tick);
    }

    public void Step()
    {
        if (!IsReady) return;
        int origin = IsPlaying ? DisplayedTick : TargetTick;
        Seek(Math.Min(origin + 1, LastTick));
    }

    public void Advance(double dt)
    {
        if (!IsReady || !IsPlaying || !double.IsFinite(dt) || dt < 0) return;
        accumulator = Math.Min(accumulator + dt, .25);
        if (!IsBusy && !HasQueuedRequest && accumulator >= TickSeconds && TargetTick < LastTick)
        {
            accumulator -= TickSeconds;
            Queue(TargetTick + 1);
        }
        if (TargetTick == LastTick) IsPlaying = false;
    }

    public bool TryBeginRequest(out PreviewFrameRequest request)
    {
        request = default;
        if (!IsReady || IsBusy || !queuedTick.HasValue) return false;
        request = new PreviewFrameRequest(queuedTick.Value, revision);
        inFlight = request;
        queuedTick = null;
        return true;
    }

    public bool CompleteRequest(PreviewFrameRequest request)
    {
        if (inFlight != request) throw new InvalidOperationException("Unexpected preview request completion");
        inFlight = null;
        if (!IsReady || request.Revision != revision) return false;
        DisplayedTick = request.Tick;
        return true;
    }

    public void FailRequest(PreviewFrameRequest request)
    {
        if (inFlight != request) throw new InvalidOperationException("Unexpected preview request failure");
        inFlight = null;
        IsPlaying = false;
        accumulator = 0;
        if (IsReady)
        {
            queuedTick = null;
            TargetTick = DisplayedTick;
            revision++;
        }
    }

    private void Queue(int tick)
    {
        TargetTick = Math.Clamp(tick, 0, LastTick);
        queuedTick = TargetTick;
        revision++;
    }
}
