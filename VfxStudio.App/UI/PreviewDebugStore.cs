using System.Text.Json;
using System.Text.Json.Serialization;

namespace VfxStudio.App.UI;

// Editor-only data. Nothing in this file is written to MOT/MOTLIST or EFX exports.
public sealed class PreviewDebugStore
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool ShowBoneMounts { get; set; }
    public bool ShowPreviewEffects { get; set; }
    public bool ShowMotionNotes { get; set; }
    public bool PreviewEffectsMuted { get; set; }
    public bool PreviewTracksCollapsed { get; set; }
    public bool NativeTracksCollapsed { get; set; }
    public float TimelineHeight { get; set; }
    public List<PreviewEffectBinding> Effects { get; set; } = new();
    public List<MotionNoteOverride> Notes { get; set; } = new();

    [JsonIgnore] public string? BackupPath { get; private set; }
    [JsonIgnore] public string? LoadError { get; private set; }
    [JsonIgnore] public bool CanSave => LoadError == null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static PreviewDebugStore Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            string json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var store = JsonSerializer.Deserialize<PreviewDebugStore>(json, JsonOptions)
                ?? throw new InvalidDataException("预览配置内容为空。");
            store.Effects ??= new();
            store.Notes ??= new();
            int storedVersion = document.RootElement.TryGetProperty(nameof(SchemaVersion), out var version)
                ? version.GetInt32() : 1;
            if (storedVersion > CurrentSchemaVersion)
                throw new InvalidDataException($"预览配置版本 {storedVersion} 高于程序支持的 {CurrentSchemaVersion}，已保留原文件。");
            bool needsMigration = storedVersion < CurrentSchemaVersion ||
                store.Effects.Any(e => e.IdentityVersion == 0) || store.Notes.Any(n => n.IdentityVersion == 0);
            if (needsMigration)
            {
                // Do not mutate even the in-memory legacy document before its bytes are preserved.
                string backup = path + $".before-v2-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.bak";
                File.Copy(path, backup, overwrite: false);
                store.BackupPath = backup;
            }
            store.SchemaVersion = CurrentSchemaVersion;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in store.Effects)
            {
                // Repeated resources are separate bindings. Repair only missing/colliding instance IDs.
                if (string.IsNullOrWhiteSpace(binding.Id) || !ids.Add(binding.Id))
                {
                    binding.Id = Guid.NewGuid().ToString("N");
                    ids.Add(binding.Id);
                }
            }
            if (!float.IsFinite(store.TimelineHeight) || store.TimelineHeight < 0f) store.TimelineHeight = 0f;
            return store;
        }
        catch (Exception ex)
        {
            // A parse or backup failure must never let a subsequent settings click overwrite user data.
            Console.WriteLine($"[PreviewDebugStore] Could not read {path}: {ex.Message}");
            return new() { LoadError = $"无法读取或备份预览配置，原文件未修改：{ex.Message}" };
        }
    }

    public void Save(string path)
    {
        if (!CanSave) throw new InvalidOperationException(LoadError);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, fullPath, true);
    }

    /// <summary>
    /// Called after reading a MOTLIST, before applying notes or displaying its bindings.
    /// Legacy indices are used only here to recover the source name of a repeated MotionId.
    /// Matching after migration never depends on list position.
    /// </summary>
    public PreviewCatalogReconcileResult ReconcileMotionCatalog(string listPath, string weaponCode,
        IReadOnlyList<PreviewMotionDescriptor> motions)
    {
        bool changed = false;
        var diagnostics = new List<string>();
        foreach (var entry in Effects.Cast<PreviewMotionReference>().Concat(Notes))
        {
            if (!SameSource(listPath, weaponCode, entry.MotlistPath, entry.WeaponCode)) continue;
            var candidates = motions.Where(m => m.MotionId == entry.MotionId).ToList();
            if (entry.IdentityVersion == 0)
            {
                PreviewMotionDescriptor? migrationTarget = candidates.Count == 1 ? candidates[0] :
                    candidates.SingleOrDefaultSafe(m => m.Index == entry.MotionIndex);
                if (migrationTarget != null && IsUnambiguous(migrationTarget, motions))
                {
                    SetIdentity(entry, listPath, weaponCode, migrationTarget);
                    changed = true;
                }
            }
            var matched = motions.Where(m => MatchesMotion(listPath, weaponCode, m, entry)).ToList();
            if (entry.IdentityVersion == 0 || matched.Count != 1 || !IsUnambiguous(matched[0], motions))
            {
                entry.IdentityWarning = $"动作身份无法唯一确认：MotionId {entry.MotionId}，名称“{entry.MotionName}”，旧索引 {entry.MotionIndex}。绑定已保留，未分配给其他动作。";
                diagnostics.Add(entry.IdentityWarning);
                continue;
            }
            entry.IdentityWarning = null;
            if (entry is PreviewEffectBinding binding)
            {
                var range = ClampFrameRange(binding.StartFrame, binding.EndFrame, matched[0].FrameCount);
                if (range.Changed)
                {
                    binding.StartFrame = range.StartFrame;
                    binding.EndFrame = range.EndFrame;
                    binding.FrameRangeWarning = range.Message;
                    diagnostics.Add($"{binding.Id}: {range.Message}");
                    changed = true;
                }
                if (binding.LastMotionFrameCount != matched[0].FrameCount)
                {
                    binding.LastMotionFrameCount = matched[0].FrameCount;
                    changed = true;
                }
            }
        }
        return new(changed, diagnostics);
    }

    public List<PreviewEffectBinding> GetBindings(string listPath, string weaponCode,
        PreviewMotionDescriptor motion, IReadOnlyList<PreviewMotionDescriptor> motions) =>
        IsUnambiguous(motion, motions) ? Effects.Where(e => MatchesMotion(listPath, weaponCode, motion, e)).ToList() : new();

    public MotionNoteOverride? GetNote(string listPath, string weaponCode,
        PreviewMotionDescriptor motion, IReadOnlyList<PreviewMotionDescriptor> motions) =>
        IsUnambiguous(motion, motions) ? Notes.LastOrDefault(e => MatchesMotion(listPath, weaponCode, motion, e)) : null;

    public PreviewEffectBinding CreateBinding(string listPath, string weaponCode,
        PreviewMotionDescriptor motion, IReadOnlyList<PreviewMotionDescriptor> motions, string efxPath)
    {
        if (!IsUnambiguous(motion, motions))
            throw new InvalidOperationException("该动作的 MotionId、名称与去歧义标识重复，无法安全绑定。请先核对源动作身份。");
        var binding = new PreviewEffectBinding { EfxPath = Path.GetFullPath(efxPath),
            StartFrame = 0f, EndFrame = ValidDuration(motion.FrameCount), LastMotionFrameCount = motion.FrameCount };
        SetIdentity(binding, listPath, weaponCode, motion);
        Effects.Add(binding);
        return binding;
    }

    public MotionNoteOverride SetNote(string listPath, string weaponCode,
        PreviewMotionDescriptor motion, IReadOnlyList<PreviewMotionDescriptor> motions, string text)
    {
        if (!IsUnambiguous(motion, motions))
            throw new InvalidOperationException("动作身份重复，无法安全保存备注。");
        var note = GetNote(listPath, weaponCode, motion, motions);
        if (note == null)
        {
            note = new MotionNoteOverride();
            SetIdentity(note, listPath, weaponCode, motion);
            Notes.Add(note);
        }
        note.Text = text;
        return note;
    }

    public static void SetIdentity(PreviewMotionReference entry, string listPath, string weaponCode,
        PreviewMotionDescriptor motion)
    {
        entry.MotlistPath = Path.GetFullPath(listPath);
        entry.WeaponCode = weaponCode;
        entry.MotionIndex = motion.Index; // migration/history only
        entry.MotionId = motion.MotionId;
        entry.MotionName = motion.Name;
        entry.MotionDiscriminator = motion.Discriminator;
        entry.IdentityVersion = 1;
        entry.IdentityWarning = null;
    }

    public static bool MatchesMotion(string listPath, string weaponCode, PreviewMotionDescriptor motion,
        PreviewMotionReference entry) => entry.IdentityVersion > 0 &&
        SameSource(listPath, weaponCode, entry.MotlistPath, entry.WeaponCode) &&
        entry.MotionId == motion.MotionId && string.Equals(entry.MotionName, motion.Name, StringComparison.Ordinal) &&
        string.Equals(entry.MotionDiscriminator, motion.Discriminator, StringComparison.Ordinal);

    public static bool IsUnambiguous(PreviewMotionDescriptor motion, IReadOnlyList<PreviewMotionDescriptor> motions) =>
        motions.Count(m => m.MotionId == motion.MotionId && m.Name == motion.Name &&
            m.Discriminator == motion.Discriminator) == 1;

    // Retained temporarily for old callers; new UI must use the descriptor overload above.
    public static bool MatchesMotion(string listPath, string weaponCode, int motionIndex,
        string entryPath, string entryWeapon, int entryIndex) =>
        motionIndex == entryIndex && SameSource(listPath, weaponCode, entryPath, entryWeapon);

    public static bool SameSource(string listPath, string weaponCode, string entryPath, string entryWeapon)
    {
        if (string.IsNullOrWhiteSpace(listPath) || string.IsNullOrWhiteSpace(entryPath) ||
            !string.Equals(weaponCode, entryWeapon, StringComparison.OrdinalIgnoreCase)) return false;
        try { return string.Equals(Path.GetFullPath(listPath), Path.GetFullPath(entryPath), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static PreviewFrameRange ClampFrameRange(float startFrame, float endFrame, float duration)
    {
        float maximum = ValidDuration(duration);
        float start = float.IsFinite(startFrame) ? Math.Clamp(startFrame, 0f, maximum) : 0f;
        float end = float.IsFinite(endFrame) ? Math.Clamp(endFrame, 0f, maximum) : maximum;
        if (end < start) end = start;
        bool changed = start != startFrame || end != endFrame;
        return new(start, end, changed, changed
            ? $"帧范围 {startFrame:0.###}–{endFrame:0.###} 已校正为 {start:0.###}–{end:0.###}（动作长度 {maximum:0.###}）。" : null);
    }

    private static float ValidDuration(float duration) => float.IsFinite(duration) ? Math.Max(0f, duration) : 0f;
}

public sealed record PreviewMotionDescriptor(int Index, int MotionId, string Name, float FrameCount, string Discriminator = "");
public sealed record PreviewCatalogReconcileResult(bool Changed, IReadOnlyList<string> Diagnostics);
public readonly record struct PreviewFrameRange(float StartFrame, float EndFrame, bool Changed, string? Message);

public abstract class PreviewMotionReference
{
    public string MotlistPath { get; set; } = string.Empty;
    public string WeaponCode { get; set; } = string.Empty;
    public int MotionIndex { get; set; }
    public int MotionId { get; set; }
    public string MotionName { get; set; } = string.Empty;
    public string MotionDiscriminator { get; set; } = string.Empty;
    public int IdentityVersion { get; set; }
    [JsonIgnore] public string? IdentityWarning { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreviewEffectEndPolicy
{
    HardStop,
    // Persisted intent only. A renderer must explicitly advertise/implement natural-end support.
    NaturalEnd
}

public sealed class PreviewEffectBinding : PreviewMotionReference
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string EfxPath { get; set; } = string.Empty;
    public float StartFrame { get; set; }
    public float EndFrame { get; set; }
    public bool Enabled { get; set; } = true;
    public string MountBoneName { get; set; } = string.Empty;
    public PreviewEffectEndPolicy EndPolicy { get; set; } = PreviewEffectEndPolicy.HardStop;
    public float LastMotionFrameCount { get; set; }
    public string? FrameRangeWarning { get; set; }
    public PreviewEffectBinding Clone() => (PreviewEffectBinding)MemberwiseClone();
}

public sealed class MotionNoteOverride : PreviewMotionReference
{
    public string Text { get; set; } = string.Empty;
    public MotionNoteOverride Clone() => (MotionNoteOverride)MemberwiseClone();
}

internal static class PreviewMotionEnumeration
{
    internal static T? SingleOrDefaultSafe<T>(this IEnumerable<T> source, Func<T, bool> predicate) where T : class
    {
        T? found = null;
        foreach (var item in source.Where(predicate))
        {
            if (found != null) return null;
            found = item;
        }
        return found;
    }
}
