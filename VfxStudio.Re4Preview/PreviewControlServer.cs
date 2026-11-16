using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VfxStudio.Re4Preview;

/// <summary>One request from the AI control pipe. The window thread owns every field except
/// <see cref="Reply"/>, which completes the waiting pipe connection.</summary>
internal sealed class PreviewControlRequest(string id, string action, JsonObject arguments)
{
    public string Id { get; } = id;
    public string Action { get; } = action;
    public JsonObject Arguments { get; } = arguments;
    public TaskCompletionSource<JsonObject> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DateTime Deadline { get; set; } = DateTime.UtcNow.AddSeconds(90);
    // Progress kept by the window thread between updates.
    public int Phase { get; set; }
    public Task<VfxStudio.Re4Render.PreviewEditorState>? EditTask { get; set; }
    public JsonObject? BackendReply { get; set; }
    public int? Tick { get; set; }
    public byte[]? Pixels { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public Task<JsonObject>? Encoding { get; set; }

    public void Succeed(JsonObject result) => Reply.TrySetResult(new JsonObject { ["protocol"] = PreviewControlServer.Protocol, ["id"] = Id, ["ok"] = true, ["result"] = result });
    public void Fail(string code, string message, JsonNode? details = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (details != null) error["details"] = details;
        Reply.TrySetResult(new JsonObject { ["protocol"] = PreviewControlServer.Protocol, ["id"] = Id, ["ok"] = false, ["error"] = error });
    }
}

/// <summary>Named pipe through which the MCP server drives this window. One connection carries one
/// JSON line each way. The pipe accepts only the current Windows user; requests run on the window
/// thread, one at a time, through the same backend session as the edit panel.</summary>
internal sealed class PreviewControlServer : IDisposable
{
    public const string DefaultPipe = "MotionVfx.Re4Preview.Control";
    public const int Protocol = 1;
    private const int MaximumRequestBytes = 1024 * 1024;
    private readonly ConcurrentQueue<PreviewControlRequest> queue = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly NamedPipeServerStream? server;

    public string PipeName { get; }
    /// <summary>Why the pipe is unavailable, or null while it is listening.</summary>
    public string? Error { get; private set; }

    public PreviewControlServer(string pipeName)
    {
        PipeName = pipeName;
        try
        {
            // A single instance holds the name for the window's lifetime, so a second window
            // cannot take over the channel between connections.
            server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "AI 控制通道已被另一个预览窗口占用：" + pipeName;
        }
        loop = server == null ? Task.CompletedTask : Task.Run(Run);
    }

    public bool TryDequeue(out PreviewControlRequest request) => queue.TryDequeue(out request!);

    private async Task Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await server!.WaitForConnectionAsync(stop.Token);
                await Serve(server);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            try { if (server!.IsConnected) server.Disconnect(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        JsonObject reply;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            byte[]? line = await ReadLine(pipe, timeout.Token);
            reply = line == null ? Rejected(null, "invalid_request", "请求超过 1 MiB 或没有换行。") : await Dispatch(line);
        }
        byte[] bytes = new UTF8Encoding(false).GetBytes(reply.ToJsonString() + "\n");
        await pipe.WriteAsync(bytes, stop.Token);
        await pipe.FlushAsync(stop.Token);
        pipe.WaitForPipeDrain();
    }

    private async Task<JsonObject> Dispatch(byte[] line)
    {
        JsonObject request;
        try { request = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return Rejected(null, "invalid_request", "请求必须是一行 JSON 对象。"); }
        string? id = request["id"] is JsonValue idValue && idValue.TryGetValue(out string? text) ? text : null;
        if (string.IsNullOrEmpty(id) || id.Length > 200) return Rejected(null, "invalid_request", "缺少有效的请求 id。");
        if (request["protocol"]?.ToJsonString() != Protocol.ToString()) return Rejected(id, "protocol_mismatch", $"预览窗口只支持控制协议 {Protocol}。");
        if (request["action"] is not JsonValue actionValue || !actionValue.TryGetValue(out string? action) || string.IsNullOrEmpty(action))
            return Rejected(id, "invalid_request", "缺少 action。");
        var arguments = request["arguments"] as JsonObject ?? new JsonObject();
        var item = new PreviewControlRequest(id, action, (JsonObject)arguments.DeepClone());
        queue.Enqueue(item);
        using var registration = stop.Token.Register(() => item.Fail("window_closing", "预览窗口正在关闭，请求结果不确定。"));
        return await item.Reply.Task;
    }

    private static JsonObject Rejected(string? id, string code, string message) => new()
    { ["protocol"] = Protocol, ["id"] = id, ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task<byte[]?> ReadLine(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var line = new MemoryStream();
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) return null;
            int end = Array.IndexOf(buffer, (byte)'\n', 0, count);
            line.Write(buffer, 0, end < 0 ? count : end);
            if (line.Length > MaximumRequestBytes) return null;
            if (end >= 0) return line.ToArray();
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        while (queue.TryDequeue(out var item)) item.Fail("window_closing", "预览窗口正在关闭，请求未执行。");
        try { server?.Dispose(); } catch (IOException) { }
        try { loop.Wait(2000); } catch (AggregateException) { }
    }
}
