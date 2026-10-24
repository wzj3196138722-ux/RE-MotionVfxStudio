using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ReeLib.Efx.Structs.Common;

namespace VfxStudio.LiveEditor;

internal static class CurveTests
{
    public static void Run(Options options, string output, Action<bool, string> check)
    {
        EfxClipFrame F(float time, float value, bool bezier = false) => new() { frameTime = time, FloatValue = value, type = bezier ? FrameInterpolationType.Bezier : FrameInterpolationType.Type2 };
        var integer = new EfxClipFrame { frameTime = 0, IntValue = int.MinValue, type = FrameInterpolationType.Bezier };
        check(integer.IntValue == int.MinValue, "Core integer curve setter preserves signed bits");
        var material = new EfxMaterialClipData(ReeLib.Efx.EfxVersion.MHWilds)
        {
            clipDuration = 100,
            clips = [new() { frameCount = 2, valueType = ClipValueType.Float }, new() { frameCount = 1, valueType = ClipValueType.Int }],
            frames = [F(0, 1), F(10, 2, true), integer],
            interpolationData = [new() { out_x = 1, out_y = 2, in_x = 3, in_y = 4 }, new() { out_x = 5, out_y = 6, in_x = 7, in_y = 8 }],
            mdfProperties = [new() { mdfPropertyHash = 123 }], indices = [9, 4]
        };
        JsonObject Key(string operation, int channel, int index, string time = "5", string value = "3", string mode = "Bezier") => new()
        {
            ["operation"] = operation, ["channel"] = channel, ["keyIndex"] = index, ["time"] = time, ["value"] = value,
            ["interpolation"] = mode, ["outX"] = "0.1", ["outY"] = "0.2", ["inX"] = "0.3", ["inY"] = "0.4"
        };
        CurveEditor.Edit(material, Key("add", 0, -1));
        check(material.frames!.Length == 4 && material.frames[1].frameTime == 5 && material.frames[3].IntValue == int.MinValue, "Inserted key sorts in its channel without reinterpreting the integer channel");
        check(material.interpolationData!.Length == 3 && material.interpolationData[0].out_x == 0.1f && material.interpolationData[1].out_x == 1 && material.interpolationData[2].out_x == 5, "Bezier tangent indices follow inserted keys across channels");
        check(material.clipDuration == 100 && material.mdfProperties![0].mdfPropertyHash == 123 && material.indices!.SequenceEqual(new uint[] { 9, 4 }), "Curve edit preserves intentional tail duration and material mappings");
        CurveEditor.Edit(material, Key("update", 0, 1, "6", "4", "Type2"));
        check(material.interpolationData!.Length == 2 && material.interpolationData[0].out_x == 1 && material.interpolationData[1].out_x == 5, "Leaving Bezier removes only that key's tangents");
        CurveEditor.Edit(material, Key("update", 1, 0, "0", int.MaxValue.ToString(CultureInfo.InvariantCulture)));
        check(material.frames![^1].IntValue == int.MaxValue, "Integer key API stores Int32 exactly rather than converting through float");
        CurveEditor.Edit(material, Key("remove", 0, 1));
        check(material.frames!.Length == 3 && material.clips![0].frameCount == 2 && material.frames[1].frameTime == 10, "Removing a key retains channel boundaries and other values");
        void Reject(JsonObject request, string label)
        {
            string before = CurveEditor.Fingerprint(material);
            bool failed = false;
            try { CurveEditor.Edit(material, request); } catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or OverflowException) { failed = true; }
            check(failed && CurveEditor.Fingerprint(material) == before, label);
        }
        Reject(Key("remove", 1, 0), "Cannot silently delete the last key of a mapped channel");
        Reject(Key("update", 1, 0, "0", "1.5"), "Fractional integer values are rejected without mutation");
        Reject(Key("add", 0, -1, "NaN"), "Non-finite curve time is rejected without mutation");
        var missing = Key("update", 0, 0); missing.Remove("inY");
        Reject(missing, "Bezier transition requires all four explicit tangent values");
        Reject(Key("update", 9, 0), "Unknown channel cannot modify the curve");
        var malformed = new EfxClipData { clips = [new() { frameCount = 1, valueType = ClipValueType.Float }], frames = [F(0, 0, true)], interpolationData = [] };
        check(!CurveEditor.Inspect(new("clipData", malformed)).Editable, "Malformed tangent counts are reported instead of silently truncated");
        var empty = new EfxClipData { clips = [new() { frameCount = 0, valueType = ClipValueType.Float }], frames = [], interpolationData = [] };
        CurveEditor.Edit(empty, Key("add", 0, -1, "0", "1", "Type2"));
        check(empty.frames!.Length == 1, "Existing empty mapped channel can receive its first key");

