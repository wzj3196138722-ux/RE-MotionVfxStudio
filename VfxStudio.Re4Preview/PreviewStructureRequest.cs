using Hexa.NET.ImGui;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

/// <summary>A structural edit asked for in the window (property panel, node graph menus, entry buttons).</summary>
/// <param name="Op">add_attribute, remove_attribute, copy_attribute, paste_attribute, copy_entry, paste_entry,
/// add_entry, duplicate_entry, remove_entry, material_parameter, or the Action operations add_action, duplicate_action,
/// remove_action, copy_action, paste_action (Target is the Action, or for add / paste any target in the receiving file).</param>
/// <param name="Type">The attribute type (add_attribute) or the new entry's Type attribute (add_entry).</param>
/// <param name="ExpectedRevision">For AI requests: the revision the change must apply to (stale ones are refused);
/// the window uses the current revision.</param>
/// <param name="Script">PtBehavior script class: add_attribute of PtBehavior fills it from that class's RE4 template;
/// add_entry builds that class's usual carrier entry (NoDraw + PtBehavior).</param>
/// <param name="Body">material_parameter: the backend request (op, listPath and the entry fields; PreviewMaterialReference.AddBody).</param>
/// <param name="Label">Shown in the undo history and in errors.</param>
/// <param name="Target">For attribute edits: the entry or Action that owns the attribute, possibly inside an embedded EFX.
/// Null means the top-level entry at <paramref name="EntryPosition"/>. Entry-level operations take top-level entries only.</param>
internal sealed record PreviewStructureRequest(string Op, int EntryPosition, int AttributeIndex = -1, string? Type = null, long? ExpectedRevision = null,
    string? Script = null, System.Text.Json.Nodes.JsonObject? Body = null, string? Label = null, PreviewEditTarget? Target = null)
{
    public PreviewEditTarget Owner => Target ?? PreviewEditTarget.Root(EntryPosition);
}

/// <summary>Menu items shared by the node graph and the main panel.</summary>
internal static class PreviewStructureMenu
{
    /// <summary>Types the preview can draw, offered for a new entry.</summary>
    public static readonly string[] NewEntryTypes =
        ["TypeBillboard3D", "TypePolygon", "TypeBillboard2D", "TypeRibbonFollow", "TypeRibbonLength", "TypePolygonTrail"];

    /// <summary>Why an attribute cannot be removed from the window, or null.</summary>
    public static string? RemoveRefusal(string type, IReadOnlyList<string> existing)
    {
        if (PreviewAttributeCatalog.IsTypeAttribute(type)) return "删除 Type 后这个 entry 无法预览；要换类型请新建 entry";
        var dependents = PreviewAttributeCatalog.Re4.Dependents(type, existing);
        return dependents.Count > 0 ? "先删除依赖它的 " + string.Join("、", dependents) : null;
    }

    /// <summary>Items for one Action.</summary>
    public static PreviewStructureRequest? ActionItems(PreviewEditTarget action)
    {
        PreviewStructureRequest? chosen = null;
        if (ImGui.MenuItem("复制 Action（可粘贴到其他文件）")) chosen = new("copy_action", action.Index, Target: action);
        if (ImGui.MenuItem("创建副本")) chosen = new("duplicate_action", action.Index, Target: action);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("副本加在 Action 列表末尾，还没有 entry 触发它。");
        if (ImGui.MenuItem("删除 Action…")) chosen = new("remove_action", action.Index, Target: action);
        return chosen;
    }

    /// <summary>Items for one attribute row. Returns the chosen request or null.</summary>
    public static PreviewStructureRequest? AttributeItems(PreviewEditTarget owner, int attribute, string type, IReadOnlyList<string> existing)
    {
        PreviewStructureRequest? chosen = null;
        ImGui.TextDisabled(type);
        if (ImGui.MenuItem("复制属性")) chosen = new("copy_attribute", owner.Index, attribute, Target: owner);
        string? refusal = RemoveRefusal(type, existing);
        if (ImGui.MenuItem("删除属性", (string?)null, false, refusal == null)) chosen = new("remove_attribute", owner.Index, attribute, Target: owner);
        if (refusal != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(refusal);
        return chosen;
    }

    /// <summary>Items for one entry. <paramref name="clipboard"/> is the backend's copy buffer (kind, attributeType).</summary>
    /// <param name="owner">An embedded entry, when the items are for one; null for the top-level entry at <paramref name="entry"/>.</param>
    public static PreviewStructureRequest? EntryItems(int entry, string? clipboardKind, string? clipboardType, bool editable, PreviewEditTarget? owner = null)
    {
        PreviewStructureRequest? chosen = null;
        if (clipboardKind == "attribute")
        {
            if (ImGui.MenuItem("粘贴属性 " + clipboardType, (string?)null, false, editable)) chosen = new("paste_attribute", entry, Target: owner);
            if (!editable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("这个节点不能在面板中编辑");
        }
        if (ImGui.MenuItem("复制 entry（可粘贴到其他文件）")) chosen = new("copy_entry", entry, Target: owner);
        if (ImGui.MenuItem("创建副本")) chosen = new("duplicate_entry", entry, Target: owner);
        if (ImGui.MenuItem("删除 entry…")) chosen = new("remove_entry", entry, Target: owner);
        return chosen;
    }

    /// <summary>Items that create entries: a submenu of Types, and paste when an entry is copied.</summary>
    /// <param name="into">An embedded entry whose file receives the new entry; null for the open file.</param>
    public static PreviewStructureRequest? FileItems(string? clipboardKind, string? clipboardName, PreviewEditTarget? into = null)
    {
        var file = into == null ? null : new PreviewEditTarget("entry", -1, into.Path);
        var chosen = FileItemsCore(clipboardKind, clipboardName);
        return chosen == null || file == null ? chosen : chosen with { Target = file };
    }

    private static PreviewStructureRequest? FileItemsCore(string? clipboardKind, string? clipboardName)
    {
        PreviewStructureRequest? chosen = null;
        if (ImGui.BeginMenu("新建 entry"))
        {
            ImGui.TextDisabled("带上同类 entry 几乎都有的属性，字段取自 RE4 的实际文件");
            foreach (var type in NewEntryTypes)
                if (ImGui.MenuItem(type[4..])) chosen = new("add_entry", -1, Type: type);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("新建脚本载体 entry"))
        {
            // NoDraw entries carrying a PtBehavior, built like RE4's usual carriers of that script.
            ImGui.TextDisabled("NoDraw + PtBehavior，属性与参数取自 RE4 的常见写法；预览不运行脚本");
            foreach (var script in PreviewAttributeCatalog.Re4.Behaviors.Classes.Where(c => c.Carrier.Count > 0))
                if (ImGui.MenuItem($"{script.ShortName}（RE4 {script.Uses} 处）")) chosen = new("add_entry", -1, Type: "TypeNoDraw", Script: script.Name);
            ImGui.EndMenu();
        }
        if (clipboardKind == "entry" && ImGui.MenuItem("粘贴 entry " + clipboardName)) chosen = new("paste_entry", -1);
        ImGui.Separator();
        if (ImGui.MenuItem("新建 Action（PlayEfx）")) chosen = new("add_action", -1, Type: "PlayEfx");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Action 加在列表末尾，还没有 entry 触发它：在某个 entry 的 PtLife 或 PtColliderAction 里填它的序号。\n" +
            "要内嵌粒子的 Action（PlayEmitter），复制一个现有的再粘贴更省事。");
        if (clipboardKind == "action" && ImGui.MenuItem("粘贴 Action " + clipboardName)) chosen = new("paste_action", -1);
        return chosen;
    }
}
