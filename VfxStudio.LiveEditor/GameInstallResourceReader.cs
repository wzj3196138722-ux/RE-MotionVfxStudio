using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReeLib;

namespace VfxStudio.LiveEditor;

// Installed game files are a separate source from the game's live memory.
// No project catalog, basename matching, or shared-target substitution is used.
internal sealed record GameFileSnapshot(CacheSourceDescriptor? Source, string OriginKind, string OriginPath,
    string NativePath, string ContentHash, string ReadAtUtc, string LoaderEvidencePath, byte[] Content)
{
    internal JsonObject Provenance()
    {
        var result = Source == null ? new JsonObject() : JsonSerializer.SerializeToNode(Source, Host.Json)!.AsObject();
        result["originKind"] = OriginKind;
        result["originPath"] = OriginPath;
        result["nativePath"] = NativePath;
        result["contentHash"] = ContentHash;
        result["readAtUtc"] = ReadAtUtc;
        result["loaderEvidencePath"] = LoaderEvidencePath;
        result["contentScope"] = "installed_game_files_not_memory_snapshot";
        return result;
    }
}

internal sealed class GameInstallResourceReader(Options options)
{
    private const int MaximumBytes = 128 * 1024 * 1024;

    internal GameFileSnapshot Read(CacheSourceDescriptor source)
    {
        string logical = ResourceResolver.Normalize(source.ActualPath);
        var version = Regex.Match(source.ActualPath.Replace('\\', '/'), @"\.efx\.(\d+)$", RegexOptions.IgnoreCase);
        if (version.Success && version.Groups[1].Value != "5571972")
            throw Error("game_resource_version_unsupported", "捕获资源的 EFX 版本不受支持；没有改读其他版本。");
        string native = "natives/stm/" + logical + ".5571972";
        return ReadNative(native, source);
    }

