using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ReeLib;
using ReeLib.Efx;
using ReeLib.InternalAttributes;
using VfxStudio.Core.Services;
using via = ReeLib.via;

namespace VfxStudio.LiveEditor;

internal static class ColorEditorTests
{
    private struct NotAColor { public int R, G, B, A; }
    private sealed class Fixture
    {
        public via.Color Tint = new(1, 2, 3, 4);
        public via.Color[] Palette = [new(10, 20, 30, 40), new(50, 60, 70, 80)];
        public Vector4 ArbitraryColor = new(5, 6, 7, 8);
        public NotAColor SimilarNames = new() { R = 1, G = 2, B = 3, A = 4 };
        public float Intensity = 7.5f;
        public readonly via.Color Locked = new(100, 110, 120, 130);
        [RszVersionExact(EfxVersion.RE4)] public via.Color Inactive = new(200, 210, 220, 230);
    }

    public static void Run(Options options, string output, Action<bool, string> check)
    {
        const EfxVersion version = EfxVersion.MHWilds;
        JsonArray Values(params int[] values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        var fixture = new Fixture();
        var colors = ColorEditor.Inspect(fixture, version);
        check(colors.Count == 5 && colors.All(color => color.Storage == "rgba8" && color.Scale == 255 && !color.Hdr),
            "Color grouping uses explicit via.Color storage, never an arbitrary vector or similarly named channels");
        var tint = colors.Single(color => color.Path == "Tint");
        check(tint.Editable && tint.Hex == "#01020304" && tint.Channels.Select(channel => channel.Component).SequenceEqual(new[] { "R", "G", "B", "A" }),
            "RGBA display and channel order differ correctly from the packed integer's printed byte order");
        check(tint.Channels.Select(channel => channel.Path).SequenceEqual(new[] { "Tint.R", "Tint.G", "Tint.B", "Tint.A" }) &&
            tint.Normalized.SequenceEqual(new[] { 1 / 255.0, 2 / 255.0, 3 / 255.0, 4 / 255.0 }), "Color schema contains real scalar paths and normalized preview channels");
        check(!colors.Single(color => color.Path == "Locked").Editable && !colors.Single(color => color.Path == "Inactive").Editable,
            "Read-only and inactive serialized color groups stay read-only");
        var scalarFields = ContentInspector.Describe(fixture, version);
        check(scalarFields.Any(field => field.Description.Path == "Tint.rgba") && tint.Channels.All(channel => scalarFields.Any(field => field.Description.Path == channel.Path)),
            "Color grouping preserves the original packed and four scalar field editors");

        var plan = ColorEditor.Prepare(fixture, version, "Tint", Values(255, 128, 0, 17));
        check(fixture.Tint.rgba == 0x04030201, "Preparing a color transaction does not mutate the model");
        plan.Apply(); plan.Verify(fixture, version);
        check(fixture.Tint.rgba == 0x110080FF && fixture.Intensity == 7.5f && fixture.ArbitraryColor == new Vector4(5, 6, 7, 8),
            "One packed assignment writes RGBA without changing HDR intensity or unrelated vectors");
        ContentInspector.Describe(fixture, version).Single(field => field.Description.Path == "Tint.G").Set!(64);
        check(ColorEditor.Inspect(fixture, version).Single(color => color.Path == "Tint").Hex == "#FF400011", "Editing an original scalar channel updates the grouped color preview");
        bool mismatchRejected = false;
        try { plan.Verify(fixture, version); } catch (InvalidDataException) { mismatchRejected = true; }
        check(mismatchRejected, "Color readback validation detects a changed individual channel");
        var arrayPlan = ColorEditor.Prepare(fixture, version, "Palette[1]", new JsonArray("0", "127", "255", "128"));
        arrayPlan.Apply(); arrayPlan.Verify(fixture, version);
        check(fixture.Palette[1] == new via.Color(0, 127, 255, 128) && fixture.Palette[0] == new via.Color(10, 20, 30, 40),
            "Color groups round-trip array element setters while preserving neighboring colors");
        var rootColor = ColorEditor.Inspect(new via.Color(1, 2, 3, 4), version).Single();
        check(rootColor.Path == "" && rootColor.Channels[0].Path == "R" && !rootColor.Editable, "A root boxed color has honest scalar paths and cannot pretend to have a writable parent");

        void Reject(string path, JsonArray values, string label)
        {
            uint before = fixture.Tint.rgba;
            bool rejected = false;
            try { ColorEditor.Prepare(fixture, version, path, values).Apply(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or OverflowException) { rejected = true; }
            check(rejected && fixture.Tint.rgba == before && fixture.Intensity == 7.5f, label);
        }
        Reject("Tint", Values(1, 2, 3), "Missing alpha rejects the whole color before mutation");
        Reject("Tint", Values(1, 2, 3, 4, 5), "Extra color channels are rejected");
        Reject("Tint", Values(1, 2, 3, 256), "An out-of-range final channel cannot leave earlier channels partially changed");
        Reject("Tint", Values(-1, 2, 3, 4), "Negative byte channels are rejected instead of wrapping");
        Reject("Tint", new JsonArray(1, 2, 3, 1.5), "Fractional raw byte channels are rejected instead of silently rounded");
        Reject("Tint", new JsonArray(1, 2, 3, "NaN"), "Non-finite color values are rejected");
        Reject("Tint", new JsonArray(1, 2, 3, true), "Boolean color values are rejected");
        Reject("Tint", new JsonArray(1, 2, 3, null), "Null color values are rejected");
        Reject("ArbitraryColor", Values(1, 2, 3, 4), "An arbitrary Vector4 cannot be mutated through the color API");
        Reject("Locked", Values(1, 2, 3, 4), "Read-only packed colors cannot be changed through grouped editing");
        Reject("Inactive", Values(1, 2, 3, 4), "Inactive serialized colors cannot be changed through grouped editing");

        string sample = Path.Combine(options.Assets, "Samples", "11_it02_000.efx.5571972");
        string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sample)));
        var service = new EfxEditorService();
        var real = service.Load(sample);
        int entryIndex = -1, attributeIndex = -1;
        for (int i = 0; i < real.Entries.Count && entryIndex < 0; i++)
            for (int j = 0; j < real.Entries[i].Attributes.Count; j++)
                if (real.Entries[i].Attributes[j].type == EfxAttributeType.RgbCommon && ColorEditor.Inspect(real.Entries[i].Attributes[j], version).Any(group => group.Editable))
                { entryIndex = i; attributeIndex = j; break; }
        real.FileHandler.Dispose();
        check(entryIndex >= 0, "Real dual-blades fixture contains explicit editable RgbCommon colors");
        using var engine = new EditorEngine(options with { Mailbox = Path.Combine(output, "color-mailbox"), Output = Path.Combine(output, "color-saved"), GameRoot = null });
        long revision = 0; int sequence = 0;
        JsonObject Request(string action, JsonObject? payload = null, bool success = true)
        {
            payload ??= new(); payload["id"] = "color-" + ++sequence; payload["action"] = action; payload["revision"] = revision;
            var response = engine.Handle(payload); revision = response["revision"]?.GetValue<long>() ?? revision;
            check(response["ok"]!.GetValue<bool>() == success, "Color request " + action + " " + sequence + (response["error"] == null ? "" : ": " + response["error"]));
            return response;
        }
        JsonObject Target() => new() { ["kind"] = "entry", ["index"] = entryIndex, ["attributeIndex"] = attributeIndex };
        JsonObject Inspect() => Request("inspect", new() { ["target"] = Target() });
        JsonObject Group(JsonObject inspected, string path) => inspected["colorGroups"]!.AsArray().Single(group => group!["path"]!.GetValue<string>() == path)!.AsObject();
        int[] Channels(JsonObject group) => group["channels"]!.AsArray().Select(channel => channel!["value"]!.GetValue<int>()).ToArray();
        string Field(JsonObject inspected, string path) => inspected["fields"]!.AsArray().Single(field => field!["path"]!.GetValue<string>() == path)!["value"]!.GetValue<string>();

