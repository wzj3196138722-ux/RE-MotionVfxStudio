using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using ReeLib;
using ReeLib.Common;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Common;
using ReeLib.InternalAttributes;

namespace VfxStudio.LiveEditor;

internal sealed record FieldDescription(string Path, string Label, string Type, string Value, bool Editable, string? Reason = null, string[]? Choices = null, string? OwnerType = null);
internal sealed record EditableField(FieldDescription Description, Type ValueType, Action<object?>? Set);
internal sealed record EditableCurve(string Path, EfxClipData Data);
internal sealed record InspectionContent(IReadOnlyList<EditableField> Fields, IReadOnlyList<EditableCurve> Curves);

/// <summary>
/// Enumerates the actual field-backed EFX model, including inherited content, manually written
/// members, arrays and boxed structs. Caches/metadata are explicitly excluded. No compiled game
/// code or user expression is evaluated here; Rsz conditions use a bounded integer parser.
/// </summary>
internal static class ContentInspector
{
    private const int MaxFields = 50000;
    private const int MaxDepth = 32;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly HashSet<string> CacheNames = ["ParsedClip", "ParsedExpressions", "expressionTree", "parsedClips", "efxFile", "parentFile", "FileHandler", "Strings", "BitNames", "BitNameDict"];

    public static IReadOnlyList<EditableField> Describe(object root, EfxVersion version) => Describe(root, version, null);

    // The read-only UI needs fields and curves from the same model state. One walk
    // preserves the existing limits and serialization conditions for both views.
    public static InspectionContent Analyze(object root, EfxVersion version)
    {
        var curves = new List<EditableCurve>();
        return new(Describe(root, version, curves), curves);
    }

    public static IReadOnlyList<EditableCurve> Curves(object root, EfxVersion version)
    {
        var curves = new List<EditableCurve>();
        Describe(root, version, curves);
        return curves;
    }

    private static IReadOnlyList<EditableField> Describe(object root, EfxVersion version, List<EditableCurve>? curves)
    {
        var result = new List<EditableField>();
        var ancestors = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(() => root, null, root.GetType(), "", null, 0, true);
        return result;

        void Leaf(string path, Type type, object? value, Action<object?>? setter, string? reason, string? ownerType = null)
        {
            result.Add(new(new(path, path, type.FullName ?? type.Name, Format(value), setter != null && reason == null, reason,
                type.IsEnum ? ExpressionInspector.Choices(type) ?? Enum.GetNames(type) : type == typeof(bool) ? ["false", "true"] : null, ownerType), type, reason == null ? setter : null));
        }

        void Walk(Func<object?> get, Action<object?>? set, Type declaredType, string path, string? blocked, int depth, bool isRoot = false, string? ownerType = null)
        {
            if (result.Count >= MaxFields) throw new InvalidOperationException($"Inspector exceeds {MaxFields} fields; narrow the selected object. No fields were silently omitted.");
            object? value;
            try { value = get(); }
            catch (Exception ex) { Leaf(path, declaredType, "<unavailable>", null, ex.GetBaseException().Message); return; }
            Type type = value?.GetType() ?? declaredType;
            if (Scalar(type)) { Leaf(path, type, value, set, blocked, ownerType); return; }
            if (value == null) { Leaf(path, type, null, null, blocked ?? "Null complex object; a structural editor must create this node before its fields can be edited."); return; }
            if (depth >= MaxDepth) { Leaf(path, type, "<depth limit>", null, "Nested content exceeds 32 levels."); return; }
            if (!type.IsValueType && !ancestors.Add(value)) { Leaf(path, type, "<reference>", null, "Parent/shared reference; inspect its owning node."); return; }
            if (blocked == null && value is EfxClipData clip && curves != null && !curves.Any(c => ReferenceEquals(c.Data, clip)))
                curves.Add(new(path, clip));
            try
            {
                if (value is IList list)
                {
                    if (list.Count == 0) Leaf(path, type, "[]", null, blocked ?? "Empty collection; collection insertion is not exposed by this first editor.");
                    Type elementType = type.IsArray ? type.GetElementType()! : type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
                    for (int i = 0; i < list.Count; i++)
                    {
                        int index = i;
                        Func<object?> childGet = () => ((IList)get()!)[index];
                        Action<object?>? childSet = !list.IsReadOnly ? item =>
                        {
                            var collection = (IList)get()!;
                            collection[index] = item;
                            set?.Invoke(collection);
                        } : null;
                        Walk(childGet, childSet, elementType, path + $"[{i}]", blocked, depth + 1);
                    }
                    return;
                }
                int before = result.Count;
                foreach (var member in Members(value, version, isRoot))
                {
                    var local = member;
                    string nextPath = path.Length == 0 ? local.Name : path + "." + local.Name;
                    Func<object?> childGet = () => local.Get(get()!);
                    Action<object?>? childSet = local.Set == null || (type.IsValueType && set == null) ? null : item =>
                    {
                        var owner = get()!;
                        local.Set!(owner, item);
                        SynchronizeSwitches(owner, version);
                        if (type.IsValueType) set!(owner);
                    };
                    string? reason = blocked ?? local.Reason;
                    Walk(childGet, childSet, local.Type, nextPath, reason, depth + 1, ownerType: type.FullName);
                }
                if (result.Count == before) Leaf(path, type, "<no supported members>", null, "This complex type needs an explicit editor adapter.");
            }
            finally { if (!type.IsValueType) ancestors.Remove(value); }
        }
    }

