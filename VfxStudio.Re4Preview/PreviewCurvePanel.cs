using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Hexa.NET.ImGui;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

/// <summary>Curve timeline for the attribute open in the property panel: every channel of its clip curves plotted
/// over clip time with the playhead, keys dragged (committed on release), added by double click, edited and
/// removed in the key inspector. Edits are curve_key requests to the backend; the panel never changes the
/// document itself.</summary>
internal sealed class PreviewCurvePanel
{
    public bool Visible = true;
    private int curveIndex, channelIndex, keyIndex = -1;
    private readonly HashSet<int> hidden = [];
    private string? inspectionKey;
    private bool dragging, snap = true;
    private Vector2 dragValue;              // (time, value) while dragging
    private string editTime = "", editValue = "";
    private string editInterpolation = "Type2";
    private string? selectionKey;
    private float viewMin, viewMax;          // value range of the plot (refit on new data)
    private string? fitKey;

    private static readonly Vector4[] Palette =
        [new(1, .35f, .35f, 1), new(.35f, 1, .45f, 1), new(.4f, .6f, 1, 1), new(.9f, .9f, .9f, 1), new(1, .8f, .3f, 1), new(.8f, .45f, 1, 1)];

    private static Vector4 ColorOf(string? name, int index) => name switch
    {
        "R" or "X" => Palette[0], "G" or "Y" => Palette[1], "B" or "Z" => Palette[2], "A" => Palette[3],
        _ => Palette[index % Palette.Length]
    };

