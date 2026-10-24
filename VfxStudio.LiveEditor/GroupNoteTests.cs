using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ReeLib;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class GroupNoteTests
{
    internal static void Run(Options options, string output, Action<bool, string> check)
    {
        string root = Path.Combine(output, "group-notes"), game = Path.Combine(root, "mock-game");
        const string logical = "Art/VFX/EffectEditor/GroupNotes/original.efx";
        string fixture = Path.Combine(Path.GetDirectoryName(options.Assets)!, "tools/LiveEditor/fixtures/game-install-it02-sub021.efx.5571972");
        byte[] original = File.ReadAllBytes(fixture);
        string loose = Path.Combine(game, "natives/stm/" + logical + ".5571972");
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!); File.WriteAllBytes(loose, original);
        File.WriteAllText(Path.Combine(game, "re2_fw_config.txt"), "LooseFileLoader_Enabled=true\n");
        var settings = options with { Mailbox = Path.Combine(root, "mailbox"), Output = Path.Combine(root, "saved"), GameRoot = game };
        using var client = new Client(settings);
        var opened = client.OpenGame(logical);
        string nativeName = opened["document"]!["groups"]![0]!["name"]!.GetValue<string>();
        check(nativeName == opened["document"]!["groups"]![1]!["name"]!.GetValue<string>(), "Note fixture contains independently indexed native groups sharing the same name");
        var inspected = client.Request("inspect", new() { ["target"] = Target("group", 0) });
        var fields = inspected["fields"]!.AsArray();
        check(fields.Count == 1 && fields[0]!["path"]!.GetValue<string>() == "note" &&
            fields[0]!["label"]!.GetValue<string>() == "分组备注" && fields[0]!["editable"]!.GetValue<bool>(),
            "Group inspector exposes only the independent editor note field");
        string cleanState = State(opened), cleanBytes = client.PublishedHash();
        var first = client.Note(0, "中文备注：刀刃火焰 🔥");
        check(State(first) == cleanState && client.PublishedHash() == cleanBytes &&
            first["noteSaved"]!.GetValue<bool>() && first["noteStorage"]!.GetValue<string>() == "editor_sidecar",
            "A note changes no clean-document bytes, revision, dirty flag or undo/redo state");
        check(Note(first, 0) == "中文备注：刀刃火焰 🔥" && Note(first, 1) == "" &&
            first["document"]!["groups"]![0]!["name"]!.GetValue<string>() == nativeName &&
            first["document"]!["groups"]![0]!["displayName"]!.GetValue<string>() == Note(first, 0),
            "Chinese and supplementary Unicode notes change only the selected group's display name");
        client.Note(1, "中文备注：刀刃火焰 🔥");
        check(Note(client.Status(), 0) == Note(client.Status(), 1), "Duplicate Chinese notes are permitted on different same-name native groups");
        var legacy = client.Request("rename_group", new() { ["groupIndex"] = 1, ["name"] = "旧接口也只保存备注" });
        check(legacy["document"]!["groups"]![1]!["name"]!.GetValue<string>() == nativeName && State(legacy) == cleanState && client.PublishedHash() == cleanBytes,
            "Legacy rename_group is a note-only alias and never writes a native name or hash");
        client.Request("set", new() { ["target"] = Target("group", 0), ["path"] = "note", ["value"] = "常规字段提交备注" });
        var reinspection = client.Request("inspect", new() { ["target"] = Target("group", 0) });
        check(reinspection["fields"]![0]!["value"]!.GetValue<string>() == "常规字段提交备注", "Group note field submission and inspection share the same saved value");
        string savedPath = client.Request("save")["outputPath"]!.GetValue<string>();
        check(Hash(File.ReadAllBytes(savedPath)) == cleanBytes && Hash(File.ReadAllBytes(loose)) == Hash(original),
            "Saving and publishing EFX output never includes note bytes or changes original game source bytes");

        client.Request("set", new() { ["target"] = Target("entry", 0), ["path"] = "name", ["value"] = "块名第一次修改" });
        client.Request("set", new() { ["target"] = Target("entry", 0), ["path"] = "name", ["value"] = "块名第二次修改" });
        client.Request("undo");
        var history = client.Status(); string dirtyState = State(history), dirtyBytes = client.PublishedHash();
        check(history["document"]!["canUndo"]!.GetValue<bool>() && history["document"]!["canRedo"]!.GetValue<bool>() && history["document"]!["dirty"]!.GetValue<bool>(),
            "Note regression has real dirty bytes and both undo and redo stacks to protect");
        var dirtyNote = client.Note(0, "保留编辑历史的备注");
        check(State(dirtyNote) == dirtyState && client.PublishedHash() == dirtyBytes, "Saving a note preserves dirty EFX bytes and both existing history stacks");
        var redo = client.Request("redo");
        check(redo["document"]!["entries"]![0]!["name"]!.GetValue<string>() == "块名第二次修改" && Note(redo, 0) == "保留编辑历史的备注",
            "EFX redo still restores the original next edit and does not undo independently saved notes");
        var undo = client.Request("undo");
        check(undo["document"]!["entries"]![0]!["name"]!.GetValue<string>() == "块名第一次修改" && Note(undo, 0) == "保留编辑历史的备注",
            "EFX undo still restores the original previous edit and leaves notes saved");

        string beforeReject = State(client.Status()), beforeRejectBytes = client.PublishedHash();
        void Rejected(JsonObject result, string message) => check(!result["ok"]!.GetValue<bool>() && State(result) == beforeReject && client.PublishedHash() == beforeRejectBytes, message);
        Rejected(client.Request("set", new() { ["target"] = Target("group", 0), ["path"] = "name", ["value"] = "不允许改原生名" }, false), "The former set group/name route explicitly rejects native renaming");
        Rejected(client.Request("set", new() { ["target"] = Target("document", 0), ["path"] = "EffectGroups[0].groupName", ["value"] = "不能旁路改名" }, false),
            "A document-level groupName setter cannot bypass note-only editing");
        Rejected(client.Request("set", new() { ["target"] = Target("entry", 0), ["path"] = "Groups[0]", ["value"] = "不能改派生归属" }, false),
            "An entry's derived group membership cannot bypass note-only editing");
        foreach (string invalid in new[] { "line\nbreak", "tab\tvalue", new string('中', 1366) })
            Rejected(client.Request("set_group_note", new() { ["groupIndex"] = 0, ["note"] = invalid }, false), "Invalid or oversized note is rejected without changing document or history");
        Rejected(client.Request("set_group_note", new() { ["groupIndex"] = 0, ["note"] = "stale", ["revision"] = client.Revision - 1 }, false), "Stale revisions cannot attach a note to a newer selected document");
        Rejected(client.Request("set_group_note", new() { ["groupIndex"] = -1, ["note"] = "invalid index" }, false), "Invalid group indices cannot create orphaned metadata");

        string sidecar = Path.Combine(settings.Mailbox, "group-notes.json");
        byte[] beforeDisk = File.ReadAllBytes(sidecar); string beforeNote = Note(client.Status(), 0);
        using (var locked = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Rejected(client.Request("set_group_note", new() { ["groupIndex"] = 0, ["note"] = "写入失败必须保留旧备注" }, false), "Failed atomic replacement preserves EFX state");
            check(Note(client.Status(), 0) == beforeNote, "Failed atomic replacement leaves the prior in-memory note intact");
        }
        check(File.ReadAllBytes(sidecar).AsSpan().SequenceEqual(beforeDisk), "Failed replacement leaves the previous sidecar bytes intact");
        check(client.OpenGame(logical)["document"]!["groups"]![0]!["note"]!.GetValue<string>() == beforeNote, "Reopening the logical resource preserves saved notes");
        using (var restarted = new Client(settings))
        {
            check(Note(restarted.OpenGame(logical), 0) == beforeNote && Note(restarted.Status(), 1) == "旧接口也只保存备注", "A new backend instance restores both distinct notes from the sidecar");
        }

        // Changes to editable block names, ordinary parameters and attributes do
        // not participate in note identity. Restore through the same installation
        // provenance just like the upgrade checkpoint route.
        client.Request("set", new() { ["target"] = Target("entry", 0), ["path"] = "name", ["value"] = "重开后仍保留备注" });
        var scalarOwner = client.Status()["document"]!["entries"]!.AsArray().Skip(1).First(entry =>
            entry!["attributes"]!.AsArray().Any(attribute => attribute!["type"]!.GetValue<string>() == "RgbCommon"))!;
        var scalarAttribute = scalarOwner["attributes"]!.AsArray().First(attribute => attribute!["type"]!.GetValue<string>() == "RgbCommon")!;
        JsonObject ScalarTarget() => new() { ["kind"] = "entry", ["index"] = scalarOwner["index"]!.GetValue<int>(), ["attributeIndex"] = scalarAttribute["index"]!.GetValue<int>() };
        client.Request("set", new() { ["target"] = ScalarTarget(), ["path"] = "GreenChIntensity", ["value"] = "7.5" });
        var attributes = client.Status()["document"]!["entries"]![0]!["attributes"]!.AsArray();
        int lastAttribute = attributes.Count - 1;
        string removedType = attributes[lastAttribute]!["type"]!.GetValue<string>();
        client.Request("remove_attribute", new() { ["target"] = new JsonObject { ["kind"] = "entry", ["index"] = 0, ["attributeIndex"] = lastAttribute } });
        var checkpoint = client.Request("save");
        string checkpointPath = checkpoint["outputPath"]!.GetValue<string>();
        var provenance = checkpoint["document"]!["gameSource"]!.DeepClone();
        string sourceHash = provenance["contentHash"]!.GetValue<string>();
        check(Note(client.Request("open", new() { ["path"] = checkpointPath }), 0) == "", "An explicit local checkpoint does not impersonate the logical game's note source");
        var restored = client.Request("restore_game_checkpoint", new() { ["checkpointPath"] = checkpointPath, ["sourceDescriptor"] = provenance, ["currentSceneEpoch"] = 8, ["expectedContentHash"] = sourceHash });
        check(Note(restored, 0) == beforeNote && restored["document"]!["entries"]![0]!["name"]!.GetValue<string>() == "重开后仍保留备注" &&
            restored["document"]!["entries"]![0]!["attributes"]!.AsArray().Count == lastAttribute,
            "Restoring verified game provenance refreshes note identity while preserving changed block names and removed attributes");
        var scalarReadback = client.Request("inspect", new() { ["target"] = ScalarTarget() });
        check(Note(scalarReadback, 0) == beforeNote && scalarReadback["fields"]!.AsArray().Single(field => field!["path"]!.GetValue<string>() == "GreenChIntensity")!["value"]!.GetValue<string>() == "7.5",
            "Ordinary numeric parameter changes survive checkpoint reopening without invalidating the group's note");
        client.Request("add_attribute", new() { ["target"] = Target("entry", 0), ["type"] = removedType });
        var addedCheckpoint = client.Request("save"); string addedPath = addedCheckpoint["outputPath"]!.GetValue<string>();
        client.Request("open", new() { ["path"] = addedPath });
        var addedRestored = client.Request("restore_game_checkpoint", new() { ["checkpointPath"] = addedPath,
            ["sourceDescriptor"] = provenance.DeepClone(), ["currentSceneEpoch"] = 8, ["expectedContentHash"] = sourceHash });
        check(Note(addedRestored, 0) == beforeNote && addedRestored["document"]!["entries"]![0]!["attributes"]!.AsArray().Count == lastAttribute + 1,
            "Adding an attribute and reopening retains notes independently of editable attribute count and types");
        var replacement = client.Request("publish");
        string generated = replacement["resourcePath"]!.GetValue<string>();
        check(Note(client.OpenGame(generated, logical), 0) == beforeNote, "Captured generated revisions use the stable complete shared target path for note identity");
        string otherLogical = "Art/VFX/EffectEditor/OtherNotes/original.efx";
        string otherLoose = Path.Combine(game, "natives/stm/" + otherLogical + ".5571972");
        Directory.CreateDirectory(Path.GetDirectoryName(otherLoose)!); File.WriteAllBytes(otherLoose, original);
        check(Note(client.OpenGame(otherLogical), 0) == "", "Same-basename game resources at different full logical paths do not share notes");
        client.Note(0, "另一路径独立备注");
        check(Note(client.OpenGame(logical), 0) == beforeNote, "Switching back restores only that logical resource's own note");
        client.Request("open", new() { ["path"] = fixture }); client.Note(0, "本地绝对路径备注");
        check(Note(client.Request("open", new() { ["path"] = checkpointPath }), 0) == "", "Different absolute local file paths do not share notes");
        check(Note(client.Request("open", new() { ["path"] = fixture }), 0) == "本地绝对路径备注", "Full local file path notes persist independently of game-source notes");
        client.OpenGame(logical);
        var cleared = client.Note(0, "");
        check(Note(cleared, 0) == "" && cleared["document"]!["groups"]![0]!["displayName"]!.GetValue<string>() == nativeName && Note(cleared, 1) != "",
            "Empty text clears only the selected note and restores its original display name");
        using (var restarted = new Client(settings)) check(Note(restarted.OpenGame(logical), 0) == "", "Note removal persists across backend restart");

        // Corrupt sidecars are never treated as an empty database and overwritten.
        string corruptMailbox = Path.Combine(root, "corrupt-mailbox"); Directory.CreateDirectory(corruptMailbox);
        string corruptPath = Path.Combine(corruptMailbox, "group-notes.json"); File.WriteAllText(corruptPath, "{broken");
        using (var corrupt = new Client(settings with { Mailbox = corruptMailbox }))
        {
            var status = corrupt.OpenGame(logical);
            var failed = corrupt.Request("set_group_note", new() { ["groupIndex"] = 0, ["note"] = "不能覆盖坏文件" }, false);
            check(status["document"]!["groupNotesWarning"] != null && !failed["ok"]!.GetValue<bool>() && File.ReadAllText(corruptPath) == "{broken",
                "Unreadable metadata shows an explicit warning and cannot be overwritten by a later note submission");
        }

        var service = new EfxEditorService(); var model = service.Load(fixture);
        try
        {
            var blocked = ContentInspector.Describe(new Embedded { Content = model }, model.Header.Version)
                .Where(field => field.Description.Path.Contains("EffectGroups[") && field.Description.Path.EndsWith(".groupName")).ToArray();
            check(blocked.Length == model.EffectGroups.Count && blocked.All(field => !field.Description.Editable && field.Set == null),
                "Nested EFX group names use owner-based read-only protection, including embedded emitter content");
            var store = new GroupNoteStore(Path.Combine(root, "identity-mailbox"));
            var identity = GroupNoteStore.Identify(model, 0);
            store.Set("game:identity.efx", identity, "身份备注");
            var reversed = identity with { Members = identity.Members.Reverse().ToArray() };
            var replaced = identity with { Members = [999991, 999992] };
            check(store.Get("game:identity.efx", reversed, out var reorderedWarning) == "" && reorderedWarning != null &&
                store.Get("game:identity.efx", replaced, out var replacedWarning) == "" && replacedWarning != null,
                "Unrelated or reordered member identities do not silently inherit a saved note");
            check(store.Get("game:identity.efx", identity with { Members = identity.Members.Append(999999).ToArray() }, out _) == "身份备注",
                "Adding a new member preserves the original group's note identity");
            var repeated = identity with { Members = [1, 2, 1] };
            store.Set("game:repeated.efx", repeated, "重复成员备注");
            check(store.Get("game:repeated.efx", repeated with { Members = [1, 1, 2] }, out var repeatWarning) == "" && repeatWarning != null,
                "Reordering repeated member occurrences does not inherit the old group's note");
        }
        finally { model.FileHandler.Dispose(); }
        Host.AtomicJson(Path.Combine(output, "group-notes.json"), new { ok = true, nativeEfxUnchanged = true, independentPersistence = true,
            chineseNotes = true, sourceAndGroupIsolation = true, writeFailurePreservesMemoryAndDisk = true, sidecar });
    }

    private sealed class Embedded { public EfxFile Content = null!; }
    private static JsonObject Target(string kind, int index) => new() { ["kind"] = kind, ["index"] = index };
    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));
    private static string Note(JsonObject result, int index) => result["document"]!["groups"]![index]!["note"]!.GetValue<string>();
    private static string State(JsonObject result)
    {
        var doc = result["document"]!;
        return string.Join("|", result["revision"], doc["dirty"], doc["canUndo"], doc["canRedo"], doc["source"], doc["sourceKind"], doc["gameSource"]);
    }
    private sealed class Client(Options options) : IDisposable
    {
        private readonly EditorEngine engine = new(options);
        internal long Revision { get; private set; }
        internal JsonObject Request(string action, JsonObject? data = null, bool expectedSuccess = true)
        {
            data ??= new(); data["id"] = Guid.NewGuid().ToString("N"); data["action"] = action;
            if (!data.ContainsKey("revision")) data["revision"] = Revision;
            var result = engine.Handle(data); Revision = result["revision"]!.GetValue<long>();
            if (result["ok"]!.GetValue<bool>() != expectedSuccess) throw new InvalidOperationException(action + ": " + result.ToJsonString());
            return result;
        }
        internal JsonObject OpenGame(string actual, string? shared = null) => Request("open_game_resource", new()
        {
            ["currentSceneEpoch"] = 8, ["sourceDescriptor"] = new JsonObject
            {
                ["sourceKind"] = "actual_effect_player_holder", ["sourceId"] = "notes-test-8-1", ["eventId"] = "notes-test-event",
                ["sceneEpoch"] = 8, ["actualPath"] = actual, ["sharedTargetPath"] = shared ?? actual, ["localConfirmed"] = true,
                ["playerIdentity"] = "101262649712", ["actualHolderIdentity"] = "101262649888",
                ["availability"] = "retained_observation", ["currentPlayerVerified"] = false, ["cacheBytesAvailable"] = false
            }
        });
        internal JsonObject Note(int index, string note) => Request("set_group_note", new() { ["groupIndex"] = index, ["note"] = note });
        internal JsonObject Status() => Request("status");
        internal string PublishedHash() => Hash(File.ReadAllBytes(Request("publish")["outputPath"]!.GetValue<string>()));
        public void Dispose() => engine.Dispose();
    }
}
