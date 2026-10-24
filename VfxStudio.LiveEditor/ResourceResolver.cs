using System.Text.RegularExpressions;

namespace VfxStudio.LiveEditor;

// Explicit offline file discovery only. A matching path never proves that
// these disk bytes are the resource currently cached by the running game.
internal static class ResourceResolver
{
    internal sealed record Candidate(string Path, string ResourcePath, string Source, string Match)
    {
        public string SourceKind => "local_file";
    }
    internal sealed record Resolution(string ResourcePath, Candidate[] Candidates, string? ResolvedPath, string Message);

    internal static string Normalize(string input)
    {
        string path = input.Trim().Replace('\\', '/').TrimStart('@', '/');
        if (path.StartsWith("natives/stm/", StringComparison.OrdinalIgnoreCase)) path = path[12..];
        path = Regex.Replace(path, @"\.efx\.\d+$", ".efx", RegexOptions.IgnoreCase);
        if (!path.EndsWith(".efx", StringComparison.OrdinalIgnoreCase) || path.Contains(':') ||
            path.Split('/').Any(s => s.Length == 0 || s is "." or "..") ||
            path.IndexOfAny(['\0', '\r', '\n', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new ArgumentException("Expected a game-relative EFX resource path without traversal or wildcards.");
        return path;
    }

    internal static Resolution Resolve(Options options, string input)
    {
        string logical = Normalize(input);
        string versioned = logical + ".5571972";
        string fileName = versioned.Split('/')[^1];
        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        string? gameFile = null;
        void Add(string path, string source, string match)
        {
            path = Path.GetFullPath(path);
            if (File.Exists(path)) candidates.TryAdd(path, new(path, logical, source, match));
        }
        if (options.GameRoot != null)
        {
            string gameCandidate = Path.Combine(options.GameRoot, "natives", "STM", versioned.Replace('/', Path.DirectorySeparatorChar));
            Add(gameCandidate, "game-loose-file", "exact");
            if (File.Exists(gameCandidate)) gameFile = Path.GetFullPath(gameCandidate);
        }
        if (Directory.Exists(options.Assets))
        {
            var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string file in Directory.EnumerateFiles(options.Assets, "*.efx.5571972", enumeration))
            {
                if (!string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = Path.GetRelativePath(options.Assets, file).Replace('\\', '/');
                bool exact = relative.Equals(versioned, StringComparison.OrdinalIgnoreCase) ||
                    relative.EndsWith('/' + versioned, StringComparison.OrdinalIgnoreCase);
                Add(file, "project-assets", exact ? "exact" : "basename");
            }
        }
        Candidate[] ordered = candidates.Values.OrderBy(c => c.Source == "game-loose-file" ? 0 : c.Match == "exact" ? 1 : 2)
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        Candidate[] exactMatches = ordered.Where(c => c.Match == "exact").ToArray();
        string? resolved = gameFile ?? (exactMatches.Length == 1 ? exactMatches[0].Path : null);
        string message = gameFile != null ? "已定位游戏当前松散资源文件。" : resolved != null ? "已按完整资源路径定位材料。" :
            ordered.Length == 0 ? "材料中未找到此资源；请提供提取后的完整 EFX 文件路径。" :
            "找到多个来源或仅同名材料，请确认要编辑的文件；不会按文件名自动替换。";
        return new(logical, ordered, resolved, "本地文件查找（不是当前游戏缓存）：" + message);
    }
}
