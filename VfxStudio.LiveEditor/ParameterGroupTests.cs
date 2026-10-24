using System.Text.Json.Nodes;
using ReeLib.Efx;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class ParameterGroupTests
{
    public static void Run(Options options, string output, Action<bool, string> check)
    {
        string sample = Path.Combine(options.Assets, "Samples", "11_it02_000.efx.5571972");
        byte[] original = File.ReadAllBytes(sample);
        var file = new EfxEditorService().Load(sample);
        var fixtures = new List<(int Entry, int Attribute, string[] Paths, string Owner)>();
        foreach (string owner in new[] { "Range", "Vector3" })
        {
            bool found = false;
            for (int i = 0; i < file.Entries.Count && !found; i++)
            for (int j = 0; j < file.Entries[i].Attributes.Count && !found; j++)
            {
                var fields = ContentInspector.Describe(file.Entries[i].Attributes[j], file.Header.Version);
                foreach (var first in fields.Where(f => f.Description.Editable &&
                    (owner == "Range" ? f.Description.OwnerType == typeof(ReeLib.via.Range).FullName || f.Description.OwnerType == typeof(ReeLib.via.RangeI).FullName : f.Description.OwnerType == "System.Numerics.Vector3")))
                {
                    string prefix = first.Description.Path[..first.Description.Path.LastIndexOf('.')];
                    string[] paths = (owner == "Range" ? new[] { "s", "r" } : new[] { "X", "Y", "Z" }).Select(c => prefix + "." + c).ToArray();
                    if (!paths.All(p => fields.Any(f => f.Description.Path == p && f.Description.Editable))) continue;
                    fixtures.Add((i, j, paths, owner)); found = true; break;
                }
            }
            check(found, "Real EFX exposes typed " + owner + " parameter components");
        }
        file.FileHandler.Dispose();
        using var engine = new EditorEngine(options with { Mailbox = Path.Combine(output, "parameter-mailbox"), Output = Path.Combine(output, "parameter-saved"), GameRoot = null });
        int seq = 0; long revision = 0;
        JsonObject Request(string action, JsonObject? body = null, bool ok = true)
        {
            body ??= new(); body["id"] = "parameters-" + ++seq; body["action"] = action;
            if (!body.ContainsKey("revision")) body["revision"] = revision;
            var response = engine.Handle(body); revision = response["revision"]!.GetValue<long>();
            check(response["ok"]!.GetValue<bool>() == ok, "Parameter transaction " + action + " " + seq + (response["error"] is null ? "" : ": " + response["error"]));
            return response;
        }
        Request("open", new() { ["path"] = sample });
        foreach (var fixture in fixtures)
        {
            JsonObject Target() => new() { ["kind"] = "entry", ["index"] = fixture.Entry, ["attributeIndex"] = fixture.Attribute };
            JsonArray Fields() => Request("inspect", new() { ["target"] = Target() })["fields"]!.AsArray();
            var before = Fields();
            string Fingerprint(JsonArray fields) => string.Join('|', fields.Select(f => f!["path"] + ":" + f["value"]));
            var edits = new JsonArray(fixture.Paths.Select((path, index) => (JsonNode)new JsonObject { ["path"] = path, ["value"] = (index + 23).ToString() }).ToArray());
            long oldRevision = revision;
            Request("set_fields", new() { ["target"] = Target(), ["fields"] = edits.DeepClone() });
            check(revision == oldRevision + 1, fixture.Owner + " group creates exactly one revision");
            var changed = Fields();
            check(edits.All(e => changed.Any(f => f!["path"]!.ToString() == e!["path"]!.ToString() && f["value"]!.ToString() == e["value"]!.ToString())), fixture.Owner + " components survive binary serialization and reopening");
            check(before.Where(f => !fixture.Paths.Contains(f!["path"]!.ToString())).All(f => changed.Any(g => g!.ToJsonString() == f!.ToJsonString())), "Untouched fields remain identical");
            Request("undo"); check(Fingerprint(Fields()) == Fingerprint(before), "One undo restores every grouped component");
            var invalid = edits.DeepClone().AsArray(); invalid[1]!["value"] = "invalid-number";
            oldRevision = revision;
            Request("set_fields", new() { ["target"] = Target(), ["fields"] = invalid }, false);
            check(revision == oldRevision && Fingerprint(Fields()) == Fingerprint(before), "Invalid second component changes no values or revision");
            var readOnly = edits.DeepClone().AsArray(); readOnly.Add(new JsonObject { ["path"] = "UniqueID", ["value"] = "1" });
            Request("set_fields", new() { ["target"] = Target(), ["fields"] = readOnly }, false);
            check(Fingerprint(Fields()) == Fingerprint(before), "Read-only member rejects the complete transaction");
            Request("set_fields", new() { ["target"] = Target(), ["fields"] = edits.DeepClone(), ["revision"] = revision - 1 }, false);
            var duplicate = edits.DeepClone().AsArray(); duplicate.Add(edits[0]!.DeepClone());
            Request("set_fields", new() { ["target"] = Target(), ["fields"] = duplicate }, false);
            check(Fingerprint(Fields()) == Fingerprint(before), "Stale revisions and duplicate paths never mutate the EFX");
        }
        check(original.AsSpan().SequenceEqual(File.ReadAllBytes(sample)), "Parameter transactions never overwrite the source EFX");
    }
}
