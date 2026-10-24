using System.Globalization;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Common;

namespace VfxStudio.LiveEditor;

internal sealed record ExpressionValueInfo(string Path, string Label, string Value);
internal sealed record ExpressionCurveInfo(int Index, string Channel, string Assignment, string Formula,
    string? Reason, ExpressionValueInfo[] Values, string? Axis, double XMin, double XMax, double[] Samples, string? PlotReason);

// Numeric opcode definitions verified against the bundled Blender plugin's REE-Lib.dll.
// The legacy Core enum names differ; keep the serialized numbers, translate at the live editor boundary.
internal static class ExpressionInspector
{
    private sealed record Function(string Name, string[] Args);
    private static readonly Dictionary<int, Function> Functions = new()
    {
        [0] = new("Sin", ["x"]), [1] = new("Cos", ["x"]), [2] = new("Asin", ["x"]), [3] = new("Acos", ["x"]),
        [4] = new("Floor", ["x"]), [5] = new("Ceil", ["x"]), [6] = new("Log", ["x"]), [7] = new("Log10", ["x"]),
        [8] = new("Exp", ["x"]), [9] = new("Abs", ["x"]), [10] = new("Saturate", ["x"]),
        [11] = new("SinDeg", ["x"]), [12] = new("CosDeg", ["x"]),
        [15] = new("Lerp", ["起始值 from", "结束值 to", "输入 t"]),
        [16] = new("Clamp", ["输入 value", "下限 lo", "上限 hi"]),
        [17] = new("SmoothStep", ["起点 lo", "终点 hi", "输入 value"]),
        [18] = new("Min", ["a", "b"]), [19] = new("Max", ["a", "b"]),
        [20] = new("Pow", ["base", "exponent"]), [21] = new("Remap", ["t", "lo", "hi", "from", "to"])
    };
    private static readonly string[] BinaryNames = ["Pow", "Mul", "Div", "Mod", "Add", "Sub"];
    public static string[]? Choices(Type type) => type == typeof(EfxExpressionFunction) ? Functions.Values.Select(f => f.Name).ToArray() :
        type == typeof(BinaryExpressionOperator) ? BinaryNames : null;
    public static string EnumText(object value)
    {
        int id = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        return value is EfxExpressionFunction ? Functions.GetValueOrDefault(id)?.Name ?? id.ToString(CultureInfo.InvariantCulture) :
            id >= 0 && id < BinaryNames.Length ? BinaryNames[id] : id.ToString(CultureInfo.InvariantCulture);
    }
    public static object ParseEnum(string text, Type type)
    {
        int id;
        if (type == typeof(EfxExpressionFunction))
        {
            var named = Functions.FirstOrDefault(f => f.Value.Name == text);
            id = named.Value != null ? named.Key : int.Parse(text, CultureInfo.InvariantCulture);
            if (!Functions.ContainsKey(id)) throw new ArgumentException("Unknown expression function opcode.");
        }
        else
        {
            id = Array.IndexOf(BinaryNames, text);
            if (id < 0) id = int.Parse(text, CultureInfo.InvariantCulture);
            if (id < 0 || id >= BinaryNames.Length) throw new ArgumentException("Unknown expression operator opcode.");
        }
        return Enum.ToObject(type, id);
    }

    private sealed record Node(int Component, string Name, double? Number, uint? Hash, Node[] Children);
    public static ExpressionCurveInfo[] Inspect(object target)
    {
        if (target is not IExpressionAttribute attribute || attribute.Expression == null) return [];
        var expressions = attribute.Expression.expressions;
        var bits = attribute.ExpressionBits.GetExpressionInts().ToArray();
        if (bits.Length != expressions.Count) return [new(0, "映射不一致", "", "", "表达式数量与启用通道数不一致；保留完整字段供检查。", [], null, 0, 0, [], "映射需要修复。")];
        return expressions.Select((expression, index) => Describe(expression, index, bits[index], attribute)).ToArray();
    }

