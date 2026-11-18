using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Hexa.NET.ImGui;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

/// <summary>The material property table of the attribute open in the property panel (TypeMesh <c>properties</c>,
/// material Types' <c>material.properties</c>), named and checked against a reference .mdf2, with overrides added
/// from that material or removed. The reference defaults to the attribute's own material path, read through the
/// preview's resource lookup; another .mdf2 file can be chosen by hand.
///
/// A Range override's four floats are (base, random, min, max): re4.exe draws random from [min, max] at birth and
/// sends base + random (docs/development/RE4-EFX-RUNTIME.zh-CN.md §4.3.11); a non-zero flags clamps to [0, 1].</summary>
internal sealed class PreviewMaterialSection
{
    public const string ValuesGroup = "材质参数", StructureGroup = "材质参数结构（只读）";
    private sealed record Override(int Position, uint Hash, int MdfIndex, int Count, string Type, string TexturePath);
    private sealed record Table(string Prefix, string ListPath, string MaterialPath, Override[] Overrides);

    /// <summary>Reads the material at an EFX material path (set by the window: the preview's resource source).</summary>
    public Func<string, PreviewMaterialReference>? Loader { get; set; }
    private Table? table;
    private string? owner;                 // document:attribute the manual choice belongs to
    private string manualInput = "", filter = "";
    private string? manual;                // a hand-chosen .mdf2 file, or null for the material path
    private string? loadedKey;
    private Task<PreviewMaterialReference>? loading;
    private PreviewMaterialReference? reference;
    private string? error;
    private bool relabel;

    public bool Present => table != null;

    /// <summary>Finds the table in a fresh inspection. <paramref name="ownerKey"/> identifies the open attribute: a
    /// hand-chosen reference is kept while it stays the same.</summary>
    public void Detect(PreviewInspection inspection, IReadOnlyDictionary<string, PreviewFieldInfo> byPath, string ownerKey)
    {
        if (owner != ownerKey) { owner = ownerKey; manual = null; manualInput = ""; filter = ""; }
        table = null;
        foreach (string prefix in new[] { "", "material." })
        {
            string? materialPath = byPath.GetValueOrDefault(prefix + "MaterialPath")?.Value ?? byPath.GetValueOrDefault(prefix + "mdfPath")?.Value;
            if (materialPath == null) continue;
            bool hasList = inspection.Fields.Any(f => f.Path.StartsWith(prefix + "properties[", StringComparison.Ordinal)) ||
                new[] { "propertiesDataSize", "propertyCount", "propDataSize" }.Any(n => byPath.ContainsKey(prefix + n));
            if (!hasList) continue;
            var overrides = new List<Override>();
            for (int i = 0; byPath.TryGetValue($"{prefix}properties[{i}].PropertyNameUTF8Hash", out var hash); i++)
            {
                string Field(string leaf) => byPath.GetValueOrDefault($"{prefix}properties[{i}].{leaf}")?.Value ?? "";
                overrides.Add(new(i, uint.Parse(hash.Value, CultureInfo.InvariantCulture),
                    int.TryParse(Field("mdfPropertyIndex"), CultureInfo.InvariantCulture, out int index) ? index : -1,
                    int.TryParse(Field("mdfParameterValueCount"), CultureInfo.InvariantCulture, out int count) ? count : 0,
                    Field("parameterType"), Field("texturePath")));
            }
            table = new(prefix, prefix + "properties", materialPath, overrides.ToArray());
            break;
        }
    }

    /// <summary>Group, label and order for a field of the table, or null for other fields.</summary>
    public (string Group, string? Label, int Order)? Place(string path)
    {
        if (table == null) return null;
        var m = Regex.Match(path, "^" + Regex.Escape(table.Prefix) + @"properties\[(\d+)\]\.(.*)$");
        if (!m.Success) return null;
        int position = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        string rest = m.Groups[2].Value;
        var item = position < table.Overrides.Length ? table.Overrides[position] : null;
        string name = item == null ? $"#{position}" : NameOf(item);
        // A texture override has only its path; the engine clamps and uploads values only for Float and Range (3d334e0, 3d30350).
        bool texture = item?.Type == "Texture";
        string? label = rest switch
        {
            "VectorValue" when !texture => item?.Type == "Range" ? "值（base · random · min · max）" : item?.Count == 1 ? "值（引擎读 X）" : "值",
            "flags" when !texture => "0-1 钳制",
            "texturePath" when texture => "贴图",
            _ => null
        };
        // VectorValue's components are one row whose path is the VectorValue prefix.
        if (rest.StartsWith("VectorValue.", StringComparison.Ordinal)) label = null;
        return label != null ? (ValuesGroup, $"{name} · {label}", -100000 + position * 10)
            : (StructureGroup, $"{name} · {rest}", 400000 + position * 10);
    }

    /// <summary>True once after a reference has finished loading: rows need new labels.</summary>
    public bool TakeRelabel() { bool r = relabel; relabel = false; return r; }

    private string NameOf(Override item) => reference?.Find(item.Hash)?.Name ?? (item.Hash == 3072153074 ? "BaseMap" : "#" + item.Hash);

    private void Load()
    {
        if (table == null) return;
        string key = manual != null ? "file:" + manual : "path:" + table.MaterialPath;
        if (key == loadedKey) return;
        loadedKey = key; reference = null; error = null;
        string? file = manual; string materialPath = table.MaterialPath; var loader = Loader;
        loading = Task.Run(() => file != null ? PreviewMaterialReference.FromFile(file)
            : string.IsNullOrWhiteSpace(materialPath) ? throw new InvalidOperationException("这个属性没有材质路径，请手动指定 .mdf2。")
            : loader?.Invoke(materialPath) ?? throw new InvalidOperationException("预览没有资源来源，请手动指定 .mdf2。"));
    }

