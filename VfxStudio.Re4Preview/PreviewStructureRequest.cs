using Hexa.NET.ImGui;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

/// <summary>A structural edit asked for in the window (property panel, node graph menus, entry buttons).</summary>
/// <param name="Op">add_attribute, remove_attribute, copy_attribute, paste_attribute, copy_entry, paste_entry,
/// add_entry, duplicate_entry or remove_entry.</param>
/// <param name="Type">The attribute type (add_attribute) or the new entry's Type attribute (add_entry).</param>
/// <param name="ExpectedRevision">For AI requests: the revision the change must apply to (stale ones are refused);
/// the window uses the current revision.</param>
internal sealed record PreviewStructureRequest(string Op, int EntryPosition, int AttributeIndex = -1, string? Type = null, long? ExpectedRevision = null);

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

    /// <summary>Items for one attribute row. Returns the chosen request or null.</summary>
    public static PreviewStructureRequest? AttributeItems(int entry, int attribute, string type, IReadOnlyList<string> existing)
    {
        PreviewStructureRequest? chosen = null;
        ImGui.TextDisabled(type);
        if (ImGui.MenuItem("复制属性")) chosen = new("copy_attribute", entry, attribute);
        string? refusal = RemoveRefusal(type, existing);
        if (ImGui.MenuItem("删除属性", (string?)null, false, refusal == null)) chosen = new("remove_attribute", entry, attribute);
        if (refusal != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(refusal);
        return chosen;
    }

    /// <summary>Items for one entry. <paramref name="clipboard"/> is the backend's copy buffer (kind, attributeType).</summary>
    public static PreviewStructureRequest? EntryItems(int entry, string? clipboardKind, string? clipboardType, bool previewable)
    {
        PreviewStructureRequest? chosen = null;
        if (clipboardKind == "attribute")
        {
            if (ImGui.MenuItem("粘贴属性 " + clipboardType, (string?)null, false, previewable)) chosen = new("paste_attribute", entry);
            if (!previewable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("预览暂不支持这个 entry 的类型，不能选中编辑");
        }
        if (ImGui.MenuItem("复制 entry（可粘贴到其他文件）")) chosen = new("copy_entry", entry);
        if (ImGui.MenuItem("创建副本")) chosen = new("duplicate_entry", entry);
        if (ImGui.MenuItem("删除 entry…")) chosen = new("remove_entry", entry);
        return chosen;
    }

    /// <summary>Items that create entries: a submenu of Types, and paste when an entry is copied.</summary>
    public static PreviewStructureRequest? FileItems(string? clipboardKind, string? clipboardName)
    {
        PreviewStructureRequest? chosen = null;
        if (ImGui.BeginMenu("新建 entry"))
        {
            ImGui.TextDisabled("带上同类 entry 几乎都有的属性，字段取自 RE4 的实际文件");
            foreach (var type in NewEntryTypes)
                if (ImGui.MenuItem(type[4..])) chosen = new("add_entry", -1, Type: type);
            ImGui.EndMenu();
        }
        if (clipboardKind == "entry" && ImGui.MenuItem("粘贴 entry " + clipboardName)) chosen = new("paste_entry", -1);
        return chosen;
    }
}
