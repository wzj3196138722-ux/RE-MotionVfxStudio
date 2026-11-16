// Structural edits the preview window sends (through PreviewEditorSession, without the window):
// add_attribute with RE4 default fields, add_entry from a Type template, duplicate, copy/paste and remove
// entries, and the preview target following its entry while positions shift.
// Usage: dotnet run -c Release --project tools/Re4StructureTests
using System.Text.Json.Nodes;
using VfxStudio.Re4Render;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
string exe = Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
string game = Path.GetDirectoryName(exe)!;
string repo = FindRepository();
int checks = 0, failures = 0;
void Check(bool ok, string name, object? detail = null)
{
    checks++;
    Console.WriteLine((ok ? "ok   " : "FAIL ") + name + (detail == null ? "" : " :: " + detail));
    if (!ok) failures++;
}
var catalog = PreviewAttributeCatalog.Re4;
JsonArray Fields(string type) => new(catalog.Defaults(type).Select(f => (JsonNode?)new JsonObject { ["path"] = f.Key, ["value"] = f.Value }).ToArray());
JsonObject Entry(int index, int? attribute = null)
{
    var t = new JsonObject { ["kind"] = "entry", ["index"] = index };
    if (attribute is int a) t["attributeIndex"] = a;
    return t;
}
int Entries(JsonObject reply) => reply["document"]!["entries"]!.AsArray().Count;

