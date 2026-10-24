using System.Text.Json;
using System.Text.Json.Nodes;

namespace VfxStudio.LiveEditor;

internal sealed partial class EditorEngine
{
    private readonly Dictionary<string, PfbWorkspace> prefabDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly PfbWorkspace.Clipboard prefabClipboard = new();
    private PfbWorkspace? prefabDocument;

    private void PrefabOperation(string action, JsonObject request, JsonObject response)
    {
        if (action == "pfb_open")
        {
            string kind = OptionalText(request, "sourceKind") ?? "local_file";
            string path = Text(request, "path");
            string key = kind + ":" + (kind == "local_file" ? Path.GetFullPath(path) : PfbWorkspace.NormalizePrefabPath(path));
            if (!prefabDocuments.TryGetValue(key, out var candidate))
            {
                candidate = new PfbWorkspace(options, path, kind, prefabClipboard);
                // Fully parse and classify before replacing the active document.
                candidate.Info(); prefabDocuments.Add(key, candidate);
            }
            prefabDocument = candidate;
        }
        else
        {
            var doc = prefabDocument ?? throw new InvalidOperationException("请先在 PFB 编辑区打开文件。");
            switch (action)
            {
                case "pfb_publish": response["pfbPreview"] = PublishPrefab(request, doc); break;
                case "pfb_add": doc.Add(request); break;
                case "pfb_copy": doc.Copy(request); break;
                case "pfb_paste": doc.Paste(request); break;
                case "pfb_set": doc.Set(request); break;
                case "pfb_remove": doc.Remove(request); break;
                case "pfb_undo": doc.Undo(request); break;
                case "pfb_redo": doc.Redo(request); break;
                case "pfb_reset": doc.Reset(request); break;
                default: throw new ArgumentException("不支持的 PFB 操作。");
            }
        }
        response["pfbDocument"] = JsonSerializer.SerializeToNode(prefabDocument!.Info(), Host.Json);
    }

    private JsonObject PublishPrefab(JsonObject request, PfbWorkspace doc)
    {
        if (request["pfbRevision"]?.GetValue<long>() != doc.Revision) throw new InvalidOperationException("PFB 文档已变化，请重新应用。");
        if (options.GameRoot == null) throw new InvalidOperationException("未配置游戏目录。");
        var proof = request["proof"]?.AsObject() ?? throw new ArgumentException("缺少实际 PFB 持有者证明。");
        string target = PfbWorkspace.NormalizePrefabPath(Text(proof, "originalPath"));
        if (!(target.StartsWith("art/", StringComparison.OrdinalIgnoreCase) || target.StartsWith("gamedesign/", StringComparison.OrdinalIgnoreCase)) ||
            !target.EndsWith(".pfb", StringComparison.OrdinalIgnoreCase) || target.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("目标必须是完整的游戏 PFB 路径。");
        string statusPath = Path.Combine(options.Mailbox, "ui-status.json");
        if (!File.Exists(statusPath) || DateTime.UtcNow-File.GetLastWriteTimeUtc(statusPath)>TimeSpan.FromSeconds(20))
            throw new InvalidOperationException("游戏状态已离线，未发布 PFB。");
        var actor = JsonNode.Parse(File.ReadAllText(statusPath))?["captureActor"];
        foreach (string key in new[] { "sceneEpoch", "weaponId", "weaponType" })
            if (actor?[key] == null || actor[key]!.ToJsonString() != proof[key]?.ToJsonString()) throw new InvalidOperationException("场景或装备已变化，请重新核实目标。");
        if (actor?["characterIdentity"]?.ToJsonString() != proof["actorIdentity"]?.ToJsonString()) throw new InvalidOperationException("角色来源已变化。");
        var metadata = JsonSerializer.SerializeToNode(doc.Info(), Host.Json)!["previewMetadata"]!.DeepClone().AsObject();
        if (doc.SourceKind == "game_files" && !string.Equals(metadata["originalPath"]?.GetValue<string>(), target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前游戏 PFB 文档与选中的热重载目标不同。");
        metadata["originalPath"] = target;
        string id = Guid.NewGuid().ToString("N"), logical = "art/MotionVfxLive/PrefabPreview/" + id;
        string gameRoot = Path.GetFullPath(options.GameRoot);
        string directory = Path.GetFullPath(Path.Combine(gameRoot, "natives", "stm", logical.Replace('/',Path.DirectorySeparatorChar)));
        for (var dir = new DirectoryInfo(directory); dir != null; dir = dir.Parent)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("PFB 预览目录不能使用符号链接。");
            if (dir.FullName.Equals(gameRoot,StringComparison.OrdinalIgnoreCase)) break;
        }
        if (Directory.Exists(directory)) throw new IOException("PFB 预览版本已存在。");
        byte[] content = doc.ExportBytes();
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory,"resource.pfb.18");
        using(var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(content); stream.Flush(true); }
        if (!File.ReadAllBytes(output).AsSpan().SequenceEqual(content)) throw new IOException("PFB 发布回读失败。");
        var plan = new JsonObject { ["schema"]=1,["id"]=id,["revision"]=doc.Revision,["source"]=metadata,["proof"]=proof.DeepClone(),
            ["resources"]=new JsonArray(new JsonObject { ["kind"]="pfb",["originalPath"]=target,["path"]=logical+"/resource.pfb",["bytes"]=content.Length,["sha256"]=Hash(content) }) };
        Host.AtomicJson(Path.Combine(directory,"preview.json"),plan);
        return plan;
    }
}
