using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Enums;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Pt;
using ReeLib.Efx.Structs.Main;

namespace VfxStudio.Re4Render;

public enum PreviewGraphNodeKind { Root, Emitter, Action, EmbeddedEmitter }

/// <summary>What a row inside a node stands for.</summary>
public enum PreviewGraphRowRole
{
    /// <summary>An ordinary attribute of an emitter.</summary>
    Item,
    /// <summary>The emitter's Type* attribute (what it draws).</summary>
    Type,
    /// <summary>PtLife / PtColliderAction: starts an Action; owns an output pin when the link is valid.</summary>
    Trigger,
    /// <summary>An Action's PlayEmitter (output pin to its embedded emitters) or PlayEfx (external file).</summary>
    Target,
    /// <summary>Text only, for example a note that nested content is not expanded.</summary>
    Note,
}

/// <param name="AttributeIndex">Position in the owner's attribute list (the editor's attribute index); null for notes.</param>
/// <param name="OutputPin">Pin id that links leave from; null when the row has no outgoing link.</param>
public sealed record PreviewGraphRow(string Label, PreviewGraphRowRole Role, int? AttributeIndex, int? OutputPin, string? Tooltip = null);

/// <param name="Column">Layer of the default layout: 0 Root, 1 emitters, 2 Actions, 3 embedded emitters.</param>
/// <param name="EntryPosition">For a top-level emitter: its position in the file's entry list.</param>
/// <param name="ActionIndex">For an Action: its index in the file's Action list; for an embedded emitter: the owning Action.</param>
/// <param name="Previewable">A top-level emitter the editor can select: a Type the offline preview draws, or NoDraw
/// (not drawn; its triggers fire child Actions the preview runs).</param>
/// <param name="EntryType">For emitters: the Type* attribute, or null when the entry has none.</param>
/// <param name="AttributeTypes">For emitters: the attribute types the entry has, in order.</param>
public sealed record PreviewGraphNode(int Id, PreviewGraphNodeKind Kind, string Title, string? Subtitle, int Column,
    int? InputPin, IReadOnlyList<PreviewGraphRow> Rows, int? EntryPosition = null, int? ActionIndex = null, bool Previewable = false,
    string? EntryType = null, IReadOnlyList<string>? AttributeTypes = null);

public sealed record PreviewGraphLink(int Id, int FromNode, int FromPin, int ToNode, int ToPin, string Label);

/// <summary>Node and pin positions of the default layout, in layout units (one unit = one text row).</summary>
public readonly record struct PreviewGraphPlacement(int Node, int Column, float Row);

/// <summary>The structure of one EFX file as a node graph, independent of any UI toolkit: the Root, one
/// node per entry with its attribute stack, the file's Actions, and the emitters embedded in each
/// PlayEmitter. Links run Root → emitter, trigger item (PtLife / PtColliderAction) → Action, and
/// PlayEmitter → embedded emitter. Node ids are stable across revisions of the same file as long as entry
/// positions and Action indices do not change, so a UI can keep node positions the user dragged.</summary>
public sealed class PreviewEffectGraph
{
    public const int RootId = 1;
    private const int EmitterBase = 1_000, ActionBase = 100_000, EmbeddedBase = 200_000, PinsPerNode = 1024;

    public IReadOnlyList<PreviewGraphNode> Nodes { get; }
    public IReadOnlyList<PreviewGraphLink> Links { get; }
    /// <summary>Problems found while building, for example a trigger pointing past the Action list.</summary>
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>Position of the file's Root entry: entry 0 when it has no Type attribute (the RE Engine editor's
    /// Root node), else null. RE4 keeps EmitterPriority, UnitCulling and similar file-wide attributes there.</summary>
    public int? RootEntry { get; }
    /// <summary>Index of EmitterPriority in the Root entry's attributes, or null when the file does not use it.</summary>
    public int? EmitterPriorityAttribute { get; }

    private PreviewEffectGraph(List<PreviewGraphNode> nodes, List<PreviewGraphLink> links, List<string> warnings, int? rootEntry, int? emitterPriority)
    {
        Nodes = nodes; Links = links; Warnings = warnings; RootEntry = rootEntry; EmitterPriorityAttribute = emitterPriority;
    }

