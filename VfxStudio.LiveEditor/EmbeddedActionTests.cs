using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class EmbeddedActionTests
{
    internal static int RunCommand(Options options)
    {
        var passed = new List<string>();
        Directory.CreateDirectory(options.Output);
        try
        {
            Run(options, options.Output, (value, name) =>
            {
                if (!value) throw new InvalidOperationException(name);
                passed.Add(name);
            });
            Host.AtomicJson(Path.Combine(options.Output, "embedded-action-results.json"), new { ok = true, passed = passed.Count, tests = passed, liveGameTested = false });
            Console.WriteLine($"PASS: {passed.Count} embedded Action checks against real Wilds files."); return 0;
        }
        catch (Exception ex)
        {
            Host.AtomicJson(Path.Combine(options.Output, "embedded-action-results.json"), new { ok = false, passed = passed.Count, tests = passed, error = ex.ToString() });
            Console.Error.WriteLine(ex); return 1;
        }
    }

    internal static void Run(Options options, string output, Action<bool, string> check)
    {
        string fixture = Path.Combine(options.Assets, "VfxShared/natives/stm/art/vfx/effecteditor/weapon/it03/11_it03_005.efx.5571972");
        var source = File.ReadAllBytes(fixture);
        string hash = Convert.ToHexString(SHA256.HashData(source));
        check(hash == "ADEAFEFB680644EBFD0443D16BC858DFC8DAB4ABC4FF0C6F576918675EAA277A", "Fixture matches the user's captured game resource hash");
        var service = new EfxEditorService();
        var original = service.Load(source, fixture);
        var testOptions = options with { Mailbox = Path.Combine(output, "embedded-mailbox"), Output = Path.Combine(output, "embedded-saved"), GameRoot = null, SelfTest = true };
        using var engine = new EditorEngine(testOptions);
        int sequence = 0; long revision = 0;
        JsonObject Request(string action, JsonObject? body = null, bool success = true)
        {
            body ??= new(); body["id"] = "embedded-" + ++sequence; body["action"] = action; body["revision"] = revision;
            var reply = engine.Handle(body);
            check(reply["ok"]!.GetValue<bool>() == success, $"Embedded request {sequence}: {action} ({reply["error"]})");
            revision = reply["revision"]?.GetValue<long>() ?? revision;
            return reply;
        }
        JsonObject Hop(int action = 0, int attribute = 0) => new() { ["kind"] = "action", ["index"] = action, ["attributeIndex"] = attribute };
        JsonObject Target(int entry, int? attribute = null, int action = 0) => new()
        {
            ["kind"] = "entry", ["index"] = entry, ["attributeIndex"] = attribute,
            ["embeddedPath"] = new JsonArray(Hop(action))
        };
        JsonObject Root(int entry = 0, int? attribute = null) => new() { ["kind"] = "entry", ["index"] = entry, ["attributeIndex"] = attribute };
        JsonObject Inspect(JsonObject target) => Request("inspect", new() { ["target"] = target });
        string Value(JsonObject target, string path) => Inspect(target)["fields"]!.AsArray().Single(f => f!["path"]!.GetValue<string>() == path)!["value"]!.GetValue<string>();
        byte[] Saved() => File.ReadAllBytes(Request("save")["outputPath"]!.GetValue<string>());
        string UntouchedFingerprint(EfxFile file) => string.Join("|", file.Entries.Cast<EFXEntryBase>().Concat(file.Actions.Skip(1))
            .SelectMany(e => e.Attributes).SelectMany(a => ContentInspector.Describe(a, file.Header.Version))
            .Select(f => f.Description.Path + "=" + f.Description.Value));
        try
        {
            var opened = Request("open", new() { ["path"] = fixture });
            var doc = opened["document"]!;
            Host.AtomicJson(Path.Combine(output, "embedded-document-real-it03-005.json"), doc);
            check(doc["actions"]!.AsArray().Count == 3, "Real weapon EFX exposes all three Actions");
            var embedded = doc["actions"]![0]!["attributes"]![0]!["embedded"]!;
            check(embedded["entries"]!.AsArray().Count == 2 && embedded["entries"]![0]!["name"]!.GetValue<string>() == "234_PLG_trail" &&
                embedded["entries"]![1]!["name"]!.GetValue<string>() == "234_P", "PlayEmitter exposes both real child entries by array position");
            check(embedded["entries"]!.AsArray().All(e => e!["attributes"]!.AsArray().Count == 14), "Every one of the 28 child attributes is represented in the document tree");
            check(JsonNode.DeepEquals(embedded["entries"]![1]!["attributes"]![5]!["target"], Target(1, 5)), "Tree supplies complete nested attribute targets without reusing root IDs");
            check(JsonNode.DeepEquals(embedded["entries"]![1]!["target"], Target(1)), "Tree supplies complete nested owner targets");
            check(doc["entries"]![0]!["target"]!["embeddedPath"]!.AsArray().Count == 0, "Root targets remain compatible with empty paths");
            var parentInspection = Inspect(new() { ["kind"] = "action", ["index"] = 0, ["attributeIndex"] = 0 });
            check(!parentInspection["fields"]!.AsArray().Single(f => f!["path"]!.GetValue<string>() == "efxrSize")!["editable"]!.GetValue<bool>(), "PlayEmitter payload length is derived and read-only");

            var ribbon = Target(1, 5); var life = Target(1, 6); var rgb = Target(1, 13);
            check(Value(ribbon.DeepClone().AsObject(), "SizeScalar.s").Length > 0 && Value(life.DeepClone().AsObject(), "KeepFrame.r").Length > 0,
                "Nested RibbonFollow size and Life fields can be inspected independently");
            check(Inspect(rgb.DeepClone().AsObject())["colorGroups"]!.AsArray().Any(c => c!["path"]!.GetValue<string>() == "RedChColor"), "Nested RgbCommon exposes its specialized color editor");
            check(Inspect(Target(1, 2))["expressionCurves"]!.AsArray().Count > 0, "Nested Transform3DExpression exposes its specialized expression curves");
            byte[] canonical = Saved();
            string untouched = UntouchedFingerprint(original);
            Request("set_color", new() { ["target"] = rgb.DeepClone(), ["path"] = "RedChColor", ["values"] = new JsonArray(12, 34, 56, 255) });
            Request("set", new() { ["target"] = ribbon.DeepClone(), ["path"] = "SizeScalar.s", ["value"] = "0.375" });
            Request("set_fields", new() { ["target"] = life.DeepClone(), ["fields"] = new JsonArray(
                new JsonObject { ["path"] = "KeepFrame.r", ["value"] = "17" }, new JsonObject { ["path"] = "KeepFrame.s", ["value"] = "17" }) });
            byte[] edited = Saved();
            var reread = service.Load(edited, fixture);
            try
            {
                var child = ((EFXAttributePlayEmitter)reread.Actions[0].Attributes[0]).efxrData!;
                check(ContentInspector.Describe(child.Entries[1].Attributes[5], child.Header.Version).Single(f => f.Description.Path == "SizeScalar.s").Description.Value == "0.375", "Nested size survives complete binary save/reopen");
                check(ContentInspector.Describe(child.Entries[1].Attributes[6], child.Header.Version).Single(f => f.Description.Path == "KeepFrame.r").Description.Value == "17", "Nested life survives complete binary save/reopen");
                check(ColorEditor.Inspect(child.Entries[1].Attributes[13], child.Header.Version).Single(c => c.Path == "RedChColor").Hex == "#0C2238FF", "Nested RGBA survives complete binary save/reopen");
                check(UntouchedFingerprint(reread) == untouched, "Nested edits preserve root entries and the other two Actions");
                check(edited.AsSpan().SequenceEqual(service.SaveToBytes(reread)), "Nested edits yield a stable full-file binary round trip");
            }
            finally { reread.FileHandler.Dispose(); }
            for (int i = 0; i < 3; i++) Request("undo");
            check(Saved().AsSpan().SequenceEqual(canonical), "Three undos restore every original byte");
            for (int i = 0; i < 3; i++) Request("redo");
            check(Saved().AsSpan().SequenceEqual(edited), "Three redos restore every edited byte");

            long stableRevision = revision;
            var invalidTargets = new List<JsonObject>();
            var malformed = Target(0, 0); malformed["embeddedPath"] = "bad"; invalidTargets.Add(malformed);
            foreach (var badHop in new JsonObject[] {
                new(){["kind"]="entry",["index"]=0,["attributeIndex"]=0}, // native root attr is not PlayEmitter
                Hop(99), Hop(0,99), new(){["kind"]="action",["index"]=-1,["attributeIndex"]=0},
                new(){["kind"]="group",["index"]=0,["attributeIndex"]=0}, new(){["kind"]="action",["index"]=0.5,["attributeIndex"]=0} })
            { var bad = Target(0,0); bad["embeddedPath"] = new JsonArray(badHop); invalidTargets.Add(bad); }
            var nullHop=Target(0,0);nullHop["embeddedPath"]=new JsonArray((JsonNode?)null);invalidTargets.Add(nullHop);
            var missingHop=Target(0,0);missingHop["embeddedPath"]=new JsonArray(new JsonObject{["kind"]="action",["index"]=0});invalidTargets.Add(missingHop);
            var longPath=Target(0,0);longPath["embeddedPath"]=new JsonArray(Enumerable.Range(0,17).Select(_=>(JsonNode?)Hop()).ToArray());invalidTargets.Add(longPath);
            invalidTargets.Add(Target(99,0));invalidTargets.Add(Target(0,99));
            var groupTarget=Target(0);groupTarget["kind"]="group";invalidTargets.Add(groupTarget);
            foreach(var bad in invalidTargets) Request("inspect",new(){["target"]=bad},false);
            foreach(string action in new[]{"duplicate_entry","remove_entry","restore_entry","add_entry","set_group_note"})
            {
                Request(action,new(){["index"]=0,["target"]=Target(0),["groupIndex"]=0,["note"]="unsafe"},false);
                Request(action,new(){["index"]=0,["embeddedPath"]=new JsonArray(Hop()),["groupIndex"]=0,["note"]="unsafe"},false);
            }
            check(revision==stableRevision && Saved().AsSpan().SequenceEqual(edited),"Malformed paths and root-only operations cannot change a same-index root entry");

            Request("remove_attribute",new(){["target"]=Target(1,12)}); // real child FadeByDepth
            var shortened=Saved();var without=service.Load(shortened,fixture);
            try
            {
                var child=((EFXAttributePlayEmitter)without.Actions[0].Attributes[0]).efxrData!;
                check(child.Entries[1].Attributes.Count==13 && child.Entries[1].Attributes.All(a=>a.type!=EfxAttributeType.FadeByDepth),"Nested attribute deletion removes its real payload and descriptor");
                check(shortened.Length<edited.Length && without.FileHandler.Tell()==without.FileHandler.FileSize(),"Nested deletion compacts the containing PlayEmitter and complete file");
                check(UntouchedFingerprint(without)==untouched,"Nested attribute deletion preserves unrelated entries and Actions");
            }
            finally{without.FileHandler.Dispose();}
            Request("undo");check(Saved().AsSpan().SequenceEqual(edited),"Nested attribute deletion undo restores all bytes");
            Request("add_attribute",new(){["target"]=Target(1),["type"]="FadeByAngle"});
            var added=service.Load(Saved(),fixture);
            try
            {
                check(((EFXAttributePlayEmitter)added.Actions[0].Attributes[0]).efxrData!.Entries[1].Attributes.Any(a=>a.type==EfxAttributeType.FadeByAngle),"Nested add_attribute uses the embedded owner");
                check(UntouchedFingerprint(added)==untouched,"Nested add_attribute cannot alter the root owner with the same position");
            }
            finally{added.FileHandler.Dispose();}
            Request("undo");check(Saved().AsSpan().SequenceEqual(edited),"Nested attribute insertion undo restores all bytes");
            Request("remove_attribute",new(){["target"]=new JsonObject{["kind"]="action",["index"]=0,["attributeIndex"]=0}});
            check(Request("status")["document"]!["actions"]![0]!["attributes"]!.AsArray().Count==0,"Deleting a top-level PlayEmitter intentionally removes its subtree without rejecting other groups");
            Request("undo");check(Saved().AsSpan().SequenceEqual(edited),"Deleting a whole PlayEmitter remains reversible");
            string rootName=Value(Root(),"name");Request("set",new(){["target"]=Root(),["path"]="name",["value"]="root_compatibility"});
            check(Value(Root(),"name")=="root_compatibility","Legacy three-field root targets still edit root entries");
            Request("undo");check(Value(Root(),"name")==rootName && Saved().AsSpan().SequenceEqual(edited),"Root undo leaves nested edits intact");

            // Bounded traversal rejects cycles before serializer recursion. Use a parsed real
            // document in memory and restore the reference immediately; no malformed file is written.
            var emitter=(EFXAttributePlayEmitter)original.Actions[0].Attributes[0];var realChild=emitter.efxrData;
            try
            {
                emitter.efxrData=original;
                bool rejected=false;
                try{typeof(EditorEngine).GetMethod("DescribeEmbeddedTree",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[original]);}
                catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidDataException && ex.InnerException.Message.Contains("cyclic",StringComparison.OrdinalIgnoreCase);}
                check(rejected,"Cyclic embedded graphs fail explicitly instead of hiding children or recursing forever");
            }
            finally{emitter.efxrData=realChild;}
            check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture)))==hash,"The real source fixture was never overwritten");
            Host.AtomicJson(Path.Combine(output,"embedded-action-evidence.json"),new{fixture,hash,actions=3,childEntryCounts=new[]{2,2,2},attributesPerChild=14,editedSha256=Convert.ToHexString(SHA256.HashData(edited)),liveGameTested=false});
        }
        finally{original.FileHandler.Dispose();}
    }
}
