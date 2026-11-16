using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.ImNodes;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

/// <summary>A pick in the graph: an emitter's attribute to open in the property panel.</summary>
internal readonly record struct PreviewGraphPick(int EntryPosition, int AttributeIndex);

/// <summary>Draws <see cref="PreviewEffectGraph"/> with ImNodes: Root → emitters (attribute stacks inline)
/// → Actions → embedded emitters. Read-only structure; clicking an attribute row of a previewable
/// emitter asks the caller to open it in the property panel.</summary>
internal sealed class PreviewGraphPanel : IDisposable
{
    private readonly ImNodesContextPtr context;
    private readonly HashSet<int> placed = [];
    /// <summary>Nodes drawn without their plain attribute rows (rows that carry links stay).</summary>
    private readonly HashSet<int> collapsed = [];
    private string? placedDocument;
    private int? focusNode;
    public bool Visible = true;
    /// <summary>An attribute dropped from the catalogue onto an emitter during the last <see cref="Draw"/>.</summary>
    public PreviewAddRequest? Dropped { get; private set; }
    /// <summary>A structural edit chosen from a right-click menu during the last <see cref="Draw"/>.</summary>
    public PreviewStructureRequest? Requested { get; private set; }
    /// <summary>The backend copy buffer, for the paste items: kind ("attribute" / "entry") and display name.</summary>
    public string? ClipboardKind, ClipboardName;
    private (PreviewGraphNode Node, PreviewGraphRow? Row)? menuTarget;
    /// <summary>ImNodes has no zoom: text is scaled with the window font scale, style sizes with it, and
    /// node positions are rescaled about the mouse when the zoom changes.</summary>
    private float zoom = 1;
    private float? requestedZoom;
    private bool fitRequested;
    private const float MinZoom = .2f, MaxZoom = 2, ColumnWidth = 380;

    public PreviewGraphPanel()
    {
        ImNodes.SetImGuiContext(ImGui.GetCurrentContext());
        context = ImNodes.CreateContext();
        ImNodes.SetCurrentContext(context);
        ImNodes.StyleColorsDark();
    }

    public void Dispose() => ImNodes.DestroyContext(context);

