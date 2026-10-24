using System.Numerics;
using ReeLib;
using ReeLib.Clip;
using ReeLib.Common;
using ReeLib.Mot;
using ReeLib.Motlist;
using VfxStudio.Core.Models;

namespace VfxStudio.Core.Services;

/// <summary>
/// Service for loading, inspecting, modifying, and saving .motlist and .mot action files.
/// Fully ports the VFX slot tracking, track cloning, and batch operations from re_motion+vfx_editor.
/// </summary>
public class MotionVfxSlotManager
{
    public MotionDictionary MotionDict { get; set; }

    public MotionVfxSlotManager(MotionDictionary? motionDict = null)
    {
        MotionDict = motionDict ?? new MotionDictionary();
    }

    /// <summary>
    /// Loads a .motlist file from disk.
    /// </summary>
    public MotlistFile LoadMotlist(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        var handler = new FileHandler(fs, filePath);
        var motlist = new MotlistFile(handler);
        motlist.Read();
        return motlist;
    }

    /// <summary>
    /// Saves a modified .motlist file to disk with optional backup.
    /// </summary>
    public void SaveMotlist(MotlistFile motlist, string outputPath, bool autoBackup = true)
    {
        if (autoBackup && File.Exists(outputPath))
        {
            var bakPath = outputPath + ".bak";
            if (!File.Exists(bakPath))
            {
                File.Copy(outputPath, bakPath);
            }
        }

        motlist.WriteTo(outputPath);
    }

    /// <summary>
    /// Extracts all MotionInfo objects from a loaded MotlistFile.
    /// </summary>
    public List<MotionInfo> GetMotions(MotlistFile motlist, string weaponKey = "common")
    {
        var result = new List<MotionInfo>();
        int slotIdCounter = 0;

        for (int i = 0; i < motlist.Motions.Count; i++)
        {
            var m = motlist.Motions[i];
            int motId = (int)m.motNumber;
            var mf = m.MotFile as MotFile;
            string motName = mf?.Name ?? $"motion_{motId}";
            string remark = MotionDict.GetMotionName(weaponKey, motId);

            var info = new MotionInfo
            {
                MotId = motId,
                Index = i,
                Name = motName,
                Remark = remark,
                ClipCount = mf?.Clips.Count ?? 0,
                FrameCount = mf?.Header?.frameCount > 0 ? mf.Header.frameCount : 60f
            };

            if (mf != null)
            {
                var slots = ExtractSlotsFromMot(mf, ref slotIdCounter);
                info.VfxSlots.AddRange(slots);
            }

            result.Add(info);
        }

        return result;
    }