    /// <summary>EmitterPriority on the Root entry: emitters draw in entry order (later entries on top) instead of
    /// being sorted by distance. All 573 RE4 files that use it carry it on entry 0, a Root without Type.</summary>
    public bool EmitterPriority => EmitterPriorityAttribute != null;

    public PreviewGraphNode? Find(int id) => Nodes.FirstOrDefault(n => n.Id == id);
    public PreviewGraphNode? EmitterAt(int position) => Find(EmitterBase + position);

    public static int InputPinOf(int node) => node * PinsPerNode + 1;
    private static int RowPin(int node, int row) => node * PinsPerNode + 2 + row;

    public static PreviewEffectGraph Read(string path)
    {
        using var stream = File.OpenRead(path);
        var file = new EfxFile(new FileHandler(stream, path) { FileVersion = (int)EfxVersion.RE4 });
        if (!file.Read()) throw new InvalidDataException("无法读取 EFX 文件：" + path);
        return Build(file, System.IO.Path.GetFileName(path));
    }

    public static PreviewEffectGraph Build(EfxFile file, string title)
    {
        var nodes = new List<PreviewGraphNode>();
        var links = new List<PreviewGraphLink>();
        var warnings = new List<string>();
        int linkId = 1;

        int rootPin = RowPin(RootId, 0);
        int? rootEntry = file.Entries.Count > 0 && file.Entries[0].TypeAttribute == null ? 0 : null;
        int? priority = rootEntry == null ? null : file.Entries[0].Attributes.FindIndex(a => a.type == EfxAttributeType.EmitterPriority) is int p and >= 0 ? p : null;
        nodes.Add(new(RootId, PreviewGraphNodeKind.Root, "Root · " + title, null, 0, null,
            [new($"{file.Entries.Count} 个 entry · {file.Actions.Count} 个 Action", PreviewGraphRowRole.Note, null, rootPin),
             new(priority != null ? "[x] EmitterPriority：按 entry 顺序绘制" : "[ ] EmitterPriority：按距离排序", PreviewGraphRowRole.Note, null, null,
                 (priority != null
                    ? "Root entry 带 EmitterPriority：entry 按文件顺序绘制，靠后的画在上面，不看距离。"
                    : "没有 EmitterPriority：entry 按到相机的距离从远到近绘制，近的在上面。") +
                 (rootEntry == null ? "\n这个文件没有 Root entry（第 0 个 entry 有 Type），不能在这里开启。" : "\n右键 Root 切换。"))]));

        for (int position = 0; position < file.Entries.Count; position++)
        {
            var entry = file.Entries[position];
            int id = EmitterBase + position;
            var rows = AttributeRows(id, entry.Attributes, file.Actions.Count, out var triggers);
            string groups = entry.Groups.Count > 0 ? " · 组 " + string.Join("/", entry.Groups) : "";
            nodes.Add(new(id, PreviewGraphNodeKind.Emitter, $"[{position}] {Name(entry.name, position)}",
                $"{entry.TypeAttribute?.type.ToString() ?? "无 Type"} · {entry.entryAssignment}{groups}", 1, InputPinOf(id), rows,
                EntryPosition: position, Previewable: PreviewDocument.KindOf(entry) != null || entry.TypeAttribute is EFXAttributeTypeNoDraw,
                EntryType: entry.TypeAttribute?.type.ToString(), AttributeTypes: entry.Attributes.Select(a => a.type.ToString()).ToArray()));
            links.Add(new(linkId++, RootId, rootPin, id, InputPinOf(id), ""));
            foreach (var (row, action, label) in triggers)
            {
                if (action < 0 || action >= file.Actions.Count)
                {
                    if (action >= 0) warnings.Add($"[{position}] {rows[row].Label} 指向不存在的 Action {action}（共 {file.Actions.Count} 个）。");
                    continue;
                }
                int target = ActionBase + action;
                links.Add(new(linkId++, id, rows[row].OutputPin!.Value, target, InputPinOf(target), label));
            }
        }

        for (int index = 0; index < file.Actions.Count; index++)
        {
            var action = file.Actions[index];
            int id = ActionBase + index;
            var rows = new List<PreviewGraphRow>();
            var embeddedLinks = new List<(int Row, EfxFile Embedded, int Attribute)>();
            for (int a = 0; a < action.Attributes.Count; a++)
            {
                var attribute = action.Attributes[a];
                switch (attribute)
                {
                    case EFXAttributePlayEmitter play when play.efxrData != null:
                        rows.Add(new($"PlayEmitter · 内嵌 {play.efxrData.Entries.Count} 个 entry", PreviewGraphRowRole.Target, a, RowPin(id, rows.Count)));
                        embeddedLinks.Add((rows.Count - 1, play.efxrData, a));
                        break;
                    case EFXAttributePlayEfx efx:
                        rows.Add(new($"PlayEfx · {System.IO.Path.GetFileName(efx.efxPath ?? "")}", PreviewGraphRowRole.Target, a, null, efx.efxPath));
                        break;
                    default:
                        rows.Add(new(attribute.type.ToString(), PreviewGraphRowRole.Item, a, null));
                        break;
                }
            }
            nodes.Add(new(id, PreviewGraphNodeKind.Action, $"Action {index}", string.IsNullOrEmpty(action.name) ? null : action.name, 2,
                InputPinOf(id), rows, ActionIndex: index));

            int embeddedOrdinal = 0;
            foreach (var (row, embedded, attribute) in embeddedLinks)
                for (int position = 0; position < embedded.Entries.Count; position++, embeddedOrdinal++)
                {
                    if (embeddedOrdinal >= PinsPerNode)
                    {
                        warnings.Add($"Action {index} 内嵌 entry 超过 {PinsPerNode} 个，其余未显示。");
                        break;
                    }
                    var entry = embedded.Entries[position];
                    int child = EmbeddedBase + index * PinsPerNode + embeddedOrdinal;
                    var childRows = AttributeRows(child, entry.Attributes, -1, out _);
                    if (embedded.Actions.Count > 0)
                        childRows.Add(new($"内嵌文件另有 {embedded.Actions.Count} 个 Action，未展开", PreviewGraphRowRole.Note, null, null));
                    nodes.Add(new(child, PreviewGraphNodeKind.EmbeddedEmitter, $"[{position}] {Name(entry.name, position)}",
                        $"内嵌 · {entry.TypeAttribute?.type.ToString() ?? "无 Type"} · 属性 {attribute}", 3, InputPinOf(child), childRows, ActionIndex: index,
                        EntryType: entry.TypeAttribute?.type.ToString(), AttributeTypes: entry.Attributes.Select(a => a.type.ToString()).ToArray()));
                    links.Add(new(linkId++, id, rows[row].OutputPin!.Value, child, InputPinOf(child), ""));
                }
        }
        return new(nodes, links, warnings, rootEntry, priority);
    }

