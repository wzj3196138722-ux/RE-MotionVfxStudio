using System.Numerics;
using Hexa.NET.ImGui;
using VfxStudio.Core.Services;
using IGui = Hexa.NET.ImGui.ImGui;

namespace VfxStudio.App.UI;

public partial class MainWindow
{
    private string _pendingPreviewScrollId = string.Empty;
    private bool _forcePreviewBindingTab;
    private readonly List<int> _lastPreviewTargetIndices = new();
    private PreviewEditState? _previewWidgetBefore;
    private string _previewWidgetBindingId = string.Empty;
    private double _previewHighlightUntil;
    private bool _previewTimelineDragChanged;
    private readonly Dictionary<string, string> _previewResourceErrors = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PreviewMotionDescriptor> _previewMotionCatalog = Array.Empty<PreviewMotionDescriptor>();

    private readonly record struct PreviewEditState(float Start, float End, bool Enabled, string Name, string Bone)
    {
        public static PreviewEditState Capture(PreviewEffectBinding b) =>
            new(b.StartFrame, b.EndFrame, b.Enabled, b.DisplayName, b.MountBoneName);
        public void Apply(PreviewEffectBinding b)
        {
            b.StartFrame = Start; b.EndFrame = End; b.Enabled = Enabled;
            b.DisplayName = Name; b.MountBoneName = Bone;
        }
    }

    private void PersistPreviewEdit()
    {
        SavePreviewDebug();
        _vfxProxy.ClearPreviewSimulators();
    }

    private void RecordPreviewEdit(PreviewEffectBinding binding, PreviewEditState before, string label)
    {
        var after = PreviewEditState.Capture(binding);
        if (before == after) return;
        UndoRedo.Record(new RelayUndoableCommand(label,
            () => { after.Apply(binding); PersistPreviewEdit(); },
            () => { before.Apply(binding); PersistPreviewEdit(); }));
        PersistPreviewEdit();
    }

    private void TrackPreviewWidget(PreviewEffectBinding binding, PreviewEditState before, string label)
    {
        if (IGui.IsItemActivated())
        {
            _previewWidgetBefore = before;
            _previewWidgetBindingId = binding.Id;
        }
        if (IGui.IsItemDeactivatedAfterEdit() && _previewWidgetBindingId == binding.Id && _previewWidgetBefore is { } original)
        {
            RecordPreviewEdit(binding, original, label);
            _previewWidgetBefore = null;
        }
    }

    private void RevealPreviewBinding(PreviewEffectBinding binding)
    {
        _selectedPreviewBindingId = binding.Id;
        _pendingPreviewScrollId = binding.Id;
        _previewDebug.PreviewTracksCollapsed = false;
        _previewHighlightUntil = IGui.GetTime() + 4d;
        _forcePreviewBindingTab = true;
    }

    private string PreviewBindingStatus(PreviewEffectBinding binding)
    {
        if (!File.Exists(binding.EfxPath)) return "资源文件不存在：" + binding.EfxPath;
        if (_previewResourceErrors.TryGetValue(binding.EfxPath, out var error)) return error;
        if (!string.IsNullOrEmpty(binding.IdentityWarning)) return binding.IdentityWarning;
        if (!string.IsNullOrEmpty(binding.FrameRangeWarning)) return binding.FrameRangeWarning;
        if (!string.IsNullOrWhiteSpace(binding.MountBoneName) && _hunterModel != null &&
            !_hunterModel.Bones.Any(b => b.Name.Equals(binding.MountBoneName, StringComparison.OrdinalIgnoreCase)))
            return "当前本地模型没有骨骼：" + binding.MountBoneName;
        if (_previewDebug.PreviewEffectsMuted) return "预览轨道已全部静音";
        if (!binding.Enabled) return "该绑定已停用";
        return "本地预览；结束帧清除残留";
    }

    private static string PreviewBindingLabel(PreviewEffectBinding binding) =>
        string.IsNullOrWhiteSpace(binding.DisplayName) ? Path.GetFileName(binding.EfxPath) : binding.DisplayName;