    /// <summary>Draws the section. Returns a structure request (add or remove an override) or null.</summary>
    public PreviewStructureRequest? Draw(int entryPosition, int attributeIndex, bool editable)
    {
        if (table == null) return null;
        Load();
        if (loading is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully) reference = done.Result;
            else error = done.Exception?.GetBaseException().Message ?? "读取失败";
            loading = null; relabel = true;
        }
        PreviewStructureRequest? request = null;
        if (!ImGui.CollapsingHeader($"材质参数（{table.Overrides.Length} 项覆盖）###材质参数", ImGuiTreeNodeFlags.DefaultOpen)) return null;
        // Reference line.
        if (loading != null) ImGui.TextDisabled("正在读取参考材质…");
        else if (reference != null)
        {
            ImGui.TextUnformatted($"参考：{Path.GetFileName(reference.Source.Split(" :: ")[^1])} · {reference.MaterialName}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(reference.Source.Replace("%", "%%") + "\n参数名、可添加的项和下标都以它为准。");
        }
        else if (error != null) { ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, .75f, .35f, 1)); ImGui.TextWrapped(error.Replace("%", "%%")); ImGui.PopStyleColor(); }
        ImGui.SetNextItemWidth(Math.Max(80, ImGui.GetContentRegionAvail().X - 120));
        ImGui.InputTextWithHint("##参考mdf2", manual == null ? "默认按材质路径读取；可填 .mdf2.32 文件" : manual, ref manualInput, 1024);
        ImGui.SameLine();
        if (ImGui.Button("载入")) { manual = manualInput.Trim().Trim('"'); if (manual.Length == 0) manual = null; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("用这个 .mdf2 作参考；留空则回到属性自己的材质路径");
        if (manual != null) { ImGui.SameLine(); if (ImGui.SmallButton("还原")) { manual = null; manualInput = ""; } }

        // Existing overrides, checked against the reference.
        if (table.Overrides.Length > 0 && ImGui.BeginTable("##材质覆盖", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("参数", ImGuiTableColumnFlags.WidthStretch, .5f);
            ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthStretch, .3f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 44);
            foreach (var item in table.Overrides)
            {
                ImGui.PushID(item.Position);
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                bool texture = item.Type == "Texture";
                string? mismatch = reference?.Mismatch(item.Hash, item.MdfIndex, item.Count, texture);
                if (mismatch != null) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, .75f, .35f, 1));
                ImGui.TextUnformatted(NameOf(item));
                if (mismatch != null) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip((mismatch ?? (reference != null ? "与参考材质一致" : "参考材质未读取")) +
                    $"\n下标 {item.MdfIndex} · 分量 {item.Count} · 哈希 {item.Hash}");
                ImGui.TableNextColumn();
                ImGui.TextDisabled(texture ? "贴图" : item.Type == "Range" ? "随机区间" : item.Count == 4 ? "float4" : "float");
                ImGui.TableNextColumn();
                ImGui.BeginDisabled(!editable);
                if (ImGui.SmallButton("删除"))
                    request = new("material_parameter", entryPosition, attributeIndex,
                        Body: new() { ["op"] = "remove", ["listPath"] = table.ListPath, ["index"] = item.Position }, Label: "删除材质参数 " + NameOf(item));
                ImGui.EndDisabled();
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        // Add from the reference: parameters and texture slots the table does not override yet.
        ImGui.BeginDisabled(!editable || reference == null);
        if (ImGui.Button("添加材质参数…")) { filter = ""; ImGui.OpenPopup("添加材质参数"); }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(reference == null ? "先读到参考材质" : "从参考材质里选一个参数覆盖；初值取材质自己的值，下标与分量数按材质填写");
        if (ImGui.BeginPopup("添加材质参数") && reference != null)
        {
            ImGui.SetNextItemWidth(320); ImGui.InputTextWithHint("##筛选", "筛选参数名", ref filter, 128);
            var present = table.Overrides.Select(o => o.Hash).ToHashSet();
            ImGui.BeginChild("##候选", new Vector2(420, 320));
            foreach (var slot in reference.Slots)
            {
                if (present.Contains(slot.Hash) || filter.Length > 0 && !slot.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                string value = slot.Texture ? slot.TexturePath : slot.ComponentCount == 4
                    ? $"({slot.Value.X:0.###}, {slot.Value.Y:0.###}, {slot.Value.Z:0.###}, {slot.Value.W:0.###})" : slot.Value.X.ToString("0.###", CultureInfo.InvariantCulture);
                if (ImGui.Selectable($"{slot.Name}##{slot.Hash}"))
                {
                    request = new("material_parameter", entryPosition, attributeIndex, Body: PreviewMaterialReference.AddBody(slot, table.ListPath),
                        Label: "添加材质参数 " + slot.Name);
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{slot.Kind} · 材质值 {value}".Replace("%", "%%") +
                    (slot.Texture ? "" : $"\n下标 {slot.Index}" + (slot.ComponentCount == 1 ? "；作为随机区间 (0, 0, 值, 值) 添加，即不随机" : "")));
                ImGui.SameLine(260); ImGui.TextDisabled(slot.Kind);
            }
            ImGui.EndChild();
            ImGui.EndPopup();
        }
        ImGui.Separator();
        return request;
    }
}
