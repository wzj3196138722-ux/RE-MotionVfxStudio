using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VfxStudio.Mcp;

internal sealed record BridgeTiming(TimeSpan Normal, TimeSpan Long, TimeSpan Poll, TimeSpan Freshness)
{
    public static BridgeTiming Default { get; } = new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(15));
}

internal sealed class BridgeException(string code, string message, string? requestId = null, bool uncertain = false) : Exception(message)
{
    public string Code { get; } = code;
    public string? RequestId { get; } = requestId;
    public bool Uncertain { get; } = uncertain;
}

internal interface IEditorBridge
{
    Task<JsonObject> Call(string requestId, string action, JsonObject arguments, CancellationToken cancellationToken);
}

/// <summary>Only exchanges the fixed AI mailbox with Lua. Never accesses the backend mailbox or native game objects.</summary>
internal sealed class MailboxBridge(string gameRoot, BridgeTiming? timing = null) : IEditorBridge
{
    private readonly string directory = Path.Combine(Path.GetFullPath(gameRoot), "reframework", "data", "MotionVfxLive");
    private readonly BridgeTiming limits = timing ?? BridgeTiming.Default;
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    internal string DirectoryPath => directory;

    public async Task<JsonObject> Call(string requestId, string action, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) throw new BridgeException("editor_not_installed", "MotionVfx Live's game mailbox is not present. Install and load the editor first.");
        if (string.IsNullOrEmpty(requestId) || requestId.Length > 200) throw new ArgumentException("Invalid bridge request ID.");
        FileStream guard;
        try { guard = new FileStream(Path.Combine(directory, "ai-bridge.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new BridgeException("bridge_busy", "Another MCP process owns the AI bridge; no request was submitted."); }
        using (guard)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckReady();
            string requestPath = Path.Combine(directory, "ai-request.json"), responsePath = Path.Combine(directory, "ai-response.json");
            var priorRequest = ReadOptional(requestPath, 1024 * 1024);
            var priorResponse = ReadOptional(responsePath, MaximumResponseBytes);
            if (priorRequest != null)
            {
                string? previousId = String(priorRequest, "id");
                if (string.IsNullOrEmpty(previousId) || Protocol(priorRequest) != 1) throw new BridgeException("mailbox_invalid", "The previous AI request has no valid protocol/ID; it was not overwritten.");
                bool answered = priorResponse != null && String(priorResponse, "id") == previousId && Protocol(priorResponse) == 1 &&
                    priorResponse["ok"] is JsonValue previousOk && previousOk.TryGetValue<bool>(out _);
                if (previousId == requestId)
                {
                    if (String(priorRequest, "action") != action || !JsonNode.DeepEquals(priorRequest["arguments"], arguments))
                        throw new BridgeException("request_id_conflict", "A request ID cannot be reused with different arguments.", requestId);
                    if (answered) return ValidateResponse(priorResponse!, requestId);
                    throw new BridgeException("outcome_uncertain", "This request was already submitted and has no final answer. It was not replayed. Wait for Lua and check the editor before issuing another mutation.", requestId, true);
                }
                if (!answered) throw new BridgeException("previous_request_pending", "An earlier AI request has no final answer and was not overwritten. Wait for the editor to answer; do not automatically retry mutations.", previousId, true);
            }
            // Recheck immediately before publishing; stale UI state cannot authorize a write.
            CheckReady();
            TimeSpan timeout = action is "open_effect" or "apply" or "restore" ? limits.Long : limits.Normal;
            var request = new JsonObject { ["protocol"] = 1, ["id"] = requestId, ["action"] = action,
                ["arguments"] = arguments.DeepClone(), ["expiresAtUnixSeconds"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(1L, (long)Math.Ceiling(timeout.TotalSeconds)) };
            if (Encoding.UTF8.GetByteCount(request.ToJsonString()) > 1024 * 1024) throw new BridgeException("request_too_large", "AI request exceeds 1 MiB; no request was submitted.");
            AtomicJson(requestPath, request);
            var watch = Stopwatch.StartNew();
            try
            {
                while (watch.Elapsed < timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    JsonObject? response = null;
                    try { response = ReadOptional(responsePath, MaximumResponseBytes); }
                    catch (Exception error) when (error is IOException or JsonException) { /* Lua may be atomically replacing the response. */ }
                    if (response != null && String(response, "id") == requestId) return ValidateResponse(response, requestId);
                    await Task.Delay(limits.Poll, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            { throw new BridgeException("outcome_uncertain", "The caller stopped waiting after submission. The editor may still finish. The request was not retried or cancelled in Lua.", requestId, true); }
            throw new BridgeException("outcome_uncertain", "Timed out after submitting the request. Lua may still complete it; do not automatically retry a mutation. Check this request ID in the in-game editor and use its stop/restore controls if needed. This AI channel remains blocked until the pending request is answered.", requestId, true);
        }
    }

    private void CheckReady()
    {
        JsonObject? status;
        try { status = ReadOptional(Path.Combine(directory, "ui-status.json"), 4 * 1024 * 1024); }
        catch (Exception error) when (error is IOException or JsonException)
        { throw new BridgeException("editor_not_ready", "The editor status could not be read; no request was submitted."); }
        if (status == null || !Boolean(status, "loaded") || !Boolean(status, "aiReady") || Protocol(status, "aiProtocol") != 1 || string.IsNullOrEmpty(String(status, "version")))
            throw new BridgeException("editor_not_ready", "Load a MotionVfx Live version supporting AI bridge protocol 1 before calling this tool.");
        if (!TryInteger(status["observedAt"], out long observed)) throw new BridgeException("editor_not_ready", "The editor has no valid freshness timestamp.");
        long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - observed;
        if (age < -5 || age > limits.Freshness.TotalSeconds) throw new BridgeException("editor_status_stale", "The editor status is stale. Make sure the game and MotionVfx Live are responding; no request was submitted.");
    }

    private static JsonObject ValidateResponse(JsonObject response, string id)
    {
        if (Protocol(response) != 1 || String(response, "id") != id || response["ok"] is not JsonValue ok || !ok.TryGetValue<bool>(out _))
            throw new BridgeException("invalid_bridge_response", "The editor returned an invalid final response. The operation's outcome is uncertain.", id, true);
        return (JsonObject)response.DeepClone();
    }

    internal static JsonObject? ReadOptional(string path, int maximumBytes)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximumBytes) throw new IOException("Mailbox document exceeds the bounded read limit.");
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (memory.Length + count > maximumBytes) throw new IOException("Mailbox document grew beyond the bounded read limit.");
            memory.Write(buffer, 0, count);
        }
        return JsonNode.Parse(memory.ToArray()) as JsonObject ?? throw new JsonException("Mailbox document is not an object.");
    }

    internal static void AtomicJson(string path, JsonObject value)
    {
        string temporary = path + ".mcp-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
                stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string? String(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool Boolean(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue<bool>(out bool result) && result;
    private static int Protocol(JsonObject obj, string key = "protocol") => TryInteger(obj[key], out long result) && result is >= int.MinValue and <= int.MaxValue ? (int)result : -1;
    private static bool TryInteger(JsonNode? value, out long result) => long.TryParse(value?.ToJsonString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);
}
