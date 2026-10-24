using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace MotionVfx.CacheDiagnostics
{
    // This entry point never resolves a game object or performs a probe automatically.
    // It starts a cancellable file mailbox. Only an explicit, new, validated request can read.
    public static class MotionVfxCacheDiagnostics
    {
        private static CacheProbeMailbox mailbox;

        [REFrameworkNET.Attributes.PluginEntryPoint]
        public static void Main()
        {
            string processPath = null, currentDirectory = null, gameRoot = null;
            try
            {
                currentDirectory = Environment.CurrentDirectory;
                processPath = Environment.ProcessPath;
                gameRoot = FindStartupRoot(processPath, currentDirectory);
                if (gameRoot == null) throw new InvalidOperationException("Cannot locate a verified game directory containing MonsterHunterWilds.exe from ProcessPath or CurrentDirectory.");
                CacheProbeMailbox.WriteStartupStatus(gameRoot, "starting", processPath, currentDirectory, null);
                string expectedExecutable = Path.Combine(gameRoot, "MonsterHunterWilds.exe");
                // Establish identity through Windows itself if Environment.ProcessPath is
                // unavailable in the embedded host. These are process metadata queries only;
                // startup never reads a holder or native resource buffer.
                using (var identity = ReadOnlyProcessMemory.CreateForCurrentProcess(expectedExecutable))
                {
                    identity.EnsureIdentity();
                    mailbox = new CacheProbeMailbox(gameRoot, identity.ExecutablePath);
                }
                mailbox.Start();
            }
            catch (Exception error)
            {
                try
                {
                    currentDirectory ??= Environment.CurrentDirectory;
                    gameRoot ??= FindStartupRoot(processPath, currentDirectory);
                    if (gameRoot != null) CacheProbeMailbox.WriteStartupStatus(gameRoot, "startup_failed", processPath, currentDirectory, error);
                    else Console.Error.WriteLine("[MotionVfxCacheDiagnostics] Startup failed: " + error);
                }
                catch (Exception statusError) { Console.Error.WriteLine("[MotionVfxCacheDiagnostics] Startup/status failure: " + error + "; " + statusError); }
            }
        }

        private static string FindStartupRoot(string processPath, string currentDirectory)
        {
            if (!string.IsNullOrWhiteSpace(processPath) && string.Equals(Path.GetFileName(processPath), "MonsterHunterWilds.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(processPath))
                return Path.GetDirectoryName(Path.GetFullPath(processPath));
            if (!string.IsNullOrWhiteSpace(currentDirectory) && File.Exists(Path.Combine(currentDirectory, "MonsterHunterWilds.exe")))
                return Path.GetFullPath(currentDirectory);
            return null;
        }

        [REFrameworkNET.Attributes.PluginExitPoint]
        public static void OnUnload()
        {
            var previous = mailbox;
            mailbox = null;
            // Cancellation wakes the poll immediately; never wait indefinitely on a game thread.
            previous?.Stop();
        }
    }

    public sealed class CacheProbeMailbox
    {
        public const string Version = "0.1.8";
        private readonly string gameRootDirectory, directory, expectedExecutable, requestPath, responsePath, statusPath;
        private readonly string candidateDirectory;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly HashSet<string> handledIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> handledHashes = new HashSet<string>(StringComparer.Ordinal);
        private readonly DateTime startedAt = DateTime.UtcNow;
        private Thread thread;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        internal CacheProbeMailbox(string gameRoot, string processExecutable)
        {
            gameRootDirectory = Path.GetFullPath(gameRoot);
            directory = Path.Combine(gameRootDirectory, "reframework", "data", "MotionVfxLive");
            expectedExecutable = Path.GetFullPath(processExecutable);
            requestPath = Path.Combine(directory, "cache-probe-request.json");
            responsePath = Path.Combine(directory, "cache-probe-response.json");
            statusPath = Path.Combine(directory, "cache-probe-plugin-status.json");
            candidateDirectory = Path.Combine(directory, "cache-diagnostics");
        }

        public void Start()
        {
            if (thread != null) throw new InvalidOperationException("Mailbox is already started.");
            Directory.CreateDirectory(directory);
            // Even a valid old request is inert after loading/reloading the plugin.
            if (File.Exists(requestPath))
            {
                try
                {
                    byte[] previous = ReadBoundedFile(requestPath, 32768);
                    handledHashes.Add(Convert.ToHexString(SHA256.HashData(previous)));
                    using var document = JsonDocument.Parse(previous);
                    if (document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        handledIds.Add(id.GetString());
                }
                catch { /* Existing incomplete requests are also rejected by startup mtime. */ }
            }
            WriteStatus(true, "waiting_for_explicit_request");
            thread = new Thread(Listen) { IsBackground = true, Name = "MotionVfx cache diagnostics mailbox" };
            thread.Start();
        }

        public void Stop()
        {
            cancellation.Cancel();
            bool stopped = thread == null || thread.Join(100);
            // A rare outstanding OS read remains on the background thread and exits at its
            // next cancellation check. No main-thread retry, abort, or unbounded join.
            try { WriteStatus(false, stopped ? "stopped" : "stopping"); } catch { }
        }

        private void WriteStatus(bool ready, string state)
        {
            WriteAtomic(statusPath, new {
                version = Version, ready, state, processId = Environment.ProcessId,
                executablePath = expectedExecutable, startedAtUtc = startedAt.ToString("O"),
                environmentProcessPath = Environment.ProcessPath, environmentCurrentDirectory = Environment.CurrentDirectory,
                observedAtUtc = DateTime.UtcNow.ToString("O"), automaticProbes = false,
                nativeProcessWrites = false, candidateDirectory
            });
        }

        internal static void WriteStartupStatus(string gameRoot, string state, string processPath, string currentDirectory, Exception error)
        {
            if (!File.Exists(Path.Combine(gameRoot, "MonsterHunterWilds.exe"))) throw new InvalidDataException("Startup diagnostics require an existing game executable in the target directory.");
            string directory = Path.Combine(gameRoot, "reframework", "data", "MotionVfxLive");
            Directory.CreateDirectory(directory);
            WriteAtomic(Path.Combine(directory, "cache-probe-plugin-status.json"), new {
                ready = false, version = Version, state, processId = Environment.ProcessId,
                observedAtUtc = DateTime.UtcNow.ToString("O"), environmentProcessPath = processPath,
                environmentCurrentDirectory = currentDirectory, diagnosticGameRoot = gameRoot,
                error = error?.GetBaseException().Message, exception = error?.ToString(), automaticProbes = false, nativeProcessWrites = false
            });
        }

        private void Listen()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    PollOnce();
                    cancellation.Token.WaitHandle.WaitOne(200);
                }
            }
            finally { try { WriteStatus(false, "stopped"); } catch { } }
        }

        private void PollOnce()
        {
            string id = null;
            try
            {
                if (!File.Exists(requestPath) || File.GetLastWriteTimeUtc(requestPath) <= startedAt) return;
                byte[] requestBytes = ReadBoundedFile(requestPath, 32768);
                string hash = Convert.ToHexString(SHA256.HashData(requestBytes));
                if (handledHashes.Contains(hash)) return;
                if (handledHashes.Count >= 256) throw new InvalidOperationException("Diagnostic request limit reached; reload the component before another request.");
                handledHashes.Add(hash);
                using var request = JsonDocument.Parse(requestBytes);
                id = RequiredString(request.RootElement, "id");
                if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,96}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Request id must contain 1-96 letters, digits, underscore or hyphen.");
                if (!handledIds.Add(id)) return;
                var response = ProcessRequest(request.RootElement, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                WriteAtomic(responsePath, response);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested)
                    try { WriteAtomic(responsePath, ErrorResponse(id, error)); } catch { }
            }
        }

        private static object ErrorResponse(string id, Exception error) => new {
            id, status = "rejected", version = Version, processId = Environment.ProcessId,
            error = error.GetBaseException().Message, observedAtUtc = DateTime.UtcNow.ToString("O"),
            authenticatedCacheFile = false, usableForEditing = false, unverified = true,
            nativeProcessWrites = false, candidateFiles = Array.Empty<object>()
        };

        internal object ProcessRequest(JsonElement request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string id = RequiredString(request, "id");
            if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,96}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid request id.");
            string action = RequiredString(request, "action");
            if (action != "probe" && action != "native_type_metadata") throw new InvalidDataException("Only explicit probe or native_type_metadata actions are supported.");
            string logical = NormalizeEfx(RequiredString(request, "logicalResourcePath"));
            bool direct = request.TryGetProperty("inspectDirectPointers", out var directValue) && directValue.GetBoolean();
            bool inspectHeader = request.TryGetProperty("inspectStringTableHeader", out var headerValue) && headerValue.GetBoolean();
            var before = ReadSource(request, logical);
            token.ThrowIfCancellationRequested();
            // All reads use -1, the current-process pseudo handle, with checked pages and RPM.
            // This is NOT an engine object API and it never dereferences or writes native memory.
            using var reader = ReadOnlyProcessMemory.CreateForCurrentProcess(expectedExecutable);
            if (DateTimeOffset.FromUnixTimeSeconds(before.LoadedAt).UtcDateTime < reader.CreationTimeUtc.AddSeconds(-1))
                throw new InvalidDataException("UI source predates the current game process.");
            reader.EnsureIdentity();
            if (action == "native_type_metadata")
                return ProcessNativeTypeMetadata(request, logical, before, reader, token);
            var result = reader.ProbeRetainedHolder(before.Holder, direct, token, inspectHeader);
            token.ThrowIfCancellationRequested();
            var after = ReadSource(request, logical);
            if (!before.SameIdentity(after)) throw new InvalidDataException("Retained source, UI lifetime, or scene changed while reading.");
            byte[] holderAgain = reader.ReadExact(after.Holder, 24);
            if (ReadOnlyProcessMemory.Hex(ReadOnlyProcessMemory.PointerAt(holderAgain, 16)) != result.NativeResourceAddress)
                throw new InvalidDataException("Retained holder native pointer changed after source revalidation.");
            reader.EnsureStringTableHeaderPointerStable(result);
            reader.EnsureIdentity();
            token.ThrowIfCancellationRequested();
            var files = new List<object>();
            foreach (var candidate in result.Candidates)
            {
                token.ThrowIfCancellationRequested();
                byte[] bytes = candidate.GetBytes();
                if (bytes == null || !candidate.StableDoubleRead || candidate.Framing == null || !candidate.Framing.Valid) continue;
                Directory.CreateDirectory(candidateDirectory);
                string path = Path.Combine(candidateDirectory, "cache-candidate-" + id + "-" + files.Count + ".unauthenticated.bin");
                // Never overwrite any earlier diagnostic with the same name, even after reload.
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) output.Write(bytes);
                files.Add(new { path, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    address = candidate.Address, unverified = true, status = "UNAUTHENTICATED_DIAGNOSTIC_ONLY_NOT_FOR_EDITING" });
            }
            return new {
                id, status = "diagnostic_complete_not_authenticated", version = Version, processId = reader.ProcessId,
                processStartedAtUtc = reader.CreationTimeUtc.ToString("O"), observedAtUtc = DateTime.UtcNow.ToString("O"),
                logicalResourcePath = logical, before = before.Descriptor, after = after.Descriptor,
                authenticatedCacheFile = false, usableForEditing = false, unverified = true, nativeProcessWrites = false,
                directPointerDepth = direct ? 1 : 0, inspectStringTableHeader = inspectHeader, rawNativeBinarySaved = false, result, candidateFiles = files,
                holderLayoutBasis = "REFramework runtime_size()=0x10 and Resource::create_holder; not an EffectResource payload layout."
            };
        }

        private object ProcessNativeTypeMetadata(JsonElement request, string logical, Source before, ReadOnlyProcessMemory reader, CancellationToken token)
        {
            if (request.TryGetProperty("typeListAddress", out var arbitraryAddress))
                throw new InvalidDataException("TypeList addresses cannot be supplied by requests; only the current-process startup log is accepted.");
            token.ThrowIfCancellationRequested();
            string logPath = Path.Combine(gameRootDirectory, "re2_framework_log.txt");
            var evidence = ReadTypeListLogEvidence(logPath, reader.CreationTimeUtc);
            byte[] holderBefore = reader.ReadExact(before.Holder, 24);
            ulong nativeBefore = ReadOnlyProcessMemory.PointerAt(holderBefore, 16);
            if (!ReadOnlyProcessMemory.PlausiblePointer(nativeBefore)) throw new InvalidDataException("Retained holder native resource pointer is invalid.");
            var metadata = NativeTypeMetadataProbe.Read(reader, evidence.AddressValue, token);
            token.ThrowIfCancellationRequested();
            var after = ReadSource(request, logical);
            if (!before.SameIdentity(after)) throw new InvalidDataException("Retained source, UI lifetime, or scene changed during metadata observation.");
            if (ReadOnlyProcessMemory.PointerAt(reader.ReadExact(after.Holder, 24), 16) != nativeBefore)
                throw new InvalidDataException("Retained holder native resource pointer changed during metadata observation.");
            var afterEvidence = ReadTypeListLogEvidence(logPath, reader.CreationTimeUtc);
            if (afterEvidence.AddressValue != evidence.AddressValue || afterEvidence.ObservedUtc != evidence.ObservedUtc)
                throw new InvalidDataException("Current-process TypeList startup evidence changed while reading.");
            reader.EnsureIdentity();
            token.ThrowIfCancellationRequested();
            return new {
                id = RequiredString(request, "id"), status = "native_type_metadata_complete_not_authenticated", version = Version,
                processId = reader.ProcessId, processStartedAtUtc = reader.CreationTimeUtc.ToString("O"), observedAtUtc = DateTime.UtcNow.ToString("O"),
                logicalResourcePath = logical, before = before.Descriptor, after = after.Descriptor, typeListEvidence = evidence, metadata,
                authenticatedCacheFile = false, usableForEditing = false, unverified = true, nativeProcessWrites = false,
                candidateFiles = Array.Empty<object>(), rawNativeBinarySaved = false
            };
        }

        internal sealed class TypeListLogEvidence
        {
            public string LogPath { get; internal set; }
            public string Address { get; internal set; }
            public string LogTimestampLocal { get; internal set; }
            public string LogTimestampUtc { get; internal set; }
            public string LocalTimeZone { get; internal set; }
            public int PrefixBytesRead { get; internal set; }
            public int MaximumPrefixBytes { get; } = 2 * 1024 * 1024;
            public string PrefixSha256 { get; internal set; }
            public bool BelongsAfterCurrentProcessStart { get; } = true;
            public bool Unverified { get; } = true;
            internal ulong AddressValue;
            internal DateTime ObservedUtc;
        }

        internal static TypeListLogEvidence ReadTypeListLogEvidence(string logPath, DateTime processCreatedUtc)
        {
            const int maximum = 2 * 1024 * 1024;
            byte[] prefix;
            using (var input = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int length = (int)Math.Min(input.Length, maximum);
                if (length == 0) throw new InvalidDataException("Game startup log is empty.");
                prefix = new byte[length]; int total = 0;
                while (total < length)
                {
                    int read = input.Read(prefix, total, length - total);
                    if (read == 0) throw new InvalidDataException("Startup log changed during its bounded read.");
                    total += read;
                }
            }
            // REFramework log timestamps are LOCAL wall-clock time, not UTC. Ignore old-run
            // records and reject DST-ambiguous times instead of guessing which process owns them.
            var pattern = new Regex(@"^.*?\[(?<stamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\].*?\bFound TypeList: (?:0x)?(?<address>[0-9a-fA-F]{1,16})\b", RegexOptions.CultureInvariant);
            TypeListLogEvidence found = null;
            string text = Encoding.UTF8.GetString(prefix);
            int finalNewline = text.LastIndexOf('\n');
            if (finalNewline < 0) throw new InvalidDataException("The bounded startup-log prefix contains no complete line.");
            using var lines = new StringReader(text.Substring(0, finalNewline + 1));
            string line;
            while ((line = lines.ReadLine()) != null)
            {
                var match = pattern.Match(line);
                if (!match.Success) continue;
                DateTime local = DateTime.SpecifyKind(DateTime.ParseExact(match.Groups["stamp"].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
                if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
                    throw new InvalidDataException("TypeList log time is invalid or ambiguous in the local time zone.");
                DateTime utc = TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);
                if (utc < processCreatedUtc || utc > DateTime.UtcNow.AddSeconds(2)) continue;
                ulong address = ulong.Parse(match.Groups["address"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                if (!ReadOnlyProcessMemory.PlausiblePointer(address)) throw new InvalidDataException("Startup log TypeList address is invalid.");
                if (found != null && found.AddressValue != address) throw new InvalidDataException("Startup log has conflicting TypeList addresses for the current process.");
                if (found != null) continue;
                found = new TypeListLogEvidence {
                    LogPath = Path.GetFullPath(logPath), Address = ReadOnlyProcessMemory.Hex(address), AddressValue = address,
                    LogTimestampLocal = match.Groups["stamp"].Value, LogTimestampUtc = utc.ToString("O"), ObservedUtc = utc,
                    LocalTimeZone = TimeZoneInfo.Local.Id, PrefixBytesRead = prefix.Length,
                    PrefixSha256 = Convert.ToHexString(SHA256.HashData(prefix))
                };
            }
            if (found == null) throw new InvalidDataException("No TypeList startup record after this process's creation was found within the log's first 2 MiB.");
            return found;
        }

        private sealed class Source
        {
            internal string SourceId, EventId, ActualPath, SharedTarget;
            internal long Epoch, LoadedAt;
            internal ulong Holder;
            internal JsonElement Descriptor;
            internal bool SameIdentity(Source other) => SourceId == other.SourceId && EventId == other.EventId &&
                ActualPath == other.ActualPath && SharedTarget == other.SharedTarget && Epoch == other.Epoch &&
                LoadedAt == other.LoadedAt && Holder == other.Holder;
        }

        private Source ReadSource(JsonElement request, string logical)
        {
            string path = Path.Combine(directory, "ui-status.json");
            DateTime modified = File.GetLastWriteTimeUtc(path);
            using var document = JsonDocument.Parse(ReadBoundedFile(path, 8 * 1024 * 1024));
            var ui = document.RootElement;
            double age = (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(RequiredLong(ui, "observedAt"))).TotalSeconds;
            if (age < -2 || age > 8 || (DateTime.UtcNow - modified).TotalSeconds > 8) throw new InvalidDataException("UI source is not fresh (maximum 8 seconds).");
            if (!IsTrue(ui, "loaded") || !ui.TryGetProperty("captureActor", out var actor) || !IsTrue(actor, "available"))
                throw new InvalidDataException("No usable capture record/source: local capture actor is unavailable.");
            if (!ui.TryGetProperty("captureResources", out var resources) || resources.ValueKind != JsonValueKind.Array || resources.GetArrayLength() == 0)
                throw new InvalidDataException("No usable capture record/source: capture list is empty or cleared.");
            JsonElement matched = default;
            int count = 0;
            foreach (var record in resources.EnumerateArray())
            {
                if (NormalizeEfx(RequiredString(record, "canonicalPath")) != logical) continue;
                matched = record; count++;
            }
            if (count != 1) throw new InvalidDataException("Expected one matching current capture record.");
            if (!IsTrue(matched, "localConfirmed") || !matched.TryGetProperty("latestSource", out var source) || source.ValueKind != JsonValueKind.Object ||
                !IsTrue(source, "localConfirmed") || RequiredString(source, "sourceKind") != "actual_effect_player_holder" || RequiredString(source, "availability") != "retained_observation")
                throw new InvalidDataException("No locally confirmed, retained actual EffectPlayer holder source.");
            var value = new Source {
                SourceId = RequiredString(source, "sourceId"), EventId = RequiredString(source, "eventId"),
                ActualPath = RequiredString(source, "actualPath"), SharedTarget = RequiredString(source, "sharedTargetPath"),
                Epoch = RequiredLong(source, "sceneEpoch"), LoadedAt = RequiredLong(ui, "loadedAt"),
                Holder = ParseAddress(RequiredString(source, "actualHolderIdentity")), Descriptor = source.Clone()
            };
            NormalizeEfx(value.ActualPath); // generated rev_N paths are intentionally permitted
            if (value.Epoch < 1 || value.Epoch != RequiredLong(actor, "sceneEpoch") || NormalizeEfx(value.SharedTarget) != logical ||
                value.SourceId != RequiredString(request, "sourceId") || value.Holder != ParseAddress(RequiredString(request, "holderIdentity")) ||
                value.Epoch != RequiredLong(request, "sceneEpoch") || value.LoadedAt != RequiredLong(request, "loadedAt"))
                throw new InvalidDataException("Request no longer matches the retained source, path, holder, scene, or UI lifetime.");
            if (request.TryGetProperty("eventId", out var expectedEvent) && expectedEvent.GetString() != value.EventId)
                throw new InvalidDataException("Request event identity differs from the actual retained event.");
            if (request.TryGetProperty("expectedActualPath", out var expectedPath) && NormalizeEfx(expectedPath.GetString()) != NormalizeEfx(value.ActualPath))
                throw new InvalidDataException("Request actual resource path differs from the retained observation.");
            return value;
        }

        private static bool IsTrue(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.True;

        private static string RequiredString(JsonElement value, string property)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var result) || result.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(result.GetString()))
                throw new InvalidDataException("Missing or invalid required text field: " + property);
            return result.GetString();
        }

        private static long RequiredLong(JsonElement value, string property)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var result) || !result.TryGetInt64(out long number))
                throw new InvalidDataException("Missing or invalid required integer field: " + property);
            return number;
        }

        private static ulong ParseAddress(string text)
        {
            ulong value;
            bool valid = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) :
                ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            if (!valid || !ReadOnlyProcessMemory.PlausiblePointer(value)) throw new InvalidDataException("Holder identity is not an exact aligned address.");
            return value;
        }

        internal static string NormalizeEfx(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Missing logical EFX path.");
            string path = value.Trim().Replace('\\', '/');
            if (path.StartsWith("@", StringComparison.Ordinal)) path = path.Substring(1).TrimStart('/');
            if (path.StartsWith("natives/stm/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(12);
            path = Regex.Replace(path, @"(?i)\.efx\.\d+$", ".efx");
            if (!path.EndsWith(".efx", StringComparison.OrdinalIgnoreCase) || path.Any(c => c < 32 || ":*?\"<>|".IndexOf(c) >= 0) ||
                path.Split('/').Any(p => p == "" || p == "." || p == "..")) throw new InvalidDataException("EFX path must be complete and game relative.");
            return path.ToLowerInvariant();
        }

        private static byte[] ReadBoundedFile(string path, int maximum)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < 1 || file.Length > maximum) throw new InvalidDataException("File is empty or exceeds the diagnostic size limit.");
            using var bytes = new MemoryStream();
            byte[] block = new byte[8192]; int count;
            while ((count = file.Read(block, 0, Math.Min(block.Length, maximum + 1 - (int)bytes.Length))) != 0)
            {
                bytes.Write(block, 0, count);
                if (bytes.Length > maximum) throw new InvalidDataException("File grew beyond its diagnostic bound.");
            }
            return bytes.ToArray();
        }

        private static void WriteAtomic(string path, object value)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
    }
}
