using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using VfxStudio.Mcp;

return await Entry.Run(args);

namespace VfxStudio.Mcp
{
    internal static class Entry
    {
        internal const int MaximumLineCharacters = 1024 * 1024;
        public static async Task<int> Run(string[] args)
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.SequenceEqual(new[] { "--version" })) { Console.WriteLine(McpServer.Version); return 0; }
            if (args.SequenceEqual(new[] { "--tools-schema" })) { Console.WriteLine(new JsonObject { ["tools"] = ToolCatalog.Describe(false) }.ToJsonString()); return 0; }
            if (args.SequenceEqual(new[] { "--self-test" })) return await SelfTests.Run();
            string gameRoot;
            try { gameRoot = ResolveGameRoot(args); }
            catch (Exception error) when (error is ArgumentException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            { Console.Error.WriteLine(error.Message); return 2; }
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
            var server = new McpServer(new MailboxBridge(gameRoot));
            try
            {
                await foreach (string? line in ReadLines(Console.In, stop.Token))
                {
                    var response = line == null ? McpServer.Error(null, -32700, "Input line exceeds 1 MiB.") : await server.Handle(line, stop.Token);
                    if (response != null) { await Console.Out.WriteLineAsync(response.ToJsonString()); await Console.Out.FlushAsync(); }
                }
                return 0;
            }
            catch (OperationCanceledException) { return 0; }
            catch (Exception error) { Console.Error.WriteLine("MCP transport stopped: " + error.Message); return 1; }
        }

        internal static string ResolveGameRoot(string[] args)
        {
            if (args.Length == 2 && args[0] == "--game-root") return Path.GetFullPath(args[1]);
            string config;
            if (args.Length == 0) config = Path.Combine(AppContext.BaseDirectory, "..", "runtime-config.json");
            else if (args.Length == 2 && args[0] == "--config") config = Path.GetFullPath(args[1]);
            else throw new ArgumentException("Usage: VfxStudio.Mcp --game-root <game directory> | --config <runtime-config.json> | --version | --tools-schema | --self-test");
            var json = MailboxBridge.ReadOptional(config, 64 * 1024) ?? throw new ArgumentException("Runtime config not found. Run the package installer or pass --game-root.");
            string root = MailboxBridge.String(json, "gameRoot") ?? throw new ArgumentException("runtime-config.json must contain gameRoot.");
            if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("gameRoot in runtime-config.json must be an absolute path.");
            return Path.GetFullPath(root);
        }

        internal static async IAsyncEnumerable<string?> ReadLines(TextReader input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var buffer = new char[4096]; var line = new StringBuilder(); bool oversized = false;
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    char character = buffer[i];
                    if (character == '\n')
                    {
                        yield return oversized ? null : line.ToString().TrimEnd('\r');
                        line.Clear(); oversized = false;
                    }
                    else if (!oversized)
                    {
                        if (line.Length == MaximumLineCharacters) { oversized = true; line.Clear(); }
                        else line.Append(character);
                    }
                }
            }
            if (oversized || line.Length != 0) yield return oversized ? null : line.ToString().TrimEnd('\r');
        }
    }
}
