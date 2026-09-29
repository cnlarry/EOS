using System.Reflection;
using System.Reflection.Emit;
using EOS.API.Features.Assistant.Tools;

namespace EOS.API.Tests;

/// <summary>
/// 结构断言的公共底座：从 IL 里解析出"这段代码**实际调得到**哪些方法"。
///
/// <para>
/// 结构断言要断的是"调不到"，而不是"没写"。逐条读源码做不到这一点（换个写法就绕过），
/// 逐条读调用点则能：看不见的调用不存在，看得见的调用一定有。
/// </para>
/// </summary>
internal static class IlCallGraph
{
    /// <summary>
    /// 助手工具面的能力清单：每个工具类型上的 <c>ToolName</c> 常量。
    /// 工具名即能力清单——名字面出现职权类动词，就是越过了可代理的边界。
    /// </summary>
    public static IReadOnlyList<string> ToolNames() =>
        [.. typeof(IAssistantTool).Assembly.GetTypes()
            .Where(type => typeof(IAssistantTool).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name == "ToolName")
                .Select(field => field.GetRawConstantValue() as string))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>
    /// 连同嵌套类型一起取——async 方法的调用点落在编译器生成的状态机里，
    /// 只看声明类型会一个调用点都读不到（断言就会"永远绿"）。
    /// </summary>
    public static IEnumerable<Type> WithNestedTypes(Type type)
    {
        yield return type;
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var inner in WithNestedTypes(nested))
            {
                yield return inner;
            }
        }
    }

    /// <summary>一个类型里所有方法体的**调用点**（含构造器），按元数据解析出被调方法。</summary>
    public static IEnumerable<MethodBase> CalledMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var methods = type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
        foreach (var method in methods)
        {
            var body = method.GetMethodBody();
            var il = body?.GetILAsByteArray();
            if (il is null)
            {
                continue;
            }
            foreach (var token in CallTokens(il))
            {
                MethodBase? called = null;
                try
                {
                    called = method.Module.ResolveMethod(token) as MethodBase;
                }
                catch (Exception)
                {
                    // 非方法成员的元数据 token（字段/字符串/签名）：不是调用点，忽略
                }
                if (called is not null)
                {
                    yield return called;
                }
            }
        }
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(op => op.Value);

    /// <summary>按 opcode 表走完整段 IL，取出所有 InlineMethod 操作数（call / callvirt / newobj / ldftn）。</summary>
    private static IEnumerable<int> CallTokens(byte[] il)
    {
        var tokens = new List<int>();
        var index = 0;
        while (index < il.Length)
        {
            OpCode op;
            if (il[index] == 0xFE)
            {
                if (index + 1 >= il.Length) break;
                if (!OpCodesByValue.TryGetValue((short)(0xFE00 | il[index + 1]), out op)) break;
                index += 2;
            }
            else
            {
                if (!OpCodesByValue.TryGetValue(il[index], out op)) break;
                index += 1;
            }

            var size = OperandSize(op, il, index);
            if (op.OperandType == OperandType.InlineMethod && index + 4 <= il.Length)
            {
                tokens.Add(BitConverter.ToInt32(il, index));
            }
            index += size;
        }
        return tokens;
    }

    private static int OperandSize(OpCode op, byte[] il, int index) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget => 1,
        OperandType.InlineVar or OperandType.InlineI or OperandType.InlineBrTarget
            or OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig
            or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => index + 4 <= il.Length ? 4 + (4 * BitConverter.ToInt32(il, index)) : 0,
        _ => 0,
    };
}
