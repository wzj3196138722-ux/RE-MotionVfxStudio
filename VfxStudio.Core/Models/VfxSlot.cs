namespace VfxStudio.Core.Models;

/// <summary>
/// Domain model representing a VFX trigger slot/event on an action timeline.
/// Corresponds 1:1 with via.effect.script.VFXRangeTrack in RE Engine .mot files.
/// </summary>
public class VfxSlot
{
    public int SlotId { get; set; }
    public int ClipIndex { get; set; } = 0;
    public int TrackIndex { get; set; } = -1;
    public int SourceKeyIndex { get; set; } = -1;
    public bool NativeMultiKeyTrack { get; set; }
    public string TrackName { get; set; } = "via.effect.script.VFXRangeTrack";

    public string EffectId { get; set; } = "10-0";
    public int ContainerId { get; set; } = 10;
    public int ElementId { get; set; } = 0;

    public float StartFrame { get; set; } = 0f;
    public float EndFrame { get; set; } = 30f;

    private string _jointName = "VFX_Attack";
    public string JointName
    {
        get => _jointName;
        set
        {
            _jointName = value ?? "";
            JointHash = BoneHashRegistry.GetHash(_jointName);
        }
    }

    public uint JointHash { get; set; }

    public int IntervalFrame { get; set; } = 0;
    public bool ActorMotion { get; set; } = false;

    public float OffsetX { get; set; } = 0f;
    public float OffsetY { get; set; } = 0f;
    public float OffsetZ { get; set; } = 0f;

    public bool IsModified { get; set; } = false;

    public VfxSlot()
    {
        JointHash = BoneHashRegistry.GetHash(_jointName);
    }

    public VfxSlot Clone()
    {
        return new VfxSlot
        {
            SlotId = SlotId,
            ClipIndex = ClipIndex,
            TrackIndex = TrackIndex,
            SourceKeyIndex = SourceKeyIndex,
            NativeMultiKeyTrack = NativeMultiKeyTrack,
            TrackName = TrackName,
            EffectId = EffectId,
            ContainerId = ContainerId,
            ElementId = ElementId,
            StartFrame = StartFrame,
            EndFrame = EndFrame,
            JointName = JointName,
            JointHash = JointHash,
            IntervalFrame = IntervalFrame,
            ActorMotion = ActorMotion,
            OffsetX = OffsetX,
            OffsetY = OffsetY,
            OffsetZ = OffsetZ,
            IsModified = true
        };
    }
}

public class MotionInfo
{
    public int MotId { get; set; }
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
    public float FrameCount { get; set; } = 60f;
    public int ClipCount { get; set; }
    public List<VfxSlot> VfxSlots { get; set; } = new();

    public override string ToString() => $"[MotID {MotId}] {Name} ({(string.IsNullOrEmpty(Remark) ? "未备注" : Remark)}) - {FrameCount} frames, {VfxSlots.Count} slots";
}
