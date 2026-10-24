using System.Text.Json.Nodes;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class DeletionTests
{
    public static void Run(Options options, string output, Action<bool, string> check)
    {
        string fixture = Path.Combine(Path.GetDirectoryName(options.Assets)!, "tools/LiveEditor/fixtures/game-install-it02-sub021.efx.5571972");
        var service = new EfxEditorService();
        string root = Path.Combine(output, "deletion"); Directory.CreateDirectory(root);
        var original = service.Load(fixture);
        byte[] canonical = service.SaveToBytes(original);
        try
        {
            foreach (int entry in new[] { 0, 7, original.Entries.Count - 1 })
            {
                using var engine = new EditorEngine(options with { Mailbox = Path.Combine(root, "mailbox"), Output = root });
                long revision = 0; int seq = 0;
                JsonObject Request(string action, JsonObject? body = null)
                {
                    body ??= new(); body["id"] = "delete-" + ++seq; body["action"] = action; body["revision"] = revision;
                    var r = engine.Handle(body); revision = r["revision"]!.GetValue<long>();
                    check(r["ok"]!.GetValue<bool>(), $"Delete test {entry} {action}: {r["error"]}"); return r;
                }
                Request("open", new() { ["path"] = fixture });
                var removed = Request("remove_entry", new() { ["index"] = entry });
                check(removed["deletion"]!["bytesAfter"]!.GetValue<int>() < canonical.Length && removed["deletion"]!["trailingBytes"]!.GetValue<int>() == 0,
                    "Entry deletion physically shrinks its serialized file");
                string savedPath = Request("save")["outputPath"]!.GetValue<string>();
                var actual = service.Load(savedPath);
                try
                {
                    check(actual.Entries.Select(e => e.index).SequenceEqual(original.Entries.Where((_, i) => i != entry).Select(e => e.index)), "Surviving native entry IDs are unchanged");
                    check(actual.EffectGroups.Select((g, i) => g.groupName == original.EffectGroups[i].groupName && g.efxEntryIndexes!.SequenceEqual(
                        original.EffectGroups[i].efxEntryIndexes!.Where(v => v != entry).Select(v => v > entry ? v - 1 : v))).All(v => v),
                        "Every group drops deleted members and shifts surviving indices without renumbering groups");
                    string overwrite = Path.Combine(root, "overwrite.efx.5571972"); File.WriteAllBytes(overwrite, canonical);
                    service.Save(actual, overwrite);
                    check(File.ReadAllBytes(overwrite).AsSpan().SequenceEqual(File.ReadAllBytes(savedPath)), "Writing a shorter EFX over a longer file leaves no old tail");
                }
                finally { actual.FileHandler.Dispose(); }
                Request("undo");
                check(File.ReadAllBytes(Request("save")["outputPath"]!.GetValue<string>()).AsSpan().SequenceEqual(canonical), "Entry undo restores the entire binary exactly");
                foreach (int attr in new[] { 0, original.Entries[entry].Attributes.Count / 2, original.Entries[entry].Attributes.Count - 1 }.Distinct())
                {
                    if (attr < 0) continue;
                    var deleted = Request("remove_attribute", new() { ["target"] = new JsonObject { ["kind"] = "entry", ["index"] = entry, ["attributeIndex"] = attr } });
                    check(deleted["deletion"]!["bytesAfter"]!.GetValue<int>() < canonical.Length, "Attribute header and payload are removed from the byte stream");
                    Request("undo");
                    check(File.ReadAllBytes(Request("save")["outputPath"]!.GetValue<string>()).AsSpan().SequenceEqual(canonical), "Attribute undo restores payload, links and group bytes exactly");
                }
            }
        }
        finally { original.FileHandler.Dispose(); }
    }
}