    private void RenderPreviewTimingFields(PreviewEffectBinding binding)
    {
        IGui.PushID(binding.Id);
        var before = PreviewEditState.Capture(binding);
        float start = binding.StartFrame;
        IGui.SetNextItemWidth(-85f);
        if (IGui.DragFloat("出现帧", ref start, 0.1f, 0f, _maxFrame, "%.2f"))
        {
            binding.StartFrame = float.IsFinite(start) ? Math.Clamp(start, 0f, _maxFrame) : 0f;
            binding.EndFrame = Math.Clamp(Math.Max(binding.StartFrame, binding.EndFrame), binding.StartFrame, _maxFrame);
            _vfxProxy.ClearPreviewSimulators();
        }
        TrackPreviewWidget(binding, before, "调整预览特效出现帧");
        before = PreviewEditState.Capture(binding);
        float end = binding.EndFrame;
        IGui.SetNextItemWidth(-85f);
        if (IGui.DragFloat("消失帧", ref end, 0.1f, 0f, _maxFrame, "%.2f"))
        {
            binding.EndFrame = float.IsFinite(end) ? Math.Clamp(end, binding.StartFrame, _maxFrame) : binding.StartFrame;
            _vfxProxy.ClearPreviewSimulators();
        }
        TrackPreviewWidget(binding, before, "调整预览特效消失帧");
        IGui.PopID();
    }

    private void RenderPreviewBindingProperties(PreviewEffectBinding binding)
    {
        IGui.PushID("properties_" + binding.Id);
        bool enabled = binding.Enabled;
        var before = PreviewEditState.Capture(binding);
        if (IGui.Checkbox("启用此绑定", ref enabled))
        {
            binding.Enabled = enabled;
            RecordPreviewEdit(binding, before, "切换预览绑定启用状态");
        }
        string name = binding.DisplayName;
        before = PreviewEditState.Capture(binding);
        IGui.SetNextItemWidth(-1f);
        if (IGui.InputText("##PreviewName", ref name, 256)) binding.DisplayName = name;
        TrackPreviewWidget(binding, before, "修改预览轨道名称");
        IGui.TextDisabled("挂点（留空使用当前武器默认挂点）");
        string bone = binding.MountBoneName;
        before = PreviewEditState.Capture(binding);
        IGui.SetNextItemWidth(-1f);
        if (IGui.InputText("##PreviewBone", ref bone, 128)) binding.MountBoneName = bone.Trim();
        TrackPreviewWidget(binding, before, "修改预览特效挂点");
        RenderPreviewTimingFields(binding);
        IGui.TextWrapped(PreviewBindingStatus(binding));
        IGui.TextDisabled("结束策略：清除残留");
        IGui.TextWrapped("自然消散和游戏精确跳帧尚待实机验证。");
        IGui.TextWrapped("绑定 ID: " + binding.Id);
        IGui.PopID();
    }

