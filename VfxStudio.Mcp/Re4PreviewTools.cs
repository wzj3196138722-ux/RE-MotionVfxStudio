using System.Text.Json.Nodes;

namespace VfxStudio.Mcp;

/// <summary>Tools that drive the RE4 offline preview window (VfxStudio.Re4Preview). Edits go through
/// the window's own LiveEditor backend, so the window shows every AI change and Undo covers it.</summary>
internal sealed class Re4PreviewToolSet : IToolSet
{
    public string Prefix => "re4preview_";
    public string ServerName => "motionvfx-re4-preview";
    public string Title => "RE4 preview";
    public string Instructions =>
        "Drive the RE4 EFX offline preview window. It simulates every supported CPU Billboard3D, Polygon, Billboard2D, RibbonFollow and RibbonLength entry of one EFX file by running re4.exe particle code natively and renders them; one entry at a time is the edited one (select_entry). " +
        "it never touches the running game or writes the game directory. Workflow: status (or launch if no window is running), open, inspect to list attributes, " +
        "inspect with attributeIndex to read field paths, set_fields with the current revision as expectedRevision, then frame or screenshot at a tick to check the result; " +
        "undo/redo with expectedRevision; save_copy. Structure: attribute_types, add_attribute, remove_attribute, copy_attribute then paste_attribute (the copy survives open, so attributes can move between files), curve_key for keys on existing curves, and add_entry, duplicate_entry, remove_entry, copy_entry, paste_entry for whole entries; " +
        "structural edits shift attribute indices, so take them from the latest result. Every result carries document.revision; a stale expectedRevision is rejected, so re-read status instead of guessing. " +
        "Mutations wait until the new revision is loaded and report previewError when it cannot be shown (for example an unsupported blend mode); the edit itself stays committed and can be undone. " +
        "A draft in the window's property panel blocks AI mutations; ask the user to apply or discard it. Never retry a mutation after outcome_uncertain or timeout: call status first.";