        Request("open", new() { ["path"] = sample });
        var beforeInspection = Inspect();
        var beforeGroup = Group(beforeInspection, "GreenChColor");
        int[] original = Channels(beforeGroup);
        string otherGroups = string.Join('|', beforeInspection["colorGroups"]!.AsArray().Where(group => group!["path"]!.GetValue<string>() != "GreenChColor").Select(group => group!.ToJsonString()));
        Request("set", new() { ["target"] = Target(), ["path"] = "GreenChIntensity", ["value"] = "7.5" });
        long beforeColorRevision = revision;
        int[] changed = original.Select(channel => 255 - channel).ToArray();
        var changedResponse = Request("set_color", new() { ["target"] = Target(), ["path"] = "GreenChColor", ["values"] = Values(changed) });
        check(revision == beforeColorRevision + 1 && changedResponse["document"]!["dirty"]!.GetValue<bool>(), "A grouped color change creates one revision and marks the document dirty");
        var afterInspection = Inspect();
        check(Channels(Group(afterInspection, "GreenChColor")).SequenceEqual(changed) && Field(afterInspection, "GreenChIntensity") == "7.5",
            "Real RgbCommon RGBA values survive serialization while HDR intensity stays unchanged");
        check(otherGroups == string.Join('|', afterInspection["colorGroups"]!.AsArray().Where(group => group!["path"]!.GetValue<string>() != "GreenChColor").Select(group => group!.ToJsonString())),
            "Editing one real color preserves the other channel and random-range color groups");
        check(new[] { "R", "G", "B", "A" }.Select((component, i) => Field(afterInspection, "GreenChColor." + component) == changed[i].ToString()).All(value => value),
            "Grouped edits and original scalar fields report the same serialized values");
        Request("undo");
        var undone = Inspect();
        check(Channels(Group(undone, "GreenChColor")).SequenceEqual(original) && Field(undone, "GreenChIntensity") == "7.5", "One Undo restores all four channels without undoing the preceding intensity edit");
        Request("redo");
        check(Channels(Group(Inspect(), "GreenChColor")).SequenceEqual(changed), "One Redo restores all four grouped color channels");
        long rejectedRevision = revision;
        Request("set_color", new() { ["target"] = Target(), ["path"] = "GreenChColor", ["values"] = Values(1, 2, 3, 256) }, false);
        check(revision == rejectedRevision && Channels(Group(Inspect(), "GreenChColor")).SequenceEqual(changed), "Rejected grouped edits preserve document values and revision");
        var stale = engine.Handle(new JsonObject { ["id"] = "color-stale", ["action"] = "set_color", ["revision"] = revision - 1,
            ["target"] = Target(), ["path"] = "GreenChColor", ["values"] = Values(1, 2, 3, 4) });
        check(!stale["ok"]!.GetValue<bool>() && stale["revision"]!.GetValue<long>() == revision, "Color edits obey the existing stale-revision guard");
        Request("set", new() { ["target"] = Target(), ["path"] = "GreenChColor.R", ["value"] = "77" });
        check(Channels(Group(Inspect(), "GreenChColor"))[0] == 77, "Legacy single-channel edits update real color-group inspection");
        check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sample))) == originalHash, "Grouped color tests never alter the source asset");
    }
}
