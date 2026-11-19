using System.Numerics;
using Hexa.NET.ImGui;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

/// <summary>Add an attribute of <paramref name="Type"/> to the entry at <paramref name="EntryPosition"/>, or to
/// <paramref name="Target"/> when it is given (an embedded entry).</summary>
internal readonly record struct PreviewAddRequest(int EntryPosition, string Type, PreviewEditTarget? Target = null);

/// <summary>The attribute catalogue: every attribute type grouped by category, ranked and checked for the current
/// entry with <see cref="PreviewAttributeCatalog"/>. Items are dragged onto the property panel or an emitter in
/// the node graph (or double-clicked) to add them.</summary>
internal sealed class PreviewCatalogPanel
{
    public const string Payload = "MVFX_ATTRIBUTE";
    public bool Visible = true;
    private string filter = "";
    private bool showUnusual, showBlocked;
    private string? cacheKey;
    private IReadOnlyList<PreviewCatalogItem> items = [];

    /// <summary>The attribute type being dragged from the catalogue (ImGui's payload only marks the drag).</summary>
    public static string? Dragging { get; private set; }

    /// <param name="existing">Attribute types of the current entry.</param>
    /// <param name="locked">Why adding is not possible now, or null.</param>
    /// <param name="target">The entry the property panel edits (top-level or embedded); double-click adds to it.</param>
    public PreviewAddRequest? Draw(PreviewEditTarget? target, string? entryName, string? entryType, IReadOnlyList<string>? existing, string? locked)
    {
        // A drag ends on mouse release whether or not something accepted it.
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.IsMouseReleased(ImGuiMouseButton.Left)) Dragging = null;
        if (!Visible) return null;
        PreviewAddRequest? add = null;
        ImGui.SetNextWindowPos(new Vector2(12, 400), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(420, 420), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("属性目录", ref Visible)) { ImGui.End(); return null; }
        if (target == null || existing == null)
        {
            ImGui.TextWrapped("打开文档并选中一个 entry 后显示可添加的属性。");
            ImGui.End(); return null;
        }
        var catalog = PreviewAttributeCatalog.Re4;
        bool action = target.Kind == "action";
        string key = target.Kind + "|" + entryType + "|" + entryName + "|" + string.Join(",", existing);
        if (key != cacheKey) { items = catalog.For(entryType, existing, entryName, action); cacheKey = key; }

        ImGui.TextWrapped(action ? $"{target.Label} {entryName} · Action（RE4 共 {catalog.ActionCount} 个，每个只有一个 PlayEmitter 或 PlayEfx）"
            : PreviewAttributeCatalog.IsRoot(entryType, entryName, existing)
            ? $"{target.Label} {entryName} · Root entry（全局控制，RE4 共 {catalog.OwnerCount(null)} 个）"
            : $"{target.Label} {entryName} · {entryType ?? "无 Type"}（RE4 同类 entry {catalog.OwnerCount(entryType)} 个）");
        ImGui.TextDisabled(locked ?? "拖到属性面板或节点图的 entry 上添加 · 双击加到当前 entry");
        ImGui.SetNextItemWidth(-60); ImGui.InputTextWithHint("##搜索", "搜索属性", ref filter, 128);
        ImGui.SameLine(); if (ImGui.SmallButton("清空")) filter = "";
        ImGui.Checkbox("RE4 未用的组合", ref showUnusual);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("也列出 RE4 里同 Type 的 entry 从未用过的属性，以及 Root 与普通 entry 互不使用的属性。后台允许添加，但游戏是否支持没有依据。");
        ImGui.SameLine(); ImGui.Checkbox("不可添加的", ref showBlocked);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("也列出缺少基础属性、第二个 Type、Action 专用的属性（灰色，不能拖）。");
        var missing = items.Where(i => i.Usual && i.Status == PreviewCatalogStatus.Common).ToArray();
        if (missing.Length > 0)
            ImGui.TextColored(new Vector4(1, .7f, .3f, 1), "同类 entry 几乎都有、这里缺少：" + string.Join("、", missing.Select(i => i.Type)));

