using System.Text.Json;
using System.Text.Json.Nodes;
using VfxStudio.LiveEditor;

return await Host.Run(args);

namespace VfxStudio.LiveEditor
{
    internal sealed record Options(string Assets, string Mailbox, string Output, string? GameRoot, bool SelfTest, string? LogDirectory = null)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool selfTest = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--self-test") { selfTest = true; continue; }
                if (args[i] is not ("--assets" or "--mailbox" or "--output" or "--game-root" or "--log-directory") || ++i >= args.Length)
                    throw new ArgumentException("Usage: VfxStudio.LiveEditor --assets <dir> --mailbox <dir> --output <dir> [--game-root <dir>] [--self-test]");
                values[args[i - 1]] = Path.GetFullPath(args[i]);
            }
            var cwd = Directory.GetCurrentDirectory();
            return new(values.GetValueOrDefault("--assets", Path.Combine(cwd, "Assets")),
                values.GetValueOrDefault("--mailbox", Path.Combine(cwd, "LiveEditorMailbox")),
                values.GetValueOrDefault("--output", Path.Combine(cwd, "LiveEditorOutput")),
                values.GetValueOrDefault("--game-root"), selfTest, values.GetValueOrDefault("--log-directory"));
        }
    }

    internal static class Host
    {
        public const string Version = "0.6.4";
        internal static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            MaxDepth = 128,
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static async Task<int> Run(string[] args)
        {
            string? actionAudit = null;
            int auditIndex = Array.IndexOf(args, "--action-source-audit");
            if (auditIndex >= 0 && auditIndex + 1 < args.Length)
            { actionAudit = args[auditIndex + 1]; args = args.Where((_, i) => i != auditIndex && i != auditIndex + 1).ToArray(); }
            bool precacheTest = args.Contains("--precache-self-test");
            bool embeddedTest = args.Contains("--embedded-action-self-test");
            args = args.Where(a => a != "--precache-self-test" && a != "--embedded-action-self-test").ToArray();
            Options options;
            try { options = Options.Parse(args); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
            if (options.LogDirectory != null)
            {
                Directory.CreateDirectory(options.LogDirectory);
                StreamWriter Log(string name) => new(new FileStream(Path.Combine(options.LogDirectory, name), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                Console.SetOut(TextWriter.Synchronized(Log("backend.stdout.log")));
                Console.SetError(TextWriter.Synchronized(Log("backend.stderr.log")));
            }
            if (precacheTest) return PrecacheTests.RunCommand(options);
            if (embeddedTest) return EmbeddedActionTests.RunCommand(options);
            if (actionAudit != null) return ActionSlotAudit.Run(options, actionAudit);
            if (options.SelfTest) return SelfTests.Run(options);
            Directory.CreateDirectory(options.Mailbox);
            Directory.CreateDirectory(options.Output);
            FileStream processLock;
            try { processLock = new FileStream(Path.Combine(options.Mailbox, "backend.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { Console.Error.WriteLine("Another backend already owns this mailbox."); return 3; }
            using (processLock)
            using (var engine = new EditorEngine(options))
            using (var stop = new CancellationTokenSource())
            {
                string startedAt = DateTimeOffset.UtcNow.ToString("O");
                object Status(bool ready) => new { ready, protocol = 1, version = Version, pid = Environment.ProcessId, startedAt, heartbeatUtc = DateTimeOffset.UtcNow.ToString("O"), heartbeatUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), mailbox = options.Mailbox };
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
                // Clients wait for ready before sending work. Everything already present
                // belongs to the previous host, including requests without a response.
                string requestPath = Path.Combine(options.Mailbox, "request.json");
                string? lastPayload = File.Exists(requestPath) ? ReadSharedText(requestPath) : null;
                JsonObject? pendingResponse = null;
                string? pendingPayload = null;
                AtomicJson(Path.Combine(options.Mailbox, "status.json"), Status(true));
                Console.WriteLine($"MotionVfx live editor {Version}; mailbox: {options.Mailbox}");
                long heartbeat = Environment.TickCount64;
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        if (Environment.TickCount64 - heartbeat >= 2000)
                        {
                            try
                            {
                                AtomicJson(Path.Combine(options.Mailbox, "status.json"), Status(true));
                                engine.PollGameSession();
                                heartbeat = Environment.TickCount64;
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        }
                        if (pendingResponse != null)
                        {
                            try
                            {
                                AtomicJson(Path.Combine(options.Mailbox, "response.json"), pendingResponse);
                                lastPayload = pendingPayload;
                                pendingResponse = null;
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                // Windows readers may temporarily prevent replacement.
                                // Keep the exact response; never execute the mutation twice.
                                await Task.Delay(50, stop.Token);
                                continue;
                            }
                        }
                        if (engine.ExitRequested) break;
                        if (File.Exists(requestPath))
                        {
                            string? payload = null;
                            try
                            {
                                if (new FileInfo(requestPath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Request exceeds 4 MiB.");
                                payload = ReadSharedText(requestPath);
                                if (payload != lastPayload)
                                {
                                    // Ignore partial JSON writes until the producer finishes. Lua normally uses a temp file + rename.
                                    var request = JsonNode.Parse(payload)?.AsObject() ?? throw new JsonException("Request must be an object.");
                                    pendingResponse = engine.Handle(request);
                                    pendingPayload = payload;
                                }
                            }
                            catch (JsonException) { }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine(ex.Message);
                                if (payload != null) lastPayload = payload;
                            }
                        }
                        await Task.Delay(50, stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
                finally { AtomicJson(Path.Combine(options.Mailbox, "status.json"), Status(false)); }
            }
            return 0;
        }

        private static string ReadSharedText(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        internal static void AtomicJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Move(temp, path, overwrite: true); break; }
                    catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                    { Thread.Sleep(20); }
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
