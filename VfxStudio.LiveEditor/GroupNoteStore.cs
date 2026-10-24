using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReeLib;

namespace VfxStudio.LiveEditor;

// Editor metadata only. No value held here is ever assigned to an EFX model.
internal sealed class GroupNoteStore
{
    private const int MaximumStoreBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string path;
    private Dictionary<string, StoredNote> notes = new(StringComparer.Ordinal);
    internal string? Warning { get; private set; }

    internal sealed record Identity(int Index, string NativeName, uint NameHashUtf16, uint NameHashUtf8, int[] Members);
    private sealed record StoredNote(string Key, string ResourceKey, Identity Group, string Note, string UpdatedAtUtc);
    private sealed record StoreFile(int SchemaVersion, StoredNote[] Entries);

    internal GroupNoteStore(string mailbox)
    {
        path = Path.Combine(Path.GetFullPath(mailbox), "group-notes.json");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumStoreBytes) throw new InvalidDataException("备注文件超过 4 MiB。");
            var file = JsonSerializer.Deserialize<StoreFile>(stream, Host.Json);
            if (file == null || file.SchemaVersion != 1 || file.Entries == null)
                throw new InvalidDataException("备注文件格式无效。");
            var loaded = new Dictionary<string, StoredNote>(StringComparer.Ordinal);
            foreach (var entry in file.Entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.ResourceKey) || entry.Group == null ||
                    entry.Group.Index < 0 || entry.Group.NativeName == null || entry.Group.Members == null ||
                    entry.Key != Key(entry.ResourceKey, entry.Group) || string.IsNullOrEmpty(entry.Note))
                    throw new InvalidDataException("备注文件中包含无效身份。");
                ValidateNote(entry.Note);
                if (!loaded.TryAdd(entry.Key, entry)) throw new InvalidDataException("备注文件中包含重复身份。");
            }
            notes = loaded;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex)
        {
            Warning = "分组备注文件无法读取，已禁止覆盖；请保留该文件后检查：" + ex.GetBaseException().Message;
        }
    }

    internal static string ResourceKey(string kind, string source, CacheSourceDescriptor? cache, JsonObject? game)
    {
        if (kind == "game_files")
            return "game:" + ResourceResolver.Normalize(EditorEngine.Text(game!, "sharedTargetPath")).ToLowerInvariant();
        if (kind == "game_cache")
            return "game:" + ResourceResolver.Normalize(cache!.SharedTargetPath).ToLowerInvariant();
        return "local:" + Path.GetFullPath(source).Replace('\\', '/').ToLowerInvariant();
    }

    internal static Identity Identify(EfxFile file, int index)
    {
        if (index < 0 || index >= file.EffectGroups.Count) throw new ArgumentOutOfRangeException(nameof(index), "分组编号无效。");
        var group = file.EffectGroups[index];
        return new(index, group.groupName, group.conditionalEffectGroupNameHashUTF16,
            group.conditionalEffectGroupNameHashUTF8,
            (group.efxEntryIndexes ?? []).Select(position => file.Entries[position].index).ToArray());
    }

    internal string Get(string resource, Identity identity, out string? warning)
    {
        warning = Warning;
        if (!notes.TryGetValue(Key(resource, identity), out var entry)) return "";
        if (!CompatibleMembers(entry.Group.Members, identity.Members))
        {
            warning = "该组的成员身份或顺序已改变，未自动套用旧备注。";
            return "";
        }
        return entry.Note;
    }

    internal void Set(string resource, Identity identity, string note)
    {
        if (Warning != null) throw new IOException(Warning);
        ValidateNote(note);
        string key = Key(resource, identity);
        var candidate = new Dictionary<string, StoredNote>(notes, StringComparer.Ordinal);
        if (note.Length == 0) candidate.Remove(key);
        else candidate[key] = new(key, resource, identity, note, DateTimeOffset.UtcNow.ToString("O"));
        var file = new StoreFile(1, candidate.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray());
        if (JsonSerializer.SerializeToUtf8Bytes(file, Host.Json).Length > MaximumStoreBytes)
            throw new InvalidDataException("备注文件超过 4 MiB；未保存本次备注。");
        // AtomicJson stages in the same directory then replaces the destination.
        // If either step fails the in-memory snapshot remains unchanged.
        Host.AtomicJson(path, file);
        notes = candidate;
    }

    internal static void ValidateNote(string note)
    {
        if (note.Any(char.IsControl)) throw new ArgumentException("分组备注必须为单行文字，不能包含换行、制表符或控制字符。");
        int length;
        try { length = StrictUtf8.GetByteCount(note); }
        catch (EncoderFallbackException) { throw new ArgumentException("分组备注包含无效 Unicode 字符。"); }
        if (length > 4096) throw new ArgumentException("分组备注最多为 4096 个 UTF-8 字节；没有截断或保存本次输入。");
    }

    private static string Key(string resource, Identity identity) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { resource, identity.Index, identity.NativeName, identity.NameHashUtf16, identity.NameHashUtf8 }, Host.Json)));

    private static bool CompatibleMembers(int[] previous, int[] current)
    {
        // Entry IDs are structural identities. Names, attribute types/counts and
        // parameter values are deliberately absent. Insertions/deletions may
        // preserve a group, but an unrelated/reordered membership must not
        // silently inherit a note belonging to a different group.
        if (previous.Length == 0 || current.Length == 0) return true;
        var common = previous.Intersect(current).ToHashSet();
        return common.Count > 0 && previous.Where(common.Contains)
            .SequenceEqual(current.Where(common.Contains));
    }
}
