// EfxCorpusGate — EFX 语料门禁。判据是“写出结果和原文件逐字节相同”，
// 不是“二次往返稳定”，也不是“和旧写入器的输出相同”（那两种对长度前缀写错、
// 骨骼关系错位这类问题完全免疫）。说明见同目录 README.md。
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;

Console.OutputEncoding = Encoding.UTF8;
return args.Length == 0 ? Usage() : args[0] switch
{
    "roundtrip" => RunRoundtrip(args),
    "bonealign" => RunBoneAlign(args),
    "compare" => RunCompare(args),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("用法:");
    Console.WriteLine("  EfxCorpusGate roundtrip <语料目录> <输出.tsv> [--limit N]");
    Console.WriteLine("  EfxCorpusGate bonealign <语料目录> <输出.json> [--extra 类型名,类型名]");
    Console.WriteLine("  EfxCorpusGate compare <基线.tsv> <当前.tsv>");
    return 2;
}

static List<string> CorpusFiles(string dir)
{
    if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
    var files = Directory.EnumerateFiles(dir, "*.efx.*", SearchOption.AllDirectories)
        .Where(f => Regex.IsMatch(f, @"\.efx\.\d+$", RegexOptions.IgnoreCase))
        .ToList();
    files.Sort(StringComparer.Ordinal);
    return files;
}

static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

static string OneLine(string text, int max = 200)
{
    var line = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    return line.Length > max ? line[..max] : line;
}

// ---------------------------------------------------------------- roundtrip

static int RunRoundtrip(string[] args)
{
    if (args.Length < 3) return Usage();
    string root = Path.GetFullPath(args[1]);
    string output = Path.GetFullPath(args[2]);
    int limitIdx = Array.IndexOf(args, "--limit");
    int limit = limitIdx >= 0 && limitIdx + 1 < args.Length ? int.Parse(args[limitIdx + 1]) : int.MaxValue;

    var files = CorpusFiles(root).Take(limit).ToList();
    Console.WriteLine($"语料 {root}：{files.Count} 个文件");
    var rows = new List<(string Status, string Rel, string Detail)>();
    var watch = System.Diagnostics.Stopwatch.StartNew();

    foreach (var path in files)
    {
        string rel = Rel(root, path);
        rows.Add(Check(path, rel));
        if (rows.Count % 500 == 0) Console.WriteLine($"  ... {rows.Count}/{files.Count}（{watch.Elapsed:mm\\:ss}）");
    }

    var counts = rows.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
    var sb = new StringBuilder();
    sb.AppendLine("# EfxCorpusGate roundtrip v1");
    sb.AppendLine($"# corpus\t{root}");
    sb.AppendLine($"# files\t{rows.Count}");
    foreach (var status in new[] { "same", "diff", "read-error", "write-error" })
        sb.AppendLine($"# {status}\t{counts.GetValueOrDefault(status)}");
    sb.AppendLine("# status\tpath\tdetail");
    foreach (var row in rows) sb.Append(row.Status).Append('\t').Append(row.Rel).Append('\t').AppendLine(row.Detail);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, sb.ToString(), new UTF8Encoding(false));

    Console.WriteLine();
    Console.WriteLine($"完成，用时 {watch.Elapsed:mm\\:ss}");
    Console.WriteLine($"  和原文件逐字节相同 {counts.GetValueOrDefault("same")} / {rows.Count}");
    Console.WriteLine($"  写出不同           {counts.GetValueOrDefault("diff")}");
    Console.WriteLine($"  读取异常           {counts.GetValueOrDefault("read-error")}");
    Console.WriteLine($"  写出异常           {counts.GetValueOrDefault("write-error")}");
    PrintBuckets("写出不同，按首个差异所在位置分桶", rows.Where(r => r.Status == "diff").Select(r => Field(r.Detail, "at")));
    PrintBuckets("写出不同，长度前缀线索", rows.Where(r => r.Status == "diff").Select(r => Field(r.Detail, "hint")));
    PrintBuckets("异常，按消息分桶", rows.Where(r => r.Status.EndsWith("error"))
        .Select(r => r.Status + ": " + Regex.Replace(r.Detail, @"\d+", "N")));
    Console.WriteLine($"结果写入 {output}");
    return 0;
}