    private sealed record Member(string Name, Type Type, Func<object, object?> Get, Action<object, object?>? Set, string? Reason);

    private static IEnumerable<Member> Members(object instance, EfxVersion version, bool root)
    {
        Type type = instance.GetType();
        if (instance is EfxFile file)
        {
            // Embedded PlayEmitter contents are real serialized content. Do not traverse FileHandler or parentFile.
            foreach (string name in new[] { "Header", "Entries", "Actions", "Bones", "BoneRelations", "ExpressionParameters", "FieldParameterValues", "EffectGroups", "UvarGroups" })
            {
                var property = typeof(EfxFile).GetProperty(name)!;
                yield return new(name, property.PropertyType, property.GetValue, null, null);
            }
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (Type? owner = type; owner != null && owner != typeof(object) && owner != typeof(BaseModel); owner = owner.BaseType)
        {
            var spans = new List<(string End, bool Active, string Reason)>();
            foreach (var field in owner.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(f => f.MetadataToken))
            {
                if (field.IsStatic || field.Name.StartsWith('<') || CacheNames.Contains(field.Name)) continue;
                if (!field.IsPublic && !field.GetCustomAttributes().Any(a => a.GetType().Namespace == typeof(RszGenerateAttribute).Namespace)) continue;
                if (!seen.Add(field.Name)) continue;
                foreach (var attribute in field.GetCustomAttributes())
                {
                    bool? active = null; string? end = null;
                    try
                    {
                        switch (attribute)
                        {
                            case RszVersionAttribute rule: active = VersionApplies(rule.Condition, instance, version); end = rule.EndAt; break;
                            case RszVersionExactAttribute exact: active = exact.Versions.Any(v => Convert.ToInt64(v) == (long)version); end = exact.EndAt; break;
                            case RszConditionalAttribute conditional: active = Condition.Evaluate(Expression(conditional.Condition), instance, version); end = conditional.EndAt; break;
                        }
                    }
                    catch { active = false; }
                    if (active.HasValue) spans.Add((end ?? field.Name, active.Value, "Inactive serialization condition: " + attribute.GetType().Name));
                }
                string? reason = spans.FirstOrDefault(s => !s.Active).Reason;
                spans.RemoveAll(s => s.End == field.Name);
                if (field.Name == "Version" || (owner == typeof(EFXAttribute) && field.Name is "type" or "UniqueID") ||
                    (instance is EFXEntryBase && field.Name is "index" or "nameHash")) reason = "Structural identity or derived metadata.";
                if (instance is EfxHeader && field.Name != "dimensionType") reason = "File identity or derived structure count; managed by the serializer.";
                if (instance is ReeLib.Efx.Structs.Basic.EFXAttributePlayEmitter && field.Name == "efxrSize")
                    reason = "Embedded EFX byte length; recalculated from its serialized content.";
                if (field.GetCustomAttributes().Any(a => a is RszArraySizeFieldAttribute or RszStringLengthFieldAttribute or RszByteSizeFieldAttribute))
                    reason = "Derived length/count; the serializer calculates it from content.";
                if (field.GetCustomAttributes().Any(a => a is RszStringHashAttribute or RszStringUTF8HashAttribute))
                    reason = "Derived string hash; edit the corresponding name.";
                if (instance is MdfPropertyTextureValue && field.Name is "pathLength" or "textureIndex") reason = "Derived texture table metadata; edit texturePath.";
                if (instance is MdfProperty material)
                {
                    if (field.Name == "texturePath" && material.parameterType != MaterialParameterType.Texture) reason = "Texture path is inactive for a non-texture material property.";
                    if (field.Name is "rawValue" or "mdfPropertyIndex" && material.parameterType == MaterialParameterType.Texture) reason = "Texture binding metadata; edit texturePath or the active TextureValue fields.";
                }
                if (instance is EfxClipHeader && field.Name == "frameCount") reason = "Curve segment frame count; changing it requires resizing and remapping the keyframe collection.";
                if (field.IsInitOnly) reason ??= "Read-only member.";
                if (field.IsDefined(typeof(RszIgnoreAttribute)) && !ManualField(instance, field.Name))
                    reason ??= "Runtime-only/cache member; no verified serialization writer.";
                if (instance is EffectGroup)
                    reason = "原生分组标识与归属；显示名称请使用分组备注，不写入 EFX。";
                if (instance is EFXEntryBase && field.Name == "Groups")
                    reason = "派生分组归属；显示名称请使用分组备注，不写入 EFX。";
                if (root && instance is EFXEntryBase && field.Name == "Attributes") continue;
                Action<object, object?>? fieldSetter = field.IsInitOnly ? null : field.SetValue;
                if (instance is EfxClipFrame && field.Name == "type")
                {
                    fieldSetter = (owner, newValue) =>
                    {
                        bool wasBezier = ((EfxClipFrame)owner).type == FrameInterpolationType.Bezier;
                        bool isBezier = (FrameInterpolationType)newValue! == FrameInterpolationType.Bezier;
                        if (wasBezier != isBezier) throw new InvalidOperationException("Changing Bezier mode requires resizing and remapping the tangent collection; edit existing tangent values or keep the current interpolation family.");
                        field.SetValue(owner, newValue);
                    };
                }
                yield return new(field.Name, field.FieldType, field.GetValue, fieldSetter, reason);
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!seen.Add(property.Name) || property.GetMethod == null || property.GetIndexParameters().Length != 0 || CacheNames.Contains(property.Name)) continue;
            if (property.DeclaringType == typeof(BaseModel) || property.Name is "IsTypeAttribute" or "TypeAttribute" or "IsParsed") continue;
            if (root && instance is EFXEntryBase && property.Name == "Attributes") continue;
            bool contentCollection = typeof(IList).IsAssignableFrom(property.PropertyType);
            if (property.SetMethod == null && !contentCollection) continue;
            Action<object, object?>? setter = property.SetMethod?.IsPublic == true ? property.SetValue : null;
            if (instance is BitSet && property.Name == nameof(BitSet.Bits))
            {
                setter = (owner, value) =>
                {
                    var bitset = (BitSet)owner;
                    var bits = (int[])value!;
                    if (bits.Length != (bitset.BitCount + 31) / 32) throw new ArgumentException("BitSet word count cannot change.");
                    int remaining = bitset.BitCount % 32;
                    if (remaining != 0 && bits.Length != 0 && ((uint)bits[^1] >> remaining) != 0)
                        throw new ArgumentException("BitSet contains flags outside the supported bit count.");
                    bitset.Bits = bits;
                };
            }
            // Preserve the exact integer bit representation, including for boxed array structs.
            if (instance is EfxClipFrame && property.Name == nameof(EfxClipFrame.IntValue))
            {
                var storage = typeof(EfxClipFrame).GetField("value", BindingFlags.Instance | BindingFlags.NonPublic)!;
                setter = (owner, value) => storage.SetValue(owner, BitConverter.Int32BitsToSingle((int)value!));
            }
            string? propertyReason = property.Name == "Version" ? "Format version is fixed for the loaded file." : null;
            if (instance is EffectGroup || instance is EFXEntryBase && property.Name == "Groups")
                propertyReason = "原生分组标识与派生归属；显示名称请使用分组备注，不写入 EFX。";
            if (instance is MdfProperty materialProperty &&
                (property.Name == "TextureValue" && materialProperty.parameterType != MaterialParameterType.Texture ||
                 property.Name is "VectorValue" or "RangeValue" && materialProperty.parameterType == MaterialParameterType.Texture))
                propertyReason = "This material view is inactive for the selected parameter type.";
            yield return new(property.Name, property.PropertyType, property.GetValue, setter, propertyReason);
        }
    }

    private static bool ManualField(object owner, string name) =>
        owner is EfxClipData && name is "clips" or "frames" or "interpolationData" ||
        owner is MdfProperty && name == "texturePath" ||
        owner is EFXExpressionParameter && name == "name" ||
        owner is EffectGroup && name == "groupName" ||
        owner is EFXUvarGroup && name == "uvarType" ||
        owner is EFXFieldParameterValue && name is "value_ukn2" or "value_ukn3" or "value_ukn4" or "value_ukn5" or "value_ukn6" or "wilds_unkn0" or "name" or "filePath" ||
        name == "efxrData" && owner is EFXAttribute;

    private static string Expression(IEnumerable<object> parts) => string.Join(" ", parts.Select(v => v is Enum ? Convert.ToInt64(v).ToString(Invariant) : Convert.ToString(v, Invariant)));

    private static bool VersionApplies(object[] condition, object owner, EfxVersion version)
    {
        string expression = Expression(condition);
        if (condition.Length == 1) expression = "Version >= " + expression;
        else if (condition.Length == 2 && Regex.IsMatch(condition[0]?.ToString() ?? "", @"^(<|>|<=|>=|==|=|!=)$")) expression = "Version " + expression;
        return Condition.Evaluate(expression, owner, version);
    }

    private static void SynchronizeSwitches(object owner, EfxVersion version)
    {
        if (owner is MdfProperty material && material.parameterType == MaterialParameterType.Texture) material.texturePath ??= string.Empty;
        foreach (var field in owner.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var rule = field.GetCustomAttribute<RszSwitchAttribute>();
            if (rule == null) continue;
            var condition = new List<object>();
            Type? selected = null;
            foreach (object arg in rule.Args)
            {
                if (arg is Type subtype)
                {
                    if (condition.Count == 0 || Condition.Evaluate(Expression(condition), owner, version)) { selected = subtype; break; }
                    condition.Clear();
                }
                else condition.Add(arg);
            }
            if (selected == null) throw new InvalidOperationException("The discriminator has no supported serialized variant: " + field.Name);
            if (field.GetValue(owner)?.GetType() == selected) continue;
            object? created = selected.GetConstructor([typeof(EfxVersion)]) is ConstructorInfo constructor
                ? constructor.Invoke([version]) : Activator.CreateInstance(selected);
            if (created == null) throw new InvalidOperationException("Cannot create selected variant: " + selected.Name);
            field.SetValue(owner, created);
        }
    }

    private static bool Scalar(Type type) => type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal);