    private static string Name(string? name, int position) => string.IsNullOrEmpty(name) ? $"Entry {position}" : name;

    /// <param name="actionCount">Size of the Action list triggers may address; -1 inside an embedded file,
    /// whose triggers address that file's own (unexpanded) Actions and get no pin.</param>
    private static List<PreviewGraphRow> AttributeRows(int node, IReadOnlyList<EFXAttribute> attributes, int actionCount,
        out List<(int Row, int Action, string Label)> triggers)
    {
        var rows = new List<PreviewGraphRow>();
        triggers = [];
        for (int a = 0; a < attributes.Count; a++)
        {
            var attribute = attributes[a];
            string type = attribute.type.ToString();
            (int Action, string Label)? trigger = attribute switch
            {
                EFXAttributePtLife life => (life.ActionIndex, LifeLabel(life.Status)),
                EFXAttributePtColliderAction hit => (hit.linkedAction == uint.MaxValue ? -1 : (int)Math.Min(hit.linkedAction, int.MaxValue), "命中"),
                _ => null,
            };
            if (attribute is EFXAttributePtBehavior behavior)
            {
                // The script class it calls, short name in the row (as the RE Engine editor shows it), full name and
                // parameters in the tooltip.
                string cls = behavior.behaviorString ?? "";
                rows.Add(new($"PtBehavior [{cls[(cls.LastIndexOf('.') + 1)..]}]", PreviewGraphRowRole.Item, a, null,
                    $"调用游戏脚本 {cls}\n参数：{string.Join("、", behavior.properties.Select(p => p.behaviorProperty))}\n" +
                    "预览不运行游戏的 C# 脚本，这个属性对画面没有影响。"));
                continue;
            }
            if (trigger is not { } t)
            {
                bool isType = type.StartsWith("Type", StringComparison.Ordinal) && !type.EndsWith("Expression", StringComparison.Ordinal) &&
                    !type.EndsWith("Clip", StringComparison.Ordinal);
                rows.Add(new(type, isType ? PreviewGraphRowRole.Type : PreviewGraphRowRole.Item, a, null));
                continue;
            }
            bool valid = actionCount >= 0 && t.Action >= 0 && t.Action < actionCount;
            string target = t.Action < 0 ? "不触发" : $"Action {t.Action}" + (actionCount >= 0 && !valid ? "（越界）" : "");
            rows.Add(new($"{type} · {t.Label} → {target}", PreviewGraphRowRole.Trigger, a, valid ? RowPin(node, rows.Count) : null));
            // Out-of-range targets are reported too (Build turns them into warnings, not links).
            if (actionCount >= 0 && t.Action >= 0) triggers.Add((rows.Count - 1, t.Action, t.Label));
        }
        return rows;
    }

