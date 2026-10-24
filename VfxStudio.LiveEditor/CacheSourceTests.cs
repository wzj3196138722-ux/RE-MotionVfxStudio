using System.Text.Json;
using System.Text.Json.Nodes;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class CacheSourceTests
{
    private const string Logical = "Art/VFX/EffectEditor/Weapon/it00/11_it00_900.efx";
    private static JsonObject Descriptor() => new()
    {
        ["sourceKind"] = "actual_effect_player_holder", ["sourceId"] = "source-7-19", ["eventId"] = "capture-7-19",
        ["sceneEpoch"] = 7, ["actualPath"] = Logical, ["playerIdentity"] = "101262649712",
        ["actualHolderIdentity"] = "101262649888", ["sharedTargetPath"] = Logical, ["localConfirmed"] = true,
        ["availability"] = "retained_observation", ["currentPlayerVerified"] = false, ["cacheBytesAvailable"] = false
    };

    private sealed class Reader(Func<CacheSourceDescriptor, long, VerifiedCacheSnapshot> callback) : IGameCacheResourceReader
    {
        public int Calls { get; private set; }
        public VerifiedCacheSnapshot Read(CacheSourceDescriptor source, long expectedSceneEpoch)
        {
            Calls++;
            return callback(source, expectedSceneEpoch);
        }
    }

    internal static void Run(Options options, string output, Action<bool, string> check)
    {
        string root = Path.Combine(output, "cache-source"), assets = Path.Combine(root, "assets"), game = Path.Combine(root, "mock-game");
        string sample = Path.Combine(options.Assets, "VfxShared/natives/stm/" + Logical.ToLowerInvariant() + ".5571972");
        byte[] original = File.ReadAllBytes(sample);
        string exact = Path.Combine(assets, "Original/natives/stm/" + Logical + ".5571972");
        string loose = Path.Combine(game, "natives/STM/" + Logical + ".5571972");
        Directory.CreateDirectory(Path.GetDirectoryName(exact)!); File.WriteAllBytes(exact, original);
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!); File.WriteAllBytes(loose, original);
        var testOptions = options with { Assets = assets, GameRoot = game, Mailbox = Path.Combine(root, "mailbox"), Output = Path.Combine(root, "saved") };
        using var engine = new EditorEngine(testOptions);
        long revision = 0;
        JsonObject Request(string action, JsonObject? payload = null)
        {
            var request = payload ?? new JsonObject();
            request["id"] = Guid.NewGuid().ToString("N"); request["action"] = action; request["revision"] = revision;
            var result = engine.Handle(request);
            revision = result["revision"]?.GetValue<long>() ?? revision;
            return result;
        }
        JsonObject CacheRequest(JsonObject? descriptor = null, long epoch = 7) => new()
        {
            ["sourceDescriptor"] = descriptor ?? Descriptor(), ["currentSceneEpoch"] = epoch,
            // Even explicit fallback hints must not be interpreted by this action.
            ["path"] = exact, ["resourcePath"] = Logical
        };
        var unavailable = Request("open_cached_resource", CacheRequest());
        check(unavailable["ok"]!.GetValue<bool>() == false && unavailable["errorCode"]!.GetValue<string>() == "cache_reader_unavailable" &&
            unavailable["error"]!.GetValue<string>().Contains("未打开本地文件") && unavailable["document"] == null && revision == 0 &&
            unavailable["resolvedPath"] == null && unavailable["candidates"] == null,
            "Cache selection never opens matching project or game loose files when no verified reader exists");

        var opened = Request("open", new() { ["path"] = exact });
        check(opened["ok"]!.GetValue<bool>() && opened["document"]!["sourceKind"]!.GetValue<string>() == "local_file",
            "Explicit offline opens identify their document as local_file");
        JsonObject EntryTarget() => new() { ["kind"] = "entry", ["index"] = 0 };
        Request("set", new() { ["target"] = EntryTarget(), ["path"] = "name", ["value"] = "cache_guard_first" });
        Request("set", new() { ["target"] = EntryTarget(), ["path"] = "name", ["value"] = "cache_guard_second" });
        Request("undo");
        var baseline = Request("status");
        string baselineDocument = baseline["document"]!.ToJsonString();
        long baselineRevision = revision;
        check(baseline["document"]!["dirty"]!.GetValue<bool>() && baseline["document"]!["canUndo"]!.GetValue<bool>() &&
            baseline["document"]!["canRedo"]!.GetValue<bool>(), "Cache guard regression starts with unsaved edits and both history directions");
        var staleSelection = CacheRequest(); staleSelection["id"] = Guid.NewGuid().ToString("N");
        staleSelection["action"] = "open_cached_resource"; staleSelection["revision"] = baselineRevision - 1;
        var staleSelectionResult = engine.Handle(staleSelection);
        check(!staleSelectionResult["ok"]!.GetValue<bool>() && staleSelectionResult["document"]!.ToJsonString() == baselineDocument &&
            staleSelectionResult["revision"]!.GetValue<long>() == baselineRevision,
            "An asynchronous cache selection cannot replace a newer document revision");
        void Preserved(JsonObject result, string code, string label)
        {
            check(!result["ok"]!.GetValue<bool>() && result["errorCode"]?.GetValue<string>() == code && revision == baselineRevision &&
                result["document"]!.ToJsonString() == baselineDocument, label);
        }
        Preserved(Request("open_cached_resource", CacheRequest()), "cache_reader_unavailable",
            "Unavailable cache reads preserve the existing dirty document, revision and undo/redo flags");
        Preserved(Request("open_cached_resource", CacheRequest(epoch: 8)), "cache_source_expired",
            "An earlier scene epoch is rejected without replacing the current document");
        foreach (var invalid in new (string Field, JsonNode? Value)[]
        {
            ("sourceId", ""), ("eventId", null), ("actualHolderIdentity", "0"), ("playerIdentity", 123),
            ("sourceKind", "local_file"), ("localConfirmed", false), ("sceneEpoch", 7.5), ("actualPath", "../escape.efx"),
            ("sharedTargetPath", "C:/same-name.efx"), ("cacheBytesAvailable", "true"), ("availability", new JsonObject())
        })
        {
            var descriptor = Descriptor(); descriptor[invalid.Field] = invalid.Value;
            Preserved(Request("open_cached_resource", CacheRequest(descriptor)), "cache_source_invalid",
                "Malformed cache descriptor rejects " + invalid.Field + " without discarding edits");
        }
        var generated = Descriptor(); generated["actualPath"] = "Art/VFX/EffectEditor/MotionVfxLive/session/rev_3_1.efx";
        Preserved(Request("open_cached_resource", CacheRequest(generated)), "cache_reader_unavailable",
            "An actually observed generated revision still requires cache verification and never opens a local published file");
        var claims = Descriptor(); claims["currentPlayerVerified"] = true; claims["cacheBytesAvailable"] = true;
        Preserved(Request("open_cached_resource", CacheRequest(claims)), "cache_reader_unavailable",
            "Request flags cannot authorize cache loading or turn local files into verified memory bytes");
        var resolved = Request("resolve_resource", new() { ["resourcePath"] = Logical, ["sourceKind"] = "local_file" });
        check(resolved["ok"]!.GetValue<bool>() && resolved["sourceKind"]!.GetValue<string>() == "local_file" &&
            resolved["candidates"]!.AsArray().Count == 2 && resolved["candidates"]!.AsArray().All(c => c!["sourceKind"]!.GetValue<string>() == "local_file") &&
            resolved["document"]!.ToJsonString() == baselineDocument,
            "Explicit disk resolution labels every project and game-loose candidate as local_file and leaves edits intact");
        Preserved(Request("resolve_resource", new() { ["resourcePath"] = Logical, ["sourceKind"] = "game_cache" }), "local_resolver_only",
            "The disk resolver refuses to serve as a game-cache interface");
        Preserved(Request("resolve_resource", new() { ["resourcePath"] = Logical }), "local_resolver_only",
            "Legacy resource selections without explicit local_file scope cannot fall back to local materials");
        Request("redo");
        var redoFields = Request("inspect", new() { ["target"] = EntryTarget() })["fields"]!.AsArray();
        check(redoFields.Single(f => f!["path"]!.GetValue<string>() == "name")!["value"]!.GetValue<string>() == "cache_guard_second",
            "Redo still restores the actual edited content after rejected cache selections");
        Request("undo");
        var undoFields = Request("inspect", new() { ["target"] = EntryTarget() })["fields"]!.AsArray();
        check(undoFields.Single(f => f!["path"]!.GetValue<string>() == "name")!["value"]!.GetValue<string>() == "cache_guard_first" &&
            File.ReadAllBytes(exact).AsSpan().SequenceEqual(original) && File.ReadAllBytes(loose).AsSpan().SequenceEqual(original),
            "Undo content and both same-name local files remain unchanged by failed cache requests");

        CacheSourceDescriptor? observedDescriptor = null;
        var staleReader = new Reader((descriptor, epoch) =>
        {
            observedDescriptor = descriptor;
            return new(descriptor, epoch - 1, "process-validated", "test-reader", original);
        });
        using var injected = new EditorEngine(testOptions with { Mailbox = Path.Combine(root, "injected-mailbox") }, staleReader);
        JsonObject Inject(JsonObject request)
        {
            request["id"] = Guid.NewGuid().ToString("N"); request["action"] = "open_cached_resource";
            return injected.Handle(request);
        }
        var malformed = Descriptor(); malformed["actualHolderIdentity"] = "";
        var malformedResult = Inject(CacheRequest(malformed));
        check(!malformedResult["ok"]!.GetValue<bool>() && staleReader.Calls == 0,
            "Malformed source identities never reach the injected cache reader");
        var staleSnapshot = Inject(CacheRequest());
        check(!staleSnapshot["ok"]!.GetValue<bool>() && staleSnapshot["errorCode"]!.GetValue<string>() == "cache_snapshot_unverified" &&
            staleSnapshot["document"] == null && staleReader.Calls == 1,
            "A reader snapshot from a different live epoch is rejected even when both request epochs agree");
        var generatedSnapshot = Inject(CacheRequest((JsonObject)generated.DeepClone()));
        check(!generatedSnapshot["ok"]!.GetValue<bool>() && staleReader.Calls == 2 &&
            observedDescriptor!.ActualPath == "Art/VFX/EffectEditor/MotionVfxLive/session/rev_3_1.efx" && observedDescriptor.SharedTargetPath == Logical,
            "Observed generated revision paths reach the cache verifier unchanged and remain separate from shared canonical targets");

        // A constructor-injected test reader exercises the future byte-only
        // loading boundary. This is not a production native cache reader.
        var service = new EfxEditorService();
        var cacheFixture = service.Load(original, sample);
        cacheFixture.Entries[0].name = "cache_fixture_memory_not_local_file";
        byte[] cacheBytes = service.SaveToBytes(cacheFixture);
        cacheFixture.FileHandler.Dispose();
        var fixtureReader = new Reader((descriptor, epoch) => new(descriptor, epoch, "offline-test-process", "offline-test-reader", cacheBytes));
        using var cached = new EditorEngine(testOptions with { Mailbox = Path.Combine(root, "fixture-mailbox") }, fixtureReader);
        long cacheRevision = 0;
        JsonObject CachedRequest(string action, JsonObject? payload = null)
        {
            var request = payload ?? new JsonObject(); request["id"] = Guid.NewGuid().ToString("N");
            request["action"] = action; request["revision"] = cacheRevision;
            var response = cached.Handle(request);
            if (!response["ok"]!.GetValue<bool>()) throw new InvalidOperationException("Injected cache fixture: " + response.ToJsonString());
            cacheRevision = response["revision"]!.GetValue<long>();
            return response;
        }
        var fixtureDescriptor = Descriptor();
        var cachedOpen = CachedRequest("open_cached_resource", CacheRequest((JsonObject)fixtureDescriptor.DeepClone()));
        bool CacheProvenance(JsonObject response) => response["document"]!["sourceKind"]!.GetValue<string>() == "game_cache" &&
            JsonNode.DeepEquals(response["document"]!["cacheSource"], fixtureDescriptor) &&
            response["document"]!["source"]!.GetValue<string>().StartsWith("game-cache://", StringComparison.Ordinal);
        string CachedName() => CachedRequest("inspect", new() { ["target"] = EntryTarget() })["fields"]!.AsArray()
            .Single(field => field!["path"]!.GetValue<string>() == "name")!["value"]!.GetValue<string>();
        check(CacheProvenance(cachedOpen) && CachedName() == "cache_fixture_memory_not_local_file" && fixtureReader.Calls == 1,
            "Injected verified-byte fixture opens through game-cache virtual paths without using matching local file contents");
        var cacheEdited = CachedRequest("set", new() { ["target"] = EntryTarget(), ["path"] = "name", ["value"] = "cache_fixture_edited" });
        check(CacheProvenance(cacheEdited) && CachedName() == "cache_fixture_edited" && cacheEdited["document"]!["dirty"]!.GetValue<bool>(),
            "Editing a cache-byte fixture retains its game_cache kind and original holder descriptor");
        var cacheUndone = CachedRequest("undo");
        check(CacheProvenance(cacheUndone) && CachedName() == "cache_fixture_memory_not_local_file",
            "Undo restores the cache-byte fixture while retaining cache provenance");
        var cacheRedone = CachedRequest("redo");
        check(CacheProvenance(cacheRedone) && CachedName() == "cache_fixture_edited",
            "Redo restores a cache-byte edit while retaining cache provenance");
        var cacheSaved = CachedRequest("save");
        var cacheOutput = service.Load(cacheSaved["outputPath"]!.GetValue<string>());
        check(CacheProvenance(cacheSaved) && !cacheSaved["document"]!["dirty"]!.GetValue<bool>() &&
            cacheOutput.Entries[0].name == "cache_fixture_edited" && File.ReadAllBytes(exact).AsSpan().SequenceEqual(original),
            "Saving a game-cache virtual document writes a valid new EFX without changing its source identity or local originals");
        cacheOutput.FileHandler.Dispose();
        Host.AtomicJson(Path.Combine(output, "cache-source-guard.json"), new
        {
            localProjectFile = exact, gameLooseFile = loose, sourceDescriptor = Descriptor(), defaultReadError = unavailable["error"]!.GetValue<string>(),
            defaultReaderAvailable = false, sameNameFallback = false, existingDirtyDocumentPreserved = true,
            liveGameTested = false, injectedReaderIsTestFixtureOnly = true, injectedFixtureOpenEditUndoRedoSavePassed = true
        });
    }
}