    private static ExpressionCurveInfo Describe(EFXExpressionObject expression, int index, int bit, IExpressionAttribute owner)
    {
        string channel = owner.ExpressionBits.BitNames?.ElementAtOrDefault(bit) ?? $"通道 {bit}";
        string assignment = owner.GetType().GetField(channel)?.GetValue(owner)?.ToString() ?? "";
        try
        {
            if (expression.components.Count > 4096) throw new InvalidDataException("表达式过长。");
            int cursor = expression.components.Count - 1;
            Node Read(int depth)
            {
                if (depth > 64 || cursor < 0) throw new InvalidDataException("表达式结构不完整或过深。");
                int component = cursor--;
                var data = expression.components[component].data;
                if (data is EFXExpressionDataFloat f)
                {
                    if (!float.IsFinite(f.value)) throw new InvalidDataException("表达式含非有限数值。");
                    return new(component, "", f.value, null, []);
                }
                if (data is EFXExpressionDataParameterHash p) return new(component, $"ext:{p.parameterHash}", null, p.parameterHash, []);
                string name; int arity;
                if (data is EFXExpressionDataFunction fn && Functions.TryGetValue((int)fn.value, out var info)) { name = info.Name; arity = info.Args.Length; }
                else if (data is EFXExpressionDataBinaryOperator binary && (int)binary.value >= 0 && (int)binary.value < BinaryNames.Length) { name = BinaryNames[(int)binary.value]; arity = 2; }
                else if (data is EFXExpressionDataUnaryOperator unary && (int)unary.value == 0) { name = "Negate"; arity = 1; }
                else throw new InvalidDataException("存在未识别的表达式操作码；未生成猜测公式。");
                var children = new Node[arity];
                for (int i = arity - 1; i >= 0; i--) children[i] = Read(depth + 1);
                return new(component, name, null, null, children);
            }
            var root = Read(0);
            if (cursor >= 0) throw new InvalidDataException("表达式存在多个输出分支；完整字段仍可编辑，专用图待支持。");
            var values = new List<ExpressionValueInfo>();
            var variables = new HashSet<uint>();
            string Emit(Node node, string label)
            {
                if (node.Number is double number)
                {
                    string text = ((float)number).ToString("R", CultureInfo.InvariantCulture);
                    values.Add(new($"expressions.expressions[{index}].components[{node.Component}].data.value", label, text));
                    return text;
                }
                if (node.Hash is uint hash)
                {
                    variables.Add(hash);
                    var param = expression.parameters?.GetParameterByHash(hash);
                    string prefix = param?.source switch { ExpressionParameterSource.Constant => "const:", ExpressionParameterSource.Parameter => "p:", _ => "ext:" };
                    return prefix + hash;
                }
                var signature = Functions.Values.FirstOrDefault(f => f.Name == node.Name);
                return node.Name + "(" + string.Join(", ", node.Children.Select((child, i) => Emit(child,
                    (label.Length == 0 ? "" : label + " / ") + node.Name + "." + (signature?.Args.ElementAtOrDefault(i) ?? $"参数 {i + 1}")))) + ")";
            }
            string formula = Emit(root, "");
            string? axis = null, plotReason = null; double[] samples = [];
            // This representative curve is sampled over an explicitly labelled input range,
            // never over animation time or assumed live game values.
            if (variables.SetEquals([302732036u]) && expression.parameters?.Any(p => p.parameterNameHash == 302732036u && p.source == ExpressionParameterSource.External) == true)
            {
                try
                {
                    samples = Enumerable.Range(0, 101).Select(i => Evaluate(root, i / 20d)).ToArray();
                    if (samples.Any(n => !double.IsFinite(n))) throw new InvalidDataException("采样出现非有限结果。");
                    axis = "EM_SPEED（ext:302732036；参考插件名称）";
                }
                catch (InvalidDataException ex) { samples = []; plotReason = ex.Message; }
            }
            else plotReason = "当前只绘制单一 EM_SPEED 输入的公式；其他变量的运行时值尚未采集。";
            return new(index, channel, assignment, formula, null, values.ToArray(), axis, 0, 5, samples, plotReason);
        }
        catch (InvalidDataException ex) { return new(index, channel, assignment, "", ex.Message, [], null, 0, 0, [], ex.Message); }
    }

    private static double Evaluate(Node node, double input)
    {
        if (node.Number is double number) return number;
        if (node.Hash == 302732036u) return input;
        var args = node.Children.Select(n => Evaluate(n, input)).ToArray();
        double Saturate(double x) => Math.Clamp(x, 0, 1);
        return node.Name switch
        {
            "Lerp" => args[0] + (args[1] - args[0]) * Saturate(args[2]),
            "SmoothStep" when args[1] != args[0] => Smooth(Saturate((args[2] - args[0]) / (args[1] - args[0]))),
            "Clamp" => Math.Max(args[1], Math.Min(args[2], args[0])),
            "Min" => Math.Min(args[0], args[1]), "Max" => Math.Max(args[0], args[1]),
            "Add" => args[0] + args[1], "Sub" => args[0] - args[1], "Mul" => args[0] * args[1],
            "Div" when args[1] != 0 => args[0] / args[1], "Negate" => -args[0],
            _ => throw new InvalidDataException("该函数或边界条件尚未接入静态曲线绘图；公式与数值仍可检查。")
        };
    }
    private static double Smooth(double t) => t * t * (3 - 2 * t);
}
