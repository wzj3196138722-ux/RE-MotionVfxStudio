using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReeLib;
using ReeLib.Common;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

// Public aliases are editor identifiers. Only immutable, editor-owned paths
// are materialized for the game's loader. A user alias never overwrites a mod.
internal sealed class PrecacheStore
{
    internal sealed record Entry(string Id, string Alias, string Kind, string SourcePath,
        string Version, int Revision, string Hash, string Blob, string EnginePath, string[] Dependencies);
    private sealed record Catalog(int Schema, Entry[] Entries);
    private readonly Options options;
    private readonly string root, manifest;
    private List<Entry> entries;
    private static readonly Regex Suffix = new(@"\.(mesh|mdf2?|uvs|tex|pfb|motlist|mot|efx)\.(\d+)$", RegexOptions.IgnoreCase);
    private static readonly Regex Reference = new(@"(?:art|animation|motion|system|character|environment|vfx)/[^\x00\r\n""<>|?*]{1,450}?\.(?:mesh|mdf2?|uvs|tex|pfb|motlist|mot|efx)(?=\x00|$)", RegexOptions.IgnoreCase);

    internal PrecacheStore(Options options)
    {
        this.options = options;
        root = Path.Combine(options.Mailbox, "precache");
        manifest = Path.Combine(root, "catalog.json");
        if (!File.Exists(manifest)) { entries = []; return; }
        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(manifest), Host.Json)
            ?? throw new InvalidDataException("预缓存清单损坏，原文件已保留。");
        if (catalog.Schema != 1) throw new InvalidDataException("预缓存清单版本不兼容。");
        entries = catalog.Entries.ToList();
        if (entries.Select(e => Normalize(e.Alias)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count ||
            entries.Any(e => !Regex.IsMatch(e.Id, "^[a-f0-9]{32}$") || e.Revision < 1 ||
                !Regex.IsMatch(e.Hash, "^[A-F0-9]{64}$") || !Regex.IsMatch(e.Version, "^\\d+$")))
            throw new InvalidDataException("预缓存清单身份校验失败，未覆盖。");
        foreach (var e in entries) _ = BlobPath(e);
    }
    internal Entry[] List() => entries.ToArray();
    internal static string Normalize(string path)
    {
        path = path.Trim().Replace('\\', '/');
        path = Regex.Replace(path, @"\.(\d+)$", "");
        if (!path.StartsWith("art/", StringComparison.OrdinalIgnoreCase) || path.Length > 480 ||
            path.Any(c => char.IsControl(c) || ":*?\"<>|".Contains(c)) ||
            path.Split('/').Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".." ||
                s.EndsWith('.') || s.EndsWith(' ') || Regex.IsMatch(s, @"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\.|$)", RegexOptions.IgnoreCase)) ||
            !Regex.IsMatch(path, @"\.(mesh|mdf2?|uvs|tex|pfb|motlist|mot|efx)$", RegexOptions.IgnoreCase))
            throw new ArgumentException("预缓存路径须以 art/ 开始，以资源扩展名结尾，不含本地盘符、版本号或父目录跳转。");
        return "art/" + path[4..];
    }
    internal Entry Import(string sourcePath, string? requestedAlias, string? reloadId = null)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        var suffix = Suffix.Match(sourcePath);
        if (!suffix.Success) throw new ArgumentException("请选择已转换的游戏文件：mesh/mdf2/uvs/tex/pfb/mot/motlist/efx，须带版本号。");
        string kind = suffix.Groups[1].Value.ToLowerInvariant(), version = suffix.Groups[2].Value;
        if (kind == "mdf") kind = "mdf2";
        var info = new FileInfo(sourcePath);
        if (!info.Exists || info.Length is < 8 or > 512 * 1024 * 1024) throw new InvalidDataException("文件不存在、过小或超过 512 MiB。");
        byte[] content = File.ReadAllBytes(sourcePath);
        CheckMagic(kind, content);
        Entry? old = reloadId == null ? null : entries.SingleOrDefault(e => e.Id == reloadId)
            ?? throw new ArgumentException("预缓存条目已不存在。");
        string id = old?.Id ?? Guid.NewGuid().ToString("N");
        string stem = Path.GetFileName(sourcePath)[..^suffix.Length];
        stem = Regex.Replace(stem, @"[^\p{L}\p{N}_-]", "_");
        string alias = Normalize(old?.Alias ?? requestedAlias ?? $"art/MotionVfxLive/Library/{kind}/{stem}_{id[..8]}.{kind}");
        if (!alias.EndsWith("." + kind, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("虚拟路径的类型必须与导入文件一致。");
        if (old != null && old.Kind != kind) throw new ArgumentException("重新加载不能改变资源类型。");
        if (entries.Any(e => e.Id != id && e.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("这个预缓存路径已被占用，请修改路径或重新加载原条目。");
        int revision = (old?.Revision ?? 0) + 1;
        string hash = Convert.ToHexString(SHA256.HashData(content));
        string blob = $"{id}/r{revision}-{Guid.NewGuid():N}.{kind}.{version}";
        string enginePath = $"art/MotionVfxLive/Precache/{id}/r{revision}_{hash[..12]}/asset.{kind}";
        var entry = new Entry(id, alias, kind, sourcePath, version, revision, hash, blob, enginePath, FindDependencies(content));
        WriteNew(BlobPath(entry), content);
        var candidate = entries.Where(e => e.Id != id).Append(entry).ToList();
        Host.AtomicJson(manifest, new Catalog(1, candidate.ToArray()));
        entries = candidate;
        return entry;
    }
    internal Entry Reload(string id) 
    {
        var e = entries.SingleOrDefault(e => e.Id == id) ?? throw new ArgumentException("未找到资源。");
        return Import(e.SourcePath, e.Alias, id);
    }
    internal Entry Rename(string id, string alias)
    {
        var e = entries.SingleOrDefault(e => e.Id == id) ?? throw new ArgumentException("未找到资源。");
        alias = Normalize(alias);
        if (!alias.EndsWith("." + e.Kind, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("路径类型不能改变。");
        if (entries.Any(x => x.Id != id && x.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("路径已占用。");
        var updated = e with { Alias = alias };
        var candidate = entries.Select(x => x.Id == id ? updated : x).ToList();
        Host.AtomicJson(manifest, new Catalog(1, candidate.ToArray())); entries = candidate;
        return updated;
    }
    private string BlobPath(Entry e)
    {
        string path = Path.GetFullPath(Path.Combine(root, e.Blob));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("缓存文件路径越界。");
        return path;
    }
    internal byte[] Read(Entry entry)
    {
        byte[] content = File.ReadAllBytes(BlobPath(entry));
        if (Convert.ToHexString(SHA256.HashData(content)) != entry.Hash) throw new InvalidDataException("预缓存内容哈希不匹配，请重新导入。");
        return content;
    }
    internal Dictionary<string, string> Materialize()
    {
        if (options.GameRoot == null) throw new InvalidOperationException("未配置游戏目录。");
        var mapping = entries.ToDictionary(e => e.Alias, e => e.EnginePath, StringComparer.OrdinalIgnoreCase);
        // The complete catalog generation isolates parent dependencies too:
        // refreshing a TEX creates a fresh UVS/MDF path even if its bytes did not change.
        string generation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', entries.OrderBy(e => e.Id).Select(e => e.Id + ":" + e.Revision + ":" + e.Alias)))))[..16];
        foreach (var e in entries) mapping[e.Alias] = e.EnginePath.Replace("/asset.", "/g" + generation + "/asset.");
        // Exact natives/stm or art-root paths associate dependencies; never guess by basename.
        var dependencies = new Dictionary<string, string>(mapping, StringComparer.OrdinalIgnoreCase);
        foreach (var group in entries.Select(e => (Entry: e, Origin: OriginPath(e.SourcePath)))
            .Where(e => e.Origin != null).GroupBy(e => e.Origin!, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1 && !dependencies.ContainsKey(group.Key))
                dependencies[group.Key] = mapping[group.Single().Entry.Alias];
        }
        var prepared = new List<(string Path, byte[] Bytes)>();
        foreach (var e in entries)
        {
            byte[] bytes = Read(e);
            if (e.Kind == "efx") bytes = RewriteEfx(bytes, "cache.efx." + e.Version, dependencies);
            else if (e.Kind is "uvs" or "mdf2") bytes = RewriteMaterial(e, bytes, dependencies);
            else if (e.Kind == "pfb" && e.Dependencies.Any(p => TryResolve(p, dependencies, out _)))
                bytes = RewritePrefab(e, bytes, dependencies);
            else if (e.Kind == "motlist" && e.Dependencies.Any(p => TryResolve(p, dependencies, out _)))
                bytes = RewriteMotionList(e, bytes, dependencies);
            string output = Path.Combine(options.GameRoot, "natives", "stm", mapping[e.Alias].Replace('/', Path.DirectorySeparatorChar) + "." + e.Version);
            prepared.Add((output, bytes));
        }
        foreach (var (output, bytes) in prepared)
        {
            if (File.Exists(output))
            {
                if (!File.ReadAllBytes(output).AsSpan().SequenceEqual(bytes)) throw new IOException("已有缓存版本内容异常，不覆盖。");
            }
            else WriteNew(output, bytes);
        }
        return mapping;
    }
    internal static string? OriginPath(string path)
    {
        path = path.Replace('\\', '/');
        int start = path.IndexOf("/natives/stm/", StringComparison.OrdinalIgnoreCase);
        if (start >= 0) path = path[(start + 13)..];
        else
        {
            start = path.IndexOf("/art/", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            path = path[(start + 1)..];
        }
        return Regex.Replace(path, @"\.\d+$", "");
    }
    internal static byte[] RewriteEfx(byte[] bytes, string path, IReadOnlyDictionary<string, string> mapping)
    {
        var service = new EfxEditorService();
        using var file = service.Load(bytes, path);
        bool changed = false;
        foreach (var field in ContentInspector.Describe(file, file.Header.Version))
        {
            if (field.ValueType != typeof(string) || field.Set == null || !field.Description.Editable) continue;
            if (!field.Description.Path.Split('.').Last().Contains("path", StringComparison.OrdinalIgnoreCase)) continue;
            if (TryResolve(field.Description.Value, mapping, out string? replacement))
            { field.Set(replacement); changed = true; }
        }
        if (!changed) return bytes;
        byte[] result = service.SaveToBytes(file);
        using var check = service.Load(result, path);
        if (!service.SaveToBytes(check).AsSpan().SequenceEqual(result)) throw new InvalidDataException("预缓存路径写入 EFX 后回读不稳定。");
        return result;
    }
    private static bool TryResolve(string path, IReadOnlyDictionary<string, string> mapping, out string? replacement)
    {
        path = path.Replace('\\', '/');
        if (mapping.TryGetValue(path, out replacement)) return true;
        // Saved EFX files contain immutable engine paths. On later edits,
        // follow the same owned resource ID to its new revision, never a basename.
        var identity = Regex.Match(path, @"^art/MotionVfxLive/Precache/([a-f0-9]{32})/r\d+_[A-F0-9]{12}/g[A-F0-9]{16}/asset\.(\w+)$", RegexOptions.IgnoreCase);
        if (!identity.Success) return false;
        string prefix = "art/MotionVfxLive/Precache/" + identity.Groups[1].Value + "/";
        replacement = mapping.Values.FirstOrDefault(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && v.EndsWith("." + identity.Groups[2].Value, StringComparison.OrdinalIgnoreCase));
        return replacement != null;
    }
    private static byte[] RewriteMaterial(Entry e, byte[] bytes, IReadOnlyDictionary<string, string> mapping)
    {
        using var source = new FileHandler(new MemoryStream(bytes), "asset." + e.Kind + "." + e.Version);
        using BaseFile file = e.Kind == "uvs" ? new UvsFile(source) : new MdfFile(source);
        if (!file.Read()) throw new InvalidDataException("无法读取素材引用。");
        // Resource strings only; never mutate shader names or material property names.
        bool changed = false;
        if (file is UvsFile uvs)
        {
            foreach (var t in uvs.Textures)
                if (TryResolve(t.path, mapping, out var value)) { t.path = value!; changed = true; }
        }
        else if (file is MdfFile mdf)
        {
            foreach (var material in mdf.Materials)
                foreach (var t in material.Textures)
                    if (TryResolve(t.texPath, mapping, out var value)) { t.texPath = value!; changed = true; }
        }
        if (!changed) return bytes;
        using var stream = new MemoryStream();
        using var output = new FileHandler(stream, "asset." + e.Kind + "." + e.Version);
        if (!file.WriteTo(output, false)) throw new InvalidDataException("素材引用写入失败。");
        return stream.ToArray();
    }
    private byte[] RewritePrefab(Entry e, byte[] bytes, IReadOnlyDictionary<string, string> mapping)
    {
        string schema = Path.Combine(options.Assets, "Lists", "rsz_pfb_classes.json");
        if (!File.Exists(schema)) schema = Path.Combine(AppContext.BaseDirectory, "formats", "rsz_pfb_classes.json");
        if (!File.Exists(schema)) throw new InvalidDataException("PFB 的已导入依赖需要 RSZ 类型表；请在素材目录提供 Lists/rsz_pfb_classes.json。原 PFB 未改动。");
        var option = new RszFileOption(GameName.mhwilds, RszParser.GetInstance(schema));
        using var source = new FileHandler(new MemoryStream(bytes), "asset.pfb." + e.Version);
        using var file = new PfbFile(option, source);
        if (!file.Read()) throw new InvalidDataException("无法完整读取 PFB；依赖重定向已取消。");
        bool changed = false;
        void RewriteRsz(RSZFile rsz)
        {
            foreach (var instance in rsz.InstanceList)
                for (int i = 0; i < instance.Values.Length; i++)
                {
                    if (instance.Fields[i].type != RszFieldType.Resource) continue;
                    if (instance.Values[i] is string value && TryResolve(value, mapping, out var next))
                    { instance.Values[i] = next; changed = true; }
                    else if (instance.Values[i] is System.Collections.IList values)
                        for (int j = 0; j < values.Count; j++)
                            if (values[j] is string path && TryResolve(path, mapping, out next))
                            { values[j] = next; changed = true; }
                }
            if (rsz.EmbeddedRSZFileList != null)
                foreach (var embedded in rsz.EmbeddedRSZFileList) RewriteRsz(embedded);
        }
        RewriteRsz(file.RSZ);
        foreach (var resource in file.ResourceInfoList)
            if (TryResolve(resource.Path, mapping, out var value)) { resource.Path = value!; changed = true; }
        if (!changed) return bytes;
        using var stream = new MemoryStream();
        using var output = new FileHandler(stream, "asset.pfb." + e.Version);
        if (!file.WriteTo(output, false)) throw new InvalidDataException("PFB 依赖写入失败。");
        byte[] result = stream.ToArray();
        using var verification = new FileHandler(new MemoryStream(result), "asset.pfb." + e.Version);
        using var check = new PfbFile(option, verification);
        if (!check.Read()) throw new InvalidDataException("PFB 依赖写入后回读失败。");
        return result;
    }
    private static byte[] RewriteMotionList(Entry e, byte[] bytes, IReadOnlyDictionary<string, string> mapping)
    {
        using var source = new FileHandler(new MemoryStream(bytes), "asset.motlist." + e.Version);
        using var file = new MotlistFile(source);
        if (!file.Read()) throw new InvalidDataException("无法读取 MOTLIST 引用，未改动动作数据。");
        bool changed = false;
        foreach (var link in file.MotFiles.OfType<ReeLib.Motlist.MotFileLink>())
            if (TryResolve(link.Path, mapping, out var value)) { link.Path = value!; changed = true; }
        if (file.Header.BaseMotListPath is string parent && TryResolve(parent, mapping, out var replacement))
        { file.Header.BaseMotListPath = replacement; changed = true; }
        if (!changed) return bytes;
        using var stream = new MemoryStream();
        using var output = new FileHandler(stream, "asset.motlist." + e.Version);
        if (!file.WriteTo(output, false)) throw new InvalidDataException("MOTLIST 引用写入失败。");
        byte[] result = stream.ToArray();
        using var verification = new FileHandler(new MemoryStream(result), "asset.motlist." + e.Version);
        using var check = new MotlistFile(verification);
        if (!check.Read() || check.Motions.Count != file.Motions.Count) throw new InvalidDataException("MOTLIST 重定向后动作数量不一致。");
        return result;
    }
    internal string[] MissingDependencies() => entries.SelectMany(e => e.Dependencies)
        .Where(p => p.StartsWith("art/MotionVfxLive/Library/", StringComparison.OrdinalIgnoreCase) &&
            !entries.Any(e => e.Alias.Equals(p, StringComparison.OrdinalIgnoreCase))).Distinct().ToArray();
    private static string[] FindDependencies(byte[] bytes)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string decoded in new[] { Encoding.Unicode.GetString(bytes), Encoding.Unicode.GetString(bytes, 1, Math.Max(0, bytes.Length - 1)), Encoding.UTF8.GetString(bytes) })
            foreach (Match m in Reference.Matches(decoded.Replace('\\', '/'))) found.Add(m.Value);
        return found.Order().Take(4096).ToArray();
    }
    private static void CheckMagic(string kind, byte[] bytes)
    {
        uint magic = BitConverter.ToUInt32(bytes);
        uint expected = kind switch { "tex" => 0x00584554, "mesh" => 0x4853454d, "mdf2" => 0x0046444d,
            "uvs" => UvsFile.Magic, "pfb" => PfbFile.Magic, "efx" => EfxFile.Magic, _ => 0 };
        if (expected != 0 && magic != expected) throw new InvalidDataException("文件头与扩展名不符，未加入预缓存。");
        if (kind is "mot" or "motlist")
        {
            expected = kind == "mot" ? MotFile.Magic : MotlistFile.Magic;
            if (BitConverter.ToUInt32(bytes, 4) != expected)
                throw new InvalidDataException("动作文件头与扩展名不符，未加入预缓存。");
        }
    }
    private static void WriteNew(string path, byte[] bytes)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(path)!); dir != null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("预缓存拒绝符号链接或目录联接。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
