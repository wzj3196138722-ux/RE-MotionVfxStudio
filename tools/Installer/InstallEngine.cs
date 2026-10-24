using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MotionVfxSetup;

internal sealed record InstallResult(string GameRoot, string InstallRoot, string Package, string Backup, bool BackendReady);
internal static class InstallEngine
{
    internal const string Version = "0.8.5";
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static string Key(string game) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(game).ToLowerInvariant())))[..16];
    internal static string DefaultRoot(string game) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MotionVfxLive", Key(game));
    internal static string? InstalledVersion(string game)
    {
        string path = Path.Combine(game, "reframework", "autorun", "MotionVfxLive.lua");
        if (!File.Exists(path)) return null;
        using var reader = File.OpenText(path);
        char[] header = new char[4096]; int length = reader.ReadBlock(header, 0, header.Length);
        var match = Regex.Match(new string(header, 0, length), "\\bversion\\s*=\\s*\"(\\d+\\.\\d+\\.\\d+)\"");
        return match.Success ? match.Groups[1].Value : null;
    }
    internal static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split('/', '\\').Any(p => p is ".." or "." || p.EndsWith('.') || p.EndsWith(' ')))
            throw new IOException("安装包包含不安全路径：" + relative);
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装路径超出目标目录。");
        RejectLinks(path);
        return path;
    }
    private static void RejectLinks(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("安装目标包含目录链接，请选择真实目录：" + p);
    }
    internal static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    // Copy readable program bytes, never source EFS/compression attributes.
    // File.Copy can fail from an encrypted user profile to an unencrypted game volume.
    internal static void CopyContent(string source, string destination, bool overwrite = false)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(true);
    }
    internal static void Atomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static JsonNode? Read(string path)
    {
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonNode.Parse(file);
    }
    internal static List<string> FindGames()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(object? value) { if (value is string s && Directory.Exists(s)) roots.Add(s); }
        void Optional(Action read) { try { read(); } catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { } }
        Optional(() => Add(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)));
        Optional(() => Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null)));
        Optional(() => {
            using var game = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 2246340");
            if (game?.GetValue("InstallLocation") is string g) games.Add(g);
        });
        Optional(() => {
            using var installs = Registry.CurrentUser.OpenSubKey(@"Software\MotionVfxLive\Installs");
            if (installs != null) foreach (string name in installs.GetValueNames()) if (installs.GetValue(name) is string g) games.Add(g);
        });
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        foreach (string steam in roots.ToArray())
        {
            string libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            Optional(() => {
                if (File.Exists(libraries)) foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"([^\"]+)\""))
                    Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            });
        }
        foreach (string library in roots)
        {
            string manifest = Path.Combine(library, "steamapps", "appmanifest_2246340.acf");
            Optional(() => { if (File.Exists(manifest)) {
                var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                if (match.Success) games.Add(Path.Combine(library, "steamapps", "common", match.Groups[1].Value));
            } });
            games.Add(Path.Combine(library, "steamapps", "common", "MonsterHunterWilds"));
        }
        return games.Where(g => File.Exists(Path.Combine(g, "MonsterHunterWilds.exe"))).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    internal static void AssertClosed()
    {
        var processes = Process.GetProcessesByName("MonsterHunterWilds");
        try { if (processes.Length > 0) throw new InvalidOperationException("请先正常退出《怪物猎人 荒野》，再点击安装。没有替换游戏文件。"); }
        finally { foreach (var p in processes) p.Dispose(); }
    }
    internal static string ExtractAndVerify(string directory)
    {
        Directory.CreateDirectory(directory);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MotionVfx.payload.zip") ?? throw new IOException("安装包缺少内置程序。请使用完整发布的 Setup.exe。");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        string? prefix = null; long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            int slash = entry.FullName.IndexOf('/');
            if (slash <= 0) throw new IOException("安装包目录结构无效。");
            prefix ??= entry.FullName[..(slash + 1)];
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("安装包包含多个根目录。");
            string relative = entry.FullName[prefix.Length..];
            if (!names.Add(relative) || entry.Length > 512L * 1024 * 1024 || (total += entry.Length) > 3L * 1024 * 1024 * 1024 || names.Count > 12000)
                throw new IOException("安装包文件大小或名称异常。");
            string destination = Child(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, false);
        }
        var manifest = Read(Child(directory, "SHA256.json"))?.AsArray() ?? throw new IOException("安装包缺少校验清单。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SHA256.json" };
        foreach (var row in manifest)
        {
            string relative = row!["path"]!.GetValue<string>(); string path = Child(directory, relative);
            if (!seen.Add(relative) || new FileInfo(path).Length != row["size"]!.GetValue<long>() || !Hash(path).Equals(row["sha256"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("安装包校验失败：" + relative);
        }
        if (!seen.SetEquals(names)) throw new IOException("安装包包含未列入清单的文件。");
        if (Read(Child(directory, "release.json"))?["version"]?.GetValue<string>() != Version) throw new IOException("安装器与内置程序版本不匹配。");
        return directory;
    }
    internal static (string Source, string Target)[] PluginMap()
    {
        var map = new List<(string, string)>
        {
            ("plugin/MotionVfxLive.lua", "reframework/autorun/MotionVfxLive.lua"),
            ("plugin/Reload.lua", "reframework/data/MotionVfxLive/Reload.lua"),
            ("plugin/RefreshTextInputs.lua", "reframework/data/MotionVfxLive/RefreshTextInputs.lua"),
            ("plugin/RepairBindings.lua", "reframework/data/MotionVfxLive/RepairBindings.lua"),
            ("plugin/reframework-d2d.lua", "reframework/autorun/reframework-d2d.lua"),
            ("native/reframework-d2d.dll", "reframework/plugins/reframework-d2d.dll"),
            ("native/MotionVfxDebugInput.dll", "reframework/plugins/MotionVfxDebugInput.dll"),
            ("licenses/REFramework-D2D-LICENSE.txt", "reframework/plugins/reframework-d2d-LICENSE.txt"),
            ("licenses/MotionVfxDebugInput-THIRD-PARTY.txt", "reframework/plugins/MotionVfxDebugInput-LICENSE.txt")
        };
        foreach (string module in new[] { "fx_action_capture", "fx_action_bindings", "fx_action_sources", "fx_attachment_targets", "fx_action_preview_transaction", "fx_action_preview_runtime", "fx_preview_runtime", "fx_ai_bridge", "fx_debug_hotkeys", "fx_d2d_widgets", "fx_d2d_workbench", "fx_parameter_ui", "fx_precache_runtime", "fx_pfb_page", "fx_pfb_preview_runtime", "fx_equipment_sources", "fx_equipment_pfb_preview_runtime", "fx_file_dialog" })
            map.Add(($"plugin/MotionVfxLive/{module}.lua", $"reframework/autorun/MotionVfxLive/{module}.lua"));
        foreach (string logo in new[] { "logo", "author-logo" }) map.Add(($"images/MotionVfxLive/{logo}.png", $"reframework/images/MotionVfxLive/{logo}.png"));
        return map.ToArray();
    }
    internal static void ValidatePluginMap(string package)
    {
        // A packaged module must never silently remain from an older installation.
        var deployed = PluginMap().Where(p => p.Source.StartsWith("plugin/MotionVfxLive/", StringComparison.Ordinal))
            .Select(p => Path.GetFileName(p.Source)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packaged = Directory.EnumerateFiles(Child(package, "plugin/MotionVfxLive"), "*.lua", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!deployed.SetEquals(packaged)) throw new IOException("安装器组件清单与包内脚本不一致，未替换旧版本。请使用完整匹配的发布包。");
    }
    internal static List<(string Relative, string Hash)> PreviousProgramFiles(string root, string? package)
    {
        var result = new List<(string Relative, string Hash)>();
        if (package == null || !Path.GetFullPath(package).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\versions\\", StringComparison.OrdinalIgnoreCase)) return result;
        var release = Read(Child(package, "release.json"));
        if (release?["name"]?.GetValue<string>() != "MotionVfx Live" ||
            !System.Version.TryParse(release["version"]?.GetValue<string>(), out var version) || version >= System.Version.Parse(Version)) return result;
        if (Read(Child(package, "SHA256.json")) is not JsonArray manifest) return result;
        foreach (var item in manifest)
        {
            string? relative = item?["path"]?.GetValue<string>(), hash = item?["sha256"]?.GetValue<string>();
            // Never retire documents, settings, exports, source files, sessions or unknown roots.
            if (relative == null || hash == null || !Regex.IsMatch(hash, "\\A[0-9A-Fa-f]{64}\\z") ||
                !Regex.IsMatch(relative, "\\A(?:backend|mcp|plugin|native|images)/")) continue;
            string path = Child(package, relative);
            if (File.Exists(path) && Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase)) result.Add((relative, hash));
        }
        return result;
    }
    internal static string? PreviousGameTarget(string relative)
    {
        if (Regex.IsMatch(relative, "\\Aplugin/MotionVfxLive/fx_[a-z0-9_]+\\.lua\\z")) return "reframework/autorun/" + relative[7..];
        if (Regex.IsMatch(relative, "\\Aimages/MotionVfxLive/[a-zA-Z0-9_-]+\\.png\\z")) return "reframework/" + relative;
        return null;
    }
    internal static void RetirePreviousPrograms(string root, string? previous, string backup, Action<string> log)
    {
        var retired = new List<object>();
        foreach (var item in PreviousProgramFiles(root, previous))
        {
            string target = Child(previous!, item.Relative), saved = Child(backup, "previous-program/" + item.Relative);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!); CopyContent(target, saved);
                // Recheck after copying in case another process or user changed the old file.
                if (Hash(saved) != Hash(target) || !Hash(saved).Equals(item.Hash, StringComparison.OrdinalIgnoreCase))
                { log("保留安装期间发生变化的旧文件：" + target); continue; }
                File.Delete(target);
                retired.Add(new { source = target, backup = saved, sha256 = item.Hash });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { log("保留无法安全清理的旧文件：" + target + "：" + ex.Message); }
        }
        if (retired.Count > 0) Atomic(Child(backup, "previous-program-manifest.json"), new { previousPackage = previous, files = retired });
    }
    internal static void RunScript(string package, string name, Action<string>? log = null)
    {
        if (name == "Start-Backend.ps1") { Backend.Start(package, log); return; }
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = package, RedirectStandardOutput = true, RedirectStandardError = true };
        // PowerShell 7's inherited module paths can hide Windows PowerShell 5's
        // Get-FileHash and other built-ins. Let this child rebuild its own defaults.
        start.Environment.Remove("PSModulePath");
        foreach (string a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Child(package, "scripts/" + name), "-NonInteractive" }) start.ArgumentList.Add(a);
        using var p = Process.Start(start) ?? throw new IOException("无法启动安装助手。");
        using var cancellation = new CancellationTokenSource();
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        var readErrors = new List<Exception>(); var outputLock = new object();
        async Task Collect(StreamReader reader, StringBuilder text, CancellationToken token)
        {
            char[] buffer = new char[4096];
            try
            {
                while (true)
                {
                    int length = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    if (length == 0) return;
                    lock (outputLock) text.Append(buffer, 0, length);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            { if (!token.IsCancellationRequested) lock (outputLock) readErrors.Add(ex); }
        }
        var reading = Task.WhenAll(Collect(p.StandardOutput, stdout, cancellation.Token), Collect(p.StandardError, stderr, cancellation.Token));
        // A detached backend can inherit the redirected pipe handles. Its lifetime
        // must not keep installation waiting for EOF after the script has exited.
        while (!p.WaitForExit(1000)) { }
        if (!reading.Wait(1000))
        {
            cancellation.Cancel();
            reading.Wait(250);
        }
        string output, error; Exception? readError;
        lock (outputLock) { output = stdout.ToString().Trim(); error = stderr.ToString().Trim(); readError = readErrors.FirstOrDefault(); }
        log?.Invoke(output);
        if (p.ExitCode != 0) throw new IOException(error.Length > 0 ? error : output.Length > 0 ? output : "安装助手退出，代码：" + p.ExitCode);
        if (readError != null) throw new IOException("无法读取安装助手的输出。", readError);
        if (error.Length > 0) log?.Invoke(error);
    }
    internal static InstallResult Install(string game, string root, bool shortcut, Action<int, string> progress, int failAfter = -1, bool register = true)
    {
        game = Path.GetFullPath(game.Trim().Trim('"')); root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(game, "MonsterHunterWilds.exe"))) throw new IOException("请选择包含 MonsterHunterWilds.exe 的游戏目录。");
        if (!File.Exists(Path.Combine(game, "dinput8.dll"))) throw new IOException("这个游戏目录尚未安装 REFramework。请先安装支持当前游戏版本的 REFramework，再运行本安装器。");
        if (System.Version.TryParse(InstalledVersion(game), out var installed) && installed > System.Version.Parse(Version))
            throw new InvalidOperationException("已安装较新版本 " + installed + "，本安装器不会将它降级为 " + Version + "。");
        AssertClosed(); RejectLinks(root); RejectLinks(game);
        using var mutex = new Mutex(false, @"Local\MotionVfxLiveInstaller_" + Key(game));
        bool owned;
        try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("另一个安装程序正在处理这个游戏目录。");
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6];
        string package = Child(root, "versions/" + Version + "-" + stamp);
        string backup = Child(game, "reframework/data/MotionVfxLive/backups/setup-" + stamp);
        var changes = new List<(string Target, string? Backup)>();
        bool filesCommitted = false;
        var log = new StringBuilder();
        void Report(int percent, string message) { log.AppendLine(message); progress(percent, message); }
        try
        {
            Report(5, "正在校验并解压内置程序…"); ExtractAndVerify(package);
            ValidatePluginMap(package);
            foreach (var item in PluginMap()) { if (!File.Exists(Child(package, item.Source))) throw new IOException("缺少组件：" + item.Source); Child(game, item.Target); }
            string exports = Child(root, "Exports"), local = Child(root, "LocalFiles");
            Directory.CreateDirectory(exports); Directory.CreateDirectory(local);
            Atomic(Child(package, "runtime-config.json"), new { gameRoot = game, output = exports, localAssets = local });
            Report(45, "正在保存旧版编辑文档并停止旧后台…");
            AssertClosed(); RunScript(package, "Stop-Backend.ps1", s => log.AppendLine(s));
            // Retain saved sessions from a previous installer that is currently stopped.
            var previous = Read(Child(root, "installation.json"));
            string? previousPackage = previous?["package"]?.GetValue<string>();
            string sessionPath = Child(package, "saved-session.json");
            string pendingSession = Child(root, "pending-session.json");
            if (!File.Exists(Child(package, "saved-session.json")) && previous?["package"] is JsonValue oldValue)
            {
                string old = oldValue.GetValue<string>();
                if (old.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(Child(old, "saved-session.json")))
                    CopyContent(Child(old, "saved-session.json"), sessionPath);
            }
            if (!File.Exists(sessionPath))
            {
                // An interrupted first installation has no installation.json yet.
                // Keep its saved document available to the next retry.
                string? recovery = File.Exists(pendingSession) ? pendingSession : Directory.EnumerateDirectories(Child(root, "versions"))
                    .Where(p => !p.Equals(package, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                    .Select(p => Child(p, "saved-session.json")).FirstOrDefault(File.Exists);
                if (recovery != null) CopyContent(recovery, sessionPath);
            }
            if (File.Exists(sessionPath)) Atomic(pendingSession, Read(sessionPath)!);
            Report(58, "正在备份并安装游戏组件…"); AssertClosed();
            void Replace(string source, string target, string backupPath)
            {
                RejectLinks(target);
                if (File.Exists(target) && Hash(source) == Hash(target)) return;
                string? saved = null;
                if (File.Exists(target)) { Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!); CopyContent(target, backupPath); saved = backupPath; }
                changes.Add((target, saved)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temporary = target + "." + stamp + ".tmp";
                try { CopyContent(source, temporary); File.Move(temporary, target, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                if (Hash(target) != Hash(source)) throw new IOException("写入校验失败：" + target);
                if (failAfter > 0 && changes.Count == failAfter) throw new IOException("Injected install failure for rollback testing.");
            }
            foreach (var item in PluginMap()) Replace(Child(package, item.Source), Child(game, item.Target), Child(backup, item.Target));
            var currentTargets = PluginMap().Select(p => p.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in PreviousProgramFiles(root, previousPackage))
            {
                string? relativeTarget = PreviousGameTarget(item.Relative);
                if (relativeTarget == null || currentTargets.Contains(relativeTarget)) continue;
                string target = Child(game, relativeTarget);
                if (!File.Exists(target) || !Hash(target).Equals(item.Hash, StringComparison.OrdinalIgnoreCase)) continue;
                string saved = Child(backup, relativeTarget); Directory.CreateDirectory(Path.GetDirectoryName(saved)!); CopyContent(target, saved);
                if (!Hash(target).Equals(item.Hash, StringComparison.OrdinalIgnoreCase)) continue;
                changes.Add((target, saved)); File.Delete(target);
            }
            RunScript(package, "Mcp-Config.ps1", s => log.AppendLine(s));
            foreach (string name in new[] { "mcp-config.json", "mcp-config.toml" })
                Replace(Child(package, name), Child(root, name), Child(backup, "application/" + name));
            string self = Environment.ProcessPath ?? throw new IOException("无法定位安装程序。");
            string launcher = Child(root, "MotionVfxLive.exe");
            if (!Path.GetFullPath(self).Equals(launcher, StringComparison.OrdinalIgnoreCase))
                Replace(self, launcher, Child(backup, "application/MotionVfxLive.exe"));
            var result = new InstallResult(game, root, package, backup, false);
            string next = Child(package, "installation-next.json"); Atomic(next, result);
            Replace(next, Child(root, "installation.json"), Child(backup, "application/installation.json"));
            Atomic(Child(backup, "manifest.json"), new { version = Version, installedAt = DateTimeOffset.UtcNow, files = changes.Select(c => new { destination = c.Target, backup = c.Backup, removed = !File.Exists(c.Target), sha256 = File.Exists(c.Target) ? Hash(c.Target) : null }) });
            filesCommitted = true;
            Report(88, "组件安装完成，正在启动编辑器后台…");
            try
            {
                RunScript(package, "Start-Backend.ps1", s => log.AppendLine(s)); result = result with { BackendReady = true };
                if (!File.Exists(sessionPath) && File.Exists(pendingSession))
                    File.Move(pendingSession, Child(root, "session-recovered-" + stamp + ".json"));
            }
            catch (Exception ex) { log.AppendLine("后台启动失败（文件已完整安装，可使用桌面入口重试）：" + ex.Message); }
            if (result.BackendReady)
            {
                try { RetirePreviousPrograms(root, previousPackage, backup, s => log.AppendLine(s)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                { log.AppendLine("旧目录保留：无法核验旧清单，未猜测删除。" + ex.Message); }
            }
            Atomic(Child(root, "installation.json"), result);
            try
            {
                if (register) { using var installs = Registry.CurrentUser.CreateSubKey(@"Software\MotionVfxLive\Installs"); installs.SetValue(Key(game), game); }
                if (shortcut) CreateShortcut(root, game);
            }
            catch (Exception ex) { log.AppendLine("快捷方式创建失败；可直接运行安装目录中的 MotionVfxLive.exe：" + ex.Message); }
            Report(100, result.BackendReady ? "安装完成 · 编辑器后台已就绪。" : "安装完成 · 后台尚未就绪，请查看安装日志。使用桌面入口可以重试。");
            return result;
        }
        catch
        {
            if (!filesCommitted)
            {
                foreach (var change in changes.AsEnumerable().Reverse())
                {
                    try
                    {
                        RejectLinks(change.Target);
                        if (change.Backup == null) { if (File.Exists(change.Target)) File.Delete(change.Target); }
                        else CopyContent(change.Backup, change.Target, true);
                    }
                    catch (Exception ex) { log.AppendLine("需手动恢复 " + change.Target + "：" + ex.Message); }
                }
                log.AppendLine("安装未完成；已尝试恢复被替换文件。编辑文档恢复副本仍保留在 " + package);
            }
            throw;
        }
        finally
        {
            try { Directory.CreateDirectory(root); File.WriteAllText(Child(root, "install-" + stamp + ".log"), log.ToString(), new UTF8Encoding(false)); } catch { }
            mutex.ReleaseMutex();
        }
    }
    internal static void Start(string root)
    {
        var install = Read(Child(root, "installation.json")) ?? throw new IOException("没有找到安装信息，请重新运行 Setup.exe。");
        string package = install["package"]!.GetValue<string>();
        if (!package.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("安装目录无效。");
        RunScript(package, "Start-Backend.ps1");
    }
    private static void CreateShortcut(string root, string game)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Type type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("无法使用系统快捷方式组件。");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(Path.Combine(desktop, "MotionVfx Live.lnk"));
            try { link.TargetPath = Child(root, "MotionVfxLive.exe"); link.Arguments = "--start"; link.WorkingDirectory = root; link.Description = "启动 MotionVfx Live 特效编辑器后台"; link.Save(); }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
}