    private static string LifeLabel(PtLifeStatus status) => status switch
    {
        PtLifeStatus.PtLifeStatus_Initialize => "出生",
        PtLifeStatus.PtLifeStatus_Appear => "进入 Appear",
        PtLifeStatus.PtLifeStatus_Keep => "进入 Keep",
        PtLifeStatus.PtLifeStatus_Vanish => "进入 Vanish",
        PtLifeStatus.PtLifeStatus_Terminate => "死亡",
        PtLifeStatus.PtLifeStatus_Unknown => "每帧",
        _ => $"状态 {(int)status}",
    };

    /// <summary>Default layered layout: nodes stacked per column in list order, with a gap.
    /// Heights are counted in text rows (title, subtitle, then one per row).</summary>
    /// <param name="visibleRows">Rows a node shows (for collapsed nodes); all rows when null.</param>
    public IReadOnlyList<PreviewGraphPlacement> Layout(Func<PreviewGraphNode, int>? visibleRows = null)
    {
        var next = new float[4];
        var result = new List<PreviewGraphPlacement>(Nodes.Count);
        foreach (var node in Nodes)
        {
            int column = Math.Clamp(node.Column, 0, 3);
            result.Add(new(node.Id, column, next[column]));
            next[column] += 2 + (visibleRows?.Invoke(node) ?? node.Rows.Count) + 1.5f;
        }
        return result;
    }

    /// <summary>Horizontal layout: each layer (Root, emitters, Actions, embedded emitters) in rows of up to
    /// <paramref name="wrap"/> nodes, layers top to bottom. <see cref="PreviewGraphPlacement.Column"/> is the slot in
    /// the row and <see cref="PreviewGraphPlacement.Row"/> the row's top, in text rows.</summary>
    public IReadOnlyList<PreviewGraphPlacement> LayoutRows(int wrap, Func<PreviewGraphNode, int>? visibleRows = null)
    {
        wrap = Math.Max(1, wrap);
        var result = new List<PreviewGraphPlacement>(Nodes.Count);
        float top = 0;
        for (int layer = 0; layer < 4; layer++)
        {
            var nodes = Nodes.Where(n => Math.Clamp(n.Column, 0, 3) == layer).ToArray();
            for (int i = 0; i < nodes.Length; i += wrap)
            {
                float height = 0;
                for (int k = 0; k < wrap && i + k < nodes.Length; k++)
                {
                    var node = nodes[i + k];
                    result.Add(new(node.Id, k, top));
                    height = Math.Max(height, 2 + (visibleRows?.Invoke(node) ?? node.Rows.Count));
                }
                top += height + 1.5f;
            }
        }
        return result;
    }
}