    public static string Format(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        float f => f.ToString("R", Invariant),
        double d => d.ToString("R", Invariant),
        EfxExpressionFunction or BinaryExpressionOperator => ExpressionInspector.EnumText(value),
        IFormattable formattable => formattable.ToString(null, Invariant) ?? "",
        _ => value.ToString() ?? ""
    };

    public static object? Parse(string text, Type type)
    {
        if (type == typeof(string)) return text;
        if (type == typeof(bool)) return bool.Parse(text);
        if (ExpressionInspector.Choices(type) != null) return ExpressionInspector.ParseEnum(text, type);
        if (type.IsEnum) return Enum.Parse(type, text, ignoreCase: false);
        if (type == typeof(char)) return text.Length == 1 ? text[0] : throw new FormatException("A character requires one character.");
        object? result = Convert.ChangeType(text, type, Invariant);
        if (result is float f && !float.IsFinite(f) || result is double d && !double.IsFinite(d))
            throw new FormatException("Non-finite floating point values cannot be entered.");
        return result;
    }
}

/// <summary>Integer/boolean subset used by the format's RszVersion and RszConditional declarations.</summary>
internal sealed class Condition
{
    private readonly string[] tokens;
    private readonly object instance;
    private readonly EfxVersion version;
    private int index;
    private Condition(string expression, object instance, EfxVersion version)
    {
        tokens = Regex.Matches(expression, @"0[xX][0-9a-fA-F]+|\d+|[A-Za-z_]\w*|&&|\|\||==|!=|<=|>=|<<|>>|[()!~&|^+\-<>*=]").Select(m => m.Value).ToArray();
        string compact = Regex.Replace(expression, @"\s+", "");
        if (string.Concat(tokens) != compact || tokens.Length > 256) throw new FormatException("Unsupported serialization condition.");
        this.instance = instance; this.version = version;
    }
    public static bool Evaluate(string expression, object instance, EfxVersion version)
    {
        var p = new Condition(expression, instance, version);
        long value = p.Or();
        if (p.index != p.tokens.Length) throw new FormatException("Unconsumed condition tokens.");
        return value != 0;
    }
    private bool Take(string token) { if (index < tokens.Length && tokens[index] == token) { index++; return true; } return false; }
    private long Or() { long x = And(); while (Take("||")) { long y = And(); x = x != 0 || y != 0 ? 1 : 0; } return x; }
    private long And() { long x = BitOr(); while (Take("&&")) { long y = BitOr(); x = x != 0 && y != 0 ? 1 : 0; } return x; }
    private long BitOr() { long x = BitXor(); while (Take("|")) x |= BitXor(); return x; }
    private long BitXor() { long x = BitAnd(); while (Take("^")) x ^= BitAnd(); return x; }
    private long BitAnd() { long x = Compare(); while (Take("&")) x &= Compare(); return x; }
    private long Compare()
    {
        long x = Shift();
        while (index < tokens.Length)
        {
            string op = tokens[index];
            if (op is not ("==" or "=" or "!=" or "<" or ">" or "<=" or ">=")) break;
            index++; long y = Shift();
            x = (op switch { "==" or "=" => x == y, "!=" => x != y, "<" => x < y, ">" => x > y, "<=" => x <= y, ">=" => x >= y, _ => false }) ? 1 : 0;
        }
        return x;
    }
    private long Shift() { long x = Add(); while (index < tokens.Length) { if (Take("<<")) x <<= checked((int)Add()); else if (Take(">>")) x >>= checked((int)Add()); else break; } return x; }
    private long Add() { long x = Unary(); while (index < tokens.Length) { if (Take("+")) x = checked(x + Unary()); else if (Take("-")) x = checked(x - Unary()); else break; } return x; }
    private long Unary()
    {
        if (Take("!")) return Unary() == 0 ? 1 : 0;
        if (Take("~")) return ~Unary();
        if (Take("-")) return -Unary();
        if (Take("+")) return Unary();
        if (Take("(")) { long x = Or(); if (!Take(")")) throw new FormatException("Missing closing parenthesis."); return x; }
        if (index >= tokens.Length) throw new FormatException("Incomplete condition.");
        string token = tokens[index++];
        if (token == "Version") return (long)version;
        if (token == "true") return 1;
        if (token == "false") return 0;
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt64(token[2..], 16);
        if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)) return number;
        var field = instance.GetType().GetField(token, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        object? value = field?.GetValue(instance) ?? instance.GetType().GetProperty(token)?.GetValue(instance);
        return value == null ? throw new FormatException("Unknown condition member: " + token) : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
