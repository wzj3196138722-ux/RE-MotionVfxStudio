using System.Text.Json.Nodes;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class ClipboardTests
{
    internal static int RunCommand(Options options)
    {
        var tests = new List<string>();
        Directory.CreateDirectory(options.Output);
        try
        {
            Run(options, options.Output, (ok, name) => { if (!ok) throw new InvalidOperationException(name); tests.Add(name); });
            Host.AtomicJson(Path.Combine(options.Output, "clipboard-results.json"), new { ok = true, passed = tests.Count, tests, liveGameTested = false });
            Console.WriteLine($"PASS: {tests.Count} clipboard checks."); return 0;
        }
        catch (Exception ex)
        {
            Host.AtomicJson(Path.Combine(options.Output, "clipboard-results.json"), new { ok = false, passed = tests.Count, tests, error = ex.ToString() });
            Console.Error.WriteLine(ex); return 1;
        }
    }

    internal static void Run(Options options, string output, Action<bool, string> check)
    {
        string root = Path.Combine(output, "clipboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var service = new EfxEditorService();
        EfxFile NewFile() { var f = new EfxFile(new FileHandler(new MemoryStream(), "memory.efx.5571972")); f.Header.Version = EfxVersion.MHWilds; f.Header.dimensionType = 1; return f; }
        EFXEntry Entry(string name, int id, int life)
        {
            var e = new EFXEntry { Version = EfxVersion.MHWilds, name = name, index = id };
            e.Attributes.Add(new EFXAttributeLife { Version = EfxVersion.MHWilds, UniqueID = id + 10, KeepFrame = new() { r = life, s = life } });
            return e;
        }
        string Write(string name, EfxFile file)
        { string path = Path.Combine(root, name + ".efx.5571972"); File.WriteAllBytes(path, service.SaveToBytes(file)); file.FileHandler.Dispose(); return path; }
        var a = NewFile(); a.Entries.Add(Entry("源块", 3, 37)); a.Entries.Add(Entry("目标块", 7, 8));
        // Duplicate group names deliberately test native indexed membership.
        a.EffectGroups.Add(new() { groupName = "same", efxEntryIndexes = [1], valueCount = 1 });
        a.EffectGroups.Add(new() { groupName = "same", efxEntryIndexes = [0], valueCount = 1 });
        a.Entries[0].Groups.Add("same"); a.Entries[1].Groups.Add("same");
        var embedded = NewFile(); embedded.Entries.Add(Entry("内嵌块", 2, 91));
        var action = new EFXAction { Version = EfxVersion.MHWilds, name = "嵌套 Action" };
        action.Attributes.Add(new EFXAttributePlayEmitter { Version = EfxVersion.MHWilds, UniqueID = 44, efxrData = embedded }); a.Actions.Add(action);
        string pathA = Write("source", a); var original = File.ReadAllBytes(pathA);
        var b = NewFile(); b.Entries.Add(new() { Version = EfxVersion.MHWilds, name = "空块", index = 30 });
        string pathB = Write("destination", b);
        using var engine = new EditorEngine(options with { Mailbox = Path.Combine(root, "mailbox"), Output = Path.Combine(root, "saved"), GameRoot = null, SelfTest = true });
        long revision = 0; int sequence = 0;
        JsonObject Request(string op, JsonObject? body = null, bool ok = true)
        {
            body ??= new(); body["id"] = "clipboard-" + ++sequence; body["action"] = op; body["revision"] = revision;
            var result = engine.Handle(body);
            check(result["ok"]!.GetValue<bool>() == ok, $"Clipboard request {sequence}: {op}: {result["error"]}");
            revision = result["revision"]?.GetValue<long>() ?? revision; return result;
        }
        JsonObject Target(string kind = "entry", int index = 0, int? attribute = null, bool nested = false) => new()
        {
            ["kind"] = kind, ["index"] = index, ["attributeIndex"] = attribute,
            ["embeddedPath"] = nested ? new JsonArray(new JsonObject { ["kind"] = "action", ["index"] = 0, ["attributeIndex"] = 0 }) : null
        };
        JsonObject Copy(JsonObject target) => Request("copy_efx", new() { ["target"] = target })["copyBuffer"]!.DeepClone().AsObject();
        JsonObject Paste(JsonObject buffer, JsonObject target, bool? replace = null, int? index = null, bool ok = true) =>
            Request("paste_efx", new() { ["clipboardId"] = buffer["id"]!.DeepClone(), ["target"] = target, ["replace"] = replace, ["replaceAttributeIndex"] = index }, ok);
        byte[] Save() => File.ReadAllBytes(Request("save")["outputPath"]!.GetValue<string>());
        EfxFile Read() => service.Load(Save());
        Request("open", new() { ["path"] = pathA });
        byte[] initial = Save(); long beforeCopy = revision;
        var entryCopy = Copy(Target());
        check(revision == beforeCopy && initial.SequenceEqual(Save()), "Copy does not mutate revision or bytes");
        Request("set", new() { ["target"] = Target(attribute: 0), ["path"] = "KeepFrame.r", ["value"] = "99" });
        Paste(entryCopy, Target("group", 0));
        var reread = Read();
        check(reread.Entries.Count == 3 && ((EFXAttributeLife)reread.Entries[2].Attributes[0]).KeepFrame.r == 37, "Copied snapshot survives later edits to source");
        check(reread.EffectGroups[0].efxEntryIndexes!.SequenceEqual(new[] { 1, 2 }) && reread.EffectGroups[1].efxEntryIndexes!.SequenceEqual(new[] { 0 }), "Selected duplicate-name Group receives the block without changing another Group");
        check(reread.Entries.Select(e => e.index).Distinct().Count() == 3 && reread.Entries.SelectMany(e => e.Attributes).Select(x => x.UniqueID).Distinct().Count() == 3, "Pasted entry and attributes get unique native IDs");
        reread.FileHandler.Dispose();
        byte[] pasted = Save(); Request("undo"); reread = Read(); check(reread.Entries.Count == 2, "Undo removes pasted entry"); reread.FileHandler.Dispose(); Request("redo"); check(pasted.SequenceEqual(Save()), "Redo restores exact serialized paste");
        Request("undo"); Request("undo"); check(initial.SequenceEqual(Save()), "Undo restores original source bytes and group order");
        var attributeCopy = Copy(Target(attribute: 0));
        Paste(attributeCopy, Target(index: 1), ok: false);
        check(initial.SequenceEqual(Save()), "Same-type attribute is never replaced without confirmation");
        Paste(attributeCopy, Target(index: 1), true, 9, false);
        var replaced = Paste(attributeCopy, Target(index: 1), true, 0);
        reread = Read();
        check(((EFXAttributeLife)reread.Entries[1].Attributes[0]).KeepFrame.r == 37 && reread.Entries[1].Attributes[0].UniqueID == 17 && replaced["selection"]!["attributeIndex"]!.GetValue<int>() == 0, "Confirmed replacement preserves target ID and selects updated attribute");
        reread.FileHandler.Dispose(); Request("undo"); check(initial.SequenceEqual(Save()), "Replacing an attribute can be undone byte for byte");
        Request("open", new() { ["path"] = pathB });
        check(Request("status")["copyBuffer"]!["id"]!.GetValue<string>() == attributeCopy["id"]!.GetValue<string>(), "Switching files retains the copied snapshot");
        Paste(attributeCopy, Target()); reread = Read();
        check(((EFXAttributeLife)reread.Entries[0].Attributes[0]).KeepFrame.r == 37, "Cross-file attribute paste preserves values"); reread.FileHandler.Dispose();
        Paste(entryCopy, Target(), ok: false); // Replaced clipboard ID must not target stale content.
        Request("open", new() { ["path"] = pathA });
        var nestedCopy = Copy(Target(nested: true));
        Paste(nestedCopy, Target(nested: true)); reread = Read();
        check(((EFXAttributePlayEmitter)reread.Actions[0].Attributes[0]).efxrData!.Entries.Count == 2 && reread.Entries.Count == 2, "Nested block paste cannot accidentally append to root entries"); reread.FileHandler.Dispose(); Request("undo");
        var emitterCopy = Copy(Target("action", 0, 0));
        Paste(emitterCopy, Target("action"), true, 0); Request("undo"); check(initial.SequenceEqual(Save()), "Copying and replacing an entire PlayEmitter preserves nested data and supports undo");
        Request("copy_efx", new() { ["target"] = Target("action") }, false);
        check(Request("status")["copyBuffer"]!["id"]!.GetValue<string>() == emitterCopy["id"]!.GetValue<string>(), "Invalid copy never clears an existing valid snapshot");
        Request("clear_efx_clipboard"); Paste(emitterCopy, Target(), ok: false);
        check(File.ReadAllBytes(pathA).SequenceEqual(original), "Clipboard never overwrites the user's source file");
        // Optional local game fixtures are never required by the public build.
        foreach (var relative in new[] { "weapon/it00/11_it00_900.efx.5571972", "weapon/it03/11_it03_005.efx.5571972" })
        {
            string fixture = Path.Combine(options.Assets, "VfxShared/natives/stm/art/vfx/effecteditor", relative);
            if (!File.Exists(fixture)) continue;
            var input = File.ReadAllBytes(fixture);
            var opened = Request("open", new() { ["path"] = fixture });
            var tree = opened["document"]!; byte[] canonical = Save();
            foreach (var node in tree["entries"]!.AsArray())
            {
                var buffer = Copy(Target(index: node!["index"]!.GetValue<int>()));
                Paste(buffer, Target("document")); Request("undo");
                check(Save().SequenceEqual(canonical), "Real block copy/undo retains all bytes: " + node["name"]);
            }
            foreach (var actionNode in tree["actions"]!.AsArray())
                foreach (var attribute in actionNode!["attributes"]!.AsArray())
                {
                    int ai = actionNode["index"]!.GetValue<int>(), attr = attribute!["index"]!.GetValue<int>();
                    var buffer = Copy(Target("action", ai, attr));
                    Paste(buffer, Target("action", ai), true, attr); Request("undo");
                    check(Save().SequenceEqual(canonical), "Real Action attribute copy/undo retains nested payload: " + attribute["type"]);
                }
            check(File.ReadAllBytes(fixture).SequenceEqual(input), "Real fixture remains untouched: " + relative);
        }
    }
}
