using System.Text.Json.Nodes;

namespace VfxStudio.Mcp;

internal sealed record ToolDefinition(string Action, string Description, JsonObject Schema, bool ReadOnly, bool Idempotent);

internal static class ToolCatalog
{
    public const string Prefix = "motionvfx_";
    private static JsonObject Text(string description, int maximum = 4096) => new() { ["type"] = "string", ["description"] = description, ["maxLength"] = maximum };
    private static JsonObject Integer(string description, long minimum = 0) => new() { ["type"] = "integer", ["description"] = description, ["minimum"] = minimum };
    private static JsonObject Object(JsonObject properties, params string[] required) => new()
    { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()), ["additionalProperties"] = false };
    private static JsonObject Target() => Object(new()
    {
        ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("entry", "action", "document", "group") },
        ["index"] = Integer("Zero-based index returned by the editor."),
        ["attributeIndex"] = Integer("Optional zero-based attribute index; omit to inspect the owning block.")
    }, "kind", "index");
    private static JsonObject Revision() => Integer("Current document revision from status/inspect. Stale revisions are rejected; re-read before editing.");
    private static ToolDefinition Define(string action, string description, JsonObject properties, string[] required, bool readOnly = false, bool idempotent = false)
        => new(action, description, Object(properties, required), readOnly, idempotent);

    public static readonly ToolDefinition[] All =
    [
        Define("status", "Read editor, document, resource-application and scene-time state. Start here. A successful apply request does not prove the next character action has created the new effect version.", new(), [], true, true),
        Define("list_effects", "List resources captured from actual character actions, using their full logical paths and ownership evidence. Do not substitute similarly named local files.", new(), [], true, true),
        Define("list_actions", "List observed character/weapon animation segments, including clips with no captured effects. Identifies weapon, bank, layer, MotionID and animation frames; these are not verified localized gameplay move names. Pass actionId to inspect effect events and trigger IDs. direct_motion_request is causal evidence; contemporaneous_only is temporal context and must never be treated as proof that this motion caused an effect. History is bounded and scene-specific.",
            new() { ["actionId"] = Text("Optional exact action id returned by this tool. Omit to list recorded and current animations.", 128) }, [], true, true),
        Define("open_effect", "Open a currently confirmed captured resource by its complete game logical path. Unsaved document changes or UI drafts block opening. This reads the enabled game files/PAKs, not native memory.",
            new() { ["path"] = Text("Full captured logical EFX path, for example Art/VFX/EffectEditor/Weapon/it02/11_it02_000.efx.") }, ["path"]),
        Define("inspect", "Inspect a document, effect block, attribute or group. Returns field paths, color groups and curves; use the exact returned paths for edits.",
            new() { ["target"] = Target(), ["expectedRevision"] = Revision() }, ["target", "expectedRevision"], true, true),
        Define("set_field", "Change one inspected editable scalar field. The backend validates serialization and supports Undo. Existing UI drafts block AI mutation.",
            new() { ["target"] = Target(), ["path"] = Text("Exact inspected field path."), ["value"] = Text("New value in the field's textual representation; at most 32768 UTF-8 bytes.", 32768), ["expectedRevision"] = Revision() }, ["target", "path", "value", "expectedRevision"]),
        Define("set_color", "Atomically change an explicit rgba8 color group. Values are raw R,G,B,A integers 0..255, not normalized floats. Independent HDR intensity remains untouched; one Undo restores all four channels.",
            new() { ["target"] = Target(), ["path"] = Text("Exact colorGroups path."), ["values"] = new JsonObject { ["type"] = "array", ["minItems"] = 4, ["maxItems"] = 4,
                ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 255 } }, ["expectedRevision"] = Revision() }, ["target", "path", "values", "expectedRevision"]),
        Define("set_group_note", "设置分组的中文备注，只写独立 JSON。不会修改 EFX 原生组名、名称哈希、成员、文档修订、dirty 或撤销/重做记录。同一备注可用于不同组，空字符串清除；恢复旧备注请再次设置旧值，不要 Undo。",
            new() { ["index"] = Integer("Zero-based effect group index."), ["note"] = Text("Editor-only single-line Unicode note, at most 4096 UTF-8 bytes. Empty string clears; spaces are preserved.", 4096), ["expectedRevision"] = Revision() }, ["index", "note", "expectedRevision"], false, true),
        Define("rename_group", "兼容旧客户端的备注接口；name 参数现在仅是中文备注。与 set_group_note 完全相同，不会改写 EFX 原生组名或哈希，也不进入 Undo。新客户端请用 set_group_note；空字符串清除，可与其他组使用相同备注。",
            new() { ["index"] = Integer("Zero-based effect group index."), ["name"] = Text("Editor-only single-line Unicode note (legacy parameter name), at most 4096 UTF-8 bytes. Never a native EFX group rename.", 4096), ["expectedRevision"] = Revision() }, ["index", "name", "expectedRevision"], false, true),
        Define("undo", "Undo one EFX document transaction. A grouped RGBA change counts as one transaction. Separate group notes are unaffected; restore notes using set_group_note with their prior value.", new() { ["expectedRevision"] = Revision() }, ["expectedRevision"]),
        Define("redo", "Redo one document transaction.", new() { ["expectedRevision"] = Revision() }, ["expectedRevision"]),
        Define("save_copy", "Save the current edited document as a separate file through the editor. Omit path for the editor's managed output folder. This does not overwrite game PAKs or prove visual changes.",
            new() { ["path"] = Text("Optional output file path accepted by the editor."), ["expectedRevision"] = Revision() }, ["expectedRevision"]),
        Define("apply", "Apply the current document to the captured character resource. Success means the replacement request completed/was accepted; ask the user to perform the relevant action, then check status for actual new-version creation. Never claim visual confirmation without observation.",
            new() { ["expectedRevision"] = Revision() }, ["expectedRevision"]),
        Define("restore", "Stop recording/preview and restore resource changes plus scene-time ownership through the editor. Check the resulting restoration status before restarting scripts or changing scenes.", new(), [], false, true),
        Define("scene_time", "Slow down, pause or resume the current scene through the editor's existing controls. Use restore to release time ownership afterwards.",
            new() { ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("speed", "pause", "resume") },
                ["speed"] = new JsonObject { ["type"] = "number", ["minimum"] = 0.01, ["maximum"] = 10, ["description"] = "Required for speed, from 0.01 to 10; for example 0.1 or 1." } }, ["operation"]),
        Define("capture_start", "Begin recording effects triggered by actual character actions. Ask the user to perform the intended action, then call capture_stop and list_effects.", new(), []),
        Define("capture_stop", "Finish recording the current character-action capture.", new(), [], false, true)
    ];

    public static ToolDefinition Find(string name) => All.SingleOrDefault(tool => Prefix + tool.Action == name)
        ?? throw new ArgumentException("Unknown tool: " + name);

    public static JsonArray Describe(bool legacy)
    {
        var result = new JsonArray();
        foreach (var tool in All)
        {
            var description = new JsonObject { ["name"] = Prefix + tool.Action, ["description"] = tool.Description, ["inputSchema"] = tool.Schema.DeepClone() };
            if (!legacy) description["annotations"] = new JsonObject { ["title"] = "MotionVfx " + tool.Action, ["readOnlyHint"] = tool.ReadOnly,
                ["destructiveHint"] = !tool.ReadOnly, ["idempotentHint"] = tool.Idempotent, ["openWorldHint"] = false };
            result.Add(description);
        }
        return result;
    }

    public static void Validate(ToolDefinition tool, JsonObject arguments)
    {
        ValidateValue(tool.Schema, arguments, "arguments");
        if (tool.Action is "set_group_note" or "rename_group")
        {
            string note = arguments[tool.Action == "set_group_note" ? "note" : "name"]!.GetValue<string>();
            if (note.Any(char.IsControl)) throw new ArgumentException("Group notes must be single-line text without control characters.");
            if (new System.Text.UTF8Encoding(false, true).GetByteCount(note) > 4096)
                throw new ArgumentException("Group notes may contain at most 4096 UTF-8 bytes.");
        }
        if (tool.Action == "scene_time" && arguments["operation"]!.GetValue<string>() == "speed" && arguments["speed"] == null)
            throw new ArgumentException("arguments.speed is required for the speed operation.");
    }

    private static void ValidateValue(JsonObject schema, JsonNode? value, string path)
    {
        switch (schema["type"]!.GetValue<string>())
        {
            case "object":
                if (value is not JsonObject obj) throw new ArgumentException(path + " must be an object.");
                var properties = schema["properties"]!.AsObject();
                foreach (var name in schema["required"]!.AsArray().Select(node => node!.GetValue<string>()))
                    if (obj[name] == null) throw new ArgumentException(path + "." + name + " is required.");
                foreach (var pair in obj)
                {
                    if (properties[pair.Key] is not JsonObject property) throw new ArgumentException(path + "." + pair.Key + " is not an accepted argument.");
                    ValidateValue(property, pair.Value, path + "." + pair.Key);
                }
                break;
            case "string":
                if (value is not JsonValue textValue || !textValue.TryGetValue<string>(out string? text)) throw new ArgumentException(path + " must be a string.");
                if (schema["maxLength"] is { } max && text.Length > max.GetValue<int>()) throw new ArgumentException(path + " is too long.");
                if (System.Text.Encoding.UTF8.GetByteCount(text) > 32768) throw new ArgumentException(path + " exceeds the Lua bridge's 32768-byte UTF-8 limit.");
                if (schema["enum"] is JsonArray choices && !choices.Any(choice => choice!.GetValue<string>() == text)) throw new ArgumentException(path + " has an unsupported value.");
                break;
            case "integer":
            case "number":
                if (value is not JsonValue numeric || !double.TryParse(numeric.ToJsonString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) throw new ArgumentException(path + " must be a finite number.");
                if (schema["type"]!.GetValue<string>() == "integer" && (number != Math.Truncate(number) || Math.Abs(number) > 9007199254740991D)) throw new ArgumentException(path + " must be a safe integer.");
                if (schema["minimum"] is { } minimum && number < Number(minimum) || schema["maximum"] is { } maximum && number > Number(maximum) ||
                    schema["exclusiveMinimum"] is { } exclusive && number <= Number(exclusive)) throw new ArgumentException(path + " is outside the supported range.");
                break;
            case "array":
                if (value is not JsonArray array) throw new ArgumentException(path + " must be an array.");
                if (array.Count < schema["minItems"]!.GetValue<int>() || array.Count > schema["maxItems"]!.GetValue<int>()) throw new ArgumentException(path + " has an incorrect item count.");
                for (int i = 0; i < array.Count; i++) ValidateValue(schema["items"]!.AsObject(), array[i], path + "[" + i + "]");
                break;
        }
    }
    private static double Number(JsonNode value) => double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
}
