using System.Text.Json;
using System.Text.Json.Nodes;

namespace VfxStudio.Mcp;

internal sealed class McpServer(IEditorBridge bridge)
{
    public const string Version = "0.1.2";
    public const string ProtocolVersion = "2025-11-25";
    private static readonly string[] SupportedVersions = [ProtocolVersion, "2025-06-18", "2024-11-05"];
    private readonly string session = Guid.NewGuid().ToString("N");
    private bool initialized, clientReady;
    private string negotiatedVersion = ProtocolVersion;
    private int sequence;
    private long cacheBytes;
    private sealed record Cached(string Request, JsonObject Response, long Bytes);
    private readonly Dictionary<string, Cached> cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();

    public async Task<JsonObject?> Handle(string line, CancellationToken cancellationToken = default)
    {
        JsonObject request;
        try { request = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return Error(null, -32700, "Expected one JSON-RPC object per line."); }
        JsonNode? id = request["id"];
        if (MailboxBridge.String(request, "jsonrpc") != "2.0" || MailboxBridge.String(request, "method") is not string method || !ValidId(id, request.ContainsKey("id")))
            return Error(null, -32600, "Invalid JSON-RPC request.");
        if (!request.ContainsKey("id"))
        {
            if (method == "notifications/initialized" && initialized) clientReady = true;
            // Notifications never perform tool operations and never receive a response.
            return null;
        }
        string key = id!.ToJsonString();
        string serialized = request.ToJsonString();
        if (cache.TryGetValue(key, out var prior))
            return JsonNode.DeepEquals(JsonNode.Parse(prior.Request), request) ? (JsonObject)prior.Response.DeepClone() : Error(id, -32600, "Request ID reused with different content.");
        if (seen.Contains(key)) return Error(id, -32600, "Request ID was already processed; its response expired. It will not be executed again.");
        if (seen.Count >= 10000) return Error(id, -32000, "Session request limit reached; start a new MCP connection.");

        JsonObject response;
        try
        {
            JsonObject parameters = request["params"] == null ? new() : request["params"] as JsonObject ?? throw new ArgumentException("params must be an object.");
            switch (method)
            {
                case "initialize":
                    if (initialized) throw new ArgumentException("This MCP connection is already initialized.");
                    string requested = MailboxBridge.String(parameters, "protocolVersion") ?? throw new ArgumentException("protocolVersion is required.");
                    negotiatedVersion = SupportedVersions.Contains(requested, StringComparer.Ordinal) ? requested : ProtocolVersion;
                    initialized = true;
                    response = Result(id, new JsonObject
                    {
                        ["protocolVersion"] = negotiatedVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "motionvfx-live", ["version"] = Version },
                        ["instructions"] = "Control MotionVfx Live through its Lua AI bridge only. Workflow: status, capture/list_effects, open_effect using the full captured path, inspect, edit with expectedRevision, inspect to verify, save_copy, apply, ask the user to perform the action and inspect status, then restore. Group labels are Chinese-capable editor-only notes: use set_group_note, which stores separate JSON and never changes native EFX group names, hashes, revision, dirty state, or Undo/Redo. rename_group is a compatibility alias for the same note operation. Clear a note with an empty string; restore it by setting its prior value, not Undo. Apply acceptance is not visual confirmation. Never automatically retry mutations after an uncertain timeout. UI drafts and pending operations take precedence. This server implements MCP 2025-11-25 stdio with explicitly supported older versions; it does not implement server/discover."
                    });
                    break;
                case "ping": response = Result(id, new JsonObject()); break;
                case "tools/list":
                    RequireReady();
                    if (parameters["cursor"] != null) throw new ArgumentException("This server returns all tools in one page; cursor is unsupported.");
                    response = Result(id, new JsonObject { ["tools"] = ToolCatalog.Describe(negotiatedVersion == "2024-11-05") });
                    break;
                case "tools/call":
                    RequireReady();
                    var definition = ToolCatalog.Find(MailboxBridge.String(parameters, "name") ?? throw new ArgumentException("Tool name is required."));
                    var arguments = parameters["arguments"] == null ? new JsonObject() : parameters["arguments"] as JsonObject ?? throw new ArgumentException("Tool arguments must be an object.");
                    ToolCatalog.Validate(definition, arguments);
                    // Older Lua versions interpreted rename_group as a native EFX
                    // mutation. Always canonicalize the compatibility tool here so
                    // connecting to an old bridge fails closed instead of renaming.
                    string bridgeAction = definition.Action;
                    JsonObject bridgeArguments = arguments;
                    if (bridgeAction == "rename_group")
                    {
                        bridgeAction = "set_group_note";
                        bridgeArguments = new JsonObject { ["index"] = arguments["index"]!.DeepClone(),
                            ["note"] = arguments["name"]!.DeepClone(), ["expectedRevision"] = arguments["expectedRevision"]!.DeepClone() };
                    }
                    string bridgeId = "mcp-" + session + "-" + ++sequence;
                    JsonObject outcome;
                    try { outcome = await bridge.Call(bridgeId, bridgeAction, bridgeArguments, cancellationToken); }
                    catch (BridgeException error)
                    {
                        outcome = new JsonObject { ["protocol"] = 1, ["id"] = error.RequestId ?? bridgeId, ["ok"] = false,
                            ["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message, ["requestId"] = error.RequestId ?? bridgeId, ["uncertain"] = error.Uncertain } };
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
                    {
                        outcome = new JsonObject { ["protocol"] = 1, ["id"] = bridgeId, ["ok"] = false,
                            ["error"] = new JsonObject { ["code"] = "bridge_io_error", ["message"] = error.Message + " Check editor state before retrying a mutation.", ["requestId"] = bridgeId, ["uncertain"] = true } };
                    }
                    var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome.ToJsonString() }),
                        ["isError"] = outcome["ok"]?.GetValue<bool>() != true };
                    if (negotiatedVersion != "2024-11-05") result["structuredContent"] = outcome;
                    response = Result(id, result);
                    break;
                default:
                    // Includes modern server/discover: clients can fall back to this
                    // explicitly implemented legacy lifecycle on -32601.
                    response = Error(id, -32601, "Method not found: " + method);
                    break;
            }
        }
        catch (ArgumentException error) { response = Error(id, -32602, error.Message); }
        catch (InvalidOperationException error) { response = Error(id, -32002, error.Message); }
        Cache(key, serialized, response);
        return response;
    }

    private void RequireReady()
    {
        if (!initialized || !clientReady) throw new InvalidOperationException("Complete initialize and notifications/initialized before using tools.");
    }
    private static bool ValidId(JsonNode? id, bool supplied) => !supplied || id is JsonValue value &&
        (value.TryGetValue<string>(out string? text) ? text.Length is > 0 and <= 256 : long.TryParse(value.ToJsonString(), out _));
    internal static JsonObject Error(JsonNode? id, int code, string message) => new()
    { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    private static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };
    private void Cache(string key, string request, JsonObject response)
    {
        seen.Add(key);
        long bytes = ((long)request.Length + response.ToJsonString().Length) * 2;
        if (bytes > 32 * 1024 * 1024) return;
        while (order.Count >= 64 || cacheBytes + bytes > 32 * 1024 * 1024)
        {
            string oldest = order.Dequeue(); cacheBytes -= cache[oldest].Bytes; cache.Remove(oldest);
        }
        cache[key] = new(request, (JsonObject)response.DeepClone(), bytes); order.Enqueue(key); cacheBytes += bytes;
    }
}
