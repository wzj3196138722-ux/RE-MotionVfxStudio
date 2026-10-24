using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

// Invoked by the isolated performance harness through reflection, never through a live mailbox.
internal static class BackendPerformanceTests
{
    internal static string Benchmark(string project, string output, string phase)
    {
        project = Path.GetFullPath(project); output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var records = new List<object>();
        var assets = Path.Combine(project, "Assets");
        string[] fixtures = [Path.Combine(assets, "VfxShared/natives/stm/art/vfx/effecteditor/weapon/it03/11_it03_005.efx.5571972"),
            Path.Combine(assets, "VfxShared/natives/stm/art/vfx/effecteditor/common/guide/11_guide_200.efx.5571972")];
        var service = new EfxEditorService();
        foreach (var fixture in fixtures)
        {
            using var engine = new EditorEngine(new(assets, Path.Combine(output, "mailbox-" + phase), Path.Combine(output, "saved-" + phase), null, true));
            int sequence = 0;
            JsonObject Request(string action, JsonObject? target = null)
            {
                var request = new JsonObject { ["id"] = phase + "-" + ++sequence, ["action"] = action };
                if (target != null) request["target"] = target.DeepClone();
                var result = engine.Handle(request);
                if (result["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(result["error"]?.GetValue<string>());
                return result;
            }
            var opened = engine.Handle(new() { ["id"] = "open", ["action"] = "open", ["path"] = fixture });
            if (opened["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(opened["error"]?.GetValue<string>());
            var parsed = service.Load(fixture);
            try
            {
                var emitter = parsed.Actions.SelectMany(a => a.Attributes).OfType<EFXAttributePlayEmitter>().First();
                var owner = emitter.efxrData!.Entries.First();
                var attribute = owner.Attributes.First(a => a.type == EfxAttributeType.Life);
                int attributeIndex = owner.Attributes.IndexOf(attribute);
                var parentTarget = new JsonObject { ["kind"] = "action", ["index"] = 0, ["attributeIndex"] = 0 };
                var nestedTarget = new JsonObject { ["kind"] = "entry", ["index"] = 0, ["attributeIndex"] = attributeIndex,
                    ["embeddedPath"] = new JsonArray(new JsonObject { ["kind"] = "action", ["index"] = 0, ["attributeIndex"] = 0 }) };
                var docMethod = typeof(EditorEngine).GetMethod("DocumentInfo", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var rows = new List<object>();
                rows.Add(Measure("DocumentInfo", 60, () => docMethod.Invoke(engine, null)));
                rows.Add(Measure("status_handle", 80, () => Request("status")));
                rows.Add(Measure("status_handle_and_json", 80, () => JsonSerializer.SerializeToUtf8Bytes(Request("status"), Host.Json)));
                rows.Add(Measure("inspect_nested_life", 50, () => Request("inspect", nestedTarget)));
                rows.Add(Measure("inspect_parent_PlayEmitter", 12, () => Request("inspect", parentTarget)));
                var clear = typeof(EditorEngine).GetMethod("ClearInspectionSnapshots", BindingFlags.Instance | BindingFlags.NonPublic);
                rows.Add(Measure("inspect_parent_uncached", 12, () => { clear?.Invoke(engine, null); return Request("inspect", parentTarget); }));
                rows.Add(Measure("ContentInspector_Describe_parent", 12, () => ContentInspector.Describe(emitter, parsed.Header.Version)));
                rows.Add(Measure("ContentInspector_Curves_parent", 12, () => ContentInspector.Curves(emitter, parsed.Header.Version)));
                rows.Add(Measure("ColorEditor_Inspect_parent", 12, () => ColorEditor.Inspect(emitter, parsed.Header.Version)));
                records.Add(new { fixture, fileBytes = new FileInfo(fixture).Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))),
                    rootEntries = parsed.Entries.Count, actions = parsed.Actions.Count,
                    statusResponseBytes = JsonSerializer.SerializeToUtf8Bytes(Request("status"), Host.Json).Length,
                    nestedInspectResponseBytes = JsonSerializer.SerializeToUtf8Bytes(Request("inspect", nestedTarget), Host.Json).Length,
                    parentInspectResponseBytes = JsonSerializer.SerializeToUtf8Bytes(Request("inspect", parentTarget), Host.Json).Length, measurements = rows });
            }
            finally { parsed.FileHandler.Dispose(); }
        }
        string path = Path.Combine(output, "backend-performance-" + phase + ".json");
        Host.AtomicJson(path, new { phase, at = DateTimeOffset.UtcNow, runtime = Environment.Version.ToString(),
            protocol = "Unchanged full responses. Unique request IDs; no request-cache hits. Warm steady-state snapshots.", records });
        return path;
    }

    internal static string Regression(string project, string output)
    {
        project=Path.GetFullPath(project);output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var passed=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);passed.Add(name);}
        string runDirectory=Path.Combine(output,"regression-"+Guid.NewGuid().ToString("N"));
        var options=new Options(Path.Combine(project,"Assets"),Path.Combine(runDirectory,"mailbox"),Path.Combine(runDirectory,"saved"),null,true);
        using var engine=new EditorEngine(options);
        string fixture=Path.Combine(options.Assets,"VfxShared/natives/stm/art/vfx/effecteditor/weapon/it03/11_it03_005.efx.5571972");
        int sequence=0;long revision=0;
        JsonObject Request(string action,JsonObject? body=null,bool success=true)
        {
            body??=new();body["id"]="snapshot-"+ ++sequence;body["action"]=action;body["revision"]=revision;
            var result=engine.Handle(body);Check(result["ok"]!.GetValue<bool>()==success,$"{action} request {sequence}: {result["error"]}");
            revision=result["revision"]?.GetValue<long>()??revision;return result;
        }
        JsonObject Target(int attribute=6,int action=0)=>new(){["kind"]="entry",["index"]=1,["attributeIndex"]=attribute,
            ["embeddedPath"]=new JsonArray(new JsonObject{["kind"]="action",["index"]=action,["attributeIndex"]=0})};
        JsonObject Inspect(JsonObject target)=>Request("inspect",new(){["target"]=target});
        string Value(JsonObject result,string path)=>result["fields"]!.AsArray().Single(f=>f!["path"]!.GetValue<string>()==path)!["value"]!.GetValue<string>();
        var modelField=typeof(EditorEngine).GetField("document",BindingFlags.Instance|BindingFlags.NonPublic)!;
        void CheckFresh(JsonObject target,JsonObject actual,string context)
        {
            var file=(ReeLib.EfxFile)modelField.GetValue(engine)!;
            var hop=target["embeddedPath"]![0]!;
            var child=((EFXAttributePlayEmitter)file.Actions[hop["index"]!.GetValue<int>()].Attributes[0]).efxrData!;
            object selected=child.Entries[target["index"]!.GetValue<int>()].Attributes[target["attributeIndex"]!.GetValue<int>()];
            var expected=JsonSerializer.SerializeToNode(new{fields=ContentInspector.Describe(selected,child.Header.Version).Select(f=>f.Description),
                curves=ContentInspector.Curves(selected,child.Header.Version).Select(CurveEditor.Inspect),expressionCurves=ExpressionInspector.Inspect(selected),
                colorGroups=ColorEditor.Inspect(selected,child.Header.Version)},Host.Json)!;
            foreach(string key in new[]{"fields","curves","expressionCurves","colorGroups"})
                Check(JsonNode.DeepEquals(expected[key],actual[key]),context+": "+key+" equals a fresh uncached inspection");
        }
        try
        {
            var opened=Request("open",new(){["path"]=fixture});
            var first=Inspect(Target());var cached=Inspect(Target());CheckFresh(Target(),cached,"initial cache hit");
            string initial=Value(first,"KeepFrame.r");
            first["fields"]![0]!["value"]="tampered";first["document"]!["entries"]![0]!["name"]="tampered";
            CheckFresh(Target(),Inspect(Target()),"response mutation isolation");
            Check(Request("status")["document"]!["entries"]![0]!["name"]!.GetValue<string>()!="tampered","A response cannot modify the cached document tree");
            var other=Inspect(Target(6,1));CheckFresh(Target(6,1),other,"same index in a different embedded Action");
            CheckFresh(Target(13),Inspect(Target(13)),"nested RGBA grouping");
            CheckFresh(Target(2),Inspect(Target(2)),"nested expression curves");
            Request("set",new(){["target"]=Target(),["path"]="KeepFrame.r",["value"]="37"});
            var modified=Inspect(Target());Check(Value(modified,"KeepFrame.r")=="37","Successful edits invalidate cached field values");CheckFresh(Target(),modified,"after set");
            Check(modified["document"]!["dirty"]!.GetValue<bool>() && modified["document"]!["canUndo"]!.GetValue<bool>(),"Document snapshot refreshes dirty and undo state");
            Request("undo");Check(Value(Inspect(Target()),"KeepFrame.r")==initial,"Undo invalidates inspection values");
            Request("redo");Check(Value(Inspect(Target()),"KeepFrame.r")=="37","Redo invalidates inspection values");
            long unchanged=revision;
            var saved=Request("save");Check(revision==unchanged && !saved["document"]!["dirty"]!.GetValue<bool>(),"Save refreshes dirty metadata even when revision is unchanged");
            var groupDoc=Request("status")["document"]!;
            if(groupDoc["groups"]!.AsArray().Count>0)
            {
                Request("set_group_note",new(){["groupIndex"]=0,["note"]="性能测试备注"});
                Check(revision==unchanged && Request("status")["document"]!["groups"]![0]!["displayName"]!.GetValue<string>()=="性能测试备注","Group notes invalidate document snapshot without changing EFX revision");
            }
            Request("remove_attribute",new(){["target"]=Target(12)});
            var shifted=Inspect(Target(12));Check(shifted["colorGroups"]!.AsArray().Count>0,"Deleting an attribute cannot reuse stale same-index fields");CheckFresh(Target(12),shifted,"after nested deletion");
            Request("inspect",new(){["target"]=Target(13)},false);
            Request("undo");CheckFresh(Target(13),Inspect(Target(13)),"after deletion undo");
            Request("set",new(){["target"]=Target(),["path"]="KeepFrame.r",["value"]="not an integer"},false);
            CheckFresh(Target(),Inspect(Target()),"rejected edit");
            var parentTarget=new JsonObject{["kind"]="action",["index"]=0,["attributeIndex"]=0};
            // A derived payload length is rejected. It must not poison subsequent reads.
            Request("set",new(){["target"]=parentTarget,["path"]="efxrSize",["value"]="1"},false);
            CheckFresh(Target(),Inspect(Target()),"derived metadata rejection");
            // Exercise a real rollback after the model has changed and serialized,
            // not merely an invalid value rejected before Mutate begins.
            var oldModel=(ReeLib.EfxFile)modelField.GetValue(engine)!;
            var selected=((EFXAttributePlayEmitter)oldModel.Actions[0].Attributes[0]).efxrData!.Entries[1].Attributes[6];
            var life=ContentInspector.Describe(selected,oldModel.Header.Version).Single(f=>f.Description.Path=="KeepFrame.r");
            long rollbackRevision=revision;
            var mutate=typeof(EditorEngine).GetMethod("Mutate",BindingFlags.Instance|BindingFlags.NonPublic)!;
            bool rolledBack=false;
            try
            {
                mutate.Invoke(engine,new object[]{(Action)(()=>life.Set!(ContentInspector.Parse("93",life.ValueType))),
                    (Action<ReeLib.EfxFile>)(_=>throw new InvalidDataException("Intentional verification failure"))});
            }
            catch(TargetInvocationException ex) when(ex.InnerException is InvalidDataException){rolledBack=true;}
            Check(rolledBack && !ReferenceEquals(oldModel,modelField.GetValue(engine)),"Verification failure restores a fresh model");
            var recovered=Inspect(Target());
            Check(revision==rollbackRevision && Value(recovered,"KeepFrame.r")=="37","Rollback invalidates snapshots without increasing revision");
            CheckFresh(Target(),recovered,"post-serialization rollback");
            string second=Path.Combine(options.Assets,"VfxShared/natives/stm/art/vfx/effecteditor/common/guide/11_guide_200.efx.5571972");
            Request("open",new(){["path"]=second});
            Check(Request("status")["document"]!["name"]!.GetValue<string>()=="11_guide_200.efx.5571972","Source switching invalidates the cached document tree");
            Request("open",new(){["path"]=fixture});
            Check(Value(Inspect(Target()),"KeepFrame.r")=="37","Returning to a retained document loads its working values instead of another file's cache");
            CheckFresh(Target(),Inspect(Target()),"retained source reopen");
            // Use public API to populate more than the bounded cache capacity with real fields.
            var document=Request("status")["document"]!;
            foreach(var action in document["actions"]!.AsArray())
                foreach(var entry in action!["attributes"]![0]!["embedded"]!["entries"]!.AsArray())
                    foreach(var attribute in entry!["attributes"]!.AsArray()) Inspect(attribute!["target"]!.DeepClone().AsObject());
            var cache=(System.Collections.IDictionary)typeof(EditorEngine).GetField("inspectionSnapshots",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(engine)!;
            int used=(int)typeof(EditorEngine).GetField("inspectionSnapshotBytes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(engine)!;
            Check(cache.Count<=32 && used<=8*1024*1024,"Inspection cache is bounded by entry count and serialized bytes");
            CheckFresh(Target(),Inspect(Target()),"after cache eviction");
            // Run the existing binary/undo/path protections, not a reduced performance-only substitute.
            EmbeddedActionTests.Run(options,Path.Combine(runDirectory,"embedded-regression"),Check);
            ColorEditorTests.Run(options,Path.Combine(runDirectory,"color-regression"),Check);
            GroupNoteTests.Run(options,Path.Combine(runDirectory,"note-regression"),Check);
            Host.AtomicJson(Path.Combine(output,"backend-performance-regression.json"),new{ok=true,passed=passed.Count,tests=passed,liveGameTested=false});
            return $"PASS: {passed.Count} backend snapshot and existing editor regression checks.";
        }
        catch(Exception ex)
        {
            Host.AtomicJson(Path.Combine(output,"backend-performance-regression.json"),new{ok=false,passed=passed.Count,tests=passed,error=ex.ToString()});throw;
        }
    }

    internal static string Transport(string project,string output)
    {
        project=Path.GetFullPath(project);output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var rows=new List<object>();
        foreach(string relative in new[]{"weapon/it03/11_it03_005.efx.5571972","common/guide/11_guide_200.efx.5571972"})
        {
            string fixture=Path.Combine(project,"Assets/VfxShared/natives/stm/art/vfx/effecteditor",relative);
            using var engine=new EditorEngine(new(Path.Combine(project,"Assets"),Path.Combine(output,"transport-mailbox"),Path.Combine(output,"transport-saved"),null,true));
            int sequence=0;
            var opened=engine.Handle(new(){["id"]="transport-open",["action"]="open",["path"]=fixture});
            if(opened["ok"]?.GetValue<bool>()!=true)throw new InvalidOperationException(opened["error"]?.GetValue<string>());
            rows.Add(new{fixture,measurement=Measure("inspect_parent_cached_and_json",20,()=>
            {
                var response=engine.Handle(new(){["id"]="transport-"+ ++sequence,["action"]="inspect",
                    ["target"]=new JsonObject{["kind"]="action",["index"]=0,["attributeIndex"]=0}});
                if(response["ok"]?.GetValue<bool>()!=true)throw new InvalidOperationException(response["error"]?.GetValue<string>());
                return JsonSerializer.SerializeToUtf8Bytes(response,Host.Json);
            })});
        }
        string path=Path.Combine(output,"backend-performance-transport-after.json");
        Host.AtomicJson(path,new{at=DateTimeOffset.UtcNow,measurements=rows,includes="Full UTF-8 JSON serialization, no disk or Lua parse; additional post-change measurement, no before comparison"});
        return path;
    }

    private static object Measure(string name, int iterations, Func<object?> operation)
    {
        for (int i = 0; i < 3; i++) GC.KeepAlive(operation());
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var timings = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            var value = operation();
            timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            GC.KeepAlive(value);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Array.Sort(timings);
        return new { name, iterations, meanMs = timings.Average(), medianMs = timings[iterations / 2],
            p95Ms = timings[Math.Min(iterations - 1, (int)Math.Ceiling(iterations * .95) - 1)], allocatedBytesPerCall = allocated / iterations };
    }
}
