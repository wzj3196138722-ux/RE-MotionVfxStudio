using System.Globalization;

namespace VfxStudio.Re4Render;

/// <summary>Which fields of one attribute the engine reads with the current values, from the When conditions in
/// the field semantics. Values come from the caller (the draft), so an edited mode field updates the rest before it
/// is applied. A condition whose field is missing counts as met: nothing is shown as inactive on a guess.</summary>
public static class PreviewFieldActivity
{
    /// <summary>Why the field does nothing right now, or null when it takes effect (or has no condition).</summary>
    public static string? Inactive(PreviewFieldInfo field, Func<string, string?> value)
    {
        var when = field.Semantic?.When;
        return when == null || Met(when, value) != false ? null : Describe(when);
    }

    /// <summary>Mode fields: path of each field some condition tests, with the paths whose activity it decides.</summary>
    public static IReadOnlyDictionary<string, string[]> Controls(IEnumerable<PreviewFieldInfo> fields)
    {
        var all = fields.ToArray();
        var known = all.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in all)
            if (f.Semantic?.When is { } when)
                foreach (string mode in Referenced(when).Where(known.Contains).Distinct())
                {
                    if (!map.TryGetValue(mode, out var list)) map[mode] = list = [];
                    list.Add(f.Path);
                }
        return map.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Field paths a condition reads.</summary>
    public static IEnumerable<string> Referenced(PreviewFieldCondition c)
    {
        if (c.Field != null) yield return c.Field;
        foreach (var child in (c.Any ?? []).Concat(c.All ?? []))
            foreach (string path in Referenced(child)) yield return path;
    }

    /// <summary>True / false, or null when a tested field is missing or its value cannot be read.</summary>
    public static bool? Met(PreviewFieldCondition c, Func<string, string?> value)
    {
        bool? result = true;
        if (c.Field != null)
        {
            string? text = value(c.Field);
            if (text == null) return null;
            if (c.Shift is int shift)
            {
                if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint flags)) return null;
                int width = c.Width ?? 1;
                uint mask = width >= 32 ? uint.MaxValue : (1u << width) - 1;
                text = ((flags >> shift) & mask).ToString(CultureInfo.InvariantCulture);
            }
            if (c.In != null && !c.In.Any(v => Same(text, v))) result = false;
            if (c.Not != null && c.Not.Any(v => Same(text, v))) result = false;
            if (c.NonZero == true && (text is "false" or "" || IsZero(text))) result = false;
        }
        if (c.All != null)
            foreach (var child in c.All)
            {
                var r = Met(child, value);
                if (r == null) return null;
                if (r == false) result = false;
            }
        if (c.Any != null)
        {
            var rs = c.Any.Select(child => Met(child, value)).ToArray();
            if (rs.Any(r => r == null)) return null;
            if (!rs.Any(r => r == true)) result = false;
        }
        return result;
    }

    private static string Describe(PreviewFieldCondition c) =>
        c.Why ?? string.Join("；", (c.Any ?? []).Concat(c.All ?? []).Select(Describe).Prepend(c.Field == null ? null : c.Field + " 不满足条件")
            .Where(s => s != null));

    private static bool Same(string text, string expected) =>
        string.Equals(text, expected, StringComparison.Ordinal) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
        double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && a == b;

    private static bool IsZero(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v == 0;
}
