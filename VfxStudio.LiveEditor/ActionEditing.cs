using System.Text.Json.Nodes;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Main;
using ReeLib.Efx.Structs.Pt;

namespace VfxStudio.LiveEditor;

/// <summary>Action-level edits: add, duplicate and remove Actions in the open file or in an embedded one
/// ("embeddedPath" at the top level of the request, as for entries). Copying and pasting whole Actions goes through
/// copy_efx / paste_efx (EfxClipboard).
///
/// Entries start Actions by list position (PtLife.ActionIndex, PtColliderAction.linkedAction), so removing an Action
/// re-points the triggers of the Actions after it, and refuses while triggers still start it unless "clearTriggers" is
/// given (those triggers then start nothing). New Actions are appended, so no trigger moves.
///
/// actionUnkn0: in the RE4 corpus (536 Actions) it is unique within a file and never equal to an entry id, mostly
/// increasing; it reads as a node id that Actions share with entries. New Actions take one past the largest of both.</summary>
internal sealed partial class EditorEngine
{
    private static int NextNodeId(EfxFile file) => checked(file.Entries.Select(e => e.index)
        .Concat(file.Actions.Select(a => a.actionUnkn0)).DefaultIfEmpty(-1).Max() + 1);

    private static JsonNode? ActionSelection(Target at, int position)
    {
        var selection = Selection(at, position)!;
        selection["kind"] = "action";
        return selection;
    }

    private void AddAction(JsonObject request, JsonObject response)
    {
        var at = EntryFileTarget(request);
        var file = ResolveTargetFile(at);
        var version = file.Header.Version;
        var action = new EFXAction { Version = version, name = OptionalText(request, "name") ?? "New action", actionUnkn0 = NextNodeId(file) };
        if (request["attributes"] is JsonArray initial)
            foreach (var item in initial)
            {
                var spec = item as JsonObject ?? throw new ArgumentException("Each attribute needs a type.");
                if (!action.AddAttribute(NewAttribute(version, Text(spec, "type"), spec["fields"] as JsonArray, spec["behavior"] as JsonObject)))
                    throw new InvalidOperationException("Attribute cannot be added to the new Action: " + Text(spec, "type"));
            }
        else if (request["attributes"] != null) throw new ArgumentException("attributes must be an array.");
        Mutate(() => file.Actions.Add(action), reopened =>
        {
            var local = ResolveTargetFile(at, reopened);
            if (local.Actions.Count != file.Actions.Count || local.Actions[^1].actionUnkn0 != action.actionUnkn0)
                throw new InvalidDataException("The new Action was not preserved by serialization; rolled back.");
        });
        response["selection"] = ActionSelection(at, ResolveTargetFile(at).Actions.Count - 1);
    }

    private void DuplicateAction(JsonObject request, JsonObject response)
    {
        var at = EntryFileTarget(request);
        var file = ResolveTargetFile(at);
        int index = Int(request, "index");
        if (index < 0 || index >= file.Actions.Count) throw new ArgumentOutOfRangeException("index", "Action index is invalid.");
        // A fresh copy from the current bytes: Clone would share a PlayEmitter's embedded file with the original.
        var snapshot = service.Load(bytes!, source!);
        try
        {
            var copy = ResolveTargetFile(at, snapshot).Actions[index];
            Mutate(() =>
            {
                copy.name = UniqueActionName(file, copy.name ?? "Action");
                copy.actionUnkn0 = NextNodeId(file);
                foreach (var attribute in copy.Attributes) PrepareClipboardAttribute(attribute, file, file);
                file.Actions.Add(copy);
            });
        }
        finally { DisposeFile(snapshot); }
        response["selection"] = ActionSelection(at, ResolveTargetFile(at).Actions.Count - 1);
    }

    /// <summary>The PtLife / PtColliderAction attributes of <paramref name="file"/>'s entries that start Action <paramref name="index"/>.</summary>
    private static List<(int Entry, EFXAttribute Trigger)> Triggers(EfxFile file, int index)
    {
        var found = new List<(int, EFXAttribute)>();
        for (int e = 0; e < file.Entries.Count; e++)
            foreach (var attribute in file.Entries[e].Attributes)
                if (attribute is EFXAttributePtLife life && life.ActionIndex == index ||
                    attribute is EFXAttributePtColliderAction hit && hit.linkedAction != uint.MaxValue && hit.linkedAction == (uint)index)
                    found.Add((e, attribute));
        return found;
    }

    private static string TriggerFingerprint(EfxFile file) => string.Join(",", file.Entries.SelectMany(e => e.Attributes).Select(a => a switch
    {
        EFXAttributePtLife life => "L" + life.ActionIndex,
        EFXAttributePtColliderAction hit => "C" + hit.linkedAction,
        _ => null
    }).OfType<string>());

    private void RemoveAction(JsonObject request, JsonObject response)
    {
        var at = EntryFileTarget(request);
        var file = ResolveTargetFile(at);
        int index = Int(request, "index");
        if (index < 0 || index >= file.Actions.Count) throw new ArgumentOutOfRangeException("index", "Action index is invalid.");
        var triggers = Triggers(file, index);
        bool clear = request["clearTriggers"]?.GetValue<bool>() == true;
        if (triggers.Count > 0 && !clear)
            throw new InvalidOperationException($"Action {index} 还被 {string.Join("、", triggers.Select(t => $"[{t.Entry}] 的 {t.Trigger.type}"))} 触发；" +
                "确认删除时这些触发会改为不触发（clearTriggers）。");
        var remaining = file.Actions.Where((_, i) => i != index).Select(a => a.actionUnkn0).ToArray();
        string expectedTriggers = "";
        int before = bytes!.Length;
        Mutate(() =>
        {
            foreach (var attribute in file.Entries.SelectMany(e => e.Attributes))
                switch (attribute)
                {
                    case EFXAttributePtLife life when life.ActionIndex == index: life.ActionIndex = -1; break;
                    case EFXAttributePtLife life when life.ActionIndex > index: life.ActionIndex--; break;
                    case EFXAttributePtColliderAction hit when hit.linkedAction != uint.MaxValue && hit.linkedAction == (uint)index: hit.linkedAction = uint.MaxValue; break;
                    case EFXAttributePtColliderAction hit when hit.linkedAction != uint.MaxValue && hit.linkedAction > (uint)index && hit.linkedAction < int.MaxValue: hit.linkedAction--; break;
                }
            file.Actions.RemoveAt(index);
            expectedTriggers = TriggerFingerprint(file);
        }, reopened =>
        {
            VerifyCompact(reopened);
            var local = ResolveTargetFile(at, reopened);
            if (!local.Actions.Select(a => a.actionUnkn0).SequenceEqual(remaining) || TriggerFingerprint(local) != expectedTriggers)
                throw new InvalidDataException("Action deletion changed surviving Actions or triggers; rolled back.");
        });
        response["deletion"] = new JsonObject { ["kind"] = "action", ["clearedTriggers"] = triggers.Count,
            ["bytesBefore"] = before, ["bytesAfter"] = bytes!.Length };
    }

    private static string UniqueActionName(EfxFile file, string name)
    {
        string stem = name + "_copy", candidate = stem; int suffix = 2;
        while (file.Actions.Any(a => a.name == candidate)) candidate = stem + "_" + suffix++;
        return candidate;
    }
}