string root = Path.Combine(repo, "test-output", "re4-structure-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
var fixture = PreviewDocument.Fixture(game, "bb2");
using var editor = new PreviewEditorSession(repo, root, protectedRoots: [game]);
var state = await editor.OpenAsync(fixture.Path, PreviewDocument.ReadEntryId(fixture.Path, fixture.Offset));
int current = state.Manifest!.EntryPosition;
Check(current == 13, "bb2 opens at entry 13", current);

// 1. add_attribute with the catalogue defaults: one revision, the fields read back as given.
var (added, reply) = await editor.SendRawAsync("add_attribute", new() { ["target"] = Entry(current), ["type"] = "Blink", ["fields"] = Fields("Blink") }, state.Revision);
int blink = added.Attributes!.Single(a => a.Type == "Blink").Index;
var inspected = await editor.InspectAsync(added.Manifest!.DocumentId, added.Revision, blink);
var values = inspected.Inspection!.Fields.ToDictionary(f => f.Path, f => f.Value);
var wrong = catalog.Defaults("Blink").Where(d => values.GetValueOrDefault(d.Key) != d.Value).Select(d => $"{d.Key}={values.GetValueOrDefault(d.Key)} (want {d.Value})").ToArray();
Check(added.Revision == state.Revision + 1 && catalog.Defaults("Blink").Count > 0 && wrong.Length == 0, "add_attribute writes the RE4 default fields in the same revision",
    $"{catalog.Defaults("Blink").Count} fields; " + string.Join(", ", wrong.Take(3)));
state = added;

// 2. An initial field that does not exist is refused and nothing changes.
try
{
    await editor.SendRawAsync("add_attribute", new() { ["target"] = Entry(current), ["type"] = "RotateAnim", ["fields"] = new JsonArray(new JsonObject { ["path"] = "NoSuchField", ["value"] = "1" }) }, state.Revision);
    Check(false, "add_attribute refuses an unknown initial field");
}
catch (PreviewBackendException ex)
{
    Check(editor.State!.Revision == state.Revision && editor.State.Attributes!.All(a => a.Type != "RotateAnim"), "add_attribute refuses an unknown initial field", ex.Message);
}

// 3. add_entry from the Billboard3D template: one revision, appended, selectable, and it emits particles.
var template = catalog.Template("TypeBillboard3D");
var attributes = new JsonArray(template.Select(t => (JsonNode?)new JsonObject { ["type"] = t, ["fields"] = Fields(t) }).ToArray());
(state, reply) = await editor.SendRawAsync("add_entry", new() { ["name"] = "New_Billboard3D", ["attributes"] = attributes }, state.Revision);
int count = Entries(reply), created = reply["selection"]!["index"]!.GetValue<int>();
Check(created == count - 1 && state.Manifest!.EntryPosition == current && state.PublicationError == null, "add_entry appends one entry and keeps the preview target",
    $"template {string.Join(",", template)}; entries {count}");
state = await editor.SelectEntryAsync(created);
Check(state.Manifest!.EntryPosition == created && state.Attributes!.Select(a => a.Type).Order().SequenceEqual(template.Order()), "the new entry is selectable with the template attributes",
    string.Join(",", state.Attributes!.Select(a => a.Type)));
using (var resources = new Re4ResourceSource(game))
using (var native = new NativePreviewClient("python", Path.Combine(repo, "tools", "Re4Native", "re4host.py"), exe))
{
    var document = PreviewDocument.Read(state.Manifest.Path, state.Manifest.Offset, resources);
    await native.LoadAsync(document.LoadCommand());
    using var frame = await native.FrameAsync(45);
    Check(frame.Particles.Count > 0 && frame.Particles.All(p => float.IsFinite(p.Position.X)), "the new Billboard3D entry emits particles in re4.exe's emitter", $"alive {frame.Particles.Count} at tick 45");
}
current = created;

// 4. Removing an entry before the target: the preview follows the target's ID; undo moves it back.
(state, reply) = await editor.SendRawAsync("remove_entry", new() { ["index"] = 2 }, state.Revision);
Check(state.Manifest!.EntryPosition == current - 1 && state.PublicationError == null && Entries(reply) == count - 1, "removing an earlier entry shifts the preview target with its entry",
    $"target {state.Manifest.EntryPosition}, error {state.PublicationError}");
state = await editor.CommandAsync("undo");
Check(state.Manifest!.EntryPosition == current && state.PublicationError == null, "undo restores the entry and the target position", state.Manifest.EntryPosition);

// 5. Duplicate and copy/paste an entry: both appended with the same attribute types.
var types = state.Attributes!.Select(a => a.Type).ToArray();
(state, reply) = await editor.SendRawAsync("duplicate_entry", new() { ["index"] = current }, state.Revision);
int duplicate = Entries(reply) - 1;
state = await editor.SelectEntryAsync(duplicate);
Check(state.Attributes!.Select(a => a.Type).SequenceEqual(types), "duplicate_entry appends a copy", duplicate);
// Undoing the duplicate removes the edited entry: the preview moves to the nearest previewable one.
state = await editor.CommandAsync("undo");
Check(state.PublicationError == null && state.Manifest!.EntryPosition == duplicate - 1, "undoing the duplicate that is being edited moves the preview to the nearest entry",
    $"target {state.Manifest?.EntryPosition}, error {state.PublicationError}");
(state, reply) = await editor.SendRawAsync("duplicate_entry", new() { ["index"] = current }, state.Revision);
(_, reply) = await editor.SendRawAsync("copy_efx", new() { ["target"] = Entry(13) });
string clip = reply["copyBuffer"]!["id"]!.GetValue<string>();
var original = (await editor.SelectEntryAsync(13)).Attributes!.Select(a => a.Type).ToArray();
(state, reply) = await editor.SendRawAsync("paste_efx", new() { ["target"] = new JsonObject { ["kind"] = "document", ["index"] = 0 }, ["clipboardId"] = clip }, editor.State!.Revision);
int pasted = reply["selection"]!["index"]!.GetValue<int>();
state = await editor.SelectEntryAsync(pasted);
Check(pasted == Entries(reply) - 1 && state.Attributes!.Select(a => a.Type).SequenceEqual(original), "copy_efx + paste_efx copies entry 13 as a new entry", pasted);

// 6. Removing the edited entry after moving to another one (what the window does).
state = await editor.SelectEntryAsync(13);
int before = Entries(reply);
(state, reply) = await editor.SendRawAsync("remove_entry", new() { ["index"] = pasted }, state.Revision);
Check(Entries(reply) == before - 1 && state.Manifest!.EntryPosition == 13 && state.PublicationError == null, "removing another entry leaves the edited one untouched");

Console.WriteLine($"{(failures == 0 ? "PASS" : "FAIL")}: {checks - failures}/{checks}; session {root}");
if (failures > 0) Environment.ExitCode = 1;

static string FindRepository()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "RE-MotionVfxStudio.sln"))) return dir.FullName;
    throw new DirectoryNotFoundException("Repository root not found.");
}
