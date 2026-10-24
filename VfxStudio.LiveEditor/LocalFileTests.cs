using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ReeLib;

namespace VfxStudio.LiveEditor;

internal static class LocalFileTests
{
    internal static void Run(Options options, string output, Action<bool, string> check)
    {
        string root = Path.Combine(output, "本地文件 回归");
        Directory.CreateDirectory(root);
        var opts = options with { Mailbox = Path.Combine(root, "mailbox"), Output = Path.Combine(root, "exports") };
        using var engine = new EditorEngine(opts);
        int sequence = 0;
        long revision = 0;
        JsonObject Call(string action, JsonObject data, bool ok = true)
        {
            data["id"] = "local-" + ++sequence; data["action"] = action; data["revision"] = revision;
            var result = engine.Handle(data);
            check(result["ok"]!.GetValue<bool>() == ok, action + " local file result: " + (result["error"]?.ToString() ?? "ok"));
            revision = result["revision"]!.GetValue<long>();
            return result;
        }
        var original = Directory.EnumerateFiles(options.Assets, "*.efx.5571972", SearchOption.AllDirectories).First();
        string first = Path.Combine(root, "第一个.efx"), second = Path.Combine(root, "第二个.efx.5571972");
        File.WriteAllBytes(first, File.ReadAllBytes(original));
        File.WriteAllBytes(second, File.ReadAllBytes(original));
        Call("open", new() { ["path"] = first });
        var opened = Call("open", new() { ["path"] = second });
        check(opened["document"]!["source"]!.ToString() == second, "Second local EFX replaces the first document source");
        Call("add_entry", new() { ["name"] = "保留的未保存修改" });
        var before = Call("status", new())["document"]!.ToJsonString();
        long beforeRevision = revision;
        string listPath = Directory.EnumerateFiles(options.Assets, "*.motlist.992", SearchOption.AllDirectories).First();
        string localList = Path.Combine(root, "双刀动作.motlist");
        byte[] listBytes = File.ReadAllBytes(listPath);
        File.WriteAllBytes(localList, listBytes);
        var response = Call("open_motion", new() { ["path"] = localList });
        var motions = response["motionDocument"]!["motions"]!.AsArray();
        check(motions.Count > 0 && motions.Any(m => m!["frameCount"]?.GetValue<float>() > 0), "Real MOTLIST exposes nonempty motions and durations");
        check(motions.Any(m => m!["slots"]!.AsArray().Count > 0), "Real MOTLIST exposes native effect trigger tracks");
        check(response["document"]!.ToJsonString() == before && revision == beforeRevision, "Motion loading does not modify dirty EFX, revision, or history");
        check(SHA256.HashData(File.ReadAllBytes(localList)).SequenceEqual(SHA256.HashData(listBytes)), "Local motion inspection never rewrites the source");
        using var handler = new FileHandler(new MemoryStream(listBytes), listPath);
        using var list = new MotlistFile(handler);
        list.Read();
        check(motions.Select(m => m!["motionId"]!.GetValue<uint>()).SequenceEqual(list.Motions.Select(m => (uint)m.motNumber)), "Motion IDs retain native numbering, including empty slots");
        var mot = list.MotFiles.OfType<MotFile>().First(m => m.Header.frameCount > 0);
        string motPath = Path.Combine(root, "独立动作.mot." + (int)mot.Header.version);
        mot.WriteTo(motPath);
        var standalone = Call("open_motion", new() { ["path"] = motPath })["motionDocument"]!["motions"]!.AsArray();
        check(standalone.Count == 1 && standalone[0]!["name"]!.ToString() == mot.Name && standalone[0]!["motionId"] == null,
            "Standalone MOT opens and never invents a bank Motion ID");
        string bad = Path.Combine(root, "损坏.efx"); File.WriteAllBytes(bad, new byte[32]);
        var failed = Call("open", new() { ["path"] = bad }, false);
        check(failed["document"]!.ToJsonString() == before, "Invalid second EFX preserves dirty first document");
        string badMot = Path.Combine(root, "损坏.mot.932"); File.WriteAllBytes(badMot, new byte[32]);
        var failedMot = Call("open_motion", new() { ["path"] = badMot }, false);
        check(failedMot["motionDocument"] == null && failedMot["document"]!.ToJsonString() == before, "Invalid motion publishes no partial view or EFX mutation");
        string mismatch = Path.Combine(root, "错误版本.motlist.999"); File.WriteAllBytes(mismatch, listBytes);
        Call("open_motion", new() { ["path"] = mismatch }, false);
        Call("open_motion", new() { ["path"] = Path.Combine(root, "missing.mot.932") }, false);
        Call("undo", new());
        check(Call("status", new())["document"]!["entries"]!.AsArray().All(e => e!["name"]!.ToString() != "保留的未保存修改"), "EFX undo remains available after local motion browsing");
    }
}
