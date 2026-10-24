using System.Globalization;
using System.Text.Json.Nodes;

namespace VfxStudio.LiveEditor;

// This describes a retained observation, not proof that an object is still
// alive or that any particular bytes came from the game's current cache.
internal sealed record CacheSourceDescriptor(string SourceKind, string SourceId, string EventId, long SceneEpoch,
    string ActualPath, string PlayerIdentity, string ActualHolderIdentity, string SharedTargetPath,
    bool LocalConfirmed, string Availability, bool CurrentPlayerVerified, bool CacheBytesAvailable)
{
    internal static CacheSourceDescriptor Parse(JsonObject request)
    {
        if (request["sourceDescriptor"] is not JsonObject source)
            throw new CacheSourceException("cache_source_invalid", "缺少角色实际创建实例的缓存来源描述；未打开本地文件。");
        string Text(string key, int max = 512)
        {
            if (source[key] is not JsonValue node || !node.TryGetValue<string>(out var value) ||
                string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
                throw new CacheSourceException("cache_source_invalid", "缓存来源字段无效：" + key + "；未打开本地文件。");
            return value;
        }
        bool Flag(string key)
        {
            if (source[key] is not JsonValue node || !node.TryGetValue<bool>(out var value))
                throw new CacheSourceException("cache_source_invalid", "缓存来源标记无效：" + key + "；未打开本地文件。");
            return value;
        }
        string Identity(string key)
        {
            string value = Text(key, 64);
            bool hexadecimal = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!ulong.TryParse(hexadecimal ? value[2..] : value, hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                CultureInfo.InvariantCulture, out var numeric) || numeric == 0)
                throw new CacheSourceException("cache_source_invalid", "缓存来源对象标识无效：" + key + "；未打开本地文件。");
            return value;
        }
        if (Text("sourceKind") != "actual_effect_player_holder" || !Flag("localConfirmed"))
            throw new CacheSourceException("cache_source_invalid", "必须选择已确认由角色实际创建的特效缓存来源；未打开本地文件。");
        if (!TryEpoch(source["sceneEpoch"], out var epoch))
            throw new CacheSourceException("cache_source_invalid", "缓存来源场景编号无效；未打开本地文件。");
        string actual = Text("actualPath", 4096), shared = Text("sharedTargetPath", 4096);
        try { ResourceResolver.Normalize(actual); ResourceResolver.Normalize(shared); }
        catch (ArgumentException) { throw new CacheSourceException("cache_source_invalid", "缓存来源路径无效；未打开本地文件。"); }
        return new("actual_effect_player_holder", Text("sourceId"), Text("eventId"), epoch,
            actual, Identity("playerIdentity"), Identity("actualHolderIdentity"), shared, true,
            Text("availability"), Flag("currentPlayerVerified"), Flag("cacheBytesAvailable"));
    }

    internal static long CurrentEpoch(JsonObject request, CacheSourceDescriptor source)
    {
        if (!TryEpoch(request["currentSceneEpoch"], out var epoch))
            throw new CacheSourceException("cache_source_invalid", "缺少有效的当前场景编号；未打开本地文件。");
        if (epoch != source.SceneEpoch)
            throw new CacheSourceException("cache_source_expired", "该缓存来源属于之前的场景，请重新记录角色动作；未打开本地文件。");
        // Equality only rejects an explicitly stale observation. The reader
        // must independently validate the live process, scene and holder.
        return epoch;
    }

    private static bool TryEpoch(JsonNode? node, out long epoch)
    {
        epoch = 0;
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<long>(out epoch)) return epoch > 0;
        if (value.TryGetValue<int>(out var narrow)) { epoch = narrow; return epoch > 0; }
        return false;
    }
}

internal sealed class CacheSourceException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

// Implementations may return this only after independently verifying the live
// process, scene epoch, player/holder identities and the complete cached EFX
// payload. Request flags, logical paths and files on disk are not that proof.
internal sealed record VerifiedCacheSnapshot(CacheSourceDescriptor Source, long CurrentSceneEpoch,
    string ProcessIdentity, string ReaderName, byte[] Content);

internal interface IGameCacheResourceReader
{
    VerifiedCacheSnapshot Read(CacheSourceDescriptor source, long expectedSceneEpoch);
}

internal sealed class UnavailableGameCacheResourceReader : IGameCacheResourceReader
{
    public VerifiedCacheSnapshot Read(CacheSourceDescriptor source, long expectedSceneEpoch) =>
        throw new CacheSourceException("cache_reader_unavailable", "尚未取得当前缓存 EFX 内容，未打开本地文件。当前只保留了实例来源记录；缓存读取器尚未完成验证，现有编辑文档保持不变。");
}