    // Callers must first establish runtime provenance. This reader only resolves
    // the exact full native path through the installed loader's precedence.
    internal GameFileSnapshot ReadActionResource(string logical, string kind)
    {
        string version = kind switch { "efx" => "5571972", "pfb" => "18", "motlist" => "992", "mot" => "932", _ => throw new ArgumentException("Unsupported action resource.") };
        logical = logical.Replace('\\', '/').Trim();
        if (logical.EndsWith("." + version, StringComparison.Ordinal)) logical = logical[..^(version.Length + 1)];
        if (!logical.EndsWith("." + kind, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(logical, @"\A(?:art|motion)/[\w ./-]+\z", RegexOptions.IgnoreCase) || logical.Split('/').Any(p => p is ".." or "." or ""))
            throw new ArgumentException("动作资源路径必须是经过核实的完整游戏路径。");
        return ReadNative("natives/stm/" + logical.ToLowerInvariant() + "." + version, null);
    }

    internal GameFileSnapshot ReadPrefabResource(string logical)
    {
        logical = logical.Replace('\\', '/').Trim();
        if (logical.EndsWith(".pfb.18", StringComparison.OrdinalIgnoreCase)) logical = logical[..^3];
        if (!Regex.IsMatch(logical, @"\A(?:art|gamedesign)/[\w ./-]+\.pfb\z", RegexOptions.IgnoreCase) ||
            logical.Split('/').Any(p => p is ".." or "." or ""))
            throw new ArgumentException("PFB 资源路径必须为 art/ 或 gamedesign/ 开头的完整游戏路径。");
        return ReadNative("natives/stm/" + logical.ToLowerInvariant() + ".18", null);
    }

    private GameFileSnapshot ReadNative(string native, CacheSourceDescriptor? source)
    {
        if (string.IsNullOrWhiteSpace(options.GameRoot) || !Directory.Exists(options.GameRoot))
            throw Error("game_root_unavailable", "未配置有效的游戏安装目录；没有读取项目素材。");
        string root = Path.GetFullPath(options.GameRoot);
        string configPath = Path.Combine(root, "re2_fw_config.txt");
        var config = ReadConfig(configPath);
        string loose = Path.GetFullPath(Path.Combine(root, native.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(loose))
        {
            if (!config.TryGetValue("LooseFileLoader_Enabled", out bool enabled))
                throw Error("game_loose_loader_unknown", "已找到精确替换文件，但无法确认安装配置是否启用散文件加载；未打开其他来源。");
            if (enabled)
            {
                EnsureInsideRoot(root, loose);
                var info = new FileInfo(loose);
                if (info.Length <= 0 || info.Length > MaximumBytes)
                    throw Error("game_resource_invalid", "游戏替换 EFX 的大小无效；现有文档保持不变。");
                return Snapshot(source, "loose_file", loose, native, configPath, File.ReadAllBytes(loose));
            }
        }

        string modDirectory = Path.Combine(root, "pak_mods");
        if (config.GetValueOrDefault("IntegrityCheckBypass_LoadPakDirectory") && Directory.Exists(modDirectory) &&
            Directory.EnumerateFiles(modDirectory, "*.pak", SearchOption.AllDirectories).Any())
            throw Error("game_pak_priority_unverified", "安装目录启用了 pak_mods 自定义包；其实际注入顺序尚未受本读取器验证，未猜测来源或打开项目素材。");

        string logPath = Path.Combine(root, "re2_framework_log.txt");
        var loaded = ReadLoadedPakNames(logPath);
        if (loaded.Count == 0)
            throw Error("game_pak_load_evidence_missing", "未找到游戏安装目录中可核对的 PAK 加载日志；请运行游戏及 REFramework 后重试。没有读取项目素材。");
        // Keep manifest-bearing mod patches. PakReader consumes this exact
        // PakUtils order in reverse: latest sub patch, sub base, main patches,
        // then main base. Only packages observed by this installed loader count.
        var archives = PakUtils.ScanPakFiles(root, ignoreModPaks: false)
            .Where(path => loaded.Contains(Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Reverse().ToArray();
        if (archives.Length == 0)
            throw Error("game_pak_load_evidence_missing", "加载日志中的 PAK 与安装目录文件不匹配；没有读取项目素材。");
        ulong hash = PakUtils.GetFilepathHash(native);
        var expected = new Dictionary<ulong, string> { [hash] = native };
        foreach (string archive in archives)
        {
            EnsureInsideRoot(root, archive);
            try
            {
                using var pak = new PakFile { filepath = archive };
                pak.ReadContents(archive, expected);
                if (pak.Entries.Count == 0) continue;
                if (pak.Entries.Count != 1)
                    throw new InvalidDataException("The exact path hash occurs more than once in this package.");
                var entry = pak.Entries[0];
                if (entry.decompressedSize <= 0 || entry.decompressedSize > MaximumBytes ||
                    entry.compressedSize < 0 || entry.compressedSize > MaximumBytes)
                    throw new InvalidDataException("EFX package entry exceeds the supported size limit.");
                using var content = new MemoryStream((int)entry.decompressedSize);
                pak.Read(entry, content);
                if (content.Length != entry.decompressedSize)
                    throw new InvalidDataException("Package entry output length does not match its declared size.");
                return Snapshot(source, "pak_entry", Path.GetFullPath(archive), native, logPath, content.ToArray());
            }
            catch (Exception ex) when (ex is not CacheSourceException)
            {
                // A corrupt/unreadable higher-priority package must never cause
                // silently opening an older copy from a lower-priority package.
                throw Error("game_pak_read_failed", "无法可靠读取已加载 PAK：" + Path.GetFileName(archive) + "；未改用旧包或项目素材。" + ex.Message);
            }
        }
        throw Error("game_resource_not_found", "已按捕获的完整路径检查启用的游戏替换文件及已加载 PAK，未找到：" + native + "。没有使用同名文件或项目素材。");
    }

    private static GameFileSnapshot Snapshot(CacheSourceDescriptor? source, string kind, string path, string native, string evidence, byte[] bytes) =>
        new(source, kind, path, native, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow.ToString("O"), evidence, bytes);

    private static Dictionary<string, bool> ReadConfig(string path)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (string line in ReadSharedLines(path))
        {
            int equals = line.IndexOf('=');
            if (equals > 0 && bool.TryParse(line[(equals + 1)..].Trim(), out bool value))
                result[line[..equals].Trim()] = value;
        }
        return result;
    }

    private static HashSet<string> ReadLoadedPakNames(string path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        string? pending = null;
        foreach (string line in ReadSharedLines(path))
        {
            const string marker = "[IntegrityCheckBypass]: Pak name: ";
            int at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0) pending = line[(at + marker.Length)..].Trim().Replace('\\', '/');
            // Pak-name logs include attempted nonexistent patch slots. The
            // subsequent flags/decryption message plus a physical file narrows
            // this to an archive the loader actually opened.
            if (pending != null && (line.Contains("Pak is encrypted", StringComparison.Ordinal) ||
                line.Contains("Unencrypted", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(pending);
                pending = null;
            }
        }
        return result;
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is string line) yield return line;
    }

    private static void EnsureInsideRoot(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw Error("game_resource_path_invalid", "游戏文件路径超出安装目录。");
        string current = root;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw Error("game_resource_path_invalid", "游戏文件路径经过链接或目录联接，无法确认实际安装来源：" + current);
        }
    }

    private static CacheSourceException Error(string code, string message) => new(code, message);
}
