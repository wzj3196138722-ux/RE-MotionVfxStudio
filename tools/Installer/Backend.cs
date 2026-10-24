using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MotionVfxSetup;

internal static class Backend
{
    private static JsonNode? Live(string mailbox)
    {
        var s = InstallEngine.Read(Path.Combine(mailbox, "status.json"));
        long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (s?["heartbeatUnixSeconds"]?.GetValue<long>() ?? 0);
        return s?["ready"]?.GetValue<bool>() == true && age is >= -5 and <= 8 ? s : null;
    }
    internal static JsonNode Send(string mailbox, JsonObject request)
    {
        request["id"] = "setup-" + Guid.NewGuid().ToString("N");
        InstallEngine.Atomic(Path.Combine(mailbox, "request.json"), request);
        for (int i = 0; i < 300; i++)
        {
            Thread.Sleep(100);
            JsonNode? response;
            try { response = InstallEngine.Read(Path.Combine(mailbox, "response.json")); } catch (IOException) { continue; }
            if (response?["id"]?.ToString() != request["id"]!.ToString()) continue;
            if (response["ok"]?.GetValue<bool>() != true) throw new IOException("后台拒绝操作：" + response["error"]);
            return response;
        }
        throw new IOException("后台响应超时。未重复启动或重复编辑。");
    }
    internal static void Start(string package, Action<string>? log)
    {
        var config = InstallEngine.Read(InstallEngine.Child(package, "runtime-config.json")) ?? throw new IOException("安装配置不存在。");
        string game = config["gameRoot"]!.GetValue<string>();
        if (!File.Exists(Path.Combine(game, "MonsterHunterWilds.exe"))) throw new IOException("游戏目录不存在，请重新运行安装器选择目录。");
        string mailbox = InstallEngine.Child(game, "reframework/data/MotionVfxLive");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mailbox.ToLowerInvariant())));
        using var mutex = new Mutex(false, @"Local\MotionVfxLiveBackendStart_" + key);
        bool owned; try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("后台正在启动，请稍后再试。");
        try
        {
            var status = Live(mailbox);
            string? version = InstallEngine.Read(InstallEngine.Child(package, "release.json"))?["backendVersion"]?.GetValue<string>();
            if (status != null)
            {
                if (status["version"]?.GetValue<string>() != version) throw new IOException("其他版本后台仍在运行，请先用安装器更新。");
                log?.Invoke("编辑器后台已就绪。"); return;
            }
            Directory.CreateDirectory(mailbox);
            string lockPath = Path.Combine(mailbox, "backend.lock");
            if (File.Exists(lockPath))
            {
                try { using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None); }
                catch (IOException) { throw new IOException("已有后台占用编辑目录，但尚未就绪；未重复启动。"); }
            }
            var start = new ProcessStartInfo(InstallEngine.Child(package, "backend/VfxStudio.LiveEditor.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = package };
            foreach (string value in new[] { "--mailbox", mailbox, "--assets", config["localAssets"]!.GetValue<string>(), "--output", config["output"]!.GetValue<string>(), "--game-root", game, "--log-directory", mailbox })
                start.ArgumentList.Add(value);
            using var process = Process.Start(start) ?? throw new IOException("无法启动后台。");
            bool ready = false;
            for (int i = 0; i < 80; i++)
            {
                Thread.Sleep(100);
                if (process.HasExited) throw new IOException("后台启动失败，请查看游戏目录 MotionVfxLive/backend.stderr.log。");
                if (Live(mailbox)?["pid"]?.GetValue<int>() == process.Id) { ready = true; break; }
            }
            if (!ready) throw new IOException("后台尚未确认就绪；未重复启动。");
            string sessionPath = InstallEngine.Child(package, "saved-session.json");
            var session = InstallEngine.Read(sessionPath);
            if (session?["checkpoint"] is JsonValue saved)
            {
                string checkpoint = saved.GetValue<string>();
                var opened = Send(mailbox, new() { ["action"] = "open", ["path"] = checkpoint });
                var document = session["document"];
                if (document?["sourceKind"]?.GetValue<string>() == "game_files" && document["gameSource"] != null)
                {
                    try
                    {
                        var epoch = InstallEngine.Read(Path.Combine(mailbox, "ui-status.json"))?["captureActor"]?["sceneEpoch"]?.DeepClone()
                            ?? throw new IOException("尚无可核验的角色动作记录");
                        Send(mailbox, new() { ["action"] = "restore_game_checkpoint", ["checkpointPath"] = checkpoint, ["sourceDescriptor"] = document["gameSource"]!.DeepClone(),
                            ["currentSceneEpoch"] = epoch, ["expectedContentHash"] = document["gameSource"]!["contentHash"]?.DeepClone(), ["revision"] = opened["revision"]!.DeepClone() });
                    }
                    catch (Exception ex) { log?.Invoke("编辑副本已恢复，游戏来源需从动作列表重新选择：" + ex.Message); }
                }
            }
            if (File.Exists(sessionPath)) File.Move(sessionPath, InstallEngine.Child(package, "saved-session-restored-" + Guid.NewGuid().ToString("N") + ".json"));
            log?.Invoke("编辑器后台已启动。现在可以在游戏内使用 MotionVfx Live。");
        }
        finally { mutex.ReleaseMutex(); }
    }
}
