using System.Text.Json;
using System.Text.Json.Nodes;

namespace VfxStudio.Mcp;

internal sealed class McpServer(IEditorBridge bridge, IToolSet? toolSet = null)
{
    private readonly IToolSet tools = toolSet ?? ToolCatalog.Live;
    public const string Version = "0.1.3";
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
                        ["serverInfo"] = new JsonObject { ["name"] = tools.ServerName, ["version"] = Version },
                        ["instructions"] = tools.Instructions
                    });
                    break;
                case "ping": response = Result(id, new JsonObject()); break;
                case "tools/list":
                    RequireReady();
                    if (parameters["cursor"] != null) throw new ArgumentException("This server returns all tools in one page; cursor is unsupported.");
                    response = Result(id, new JsonObject { ["tools"] = ToolCatalog.Describe(negotiatedVersion == "2024-11-05", tools) });
                    break;
                case "tools/call":
                    RequireReady();
                    var definition = ToolCatalog.Find(MailboxBridge.String(parameters, "name") ?? throw new ArgumentException("Tool name is required."), tools);
                    var arguments = parameters["arguments"] == null ? new JsonObject() : parameters["arguments"] as JsonObject ?? throw new ArgumentException("Tool arguments must be an object.");
                    tools.Validate(definition, arguments);
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
                    var content = new JsonArray();
                    // An image result (base64 "data" plus "mimeType") becomes an image block; the
                    // text and structured copies keep its metadata without the payload.
                    if (outcome["result"] is JsonObject image && MailboxBridge.String(image, "mimeType") is string mime && mime.StartsWith("image/", StringComparison.Ordinal) &&
                        MailboxBridge.String(image, "data") is string data)
                    {
                        image.Remove("data");
                        content.Add(new JsonObject { ["type"] = "image", ["data"] = data, ["mimeType"] = mime });
                    }
                    content.Insert(0, new JsonObject { ["type"] = "text", ["text"] = outcome.ToJsonString() });
                    var result = new JsonObject { ["content"] = content, ["isError"] = outcome["ok"]?.GetValue<bool>() != true };
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
