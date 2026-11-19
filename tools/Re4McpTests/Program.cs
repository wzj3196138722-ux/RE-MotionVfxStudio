// End-to-end test of the RE4 preview MCP server: the real MCP process (stdio) launches a real
// preview window on a private pipe, then edits, reads frames, captures, undoes and closes it.
// Usage: dotnet run -c Release --project tools/Re4McpTests [-- --repo <repository>]
// Requires Release builds of VfxStudio.Mcp and VfxStudio.Re4Preview.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
string repo = args.Length == 2 && args[0] == "--repo" ? Path.GetFullPath(args[1]) : FindRepository();
// RE4_MCP_DLL: another build when the Release dll is held by a running MCP host.
string mcp = Environment.GetEnvironmentVariable("RE4_MCP_DLL") ?? Path.Combine(repo, "VfxStudio.Mcp", "bin", "Release", "net8.0", "VfxStudio.Mcp.dll");
if (!File.Exists(mcp)) { Console.Error.WriteLine("Build VfxStudio.Mcp -c Release first: " + mcp); return 2; }
string pipe = "MotionVfx.Re4Preview.Test." + Guid.NewGuid().ToString("N")[..8];
string output = Path.Combine(repo, "test-output", "re4-mcp-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
Directory.CreateDirectory(output);
int checks = 0, failures = 0;
void Check(bool condition, string name, object? detail = null)
{
    checks++;
    if (condition) Console.WriteLine("ok   " + name);
    else { failures++; Console.WriteLine("FAIL " + name + (detail == null ? "" : " :: " + detail)); }
}

var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
foreach (var a in new[] { mcp, "--re4-preview", "--pipe", pipe, "--repo", repo }) info.ArgumentList.Add(a);
using var server = Process.Start(info)!;
server.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine("[mcp] " + e.Data); };
server.BeginErrorReadLine();
int nextId = 0;
var transcript = new List<string>();
async Task<JsonObject> Rpc(string method, JsonObject? parameters = null)
{
    var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++nextId, ["method"] = method };
    if (parameters != null) request["params"] = parameters;
    await server.StandardInput.WriteLineAsync(request.ToJsonString());
    await server.StandardInput.FlushAsync();
    string line = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(200)) ?? throw new IOException("MCP server closed stdout");
    transcript.Add(line.Length > 4000 ? line[..4000] + "…" : line);
    return JsonNode.Parse(line)!.AsObject();
}
// Returns (isError, structured outcome, raw result).
async Task<(bool Error, JsonObject Outcome, JsonObject Result)> Tool(string name, JsonObject? arguments = null)
{
    var response = await Rpc("tools/call", new JsonObject { ["name"] = "re4preview_" + name, ["arguments"] = arguments ?? new JsonObject() });
    if (response["error"] != null) return (true, new JsonObject { ["rpcError"] = response["error"]!.DeepClone() }, new JsonObject());
    var result = response["result"]!.AsObject();
    return (result["isError"]!.GetValue<bool>(), result["structuredContent"]!.AsObject(), result);
}
string Code(JsonObject outcome) => outcome["error"]?["code"]?.GetValue<string>() ?? outcome["rpcError"]?["message"]?.GetValue<string>() ?? "";
long Revision(JsonObject outcome) => outcome["result"]!["document"]!["revision"]!.GetValue<long>();
int windowPid = 0;

