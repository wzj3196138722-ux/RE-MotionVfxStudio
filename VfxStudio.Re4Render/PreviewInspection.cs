using System.Globalization;

namespace VfxStudio.Re4Render;

public sealed record PreviewAttributeInfo(int Index, string Type, string ClrType);
public sealed record PreviewFieldInfo(string Path, string Label, string Type, string Value, bool Editable,
    string? Reason = null, string[]? Choices = null, string? OwnerType = null, PreviewFieldSemantic? Semantic = null);
public sealed record PreviewColorChannel(string Component, string Path, int Value);
public sealed record PreviewColorGroup(string Path, string Label, string Storage, int Scale, bool Hdr,
    bool Editable, string? Reason, PreviewColorChannel[] Channels);
public sealed record PreviewInspection(string DocumentId, long Revision, int EntryPosition, int AttributeIndex,
    PreviewFieldInfo[] Fields, PreviewColorGroup[] ColorGroups, int CurveCount,
    PreviewCurveInfo[]? Curves = null, string[]? ClipChannels = null);
/// <summary>A curve key as the backend reports it (exact protocol text). Interpolation: Type1 holds the value until
/// the next key, Type2 is linear (both measured in re4.exe); Bezier carries tangents.</summary>
public sealed record PreviewCurveKey(int Index, string Time, string Value, string Interpolation,
    string? OutX = null, string? OutY = null, string? InX = null, string? InY = null);
public sealed record PreviewCurveChannel(int Index, string ValueType, PreviewCurveKey[] Keys);
public sealed record PreviewCurveInfo(string Path, bool Editable, string? Reason, string Duration, PreviewCurveChannel[] Channels);
public sealed record PreviewFieldEdit(string Path, string Value);
public sealed record PreviewFieldBatch(PreviewInspection Inspection, PreviewFieldEdit[] Fields);

/// <summary>One attribute's revision-bound UI draft; values are exact protocol text, never an EFX model.</summary>
public sealed class PreviewPropertyDraft(PreviewInspection inspection)
{
    public PreviewInspection Inspection { get; } = inspection;
    private readonly Dictionary<string,PreviewFieldInfo> fields=inspection.Fields.ToDictionary(f=>f.Path,StringComparer.Ordinal);
    private readonly Dictionary<string,string> values=inspection.Fields.ToDictionary(f=>f.Path,f=>f.Value,StringComparer.Ordinal);
    private readonly HashSet<string> changed=new(StringComparer.Ordinal);
    private PreviewFieldEdit[]? cachedChanges;
    public string Value(string path)=>values[path];
    public bool Dirty=>changed.Count!=0;
    public bool Matches(PreviewEditorState? state)=>state?.Manifest is {} m && m.DocumentId==Inspection.DocumentId &&
        m.EntryPosition==Inspection.EntryPosition && state.Revision==Inspection.Revision;
    public void Set(string path,string value)
    {
        var field=fields[path];
        if(!field.Editable) throw new InvalidOperationException(field.Reason??"字段只读。");
        if(values[path]==value) return;
        values[path]=value;
        if(value==field.Value) changed.Remove(path);else changed.Add(path);
        cachedChanges=null;
    }
    public PreviewFieldEdit[] Changes()=>cachedChanges??=Inspection.Fields.Where(f=>changed.Contains(f.Path))
        .Select(f=>new PreviewFieldEdit(f.Path,values[f.Path])).ToArray();
    public void Reset() {foreach(var f in Inspection.Fields) values[f.Path]=f.Value;changed.Clear();cachedChanges=null;}

    public void SetColor(PreviewColorGroup color, ReadOnlySpan<float> normalized)
    {
        if(color.Storage!="rgba8" || color.Scale!=255 || color.Hdr || !color.Editable || normalized.Length!=4 || color.Channels.Length!=4)
            throw new ArgumentException("此颜色组不能使用 RGBA8 控件。");
        var edits=new List<(string Path,string Value)>();
        for(int i=0;i<4;i++)
        {
            if(!float.IsFinite(normalized[i]) || normalized[i]<0 || normalized[i]>1) throw new ArgumentException("颜色通道应在 0–1 之间。");
            var field=fields[color.Channels[i].Path];
            if(!field.Editable) throw new InvalidOperationException("颜色通道只读。");
            edits.Add((field.Path,((int)MathF.Round(normalized[i]*255)).ToString(CultureInfo.InvariantCulture)));
        }
        foreach(var edit in edits) Set(edit.Path,edit.Value);
    }
}