    private void RenderBottomTimeline_Card(float width, float height)
    {
        StudioTheme.BeginCard("TimelineCard", new Vector2(width, height));
        StudioTheme.SectionHeader("|", "动作时间轴");
        if (IGui.Button("|<")) { _currentFrame = 0; _isPlaying = false; }
        IGui.SameLine();
        if (IGui.Button("<")) { _currentFrame = Math.Max(0, _currentFrame - 1); _isPlaying = false; }
        IGui.SameLine();
        if (IGui.Button(_isPlaying ? "||##TimelinePlay" : ">##TimelinePlay")) _isPlaying = !_isPlaying;
        IGui.SameLine();
        if (IGui.Button(">##TimelineNext")) { _currentFrame = Math.Min(_maxFrame, _currentFrame + 1); _isPlaying = false; }
        IGui.SameLine();
        if (IGui.Button(">|")) { _currentFrame = _maxFrame; _isPlaying = false; }
        IGui.SameLine();
        IGui.Text($"{_currentFrame:F2}/{_maxFrame:F2}");
        var bindings = GetPreviewBindings(_selectedMotionIndex);
        if (bindings.Count > 0 || _previewDebug.ShowPreviewEffects)
        {
            bool muted = _previewDebug.PreviewEffectsMuted;
            if (IGui.Checkbox("静音预览特效", ref muted))
            {
                _previewDebug.PreviewEffectsMuted = muted;
                PersistPreviewEdit();
            }
            if (width > 430f) IGui.SameLine();
            float h = _previewDebug.TimelineHeight <= 0 ? height : _previewDebug.TimelineHeight;
            IGui.SetNextItemWidth(85f);
            if (IGui.DragFloat("轨道区高度", ref h, 1f, 180f, Math.Max(180f, IGui.GetIO().DisplaySize.Y - 270f), "%.0f"))
                _previewDebug.TimelineHeight = h;
            if (IGui.IsItemDeactivatedAfterEdit()) SavePreviewDebug();
        }

        // The ruler stays visible while track groups scroll.
        float maxFrame = Math.Max(1f, _maxFrame);
        float labelWidth = 92f;
        Vector2 ruler = IGui.GetCursorScreenPos();
        float trackWidth = Math.Max(40f, IGui.GetContentRegionAvail().X - labelWidth - 14f);
        float laneLeft = ruler.X + labelWidth;
        var draw = IGui.GetWindowDrawList();
        draw.AddRectFilled(ruler, ruler + new Vector2(labelWidth + trackWidth, 25f), 0xFF181A22u);
        float majorStep = Math.Max(10f, MathF.Ceiling(maxFrame / Math.Max(2f, trackWidth / 70f) / 5f) * 5f);
        for (float f = 0f; f <= maxFrame; f += majorStep / 2f)
        {
            float x = laneLeft + f / maxFrame * trackWidth;
            draw.AddLine(new Vector2(x, ruler.Y + 17), new Vector2(x, ruler.Y + 25), 0xFF708090u);
            if (MathF.Abs(f % majorStep) < 0.01f)
                draw.AddText(new Vector2(x - 4, ruler.Y), 0xFFB0C4DEu, $"{f:0.#}");
        }
        IGui.InvisibleButton("TimelineRuler", new Vector2(labelWidth + trackWidth, 26));
        if (IGui.IsItemActive() && string.IsNullOrEmpty(_previewTimelineDragId))
        {
            _currentFrame = Math.Clamp((IGui.GetIO().MousePos.X - laneLeft) / trackWidth, 0, 1) * maxFrame;
            _isPlaying = false;
        }
        float cursorX = laneLeft + _currentFrame / maxFrame * trackWidth;
        draw.AddTriangleFilled(new(cursorX - 5, ruler.Y), new(cursorX + 5, ruler.Y), new(cursorX, ruler.Y + 9), 0xFF3333FFu);

        IGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        IGui.BeginChild("TimelineTrackGroups", new Vector2(-1, -1));
        var contentOrigin = IGui.GetCursorScreenPos();
        draw = IGui.GetWindowDrawList();
        DrawTimelinePlainLane("动作", _currentMot?.Name ?? "未加载动作", StudioTheme.TrackMotion, laneLeft, trackWidth, 0, maxFrame, maxFrame);

        if (bindings.Count > 0 || _previewDebug.ShowPreviewEffects)
        {
            IGui.SetNextItemOpen(!_previewDebug.PreviewTracksCollapsed, ImGuiCond.Always);
            bool open = IGui.TreeNodeEx($"预览特效 ({bindings.Count}){(_previewDebug.PreviewEffectsMuted ? " · 已静音" : "")}##PreviewTrackGroup",
                ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
            if (_previewDebug.PreviewTracksCollapsed == open)
            { _previewDebug.PreviewTracksCollapsed = !open; SavePreviewDebug(); }
            if (open)
            {
                if (bindings.Count == 0) IGui.TextDisabled("导入并绑定后，每个文件会显示为独立轨道。");
                for (int i = 0; i < bindings.Count; i++)
                    DrawPreviewTimelineLane(bindings[i], i, laneLeft, trackWidth, maxFrame);
            }
        }

        IGui.SetNextItemOpen(!_previewDebug.NativeTracksCollapsed, ImGuiCond.Always);
        bool nativeOpen = IGui.TreeNodeEx($"动作原生轨道 ({_currentSlots.Count} 个特效事件)##NativeTrackGroup",
            ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
        if (_previewDebug.NativeTracksCollapsed == nativeOpen)
        { _previewDebug.NativeTracksCollapsed = !nativeOpen; SavePreviewDebug(); }
        if (nativeOpen)
        {
            DrawTimelinePlainLane("武器", "", StudioTheme.TrackWeapon, laneLeft, trackWidth, 0, 0, maxFrame);
            DrawTimelinePlainLane("骨骼", "", StudioTheme.TrackBone, laneLeft, trackWidth, 0, 0, maxFrame);
            for (int i = 0; i < _currentSlots.Count; i++)
            {
                var slot = _currentSlots[i];
                Vector2 pos = IGui.GetCursorScreenPos();
                DrawTimelinePlainLane($"特效 #{i}", $"[{slot.ContainerId}-{slot.EffectId}] [{slot.JointName}] {slot.StartFrame:0.##}–{slot.EndFrame:0.##}f",
                    StudioTheme.TrackVfx, laneLeft, trackWidth, slot.StartFrame, slot.EndFrame, maxFrame, _selectedSlotIndex == i);
                if (IGui.IsItemClicked())
                {
                    _selectedSlotIndex = i;
                    if (_slotResolvedEfxMap.TryGetValue(slot.SlotId, out var resolved) && resolved.EfxList.Count > 0)
                        SelectCalledEfx(resolved.EfxList[0].Path);
                }
            }
        }
        float contentEnd = IGui.GetCursorScreenPos().Y;
        draw.AddLine(new(cursorX, contentOrigin.Y), new(cursorX, Math.Max(contentEnd, contentOrigin.Y + IGui.GetWindowSize().Y)), 0xFF3333FFu, 2f);
        IGui.EndChild();
        IGui.PopStyleVar();
        if (!string.IsNullOrEmpty(_previewTimelineDragId) && !IGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var binding = bindings.FirstOrDefault(b => b.Id == _previewTimelineDragId);
            if (binding != null && _previewTimelineDragChanged)
            {
                var before = PreviewEditState.Capture(binding) with { Start = _previewTimelineDragStartFrame, End = _previewTimelineDragEndFrame };
                RecordPreviewEdit(binding, before, "移动或修剪预览特效轨道");
            }
            _previewTimelineDragId = string.Empty;
            _previewTimelineDragChanged = false;
        }
        StudioTheme.EndCard();
    }

    private void DrawTimelinePlainLane(string label, string text, Vector4 color, float laneLeft, float width,
        float start, float end, float maximum, bool selected = false)
    {
        var pos = IGui.GetCursorScreenPos();
        var draw = IGui.GetWindowDrawList();
        draw.AddText(pos + new Vector2(4, 5), StudioTheme.V4ToAbgr(color), label);
        draw.AddRectFilled(new(laneLeft, pos.Y), new(laneLeft + width, pos.Y + 26), 0x18FFFFFFu);
        if (end > start)
        {
            float x = laneLeft + Math.Clamp(start / maximum, 0, 1) * width;
            float x2 = laneLeft + Math.Clamp(end / maximum, 0, 1) * width;
            draw.AddRectFilled(new(x, pos.Y + 2), new(Math.Max(x + 3, x2), pos.Y + 24),
                StudioTheme.V4ToAbgr(new Vector4(color.X, color.Y, color.Z, selected ? 0.95f : 0.65f)), 3);
            draw.PushClipRect(new(laneLeft, pos.Y), new(laneLeft + width, pos.Y + 26), true);
            draw.AddText(new(x + 5, pos.Y + 5), 0xFFFFFFFFu, text);
            draw.PopClipRect();
        }
        IGui.InvisibleButton("lane_" + label, new Vector2(laneLeft + width - pos.X, 30));
    }

    private void DrawPreviewTimelineLane(PreviewEffectBinding binding, int index, float laneLeft, float width, float maximum)
    {
        var pos = IGui.GetCursorScreenPos();
        var draw = IGui.GetWindowDrawList();
        bool enabled = binding.Enabled;
        var before = PreviewEditState.Capture(binding);
        if (IGui.Checkbox("##PreviewEnable_" + binding.Id, ref enabled))
        { binding.Enabled = enabled; RecordPreviewEdit(binding, before, "切换预览轨道启用状态"); }
        draw.AddText(pos + new Vector2(26, 5), 0xFF5CA9FFu, $"预览 {index + 1}");
        IGui.SetCursorScreenPos(new(laneLeft, pos.Y));
        IGui.InvisibleButton("PreviewTrack_" + binding.Id, new Vector2(width, 26));
        bool hovered = IGui.IsItemHovered();
        bool selected = binding.Id == _selectedPreviewBindingId;
        float start = Math.Clamp(binding.StartFrame, 0, maximum);
        float end = Math.Clamp(binding.EndFrame, start, maximum);
        float x = laneLeft + start / maximum * width;
        float x2 = laneLeft + end / maximum * width;
        // Zero-duration events retain a usable selection/resize target at either boundary.
        if (x2 - x < 12) { x = Math.Min(x, laneLeft + width - 12); x2 = Math.Min(laneLeft + width, x + 12); }
        string status = PreviewBindingStatus(binding);
        bool missing = !File.Exists(binding.EfxPath) || _previewResourceErrors.ContainsKey(binding.EfxPath);
        bool muted = !binding.Enabled || _previewDebug.PreviewEffectsMuted;
        uint color = missing ? 0xAA4545DAu : muted ? 0xFF655C50u : selected ? 0xFF40AAFFu : 0xCC3695DAu;
        draw.AddRectFilled(new(laneLeft, pos.Y), new(laneLeft + width, pos.Y + 26), 0x18FFFFFFu);
        draw.AddRectFilled(new(x, pos.Y + 2), new(x2, pos.Y + 24), color, 3);
        draw.AddRect(new(x, pos.Y + 2), new(x2, pos.Y + 24), selected ? 0xFFFFFFFFu : 0xFF5CA9FFu, 3,
            ImDrawFlags.None, selected && IGui.GetTime() < _previewHighlightUntil ? 3f : 1f);
        draw.AddLine(new(x + 3, pos.Y + 7), new(x + 3, pos.Y + 20), 0xFFFFFFFFu, 2);
        draw.AddLine(new(x2 - 3, pos.Y + 7), new(x2 - 3, pos.Y + 20), 0xFFFFFFFFu, 2);
        draw.PushClipRect(new(laneLeft, pos.Y), new(laneLeft + width, pos.Y + 26), true);
        string bone = string.IsNullOrWhiteSpace(binding.MountBoneName) ? "默认挂点" : binding.MountBoneName;
        draw.AddText(new(Math.Max(laneLeft + 4, x + 7), pos.Y + 5), 0xFFFFFFFFu,
            $"{(missing ? "[缺失] " : muted ? "[静音] " : "")}{PreviewBindingLabel(binding)} [{bone}] {binding.StartFrame:0.##}–{binding.EndFrame:0.##}f");
        draw.PopClipRect();
        if (hovered)
        {
            IGui.SetTooltip($"{PreviewBindingLabel(binding)}\n{status}\nID: {binding.Id}\n拖动两端修剪，拖动中间移动；Ctrl+Z 撤销");
            var mouse = IGui.GetIO().MousePos;
            if (IGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _selectedPreviewBindingId = binding.Id;
                if (mouse.X >= x && mouse.X <= x2)
                {
                    _previewTimelineDragId = binding.Id;
                    _previewTimelineDragMouseX = mouse.X;
                    _previewTimelineDragStartFrame = binding.StartFrame;
                    _previewTimelineDragEndFrame = binding.EndFrame;
                    float edge = Math.Min(8, (x2 - x) * 0.35f);
                    _previewTimelineDragMode = mouse.X - x <= edge ? 2 : x2 - mouse.X <= edge ? 3 : 1;
                    _previewTimelineDragChanged = false;
                    _isPlaying = false;
                }
            }
        }
        if (_previewTimelineDragId == binding.Id && IGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            float delta = (IGui.GetIO().MousePos.X - _previewTimelineDragMouseX) / width * maximum;
            if (_previewTimelineDragMode == 2)
                binding.StartFrame = Math.Clamp(_previewTimelineDragStartFrame + delta, 0, binding.EndFrame);
            else if (_previewTimelineDragMode == 3)
                binding.EndFrame = Math.Clamp(_previewTimelineDragEndFrame + delta, binding.StartFrame, maximum);
            else
            {
                float duration = Math.Clamp(_previewTimelineDragEndFrame - _previewTimelineDragStartFrame, 0, maximum);
                binding.StartFrame = Math.Clamp(_previewTimelineDragStartFrame + delta, 0, maximum - duration);
                binding.EndFrame = binding.StartFrame + duration;
            }
            _previewTimelineDragChanged |= binding.StartFrame != _previewTimelineDragStartFrame || binding.EndFrame != _previewTimelineDragEndFrame;
        }
        if (_pendingPreviewScrollId == binding.Id)
        {
            IGui.SetScrollHereY(0.45f);
            _pendingPreviewScrollId = string.Empty;
        }
        IGui.SetCursorScreenPos(pos + new Vector2(0, 30));
    }
}