    private static JsonObject Text(string description, int maximum = 4096) => new() { ["type"] = "string", ["description"] = description, ["maxLength"] = maximum };
    private static JsonObject Integer(string description, long minimum = 0, long? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "integer", ["description"] = description, ["minimum"] = minimum };
        if (maximum != null) schema["maximum"] = maximum;
        return schema;
    }
    private static JsonObject Number(string description, double minimum, double maximum) => new() { ["type"] = "number", ["description"] = description, ["minimum"] = minimum, ["maximum"] = maximum };
    private static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };
    private static JsonObject Choice(string description, params string[] values) => new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    private static JsonObject Object(JsonObject properties, params string[] required) => new()
    { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()), ["additionalProperties"] = false };
    private static JsonObject Revision() => Integer("Current document.revision from status or the previous result. A stale value is rejected.");
    private static JsonObject Tick() => Integer("Optional tick (1/60 s) to seek to and pause on before reading; omit to use the displayed frame.", 0, 3600);
    private static JsonObject Fixture() => Choice("Built-in test emitter: bb (saliva, csa013) or bb2 (sm82_320).", "bb", "bb2");
    private static JsonObject Path() => Text("Absolute path of an RE4 .efx file. Game files are only read.", 1024);
    private static JsonObject Offset() => Text("Byte offset of the entry in the file, hexadecimal (for example \"0x2e29\").", 32);
    private static ToolDefinition Define(string action, string description, JsonObject properties, string[] required, bool readOnly = false, bool idempotent = false)
        => new(action, description, Object(properties, required), readOnly, idempotent);

    private static readonly ToolDefinition[] Tools =
    [
        Define("status", "Read the window state: document revision, dirty/undo state, attribute list, preview (displayed revision and tick, load or frame errors, unsupported features), and whether the user has an unapplied draft. Start here.", new(), [], true, true),
        Define("launch", "Start the preview window when none is running, then wait for its first document to load. If a window already answers, returns its status instead of starting another. Requires a Release or Debug build of VfxStudio.Re4Preview.",
            new() { ["fixture"] = Fixture(), ["path"] = Path(), ["offset"] = Offset(),
                ["quality"] = Choice("Particle render resolution; default auto.", "auto", "full", "half", "quarter") }, []),
        Define("open", "Open an EFX file and select one CPU Billboard3D emitter, replacing the current document. Give fixture, or path with exactly one of entryPosition, entryId or offset. The timeline restarts paused at tick 0.",
            new() { ["fixture"] = Fixture(), ["path"] = Path(), ["entryPosition"] = Integer("Zero-based position in the file's entry list."),
                ["entryId"] = Integer("Native entry index field."), ["offset"] = Offset(),
                ["discardUnsaved"] = Boolean("Required true when the current document has unsaved edits that should be dropped.") }, []),
        Define("select_entry", "Make another entry of the open file the edited one (inspect, set_fields and structure tools act on it). The whole file stays loaded, so the preview neither reloads nor moves in time. Billboard3D, Polygon, Billboard2D, RibbonFollow, RibbonLength, RibbonFixEnd and PolygonTrail entries can be selected, and NoDraw and Mesh entries too (not drawn); status.preview.entries lists them.",
            new() { ["entryPosition"] = Integer("Entry position from status.preview.entries."), }, ["entryPosition"]),
        Define("inspect", "Without attributeIndex: list the selected emitter's attributes. With attributeIndex: return its fields (path, type, value, editable, reason, choices), color groups and curves (path, channels, keys). Use the exact paths for set_fields and curve_key.",
            new() { ["attributeIndex"] = Integer("Attribute index from the attribute list.", 0, 4096) }, [], true, true),
        Define("set_fields", "Change 1–64 fields of one attribute as one revision and one Undo step. Values are the textual form shown by inspect (numbers, enum names or values, true/false, strings). The backend validates types, ranges (min ≤ max) and serialization; a rejected batch changes nothing. Waits until the new revision is shown.",
            new() { ["attributeIndex"] = Integer("Attribute index from inspect.", 0, 4096),
                ["fields"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 64, ["description"] = "Field edits with distinct paths.",
                    ["items"] = Object(new() { ["path"] = Text("Exact field path from inspect.", 512), ["value"] = Text("New value as text.", 32768) }, "path", "value") },
                ["expectedRevision"] = Revision() }, ["attributeIndex", "fields", "expectedRevision"]),
        Define("attribute_types", "List the attribute type names this EFX version accepts, for add_attribute. Also returns catalog: for the current entry, every type with category, status (common: RE4 entries with this Type use it; unseen/unused: the backend allows it but RE4 never pairs it with this Type / never uses it; present, secondType, missingBase, actionOnly, notCreatable: refused), canAdd, uses (RE4 entries with this Type that have it, out of sameTypeEntries) and usual (at least 95 % of them have it).", new(), [], true, true),
        Define("add_attribute", "Add a new attribute to the selected emitter; its fields start from a typical RE4 attribute of that type (the same values the window uses). The backend refuses duplicates, a second Type attribute, and attributes whose base attribute is missing. Attribute indices can shift: re-read them from the result. One revision, one Undo step.",
            new() { ["type"] = Text("Attribute type name from attribute_types, for example Life or UVSequence.", 128),
                ["script"] = Text("For PtBehavior: the game script class it calls, e.g. via.effect.script.EffectDecal (the default). Its parameters start from RE4's usual parameter list; the preview does not run the script.", 128),
                ["expectedRevision"] = Revision() }, ["type", "expectedRevision"]),
        Define("remove_attribute", "Remove one attribute from the selected emitter; other attributes and groups must read back unchanged or the edit rolls back. Removing TypeBillboard3D leaves nothing the preview can show (reported as previewError). One Undo step.",
            new() { ["attributeIndex"] = Integer("Attribute index from inspect.", 0, 4096), ["expectedRevision"] = Revision() }, ["attributeIndex", "expectedRevision"]),
        Define("copy_attribute", "Copy one attribute of the selected emitter into the editor's copy buffer. The buffer is a snapshot that survives open, so an attribute can be copied from one file and pasted into another. Does not change the document.",
            new() { ["attributeIndex"] = Integer("Attribute index from inspect.", 0, 4096) }, ["attributeIndex"]),
        Define("paste_attribute", "Paste the copied attribute into the selected emitter. If the emitter already has that type, pass replaceAttributeIndex (the existing attribute of the same type) to replace it; otherwise the paste is refused. Files with different expression/variable tables are refused. One Undo step.",
            new() { ["clipboardId"] = Text("copyBuffer.id from copy_attribute or status.", 64),
                ["replaceAttributeIndex"] = Integer("Existing attribute of the same type to replace.", 0, 4096), ["expectedRevision"] = Revision() }, ["clipboardId", "expectedRevision"]),
        Define("add_entry", "Append a new entry with a Type the preview draws. It gets the attributes at least 95 % of RE4 entries with that Type have, with field values from a typical RE4 attribute of each type, and becomes the selected entry. It joins no effect group. One revision, one Undo step.",
            new() { ["type"] = Choice("Type attribute of the new entry (ignored when script is given).", "TypeBillboard3D", "TypePolygon", "TypeBillboard2D", "TypeRibbonFollow", "TypeRibbonLength", "TypePolygonTrail"),
                ["script"] = Text("Instead of type: a PtBehavior script class (e.g. via.effect.script.EffectDecal). The entry is RE4's usual carrier of that script: TypeNoDraw with Transform3D, ParentOptions, Spawn, Life and the PtBehavior with RE4's usual parameters.", 128),
                ["expectedRevision"] = Revision() }, ["expectedRevision"]),
        Define("duplicate_entry", "Append a copy of an entry (positions from status.preview.entries) and select it when the preview can draw it. One Undo step.",
            new() { ["entryPosition"] = Integer("Entry to copy."), ["expectedRevision"] = Revision() }, ["entryPosition", "expectedRevision"]),
        Define("remove_entry", "Remove an entry. When it is the selected one, the nearest previewable entry becomes selected first; refused when no other entry can be previewed. Later entries move up one position. One Undo step.",
            new() { ["entryPosition"] = Integer("Entry to remove."), ["expectedRevision"] = Revision() }, ["entryPosition", "expectedRevision"]),
        Define("copy_entry", "Copy an entry into the copy buffer (it survives open, so entries can move between files). Does not change the document.",
            new() { ["entryPosition"] = Integer("Entry to copy.") }, ["entryPosition"]),
        Define("paste_entry", "Append the copied entry to the open file (named <name>_copy) and select it when the preview can draw it. Files with different expression/variable tables are refused. One Undo step.",
            new() { ["clipboardId"] = Text("copyBuffer.id from copy_entry or status.", 64), ["expectedRevision"] = Revision() }, ["clipboardId", "expectedRevision"]),
        Define("curve_key", "Add, update or remove one key on an existing curve channel (curves are listed by inspect with attributeIndex). Channel count and mappings never change; a channel keeps at least one key. Bezier keys need all four tangents. One Undo step.",
            new() { ["attributeIndex"] = Integer("Attribute index from inspect.", 0, 4096), ["path"] = Text("Curve path from inspect curves.", 512),
                ["channel"] = Integer("Channel index within the curve.", 0, 4096), ["operation"] = Choice("Key operation.", "add", "update", "remove"),
                ["keyIndex"] = Integer("Key index for update and remove.", 0, 1048576), ["time"] = Text("Key time as text (frames).", 64), ["value"] = Text("Key value as text.", 64),
                ["interpolation"] = Text("Interpolation name as listed by inspect, for example Linear or Bezier; omit to keep the current one.", 64),
                ["outX"] = Text("Bezier out tangent x.", 64), ["outY"] = Text("Bezier out tangent y.", 64), ["inX"] = Text("Bezier in tangent x.", 64), ["inY"] = Text("Bezier in tangent y.", 64),
                ["expectedRevision"] = Revision() }, ["attributeIndex", "path", "channel", "operation", "expectedRevision"]),
        Define("undo", "Undo one document transaction (one set_fields batch, or a window edit). Waits until the restored revision is shown.", new() { ["expectedRevision"] = Revision() }, ["expectedRevision"]),
        Define("redo", "Redo one document transaction.", new() { ["expectedRevision"] = Revision() }, ["expectedRevision"]),
        Define("save_copy", "Save the current document as a new file. Omit path to save in the window's session folder. Paths inside the game directory or the unpacked resources are refused.",
            new() { ["path"] = Text("Optional absolute output path.", 1024) }, []),
        Define("playback", "Control the timeline: play, pause, seek (with tick), step one tick, or replay (reload the emitter and pause at tick 0). Returns after the requested frame is shown, except play.",
            new() { ["operation"] = Choice("Timeline operation.", "play", "pause", "seek", "step", "replay"), ["tick"] = Integer("Target tick for seek.", 0, 3600) }, ["operation"]),
        Define("frame", "Read the displayed frame: tick, time, alive particle count, per-entry alive counts (statistics cover all shown entries unless entryPosition is given), position bounds, width/height and vertex-alpha ranges, UV sequence histogram, and optionally the first particles decoded.",
            new() { ["tick"] = Tick(), ["particles"] = Integer("How many particles to list (default 0).", 0, 256),
                ["entryPosition"] = Integer("Limit statistics and particles to this entry.") }, [], true, true),
        Define("screenshot", "Capture the rendered scene (without the UI panels) as PNG and return it as an image. Also saved under the session folder's ai-captures. Fails while the frame cannot be drawn, for example with an unsupported blend mode.",
            new() { ["tick"] = Tick(), ["maxWidth"] = Integer("Downscale to at most this width (default 960).", 64, 4096) }, [], true, true),
        Define("view", "Set the orbit camera, exposure, bloom, particle-point overlay or particle resolution. Omitted values stay unchanged; reset restores the defaults first.",
            new() { ["reset"] = Boolean("Restore default camera, exposure and bloom first."),
                ["target"] = new JsonObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["description"] = "Orbit target x, y, z.", ["items"] = new JsonObject { ["type"] = "number" } },
                ["distance"] = Number("Camera distance.", 0.3, 25), ["yaw"] = Number("Yaw in radians.", -100, 100), ["pitch"] = Number("Pitch in radians.", -1.5707963, 1.5707963),
                ["exposure"] = Number("Exposure.", 0.1, 5), ["bloom"] = Number("Bloom intensity.", 0, 2),
                ["showPoints"] = Boolean("Draw particle points instead of billboards."),
                ["demoMotion"] = Boolean("Swing the emitter (1 m arm, +-60 degrees, once per second) so PolygonTrail sweeps form; reloads the document."),
                ["soloSelected"] = Boolean("Draw only the selected entry."),
                ["quality"] = Choice("Particle render resolution.", "auto", "full", "half", "quarter") }, [], false, true),
        Define("composition", "Read or change the colour-chain switches (RENDER-HOST 6.9): fades, AlphaCorrection, Rgb layers, soft particles, depth bias, detonemap, header AlphaRate, entry sorting, in-entry depth sorting, Physical gain, TextureFilter, TexelChannelOperator, FrameBlend, the preview light for lit particles, and the blend factors per BlendType 0-4. Omitted values stay unchanged. The result also lists the last draw order (entry positions).",
            new() { ["fades"] = Boolean("FadeByDepth / FadeByAngle."), ["alphaCorrection"] = Boolean("AlphaCorrection."), ["rgbLayers"] = Boolean("RgbCommon / RgbWater."),
                ["softParticles"] = Boolean("Soft particles."), ["depthBias"] = Boolean("ShaderSettings DepthBias."), ["detonemap"] = Boolean("DetonemapRatio (inverse ACES)."),
                ["alphaRate"] = Boolean("Multiply alpha by the header AlphaRate (off by default: it is 0 in most official files)."),
                ["sortEntries"] = Boolean("Sort entries far to near."), ["sortWithinEntry"] = Boolean("Sort particles inside each entry by depth."),
                ["physicalGain"] = Number("Gain on the lit part of Rgb layers under Physical.", 0, 16),
                ["textureFilter"] = Boolean("TextureFilter alpha shaping."), ["texelChannel"] = Boolean("TexelChannelOperator (blackbody colour from the texel)."),
                ["frameBlend"] = Boolean("UVSequence FrameBlend cross-fade."),
                ["distortion"] = Boolean("Draw Distortion entries (screen distortion of the scene drawn before them)."),
                ["lighting"] = Boolean("Light particles whose ShaderSettings.ParticleLighting is 1-4 with the preview light (the preview has no scene lights)."),
                ["ambient"] = Number("Preview ambient light for lit particles.", 0, 4), ["keyLight"] = Number("Preview key light intensity for lit particles.", 0, 4),
                ["decals"] = Boolean("Approximate EffectDecal decals on the ground (flat quad under each carrier particle within SearchTerrainDistance)."),
                ["decalScale"] = Number("Correction on the decal footprint: width = 2 x Extent.X x scale x factor, depth = 2 x Extent.Z x scale x factor (metres; Extent is a half size, checked in game). 1 = as in game.", 0.01, 100),
                ["decalScaleXYRandom"] = Boolean("Read ScaleXY as a random scale X + u*Y (same on both axes) instead of width/depth scales."),
                ["blends"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 5, ["description"] = "Blend factor overrides.",
                    ["items"] = Object(new() { ["blend"] = Integer("BlendType 0-4.", 0, 4),
                        ["source"] = Choice("Source factor.", "Zero", "One", "SrcAlpha", "OneMinusSrcAlpha", "DstColor", "OneMinusDstColor", "SrcColor", "OneMinusSrcColor"),
                        ["destination"] = Choice("Destination factor.", "Zero", "One", "SrcAlpha", "OneMinusSrcAlpha", "DstColor", "OneMinusDstColor", "SrcColor", "OneMinusSrcColor") }, "blend") } }, [], false, true),
        Define("close", "Close the preview window. Refused when the document has unsaved edits unless discardUnsaved is true.",
            new() { ["discardUnsaved"] = Boolean("Close even if edits are unsaved.") }, [])
    ];

    public IReadOnlyList<ToolDefinition> All => Tools;

    public void Validate(ToolDefinition tool, JsonObject arguments)
    {
        ToolCatalog.ValidateValue(tool.Schema, arguments, "arguments");
        bool Has(string name) => arguments[name] != null;
        switch (tool.Action)
        {
            case "launch":
                if (Has("fixture") && Has("path")) throw new ArgumentException("Give fixture or path, not both.");
                if (Has("path") != Has("offset")) throw new ArgumentException("path and offset go together.");
                break;
            case "open":
                if (Has("fixture") == Has("path")) throw new ArgumentException("Give exactly one of fixture or path.");
                int selectors = new[] { "entryPosition", "entryId", "offset" }.Count(Has);
                if (Has("path") ? selectors != 1 : selectors != 0) throw new ArgumentException("path needs exactly one of entryPosition, entryId or offset; fixture takes none.");
                break;
            case "playback":
                if ((arguments["operation"]!.GetValue<string>() == "seek") != Has("tick")) throw new ArgumentException("tick is required for seek and only accepted with seek.");
                break;
            case "curve_key":
                string operation = arguments["operation"]!.GetValue<string>();
                if ((operation == "add") == Has("keyIndex")) throw new ArgumentException("keyIndex is required for update and remove, and not accepted for add.");
                if (operation != "remove" && !(Has("time") && Has("value"))) throw new ArgumentException("add and update need time and value.");
                if (operation == "remove" && new[] { "time", "value", "interpolation", "outX", "outY", "inX", "inY" }.Any(Has)) throw new ArgumentException("remove takes only keyIndex.");
                break;
            case "set_fields":
                var paths = arguments["fields"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).ToArray();
                if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length) throw new ArgumentException("Field paths must be distinct.");
                break;
        }
    }
}
