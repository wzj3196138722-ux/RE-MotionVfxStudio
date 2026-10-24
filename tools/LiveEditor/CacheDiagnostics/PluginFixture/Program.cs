using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using MotionVfx.CacheDiagnostics;

public static class CacheProbeFixtureProgram
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--compiler-only") return InstalledCompiler(args[1], args[2], args[3]);
        string output = Path.GetFullPath(args[0]);
        string run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string root = Path.Combine(output, "cache-plugin-fixture-" + run);
        string directory = Path.Combine(root, "reframework", "data", "MotionVfxLive");
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        var fixture = new byte[4096];
        var pin = GCHandle.Alloc(fixture, GCHandleType.Pinned);
        CacheProbeMailbox mailbox = null;
        try
        {
            ulong holder = checked((ulong)pin.AddrOfPinnedObject().ToInt64());
            ulong native = holder + 128, child = holder + 1024;
            Buffer.BlockCopy(BitConverter.GetBytes(native), 0, fixture, 16, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(child), 0, fixture, 128 + 24, 8);
            fixture[1024 + 63] = 0xAB;
            // Owned serialized EFX fixture: one entry named A, no attributes, mandatory
            // empty UVAR words. Its string table is exactly 48 bytes after the header.
            const int efxOffset = 2048, efxLength = 74;
            fixture[efxOffset] = (byte)'e'; fixture[efxOffset + 1] = (byte)'f'; fixture[efxOffset + 2] = (byte)'x'; fixture[efxOffset + 3] = (byte)'r';
            fixture[efxOffset + 8] = 1; fixture[efxOffset + 12] = 2; fixture[efxOffset + 48] = (byte)'A';
            Buffer.BlockCopy(BitConverter.GetBytes(holder + efxOffset + 48), 0, fixture, 128 + 0x88, 8);
            using (var reader = ReadOnlyProcessMemory.CreateForCurrentProcess(Environment.ProcessPath))
            {
                Check(reader.UsesCurrentProcessPseudoHandle && reader.ProcessId == Environment.ProcessId, "Self factory uses current-process pseudo handle", checks);
                reader.EnsureIdentity();
                Check(reader.ReadExact(holder, 4096).AsSpan().SequenceEqual(fixture), "Pseudo-handle RPM returns exactly the owned 4096-byte fixture", checks);
                bool refused = false;
                try { reader.ReadExact(1, 24); } catch (InvalidDataException) { refused = true; }
                Check(refused, "Pseudo-handle reader rejects invalid addresses", checks);
            }
            bool wrongExecutable = false;
            try { using var reader = ReadOnlyProcessMemory.CreateForCurrentProcess(Path.Combine(root, "wrong.exe")); }
            catch (InvalidOperationException) { wrongExecutable = true; }
            Check(wrongExecutable, "Pseudo-handle reader still enforces exact executable identity", checks);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string logical = "vfx/fixture/original.efx", actual = "MotionVfxLive/rev_9/vfx/fixture/original.efx";
            var source = new Dictionary<string, object> {
                ["sourceId"] = "fixture-source", ["eventId"] = "fixture-event", ["sceneEpoch"] = 2L,
                ["actualPath"] = actual, ["sharedTargetPath"] = logical, ["actualHolderIdentity"] = holder.ToString(),
                ["localConfirmed"] = true, ["sourceKind"] = "actual_effect_player_holder", ["availability"] = "retained_observation"
            };
            var record = new Dictionary<string, object> { ["canonicalPath"] = logical, ["localConfirmed"] = true, ["latestSource"] = source };
            var ui = new Dictionary<string, object> {
                ["loaded"] = true, ["loadedAt"] = now, ["observedAt"] = now,
                ["captureActor"] = new { available = true, sceneEpoch = 2 }, ["captureResources"] = new[] { record }
            };
            var request = new Dictionary<string, object> {
                ["id"] = "fixture-direct", ["action"] = "probe", ["logicalResourcePath"] = logical,
                ["holderIdentity"] = holder.ToString(), ["sourceId"] = "fixture-source", ["sceneEpoch"] = 2L,
                ["loadedAt"] = now, ["inspectDirectPointers"] = true, ["eventId"] = "fixture-event", ["expectedActualPath"] = actual
            };
            string status = Path.Combine(directory, "ui-status.json"), requestFile = Path.Combine(directory, "cache-probe-request.json"), responseFile = Path.Combine(directory, "cache-probe-response.json");
            Write(status, ui);
            mailbox = new CacheProbeMailbox(root, Environment.ProcessPath);
            using (var result = JsonDocument.Parse(JsonSerializer.Serialize(mailbox.ProcessRequest(Element(request), CancellationToken.None))))
            {
                var body = result.RootElement;
                Check(body.GetProperty("status").GetString() == "diagnostic_complete_not_authenticated", "Complete mailbox request succeeds on owned self-process fixture", checks);
                Check(body.GetProperty("result").GetProperty("Qwords").GetArrayLength() == 64 && body.GetProperty("result").GetProperty("NativeHexRows").GetArrayLength() == 32, "Mailbox root probe remains exactly 512 bytes / 64 qwords", checks);
                Check(body.GetProperty("result").GetProperty("Qwords")[3].GetProperty("PrefixHex").GetString().EndsWith("AB"), "Mailbox optional direct child is a 64-byte prefix", checks);
                Check(body.GetProperty("result").GetProperty("RequestedAccess").GetInt32() == 0 && body.GetProperty("result").GetProperty("HandleKind").GetString() == "current_process_pseudo_handle_minus_one", "Mailbox reports pseudo handle without an OpenProcess access request", checks);
                Check(!body.GetProperty("authenticatedCacheFile").GetBoolean() && !body.GetProperty("usableForEditing").GetBoolean() && body.GetProperty("unverified").GetBoolean(), "Candidates never acquire authentication or editing permission", checks);
                Check(body.GetProperty("candidateFiles").GetArrayLength() == 0, "Non-EFX self fixture creates no binary candidate", checks);
                Check(body.GetProperty("result").GetProperty("StringTableHeader").ValueKind == JsonValueKind.Null, "Default request does not inspect bytes before the string-table pointer", checks);
            }
            request["id"] = "fixture-explicit-string-header";
            request["inspectStringTableHeader"] = true;
            using (var result = JsonDocument.Parse(JsonSerializer.Serialize(mailbox.ProcessRequest(Element(request), CancellationToken.None))))
            {
                var body = result.RootElement;
                var observation = body.GetProperty("result").GetProperty("StringTableHeader");
                Check(observation.GetProperty("PrefixHex").GetString().StartsWith("65 66 78 72") && observation.GetProperty("PrefixHex").GetString().Length == 191 && observation.GetProperty("PointerStable").GetBoolean(), "Explicit +0x88 hypothesis reads exactly 64 prefix bytes and rechecks the root pointer", checks);
                var candidate = body.GetProperty("result").GetProperty("Candidates")[0];
                Check(candidate.GetProperty("Framing").GetProperty("Valid").GetBoolean() && candidate.GetProperty("Framing").GetProperty("ExactLength").GetInt32() == efxLength, "Header-before-string-table fixture validates the exact serialized EFX length", checks);
                var file = body.GetProperty("candidateFiles")[0];
                Check(File.ReadAllBytes(file.GetProperty("path").GetString()).AsSpan().SequenceEqual(fixture.AsSpan(efxOffset, efxLength)) && file.GetProperty("unverified").GetBoolean(), "Only exact candidate bytes are saved with unverified status", checks);
            }
            request["id"] = "fixture-direct";
            request.Remove("inspectStringTableHeader");
            string typeLog = Path.Combine(root, "re2_framework_log.txt");
            DateTime logUtc = DateTime.UtcNow.AddSeconds(-1);
            string localStamp = TimeZoneInfo.ConvertTimeFromUtc(logUtc, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            string typeLine = "[" + localStamp + "] [info] Found TypeList: 154890a30 at ref 140001000\n";
            File.WriteAllText(typeLog, typeLine);
            var evidence = CacheProbeMailbox.ReadTypeListLogEvidence(typeLog, logUtc.AddMinutes(-1));
            Check(evidence.Address == "0x0000000154890A30" && Math.Abs((DateTime.Parse(evidence.LogTimestampUtc).ToUniversalTime() - logUtc).TotalMilliseconds) < 2, "TypeList startup timestamp is converted from local time to UTC", checks);
            bool badLogRejected = false;
            try { CacheProbeMailbox.ReadTypeListLogEvidence(typeLog, logUtc.AddSeconds(1)); } catch (InvalidDataException) { badLogRejected = true; }
            Check(badLogRejected, "Startup evidence from before process creation is rejected", checks);
            File.WriteAllText(typeLog, typeLine + typeLine.Replace("154890a30", "154890a38"));
            badLogRejected = false;
            try { CacheProbeMailbox.ReadTypeListLogEvidence(typeLog, logUtc.AddMinutes(-1)); } catch (InvalidDataException) { badLogRejected = true; }
            Check(badLogRejected, "Conflicting current-run TypeList addresses are rejected", checks);
            File.WriteAllText(typeLog, new string(' ', 2 * 1024 * 1024) + "\n" + typeLine);
            badLogRejected = false;
            try { CacheProbeMailbox.ReadTypeListLogEvidence(typeLog, logUtc.AddMinutes(-1)); } catch (InvalidDataException) { badLogRejected = true; }
            Check(badLogRejected, "TypeList lines beyond the first 2 MiB are never accepted", checks);
            File.WriteAllText(typeLog, typeLine.TrimEnd('\n'));
            badLogRejected = false;
            try { CacheProbeMailbox.ReadTypeListLogEvidence(typeLog, logUtc.AddMinutes(-1)); } catch (InvalidDataException) { badLogRejected = true; }
            Check(badLogRejected, "A truncated final TypeList log line is rejected", checks);
            File.WriteAllText(typeLog, typeLine);
            request["action"] = "native_type_metadata"; request["typeListAddress"] = "0x154890a30";
            RejectCurrent(mailbox, request, "Metadata request cannot supply an arbitrary TypeList address", checks);
            request["action"] = "probe"; request.Remove("typeListAddress");
            Reject(mailbox, request, "sourceId", "wrong-source", "Changed source ID is rejected", checks);
            Reject(mailbox, request, "sceneEpoch", 3L, "Changed scene epoch is rejected", checks);
            Reject(mailbox, request, "loadedAt", now - 1, "Changed UI lifetime is rejected", checks);
            Reject(mailbox, request, "holderIdentity", (holder + 8).ToString(), "Changed holder identity is rejected", checks);
            Reject(mailbox, request, "eventId", "wrong-event", "Changed event identity is rejected", checks);
            Reject(mailbox, request, "expectedActualPath", "vfx/fixture/different.efx", "Changed actual path is rejected", checks);
            Reject(mailbox, request, "action", "automatic", "Only explicit probe action is accepted", checks);
            Reject(mailbox, request, "id", "../escape", "Unsafe request ID cannot create an output path", checks);
            ui["observedAt"] = now - 20; Write(status, ui);
            RejectCurrent(mailbox, request, "Stale UI is rejected", checks);
            ui["observedAt"] = now; ui["captureResources"] = null; Write(status, ui);
            RejectCurrent(mailbox, request, "Cleared capture list is rejected", checks);
            ui["captureResources"] = new[] { record }; Write(status, ui);
            bool cancelled = false;
            try { mailbox.ProcessRequest(Element(request), new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancellation before a probe performs no memory read", checks);

            request["id"] = "fixture-existing"; Write(requestFile, request);
            mailbox.Start(); Thread.Sleep(300);
            Check(!File.Exists(responseFile), "Startup ignores an existing valid request", checks);
            request["id"] = "fixture-new"; Write(requestFile, request);
            WaitUntil(() => File.Exists(responseFile), 3000);
            using (var reply = JsonDocument.Parse(File.ReadAllText(responseFile)))
                Check(reply.RootElement.GetProperty("id").GetString() == "fixture-new" && reply.RootElement.GetProperty("status").GetString() == "diagnostic_complete_not_authenticated", "New explicit request is served by the background mailbox", checks);
            string originalReply = File.ReadAllText(responseFile);
            request["action"] = "invalid-repeat"; Write(requestFile, request); Thread.Sleep(350);
            Check(File.ReadAllText(responseFile) == originalReply, "The same request ID is never processed twice", checks);
            var timer = Stopwatch.StartNew(); mailbox.Stop(); timer.Stop();
            Check(timer.ElapsedMilliseconds < 500, "Unload cancellation and bounded join do not wait indefinitely", checks);
            using (var pluginStatus = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "cache-probe-plugin-status.json"))))
                Check(!pluginStatus.RootElement.GetProperty("ready").GetBoolean(), "Stopped component reports ready=false", checks);
            mailbox = null;
            string reportPath = Path.Combine(output, "cache-plugin-self-fixture-" + run + ".json");
            Write(reportPath, new { status = "passed", checks, gameRead = false, gameDeployment = false, nativeProcessWrites = false, fixtureRoot = root });
            Console.WriteLine(reportPath);
            Console.WriteLine("Standalone plugin compiled; self-process fixture passed: " + checks.Count + " checks. No game accessed or deployment performed.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { mailbox?.Stop(); pin.Free(); }
    }

    private static int InstalledCompiler(string sourcePath, string gameRoot, string output)
    {
        string run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        string dependencies = Path.Combine(gameRoot, "reframework", "plugins", "managed", "dependencies");
        var previousOut = Console.Out; var previousError = Console.Error;
        using var messages = new StringWriter();
        var errors = new List<string>();
        var entrypoints = new List<string>();
        byte[] compiled = null; Exception failure = null;
        try
        {
            Console.SetOut(messages); Console.SetError(messages);
            var refs = new List<Assembly>();
            foreach (string name in new[] { "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll", "REFCoreDeps.dll" })
                refs.Add(AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dependencies, name)));
            var compiler = refs[2].GetType("REFrameworkNET.Compiler", true);
            // This fixture assembly deliberately exports namespace "_", matching the game
            // generated references that make bare out _ fail with CS0118.
            refs.Add(typeof(global::_.GeneratedGameNamespaceFixture).Assembly);
            compiler.GetProperty("OnCompileError")?.SetValue(null, new Action<string>(errors.Add));
            var framework = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gameRoot, "reframework", "plugins", "REFramework.NET.dll"));
            compiled = (byte[])compiler.GetMethod("Compile").Invoke(null, new object[] { Path.GetFullPath(sourcePath), framework, refs });
            if (compiled != null)
            {
                // Inspect metadata only. Never invoke an entry point or initialize the plugin.
                var assembly = Assembly.Load(compiled);
                foreach (var type in assembly.GetTypes())
                    foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        foreach (var attribute in method.GetCustomAttributesData())
                            if (attribute.AttributeType.FullName == "REFrameworkNET.Attributes.PluginEntryPoint")
                                entrypoints.Add(type.FullName + "." + method.Name);
                if (entrypoints.Count != 1) throw new Exception("Expected exactly one discoverable plugin entry point, found " + entrypoints.Count);
            }
        }
        catch (Exception ex) { failure = ex.GetBaseException(); }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
        Directory.CreateDirectory(output);
        string log = Path.Combine(output, "installed-plugin-compiler-" + run + ".log");
        File.WriteAllText(log, messages.ToString());
        string report = Path.Combine(output, "installed-plugin-compiler-" + run + ".json");
        Write(report, new { status = compiled != null && failure == null ? "compiled_without_running_entrypoint" : "failed", bytes = compiled?.Length ?? 0, errors, entrypoints, generatedUnderscoreNamespaceReference = true, exception = failure?.ToString(), compiler = Path.Combine(dependencies, "REFCoreDeps.dll"), source = Path.GetFullPath(sourcePath), log, pluginMainCalled = false, gameAccessed = false });
        Console.WriteLine(report);
        if (compiled == null || failure != null) { Console.Error.WriteLine(failure?.ToString() ?? string.Join(Environment.NewLine, errors)); return 1; }
        Console.WriteLine("Installed REFCoreDeps Compiler.Compile succeeded; plugin Main was not called.");
        return 0;
    }

    private static JsonElement Element(object value) { using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value)); return doc.RootElement.Clone(); }
    private static void Write(string path, object value) { string temp = path + ".fixture.tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true); }
    private static void Check(bool success, string message, List<string> checks) { if (!success) throw new Exception(message); checks.Add(message); }
    private static void RejectCurrent(CacheProbeMailbox mailbox, Dictionary<string, object> request, string message, List<string> checks) { bool rejected = false; try { mailbox.ProcessRequest(Element(request), CancellationToken.None); } catch (InvalidDataException) { rejected = true; } Check(rejected, message, checks); }
    private static void Reject(CacheProbeMailbox mailbox, Dictionary<string, object> request, string field, object value, string message, List<string> checks) { object previous = request[field]; request[field] = value; try { RejectCurrent(mailbox, request, message, checks); } finally { request[field] = previous; } }
    private static void WaitUntil(Func<bool> condition, int timeout) { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException("Fixture mailbox did not respond."); Thread.Sleep(20); } }
}
