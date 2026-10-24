using System.Text.RegularExpressions;
using ReeLib;
using ReeLib.Mot;
using ReeLib.Motlist;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

// Read-only local files have their own view. They must never replace the open
// EFX, discard its undo history, or acquire a captured game-resource binding.
internal static class LocalMotionReader
{
    internal static object Open(string path)
    {
        path = Path.GetFullPath(path);
        var match = Regex.Match(path, @"\.(motlist|mot)(?:\.(\d+))?$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new ArgumentException("请选择 .mot / .motlist 动作文件（可带版本号）。");
        if (new FileInfo(path).Length is < 16 or > 256 * 1024 * 1024)
            throw new InvalidDataException("动作文件为空、已截断或超过 256 MiB 限制。");
        byte[] bytes = File.ReadAllBytes(path);
        string kind = match.Groups[1].Value.ToLowerInvariant();
        int version = BitConverter.ToInt32(bytes);
        uint magic = BitConverter.ToUInt32(bytes, 4);
        if (magic != (kind == "mot" ? MotFile.Magic : MotlistFile.Magic))
            throw new InvalidDataException("文件头与动作扩展名不符。");
        if (match.Groups[2].Success && match.Groups[2].Value != version.ToString())
            throw new InvalidDataException("文件版本后缀与内部版本不一致。");
        if (kind == "mot" ? !Enum.IsDefined((MotVersion)version) : !Enum.IsDefined((MotlistVersion)version))
            throw new InvalidDataException("暂不支持此动作格式版本：" + version);
        using var handler = new FileHandler(new MemoryStream(bytes, writable: false), path);
        var motions = new List<object>();
        var manager = new MotionVfxSlotManager();
        object Describe(MotFileBase? value, uint? motionId, List<MotClip>? overrides = null)
        {
            var mot = value as MotFile;
            var slots = new List<object>();
            int counter = 0;
            void AddSlots(MotFile source, string origin)
            {
                foreach (var slot in manager.ExtractSlotsFromMot(source, ref counter))
                    slots.Add(new { slot.EffectId, slot.ContainerId, slot.ElementId, slot.StartFrame, slot.EndFrame,
                        slot.JointName, slot.TrackName, slot.ClipIndex, slot.TrackIndex, slot.IntervalFrame, slot.ActorMotion, origin });
            }
            if (mot != null) AddSlots(mot, "MOT");
            if (overrides is { Count: > 0 })
            {
                // Additional clip tracks belong to this MOTLIST entry, not to
                // the shared MOT. Keep them distinct and leave both unmodified.
                var extra = new MotFile(handler);
                extra.Clips.AddRange(overrides);
                AddSlots(extra, "MOTLIST 追加调用");
            }
            return new { motionId, name = value?.Name ?? "空动作槽位", frameCount = mot?.Header.frameCount,
                frameRate = mot?.Header.FrameRate, clipCount = (mot?.Clips.Count ?? 0) + (overrides?.Count ?? 0),
                externalPath = (value as MotFileLink)?.Path, slots,
                status = value == null ? "此槽位没有内嵌动作。" : value is MotTreeFile ? "这是混合动作树；不包含可直接展开的 MOT 帧轨道。" : null };
        }
        string name; string? basePath = null;
        if (kind == "mot")
        {
            using var mot = new MotFile(handler);
            if (!mot.Read()) throw new InvalidDataException("读取 MOT 失败。");
            name = mot.Name;
            motions.Add(Describe(mot, null)); // A standalone MOT does not encode a bank's Motion ID.
        }
        else
        {
            using var list = new MotlistFile(handler);
            if (!list.Read()) throw new InvalidDataException("读取 MOTLIST 失败。");
            name = list.Name; basePath = list.Header.BaseMotListPath;
            foreach (var item in list.Motions) motions.Add(Describe(item.MotFile, item.motNumber, item.OverrideClips));
        }
        return new { path, name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name, kind, version, basePath, motions };
    }
}
