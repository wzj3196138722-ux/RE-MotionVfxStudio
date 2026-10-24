using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ReeLib.Efx;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class GroupEditingTests
{
    internal static void Run(Options options, string output, Action<bool, string> check)
    {
        string fixture = Path.Combine(Path.GetDirectoryName(options.Assets)!, "tools/LiveEditor/fixtures/game-install-it02-sub021.efx.5571972");
        check(File.Exists(fixture) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))) ==
            "E3CA412E965D72E1FE5B87D9D60C3927477D06AE44B896BCFE97C8DEF5213369", "Current installed-game 38-entry regression fixture retains its original PAK payload hash");
        string root = Path.Combine(output, "group-editing"); Directory.CreateDirectory(root);
        var service = new EfxEditorService();
        foreach (string path in new[] { fixture, Path.Combine(options.Assets, "VfxShared/natives/stm/art/vfx/effecteditor/weapon/it00/11_it00_900.efx.5571972") })
        {
            var file = service.Load(path);
            try
            {
                using var engine = new EditorEngine(options with { Mailbox = Path.Combine(root, Guid.NewGuid().ToString("N")), Output = root });
                var opened = engine.Handle(new JsonObject { ["id"] = "open", ["action"] = "open", ["path"] = path });
                var document = opened["document"]!;
                check(opened["ok"]!.GetValue<bool>() && document["groups"]!.AsArray().Select((group, index) =>
                    group!["index"]!.GetValue<int>() == index && group["name"]!.GetValue<string>() == file.EffectGroups[index].groupName &&
                    group["entries"]!.AsArray().Select(item => item!.GetValue<int>()).SequenceEqual(file.EffectGroups[index].efxEntryIndexes!)).All(value => value),
                    Path.GetFileName(path) + ": native groups retain original names, index order and exact member sequences");
                int[] covered = document["groups"]!.AsArray().SelectMany(group => group!["entries"]!.AsArray().Select(item => item!.GetValue<int>()))
                    .Concat(document["ungrouped"]!.AsArray().Select(item => item!.GetValue<int>())).Distinct().Order().ToArray();
                check(covered.SequenceEqual(Enumerable.Range(0, file.Entries.Count)), "Grouped and ungrouped rows cover every original entry");
                var saved = engine.Handle(new JsonObject { ["id"] = "save", ["action"] = "save", ["revision"] = opened["revision"]!.GetValue<long>() });
                var reopened = service.Load(saved["outputPath"]!.GetValue<string>());
                try
                {
                    check(reopened.EffectGroups.Count == file.EffectGroups.Count && reopened.EffectGroups.Select((group, index) =>
                        group.groupName == file.EffectGroups[index].groupName && group.efxEntryIndexes!.SequenceEqual(file.EffectGroups[index].efxEntryIndexes!)).All(value => value),
                        "Unmodified save preserves separate duplicate-name native groups and their original ordered membership");
                }
                finally { reopened.FileHandler.Dispose(); }
            }
            finally { file.FileHandler.Dispose(); }
        }

        var shape = service.Load(fixture);
        try
        {
            var nativeGroups = shape.EffectGroups.Select(group => group.efxEntryIndexes!.ToArray()).ToArray();
            int initialCount = shape.Entries.Count;
            check(initialCount == 38 && shape.EffectGroups.Count == 5 && shape.Actions.Count == 1, "Current installed it02 retains 38 entries, five groups and its PlayEfx action");
            service.DeepCloneEntry(shape, shape.Entries[7]);
            var cloned = service.SaveToBytes(shape); shape.FileHandler.Dispose(); shape = service.Load(cloned, fixture);
            check(shape.EffectGroups.Select((group, index) => group.efxEntryIndexes!.SequenceEqual(
                nativeGroups[index].Concat(Enumerable.Repeat(initialCount, nativeGroups[index].Count(member => member == 7))))).All(value => value),
                "Cloning preserves exact native groups even when multiple groups share a name");
            service.RemoveEntry(shape, initialCount);
            var removed = service.SaveToBytes(shape); shape.FileHandler.Dispose(); shape = service.Load(removed, fixture);
            check(shape.EffectGroups.Select((group, index) => group.efxEntryIndexes!.SequenceEqual(nativeGroups[index])).All(value => value),
                "Removing the clone restores all original same-name group member sequences");
            shape.EffectGroups.Clear(); foreach (var entry in shape.Entries) entry.Groups.Clear();
            void Add(string name, int[] members)
            {
                shape.EffectGroups.Add(new EffectGroup { groupName = name, efxEntryIndexes = members, valueCount = members.Length });
                foreach (int index in members) shape.Entries[index].Groups.Add(name);
            }
            Add("first", [2, 1, 2]); Add("shared", [2, 3]); Add("empty", []);
            string complex = Path.Combine(root, "complex.efx.5571972"); File.WriteAllBytes(complex, service.SaveToBytes(shape));
            using var test = new EditorEngine(options with { Mailbox = Path.Combine(root, "complex-mailbox"), Output = root });
            var opened = test.Handle(new() { ["id"] = "open", ["action"] = "open", ["path"] = complex });
            var annotated = test.Handle(new() { ["id"] = "note", ["action"] = "set_group_note", ["revision"] = opened["revision"]!.GetValue<long>(), ["groupIndex"] = 0, ["note"] = "共享和重复成员" });
            check(annotated["ok"]!.GetValue<bool>() && annotated["document"]!["groups"]![0]!["entries"]!.ToJsonString() == "[2,1,2]" &&
                annotated["document"]!["groups"]![1]!["entries"]!.ToJsonString() == "[2,3]" && annotated["document"]!["groups"]![2]!["entries"]!.ToJsonString() == "[]",
                "Shared, repeated and empty group memberships survive editor notes without changes");
            var deleted = test.Handle(new() { ["id"] = "remove-shared", ["action"] = "remove_entry", ["revision"] = annotated["revision"]!.GetValue<long>(), ["index"] = 2 });
            check(deleted["ok"]!.GetValue<bool>() && deleted["deletion"]!["removedGroupReferences"]!.GetValue<int>() == 3 &&
                deleted["document"]!["groups"]![0]!["entries"]!.ToJsonString() == "[1]" &&
                deleted["document"]!["groups"]![1]!["entries"]!.ToJsonString() == "[2]" &&
                deleted["document"]!["groups"]![2]!["entries"]!.ToJsonString() == "[]",
                "Deleting a repeated shared member removes all three references, shifts later members and keeps the empty group slot");
            shape.EffectGroups.Clear(); foreach (var entry in shape.Entries) entry.Groups.Clear();
            string flatPath = Path.Combine(root, "no-groups.efx.5571972"); File.WriteAllBytes(flatPath, service.SaveToBytes(shape));
            var flat = test.Handle(new() { ["id"] = "flat", ["action"] = "open", ["path"] = flatPath });
            check(flat["ok"]!.GetValue<bool>() && flat["document"]!["groups"]!.AsArray().Count == 0 &&
                flat["document"]!["ungrouped"]!.AsArray().Select(item => item!.GetValue<int>()).SequenceEqual(Enumerable.Range(0, shape.Entries.Count)),
                "Documents without groups retain every entry for the flat UI fallback");
        }
        finally { shape.FileHandler.Dispose(); }
        Host.AtomicJson(Path.Combine(output, "group-editing.json"), new { originalGroupsPreserved = true, duplicateNameGroupsPreserved = true, actualGroupsRenamed = false });
    }
}
