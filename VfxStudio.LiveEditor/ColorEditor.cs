using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using ReeLib;
using ReeLib.Efx;
using via = ReeLib.via;

namespace VfxStudio.LiveEditor;

internal sealed record ColorChannelInfo(string Component, string Path, int Value);
internal sealed record ColorGroupInfo(string Path, string Label, string Storage, int Scale, bool Hdr,
    bool Editable, string? Reason, ColorChannelInfo[] Channels, double[] Normalized, string Hex);

/// <summary>
/// Groups only the model's explicit via.Color type. Its four serialized bytes are not
/// arbitrary vectors or HDR intensity values. The existing scalar inspector remains intact.
/// </summary>
internal static class ColorEditor
{
    private static readonly string[] Components = ["R", "G", "B", "A"];
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<ColorGroupInfo> Inspect(object root, EfxVersion version)
        => Inspect(root, ContentInspector.Describe(root, version));

    internal static IReadOnlyList<ColorGroupInfo> Inspect(object root, IReadOnlyList<EditableField> describedFields)
    {
        var fields = describedFields.ToDictionary(field => field.Description.Path, StringComparer.Ordinal);
        var colors = new List<ColorGroupInfo>();
        foreach (var field in fields.Values)
        {
            string fieldPath = field.Description.Path;
            if (fieldPath != "R" && !fieldPath.EndsWith(".R", StringComparison.Ordinal)) continue;
            string path = fieldPath == "R" ? "" : fieldPath[..^2];
            if (!TryGetValue(root, path, out var value) || value is not via.Color color) continue;
            string prefix = path.Length == 0 ? "" : path + ".";
            var channels = new ColorChannelInfo[4];
            string? reason = null;
            bool complete = true;
            int[] raw = [color.R, color.G, color.B, color.A];
            for (int i = 0; i < Components.Length; i++)
            {
                string channelPath = prefix + Components[i];
                if (!fields.TryGetValue(channelPath, out var channel) || channel.ValueType != typeof(int)) { complete = false; break; }
                channels[i] = new(Components[i], channelPath, raw[i]);
                if (!channel.Description.Editable || channel.Set == null) reason ??= channel.Description.Reason ?? "Color channel is read-only.";
            }
            if (!complete) continue;
            if (!fields.TryGetValue(prefix + "rgba", out var packed) || packed.ValueType != typeof(uint) || !packed.Description.Editable || packed.Set == null)
                reason ??= packed?.Description.Reason ?? "Packed color storage is read-only.";
            colors.Add(new(path, path.Length == 0 ? "颜色" : path, "rgba8", 255, false, reason == null, reason,
                channels, raw.Select(channel => channel / 255.0).ToArray(), "#" + string.Concat(raw.Select(channel => channel.ToString("X2", Invariant)))));
        }
        return colors;
    }

    public static ColorEditPlan Prepare(object root, EfxVersion version, string path, JsonArray values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var group = Inspect(root, version).SingleOrDefault(color => color.Path == path)
            ?? throw new ArgumentException("The selected path is not an explicit RGBA color: " + path);
        if (!group.Editable) throw new InvalidOperationException(group.Reason ?? "Color is read-only.");
        if (values.Count != 4) throw new ArgumentException("A color edit requires exactly four RGBA values.");
        var raw = new int[4];
        for (int i = 0; i < raw.Length; i++)
        {
            if (values[i] is not JsonValue item) throw new ArgumentException("RGBA values must be numbers or numeric strings.");
            string text = item.TryGetValue<string>(out var supplied) ? supplied : item.ToJsonString();
            if (!decimal.TryParse(text, NumberStyles.Float, Invariant, out decimal number) ||
                number < 0 || number > 255 || decimal.Truncate(number) != number)
                throw new ArgumentException("RGBA byte channels require integers from 0 through 255; no values were changed.");
            raw[i] = (int)number;
        }
        string packedPath = path.Length == 0 ? "rgba" : path + ".rgba";
        var packed = ContentInspector.Describe(root, version).Single(field => field.Description.Path == packedPath);
        uint rgba = (uint)raw[0] | (uint)raw[1] << 8 | (uint)raw[2] << 16 | (uint)raw[3] << 24;
        // A single packed assignment also makes the four-byte update indivisible at
        // the model level. EditorEngine owns the one Mutate/Undo transaction and rollback.
        return new ColorEditPlan(path, raw, () => packed.Set!(rgba));
    }

    // This follows only paths already emitted by ContentInspector, and never discovers
    // or classifies colors from member names alone. Arrays and boxed structs are supported.
    private static bool TryGetValue(object root, string path, out object? value)
    {
        value = root;
        if (path.Length == 0) return true;
        string[] parts = path.Split('.');
        if (parts.Length > 32 || path.Length > 8192) return false;
        try
        {
            foreach (string part in parts)
            {
                if (value == null || part.Length == 0) return false;
                int bracket = part.IndexOf('[');
                string name = bracket < 0 ? part : part[..bracket];
                if (name.Length != 0)
                {
                    var type = value.GetType();
                    var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null) value = field.GetValue(value);
                    else
                    {
                        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                        if (property?.GetMethod == null || property.GetIndexParameters().Length != 0) return false;
                        value = property.GetValue(value);
                    }
                }
                while (bracket >= 0)
                {
                    int end = part.IndexOf(']', bracket + 1);
                    if (end < 0 || !int.TryParse(part.AsSpan(bracket + 1, end - bracket - 1), NumberStyles.None, Invariant, out int index) ||
                        value is not IList list || index < 0 || index >= list.Count) return false;
                    value = list[index];
                    if (end == part.Length - 1) break;
                    if (part[end + 1] != '[') return false;
                    bracket = end + 1;
                }
            }
            return true;
        }
        catch (Exception error) when (error is TargetInvocationException or ArgumentException or InvalidOperationException or MemberAccessException)
        { value = null; return false; }
    }
}

internal sealed class ColorEditPlan
{
    private readonly string path;
    private readonly int[] expected;
    private readonly Action apply;
    internal ColorEditPlan(string path, int[] expected, Action apply) { this.path = path; this.expected = expected; this.apply = apply; }
    public void Apply() => apply();
    public void Verify(object reopenedRoot, EfxVersion version)
    {
        var actual = ColorEditor.Inspect(reopenedRoot, version).SingleOrDefault(group => group.Path == path);
        if (actual == null || actual.Storage != "rgba8" || !actual.Channels.Select(channel => channel.Value).SequenceEqual(expected))
            throw new InvalidDataException("The serializer did not preserve all four color channels. The complete color edit was rolled back.");
    }
}
