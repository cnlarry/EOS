using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Query;

/// <summary>
/// 高级查询条件的**唯一算子表**：把「列表达式 + 运算符 + 值」渲染为参数化 SQL 谓词，
/// 并把值登记到命令参数上。工作台列表查询（<see cref="WorkbenchQueryComposer"/>）与
/// 统一选择器高级查询（<see cref="ChooserConditionBuilder"/>）共用这一份。
///
/// 为什么必须合一：两处原先各有一张逐字相同的算子表（eq/ne/gt/gte/lt/lte/contains/
/// notcontains/startswith/endswith/empty/notempty/between + AND/OR 连接），任何一次算子语义
/// 调整都要改两遍，漏一处就会让两条入口对同一运算符给出不同结果。列表达式仍由调用方按各自
/// 白名单解析后传入——本类是纯渲染，不做字段白名单判断，也不拼用户值。
/// </summary>
internal static class QueryConditionRenderer
{
    /// <summary>单个查询允许的条件数上限（两条入口同口径，超出即拒）。</summary>
    internal const int MaxConditions = 20;

    /// <summary>
    /// 渲染单个条件为 SQL 谓词，并把值写入 <paramref name="command"/>。
    /// 运算符不受支持时抛 <see cref="ArgumentException"/>（→ 400），错误信息带原始运算符便于定位。
    /// </summary>
    internal static string Render(
        string column,
        string? rawOperator,
        string? value,
        string? valueTo,
        string parameterName,
        SqlCommand command)
    {
        var op = rawOperator?.Trim().ToLowerInvariant() ?? string.Empty;
        switch (op)
        {
            case "eq":
            case "ne":
            case "gt":
            case "gte":
            case "lt":
            case "lte":
                var sqlOperator = op switch { "eq" => "=", "ne" => "<>", "gt" => ">", "gte" => ">=", "lt" => "<", _ => "<=" };
                command.Parameters.AddWithValue(parameterName, value ?? string.Empty);
                return $"{column} {sqlOperator} {parameterName}";
            case "contains":
            case "notcontains":
            case "startswith":
            case "endswith":
                var text = value ?? string.Empty;
                var pattern = op is "contains" or "notcontains" ? $"%{text}%" : op == "startswith" ? $"{text}%" : $"%{text}";
                command.Parameters.AddWithValue(parameterName, pattern);
                return $"{column} {(op == "notcontains" ? "NOT LIKE" : "LIKE")} {parameterName}";
            case "empty":
                return $"({column} IS NULL OR {column}='')";
            case "notempty":
                return $"({column} IS NOT NULL AND {column}<>'')";
            case "between":
                command.Parameters.AddWithValue(parameterName, value ?? string.Empty);
                command.Parameters.AddWithValue(parameterName + "b", valueTo ?? string.Empty);
                return $"{column} BETWEEN {parameterName} AND {parameterName}b";
            default:
                throw new ArgumentException($"无效查询运算符：{rawOperator}");
        }
    }

    /// <summary>
    /// 给条件加逻辑连接词前缀。首条也带前缀（由 <see cref="Combine"/> 统一去掉），
    /// 这样"连接词只出现在后续条件上"的语义只在一处保证。
    /// </summary>
    internal static string Prefix(bool hasPrevious, string? logic, string expression)
        => (hasPrevious && string.Equals(logic, "or", StringComparison.OrdinalIgnoreCase) ? "OR " : "AND ") + expression;

    /// <summary>把逐条带前缀的谓词拼成整体谓词（首条去前缀 + 整体括号包裹）；无条件返回 null。</summary>
    internal static string? Combine(List<string> parts)
    {
        if (parts.Count == 0)
        {
            return null;
        }
        parts[0] = parts[0][4..];
        return "(" + string.Join(' ', parts) + ")";
    }
}