    /// <param name="inspection">Inspection of the attribute open in the property panel (null when none).</param>
    /// <param name="attributeType">That attribute's type name (Pt* clips run on particle age).</param>
    /// <param name="tick">Displayed tick, for the playhead of emitter-time clips.</param>
    /// <param name="locked">Why edits are refused now, or null.</param>
    /// <returns>A curve_key request body without the target, or null.</returns>
    public JsonObject? Draw(PreviewInspection? inspection, string? attributeType, int tick, string? locked)
    {
        if (!Visible) return null;
        JsonObject? request = null;
        var display = ImGui.GetIO().DisplaySize;
        // Wide windows: bottom left (the node graph sits right of the property panel). Narrow ones: the gap between
        // the main panel (x < 440) and the property panel (x > 850), since the node graph takes the bottom.
        bool wide = display.X >= 1800;
        ImGui.SetNextWindowPos(wide ? new Vector2(12, Math.Max(12, display.Y - 330)) : new Vector2(448, 12), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(wide ? new Vector2(1240, 318) : new Vector2(396, Math.Max(240, display.Y * .52f)), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("曲线时间轴", ref Visible)) { ImGui.End(); return null; }
        var curves = inspection?.Curves ?? [];
        if (inspection == null) { ImGui.TextWrapped("在属性面板中选择一个属性。"); ImGui.End(); return null; }
        if (curves.Length == 0)
        {
            ImGui.TextWrapped($"所选属性（{attributeType}）没有曲线。曲线在 *Clip 属性里，RE4 常见的是 EmitterColorClip 和 PtColorClip。");
            ImGui.End(); return null;
        }
        string key = inspection.Key;
        if (inspectionKey != key) { inspectionKey = key; curveIndex = 0; channelIndex = 0; keyIndex = -1; hidden.Clear(); fitKey = null; dragging = false; }
        curveIndex = Math.Clamp(curveIndex, 0, curves.Length - 1);
        var curve = curves[curveIndex];
        float duration = PreviewCurveMath.ParseFloat(curve.Duration);
        bool particleTime = PreviewCurveMath.IsParticleTime(attributeType);
        var names = inspection.ClipChannels;

        // Header: curve, time base, playhead.
        if (curves.Length > 1)
        {
            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo("曲线", curve.Path))
            {
                for (int i = 0; i < curves.Length; i++)
                    if (ImGui.Selectable(curves[i].Path, i == curveIndex)) { curveIndex = i; channelIndex = 0; keyIndex = -1; fitKey = null; }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
        }
        float clipTime = PreviewCurveMath.EmitterClipTime(tick, duration);
        ImGui.TextDisabled(particleTime ? $"时长 {curve.Duration} 帧 · 时间为粒子年龄（各粒子不同，不画播放头）"
            : $"时长 {curve.Duration} 帧 · 发射器时间，循环周期 {duration + 1:0.##} 帧 · 播放头 {clipTime:0.#}");
        ImGui.SameLine(); ImGui.Checkbox("吸附到帧", ref snap);
        if (locked != null) { ImGui.SameLine(); ImGui.TextColored(new Vector4(1, .75f, .35f, 1), locked); }
        if (!curve.Editable) ImGui.TextColored(new Vector4(1, .45f, .35f, 1), "曲线只读：" + curve.Reason);

        // Channel toggles.
        var parsed = curve.Channels.Select(PreviewCurveMath.Parse).ToArray();
        channelIndex = Math.Clamp(channelIndex, 0, Math.Max(0, parsed.Length - 1));
        for (int c = 0; c < parsed.Length; c++)
        {
            string name = names != null && c < names.Length ? names[c] : $"通道 {c}";
            var color = ColorOf(names != null && c < names.Length ? names[c] : null, c);
            bool shown = !hidden.Contains(c);
            ImGui.PushID(c);
            ImGui.PushStyleColor(ImGuiCol.CheckMark, color);
            if (ImGui.Checkbox("##show", ref shown)) { if (shown) hidden.Remove(c); else hidden.Add(c); }
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.Selectable($"{name} ({curve.Channels[c].ValueType}, {parsed[c].Length} 键)", c == channelIndex, ImGuiSelectableFlags.None, new Vector2(150, 0)))
            { channelIndex = c; keyIndex = -1; }
            ImGui.PopID();
            ImGui.SameLine();
        }
        ImGui.NewLine();

        // Value range: refit when the curve data changes.
        string dataKey = key + ":" + curveIndex + ":" + string.Join(",", curve.Channels.SelectMany(c => c.Keys.Select(k => k.Value)));
        if (fitKey != dataKey)
        {
            fitKey = dataKey;
            var values = parsed.Where((_, c) => !hidden.Contains(c)).SelectMany(k => k.Select(x => x.Value)).DefaultIfEmpty(0).ToArray();
            float lo = values.Min(), hi = values.Max();
            if (hi - lo < 1e-4f) { lo -= 1; hi += 1; }
            float pad = (hi - lo) * .1f;
            viewMin = lo - pad; viewMax = hi + pad;
        }

        // Plot.
        var canvasMin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var size = new Vector2(Math.Max(200, avail.X), Math.Max(80, avail.Y - 64));
        ImGui.InvisibleButton("##plot", size);
        bool hovered = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
        var dl = ImGui.GetWindowDrawList();
        var canvasMax = canvasMin + size;
        dl.AddRectFilled(canvasMin, canvasMax, ImGui.GetColorU32(new Vector4(.08f, .09f, .11f, 1)));
        float span = Math.Max(1, duration);
        Vector2 ToScreen(float t, float v) => new(canvasMin.X + 8 + (t / span) * (size.X - 16), canvasMax.Y - 8 - (v - viewMin) / (viewMax - viewMin) * (size.Y - 16));
        Vector2 FromScreen(Vector2 p) => new((p.X - canvasMin.X - 8) / (size.X - 16) * span, viewMin + (canvasMax.Y - 8 - p.Y) / (size.Y - 16) * (viewMax - viewMin));
        uint grid = ImGui.GetColorU32(new Vector4(1, 1, 1, .07f));
        for (int g = 0; g <= 10; g++)
        {
            float t = span * g / 10;
            var a = ToScreen(t, viewMin); var b = ToScreen(t, viewMax);
            dl.AddLine(new Vector2(a.X, canvasMin.Y), new Vector2(b.X, canvasMax.Y), grid);
            dl.AddText(new Vector2(a.X + 2, canvasMax.Y - 16), ImGui.GetColorU32(new Vector4(1, 1, 1, .35f)), t.ToString("0", CultureInfo.InvariantCulture));
        }
        for (int g = 0; g <= 4; g++)
        {
            float v = viewMin + (viewMax - viewMin) * g / 4;
            var p = ToScreen(0, v);
            dl.AddLine(new Vector2(canvasMin.X, p.Y), new Vector2(canvasMax.X, p.Y), grid);
            dl.AddText(new Vector2(canvasMin.X + 2, p.Y - 14), ImGui.GetColorU32(new Vector4(1, 1, 1, .35f)), v.ToString("0.##", CultureInfo.InvariantCulture));
        }

        // Selected key follows drag.
        PreviewCurveMath.Key[] Shown(int c)
        {
            if (!(dragging && c == channelIndex && keyIndex >= 0 && keyIndex < parsed[c].Length)) return parsed[c];
            var copy = parsed[c].ToArray();
            copy[keyIndex] = copy[keyIndex] with { Time = dragValue.X, Value = dragValue.Y };
            return copy.OrderBy(k => k.Time).ToArray();
        }

        int hoverChannel = -1, hoverKey = -1;
        var mouse = ImGui.GetMousePos();
        for (int c = 0; c < parsed.Length; c++)
        {
            if (hidden.Contains(c)) continue;
            var keys = Shown(c);
            var color = ImGui.GetColorU32(ColorOf(names != null && c < names.Length ? names[c] : null, c) * new Vector4(1, 1, 1, c == channelIndex ? 1 : .55f));
            int steps = (int)Math.Clamp(size.X / 2, 32, 1200);
            var prev = ToScreen(0, PreviewCurveMath.Evaluate(keys, 0));
            for (int s = 1; s <= steps; s++)
            {
                float t = span * s / steps;
                var p = ToScreen(t, PreviewCurveMath.Evaluate(keys, t));
                dl.AddLine(prev, p, color, c == channelIndex ? 2 : 1.2f);
                prev = p;
            }
            for (int k = 0; k < parsed[c].Length; k++)
            {
                var kk = dragging && c == channelIndex && k == keyIndex ? parsed[c][k] with { Time = dragValue.X, Value = dragValue.Y } : parsed[c][k];
                var p = ToScreen(kk.Time, kk.Value);
                bool selected = c == channelIndex && k == keyIndex;
                if (hovered && Vector2.Distance(p, mouse) < 6) { hoverChannel = c; hoverKey = k; }
                float r = selected ? 5 : 4;
                dl.AddRectFilled(p - new Vector2(r), p + new Vector2(r), selected ? ImGui.GetColorU32(new Vector4(1, .85f, .3f, 1)) : color);
                if (!PreviewCurveMath.IsKnown(kk.Interpolation)) dl.AddRect(p - new Vector2(r + 2), p + new Vector2(r + 2), ImGui.GetColorU32(new Vector4(1, .3f, .3f, 1)));
            }
        }
        if (!particleTime)
        {
            var ph = ToScreen(clipTime, viewMin);
            dl.AddLine(new Vector2(ph.X, canvasMin.Y), new Vector2(ph.X, canvasMax.Y), ImGui.GetColorU32(new Vector4(1, .85f, .3f, .8f)), 1.5f);
        }

        bool canEdit = locked == null && curve.Editable && parsed.Length > 0;
        var channel = parsed.Length > 0 ? parsed[channelIndex] : [];
        bool isInt = parsed.Length > 0 && curve.Channels[channelIndex].ValueType == "Int";
        Vector2 Snap(Vector2 tv) => new(snap ? MathF.Round(tv.X) : tv.X, isInt ? MathF.Round(tv.Y) : tv.Y);

        // Mouse: select / drag (commit on release) / double-click to add.
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hoverKey >= 0) { channelIndex = hoverChannel; keyIndex = hoverKey; channel = parsed[channelIndex]; dragging = canEdit; dragValue = new(channel[keyIndex].Time, channel[keyIndex].Value); }
            else keyIndex = -1;
        }
        if (dragging && active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var tv = Snap(FromScreen(mouse));
            dragValue = new(Math.Clamp(tv.X, 0, Math.Max(span, tv.X)), tv.Y);
        }
        if (dragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            dragging = false;
            if (keyIndex >= 0 && keyIndex < channel.Length && (dragValue.X != channel[keyIndex].Time || dragValue.Y != channel[keyIndex].Value))
                request = Update(curve, channelIndex, keyIndex, dragValue.X, dragValue.Y, channel[keyIndex].Interpolation);
        }
        if (canEdit && hovered && hoverKey < 0 && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            var tv = Snap(FromScreen(mouse));
            request = new JsonObject { ["path"] = curve.Path, ["channel"] = channelIndex, ["operation"] = "add",
                ["time"] = Text(Math.Max(0, tv.X)), ["value"] = Text(tv.Y), ["interpolation"] = "Type2" };
        }
        if (hovered && hoverKey >= 0)
        {
            var k = parsed[hoverChannel][hoverKey];
            ImGui.SetTooltip($"t {k.Time:0.##} · 值 {k.Value:0.###} · {Label(k.Interpolation)}");
        }

        // Key inspector.
        string sel = $"{dataKey}:{channelIndex}:{keyIndex}";
        if (keyIndex >= 0 && keyIndex < channel.Length)
        {
            var k = channel[keyIndex];
            if (selectionKey != sel) { selectionKey = sel; editTime = Text(k.Time); editValue = Text(k.Value); editInterpolation = k.Interpolation; }
            ImGui.BeginDisabled(!canEdit);
            ImGui.Text($"关键帧 {keyIndex}"); ImGui.SameLine();
            ImGui.SetNextItemWidth(90); ImGui.InputText("时间", ref editTime, 64); ImGui.SameLine();
            ImGui.SetNextItemWidth(90); ImGui.InputText("值", ref editValue, 64); ImGui.SameLine();
            ImGui.SetNextItemWidth(190);
            if (ImGui.BeginCombo("插值", Label(editInterpolation)))
            {
                foreach (var (name, label) in PreviewCurveMath.Editable)
                    if (ImGui.Selectable(label, name == editInterpolation)) editInterpolation = name;
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("关键帧的插值决定从它到下一个关键帧这一段。保持 / 线性在 RE4 里实测；事件、Hermite 来自 MHWilds 的同一格式测试，RE4 语料没有用到。");
            ImGui.SameLine();
            if (ImGui.Button("应用") && float.TryParse(editTime, NumberStyles.Float, CultureInfo.InvariantCulture, out float t) &&
                float.TryParse(editValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                request = Update(curve, channelIndex, keyIndex, t, isInt ? MathF.Round(v) : v, editInterpolation);
            ImGui.SameLine();
            ImGui.BeginDisabled(channel.Length <= 1);
            if (ImGui.Button("删除")) request = new JsonObject { ["path"] = curve.Path, ["channel"] = channelIndex, ["operation"] = "remove", ["keyIndex"] = keyIndex };
            ImGui.EndDisabled();
            if (channel.Length <= 1 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("通道至少保留一个关键帧。");
            ImGui.EndDisabled();
        }
        else ImGui.TextDisabled("点关键帧选中并拖动（松手提交）· 双击空白处在所选通道加关键帧");
        ImGui.End();
        return request;

        JsonObject Update(PreviewCurveInfo c, int ch, int index, float time, float value, string interpolation)
        {
            var body = new JsonObject { ["path"] = c.Path, ["channel"] = ch, ["operation"] = "update", ["keyIndex"] = index,
                ["time"] = Text(Math.Max(0, time)), ["value"] = Text(value), ["interpolation"] = interpolation };
            if (PreviewCurveMath.IsHermite(interpolation))
            {
                // Keep the key's tangents; a key switched to Hermite starts with flat tangents of a quarter
                // of the curve's span on each side (the backend never invents them).
                var src = c.Channels[ch].Keys[index];
                float flat = Math.Max(1, PreviewCurveMath.ParseFloat(c.Duration) / 4);
                body["outX"] = src.OutX ?? Text(flat); body["outY"] = src.OutY ?? "0";
                body["inX"] = src.InX ?? Text(-flat); body["inY"] = src.InY ?? "0";
            }
            return body;
        }
    }

    private static string Label(string interpolation) =>
        PreviewCurveMath.Editable.FirstOrDefault(e => e.Name == interpolation).Label ?? interpolation + "（未验证）";

    private static string Text(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
