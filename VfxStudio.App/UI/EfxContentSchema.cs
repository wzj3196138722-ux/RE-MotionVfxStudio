using System.Reflection;
using ReeLib.Efx;
using ReeLib.InternalAttributes;

namespace VfxStudio.App.UI;

/// <summary>Fields consumed by the EFX generator, including inherited and versioned spans.</summary>
internal static class EfxContentSchema
{
    internal sealed record Field(FieldInfo Info, bool Applicable, string Condition);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, EfxVersion), IReadOnlyList<Field>> Cache = new();

    internal static IReadOnlyList<Field> Describe(Type type, EfxVersion version) =>
        Cache.GetOrAdd((type, version), key => Build(key.Item1, key.Item2));

    private static IReadOnlyList<Field> Build(Type type, EfxVersion version)
    {
        var result = new List<Field>();
        for (Type? owner = type; owner != null && owner != typeof(object) && owner != typeof(EFXAttribute);
             owner = owner.BaseType)
        {
            var spans = new List<(string End, bool Applies, string Reason)>();
            foreach (var field in owner.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(f => f.MetadataToken))
            {
                if (field.IsStatic) continue;
                if (field.IsDefined(typeof(RszIgnoreAttribute), false))
                {
                    result.Add(new Field(field, false, "RszIgnore: manual serialization or internal state not classified"));
                    spans.RemoveAll(s => s.End == field.Name);
                    continue;
                }
                foreach (var attr in field.GetCustomAttributes(false))
                {
                    bool? applies = attr switch
                    {
                        RszVersionAttribute rule => CheckVersion(rule.Condition, version),
                        RszVersionExactAttribute exact => exact.Versions.Any(v => Convert.ToInt64(v) == Convert.ToInt64(version)),
                        // Runtime condition expressions need a separate evaluator.
                        // Keep them visible as read-only until that is implemented.
                        RszConditionalAttribute => false,
                        _ => null
                    };
                    if (applies.HasValue)
                    {
                        string end = attr switch
                        {
                            RszVersionAttribute rule => rule.EndAt ?? field.Name,
                            RszVersionExactAttribute exact => exact.EndAt ?? field.Name,
                            RszConditionalAttribute conditional => conditional.EndAt ?? field.Name,
                            _ => field.Name
                        };
                        spans.Add((end, applies.Value, attr.ToString() ?? attr.GetType().Name));
                    }
                }
                bool applicable = spans.All(s => s.Applies);
                result.Add(new Field(field, applicable,
                    applicable ? "" : string.Join("; ", spans.Where(s => !s.Applies).Select(s => s.Reason))));
                spans.RemoveAll(s => s.End == field.Name);
            }
        }
        return result;
    }

    private static bool CheckVersion(object[] condition, EfxVersion version)
    {
        if (condition.Length == 0) return true;
        try
        {
            long actual = Convert.ToInt64(version);
            if (condition.Length == 1) return actual >= Convert.ToInt64(condition[0]);
            string op = condition[0].ToString() ?? "";
            long bound = Convert.ToInt64(condition[1]);
            return op switch
            {
                ">" => actual > bound,
                ">=" => actual >= bound,
                "<" => actual < bound,
                "<=" => actual <= bound,
                "=" or "==" => actual == bound,
                "!=" => actual != bound,
                _ => false
            };
        }
        catch (FormatException) { return false; }
        catch (InvalidCastException) { return false; }
    }
}
