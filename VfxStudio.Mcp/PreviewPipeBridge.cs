using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VfxStudio.Mcp;

/// <summary>Talks to a running VfxStudio.Re4Preview window over its control pipe (one JSON line each
/// way per connection). "launch" is handled here: it starts the window when none answers.</summary>
internal sealed class PreviewPipeBridge(string pipeName, string? repository, TimeSpan? replyTimeout = null) : IEditorBridge
{
    public const string DefaultPipe = "MotionVfx.Re4Preview.Control";
    private readonly TimeSpan reply = replyTimeout ?? TimeSpan.FromSeconds(150);
    private const int MaximumReplyBytes = 64 * 1024 * 1024;

    public async Task<JsonObject> Call(string requestId, string action, JsonObject arguments, CancellationToken cancellationToken)
        => action == "launch" ? await Launch(requestId, arguments, cancellationToken) : await Send(requestId, action, arguments, cancellationToken);

    private async Task<JsonObject> Send(string requestId, string action, JsonObject arguments, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(3000, cancellationToken); }
        catch (TimeoutException)
        {
            throw new BridgeException("preview_not_running", "No RE4 preview window answers on pipe " + pipeName +
                ". Call re4preview_launch, or start VfxStudio.Re4Preview yourself. A window that is busy with another AI request also refuses new connections; retry after it finishes.");
        }
        var request = new JsonObject { ["protocol"] = 1, ["id"] = requestId, ["action"] = action, ["arguments"] = arguments.DeepClone() };
        await pipe.WriteAsync(new UTF8Encoding(false).GetBytes(request.ToJsonString() + "\n"), cancellationToken);
        await pipe.FlushAsync(cancellationToken);
        // From here the window may act on the request: a lost reply leaves the outcome uncertain.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(reply);
        byte[]? line;
        try { line = await ReadLine(pipe, timeout.Token); }
        catch (OperationCanceledException)
        { throw new BridgeException("outcome_uncertain", "No reply from the preview window in time. It may still finish; call re4preview_status before repeating a mutation.", requestId, true); }
        catch (IOException)
        { throw new BridgeException("outcome_uncertain", "The preview window closed the connection before replying. Call re4preview_status before repeating a mutation.", requestId, true); }
        if (line == null) throw new BridgeException("outcome_uncertain", "The preview window closed the connection before replying. Call re4preview_status before repeating a mutation.", requestId, true);
        JsonObject response;
        try { response = JsonNode.Parse(line) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new BridgeException("invalid_bridge_response", "The preview window returned an invalid reply; the outcome is uncertain.", requestId, true); }
        if (response["ok"] is not JsonValue ok || !ok.TryGetValue<bool>(out _))
            throw new BridgeException("invalid_bridge_response", "The preview window reply has no ok flag; the outcome is uncertain.", requestId, true);
        return response;
    }

    private static async Task<byte[]?> ReadLine(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        using var line = new MemoryStream();
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) return null;
            int end = Array.IndexOf(buffer, (byte)'\n', 0, count);
            line.Write(buffer, 0, end < 0 ? count : end);
            if (line.Length > MaximumReplyBytes) throw new IOException("Preview reply exceeds 64 MiB.");
            if (end >= 0) return line.ToArray();
        }
    }

    private async Task<JsonObject> Launch(string requestId, JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            var running = await Send(requestId + "-probe", "status", new JsonObject(), cancellationToken);
            return Wrap(requestId, running, alreadyRunning: true);
        }
        catch (BridgeException error) when (error.Code == "preview_not_running") { }

        string repo = repository ?? FindRepository() ?? throw new BridgeException("repository_not_found",
            "Could not find the repository (tools/Re4Native/re4host.py). Start the MCP server with --repo <repository>.");
        string? exe = new[] { "Release", "Debug" }.Select(c => System.IO.Path.Combine(repo, "VfxStudio.Re4Preview", "bin", c, "net8.0", "VfxStudio.Re4Preview.exe")).FirstOrDefault(File.Exists)
            ?? throw new BridgeException("preview_not_built", "VfxStudio.Re4Preview is not built. Run: dotnet build VfxStudio.Re4Preview -c Release");
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = repo, UseShellExecute = false, CreateNoWindow = true,
            // Never let the window inherit this server's stdio, which carries the MCP stream.
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--repo"); info.ArgumentList.Add(repo);
        info.ArgumentList.Add("--ai-pipe"); info.ArgumentList.Add(pipeName);
        if (arguments["fixture"] is JsonValue fixture) { info.ArgumentList.Add("--fixture"); info.ArgumentList.Add(fixture.GetValue<string>()); }
        if (arguments["path"] is JsonValue path)
        {
            info.ArgumentList.Add("--efx"); info.ArgumentList.Add(path.GetValue<string>());
            string offset = arguments["offset"]!.GetValue<string>();
            info.ArgumentList.Add("--offset"); info.ArgumentList.Add(offset.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? offset[2..] : offset);
        }
        if (arguments["quality"] is JsonValue quality) { info.ArgumentList.Add("--quality"); info.ArgumentList.Add(quality.GetValue<string>()); }
        string logDirectory = System.IO.Path.Combine(repo, "runtime", "re4-preview");
        Directory.CreateDirectory(logDirectory);
        string log = System.IO.Path.Combine(logDirectory, $"mcp-launch-{DateTime.Now:yyyyMMdd_HHmmss}.log");
        var writer = TextWriter.Synchronized(new StreamWriter(new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true });
        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) try { writer.WriteLine(e.Data); } catch (ObjectDisposedException) { } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) try { writer.WriteLine(e.Data); } catch (ObjectDisposedException) { } };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine(); process.BeginErrorReadLine();

        // Wait for the pipe, then for the first document to be shown (the first native load takes ~5 s).
        var watch = Stopwatch.StartNew();
        JsonObject? last = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(90))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new BridgeException("launch_failed", $"The preview window exited with code {process.ExitCode}. See {log}.");
            try
            {
                last = await Send(requestId + "-wait", "status", new JsonObject(), cancellationToken);
                var result = last["result"];
                var preview = result?["preview"];
                // Settled: a frame is shown and nothing is still loading (first loads may reload once to
                // leave out an entry the emulator cannot run), or loading failed, or no document opened.
                bool shown = preview?["displayedRevision"] != null && preview?["loadingRevision"] == null && preview?["ready"]?.GetValue<bool>() == true;
                bool failed = preview?["loadError"] != null ||
                    result?["document"]?["open"]?.GetValue<bool>() != true && result?["window"]?["editError"] != null;
                if (shown || failed)
                    return Wrap(requestId, last, alreadyRunning: false, log);
            }
            catch (BridgeException error) when (error.Code == "preview_not_running") { }
            await Task.Delay(500, cancellationToken);
        }
        if (last != null) return Wrap(requestId, last, alreadyRunning: false, log);
        throw new BridgeException("launch_failed", $"The preview window did not open its control pipe within 90 s. See {log}.");
    }

    private static JsonObject Wrap(string requestId, JsonObject status, bool alreadyRunning, string? log = null)
    {
        var response = (JsonObject)status.DeepClone();
        response["id"] = requestId;
        if (response["result"] is JsonObject result)
        {
            result["alreadyRunning"] = alreadyRunning;
            if (log != null) result["launchLog"] = log;
        }
        return response;
    }

    internal static string? FindRepository()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var folder = new DirectoryInfo(start); folder != null; folder = folder.Parent)
                if (File.Exists(System.IO.Path.Combine(folder.FullName, "tools", "Re4Native", "re4host.py"))) return folder.FullName;
        return null;
    }
}
