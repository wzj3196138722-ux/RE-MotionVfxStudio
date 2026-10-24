using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ReeLib;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class GameInstallSourceTests
{
    private const string Logical = "Art/VFX/EffectEditor/Weapon/it00/11_it00_900.efx";
    private const string Native = "natives/stm/" + Logical + ".5571972";

    internal static void Run(Options options, string output, Action<bool, string> check)
    {
        string root = Path.Combine(output, "game-install-source"), game = Path.Combine(root, "mock-game");
        string assets = Path.Combine(root, "project-materials"), config = Path.Combine(game, "re2_fw_config.txt");
        Directory.CreateDirectory(game);
        var service = new EfxEditorService();
        string sample = Path.Combine(options.Assets, "VfxShared", Native.ToLowerInvariant());
        byte[] original = File.ReadAllBytes(sample);
        byte[] Named(string name)
        {
            var file = service.Load(original, sample);
            try { file.Entries[0].name = name; return service.SaveToBytes(file); }
            finally { file.FileHandler.Dispose(); }
        }
        string Put(string path, byte[] content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, content); return path;
        }
        var archives = new List<string>();
        string Pak(string name, byte[] content, bool manifest = false, bool loaded = true)
        {
            string path = Path.Combine(game, name);
            var writer = new PakWriter { IncludeFileListManifest = manifest };
            writer.AddFile(Native, content); writer.SaveTo(path);
            if (loaded) archives.Add(name);
            return path;
        }
        void Log()
        {
            File.WriteAllLines(Path.Combine(game, "re2_framework_log.txt"), archives.SelectMany(name => new[]
            {
                "[IntegrityCheckBypass]: Pak name: " + name,
                "[IntegrityCheckBypass]: sha3_code_midhook called!",
                "[IntegrityCheckBypass]: Unencrypted pak detected, skipping decryption code!"
            }).Concat(new[] { "[IntegrityCheckBypass]: Pak name: re_chunk_000.pak.sub_000.pak.patch_099.pak" }));
        }
        void Config(bool loose, bool mods = false) => File.WriteAllText(config,
            "LooseFileLoader_Enabled=" + loose.ToString().ToLowerInvariant() + "\nIntegrityCheckBypass_LoadPakDirectory=" + mods.ToString().ToLowerInvariant());
        Config(true);
        Pak("re_chunk_000.pak", Named("pak_base"));
        Pak("re_chunk_000.pak.patch_001.pak", Named("pak_main_patch"));
        Pak("re_chunk_000.pak.sub_000.pak", Named("pak_sub_base"));
        byte[] modBytes = Named("pak_active_mod_patch");
        string winning = Pak("re_chunk_000.pak.sub_000.pak.patch_001.pak", modBytes, manifest: true);
        Pak("re_chunk_000.pak.sub_000.pak.patch_002.pak", Named("not_loaded_patch"), loaded: false);
        Log();
        string project = Put(Path.Combine(assets, Native), Named("stale_project_material"));
        string backup = Put(Path.Combine(game, "backup", Native), Named("unloaded_backup"));
        var testOptions = options with { GameRoot = game, Assets = assets, Mailbox = Path.Combine(root, "mailbox"), Output = Path.Combine(root, "exports") };
        using var engine = new EditorEngine(testOptions);
        long revision = 0;
        JsonObject Descriptor(string path = Logical) => new()
        {
            ["sourceKind"] = "actual_effect_player_holder", ["sourceId"] = "source-3-9", ["eventId"] = "event-3-9",
            ["sceneEpoch"] = 3, ["actualPath"] = path, ["sharedTargetPath"] = Logical,
            ["playerIdentity"] = "101262649712", ["actualHolderIdentity"] = "101262649888", ["localConfirmed"] = true,
            ["availability"] = "retained_observation", ["currentPlayerVerified"] = false, ["cacheBytesAvailable"] = false
        };
        JsonObject Selection(string path = Logical, long epoch = 3) => new()
        {
            ["sourceDescriptor"] = Descriptor(path), ["currentSceneEpoch"] = epoch,
            ["path"] = project, ["resolvedPath"] = project // These hints must be ignored.
        };
        JsonObject Request(string action, JsonObject? payload = null)
        {
            var request = payload ?? new(); request["action"] = action;
            request["id"] = Guid.NewGuid().ToString("N"); request["revision"] = revision;
            var response = engine.Handle(request); revision = response["revision"]!.GetValue<long>(); return response;
        }
        bool Ok(JsonObject response) => response["ok"]!.GetValue<bool>();
        JsonObject Target() => new() { ["kind"] = "entry", ["index"] = 0 };
        string Name() => Request("inspect", new() { ["target"] = Target() })["fields"]!.AsArray()
            .Single(f => f!["path"]!.GetValue<string>() == "name")!["value"]!.GetValue<string>();
        JsonObject SetName(string name) => Request("set", new() { ["target"] = Target(), ["path"] = "name", ["value"] = name });
        bool Provenance(JsonObject response, string kind, string origin) =>
            response["document"]!["sourceKind"]!.GetValue<string>() == "game_files" && response["document"]!["cacheSource"] == null &&
            response["document"]!["gameSource"]!["originKind"]!.GetValue<string>() == kind &&
            string.Equals(response["document"]!["gameSource"]!["originPath"]!.GetValue<string>(), Path.GetFullPath(origin), StringComparison.OrdinalIgnoreCase);

        var pakOpened = Request("open_game_resource", Selection());
        check(Ok(pakOpened) && Provenance(pakOpened, "pak_entry", winning) && Name() == "pak_active_mod_patch",
            "Game install loading prefers the latest observed sub patch and includes manifest-bearing mod PAKs");
        var source = pakOpened["document"]!["gameSource"]!;
        check(source["nativePath"]!.GetValue<string>() == Native && source["contentHash"]!.GetValue<string>() == Convert.ToHexString(SHA256.HashData(modBytes)) &&
            source["actualPath"]!.GetValue<string>() == Logical && source["sharedTargetPath"]!.GetValue<string>() == Logical &&
            source["sourceId"]!.GetValue<string>() == "source-3-9" && source["readAtUtc"] != null &&
            source["contentScope"]!.GetValue<string>() == "installed_game_files_not_memory_snapshot",
            "Game source records exact capture identity, native path, archive, byte hash and honest installed-file scope");
        check(PakUtils.ScanPakFiles(game, true).All(path => !path.Equals(winning.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)),
            "The selected test mod is one that the old default PAK scanner would exclude");
        var edited = SetName("edited_installed_resource");
        check(Ok(edited) && Provenance(edited, "pak_entry", winning) && edited["document"]!["dirty"]!.GetValue<bool>(),
            "Game-file documents remain editable and retain their installation provenance");
        Request("undo"); check(Name() == "pak_active_mod_patch", "Undo restores extracted game bytes");
        var redone = Request("redo"); check(Name() == "edited_installed_resource" && Provenance(redone, "pak_entry", winning), "Redo retains the game-files source");
        var saved = Request("save");
        var reopened = service.Load(saved["outputPath"]!.GetValue<string>());
        check(Ok(saved) && Provenance(saved, "pak_entry", winning) && reopened.Entries[0].name == "edited_installed_resource",
            "Extracted game documents save and reopen correctly through game-files virtual paths");
        reopened.FileHandler.Dispose();
        string checkpointPath = saved["outputPath"]!.GetValue<string>();
        JsonObject Checkpoint() => new()
        {
            ["checkpointPath"] = checkpointPath, ["sourceDescriptor"] = Descriptor(), ["currentSceneEpoch"] = 3,
            ["expectedContentHash"] = Convert.ToHexString(SHA256.HashData(modBytes))
        };
        var wrongCurrent = Request("restore_game_checkpoint", Checkpoint());
        check(!Ok(wrongCurrent) && wrongCurrent["errorCode"]!.GetValue<string>() == "checkpoint_source_mismatch",
            "Checkpoint restoration cannot attach game provenance to an arbitrary current document");
        var localCheckpoint = Request("open", new() { ["path"] = checkpointPath });
        string checkpointBaseline = localCheckpoint["document"]!.ToJsonString(); long checkpointRevision = revision;
        var wrongPath = Checkpoint(); wrongPath["checkpointPath"] = project;
        var wrongPathResult = Request("restore_game_checkpoint", wrongPath);
        check(!Ok(wrongPathResult) && revision == checkpointRevision && wrongPathResult["document"]!.ToJsonString() == checkpointBaseline,
            "Checkpoint provenance requires the exact explicitly opened checkpoint path");
        string changedLoose = Put(Path.Combine(game, Native), Named("changed_installation_after_checkpoint"));
        var changedOrigin = Request("restore_game_checkpoint", Checkpoint());
        check(!Ok(changedOrigin) && changedOrigin["errorCode"]!.GetValue<string>() == "checkpoint_origin_changed" &&
            revision == checkpointRevision && changedOrigin["document"]!.ToJsonString() == checkpointBaseline && Name() == "edited_installed_resource",
            "Changed installation bytes reject checkpoint rebinding while preserving its edited local document");
        File.Delete(changedLoose);
        var rebound = Request("restore_game_checkpoint", Checkpoint());
        check(Ok(rebound) && Provenance(rebound, "pak_entry", winning) && Name() == "edited_installed_resource" &&
            rebound["document"]!["gameSource"]!["contentHash"]!.GetValue<string>() == Convert.ToHexString(SHA256.HashData(modBytes)),
            "Verified checkpoint restoration retains edited bytes and reattaches the actual game-file source");
        var published = Request("publish");
        check(Ok(published) && Provenance(published, "pak_entry", winning) && File.Exists(published["outputPath"]!.GetValue<string>()),
            "Publishing an edited game-files document produces a separate replacement and preserves its original source");
        string generated = published["resourcePath"]!.GetValue<string>();
        var observedRevision = Request("open_game_resource", Selection(generated));
        check(Ok(observedRevision) && Provenance(observedRevision, "loose_file", published["outputPath"]!.GetValue<string>()) &&
            observedRevision["document"]!["gameSource"]!["actualPath"]!.GetValue<string>() == generated &&
            observedRevision["document"]!["gameSource"]!["sharedTargetPath"]!.GetValue<string>() == Logical && Name() == "edited_installed_resource",
            "A character-observed generated revision opens its exact installed bytes while retaining the original shared target");

        string loose = Put(Path.Combine(game, Native), Named("enabled_loose_replacement"));
        var looseOpened = Request("open_game_resource", Selection());
        check(Ok(looseOpened) && Provenance(looseOpened, "loose_file", loose) && Name() == "enabled_loose_replacement",
            "An enabled exact-path game replacement takes precedence over installed PAKs");
        Config(false);
        var disabled = Request("open_game_resource", Selection());
        check(Ok(disabled) && Provenance(disabled, "pak_entry", winning) && Name() == "edited_installed_resource",
            "Returning to the observed PAK restores that version's retained session edits instead of loose-file content");

        SetName("preserve_dirty_first"); SetName("preserve_dirty_second"); Request("undo");
        string baseline = Request("status")["document"]!.ToJsonString(); long baselineRevision = revision;
        void Preserved(JsonObject response, string code, string label) => check(!Ok(response) &&
            response["errorCode"]?.GetValue<string>() == code && revision == baselineRevision && response["document"]!.ToJsonString() == baseline, label);
        Preserved(Request("open_game_resource", Selection(epoch: 4)), "cache_source_expired",
            "Stale captured scene selection cannot replace an existing dirty game-file document");
        var unconfirmed = Selection(); unconfirmed["sourceDescriptor"]!["localConfirmed"] = false;
        Preserved(Request("open_game_resource", unconfirmed), "cache_source_invalid", "Unconfirmed character ownership cannot authorize game-file selection");
        var malformed = Selection(); malformed["sourceDescriptor"]!["actualPath"] = "../escape.efx";
        Preserved(Request("open_game_resource", malformed), "cache_source_invalid", "Malformed captured paths are rejected before file lookup");
        Preserved(Request("open_game_resource", Selection(Logical + ".123")), "game_resource_version_unsupported", "A captured EFX version never silently becomes a different version");
        string missing = "Art/VFX/EffectEditor/Other/11_it00_900.efx";
        Put(Path.Combine(assets, "natives/stm/" + missing + ".5571972"), Named("exact_project_only"));
        Put(Path.Combine(game, "backup/natives/stm/" + missing + ".5571972"), Named("exact_backup_only"));
        Preserved(Request("open_game_resource", Selection(missing)), "game_resource_not_found",
            "Missing exact game resources never fall back to project Assets, backup folders or matching basenames");
        File.WriteAllText(config, "IntegrityCheckBypass_LoadPakDirectory=false\n");
        Preserved(Request("open_game_resource", Selection()), "game_loose_loader_unknown",
            "An exact loose file with unknown loader state cannot silently replace a game source");
        Config(false, true); Put(Path.Combine(game, "pak_mods", "custom.pak"), [1, 2, 3]);
        Preserved(Request("open_game_resource", Selection()), "game_pak_priority_unverified",
            "Unverified custom PAK injection order fails explicitly without discarding edits");
        Config(false, false);
        File.WriteAllText(Path.Combine(game, "re2_framework_log.txt"), "[IntegrityCheckBypass]: Pak name: " + Path.GetFileName(winning));
        Preserved(Request("open_game_resource", Selection()), "game_pak_load_evidence_missing",
            "An attempted PAK name alone is insufficient evidence of an opened game archive");
        Log();
        Put(winning, new byte[32]);
        Preserved(Request("open_game_resource", Selection()), "game_pak_read_failed",
            "An unreadable highest-priority package fails instead of silently loading an older asset");
        Request("redo"); check(Name() == "preserve_dirty_second", "Rejected game-source reads preserve actual redo content");
        Request("undo"); check(Name() == "preserve_dirty_first", "Rejected game-source reads preserve actual undo content");
        var local = Request("open", new() { ["path"] = project });
        check(Ok(local) && local["document"]!["sourceKind"]!.GetValue<string>() == "local_file" && local["document"]!["gameSource"] == null,
            "Opening an explicit local file clears game-source attribution");
        Host.AtomicJson(Path.Combine(output, "game-install-source.json"), new
        {
            productionReader = "GameInstallResourceReader", sourceKind = "game_files", fixtureOnly = true,
            pakSelection = source.DeepClone(), gameRoot = game, localMaterialsIgnored = project, backupIgnored = backup,
            sharedTargetPreserved = true, noMemorySnapshotClaim = true, dirtyHistoryPreservedOnFailure = true,
            testedOpenEditUndoRedoSavePublish = true
        });
    }
}
