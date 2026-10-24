using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReeLib.Efx.Structs.Common;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class ExpressionInspectorTests
{
    public static void Run(Options options, string output, Action<bool, string> check)
    {
        check(ContentInspector.Format((EfxExpressionFunction)17) == "SmoothStep" &&
            ContentInspector.Format((EfxExpressionFunction)16) == "Clamp", "Live expression function names agree with reference plugin numeric opcodes");
        check((int)(EfxExpressionFunction)ContentInspector.Parse("Clamp", typeof(EfxExpressionFunction))! == 16 &&
            (int)(EfxExpressionFunction)ContentInspector.Parse("SmoothStep", typeof(EfxExpressionFunction))! == 17,
            "Corrected expression names write their matching opcodes rather than legacy enum names");
        check(ContentInspector.Format((BinaryExpressionOperator)1) == "Mul" &&
            (int)(BinaryExpressionOperator)ContentInspector.Parse("Sub", typeof(BinaryExpressionOperator))! == 5,
            "Binary expression operator display and edits use reference opcode mapping");
        var sample = Path.Combine(options.Assets, "VfxShared/natives/stm/art/vfx/effecteditor/weapon/it00/11_it00_900.efx.5571972");
        string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sample)));
        var service = new EfxEditorService();
        var originalFile = service.Load(sample);
        var originalGroupOrder = originalFile.EffectGroups.Select(group => new { group.groupName, indexes = group.efxEntryIndexes }).ToArray();
        string expectedGroups = JsonSerializer.Serialize(originalGroupOrder, Host.Json);
        originalFile.FileHandler.Dispose();
        using var engine = new EditorEngine(options with { Output = Path.Combine(output, "expression-saved"), GameRoot = null });
        long revision = 0;
        JsonObject Request(string action, object? payload = null)
        {
            var request = payload == null ? new JsonObject() : JsonSerializer.SerializeToNode(payload, Host.Json)!.AsObject();
            request["id"] = Guid.NewGuid().ToString("N"); request["action"] = action; request["revision"] = revision;
            var response = engine.Handle(request);
            if (response["ok"]?.GetValue<bool>() != true) throw new Exception(response.ToJsonString());
            if (response["revision"] != null) revision = response["revision"]!.GetValue<long>();
            return response;
        }
        var target = new { kind = "entry", index = 1, attributeIndex = 4 };
        JsonArray Inspect() => Request("inspect", new { target })["expressionCurves"]!.AsArray();
        Request("open", new { path = sample });
        var curves = Inspect();
        check(curves.Count == 2 && curves[0]!["channel"]!.GetValue<string>() == "spawnNum" && curves[1]!["channel"]!.GetValue<string>() == "spawnNumRange",
            "Real SpawnExpression maps enabled bits to both spawn fields");
        const string expectedFormula = "Lerp(1, 0, SmoothStep(1.5, 3, ext:302732036))";
        check(curves.All(c => c!["formula"]!.GetValue<string>() == expectedFormula),
            "Real SpawnExpression formula exactly matches the independent reference bridge dump");
        var values = curves[0]!["values"]!.AsArray();
        check(values.Count == 4 && values[0]!["label"]!.GetValue<string>().Contains("from") && values[3]!["label"]!.GetValue<string>().Contains("hi"),
            "Expression constants have semantic labels and exact serialized field paths");
        var samples = curves[0]!["samples"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
        check(samples.Length == 101 && samples[0] == 1 && samples[30] == 1 && samples[45] == .5 && samples[60] == 0 && samples[100] == 0,
            "Static EM_SPEED sample has smooth midpoint and saturating endpoints rather than a time-axis assumption");
        string before = curves.ToJsonString();
        string path = values[0]!["path"]!.GetValue<string>();
        Request("set", new { target, path, value = "3" });
        string edited = Inspect().ToJsonString();
        var updated = Inspect();
        check(updated[0]!["formula"]!.GetValue<string>() == "Lerp(3, 0, SmoothStep(1.5, 3, ext:302732036))" &&
            updated[1]!.ToJsonString() == curves[1]!.ToJsonString(), "Editing labelled curve coefficient changes only the selected expression channel after reopening");
        check(updated[0]!["samples"]![45]!.GetValue<double>() == 1.5, "Static expression curve is regenerated from the submitted value");
        Request("undo"); check(Inspect().ToJsonString() == before, "Expression coefficient undo restores complete formula and plot");
        Request("redo"); check(Inspect().ToJsonString() == edited, "Expression coefficient redo restores complete formula and plot");
        string saved = Request("save")["outputPath"]!.GetValue<string>();
        var savedFile = service.Load(saved);
        check(JsonSerializer.Serialize(savedFile.EffectGroups.Select(group => new { group.groupName, indexes = group.efxEntryIndexes }), Host.Json) == expectedGroups,
            "Expression coefficient edit, undo, redo and save preserve original effect group member ordering");
        savedFile.FileHandler.Dispose();
        check(Path.GetFileName(saved).Split(".efx", StringSplitOptions.None).Length == 2,
            "Saved EFX has a single format suffix so reference tools infer its version correctly");
        Request("open", new { path = saved });
        check(Inspect().ToJsonString() == edited && originalHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sample))),
            "Expression edit saves and reloads identically without overwriting the captured source sample");
        Host.AtomicJson(Path.Combine(output, "spawn-expression.json"), new { sample, originalHash, target, path, beforeFormula = expectedFormula,
            afterFormula = updated[0]!["formula"]!.GetValue<string>(), saved, reference = "bundled mhws_efx_editor-0.3.0 EfxBridge dump", liveGameTested = false });
    }
}
