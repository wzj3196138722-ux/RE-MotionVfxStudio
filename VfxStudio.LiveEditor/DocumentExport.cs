using System.Text.Json;
using System.Text.Json.Nodes;

namespace VfxStudio.LiveEditor;

internal sealed partial class EditorEngine
{
    private sealed record ExportItem(string Key, string Name, string Label, byte[] Content, string? ProtectedPath);
    private sealed record ExportPlan(string Token, ExportItem[] Items, long Revision);
    private ExportPlan? exportPlan;

    private JsonNode BeginExport(JsonObject request)
    {
        var prepared = PrepareExport(request);
        var desktopRequest = (JsonObject)prepared.DeepClone();
        desktopRequest["mode"] = "export";
        return JsonSerializer.SerializeToNode((desktop ??= new(options)).Start(desktopRequest), Host.Json)!;
    }

    private JsonObject PrepareExport(JsonObject request)
    {
        string scope = OptionalText(request, "scope") ?? "efx";
        if (scope is not ("efx" or "action" or "pfb")) throw new ArgumentException("未知导出类型。");
        if (scope == "efx") { Require(); CheckRevision(request); }
        var items = new List<ExportItem>();
        if (scope == "efx")
        {
            string name = Path.GetFileName(source!);
            if (!name.EndsWith($".efx.{(int)document!.Header.Version}", StringComparison.OrdinalIgnoreCase)) name += $".{(int)document.Header.Version}";
            items.Add(new("efx", name, "当前特效文件", ResolvePrecache(Validate(Require(), source!), source!), sourceKind == "local_file" ? source : null));
        }
        if (scope == "action")
        {
            if (actionWorkspace == null) throw new InvalidOperationException("请先选择已核实来源的动作并添加槽位。");
            foreach (var item in actionWorkspace.Export()) items.Add(new(item.Key, item.Name, item.Label, item.Content, null));
        }
        if (scope == "pfb")
        {
            var doc = prefabDocument ?? throw new InvalidOperationException("请先打开 PFB 文件。");
            string name = Path.GetFileName(doc.SourcePath);
            if (!name.EndsWith(".pfb.18", StringComparison.OrdinalIgnoreCase)) name += name.EndsWith(".pfb", StringComparison.OrdinalIgnoreCase) ? ".18" : ".pfb.18";
            items.Add(new("pfb", name, "当前 PFB 文件", doc.ExportBytes(), doc.SourceKind == "local_file" ? Path.GetFullPath(doc.SourcePath) : null));
        }
        string token = Guid.NewGuid().ToString("N");
        var prepared = new JsonObject { ["exportToken"] = token,
            ["items"] = JsonSerializer.SerializeToNode(items.Select(i => new { i.Key, i.Name, i.Label }), Host.Json) };
        exportPlan = new(token, items.ToArray(), revision);
        return prepared;
    }

    private JsonArray FinishExport(JsonObject request)
    {
        var plan = exportPlan;
        if (plan == null || Text(request, "exportToken") != plan.Token) throw new InvalidOperationException("导出选择已过期；请重新导出。");
        var destinations = request["destinations"]?.AsArray() ?? throw new ArgumentException("Missing destinations.");
        var targets = new List<(ExportItem Item, string Path, string? Expected)>();
        foreach (var node in destinations)
        {
            var item = plan.Items.Single(i => i.Key == node!["key"]!.GetValue<string>());
            string path = Path.GetFullPath(node!["path"]!.GetValue<string>());
            if (string.Equals(path, item.ProtectedPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能覆盖当前导入的原文件，请另选导出位置。");
            string suffix = item.Name[item.Name.LastIndexOf('.', item.Name.LastIndexOf('.') - 1)..];
            if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("文件后缀应为 " + suffix);
            string? expected = node["expectedHash"]?.GetValue<string>();
            if (File.Exists(path) ? expected == null || Hash(File.ReadAllBytes(path)) != expected : expected != null)
                throw new IOException("目标文件在选择后发生变化，请重新导出：" + path);
            if (targets.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase) || t.Item.Key == item.Key)) throw new ArgumentException("导出位置不能重复。");
            targets.Add((item, path, expected));
        }
        var result = new JsonArray();
        var staged = new List<string>();
        try
        {
            // Prepare every selected destination before publishing any file.
            // A missing/unwritable second location must not export the first.
            foreach (var target in targets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
                string temporary = target.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                staged.Add(temporary);
                File.WriteAllBytes(temporary, target.Item.Content);
            }
            for (int i=0;i<targets.Count;i++)
            {
                var target=targets[i]; string path=target.Path;
                // Recheck immediately before commit. Picker consent applies only
                // to the exact existing file the user saw.
                if (target.Expected != null && (!File.Exists(path) || Hash(File.ReadAllBytes(path)) != target.Expected)) throw new IOException("目标文件已变化。");
                File.Move(staged[i], path, target.Expected != null);
                result.Add(path);
            }
        }
        catch (Exception ex) when (result.Count>0)
        {
            exportPlan=null;
            throw new IOException("部分文件已导出："+string.Join("；",result.Select(n=>n!.GetValue<string>()))+"。其余文件未完成："+ex.Message,ex);
        }
        finally { foreach (string path in staged) if (File.Exists(path)) File.Delete(path); }
        exportPlan = null;
        return result;
    }
}