static string Field(string detail, string key)
{
    foreach (var part in detail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        if (part.StartsWith(key + "=", StringComparison.Ordinal)) return part[(key.Length + 1)..];
    return "-";
}

static void PrintBuckets(string title, IEnumerable<string> keys)
{
    var buckets = keys.GroupBy(k => k).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
    if (buckets.Count == 0) return;
    Console.WriteLine($"  {title}：");
    foreach (var bucket in buckets.Take(40)) Console.WriteLine($"    {bucket.Count(),6}  {bucket.Key}");
    if (buckets.Count > 40) Console.WriteLine($"    …… 另有 {buckets.Count - 40} 个桶");
}

static (string, string, string) Check(string path, string rel)
{
    byte[] original = File.ReadAllBytes(path);
    EfxFile efx;
    try
    {
        efx = new EfxFile(new FileHandler(new MemoryStream(original, writable: false), path));
        efx.Read();
    }
    catch (Exception ex)
    {
        return ("read-error", rel, OneLine(ex.GetType().Name + ": " + ex.Message));
    }

    byte[] written;
    try
    {
        using var stream = new MemoryStream();
        // saveFile:false 必须写明：默认 true 会把结果存回 handler.FilePath（即原语料文件）。
        // 写出路径也不带原文件名，避免任何“按 FilePath 保存”的代码碰到语料。
        if (!efx.WriteTo(new FileHandler(stream), saveFile: false))
            return ("write-error", rel, "WriteTo returned false (exception was logged by BaseFile.WriteTo)");
        written = stream.ToArray();
    }
    catch (Exception ex)
    {
        return ("write-error", rel, OneLine(ex.GetType().Name + ": " + ex.Message));
    }

    if (original.AsSpan().SequenceEqual(written)) return ("same", rel, "");

    int common = Math.Min(original.Length, written.Length);
    int offset = 0;
    while (offset < common && original[offset] == written[offset]) offset++;
    // 写出后各 attribute 的 Start/StructSize 是写出流里的位置；首个差异之前两份字节
    // 完全相同，所以写出流里覆盖该偏移的 attribute 就是首个出错的位置。
    string at = Locate(efx, offset, 0, "");
    string hint = PrefixHint(original, written, offset);
    return ("diff", rel, $"off={offset} orig={original.Length} new={written.Length} at={at} hint={hint}");
}

static string Locate(EfxFile efx, long offset, long baseOffset, string prefix)
{
    long firstStart = long.MaxValue, lastEnd = -1, nextStart = long.MaxValue;
    string? nextName = null;
    foreach (var (owner, attr) in Attributes(efx))
    {
        long start = baseOffset + attr.Start;
        long end = start + StructSize(attr);
        firstStart = Math.Min(firstStart, start);
        lastEnd = Math.Max(lastEnd, end);
        if (start > offset && start < nextStart) { nextStart = start; nextName = owner + ":" + attr.type; }
        if (offset < start || offset >= end) continue;
        string name = prefix + owner + ":" + attr.type;
        if (attr is EFXAttributePlayEmitter { efxrData: not null } emitter)
        {
            // PlayEmitter 的写出：4 字节 efxrSize，随后是以 (Start+4) 为原点的嵌入 EFX。
            long inner = start + 4;
            if (offset >= inner) return Locate(emitter.efxrData, offset, inner, name + ">");
        }
        return name;
    }
    if (firstStart == long.MaxValue) return prefix + "no-attributes";
    if (offset < firstStart) return prefix + "before-attributes";
    if (offset >= lastEnd) return prefix + "after-attributes";
    // 两个 attribute 之间是下一个 attribute 的头（类型、声明长度、UniqueID）；
    // 声明长度不对通常说明这个 attribute 自己的写出长度变了。
    return prefix + "header-of-" + nextName;
}

static IEnumerable<(string Owner, EFXAttribute Attr)> Attributes(EfxFile efx)
{
    foreach (var entry in efx.Entries)
        foreach (var attr in entry.Attributes) yield return ("entry", attr);
    foreach (var action in efx.Actions)
        foreach (var attr in action.Attributes) yield return ("action", attr);
}

// 内联宽字符串长度前缀写错时，差异点上的 u32 恰好是原值的一半或两倍。
static string PrefixHint(byte[] original, byte[] written, int offset)
{
    for (int p = Math.Max(0, offset - 3); p <= offset; p++)
    {
        if (p + 4 > original.Length || p + 4 > written.Length) break;
        uint a = BitConverter.ToUInt32(original, p), b = BitConverter.ToUInt32(written, p);
        if (a == b || a == 0 || b == 0 || a > 4096 || b > 4096) continue;
        if (a == b * 2) return "prefix-bytes-written-as-units";
        if (b == a * 2) return "prefix-units-written-as-bytes";
    }
    return original.Length == written.Length ? "same-length" : "length-changed";
}

// BaseModel.StructSize 是 internal；门禁只读它，不为此改动 Core。
static long StructSize(EFXAttribute attr) => (long)StructSizeProperty.GetValue(attr)!;

// ---------------------------------------------------------------- compare

static Dictionary<string, (string Status, string Detail)> LoadTsv(string path)
{
    var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
    foreach (var line in File.ReadLines(path))
    {
        if (line.Length == 0 || line[0] == '#') continue;
        var parts = line.Split('\t');
        map[parts[1]] = (parts[0], parts.Length > 2 ? parts[2] : "");
    }
    return map;
}

static int RunCompare(string[] args)
{
    if (args.Length < 3) return Usage();
    var baseline = LoadTsv(args[1]);
    var current = LoadTsv(args[2]);
    var missing = baseline.Keys.Where(k => !current.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
    var added = current.Keys.Where(k => !baseline.ContainsKey(k)).ToList();
    var regressed = baseline.Where(kv => kv.Value.Status == "same" && current.TryGetValue(kv.Key, out var c) && c.Status != "same")
        .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    var newErrors = baseline.Where(kv => !kv.Value.Status.EndsWith("error") && current.TryGetValue(kv.Key, out var c) && c.Status.EndsWith("error"))
        .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    var fixedFiles = baseline.Where(kv => kv.Value.Status != "same" && current.TryGetValue(kv.Key, out var c) && c.Status == "same")
        .Select(kv => kv.Key).ToList();
    var moved = baseline.Where(kv => kv.Value.Status == "diff" && current.TryGetValue(kv.Key, out var c) && c.Status == "diff" && c.Detail != kv.Value.Detail)
        .Select(kv => kv.Key).ToList();

    int Same(Dictionary<string, (string Status, string Detail)> m) => m.Values.Count(v => v.Status == "same");
    Console.WriteLine($"基线逐字节相同 {Same(baseline)} / {baseline.Count}，当前 {Same(current)} / {current.Count}");
    Console.WriteLine($"  新变成相同 {fixedFiles.Count}，从相同退化 {regressed.Count}，新出现异常 {newErrors.Count}，" +
                      $"差异位置改变 {moved.Count}，基线有而当前缺失 {missing.Count}，当前新增 {added.Count}");
    foreach (var key in regressed.Take(50)) Console.WriteLine($"  [退化] {key}  {current[key].Status} {current[key].Detail}");
    foreach (var key in newErrors.Where(k => !regressed.Contains(k)).Take(50)) Console.WriteLine($"  [新异常] {key}  {current[key].Detail}");
    foreach (var key in missing.Take(20)) Console.WriteLine($"  [缺失] {key}");
    bool pass = regressed.Count == 0 && newErrors.Count == 0 && missing.Count == 0;
    Console.WriteLine(pass ? "门禁：通过" : "门禁：失败");
    return pass ? 0 : 1;
}

// ---------------------------------------------------------------- bonealign

// Bones/BoneRelations 是按位置分配的索引流：每个 IBoneRelationAttribute 按出现顺序
// 消费一个槽位。消费者集合少一个，后面整条流都会错位。判据：作用域里消费者数量
// 必须等于文件声明的 BoneRelations 数量，且按下标查出的骨骼名等于 attribute 内联的骨骼名。
static int RunBoneAlign(string[] args)
{
    if (args.Length < 3) return Usage();
    string root = Path.GetFullPath(args[1]);
    string output = Path.GetFullPath(args[2]);
    int extraIdx = Array.IndexOf(args, "--extra");
    var extra = extraIdx >= 0 && extraIdx + 1 < args.Length
        ? args[extraIdx + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet()
        : new HashSet<string>();

    var inlineCache = new Dictionary<Type, System.Reflection.MemberInfo?>();
    string? InlineBoneName(EFXAttribute attr)
    {
        var type = attr.GetType();
        if (!inlineCache.TryGetValue(type, out var member))
        {
            member = null;
            foreach (var name in new[] { "BoneName", "boneName", "JointName" })
            {
                if (type.GetField(name) is { } f && f.FieldType == typeof(string)) { member = f; break; }
                if (type.GetProperty(name) is { } p && p.PropertyType == typeof(string)) { member = p; break; }
            }
            inlineCache[type] = member;
        }
        return member switch
        {
            System.Reflection.FieldInfo f => f.GetValue(attr) as string,
            System.Reflection.PropertyInfo p => p.GetValue(attr) as string,
            _ => null,
        };
    }

    var files = CorpusFiles(root);
    int scanned = 0, failed = 0, scopes = 0, countMismatch = 0, agree = 0, disagree = 0;
    var bad = new List<object>();

    void Visit(string rel, string scope, EfxFile efx, List<EFXBone> outerBones)
    {
        scopes++;
        var bones = efx.Bones.Count > 0 ? efx.Bones : outerBones;
        int consumers = 0, localAgree = 0, localDisagree = 0;
        foreach (var entry in efx.Entries)
        foreach (var attr in entry.Attributes)
        {
            bool isConsumer = attr is IBoneRelationAttribute || extra.Contains(attr.GetType().Name);
            if (!isConsumer) continue;
            short index = consumers < efx.BoneRelations.Count ? efx.BoneRelations[consumers] : (short)-1;
            consumers++;
            if (attr is not IBoneRelationAttribute) continue;
            string? resolved = index >= 0 && index < bones.Count ? bones[index].name : null;
            string? inline = InlineBoneName(attr);
            if (string.IsNullOrEmpty(resolved) == string.IsNullOrEmpty(inline) && (string.IsNullOrEmpty(resolved) || resolved == inline))
                localAgree++;
            else localDisagree++;
        }
        agree += localAgree; disagree += localDisagree;
        bool countOk = consumers == efx.BoneRelations.Count;
        if (!countOk) countMismatch++;
        if ((!countOk || localDisagree > 0) && bad.Count < 400)
        {
            var hist = efx.Entries.SelectMany(e => e.Attributes).GroupBy(a => a.GetType().Name).ToDictionary(g => g.Key, g => g.Count());
            bad.Add(new { file = rel, scope, declared = efx.BoneRelations.Count, consumers, nameDisagree = localDisagree, types = hist });
        }
        int actionIndex = 0;
        foreach (var action in efx.Actions)
        {
            foreach (var attr in action.Attributes)
                if (attr is EFXAttributePlayEmitter { efxrData: not null } emitter)
                    Visit(rel, $"{scope}/Actions[{actionIndex}].PlayEmitter", emitter.efxrData, bones);
            actionIndex++;
        }
    }

    foreach (var path in files)
    {
        try
        {
            var efx = new EfxFile(new FileHandler(new MemoryStream(File.ReadAllBytes(path), writable: false), path));
            efx.Read();
            scanned++;
            Visit(Rel(root, path), "root", efx, efx.Bones);
        }
        catch (Exception) { failed++; }
    }

    var result = new
    {
        corpus = root,
        extraConsumerTypes = extra.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        filesTotal = files.Count, filesScanned = scanned, filesFailed = failed, scopes,
        countMismatchScopes = countMismatch, nameAgree = agree, nameDisagree = disagree,
        mismatched = bad,
    };
    File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }), new UTF8Encoding(false));
    Console.WriteLine($"扫描 {scanned} 个文件（读取失败 {failed}），{scopes} 个作用域");
    Console.WriteLine($"  数量对不上的作用域 {countMismatch}，骨骼名一致 {agree} / 不一致 {disagree}" +
                      (extra.Count > 0 ? $"（额外消费者：{string.Join(",", extra)}）" : ""));
    Console.WriteLine($"结果写入 {output}");
    return countMismatch == 0 && disagree == 0 ? 0 : 1;
}

static partial class Program
{
    static readonly System.Reflection.PropertyInfo StructSizeProperty = typeof(BaseModel).GetProperty("StructSize",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
        ?? throw new MissingMemberException(nameof(BaseModel), "StructSize");
}
