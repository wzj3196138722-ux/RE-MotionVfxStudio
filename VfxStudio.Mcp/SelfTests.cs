using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VfxStudio.Mcp;

internal static class SelfTests
{
    private static int checks;
    private static void Check(bool condition, string label) { checks++; if (!condition) throw new InvalidOperationException("FAIL: " + label); }
    private sealed class FakeBridge : IEditorBridge
    {
        internal int Calls;
        internal bool Uncertain;
        public Task<JsonObject> Call(string id, string action, JsonObject arguments, CancellationToken cancellationToken)
        {
            Calls++;
            if (Uncertain) throw new BridgeException("outcome_uncertain", "Synthetic timeout", id, true);
            return Task.FromResult(new JsonObject { ["protocol"] = 1, ["id"] = id, ["ok"] = true,
                ["result"] = new JsonObject { ["action"] = action, ["arguments"] = arguments.DeepClone() } });
        }
    }

    private static JsonObject Rpc(int id, string method, JsonObject? parameters = null) => new()
    { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
    private static JsonObject Tool(int id, string action, JsonObject? arguments = null) => Rpc(id, "tools/call", new()
    { ["name"] = ToolCatalog.Prefix + action, ["arguments"] = arguments ?? new JsonObject() });
    private static async Task Initialize(McpServer server, string protocol = McpServer.ProtocolVersion)
    {
        var initialized = await server.Handle(Rpc(1, "initialize", new() { ["protocolVersion"] = protocol, ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "offline-tests", ["version"] = "1" } }).ToJsonString());
        Check(initialized!["result"]!["protocolVersion"]!.GetValue<string>() == protocol, "Known protocol negotiation: " + protocol);
        Check(await server.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}") == null, "Initialization notification produces no output");
    }

