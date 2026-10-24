using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VfxStudio.LiveEditor;

// Read-only filesystem navigation for the in-game dialog. No desktop window or
// recursive scan is involved; selecting a file still uses the existing editors.
internal static class FileBrowser
{
    private sealed record Location(string Name, string Path);
    private sealed record Entry(string Name, string Path, bool IsDirectory, long Size);
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["efx"] = "efx", ["motion"] = "mot|motlist",
        ["pfb"] = "pfb", ["assets"] = "mesh|mdf|mdf2|uvs|tex|pfb|efx|mot|motlist"
    };

    public static object Browse(Options options, JsonObject request)
    {
        string kind = request["kind"]?.GetValue<string>() ?? "all";
        if (!kind.Equals("all", StringComparison.OrdinalIgnoreCase) && !Extensions.ContainsKey(kind))
            throw new ArgumentException("未知文件类型，请重新打开文件选择器。");
        string filter = (request["filter"]?.GetValue<string>() ?? "").Trim();
        int offset = Math.Max(0, request["offset"]?.GetValue<int>() ?? 0);
        int limit = Math.Clamp(request["limit"]?.GetValue<int>() ?? 100, 1, 200);
        var shortcuts = Shortcuts(options);
        string? input = request["path"]?.GetValue<string>();
        string path = string.IsNullOrWhiteSpace(input) ? shortcuts.First().Path : Absolute(input);
        string? selectedPath = null;
        try
        {
            var attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                selectedPath = path;
                path = Path.GetDirectoryName(path)!;
            }
            var enumeration = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
            // PrecacheStore.Import requires a numeric version; the file editors
            // additionally support their unversioned filenames.
            string versionSuffix = kind.Equals("assets", StringComparison.OrdinalIgnoreCase) ? @"\.\d+$" : @"(?:\.\d+)?$";
            var pattern = kind.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : new Regex(@"\.(?:" + Extensions[kind] + ")" + versionSuffix, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (selectedPath != null && pattern != null && !pattern.IsMatch(selectedPath))
                throw new ArgumentException("此文件不属于当前选择的文件类型，请选择支持的文件。");
            var entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", enumeration)
                .Where(item => filter.Length == 0 || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.FullName, selectedPath, StringComparison.OrdinalIgnoreCase))
                .Where(item => item.Attributes.HasFlag(FileAttributes.Directory) || pattern == null || pattern.IsMatch(item.Name))
                .Select(item => new Entry(item.Name, item.FullName, item.Attributes.HasFlag(FileAttributes.Directory), item is FileInfo file ? file.Length : 0))
                .OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
            if (selectedPath != null)
            {
                int selectedIndex = Array.FindIndex(entries, entry => entry.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
                if (selectedIndex >= 0) offset = selectedIndex / limit * limit;
            }
            if (offset >= entries.Length) offset = entries.Length == 0 ? 0 : (entries.Length - 1) / limit * limit;
            return new { Path = path, ParentPath = Directory.GetParent(path)?.FullName, Roots = Roots(), Shortcuts = shortcuts,
                Entries = entries.Skip(offset).Take(limit).ToArray(), Total = entries.Length, Offset = offset, Limit = limit, SelectedPath = selectedPath };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        { throw new IOException("没有权限读取此文件夹，请选择其他位置：" + path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { throw new IOException("找不到此路径，请检查地址后重试：" + path); }
        catch (IOException ex) { throw new IOException("无法读取此位置，请检查磁盘或文件是否可用：" + path + "；" + ex.Message); }
    }

    public static object Stat(string input)
    {
        string path = Absolute(input);
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory)) return new { Path = path, Exists = true, IsDirectory = true, Size = 0L, Sha256 = (string?)null };
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long size = stream.Length;
            string sha256 = Convert.ToHexString(SHA256.HashData(stream));
            return new { Path = path, Exists = true, IsDirectory = false, Size = size, Sha256 = sha256 };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return new { Path = path, Exists = false, IsDirectory = false, Size = 0L, Sha256 = (string?)null }; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        { throw new IOException("没有权限读取此文件，不能确认覆盖：" + path); }
    }

    private static string Absolute(string input)
    {
        string path = input.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请输入完整的本地路径，例如 D:\\素材；不能使用游戏资源路径。");
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("请选择普通磁盘或共享文件夹路径。");
        return path;
    }

    private static Location[] Roots() => DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } })
        .Select(d => new Location(d.Name, d.RootDirectory.FullName)).ToArray();

    private static Location[] Shortcuts(Options options)
    {
        var locations = new List<Location>();
        void Add(string name, string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && !locations.Any(l => l.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                locations.Add(new(name, Path.GetFullPath(path)));
        }
        Add("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("下载", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        Add("游戏目录", options.GameRoot);
        Add("编辑器素材", options.Assets);
        Add("导出目录", options.Output);
        Add("用户目录", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add("工作目录", Directory.GetCurrentDirectory());
        return locations.ToArray();
    }
}