    public List<VfxSlot> ExtractSlotsFromMot(MotFile mot, ref int slotIdCounter)
    {
        var slots = new List<VfxSlot>();
        if (mot.Clips == null) return slots;

        for (int c = 0; c < mot.Clips.Count; c++)
        {
            var clip = mot.Clips[c];
            if (clip.ClipEntry?.Tracks == null) continue;

            for (int t = 0; t < clip.ClipEntry.Tracks.Count; t++)
            {
                var trk = clip.ClipEntry.Tracks[t];
                if (!trk.Name.Contains("VFXRangeTrack") && !trk.Name.Contains("EffectTriggerTrack"))
                    continue;

                var effect = trk.Properties.FirstOrDefault(p => p.Info.FunctionName == "EffectId");
                var keys = effect?.Keys?.Select((key, index) => (key, index))
                    .Where(pair => pair.key is NoHermiteKey noHermite && noHermite.Value != null)
                    .OrderBy(pair => pair.key.frame).ToArray();
                if (keys == null || keys.Length == 0) continue;
                // VFXRangeTrack is a continuous range. Repeated equal keys
                // hold its value; only a changed EffectId creates a new call.
                var changes = keys.Where((pair, index) => index == 0 ||
                    ((NoHermiteKey)pair.key).Value?.ToString() !=
                    ((NoHermiteKey)keys[index - 1].key).Value?.ToString()).ToArray();
                var boundaries = new SortedSet<float>(changes.Select(pair => pair.key.frame));
                foreach (var control in trk.Properties.Where(p => p.Info.FunctionName is
                             "DataContainerId" or "JointName" or "ActorMotion" or "InvervalFrame"))
                {
                    if (control.Keys == null) continue;
                    var controlKeys = control.Keys.OrderBy(key => key.frame).ToArray();
                    for (int j = 1; j < controlKeys.Length; j++)
                    {
                        static object? KeyValue(KeyBase key) => key switch
                        {
                            NoHermiteKey n => n.Value,
                            BoolKey b => b.value != 0,
                            _ => null
                        };
                        if (!Equals(KeyValue(controlKeys[j]), KeyValue(controlKeys[j - 1])) &&
                            controlKeys[j].frame <= effect!.Info.endFrame)
                            boundaries.Add(controlKeys[j].frame);
                    }
                }
                var eventFrames = boundaries.ToArray();

                static object? ValueAt(ClipTrack track, string property, float frame)
                {
                    var p = track.Properties.FirstOrDefault(x => x.Info.FunctionName == property);
                    if (p?.Keys == null || p.Keys.Count == 0) return null;
                    var key = p.Keys.Where(x => x.frame <= frame).OrderBy(x => x.frame).LastOrDefault()
                        ?? p.Keys.OrderBy(x => x.frame).First();
                    return key switch { NoHermiteKey n => n.Value, BoolKey b => b.value != 0, _ => null };
                }

                for (int k = 0; k < eventFrames.Length; k++)
                {
                    float eventFrame = eventFrames[k];
                    var (key, keyIndex) = keys.LastOrDefault(pair => pair.key.frame <= eventFrame);
                    if (key == null) (key, keyIndex) = keys[0];
                    string effectId = ((NoHermiteKey)key).Value?.ToString() ?? "";
                    float start = Math.Max(effect!.Info.startFrame, eventFrame);
                    float end = effect.Info.endFrame;
                    // Slot end is inclusive in the preview. A changed key
                    // takes effect at its own frame, so the prior event ends
                    // immediately before that boundary.
                    if (k + 1 < eventFrames.Length) end = Math.Min(end, MathF.BitDecrement(eventFrames[k + 1]));

                    var slot = new VfxSlot
                    {
                        SlotId = ++slotIdCounter,
                        ClipIndex = c,
                        TrackIndex = t,
                        SourceKeyIndex = keyIndex,
                        NativeMultiKeyTrack = trk.Properties.Any(p => p.Keys?.Count > 1),
                        TrackName = trk.Name,
                        EffectId = effectId,
                        StartFrame = start,
                        EndFrame = Math.Max(start, end)
                    };
                    if (effectId.Contains('-'))
                    {
                        var parts = effectId.Split('-');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int cid)) slot.ContainerId = cid;
                        if (parts.Length == 2 && int.TryParse(parts[1], out int eid)) slot.ElementId = eid;
                    }
                    if (ValueAt(trk, "DataContainerId", start) is { } container && int.TryParse(container.ToString(), out int dataCid))
                        slot.ContainerId = dataCid;
                    if (ValueAt(trk, "JointName", start) is { } joint) slot.JointName = joint.ToString() ?? "";
                    if (ValueAt(trk, "InvervalFrame", start) is { } interval && int.TryParse(interval.ToString(), out int inv))
                        slot.IntervalFrame = inv;
                    if (ValueAt(trk, "ActorMotion", start) is bool actor) slot.ActorMotion = actor;
                    slots.Add(slot);
                }
            }
        }

        return slots;
    }

    /// <summary>
    /// Adds a new VFX trigger slot into a MotFile action.
    /// </summary>
    public void AddSlot(MotFile mot, VfxSlot slot, int targetClipIndex = -1)
    {
        if (mot.Clips == null || mot.Clips.Count == 0)
            throw new InvalidOperationException("MotFile has no clips to host VFX tracks.");

        // Detect or choose clip
        if (targetClipIndex < 0 || targetClipIndex >= mot.Clips.Count)
        {
            targetClipIndex = -1;
            for (int c = 0; c < mot.Clips.Count; c++)
            {
                if (mot.Clips[c].ClipEntry?.Tracks.Any(t => t.Name.Contains("VFX") || t.Name.Contains("EPV")) == true)
                {
                    targetClipIndex = c;
                    break;
                }
            }
            if (targetClipIndex == -1) targetClipIndex = mot.Clips.Count - 1;
        }

        var clip = mot.Clips[targetClipIndex];
        if (clip.ClipEntry == null)
            throw new InvalidOperationException("ClipEntry is null on target clip.");

        var templateTrack = clip.ClipEntry.Tracks.FirstOrDefault(t => t.Name.Contains("VFXRangeTrack"));
        ClipTrack newTrack;
        if (templateTrack != null)
        {
            newTrack = CloneTrackWithValues(templateTrack, slot);
        }
        else
        {
            newTrack = CreateDefaultVfxTrack(clip.ClipEntry.Header.version, slot);
        }

        clip.ClipEntry.Tracks.Add(newTrack);
        slot.ClipIndex = targetClipIndex;
        slot.TrackIndex = clip.ClipEntry.Tracks.Count - 1;
        slot.IsModified = true;
    }

    /// <summary>
    /// Updates the properties of an existing VFX track in a MotFile.
    /// </summary>
    public bool UpdateSlot(MotFile mot, VfxSlot slot)
    {
        // Editing a flattened event through the old one-track writer would
        // overwrite every native key. A dedicated key-preserving writer is needed.
        if (slot.NativeMultiKeyTrack) return false;
        if (mot.Clips == null || slot.ClipIndex < 0 || slot.ClipIndex >= mot.Clips.Count) return false;
        var clip = mot.Clips[slot.ClipIndex];
        if (clip.ClipEntry == null || slot.TrackIndex < 0 || slot.TrackIndex >= clip.ClipEntry.Tracks.Count) return false;

        var trk = clip.ClipEntry.Tracks[slot.TrackIndex];
        trk.startFrame = slot.StartFrame;
        trk.endFrame = slot.EndFrame;

        int cid = slot.ContainerId >= 0 ? slot.ContainerId : 10;
        if (slot.EffectId.Contains("-"))
        {
            var parts = slot.EffectId.Split('-');
            if (int.TryParse(parts[0], out int pCid)) cid = pCid;
        }

        foreach (var p in trk.Properties)
        {
            p.Info.startFrame = slot.StartFrame;
            p.Info.endFrame = slot.EndFrame;

            if (p.Keys != null)
            {
                foreach (var k in p.Keys)
                {
                    k.frame = slot.StartFrame;
                    if (k is NoHermiteKey nhk)
                    {
                        if (p.Info.FunctionName == "EffectId" && !string.IsNullOrEmpty(slot.EffectId))
                            nhk.Value = slot.EffectId;
                        else if (p.Info.FunctionName == "DataContainerId")
                            nhk.Value = cid;
                        else if (p.Info.FunctionName == "JointName" && !string.IsNullOrEmpty(slot.JointName))
                            nhk.Value = slot.JointName;
                        else if (p.Info.FunctionName == "InvervalFrame")
                            nhk.Value = slot.IntervalFrame;
                    }
                    else if (k is BoolKey bk && p.Info.FunctionName == "ActorMotion")
                    {
                        bk.value = slot.ActorMotion ? (short)1 : (short)0;
                    }
                }
            }
        }

        slot.IsModified = true;
        return true;
    }

    /// <summary>
    /// Removes a slot from a MotFile.
    /// </summary>
    public bool RemoveSlot(MotFile mot, int clipIndex, int trackIndex)
    {
        if (mot.Clips == null || clipIndex < 0 || clipIndex >= mot.Clips.Count) return false;
        var clip = mot.Clips[clipIndex];
        if (clip.ClipEntry == null || trackIndex < 0 || trackIndex >= clip.ClipEntry.Tracks.Count) return false;

        clip.ClipEntry.Tracks.RemoveAt(trackIndex);
        return true;
    }

    /// <summary>
    /// Removes a slot from a MotFile.
    /// </summary>
    public bool RemoveSlot(MotFile mot, VfxSlot slot) => RemoveSlot(mot, slot.ClipIndex, slot.TrackIndex);

    /// <summary>
    /// Batch shifts start and end frames for a collection of slots.
    /// </summary>
    public void BatchShiftFrames(IEnumerable<VfxSlot> slots, float deltaFrames)
    {
        foreach (var s in slots)
        {
            float duration = Math.Max(1f, s.EndFrame - s.StartFrame);
            s.StartFrame = Math.Max(0f, s.StartFrame + deltaFrames);
            s.EndFrame = s.StartFrame + duration;
            s.IsModified = true;
        }
    }

    /// <summary>
    /// Batch scales duration for a collection of slots.
    /// </summary>
    public void BatchScaleDuration(IEnumerable<VfxSlot> slots, float scaleFactor)
    {
        if (scaleFactor <= 0f) return;
        foreach (var s in slots)
        {
            float duration = Math.Max(1f, s.EndFrame - s.StartFrame);
            s.EndFrame = s.StartFrame + Math.Max(1f, duration * scaleFactor);
            s.IsModified = true;
        }
    }

    /// <summary>
    /// Batch remaps attachment bones for a collection of slots.
    /// </summary>
    public void BatchRemapBones(IEnumerable<VfxSlot> slots, string newBoneName)
    {
        foreach (var s in slots)
        {
            s.JointName = newBoneName;
            s.IsModified = true;
        }
    }

    private static ClipTrack CloneTrackWithValues(ClipTrack template, VfxSlot slot)
    {
        var trk = new ClipTrack(template.Version)
        {
            Name = "via.effect.script.VFXRangeTrack",
            startFrame = slot.StartFrame,
            endFrame = slot.EndFrame,
            pragmataHash = template.pragmataHash,
            nameHash1 = template.nameHash1,
            nameUtf16Hash = template.nameUtf16Hash
        };

        int cid = slot.ContainerId >= 0 ? slot.ContainerId : 10;
        if (slot.EffectId.Contains("-"))
        {
            var parts = slot.EffectId.Split('-');
            if (int.TryParse(parts[0], out int pCid)) cid = pCid;
        }

        foreach (var p in template.Properties)
        {
            var prop = new Property(template.Version)
            {
                Info = {
                    FunctionName = p.Info.FunctionName,
                    DataType = p.Info.DataType,
                    startFrame = slot.StartFrame,
                    endFrame = slot.EndFrame,
                    arrayIndex = p.Info.arrayIndex,
                    speedPointNum = p.Info.speedPointNum,
                    flags = p.Info.flags,
                    KeyType = p.Info.KeyType,
                    nameAsciiHash = p.Info.nameAsciiHash,
                    nameUtf16Hash = p.Info.nameUtf16Hash,
                    wildsNameHash = p.Info.wildsNameHash,
                }
            };

            if (p.Keys != null)
            {
                prop.Keys = new List<KeyBase>();
                foreach (var k in p.Keys)
                {
                    if (k is NoHermiteKey nhk)
                    {
                        object val = nhk.Value;
                        if (p.Info.FunctionName == "EffectId") val = slot.EffectId;
                        else if (p.Info.FunctionName == "DataContainerId") val = cid;
                        else if (p.Info.FunctionName == "JointName" && !string.IsNullOrEmpty(slot.JointName)) val = slot.JointName;
                        else if (p.Info.FunctionName == "InvervalFrame" && slot.IntervalFrame > 0) val = slot.IntervalFrame;

                        var newKey = new NoHermiteKey(template.Version)
                        {
                            PropertyType = p.Info.DataType,
                            frame = slot.StartFrame,
                            interpolation = nhk.interpolation,
                            flags = nhk.flags,
                            unknown = nhk.unknown,
                            Value = val
                        };
                        prop.Keys.Add(newKey);
                    }
                    else if (k is BoolKey bk)
                    {
                        var newKey = new BoolKey(template.Version)
                        {
                            PropertyType = PropertyType.Bool,
                            frame = slot.StartFrame,
                            flags = bk.flags,
                            uknByte = bk.uknByte,
                            value = slot.ActorMotion ? (short)1 : (short)0
                        };
                        prop.Keys.Add(newKey);
                    }
                }
            }
            trk.Properties.Add(prop);
        }

        return trk;
    }

    private static ClipTrack CreateDefaultVfxTrack(ClipVersion version, VfxSlot slot)
    {
        var trk = new ClipTrack(version)
        {
            Name = "via.effect.script.VFXRangeTrack",
            startFrame = slot.StartFrame,
            endFrame = slot.EndFrame
        };

        var pEff = new Property(version)
        {
            Info = { FunctionName = "EffectId", DataType = PropertyType.Str16, KeyType = 3, startFrame = slot.StartFrame, endFrame = slot.EndFrame }
        };
        pEff.Keys = new List<KeyBase> {
            new NoHermiteKey(version) { PropertyType = PropertyType.Str16, frame = slot.StartFrame, Value = slot.EffectId }
        };
        trk.Properties.Add(pEff);

        var pActor = new Property(version)
        {
            Info = { FunctionName = "ActorMotion", DataType = PropertyType.Bool, KeyType = 1, startFrame = slot.StartFrame, endFrame = slot.EndFrame }
        };
        pActor.Keys = new List<KeyBase> {
            new BoolKey(version) { PropertyType = PropertyType.Bool, frame = slot.StartFrame, value = slot.ActorMotion ? (short)1 : (short)0 }
        };
        trk.Properties.Add(pActor);

        var pCont = new Property(version)
        {
            Info = { FunctionName = "DataContainerId", DataType = PropertyType.S32, KeyType = 3, startFrame = slot.StartFrame, endFrame = slot.EndFrame }
        };
        pCont.Keys = new List<KeyBase> {
            new NoHermiteKey(version) { PropertyType = PropertyType.S32, frame = slot.StartFrame, Value = slot.ContainerId }
        };
        trk.Properties.Add(pCont);

        if (!string.IsNullOrEmpty(slot.JointName))
        {
            var pJoint = new Property(version)
            {
                Info = { FunctionName = "JointName", DataType = PropertyType.Str16, KeyType = 3, startFrame = slot.StartFrame, endFrame = slot.EndFrame }
            };
            pJoint.Keys = new List<KeyBase> {
                new NoHermiteKey(version) { PropertyType = PropertyType.Str16, frame = slot.StartFrame, Value = slot.JointName }
            };
            trk.Properties.Add(pJoint);
        }

        return trk;
    }

    // =========================================================================
    // Undoable Command Factories
    // =========================================================================

    public IUndoableCommand CreateAddSlotCommand(MotFile mot, VfxSlot slot, int targetClipIndex = 0)
    {
        return new RelayUndoableCommand(
            $"Add VFX Slot '{slot.EffectId}' @ frame {slot.StartFrame}",
            execute: () => AddSlot(mot, slot, targetClipIndex),
            undo: () => RemoveSlot(mot, slot)
        );
    }

    public IUndoableCommand CreateRemoveSlotCommand(MotFile mot, VfxSlot slot, int targetClipIndex = 0)
    {
        return new RelayUndoableCommand(
            $"Remove VFX Slot '{slot.EffectId}' @ frame {slot.StartFrame}",
            execute: () => RemoveSlot(mot, slot),
            undo: () => AddSlot(mot, slot, targetClipIndex)
        );
    }

    public IUndoableCommand CreateUpdateSlotCommand(MotFile mot, VfxSlot oldSlot, VfxSlot newSlot)
    {
        return new RelayUndoableCommand(
            $"Update VFX Slot '{oldSlot.EffectId}' -> '{newSlot.EffectId}'",
            execute: () => UpdateSlot(mot, newSlot),
            undo: () => UpdateSlot(mot, oldSlot)
        );
    }

    public IUndoableCommand CreateBatchShiftCommand(MotFile mot, IEnumerable<VfxSlot> slots, float deltaFrames)
    {
        var slotList = slots.ToList();
        return new RelayUndoableCommand(
            $"Batch Shift {slotList.Count} slots by {deltaFrames} frames",
            execute: () =>
            {
                BatchShiftFrames(slotList, deltaFrames);
                foreach (var s in slotList) UpdateSlot(mot, s);
            },
            undo: () =>
            {
                BatchShiftFrames(slotList, -deltaFrames);
                foreach (var s in slotList) UpdateSlot(mot, s);
            }
        );
    }

    public IUndoableCommand CreateBatchScaleCommand(MotFile mot, IEnumerable<VfxSlot> slots, float scaleFactor)
    {
        var slotList = slots.ToList();
        var originalStates = slotList.Select(s => (Slot: s, s.StartFrame, s.EndFrame)).ToList();

        return new RelayUndoableCommand(
            $"Batch Scale {slotList.Count} slots by x{scaleFactor:F2}",
            execute: () =>
            {
                BatchScaleDuration(slotList, scaleFactor);
                foreach (var s in slotList) UpdateSlot(mot, s);
            },
            undo: () =>
            {
                foreach (var (slot, start, end) in originalStates)
                {
                    slot.StartFrame = start;
                    slot.EndFrame = end;
                    UpdateSlot(mot, slot);
                }
            }
        );
    }
}