    public static async Task<int> Run()
    {
        string temporaryRoot = Path.Combine(Environment.GetEnvironmentVariable("MOTIONVFX_MCP_TEST_ROOT") ?? Path.GetTempPath(), "MotionVfx-Mcp-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await ProtocolTests();
            await MailboxTests(temporaryRoot);
            await TransportTests(temporaryRoot);
            Console.WriteLine("PASS: " + checks + " MCP checks; fake Lua mailbox only, no live game access. Fixtures: " + temporaryRoot);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Fixtures: " + temporaryRoot); return 1; }
    }

    private static async Task ProtocolTests()
    {
        var bridge = new FakeBridge(); var server = new McpServer(bridge);
        Check((await server.Handle("bad json"))!["error"]!["code"]!.GetValue<int>() == -32700, "Malformed JSON returns a parse error");
        Check((await server.Handle("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}"))!["error"]!["code"]!.GetValue<int>() == -32600, "Null request IDs are rejected");
        Check((await server.Handle(Rpc(50, "server/discover").ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32601, "Modern discovery receives explicit method-not-found for fallback");
        Check((await server.Handle(Rpc(51, "ping").ToJsonString()))!["result"] is JsonObject, "Ping works during initialization");
        Check((await server.Handle(Rpc(52, "tools/list").ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32002, "Tools require the initialized lifecycle");
        await Initialize(server);
        var listed = await server.Handle(Rpc(2, "tools/list").ToJsonString());
        var tools = listed!["result"]!["tools"]!.AsArray();
        Check(tools.Count == 17 && tools.All(tool => tool!["name"]!.GetValue<string>().StartsWith("motionvfx_", StringComparison.Ordinal)), "Seventeen namespaced tools including animation capture are discoverable");
        Check(ToolCatalog.Find("motionvfx_list_actions").ReadOnly, "Action observation tool is read-only");
        Check(tools.All(tool => tool!["inputSchema"]!["additionalProperties"]!.GetValue<bool>() == false && tool["annotations"] != null), "Every tool has a bounded schema and annotations");
        Check(ToolCatalog.All.Where(tool => tool.Action is "inspect" or "set_field" or "set_color" or "set_group_note" or "rename_group" or "undo" or "redo" or "save_copy" or "apply")
            .All(tool => tool.Schema["required"]!.AsArray().Any(node => node!.GetValue<string>() == "expectedRevision")), "All document-sensitive tools require an expected revision");
        var valid = Tool(3, "set_color", new() { ["target"] = new JsonObject { ["kind"] = "entry", ["index"] = 2, ["attributeIndex"] = 3 },
            ["path"] = "GreenChColor", ["values"] = new JsonArray(255, 128, 0, 255), ["expectedRevision"] = 7 });
        var success = await server.Handle(valid.ToJsonString());
        Check(success!["result"]!["isError"]!.GetValue<bool>() == false && bridge.Calls == 1, "Validated tool calls reach only the bridge");
        Check(success["result"]!["structuredContent"]!["result"]!["arguments"]!["expectedRevision"]!.GetValue<int>() == 7, "Expected revision is preserved through the bridge");
        var replay = await server.Handle(valid.ToJsonString());
        Check(JsonNode.DeepEquals(success, replay) && bridge.Calls == 1, "Same JSON-RPC request returns a cached response without replaying mutation");
        valid["params"]!["arguments"]!["values"] = new JsonArray(1, 2, 3, 4);
        Check((await server.Handle(valid.ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32600 && bridge.Calls == 1, "Reusing an ID with different arguments never mutates");
        var missingRevision = Tool(4, "apply");
        Check((await server.Handle(missingRevision.ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32602 && bridge.Calls == 1, "Missing document revision is rejected before bridge access");
        var actionQuery = await server.Handle(Tool(150, "list_actions", new() { ["actionId"] = "motion-1-7" }).ToJsonString());
        Check(actionQuery!["result"]!["structuredContent"]!["result"]!["arguments"]!["actionId"]!.GetValue<string>() == "motion-1-7", "Action detail selection is passed to the game UI");
        var invalidColor = (JsonObject)valid.DeepClone(); invalidColor["id"] = 5; invalidColor["params"]!["arguments"]!["values"] = new JsonArray(1, 2, 3, 256);
        Check((await server.Handle(invalidColor.ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32602, "Out-of-range raw RGBA is rejected");
        Check((await server.Handle(Tool(6, "execute_lua", new() { ["code"] = "forbidden" }).ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32602, "No arbitrary Lua or native execution tool exists");
        Check((await server.Handle(Tool(7, "status", new() { ["backendAction"] = "raw" }).ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32602, "Unknown arguments cannot tunnel another backend action");
        Check((await server.Handle(Tool(8, "scene_time", new() { ["operation"] = "speed" }).ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32602, "Speed operation requires a numeric speed");
        Check((await server.Handle(Tool(9, "scene_time", new() { ["operation"] = "speed", ["speed"] = 0.1 }).ToJsonString()))!["result"]!["isError"]!.GetValue<bool>() == false, "Valid slow-motion arguments are accepted");
        Check((await server.Handle(Tool(91, "scene_time", new() { ["operation"] = "speed", ["speed"] = 8 }).ToJsonString()))!["result"]!["isError"]!.GetValue<bool>() == false, "Lua-supported fast motion is accepted");
        Check((await server.Handle(Tool(92, "scene_time", new() { ["operation"] = "speed", ["speed"] = 0.001 }).ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32602, "Below-minimum scene speed is rejected before submission");
        Check((await server.Handle(Tool(93, "set_field", new() { ["target"] = new JsonObject { ["kind"] = "entry", ["index"] = 0 }, ["path"] = "name", ["value"] = new string('界', 12000), ["expectedRevision"] = 7 }).ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32602, "Multibyte text respects the Lua fingerprint byte limit");
        var note = await server.Handle(Tool(94, "set_group_note", new() { ["index"] = 0, ["note"] = "角色脚下的蓝色火焰", ["expectedRevision"] = 7 }).ToJsonString());
        Check(note!["result"]!["structuredContent"]!["result"]!["arguments"]!["note"]!.GetValue<string>() == "角色脚下的蓝色火焰", "Chinese group note passes through without conversion to a native rename");
        Check((await server.Handle(Tool(95, "set_group_note", new() { ["index"] = 1, ["note"] = "角色脚下的蓝色火焰", ["expectedRevision"] = 7 }).ToJsonString()))!["result"]!["isError"]!.GetValue<bool>() == false, "Different groups may use identical notes");
        Check((await server.Handle(Tool(96, "set_group_note", new() { ["index"] = 0, ["note"] = "", ["expectedRevision"] = 7 }).ToJsonString()))!["result"]!["isError"]!.GetValue<bool>() == false, "Empty note is accepted for clearing");
        var compatibility = await server.Handle(Tool(97, "rename_group", new() { ["index"] = 0, ["name"] = "中文兼容备注", ["expectedRevision"] = 7 }).ToJsonString());
        Check(compatibility!["result"]!["structuredContent"]!["result"]!["action"]!.GetValue<string>() == "set_group_note" &&
            compatibility["result"]!["structuredContent"]!["result"]!["arguments"]!["note"]!.GetValue<string>() == "中文兼容备注" &&
            compatibility["result"]!["structuredContent"]!["result"]!["arguments"]!["name"] == null,
            "Legacy tool canonicalizes to set_group_note so an older Lua bridge cannot perform a native rename");
        Check(ToolCatalog.Find("motionvfx_set_group_note").Idempotent && ToolCatalog.Find("motionvfx_rename_group").Description.Contains("不会改写 EFX"), "Note tools advertise their sidecar-only semantics and idempotency");
        Check((await server.Handle(Tool(98, "set_group_note", new() { ["index"] = 0, ["note"] = "备注" }).ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32602, "Notes still require the current document revision");
        Check((await server.Handle(Tool(99, "set_group_note", new() { ["index"] = 0, ["note"] = new string('界', 1366), ["expectedRevision"] = 7 }).ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32602, "Group notes enforce the 4096-byte UTF-8 limit");
        Check((await server.Handle(Tool(100, "set_group_note", new() { ["index"] = 0, ["note"] = new string('x', 4096), ["expectedRevision"] = 7 }).ToJsonString()))!["result"]!["isError"]!.GetValue<bool>() == false, "A 4096-byte note remains supported");
        Check((await server.Handle(Tool(101, "rename_group", new() { ["index"] = 0, ["name"] = "第一行\n第二行", ["expectedRevision"] = 7 }).ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32602, "The legacy alias also rejects multiline notes");
        var spaces = await server.Handle(Tool(102, "set_group_note", new() { ["index"] = 0, ["note"] = "  中文  ", ["expectedRevision"] = 7 }).ToJsonString());
        Check(spaces!["result"]!["structuredContent"]!["result"]!["arguments"]!["note"]!.GetValue<string>() == "  中文  ", "Note whitespace is preserved without trim or truncation");
        int previousCalls = bridge.Calls;
        var notification = Tool(10, "apply", new() { ["expectedRevision"] = 7 }); notification.Remove("id");
        Check(await server.Handle(notification.ToJsonString()) == null && bridge.Calls == previousCalls, "Tool notifications never execute side effects");
        bridge.Uncertain = true;
        var uncertainRequest = Tool(11, "apply", new() { ["expectedRevision"] = 7 });
        var uncertain = await server.Handle(uncertainRequest.ToJsonString());
        Check(uncertain!["result"]!["isError"]!.GetValue<bool>() && uncertain["result"]!["structuredContent"]!["error"]!["uncertain"]!.GetValue<bool>(), "Timeout becomes an explicit uncertain tool error");
        Check(!string.IsNullOrWhiteSpace(uncertain["result"]!["structuredContent"]!["error"]!["requestId"]!.GetValue<string>()), "Uncertain outcomes retain the bridge request ID");
        previousCalls = bridge.Calls;
        Check(JsonNode.DeepEquals(uncertain, await server.Handle(uncertainRequest.ToJsonString())) && bridge.Calls == previousCalls, "Uncertain mutations are not replayed on same-ID retry");

        var legacy = new McpServer(new FakeBridge()); await Initialize(legacy, "2024-11-05");
        var legacyList = await legacy.Handle(Rpc(2, "tools/list").ToJsonString());
        Check(legacyList!["result"]!["tools"]![0]!["annotations"] == null, "2024 tools omit newer annotations");
        Check((await legacy.Handle(Tool(3, "status").ToJsonString()))!["result"]!["structuredContent"] == null, "2024 tool results use text content without newer structured output");
        await Initialize(new McpServer(new FakeBridge()), "2025-06-18");
        var fallback = new McpServer(new FakeBridge());
        Check((await fallback.Handle(Rpc(1, "initialize", new() { ["protocolVersion"] = "2099-01-01" }).ToJsonString()))!["result"]!["protocolVersion"]!.GetValue<string>() == McpServer.ProtocolVersion,
            "Unknown protocols negotiate an explicitly supported version without claiming modern support");
    }

    private static async Task MailboxTests(string temporaryRoot)
    {
        var timing = new BridgeTiming(TimeSpan.FromMilliseconds(180), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(15));
        var bridge = new MailboxBridge(temporaryRoot, timing);
        string directory = bridge.DirectoryPath; Directory.CreateDirectory(directory);
        string requestPath = Path.Combine(directory, "ai-request.json"), responsePath = Path.Combine(directory, "ai-response.json"), statusPath = Path.Combine(directory, "ui-status.json");
        JsonObject Status(bool ready = true, long? time = null, int protocol = 1) => new() { ["version"] = "0.3.0", ["loaded"] = true, ["aiReady"] = ready,
            ["aiProtocol"] = protocol, ["observedAt"] = time ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["loadedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1 };
        async Task<BridgeException> Reject(Func<Task> operation, string code, string label)
        {
            try { await operation(); } catch (BridgeException error) { Check(error.Code == code, label); return error; }
            throw new InvalidOperationException("Expected rejection: " + label);
        }
        await Reject(() => bridge.Call("not-ready", "status", new(), default), "editor_not_ready", "Missing editor status refuses AI requests");
        Check(!File.Exists(requestPath), "Readiness failure creates no AI request");
        MailboxBridge.AtomicJson(statusPath, Status(false));
        await Reject(() => bridge.Call("not-ready2", "status", new(), default), "editor_not_ready", "Explicitly unready bridge refuses requests");
        MailboxBridge.AtomicJson(statusPath, Status(protocol: 2));
        await Reject(() => bridge.Call("not-ready3", "status", new(), default), "editor_not_ready", "Unknown Lua bridge protocol is rejected");
        MailboxBridge.AtomicJson(statusPath, Status(time: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60));
        await Reject(() => bridge.Call("stale", "status", new(), default), "editor_status_stale", "Stale editor heartbeat cannot authorize submission");
        MailboxBridge.AtomicJson(statusPath, Status(time: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60));
        await Reject(() => bridge.Call("future", "status", new(), default), "editor_status_stale", "A far-future timestamp cannot authorize submission");
        MailboxBridge.AtomicJson(statusPath, Status());

        async Task<JsonObject> Respond(string id, bool ok = true)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                var request = MailboxBridge.ReadOptional(requestPath, 1024 * 1024);
                if (request != null && MailboxBridge.String(request, "id") == id)
                {
                    Check(request["protocol"]!.GetValue<int>() == 1 && request["expiresAtUnixSeconds"]!.GetValue<long>() >= DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Request includes protocol and bounded acceptance deadline");
                    var response = new JsonObject { ["protocol"] = 1, ["id"] = id, ["ok"] = ok,
                        ["result"] = ok ? new JsonObject { ["accepted"] = true } : null,
                        ["error"] = ok ? null : new JsonObject { ["code"] = "ui_drafts", ["message"] = "User has pending UI edits." } };
                    MailboxBridge.AtomicJson(responsePath, response); return request;
                }
                await Task.Delay(5);
            }
            throw new TimeoutException("Fake Lua did not observe its request.");
        }
        Task<JsonObject> responder = Respond("one");
        var response = await bridge.Call("one", "set_field", new() { ["expectedRevision"] = 7 }, default); await responder;
        Check(response["ok"]!.GetValue<bool>(), "Fake Lua bridge returns matching completion");
        string savedRequest = File.ReadAllText(requestPath);
        Check((await bridge.Call("one", "set_field", new() { ["expectedRevision"] = 7 }, default))["ok"]!.GetValue<bool>() && File.ReadAllText(requestPath) == savedRequest,
            "Same bridge request ID uses an existing final answer without rewriting");
        await Reject(() => bridge.Call("one", "set_field", new() { ["expectedRevision"] = 8 }, default), "request_id_conflict", "Bridge ID reuse with different content is rejected");
        using (var guard = new FileStream(Path.Combine(directory, "ai-bridge.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Reject(() => new MailboxBridge(temporaryRoot, timing).Call("locked", "status", new(), default), "bridge_busy", "OS exclusive file locking prevents competing bridge writers");
        Check(File.ReadAllText(requestPath) == savedRequest, "A competing writer did not overwrite the mailbox");

        responder = Respond("ui-blocked", false);
        response = await bridge.Call("ui-blocked", "apply", new(), default); await responder;
        Check(!response["ok"]!.GetValue<bool>() && response["error"]!["code"]!.GetValue<string>() == "ui_drafts", "Lua UI ownership errors are propagated unchanged");
        var timeout = await Reject(() => bridge.Call("timeout", "set_field", new(), default), "outcome_uncertain", "Unanswered submissions time out as uncertain");
        Check(timeout.Uncertain && timeout.RequestId == "timeout", "Timeout retains its request ID and uncertainty flag");
        savedRequest = File.ReadAllText(requestPath);
        var pending = await Reject(() => bridge.Call("new-mutation", "set_field", new(), default), "previous_request_pending", "An unanswered request blocks all later mailbox overwrites");
        Check(pending.Uncertain && pending.RequestId == "timeout" && File.ReadAllText(requestPath) == savedRequest, "Pending operation's identity and payload remain intact");
        await Reject(() => bridge.Call("timeout", "set_field", new(), default), "outcome_uncertain", "Same-ID pending request is not automatically replayed");
        MailboxBridge.AtomicJson(responsePath, new() { ["protocol"] = 1, ["id"] = "timeout", ["ok"] = true, ["result"] = new JsonObject { ["late"] = true } });
        Check((await bridge.Call("timeout", "set_field", new(), default))["result"]!["late"]!.GetValue<bool>(), "A late final response can be read without resubmitting the mutation");
        responder = Respond("after-late"); await bridge.Call("after-late", "status", new(), default); await responder;
        Check(MailboxBridge.ReadOptional(requestPath, 1024 * 1024)!["id"]!.GetValue<string>() == "after-late", "A completed old request no longer blocks a new read");
        Check(!File.Exists(Path.Combine(directory, "request.json")) && !File.Exists(Path.Combine(directory, "response.json")), "MCP never writes or reads the backend command mailbox");

        MailboxBridge.AtomicJson(responsePath, new() { ["protocol"] = 1, ["id"] = "after-late", ["ok"] = "not-a-boolean" });
        await Reject(() => bridge.Call("malformed-answer", "apply", new(), default), "previous_request_pending", "A malformed answer never authorizes overwriting the prior request");
        Check(MailboxBridge.ReadOptional(requestPath, 1024 * 1024)!["id"]!.GetValue<string>() == "after-late", "Malformed final answer leaves mailbox untouched");
        string config = Path.Combine(temporaryRoot, "runtime-config.json");
        MailboxBridge.AtomicJson(config, new() { ["gameRoot"] = temporaryRoot });
        Check(Entry.ResolveGameRoot(["--config", config]) == Path.GetFullPath(temporaryRoot), "Portable runtime config resolves the installer-provided game root");
        Check(Entry.ResolveGameRoot(["--game-root", temporaryRoot]) == Path.GetFullPath(temporaryRoot), "Explicit game root is supported");
    }

    private static async Task TransportTests(string temporaryRoot)
    {
        var lines = new List<string?>();
        await foreach (var line in Entry.ReadLines(new StringReader(new string('x', Entry.MaximumLineCharacters + 1) + "\n{\"ok\":true}\r\n"), default)) lines.Add(line);
        Check(lines.Count == 2 && lines[0] == null && lines[1] == "{\"ok\":true}", "Oversized input is drained within a bounded buffer and the next line survives");
        string executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Entry).Assembly.Location);
        start.ArgumentList.Add("--game-root"); start.ArgumentList.Add(temporaryRoot);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch independent stdio test.");
        var outputTask = process.StandardOutput.ReadToEndAsync(); var errorsTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(Rpc(1, "initialize", new() { ["protocolVersion"] = McpServer.ProtocolVersion }).ToJsonString());
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await process.StandardInput.WriteLineAsync(Rpc(2, "tools/list").ToJsonString());
        await process.StandardInput.WriteLineAsync(Rpc(3, "ping").ToJsonString());
        await process.StandardInput.WriteLineAsync(Rpc(4, "server/discover").ToJsonString());
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        string stdout = await outputTask, stderr = await errorsTask;
        var messages = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line.TrimEnd('\r'))!.AsObject()).ToArray();
        Check(process.ExitCode == 0 && stderr.Length == 0, "Independent stdio server exits cleanly without diagnostic noise");
        Check(messages.Length == 4 && messages.All(message => message["jsonrpc"]!.GetValue<string>() == "2.0"), "Stdout contains only one valid JSON-RPC message per response line");
        Check(messages[1]["result"]!["tools"]!.AsArray().Count == 17 && messages[3]["error"]!["code"]!.GetValue<int>() == -32601, "Independent client discovers tools and receives modern-discovery fallback");
    }
}