        ImGui.BeginChild("目录列表");
        foreach (var category in PreviewAttributeCatalog.Categories)
        {
            var shown = items.Where(i => i.Category == category && Shown(i)).ToArray();
            if (shown.Length == 0) continue;
            int addable = shown.Count(i => i.CanAdd);
            if (!ImGui.CollapsingHeader($"{category}（{addable}）###{category}", (category is "类型" or "Action" or "Root") != (action && category == "Action") ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen)) continue;
            string? lastGroup = null;
            foreach (var item in shown)
            {
                // Indent a variant only when its base is the row group just above it (a filter can hide the base).
                string group = catalog.GroupOf(item);
                bool child = group != item.Type && lastGroup == group;
                if (group == item.Type) lastGroup = group; else if (!child) lastGroup = null;
                ImGui.PushID(item.Type);
                bool enabled = item.CanAdd && locked == null;
                var color = item.Status switch
                {
                    PreviewCatalogStatus.Common when item.Usual => new Vector4(1, .7f, .3f, 1),
                    PreviewCatalogStatus.Common => new Vector4(.92f, .92f, .92f, 1),
                    PreviewCatalogStatus.Unseen or PreviewCatalogStatus.RootOnly or PreviewCatalogStatus.NotOnRoot => new Vector4(.85f, .8f, .5f, 1),
                    PreviewCatalogStatus.Unused => new Vector4(.7f, .6f, .45f, 1),
                    _ => new Vector4(.5f, .5f, .5f, 1),
                };
                string mark = (child ? "  └ " : "") + item.Status switch { PreviewCatalogStatus.Present => "(已有) ", PreviewCatalogStatus.Unseen or PreviewCatalogStatus.Unused or PreviewCatalogStatus.RootOnly or PreviewCatalogStatus.NotOnRoot => "? ", _ => "" };
                float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                bool clicked = ImGui.Selectable(mark + item.Type, false, ImGuiSelectableFlags.AllowDoubleClick);
                ImGui.PopStyleColor();
                if (enabled && clicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) add = new PreviewAddRequest(target.Index, item.Type, target);
                if (enabled && ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
                {
                    Dragging = item.Type;
                    unsafe { int marker = 1; ImGui.SetDragDropPayload(Payload, &marker, sizeof(int), ImGuiCond.None); }
                    ImGui.TextUnformatted("添加 " + item.Type);
                    ImGui.EndDragDropSource();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{item.Type}{(item.Base == null ? "" : "（扩展 " + item.Base + "）")}\n{item.Reason}" +
                        (item.CanAdd && locked != null ? "\n现在不能添加：" + locked : ""));
                if (item.Status == PreviewCatalogStatus.Common || item.Status == PreviewCatalogStatus.Present && item.Uses > 0)
                {
                    ImGui.SameLine(right - 44);
                    // TextDisabled takes a printf format: a plain "%" would be eaten.
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                    ImGui.TextUnformatted(item.Uses > 0 && item.Share < .01f ? "<1%" : $"{item.Share * 100:F0}%");
                    ImGui.PopStyleColor();
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.End();
        return add;
    }

    private bool Shown(PreviewCatalogItem item)
    {
        if (filter.Length > 0 && !item.Type.Contains(filter, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter.Length > 0) return true;   // a search shows everything that matches
        return item.Status switch
        {
            PreviewCatalogStatus.Common or PreviewCatalogStatus.Present => true,
            PreviewCatalogStatus.Unseen or PreviewCatalogStatus.Unused or PreviewCatalogStatus.RootOnly or PreviewCatalogStatus.NotOnRoot => showUnusual,
            _ => showBlocked,
        };
    }

    /// <summary>Makes a screen rectangle a drop target for catalogue items, and explains a refusal while an item
    /// is dragged over it. Call inside the window that owns the rectangle.</summary>
    /// <param name="refusal">Given the dragged type: why it cannot be added here, or null to accept it.</param>
    /// <returns>The dropped attribute type, once, on release.</returns>
    public static string? DropTarget(Vector2 min, Vector2 max, uint id, Func<string, string?> refusal)
    {
        if (Dragging is not { } type) return null;
        var mouse = ImGui.GetIO().MousePos;
        if (mouse.X < min.X || mouse.Y < min.Y || mouse.X >= max.X || mouse.Y >= max.Y) return null;
        if (refusal(type) is { } reason)
        {
            ImGui.SetTooltip("不能加到这里：" + reason);
            return null;
        }
        if (!ImGuiP.BeginDragDropTargetCustom(new ImRect { Min = min, Max = max }, id)) return null;
        string? dropped = null;
        unsafe
        {
            var payload = ImGui.AcceptDragDropPayload(Payload, ImGuiDragDropFlags.None);
            if (!payload.IsNull && payload.IsDelivery()) dropped = type;
        }
        ImGui.EndDragDropTarget();
        if (dropped != null) Dragging = null;
        return dropped;
    }

    /// <summary>Why <paramref name="type"/> cannot be added to an entry with these attributes, or null.</summary>
    /// <param name="action">The owner is an Action.</param>
    public static string? Refusal(string type, string? entryType, IEnumerable<string> existing, bool action = false)
    {
        var item = PreviewAttributeCatalog.Re4.Check(type, entryType, existing, action: action);
        return item == null ? "不是 RE4 的属性类型" : item.CanAdd ? null : item.Reason;
    }
}