        string sample = Directory.EnumerateFiles(options.Assets, "11_guide_002.efx.5571972", SearchOption.AllDirectories).First();
        string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sample)));
        using var engine = new EditorEngine(options with { Mailbox = Path.Combine(output, "curve-mailbox"), Output = Path.Combine(output, "curve-saved"), GameRoot = null });
        int sequence = 0; long revision = 0;
        JsonObject Request(string action, JsonObject? data = null, bool success = true)
        {
            data ??= new(); data["id"] = "curve-" + ++sequence; data["action"] = action; data["revision"] = revision;
            var result = engine.Handle(data); revision = result["revision"]!.GetValue<long>();
            check(result["ok"]!.GetValue<bool>() == success, "Curve request " + action + " " + sequence + (result["error"] == null ? "" : ": " + result["error"]));
            return result;
        }
        JsonObject Target() => new() { ["kind"] = "document", ["index"] = 0 };
        Request("open", new() { ["path"] = sample });
        JsonArray Inspect() => Request("inspect", new() { ["target"] = Target() })["curves"]!.AsArray();
        var curve = Inspect().First(c => c!["editable"]!.GetValue<bool>() && c["channels"]!.AsArray().Any(ch => ch!["keys"]!.AsArray().Count > 0))!;
        string path = curve["path"]!.GetValue<string>();
        var channel = curve["channels"]!.AsArray().First(ch => ch!["keys"]!.AsArray().Count > 0)!;
        int channelIndex = channel["index"]!.GetValue<int>();
        int count = channel["keys"]!.AsArray().Count;
        string value = channel["keys"]![0]!["value"]!.GetValue<string>();
        float next = channel["keys"]!.AsArray().Max(k => float.Parse(k!["time"]!.GetValue<string>(), CultureInfo.InvariantCulture)) + 1;
        string before = Inspect().ToJsonString();
        var add = Key("add", channelIndex, -1, next.ToString("R", CultureInfo.InvariantCulture), value);
        add["target"] = Target(); add["path"] = path;
        Request("curve_key", add);
        var after = Inspect();
        check(after.First(c => c!["path"]!.GetValue<string>() == path)!["channels"]![channelIndex]!["keys"]!.AsArray().Count == count + 1, "Real EFX key insertion and Bezier tangents survive serializer reopening");
        string edited = after.ToJsonString();
        Request("undo"); check(Inspect().ToJsonString() == before, "Curve undo restores the complete original channel and tangent state");
        Request("redo"); check(Inspect().ToJsonString() == edited, "Curve redo restores all edited keys and tangents");
        var bad = Key("add", channelIndex, -1); bad["target"] = Target(); bad["path"] = "absent.clipData";
        long savedRevision = revision; Request("curve_key", bad, false);
        check(revision == savedRevision && Inspect().ToJsonString() == edited, "Rejected curve paths preserve document revision and contents");
        string saved = Request("save")["outputPath"]!.GetValue<string>();
        Request("open", new() { ["path"] = saved });
        check(Inspect().ToJsonString() == edited, "Saved real EFX curve structure reopens identically");
        check(sourceHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sample))), "Curve integration test does not overwrite its imported source");
        Host.AtomicJson(Path.Combine(output, "curve-editor.json"), new { source = sample, sourceHash, curvePath = path, channel = channelIndex, beforeKeys = count, afterKeys = count + 1, saved, liveGameTested = false });
    }
}
