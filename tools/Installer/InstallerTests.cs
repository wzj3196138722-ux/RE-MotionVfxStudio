using System.Diagnostics;
using System.Text.Json.Nodes;

namespace MotionVfxSetup;

internal static class InstallerTests
{
    internal static int Run(string output, string? fixture, string? legacyInstaller, string? legacyPackage)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        string game = InstallEngine.Child(output, "模拟游戏 Game $ literal"); Directory.CreateDirectory(game);
        string app = InstallEngine.Child(output, "独立安装 App $ literal");
        string report = InstallEngine.Child(output, "installer-tests.json");
        var checks = new List<string>(); InstallResult? result = null;
        void Check(bool value, string text) { if (!value) throw new Exception(text); checks.Add(text); }
        try
        {
            CheckPowerShellEnvironment(output, Check);
            CheckPowerShellPipeLifetime(output, Check);
            CheckRetirement(output, Check);
            foreach (string invalid in new[] { "../escape", @"..\escape", @"C:\escape", "root:stream", "valid/../escape", "trailing./file", "trailing /file" })
            {
                bool blocked = false; try { InstallEngine.Child(app, invalid); } catch (IOException) { blocked = true; }
                Check(blocked, "Reject unsafe target: " + invalid);
            }
            File.WriteAllText(Path.Combine(game, "MonsterHunterWilds.exe"), "Isolated test marker, never executed.");
            bool needsFramework = false;
            try { InstallEngine.Install(game, app, false, (_, _) => { }, register: false); }
            catch (IOException ex) { needsFramework = ex.Message.Contains("REFramework"); }
            Check(needsFramework && !Directory.Exists(app), "Missing REFramework fails before mutation");
            File.WriteAllText(Path.Combine(game, "dinput8.dll"), "Existing REFramework marker, never loaded.");
            string old = InstallEngine.Child(game, "reframework/autorun/MotionVfxLive.lua"); Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            File.WriteAllText(old, "old lua content");
            string note = InstallEngine.Child(game, "reframework/data/MotionVfxLive/group-notes.json"); Directory.CreateDirectory(Path.GetDirectoryName(note)!);
            File.WriteAllText(note, "{\"note\":\"用户中文备注\"}"); string noteHash = InstallEngine.Hash(note);
            var untouched = new Dictionary<string, string>();
            foreach (string relative in new[] { "natives/stm/art/user-mod.efx.1", "reframework/autorun/OtherMod.lua", "reframework/autorun/MotionVfxLive/user-custom.lua", "reframework/data/MotionVfxLive/workbench-settings.json", "reframework/data/MotionVfxLive/recordings/user-recording.txt" })
            {
                string path = InstallEngine.Child(game, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "用户文件必须保留：" + relative); untouched[path] = InstallEngine.Hash(path);
            }
            bool failure = false;
            try { InstallEngine.Install(game, app, false, (_, _) => { }, failAfter: 2, register: false); }
            catch (IOException ex) { failure = ex.Message.Contains("Injected"); }
            Check(failure, "Injected mid-copy failure is reported");
            Check(File.ReadAllText(old) == "old lua content", "Partial installation restores overwritten files");
            Check(!File.Exists(InstallEngine.Child(game, "reframework/data/MotionVfxLive/Reload.lua")), "Partial installation removes newly added program files");
            Check(InstallEngine.Hash(note) == noteHash, "Rollback preserves unrelated user notes");
            result = InstallEngine.Install(game, app, false, (_, _) => { }, register: false);
            Check(result.BackendReady, "Self-contained backend starts from Chinese and space-containing paths");
            InstallEngine.ValidatePluginMap(result.Package);
            Check(true, "Every packaged production Lua module is covered by the installer map");
            foreach (var item in InstallEngine.PluginMap())
                Check(InstallEngine.Hash(InstallEngine.Child(result.Package, item.Source)) == InstallEngine.Hash(InstallEngine.Child(game, item.Target)), "Installed component hash: " + item.Target);
            Check(File.Exists(InstallEngine.Child(app, "MotionVfxLive.exe")), "Installed graphical launcher is present");
            var config = InstallEngine.Read(InstallEngine.Child(app, "mcp-config.json"));
            Check(config?["mcpServers"]?["motionvfx"]?["command"]?.GetValue<string>() == InstallEngine.Child(result.Package, "mcp/VfxStudio.Mcp.exe"), "MCP config targets installed standalone executable");
            Check(InstallEngine.Hash(note) == noteHash && File.ReadAllText(Path.Combine(game, "dinput8.dll")) == "Existing REFramework marker, never loaded.", "Install preserves REFramework and user data");
            var backend = InstallEngine.Read(InstallEngine.Child(game, "reframework/data/MotionVfxLive/status.json"));
            int pid = backend!["pid"]!.GetValue<int>();
            InstallEngine.Start(app);
            Check(InstallEngine.Read(InstallEngine.Child(game, "reframework/data/MotionVfxLive/status.json"))!["pid"]!.GetValue<int>() == pid, "Launcher reuses the ready backend instead of duplicating it");
            if (fixture != null)
            {
                string mailbox = InstallEngine.Child(game, "reframework/data/MotionVfxLive");
                var opened = Backend.Send(mailbox, new() { ["action"] = "open", ["path"] = Path.GetFullPath(fixture) });
                Backend.Send(mailbox, new() { ["action"] = "set", ["target"] = new JsonObject { ["kind"] = "entry", ["index"] = 0 },
                    ["path"] = "name", ["value"] = "安装恢复校验", ["revision"] = opened["revision"]!.DeepClone() });
                File.WriteAllText(old, "rollback update sentinel");
                bool updateFailed = false;
                try { InstallEngine.Install(game, app, false, (_, _) => { }, failAfter: 1, register: false); }
                catch (IOException ex) { updateFailed = ex.Message.Contains("Injected"); }
                Check(updateFailed && File.ReadAllText(old) == "rollback update sentinel", "Failed upgrade restores the previous Lua content");
                var pending = InstallEngine.Read(InstallEngine.Child(app, "pending-session.json"));
                Check(pending?["checkpoint"] is JsonValue v && File.Exists(v.GetValue<string>()), "Failed upgrade keeps a durable document recovery checkpoint");
            }
            File.WriteAllText(old, "second installed lua sentinel");
            result = InstallEngine.Install(game, app, false, (_, _) => { }, register: false);
            Check(result.BackendReady, "Reinstall safely replaces and restarts the installed backend");
            if (fixture != null)
            {
                var restored = Backend.Send(InstallEngine.Child(game, "reframework/data/MotionVfxLive"), new() { ["action"] = "status" });
                Check(restored["document"]?["entries"]?[0]?["name"]?.GetValue<string>() == "安装恢复校验", "Retry after failed upgrade restores the last edited document");
                Check(!File.Exists(InstallEngine.Child(app, "pending-session.json")), "Recovered checkpoint is archived rather than replayed on the next installation");
            }
            Check(File.ReadAllText(InstallEngine.Child(result.Backup, "reframework/autorun/MotionVfxLive.lua")) == "second installed lua sentinel", "Upgrade retains the exact prior program in its backup");
            Check(InstallEngine.Hash(note) == noteHash, "Reinstall leaves personal notes unchanged");
            foreach (var item in untouched) Check(File.Exists(item.Key) && InstallEngine.Hash(item.Key) == item.Value, "Install and reinstall preserve unrelated user file: " + Path.GetRelativePath(game, item.Key));
            InstallEngine.RunScript(result.Package, "Stop-Backend.ps1");
            Check(InstallEngine.Read(InstallEngine.Child(game, "reframework/data/MotionVfxLive/status.json"))!["ready"]!.GetValue<bool>() == false, "Test backend shuts down cleanly");
            result = null;
            if (legacyInstaller != null) CheckLegacyUpgrade(output, legacyInstaller, fixture, Check);
            if (legacyPackage != null) CheckPortableUpgrade(output, legacyPackage, fixture, Check);
            InstallEngine.Atomic(report, new { passed = true, count = checks.Count, checks, gameTouched = false, registryTouched = false, desktopShortcutCreated = false });
            return 0;
        }
        catch (Exception ex) { InstallEngine.Atomic(report, new { passed = false, checks, error = ex.ToString() }); return 1; }
        finally { if (result != null) try { InstallEngine.RunScript(result.Package, "Stop-Backend.ps1"); } catch { } }
    }
    private static void CheckPowerShellEnvironment(string output, Action<bool, string> check)
    {
        string package = InstallEngine.Child(output, "powershell-environment"), script = InstallEngine.Child(package, "scripts/Probe-Hash.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "param([switch]$NonInteractive)\n$ErrorActionPreference='Stop'\nif($env:PSModulePath -like '*MotionVfx-Incompatible*'){throw 'Inherited incompatible module path'}\n(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash\n");
        string? original = Environment.GetEnvironmentVariable("PSModulePath"), observed = null;
        string incompatible = InstallEngine.Child(output, "MotionVfx-Incompatible");
        try
        {
            Environment.SetEnvironmentVariable("PSModulePath", incompatible + ";" + original);
            InstallEngine.RunScript(package, "Probe-Hash.ps1", text => observed = text);
            check(observed == InstallEngine.Hash(script), "Windows PowerShell helper resolves Get-FileHash despite an incompatible inherited module path");
            check(Environment.GetEnvironmentVariable("PSModulePath") == incompatible + ";" + original, "Module-path normalization changes only the helper process environment");
        }
        finally { Environment.SetEnvironmentVariable("PSModulePath", original); }
    }
    private static void CheckPowerShellPipeLifetime(string output, Action<bool, string> check)
    {
        string package = InstallEngine.Child(output, "powershell-pipe-lifetime");
        foreach (int exitCode in new[] { 0, 17 })
        {
            string name = "Probe-Pipe-" + exitCode + ".ps1", script = InstallEngine.Child(package, "scripts/" + name);
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, $$"""
                param([switch]$NonInteractive)
                $ErrorActionPreference='Stop'
                $child=Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList '-NoLogo -NoProfile -NonInteractive -Command "Start-Sleep -Seconds 8"' -WindowStyle Hidden -PassThru -RedirectStandardOutput ($PSCommandPath+'.child.out') -RedirectStandardError ($PSCommandPath+'.child.err')
                [IO.File]::WriteAllText(($PSCommandPath+'.pid'),[string]$child.Id)
                [Console]::Out.Write('parent-output-without-newline')
                [Console]::Error.Write('parent-error-without-newline')
                exit {{exitCode}}
                """);
            var logs = new List<string>(); IOException? failure = null;
            var elapsed = Stopwatch.StartNew();
            try { InstallEngine.RunScript(package, name, logs.Add); }
            catch (IOException ex) { failure = ex; }
            elapsed.Stop();
            check(elapsed.Elapsed < TimeSpan.FromSeconds(6), "Helper returns before its detached child closes inherited output pipes: " + exitCode);
            using var child = Process.GetProcessById(int.Parse(File.ReadAllText(script + ".pid")));
            check(!child.HasExited, "Output-drain timeout leaves the detached child running: " + exitCode);
            check(logs.Any(text => text.Contains("parent-output-without-newline")), "Partial stdout is retained without waiting for a newline or EOF: " + exitCode);
            if (exitCode == 0)
                check(failure == null && logs.Any(text => text.Contains("parent-error-without-newline")), "Successful helper preserves captured stderr in its log");
            else
                check(failure?.Message.Contains("parent-error-without-newline") == true, "Nonzero helper exit still reports partial stderr while a detached child remains alive");
            // The finite-lived fixture exits itself; never terminate a detached backend.
        }
    }
    private static void CheckRetirement(string output, Action<bool, string> check)
    {
        string root = InstallEngine.Child(output, "retirement-policy"), package = InstallEngine.Child(root, "versions/0.1.0-fixture"), backup = InstallEngine.Child(root, "backup");
        Directory.CreateDirectory(package);
        InstallEngine.Atomic(InstallEngine.Child(package, "release.json"), new { name = "MotionVfx Live", version = "0.1.0" });
        var manifest = new JsonArray();
        foreach (string relative in new[] { "backend/program.dll", "backend/modified.dll", "plugin/MotionVfxLive/fx_retired.lua", "Exports/saved.efx", "LocalFiles/source.tex", "saved-session.json" })
        {
            string path = InstallEngine.Child(package, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "old original " + relative);
            manifest.Add(new JsonObject { ["path"] = relative, ["sha256"] = InstallEngine.Hash(path), ["size"] = new FileInfo(path).Length });
        }
        InstallEngine.Atomic(InstallEngine.Child(package, "SHA256.json"), manifest);
        File.WriteAllText(InstallEngine.Child(package, "backend/modified.dll"), "user changed program file");
        File.WriteAllText(InstallEngine.Child(package, "backend/unknown.bin"), "unlisted file");
        InstallEngine.RetirePreviousPrograms(root, package, backup, _ => { });
        check(!File.Exists(InstallEngine.Child(package, "backend/program.dll")) && File.Exists(InstallEngine.Child(backup, "previous-program/backend/program.dll")), "Retires only matching old program bytes after preserving a backup");
        foreach (string relative in new[] { "backend/modified.dll", "backend/unknown.bin", "Exports/saved.efx", "LocalFiles/source.tex", "saved-session.json" })
            check(File.Exists(InstallEngine.Child(package, relative)), "Retirement preserves modified, unknown or user data file: " + relative);
        check(InstallEngine.PreviousGameTarget("plugin/MotionVfxLive/fx_retired.lua") == "reframework/autorun/MotionVfxLive/fx_retired.lua" && InstallEngine.PreviousGameTarget("plugin/OtherMod.lua") == null, "Obsolete game-file cleanup is restricted to editor-owned paths");
    }
    private static void CheckPortableUpgrade(string output, string legacyPackage, string? fixture, Action<bool, string> check)
    {
        string area = InstallEngine.Child(output, "portable-upgrade"), game = InstallEngine.Child(area, "模拟旧版游戏"), app = InstallEngine.Child(area, "new-install"), old = InstallEngine.Child(area, "原始便携包");
        Directory.CreateDirectory(game); Directory.CreateDirectory(old);
        File.WriteAllText(InstallEngine.Child(game, "MonsterHunterWilds.exe"), "Not an executable.");
        File.WriteAllText(InstallEngine.Child(game, "dinput8.dll"), "Existing REFramework fixture.");
        foreach (string directory in new[] { "backend", "mcp", "scripts", "plugin" })
        {
            string source = InstallEngine.Child(Path.GetFullPath(legacyPackage), directory);
            foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = InstallEngine.Child(old, directory + "/" + Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); InstallEngine.CopyContent(path, target);
            }
        }
        InstallEngine.CopyContent(InstallEngine.Child(Path.GetFullPath(legacyPackage), "release.json"), InstallEngine.Child(old, "release.json"));
        string exports = InstallEngine.Child(old, "Exports"), local = InstallEngine.Child(old, "LocalFiles");
        InstallEngine.Atomic(InstallEngine.Child(old, "runtime-config.json"), new { gameRoot = game, output = exports, localAssets = local });
        string current = old;
        try
        {
            InstallEngine.RunScript(old, "Install.ps1");
            string oldVersion = InstallEngine.InstalledVersion(game) ?? throw new Exception("Portable version was not detected.");
            check(!File.Exists(InstallEngine.Child(app, "installation.json")), "Portable fixture has no EXE installation registration: " + oldVersion);
            var retained = new Dictionary<string, string>();
            foreach (string path in new[] { InstallEngine.Child(exports, "用户导出.efx"), InstallEngine.Child(local, "用户素材.tex"), InstallEngine.Child(game, "reframework/data/MotionVfxLive/group-notes.json") })
            { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{\"note\":\"原便携版用户内容\"}"); retained[path] = InstallEngine.Hash(path); }
            string mailbox = InstallEngine.Child(game, "reframework/data/MotionVfxLive");
            if (fixture != null)
            {
                var opened = Backend.Send(mailbox, new() { ["action"] = "open", ["path"] = Path.GetFullPath(fixture) });
                Backend.Send(mailbox, new() { ["action"] = "set", ["revision"] = opened["revision"]!.DeepClone(), ["target"] = new JsonObject { ["kind"] = "entry", ["index"] = 0 }, ["path"] = "name", ["value"] = "从便携版升级保留" });
            }
            var result = InstallEngine.Install(game, app, false, (_, _) => { }, register: false); current = result.Package;
            check(result.BackendReady && InstallEngine.InstalledVersion(game) == InstallEngine.Version, "Original portable " + oldVersion + " upgrades without previous installation metadata");
            foreach (var item in retained) check(File.Exists(item.Key) && InstallEngine.Hash(item.Key) == item.Value, "Portable upgrade preserves original user bytes: " + Path.GetRelativePath(area, item.Key));
            if (fixture != null) check(Backend.Send(mailbox, new() { ["action"] = "status" })["document"]?["entries"]?[0]?["name"]?.GetValue<string>() == "从便携版升级保留", "Portable backend's edited document is restored by the new EXE installation");
        }
        finally { InstallEngine.RunScript(current, "Stop-Backend.ps1"); }
    }
    private static void CheckLegacyUpgrade(string output, string legacyInstaller, string? fixture, Action<bool,string> check)
    {
        string legacy = InstallEngine.Child(output, "legacy-upgrade");
        var start = new ProcessStartInfo(Path.GetFullPath(legacyInstaller)) { UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden };
        // Reproduce a normal Explorer launch, not this test runner's pwsh modules.
        start.Environment.Remove("PSModulePath");
        foreach(string arg in new[]{"--self-test","--output",legacy}) start.ArgumentList.Add(arg);
        if(fixture!=null) {start.ArgumentList.Add("--fixture");start.ArgumentList.Add(Path.GetFullPath(fixture));}
        using(var process=Process.Start(start) ?? throw new IOException("Old installer did not start."))
        { if(!process.WaitForExit(240000)) throw new IOException("Old installer test timed out."); check(process.ExitCode==0,"Original released installer establishes a verified legacy installation"); }
        string game=InstallEngine.Child(legacy,"模拟游戏 Game $ literal"), app=InstallEngine.Child(legacy,"独立安装 App $ literal");
        string mailbox=InstallEngine.Child(game,"reframework/data/MotionVfxLive");
        string previous=InstallEngine.Read(InstallEngine.Child(app,"installation.json"))!["package"]!.GetValue<string>();
        string oldVersion=InstallEngine.InstalledVersion(game) ?? throw new Exception("Legacy version not detected.");
        check(oldVersion!=InstallEngine.Version,"Upgrade fixture uses a different released version: "+oldVersion);
        var retained=new Dictionary<string,string>();
        void Keep(string path,string content){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,content);retained[path]=InstallEngine.Hash(path);}
        Keep(InstallEngine.Child(mailbox,"workbench-settings.json"),"{\"scale\":1.15,\"language\":\"zh\"}");
        Keep(InstallEngine.Child(mailbox,"debug-input-settings.json"),"{\"keys\":[17,16,18,80]}");
        Keep(InstallEngine.Child(app,"Exports/用户导出.txt"),"keep export");
        Keep(InstallEngine.Child(app,"LocalFiles/用户素材.txt"),"keep source");
        string notes=InstallEngine.Child(mailbox,"group-notes.json");retained[notes]=InstallEngine.Hash(notes);
        var oldManifest = InstallEngine.Read(InstallEngine.Child(previous, "SHA256.json"))!.AsArray();
        foreach (string name in new[] { "fx_retired_test", "fx_modified_test" })
        {
            string relative = "plugin/MotionVfxLive/" + name + ".lua", source = InstallEngine.Child(previous, relative);
            File.WriteAllText(source, "-- formerly managed module");
            oldManifest.Add(new JsonObject { ["path"] = relative, ["sha256"] = InstallEngine.Hash(source), ["size"] = new FileInfo(source).Length });
            string target = InstallEngine.Child(game, "reframework/autorun/MotionVfxLive/" + name + ".lua");
            InstallEngine.CopyContent(source, target);
            if (name == "fx_modified_test") Keep(target, "-- user-modified module must survive");
        }
        InstallEngine.Atomic(InstallEngine.Child(previous, "SHA256.json"), oldManifest);
        string currentPackage=previous;
        try
        {
            InstallEngine.Start(app);
            JsonNode? cacheId=null;
            if(fixture!=null)
            {
                var opened=Backend.Send(mailbox,new(){["action"]="open",["path"]=Path.GetFullPath(fixture)});
                Backend.Send(mailbox,new(){["action"]="set",["revision"]=opened["revision"]!.DeepClone(),["target"]=new JsonObject{["kind"]="entry",["index"]=0},["path"]="name",["value"]="从旧版升级保留"});
                var cached=Backend.Send(mailbox,new(){["action"]="cache_import",["path"]=Path.GetFullPath(fixture),["alias"]="art/UpgradeTest/保留素材.efx"});
                cacheId=cached["cacheEntry"]!["id"]!.DeepClone();
                foreach(string path in Directory.EnumerateFiles(InstallEngine.Child(mailbox,"precache"),"*",SearchOption.AllDirectories)) retained[path]=InstallEngine.Hash(path);
            }
            var upgraded=InstallEngine.Install(game,app,false,(_,_)=>{},register:false);currentPackage=upgraded.Package;
            check(upgraded.BackendReady && InstallEngine.InstalledVersion(game)==InstallEngine.Version,"Released "+oldVersion+" upgrades to "+InstallEngine.Version+" with a ready backend");
            check(Directory.Exists(previous),"Original version folder remains available after upgrade");
            check(!File.Exists(InstallEngine.Child(previous, "backend/VfxStudio.LiveEditor.exe")) && File.Exists(InstallEngine.Child(upgraded.Backup, "previous-program/backend/VfxStudio.LiveEditor.exe")), "Old backend program is retired to backup after the new backend is ready");
            check(!File.Exists(InstallEngine.Child(game, "reframework/autorun/MotionVfxLive/fx_retired_test.lua")) && File.Exists(InstallEngine.Child(upgraded.Backup, "reframework/autorun/MotionVfxLive/fx_retired_test.lua")), "Removed managed game module is cleaned with an exact-byte backup");
            foreach(var pair in retained) check(File.Exists(pair.Key)&&InstallEngine.Hash(pair.Key)==pair.Value,"Upgrade preserves user bytes: "+Path.GetRelativePath(legacy,pair.Key));
            if(fixture!=null)
            {
                check(Backend.Send(mailbox,new(){["action"]="status"})["document"]?["entries"]?[0]?["name"]?.GetValue<string>()=="从旧版升级保留","Upgrade restores the running old backend's edited document");
                check(Backend.Send(mailbox,new(){["action"]="cache_list"})["cache"]!.AsArray().Any(e=>e?["id"]?.ToJsonString()==cacheId!.ToJsonString()),"Imported old-version cache entry remains readable after upgrade");
            }
            string lua=InstallEngine.Child(game,"reframework/autorun/MotionVfxLive.lua"), saved=File.ReadAllText(lua);
            try
            {
                File.WriteAllText(lua,"local M = { version = \"99.0.0\" }");
                bool rejected=false;try{InstallEngine.Install(game,app,false,(_,_)=>{},register:false);}catch(InvalidOperationException ex){rejected=ex.Message.Contains("降级");}
                check(rejected && File.ReadAllText(lua).Contains("99.0.0"),"Installer refuses to overwrite a newer installed version");
            }
            finally {File.WriteAllText(lua,saved);}
        }
        finally {InstallEngine.RunScript(currentPackage,"Stop-Backend.ps1");}
    }
}