    /// <param name="documentId">Node positions reset when the document changes and are kept across its revisions.</param>
    /// <param name="selectedEntry">Entry open in the property panel, highlighted.</param>
    /// <param name="selectedAttribute">Attribute open in the property panel, highlighted in that entry.</param>
    /// <param name="locked">Why picking is not possible now (busy, unapplied draft), or null.</param>
    public PreviewGraphPick? Draw(PreviewEffectGraph? graph, string? documentId, string? sourceName,
        int? selectedEntry, int selectedAttribute, string? locked)
    {
        Dropped = null; Requested = null;
        if (!Visible) return null;
        (PreviewGraphNode Node, PreviewGraphRow Row)? hoveredRow = null;
        PreviewGraphPick? pick = null;
        // Right of the property panel (x 850..1260) when the window is wide enough, else along the bottom.
        var display = ImGui.GetIO().DisplaySize;
        bool wide = display.X >= 1800;
        ImGui.SetNextWindowPos(wide ? new Vector2(1272, 12) : new Vector2(12, Math.Max(12, display.Y * .55f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(wide ? new Vector2(display.X - 1284, display.Y - 24) : new Vector2(display.X - 24, display.Y * .43f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("EFX 节点图", ref Visible)) { ImGui.End(); return null; }
        if (graph == null) { ImGui.TextWrapped("文档加载后显示节点图。"); ImGui.End(); return null; }
        if (placedDocument != documentId)
        {
            placed.Clear(); collapsed.Clear(); placedDocument = documentId;
            // Large files open fitted; small ones at 100 % centred on the selected entry.
            if (graph.Nodes.Count > 12) fitRequested = true;
            else focusNode = graph.EmitterAt(selectedEntry ?? 0)?.Id;
        }

        // Collapsing or expanding everything also re-packs the layout and fits it, for an overview.
        if (ImGui.SmallButton("全部收起")) { foreach (var n in graph.Nodes) if (Collapsible(n)) collapsed.Add(n.Id); placed.Clear(); fitRequested = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("全部展开")) { collapsed.Clear(); placed.Clear(); fitRequested = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("重新排列")) placed.Clear();
        ImGui.SameLine();
        if (ImGui.SmallButton("适配全部")) fitRequested = true;
        ImGui.SameLine();
        if (ImGui.SmallButton("定位所选 entry") && selectedEntry is int s) focusNode = graph.EmitterAt(s)?.Id;
        ImGui.SameLine();
        if (ImGui.SmallButton("100%")) requestedZoom = 1;
        ImGui.SameLine();
        ImGui.TextDisabled($"{zoom * 100:F0}%% · " + (locked ?? "点 item 在属性面板中打开 · 滚轮缩放 · 中键拖动画布"));
        if (graph.Warnings.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1, .75f, .35f, 1), $"警告 {graph.Warnings.Count}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", graph.Warnings));
        }

        ImNodes.SetCurrentContext(context);
        var canvasMin = ImGui.GetCursorScreenPos();
        var canvasSize = ImGui.GetContentRegionAvail();
        // ImGui 1.92 bakes fonts on demand, so a larger size stays sharp. The size argument excludes the
        // global scales, which ImGui applies on top.
        var style = ImGui.GetStyle();
        float baseSize = ImGui.GetFontSize() / Math.Max(1e-3f, style.FontScaleMain * style.FontScaleDpi);
        ImGui.PushFont(ImGui.GetFont(), baseSize * zoom);
        ImNodes.PushStyleVar(ImNodesStyleVar.NodePadding, new Vector2(8, 8) * zoom);
        ImNodes.PushStyleVar(ImNodesStyleVar.GridSpacing, 24 * zoom);
        ImNodes.PushStyleVar(ImNodesStyleVar.PinCircleRadius, 4 * zoom);
        ImNodes.PushStyleVar(ImNodesStyleVar.LinkThickness, Math.Max(1, 3 * zoom));
        ImNodes.PushStyleVar(ImNodesStyleVar.NodeCornerRounding, 4 * zoom);
        ImNodes.BeginNodeEditor();
        float row = ImGui.GetTextLineHeightWithSpacing();
        var layout = graph.Layout(n => VisibleRows(n).Count() + (collapsed.Contains(n.Id) ? 1 : 0));
        foreach (var placement in layout)
            if (placed.Add(placement.Node))
                ImNodes.SetNodeGridSpacePos(placement.Node, new Vector2(20 + placement.Column * ColumnWidth * zoom, 20 + placement.Row * row));

        foreach (var node in graph.Nodes)
        {
            bool selectedNode = node.Kind == PreviewGraphNodeKind.Emitter && node.EntryPosition == selectedEntry;
            int pushed = PushTitleColors(node, selectedNode);
            ImNodes.BeginNode(node.Id);
            bool folded = collapsed.Contains(node.Id);
            ImNodes.BeginNodeTitleBar();
            if (Collapsible(node))
            {
                ImGui.PushID(node.Id);
                if (ImGui.SmallButton(folded ? "+" : "-")) { if (folded) collapsed.Remove(node.Id); else collapsed.Add(node.Id); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(folded ? "展开" : "收起（保留带连线的行）");
                ImGui.PopID();
                ImGui.SameLine();
            }
            ImGui.TextUnformatted(node.Root(sourceName));
            ImNodes.EndNodeTitleBar();
            if (node.InputPin is int input)
            {
                ImNodes.BeginInputAttribute(input);
                ImGui.TextDisabled(node.Subtitle ?? "");
                ImNodes.EndInputAttribute();
            }
            else if (node.Subtitle != null) ImGui.TextDisabled(node.Subtitle);
            if (node.Kind == PreviewGraphNodeKind.Emitter && !node.Previewable)
                ImGui.TextDisabled("（预览暂不支持此类型，只能查看）");
            if (node.EntryType == "TypeNoDraw") ImGui.TextDisabled("（NoDraw：不绘制，用来触发子 Action）");

            if (folded) ImGui.TextDisabled($"  已收起 {node.Rows.Count - VisibleRows(node).Count()} 项");
            foreach (var r in VisibleRows(node))
            {
                if (r.OutputPin is int output) ImNodes.BeginOutputAttribute(output);
                bool current = selectedNode && r.AttributeIndex == selectedAttribute;
                var color = current ? new Vector4(1, .85f, .3f, 1) : r.Role switch
                {
                    PreviewGraphRowRole.Type => new Vector4(.55f, .8f, 1, 1),
                    PreviewGraphRowRole.Trigger => new Vector4(.6f, 1, .6f, 1),
                    PreviewGraphRowRole.Target => new Vector4(.6f, 1, .6f, 1),
                    PreviewGraphRowRole.Note => new Vector4(.6f, .6f, .6f, 1),
                    _ => new Vector4(.9f, .9f, .9f, 1),
                };
                ImGui.TextColored(color, (current ? "> " : "  ") + r.Label);
                bool clickable = node.Kind == PreviewGraphNodeKind.Emitter && node.Previewable && r.AttributeIndex is int && locked == null;
                if (ImGui.IsItemHovered()) hoveredRow = (node, r);
                if (ImGui.IsItemHovered())
                {
                    string tip = r.Tooltip ?? (node.Kind == PreviewGraphNodeKind.Emitter
                        ? node.Previewable ? locked ?? "点击在属性面板中打开" : "此类型的 entry 预览暂不支持，不能在面板中编辑。"
                        : node.Kind == PreviewGraphNodeKind.EmbeddedEmitter ? "Action 内嵌的 entry：触发时预览会运行它（Billboard3D / Polygon / Billboard2D），还不能在窗口里编辑。" : "");
                    if (tip.Length > 0) ImGui.SetTooltip(tip);
                    if (clickable && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        pick = new PreviewGraphPick(node.EntryPosition!.Value, r.AttributeIndex!.Value);
                }
                if (r.OutputPin is not null) ImNodes.EndOutputAttribute();
            }
            ImNodes.EndNode();
            for (int p = 0; p < pushed; p++) ImNodes.PopColorStyle();
        }
        foreach (var link in graph.Links) ImNodes.Link(link.Id, link.FromPin, link.ToPin);
        ImNodes.MiniMap(.18f, ImNodesMiniMapLocation.BottomRight);
        ImNodes.EndNodeEditor();
        ImNodes.PopStyleVar(5);
        ImGui.PopFont();

        // Catalogue items dropped onto a top-level emitter add the attribute to that entry.
        if (PreviewCatalogPanel.Dragging != null && placed.Count > 0)
            foreach (var node in graph.Nodes)
            {
                if (!placed.Contains(node.Id) || node.Kind is PreviewGraphNodeKind.Root) continue;
                var min = ImNodes.GetNodeScreenSpacePos(node.Id);
                var max = Vector2.Min(min + ImNodes.GetNodeDimensions(node.Id), canvasMin + canvasSize);
                min = Vector2.Max(min, canvasMin);
                if (max.X <= min.X || max.Y <= min.Y) continue;
                var target = node;
                if (PreviewCatalogPanel.DropTarget(min, max, (uint)node.Id, t =>
                        target.Kind == PreviewGraphNodeKind.Action ? "Action 的属性还不能在窗口里添加"
                        : target.Kind == PreviewGraphNodeKind.EmbeddedEmitter ? "Action 内嵌的 entry 还不能在窗口里编辑"
                        : !target.Previewable ? "预览暂不支持这个 entry 的类型，不能选中编辑"
                        : locked ?? PreviewCatalogPanel.Refusal(t, target.EntryType, target.AttributeTypes ?? [])) is { } dropped)
                    Dropped = new PreviewAddRequest(node.EntryPosition!.Value, dropped);
            }

        var io = ImGui.GetIO();
        // Not ImNodes.IsEditorHovered(): after EndNodeEditor it tests the outer window, which is neither
        // hovered (the canvas is its child) nor focused once a node was clicked, so zoom stopped working.
        var mouse = io.MousePos;
        bool overCanvas = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) &&
            mouse.X >= canvasMin.X && mouse.Y >= canvasMin.Y && mouse.X < canvasMin.X + canvasSize.X && mouse.Y < canvasMin.Y + canvasSize.Y;
        // Right click: a menu for the attribute row, the emitter, or the file (empty canvas).
        if (overCanvas && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            int hoveredNode = 0;
            menuTarget = hoveredRow is { } hr ? (hr.Node, hr.Row)
                : ImNodes.IsNodeHovered(ref hoveredNode) && graph.Find(hoveredNode) is { } n ? (n, null)
                : null;
            ImGui.OpenPopup("节点图菜单");
        }
        if (ImGui.BeginPopup("节点图菜单"))
        {
            if (locked != null) ImGui.TextDisabled(locked);
            else Requested = Menu(menuTarget);
            ImGui.EndPopup();
        }
        if (overCanvas && io.MouseWheel != 0)
            Rescale(graph, Math.Clamp(zoom * MathF.Pow(1.15f, io.MouseWheel), MinZoom, MaxZoom), io.MousePos);
        else if (requestedZoom is float z) Rescale(graph, z, canvasMin + canvasSize / 2);
        requestedZoom = null;
        if (fitRequested && placed.Count > 0) { Fit(graph, canvasMin, canvasSize); fitRequested = false; }
        else if (focusNode is int focus && placed.Contains(focus)) { ImNodes.EditorContextMoveToNode(focus); focusNode = null; }
        ImGui.End();
        return pick;
    }

    /// <summary>Changes the zoom, moving every node so the point under <paramref name="anchor"/> (screen
    /// space) stays put.</summary>
    private void Rescale(PreviewEffectGraph graph, float next, Vector2 anchor)
    {
        if (MathF.Abs(next - zoom) < 1e-4f) return;
        float k = next / zoom;
        foreach (var node in graph.Nodes)
            if (placed.Contains(node.Id))
                ImNodes.SetNodeScreenSpacePos(node.Id, anchor + (ImNodes.GetNodeScreenSpacePos(node.Id) - anchor) * k);
        zoom = next;
    }

    /// <summary>Zooms and moves the nodes so all of them fit the canvas, keeping their arrangement.</summary>
    private void Fit(PreviewEffectGraph graph, Vector2 canvasMin, Vector2 canvasSize)
    {
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        foreach (var node in graph.Nodes)
        {
            if (!placed.Contains(node.Id)) continue;
            var p = ImNodes.GetNodeScreenSpacePos(node.Id);
            min = Vector2.Min(min, p); max = Vector2.Max(max, p + ImNodes.GetNodeDimensions(node.Id));
        }
        var size = max - min;
        if (size.X <= 1 || size.Y <= 1 || canvasSize.X <= 40 || canvasSize.Y <= 40) return;
        float next = Math.Clamp(zoom * MathF.Min((canvasSize.X - 40) / size.X, (canvasSize.Y - 40) / size.Y), MinZoom, MaxZoom);
        float k = next / zoom;
        var origin = canvasMin + new Vector2(20);
        foreach (var node in graph.Nodes)
            if (placed.Contains(node.Id))
                ImNodes.SetNodeScreenSpacePos(node.Id, origin + (ImNodes.GetNodeScreenSpacePos(node.Id) - min) * k);
        zoom = next;
    }

    private PreviewStructureRequest? Menu((PreviewGraphNode Node, PreviewGraphRow? Row)? target)
    {
        if (target is not { } t || t.Node.Kind == PreviewGraphNodeKind.Root) return PreviewStructureMenu.FileItems(ClipboardKind, ClipboardName);
        var node = t.Node;
        if (node.Kind != PreviewGraphNodeKind.Emitter)
        {
            ImGui.TextDisabled(node.Kind == PreviewGraphNodeKind.Action ? "Action 还不能在窗口里编辑" : "Action 内嵌的 entry 还不能在窗口里编辑");
            return null;
        }
        int entry = node.EntryPosition!.Value;
        PreviewStructureRequest? chosen = null;
        if (t.Row is { AttributeIndex: int attribute } row && node.AttributeTypes is { } types && attribute < types.Count)
        {
            if (node.Previewable) chosen = PreviewStructureMenu.AttributeItems(entry, attribute, types[attribute], types);
            else ImGui.TextDisabled(types[attribute] + "（预览不支持此 entry，不能编辑属性）");
            ImGui.Separator();
        }
        ImGui.TextDisabled(node.Title);
        chosen ??= PreviewStructureMenu.EntryItems(entry, ClipboardKind, ClipboardName, node.Previewable);
        ImGui.Separator();
        chosen ??= PreviewStructureMenu.FileItems(ClipboardKind, ClipboardName);
        return chosen;
    }

    private static bool Collapsible(PreviewGraphNode node) => node.Kind != PreviewGraphNodeKind.Root;

    /// <summary>Rows drawn for a node: all of them, or when collapsed only those that own a link pin
    /// (ImNodes needs every linked pin to exist).</summary>
    private IEnumerable<PreviewGraphRow> VisibleRows(PreviewGraphNode node) =>
        collapsed.Contains(node.Id) ? node.Rows.Where(r => r.OutputPin != null) : node.Rows;

    private static int PushTitleColors(PreviewGraphNode node, bool selected)
    {
        uint color = selected ? Rgba(200, 140, 30) : node.Kind switch
        {
            PreviewGraphNodeKind.Root => Rgba(90, 90, 100),
            PreviewGraphNodeKind.Action => Rgba(40, 130, 60),
            PreviewGraphNodeKind.EmbeddedEmitter => Rgba(70, 80, 110),
            _ => node.Previewable ? Rgba(50, 90, 160) : Rgba(80, 80, 80),
        };
        ImNodes.PushColorStyle(ImNodesCol.TitleBar, color);
        ImNodes.PushColorStyle(ImNodesCol.TitleBarHovered, Lighter(color));
        ImNodes.PushColorStyle(ImNodesCol.TitleBarSelected, Lighter(color));
        return 3;
    }

    private static uint Rgba(int r, int g, int b, int a = 255) => (uint)(r | g << 8 | b << 16 | a << 24);
    private static uint Lighter(uint c)
    {
        int r = (int)(c & 255), g = (int)(c >> 8 & 255), b = (int)(c >> 16 & 255);
        return Rgba(Math.Min(255, r + 30), Math.Min(255, g + 30), Math.Min(255, b + 30));
    }
}

internal static class PreviewGraphNodeText
{
    /// <summary>Root shows the source file name instead of the snapshot file name.</summary>
    public static string Root(this PreviewGraphNode node, string? sourceName) =>
        node.Kind == PreviewGraphNodeKind.Root && sourceName != null ? "Root · " + sourceName : node.Title;
}