try
{
    var init = await Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "re4-mcp-tests", ["version"] = "1" } });
    Check(init["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "motionvfx-re4-preview", "initialize names the preview server");
    await server.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString());
    var list = await Rpc("tools/list");
    var names = list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();
    Check(names.Length == 26 && names.All(n => n.StartsWith("re4preview_")), "tools/list returns the 26 preview tools", string.Join(",", names));

    var (error, outcome, _) = await Tool("status");
    Check(error && Code(outcome) == "preview_not_running", "status without a window reports preview_not_running", outcome.ToJsonString());
    (error, outcome, _) = await Tool("open", new JsonObject { ["fixture"] = "bb2", ["entryId"] = 1 });
    Check(error && outcome["rpcError"] != null, "schema rule: fixture takes no entry selector");

    var watch = Stopwatch.StartNew();
    (error, outcome, _) = await Tool("launch", new JsonObject { ["fixture"] = "bb2", ["quality"] = "full" });
    Console.WriteLine($"launch {watch.Elapsed.TotalSeconds:F1} s");
    Check(!error && outcome["result"]?["alreadyRunning"]?.GetValue<bool>() == false && outcome["result"]?["preview"]?["displayedRevision"] != null, "launch starts a window and waits for the first frame", outcome.ToJsonString());
    windowPid = outcome["result"]?["window"]?["pid"]?.GetValue<int>() ?? 0;
    (error, outcome, _) = await Tool("launch");
    Check(!error && outcome["result"]?["alreadyRunning"]?.GetValue<bool>() == true && outcome["result"]?["window"]?["pid"]?.GetValue<int>() == windowPid, "second launch reuses the running window");

    // ---- Multi-entry: the whole sm82_320_00 file is loaded; entry 13 is edited.
    (error, outcome, _) = await Tool("status");
    var entries = outcome["result"]?["preview"]?["entries"]?.AsArray() ?? new JsonArray();
    int shownCount = entries.Count(e => e!["shown"]!.GetValue<bool>());
    Check(!error && entries.Count == 31 && shownCount >= 20 && entries.Single(e => e!["selected"]!.GetValue<bool>())!["position"]!.GetValue<int>() == 13,
        "status lists all 31 entries, most shown, entry 13 selected", $"shown {shownCount}; " + string.Join("; ", entries.Where(e => !e!["shown"]!.GetValue<bool>()).Select(e => $"[{e!["position"]}] {e["excluded"]}")));
    (error, outcome, _) = await Tool("frame", new JsonObject { ["tick"] = 45 });
    int aliveAll = outcome["result"]?["alive"]?.GetValue<int>() ?? 0;
    int frameEntries = outcome["result"]?["entries"]?.AsArray().Count ?? -1;
    // Reaching tick 45 can exclude an entry that fails in the emulator; compare with the status after it.
    int shownNow = (await Tool("status")).Outcome["result"]?["preview"]?["entries"]?.AsArray().Count(e => e!["shown"]!.GetValue<bool>()) ?? -2;
    Check(!error && frameEntries == shownNow && aliveAll > 2, "frame reports every shown entry", $"alive {aliveAll}, frame entries {frameEntries}, shown {shownNow}");
    long revBefore = Revision((await Tool("status")).Outcome);
    (error, outcome, _) = await Tool("select_entry", new JsonObject { ["entryPosition"] = 12 });
    Check(!error && outcome["result"]?["document"]?["entryPosition"]?.GetValue<int>() == 12 && Revision(outcome) == revBefore &&
        outcome["result"]?["preview"]?["displayedTick"]?.GetValue<int>() == 45 && outcome["result"]?["document"]?["attributes"]?.AsArray().Count > 0,
        "select_entry switches the edited entry without a revision or a timeline change", outcome.ToJsonString());
    // A Mesh entry is not drawn, but it can be the edited entry (its attributes and material overrides are editable).
    (error, outcome, _) = await Tool("select_entry", new JsonObject { ["entryPosition"] = 19 });
    Check(!error && outcome["result"]?["document"]?["entryPosition"]?.GetValue<int>() == 19 &&
        outcome["result"]?["document"]?["attributes"]?.AsArray().Any(a => a!["type"]?.GetValue<string>() == "TypeMesh") == true &&
        outcome["result"]?["preview"]?["entries"]?.AsArray().Any(e => e!["position"]!.GetValue<int>() == 19 && !e["shown"]!.GetValue<bool>()) == true,
        "a Mesh entry can be selected for editing but is not drawn", outcome.ToJsonString());
    // Editing does not depend on drawing: even Root (no Type attribute) can be the edited entry and inspected.
    (error, outcome, _) = await Tool("select_entry", new JsonObject { ["entryPosition"] = 0 });
    Check(!error && outcome["result"]?["document"]?["entryPosition"]?.GetValue<int>() == 0, "the Root entry (no Type) can be selected for editing", outcome.ToJsonString());
    (error, outcome, _) = await Tool("inspect", new JsonObject { ["attributeIndex"] = 0 });
    Check(!error && outcome["result"]?["fields"]?.AsArray().Count > 0, "a Root attribute can be inspected", outcome.ToJsonString());
    (error, outcome, _) = await Tool("select_entry", new JsonObject { ["entryPosition"] = 13 });
    Check(!error && outcome["result"]?["document"]?["entryPosition"]?.GetValue<int>() == 13, "select_entry returns to entry 13");
    (error, outcome, _) = await Tool("inspect");
    var attributes = outcome["result"]?["document"]?["attributes"]?.AsArray() ?? new JsonArray();
    int Attribute(string type) => attributes.FirstOrDefault(a => a!["type"]!.GetValue<string>() == type)?["index"]?.GetValue<int>() ?? -1;
    int billboard = Attribute("TypeBillboard3D"), life = Attribute("Life");
    Check(!error && billboard >= 0 && life >= 0, "inspect lists attributes including TypeBillboard3D and Life", outcome.ToJsonString());
    (error, outcome, _) = await Tool("inspect", new JsonObject { ["attributeIndex"] = billboard });
    var fields = outcome["result"]?["fields"]?.AsArray() ?? new JsonArray();
    string? Value(string path) => fields.FirstOrDefault(f => f!["path"]!.GetValue<string>() == path)?["value"]?.GetValue<string>();
    string? colorRate = Value("ColorRate"), sizeCenter = Value("SizeScalar.center"), flags = Value("Flags");
    Check(!error && colorRate != null && sizeCenter != null, "inspect returns Billboard3D field paths and values", $"ColorRate={colorRate} SizeScalar.center={sizeCenter} Flags={flags} fields={fields.Count}");
    long revision = Revision(outcome);

    (error, outcome, _) = await Tool("frame", new JsonObject { ["tick"] = 45, ["particles"] = 3, ["entryPosition"] = 13 });
    var before = outcome["result"];
    Check(!error && before?["tick"]?.GetValue<int>() == 45 && before?["alive"]?.GetValue<int>() > 0 && before?["particles"]?.AsArray().Count == Math.Min(3, before?["alive"]?.GetValue<int>() ?? 0), "frame seeks to tick 45 and lists particles", outcome.ToJsonString());
    double widthBefore = before?["width"]?["mean"]?.GetValue<double>() ?? double.NaN;

    (error, outcome, var raw) = await Tool("screenshot", new JsonObject { ["tick"] = 45, ["maxWidth"] = 640 });
    var image = raw["content"]?.AsArray().FirstOrDefault(c => c!["type"]!.GetValue<string>() == "image");
    string? shotPath = outcome["result"]?["path"]?.GetValue<string>();
    byte[] png = image == null ? [] : Convert.FromBase64String(image["data"]!.GetValue<string>());
    Check(!error && image != null && png.Length > 1000 && png[1] == (byte)'P' && outcome["result"]?["width"]?.GetValue<int>() == 640 && outcome["result"]?["data"] == null,
        "screenshot returns a PNG image block without duplicating the payload in text", $"bytes={png.Length} {outcome.ToJsonString()}");
    Check(shotPath != null && File.Exists(shotPath), "screenshot is saved in the session folder", shotPath);
    if (png.Length > 0) File.WriteAllBytes(Path.Combine(output, "before.png"), png);

    double newCenter = double.Parse(sizeCenter!, CultureInfo.InvariantCulture) * 2;
    double newRate = Math.Max(double.Parse(colorRate!, CultureInfo.InvariantCulture) * 4, 0.2);
    watch.Restart();
    (error, outcome, _) = await Tool("set_fields", new JsonObject
    {
        ["attributeIndex"] = billboard, ["expectedRevision"] = revision,
        ["fields"] = new JsonArray(new JsonObject { ["path"] = "SizeScalar.center", ["value"] = newCenter.ToString("R", CultureInfo.InvariantCulture) },
            new JsonObject { ["path"] = "ColorRate", ["value"] = newRate.ToString("R", CultureInfo.InvariantCulture) })
    });
    Console.WriteLine($"set_fields round trip {watch.Elapsed.TotalMilliseconds:F0} ms (includes reload and first frame)");
    long edited = error ? revision : Revision(outcome);
    Check(!error && edited == revision + 1 && outcome["result"]?["preview"]?["displayedRevision"]?.GetValue<long>() == edited && outcome["result"]?["previewError"] == null,
        "set_fields commits one revision and returns once it is displayed", outcome.ToJsonString());
    Check(outcome["result"]?["preview"]?["displayedTick"]?.GetValue<int>() == 45, "an edit keeps the paused tick");
    (error, outcome, _) = await Tool("frame", new JsonObject { ["tick"] = 45, ["entryPosition"] = 13 });
    double widthAfter = outcome["result"]?["width"]?["mean"]?.GetValue<double>() ?? double.NaN;
    Check(!error && widthAfter > widthBefore * 1.3, "doubling SizeScalar.center widens the native particles", $"{widthBefore} -> {widthAfter}");
    (error, outcome, raw) = await Tool("screenshot", new JsonObject { ["maxWidth"] = 640 });
    image = raw["content"]?.AsArray().FirstOrDefault(c => c!["type"]!.GetValue<string>() == "image");
    if (image != null) File.WriteAllBytes(Path.Combine(output, "after.png"), Convert.FromBase64String(image["data"]!.GetValue<string>()));
    Check(!error && outcome["result"]?["revision"]?.GetValue<long>() == edited, "screenshot shows the edited revision");

    (error, outcome, _) = await Tool("set_fields", new JsonObject { ["attributeIndex"] = billboard, ["expectedRevision"] = revision,
        ["fields"] = new JsonArray(new JsonObject { ["path"] = "ColorRate", ["value"] = "1" }) });
    Check(error && Code(outcome) == "backend_rejected", "a stale expectedRevision is rejected by the backend", outcome.ToJsonString());
    (error, outcome, _) = await Tool("set_fields", new JsonObject { ["attributeIndex"] = life, ["expectedRevision"] = edited,
        ["fields"] = new JsonArray(new JsonObject { ["path"] = "AppearFrame.min", ["value"] = "50" }, new JsonObject { ["path"] = "AppearFrame.max", ["value"] = "40" }) });
    Check(error && Code(outcome) == "backend_rejected", "an inverted Life range is rejected", outcome.ToJsonString());
    (error, outcome, _) = await Tool("status");
    Check(!error && Revision(outcome) == edited, "rejected edits leave the revision unchanged");

    if (flags != null && uint.TryParse(flags, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint flagValue))
    {
        (error, outcome, _) = await Tool("set_fields", new JsonObject { ["attributeIndex"] = billboard, ["expectedRevision"] = edited,
            ["fields"] = new JsonArray(new JsonObject { ["path"] = "Flags", ["value"] = ((flagValue & ~15u) | 5u).ToString(CultureInfo.InvariantCulture) }) });
        long blend2 = error ? edited : Revision(outcome);
        Check(!error && blend2 == edited + 1 && outcome["result"]?["previewError"]?.GetValue<string>() is string reason && reason.Contains('5'),
            "an unsupported blend commits but reports previewError", outcome.ToJsonString());
        (error, outcome, _) = await Tool("screenshot", new JsonObject { ["maxWidth"] = 320 });
        Check(!error, "the other entries still draw, so the scene screenshot succeeds", outcome.ToJsonString());
        await Tool("view", new JsonObject { ["soloSelected"] = true });
        (error, outcome, _) = await Tool("screenshot");
        Check(error && Code(outcome) == "preview_unavailable", "with only the edited entry shown, screenshot refuses a revision it cannot draw", outcome.ToJsonString());
        await Tool("view", new JsonObject { ["soloSelected"] = false });
        (error, outcome, _) = await Tool("undo", new JsonObject { ["expectedRevision"] = blend2 });
        Check(!error && outcome["result"]?["previewError"] == null && outcome["result"]?["preview"]?["displayedRevision"] != null, "undo restores a drawable revision", outcome.ToJsonString());
        edited = error ? edited : Revision(outcome);
    }
    else Check(false, "Flags is a numeric field", flags);

    (error, outcome, _) = await Tool("undo", new JsonObject { ["expectedRevision"] = edited });
    long undone = error ? edited : Revision(outcome);
    (error, outcome, _) = await Tool("frame", new JsonObject { ["tick"] = 45, ["entryPosition"] = 13 });
    double widthUndone = outcome["result"]?["width"]?["mean"]?.GetValue<double>() ?? double.NaN;
    Check(!error && Math.Abs(widthUndone - widthBefore) < 1e-6 * Math.Max(1, widthBefore), "undo restores the original particle widths", $"{widthBefore} vs {widthUndone}");

    // ---- Structure editing
    async Task<JsonObject> Status() => (await Tool("status")).Outcome["result"]!.AsObject();
    long Current(JsonObject status) => status["document"]!["revision"]!.GetValue<long>();
    int IndexOf(JsonObject document, string type) => document["attributes"]!.AsArray().FirstOrDefault(x => x!["type"]!.GetValue<string>() == type)?["index"]?.GetValue<int>() ?? -1;
    // Edits a key on the first attribute with an editable curve, adds one, then undoes both.
    async Task<string?> CurveRoundTrip(string label)
    {
    var current = await Status();
    long rev = Current(current);
    var (error, outcome, _) = (false, new JsonObject(), new JsonObject());
    foreach (var attribute in current["document"]!["attributes"]!.AsArray())
    {
        int index = attribute!["index"]!.GetValue<int>();
        (error, outcome, _) = await Tool("inspect", new JsonObject { ["attributeIndex"] = index });
        var curve = outcome["result"]?["curves"]?.AsArray().FirstOrDefault(c => c!["editable"]?.GetValue<bool>() == true && c["channels"]?.AsArray().Any(ch => ch!["keys"]!.AsArray().Count > 0) == true);
        if (curve == null) continue;
        var channel = curve["channels"]!.AsArray().First(ch => ch!["keys"]!.AsArray().Count > 0)!;
        var key = channel["keys"]![0]!;
        string path = curve["path"]!.GetValue<string>();
        int channelIndex = channel["index"]!.GetValue<int>(), keyCount = channel["keys"]!.AsArray().Count;
        string time = key["time"]!.GetValue<string>();
        bool isInt = channel["valueType"]!.GetValue<string>() == "Int";
        string newValue = isInt ? (int.Parse(key["value"]!.GetValue<string>(), CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture)
            : (double.Parse(key["value"]!.GetValue<string>(), CultureInfo.InvariantCulture) + 0.25).ToString("R", CultureInfo.InvariantCulture);
        (error, outcome, _) = await Tool("curve_key", new JsonObject { ["attributeIndex"] = index, ["path"] = path, ["channel"] = channelIndex, ["operation"] = "update",
            ["keyIndex"] = 0, ["time"] = time, ["value"] = newValue, ["expectedRevision"] = rev });
        bool updated = !error && Revision(outcome) == rev + 1;
        rev = error ? rev : Revision(outcome);
        (error, outcome, _) = await Tool("curve_key", new JsonObject { ["attributeIndex"] = index, ["path"] = path, ["channel"] = channelIndex, ["operation"] = "add",
            ["time"] = time, ["value"] = newValue, ["expectedRevision"] = rev });
        bool added = !error;
        rev = error ? rev : Revision(outcome);
        (error, outcome, _) = await Tool("inspect", new JsonObject { ["attributeIndex"] = index });
        var after = outcome["result"]?["curves"]?.AsArray().First(c => c!["path"]!.GetValue<string>() == path)!["channels"]![channelIndex]!["keys"]!.AsArray();
        Check(updated && added && after?.Count == keyCount + 1 && after.Any(k => k!["value"]!.GetValue<string>() == newValue),
            $"curve_key updates and adds keys on {attribute["type"]} {path}[{channelIndex}]", outcome.ToJsonString()[..Math.Min(600, outcome.ToJsonString().Length)]);
        (error, outcome, _) = await Tool("undo", new JsonObject { ["expectedRevision"] = rev });
        rev = error ? rev : Revision(outcome);
        (error, outcome, _) = await Tool("undo", new JsonObject { ["expectedRevision"] = rev });
        rev = error ? rev : Revision(outcome);
        (error, outcome, _) = await Tool("inspect", new JsonObject { ["attributeIndex"] = index });
        var restored = outcome["result"]?["curves"]?.AsArray().First(c => c!["path"]!.GetValue<string>() == path)!["channels"]![channelIndex]!["keys"]!.AsArray();
        Check(restored?.Count == keyCount && restored[0]!["value"]!.GetValue<string>() == key["value"]!.GetValue<string>(), "two undos restore the curve keys");
        return label + ":" + path;
    }
    return null;
    }
    var status0 = await Status();
    long rev = Current(status0);
    int attributeCount = status0["document"]!["attributes"]!.AsArray().Count;
    (error, outcome, _) = await Tool("frame", new JsonObject { ["tick"] = 45, ["entryPosition"] = 13 });
    int aliveBase = outcome["result"]?["alive"]?.GetValue<int>() ?? -1;
    double widthBase = outcome["result"]?["width"]?["mean"]?.GetValue<double>() ?? double.NaN;

    (error, outcome, _) = await Tool("attribute_types");
    var types = outcome["result"]?["types"]?.AsArray().Select(t => t!.GetValue<string>()).ToArray() ?? [];
    Check(!error && types.Contains("Life") && types.Contains("TypeBillboard3D"), "attribute_types lists RE4 attribute types", types.Length);
    var catalog = outcome["result"]?["catalog"]?["items"]?.AsArray().OfType<JsonObject>().ToDictionary(i => i["type"]!.GetValue<string>(), i => i["status"]!.GetValue<string>()) ?? [];
    Check(!error && catalog.GetValueOrDefault("Life") == "present" && catalog.GetValueOrDefault("PlayEmitter") == "actionOnly" &&
        catalog.GetValueOrDefault("TypeMesh") == "secondType" && outcome["result"]?["catalog"]?["entryType"]?.GetValue<string>() == "TypeBillboard3D",
        "attribute_types reports the catalogue for the current entry", $"{catalog.Count} items, Life={catalog.GetValueOrDefault("Life")}");

    int lifeIndex = IndexOf(status0["document"]!.AsObject(), "Life");
    (error, outcome, _) = await Tool("copy_attribute", new JsonObject { ["attributeIndex"] = lifeIndex });
    string? clip = outcome["result"]?["copyBuffer"]?["id"]?.GetValue<string>();
    Check(!error && clip != null && outcome["result"]?["copyBuffer"]?["attributeType"]?.GetValue<string>() == "Life" && Revision(outcome) == rev, "copy_attribute fills the copy buffer without a new revision", outcome.ToJsonString());

    (error, outcome, _) = await Tool("add_attribute", new JsonObject { ["type"] = "Life", ["expectedRevision"] = rev });
    Check(error && Code(outcome) == "backend_rejected", "add_attribute refuses a duplicate type", outcome.ToJsonString());
    (error, outcome, _) = await Tool("paste_attribute", new JsonObject { ["clipboardId"] = clip ?? "", ["expectedRevision"] = rev });
    Check(error && Code(outcome) == "backend_rejected", "paste_attribute without replace refuses an existing type", outcome.ToJsonString());

    (error, outcome, _) = await Tool("remove_attribute", new JsonObject { ["attributeIndex"] = lifeIndex, ["expectedRevision"] = rev });
    var afterRemove = outcome["result"]?["document"] as JsonObject;
    Check(!error && afterRemove != null && Revision(outcome) == rev + 1 && IndexOf(afterRemove, "Life") < 0 && afterRemove["attributes"]!.AsArray().Count == attributeCount - 1,
        "remove_attribute removes Life as one revision", outcome.ToJsonString());
    Console.WriteLine("  preview after removing Life: " + outcome["result"]?["previewError"] + " displayed r" + outcome["result"]?["preview"]?["displayedRevision"]);
    rev = error ? rev : Revision(outcome);

    (error, outcome, _) = await Tool("paste_attribute", new JsonObject { ["clipboardId"] = clip ?? "", ["expectedRevision"] = rev });
    var afterPaste = outcome["result"]?["document"] as JsonObject;
    Check(!error && afterPaste != null && IndexOf(afterPaste, "Life") >= 0 && outcome["result"]?["selection"]?["attributeIndex"]?.GetValue<int>() == IndexOf(afterPaste, "Life"),
        "paste_attribute restores Life and reports where it landed", outcome.ToJsonString());
    rev = error ? rev : Revision(outcome);
    (error, outcome, _) = await Tool("frame", new JsonObject { ["tick"] = 45, ["entryPosition"] = 13 });
    Check(!error && outcome["result"]?["alive"]?.GetValue<int>() == aliveBase && Math.Abs((outcome["result"]?["width"]?["mean"]?.GetValue<double>() ?? 0) - widthBase) < 1e-6 * Math.Max(1, widthBase),
        "the pasted Life reproduces the original native frame", $"{aliveBase}/{widthBase} vs {outcome["result"]?["alive"]}/{outcome["result"]?["width"]?["mean"]}");

    int pastedLife = afterPaste == null ? -1 : IndexOf(afterPaste, "Life");
    (error, outcome, _) = await Tool("paste_attribute", new JsonObject { ["clipboardId"] = clip ?? "", ["replaceAttributeIndex"] = pastedLife, ["expectedRevision"] = rev });
    Check(!error && Revision(outcome) == rev && outcome["result"]?["selection"]?["attributeIndex"]?.GetValue<int>() == pastedLife,
        "replacing Life with an identical copy succeeds without a new revision", outcome.ToJsonString());

    string? curveDone = await CurveRoundTrip("bb2");
    if (curveDone == null) Console.WriteLine("  bb2 has no editable curve key; trying bb after open");
    (error, outcome, _) = await Tool("curve_key", new JsonObject { ["attributeIndex"] = 0, ["path"] = "NoSuchCurve", ["channel"] = 0, ["operation"] = "remove", ["keyIndex"] = 0, ["expectedRevision"] = rev });
    Check(error && Code(outcome) == "backend_rejected", "curve_key on a missing curve is rejected", outcome.ToJsonString());

    // Undo the three structural steps back to the state before this block.
    for (int i = 0; i < 2; i++) { (error, outcome, _) = await Tool("undo", new JsonObject { ["expectedRevision"] = rev }); rev = error ? rev : Revision(outcome); }
    var back = await Status();
    Check(back["document"]!["attributes"]!.AsArray().Count == attributeCount && IndexOf(back["document"]!.AsObject(), "Life") == lifeIndex, "undo walks back paste and remove");

    // Entry-level tools: add, duplicate, copy/paste and remove whole entries, then undo all of it.
    {
        int Entries(JsonObject o) => o["result"]?["preview"]?["entries"]?.AsArray().Count ?? -1;
        int Selected(JsonObject o) => o["result"]?["document"]?["entryPosition"]?.GetValue<int>() ?? -1;
        var entryStatus = await Status();
        int entryCount = entryStatus["preview"]!["entries"]!.AsArray().Count;
        (error, outcome, _) = await Tool("add_entry", new JsonObject { ["type"] = "TypeBillboard3D", ["expectedRevision"] = rev });
        int created = entryCount;
        Check(!error && Revision(outcome) == rev + 1 && Entries(outcome) == entryCount + 1 && Selected(outcome) == created &&
            IndexOf(outcome["result"]!["document"]!.AsObject(), "UVSequence") >= 0, "add_entry appends a Billboard3D entry from the template and selects it", outcome.ToJsonString()[..Math.Min(400, outcome.ToJsonString().Length)]);
        rev = error ? rev : Revision(outcome);
        (error, outcome, _) = await Tool("frame", new JsonObject { ["tick"] = 45, ["entryPosition"] = created });
        Check(!error && outcome["result"]?["alive"]?.GetValue<int>() > 0, "the new entry has particles at tick 45", outcome["result"]?["alive"]);
        (error, outcome, _) = await Tool("duplicate_entry", new JsonObject { ["entryPosition"] = created, ["expectedRevision"] = rev });
        Check(!error && Entries(outcome) == entryCount + 2 && Selected(outcome) == created + 1, "duplicate_entry appends a copy and selects it");
        rev = error ? rev : Revision(outcome);
        (error, outcome, _) = await Tool("copy_entry", new JsonObject { ["entryPosition"] = 13 });
        string? entryClip = outcome["result"]?["copyBuffer"]?["id"]?.GetValue<string>();
        Check(!error && outcome["result"]?["copyBuffer"]?["kind"]?.GetValue<string>() == "entry" && Revision(outcome) == rev, "copy_entry fills the copy buffer without a new revision");
        (error, outcome, _) = await Tool("paste_entry", new JsonObject { ["clipboardId"] = entryClip ?? "", ["expectedRevision"] = rev });
        Check(!error && Entries(outcome) == entryCount + 3 && Selected(outcome) == entryCount + 2, "paste_entry appends the copied entry and selects it");
        rev = error ? rev : Revision(outcome);
        (error, outcome, _) = await Tool("remove_entry", new JsonObject { ["entryPosition"] = entryCount + 2, ["expectedRevision"] = rev - 1 });
        Check(error, "remove_entry with a stale revision is refused", outcome.ToJsonString());
        (error, outcome, _) = await Tool("remove_entry", new JsonObject { ["entryPosition"] = entryCount + 2, ["expectedRevision"] = rev });
        Check(!error && Entries(outcome) == entryCount + 2 && Selected(outcome) == entryCount + 1 && outcome["result"]?["previewError"] == null,
            "remove_entry on the selected entry moves the selection to the nearest entry", outcome.ToJsonString()[..Math.Min(400, outcome.ToJsonString().Length)]);
        rev = error ? rev : Revision(outcome);
        for (int i = 0; i < 4; i++) { (error, outcome, _) = await Tool("undo", new JsonObject { ["expectedRevision"] = rev }); rev = error ? rev : Revision(outcome); }
        (error, outcome, _) = await Tool("select_entry", new JsonObject { ["entryPosition"] = 13 });
        Check(!error && Entries(outcome) == entryCount && Selected(outcome) == 13 && outcome["result"]?["previewError"] == null, "undo removes the four entry edits; entry 13 is selected again",
            $"entries {Entries(outcome)} (expected {entryCount}), selected {Selected(outcome)}, previewError {outcome["result"]?["previewError"]}, error {(error ? outcome.ToJsonString()[..Math.Min(300, outcome.ToJsonString().Length)] : "none")}");
        rev = error ? rev : Revision(outcome);
        // A decal carrier: NoDraw + an EffectDecal PtBehavior with RE4's usual parameters; then undone.
        (error, outcome, _) = await Tool("add_entry", new JsonObject { ["script"] = "via.effect.script.EffectDecal", ["expectedRevision"] = rev });
        Check(!error && Entries(outcome) == entryCount + 1 && Selected(outcome) == entryCount && IndexOf(outcome["result"]!["document"]!.AsObject(), "PtBehavior") >= 0,
            "add_entry with script builds a decal carrier (NoDraw + EffectDecal) and selects it", outcome.ToJsonString()[..Math.Min(400, outcome.ToJsonString().Length)]);
        rev = error ? rev : Revision(outcome);
        (error, outcome, _) = await Tool("undo", new JsonObject { ["expectedRevision"] = rev }); rev = error ? rev : Revision(outcome);
        (error, outcome, _) = await Tool("select_entry", new JsonObject { ["entryPosition"] = 13 });
        rev = error ? rev : Revision(outcome);
        // copy_entry replaced the copied Life that the cross-file check below pastes: copy it again.
        (error, outcome, _) = await Tool("copy_attribute", new JsonObject { ["attributeIndex"] = lifeIndex });
        clip = outcome["result"]?["copyBuffer"]?["id"]?.GetValue<string>();
    }

    string exe = Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
    string forbidden = Path.Combine(Path.GetDirectoryName(exe)!, "mcp-test-should-not-exist.efx.3539837");
    (error, outcome, _) = await Tool("save_copy", new JsonObject { ["path"] = forbidden });
    Check(error && !File.Exists(forbidden), "save_copy refuses the game directory", outcome.ToJsonString());
    (error, outcome, _) = await Tool("save_copy");
    string? saved = outcome["result"]?["savedPath"]?.GetValue<string>();
    Check(!error && saved != null && File.Exists(saved), "save_copy without a path saves into the session folder", saved);

    (error, outcome, _) = await Tool("playback", new JsonObject { ["operation"] = "seek", ["tick"] = 120 });
    Check(!error && outcome["result"]?["displayedTick"]?.GetValue<int>() == 120 && outcome["result"]?["playing"]?.GetValue<bool>() == false, "playback seek shows the requested tick, paused", outcome.ToJsonString());
    (error, outcome, _) = await Tool("playback", new JsonObject { ["operation"] = "step" });
    Check(!error && outcome["result"]?["displayedTick"]?.GetValue<int>() == 121, "playback step advances one tick");
    (error, outcome, _) = await Tool("playback", new JsonObject { ["operation"] = "replay" });
    Check(!error && outcome["result"]?["displayedTick"]?.GetValue<int>() == 0, "replay reloads and pauses at tick 0", outcome.ToJsonString());
    (error, outcome, _) = await Tool("composition");
    var order = outcome["result"]?["drawOrder"]?.AsArray();
    Check(!error && outcome["result"]?["detonemap"]?.GetValue<bool>() == true && outcome["result"]?["alphaRate"]?.GetValue<bool>() == false &&
        outcome["result"]?["blends"]?.AsArray().Count == 5 && order != null && order.Count > 1, "composition reports defaults and the draw order", outcome.ToJsonString());
    (error, outcome, _) = await Tool("composition", new JsonObject { ["detonemap"] = false, ["blends"] = new JsonArray(new JsonObject { ["blend"] = 1, ["destination"] = "OneMinusSrcAlpha" }) });
    Check(!error && outcome["result"]?["detonemap"]?.GetValue<bool>() == false && outcome["result"]?["blends"]?[1]?["destination"]?.GetValue<string>() == "OneMinusSrcAlpha", "composition changes switches and blend factors");
    (error, outcome, _) = await Tool("composition", new JsonObject { ["detonemap"] = true, ["blends"] = new JsonArray(new JsonObject { ["blend"] = 1, ["destination"] = "One" }) });
    (error, outcome, _) = await Tool("composition", new JsonObject { ["blends"] = new JsonArray(new JsonObject { ["blend"] = 1, ["source"] = "Bogus" }) });
    Check(error, "an unknown blend factor is rejected");
    (error, outcome, _) = await Tool("view", new JsonObject { ["distance"] = 6, ["exposure"] = 1.5 });
    Check(!error && Math.Abs(outcome["result"]!["distance"]!.GetValue<float>() - 6) < 1e-5, "view sets the camera");

    (error, outcome, _) = await Tool("open", new JsonObject { ["fixture"] = "bb" });
    Check(!error && outcome["result"]?["preview"]?["displayedTick"]?.GetValue<int>() == 0 && outcome["result"]?["document"]?["source"]?.GetValue<string>()?.Contains("saliva") == true,
        "open switches to another emitter and starts paused at tick 0", outcome.ToJsonString());

    // Cross-file assembly: the copy buffer from bb2 survives opening bb.
    var bbStatus = await Status();
    int bbLife = IndexOf(bbStatus["document"]!.AsObject(), "Life");
    (error, outcome, _) = await Tool("paste_attribute", new JsonObject { ["clipboardId"] = clip ?? "", ["replaceAttributeIndex"] = bbLife, ["expectedRevision"] = Current(bbStatus) });
    string crossMessage = outcome["error"]?["message"]?.GetValue<string>() ?? "";
    Check(bbLife >= 0 && (!error && Revision(outcome) == Current(bbStatus) + 1 || error && crossMessage.Contains("表达式")),
        "bb2's Life pastes into bb, or is refused only for differing expression tables", outcome.ToJsonString());
    Console.WriteLine("  cross-file paste: " + (error ? "refused: " + crossMessage : "replaced Life in bb, previewError=" + outcome["result"]?["previewError"]));

    if (curveDone == null)
    {
        // An emitter whose PtUvSequenceClip has keys; also covers open by path + entryPosition.
        string clipFile = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe")!,
            @"RE4_EXTRACT\natives\stm\_anotherorder\vfx\effecteditor\efd_event\cs\efd_cse010_s00\efd_0015_cse010_s00_muzzleflash_0000.efx.3539837");
        (error, outcome, _) = await Tool("open", new JsonObject { ["path"] = clipFile, ["entryPosition"] = 4, ["discardUnsaved"] = true });
        // The native emitter does not run this entry yet (PtUvSequence); the document still opens and
        // the failure arrives as previewError, which is what this check pins down.
        Check(!error && outcome["result"]?["document"]?["entryPosition"]?.GetValue<int>() == 4 &&
            outcome["result"]?["document"]?["source"]?.GetValue<string>()?.Contains("muzzleflash") == true,
            "open by path and entryPosition opens the muzzle-flash emitter", outcome.ToJsonString());
        Console.WriteLine("  muzzleflash preview: " + (outcome["result"]?["previewError"]?.GetValue<string>() ?? "shown"));
        curveDone = await CurveRoundTrip("muzzleflash");
        Check(curveDone != null, "an emitter with an editable curve exercises curve_key");
    }
    Console.WriteLine("  curve_key exercised on " + curveDone);

    // An entry embedded in an Action's PlayEmitter: listed in status, selected with embeddedPath, inspected and edited
    // like a top-level one; the preview stays on its top-level entry and shows the new revision.
    {
        string damage = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe")!,
            @"RE4_EXTRACT\natives\stm\_anotherorder\vfx\effecteditor\efd_character\efd_ao_chfa\efd_0015_ao_chfa_0000_damage_0000.efx.3539837");
        (error, outcome, _) = await Tool("open", new JsonObject { ["path"] = damage, ["entryPosition"] = 0, ["discardUnsaved"] = true });
        Check(!error, "open a file whose Action embeds entries", outcome.ToJsonString());
        (error, outcome, _) = await Tool("status");
        var embeddedEntry = outcome["result"]?["preview"]?["embeddedEntries"]?.AsArray().FirstOrDefault();
        Check(!error && embeddedEntry?["embeddedPath"]?.AsArray().Count == 1, "status lists embedded entries with their embeddedPath", outcome.ToJsonString());
        long embeddedBefore = Revision(outcome);
        (error, outcome, _) = await Tool("select_entry", new JsonObject { ["entryPosition"] = embeddedEntry?["entryPosition"]?.GetValue<int>() ?? 0,
            ["embeddedPath"] = embeddedEntry?["embeddedPath"]?.DeepClone() });
        var embeddedDocument = outcome["result"]?["document"];
        Check(!error && embeddedDocument?["editTarget"]?["embeddedPath"]?.AsArray().Count == 1 && embeddedDocument["entryPosition"]?.GetValue<int>() == 0 &&
            Revision(outcome) == embeddedBefore && embeddedDocument["attributes"]?.AsArray().Count > 0,
            "select_entry with embeddedPath edits the embedded entry and keeps the preview entry", outcome.ToJsonString());
        int embeddedLife = embeddedDocument?["attributes"]?.AsArray().FirstOrDefault(a => a!["type"]!.GetValue<string>() == "Life")?["index"]?.GetValue<int>() ?? -1;
        (error, outcome, _) = await Tool("inspect", new JsonObject { ["attributeIndex"] = Math.Max(0, embeddedLife) });
        string? appear = outcome["result"]?["fields"]?.AsArray().FirstOrDefault(f => f!["path"]!.GetValue<string>() == "AppearFrame.max")?["value"]?.GetValue<string>();
        Check(!error && embeddedLife >= 0 && appear != null, "inspect reads the embedded entry's Life", outcome.ToJsonString());
        string nextAppear = ((int.TryParse(appear, out int a0) ? a0 : 0) + 1).ToString();
        (error, outcome, _) = await Tool("set_fields", new JsonObject { ["attributeIndex"] = embeddedLife,
            ["fields"] = new JsonArray(new JsonObject { ["path"] = "AppearFrame.max", ["value"] = nextAppear }), ["expectedRevision"] = embeddedBefore });
        Check(!error && Revision(outcome) == embeddedBefore + 1 && outcome["result"]?["preview"]?["displayedRevision"]?.GetValue<long>() == embeddedBefore + 1,
            "set_fields on the embedded entry is one revision and the preview shows it", outcome.ToJsonString());
        (error, outcome, _) = await Tool("inspect", new JsonObject { ["attributeIndex"] = embeddedLife });
        Check(!error && outcome["result"]?["fields"]?.AsArray().Any(f => f!["path"]!.GetValue<string>() == "AppearFrame.max" && f["value"]!.GetValue<string>() == nextAppear) == true,
            "the embedded edit reads back", outcome.ToJsonString());
        (error, outcome, _) = await Tool("select_entry", new JsonObject { ["entryPosition"] = 0 });
        Check(!error && outcome["result"]?["document"]?["editTarget"] == null, "select_entry without embeddedPath returns to the top-level entry", outcome.ToJsonString());
        (error, outcome, _) = await Tool("select_entry", new JsonObject { ["entryPosition"] = 999, ["embeddedPath"] = embeddedEntry?["embeddedPath"]?.DeepClone() });
        Check(error, "select_entry refuses an embedded entry that does not exist", outcome.ToJsonString());
    }

    (error, outcome, _) = await Tool("close", new JsonObject { ["discardUnsaved"] = true });
    Check(!error, "close is accepted");
    bool exited = false;
    try { using var window = Process.GetProcessById(windowPid); exited = window.WaitForExit(15000); } catch (ArgumentException) { exited = true; }
    Check(exited, "the preview window exits after close");
    (error, outcome, _) = await Tool("status");
    Check(error && Code(outcome) == "preview_not_running", "status after close reports preview_not_running");
}
finally
{
    try { using var window = Process.GetProcessById(windowPid); if (!window.HasExited) window.Kill(true); } catch (ArgumentException) { }
    server.StandardInput.Close();
    if (!server.WaitForExit(5000)) server.Kill(true);
    File.WriteAllLines(Path.Combine(output, "transcript.jsonl"), transcript);
}
Console.WriteLine($"{(failures == 0 ? "PASS" : "FAIL")}: {checks - failures}/{checks} checks; output {output}");
return failures == 0 ? 0 : 1;

static string FindRepository()
{
    for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        if (File.Exists(Path.Combine(folder.FullName, "tools", "Re4Native", "re4host.py"))) return folder.FullName;
    return Directory.GetCurrentDirectory();
}
