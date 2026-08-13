using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 统一选择器高级查询条件 → 参数化 SQL 谓词。
/// 字段名只来自数据源白名单（调用方传入「字段 → SQL 表达式」映射，编译期常量），
/// 运算符白名单固定，值全部参数化；条件数上限 20。非法输入抛 ArgumentException（→ 400）。
/// 语义与工作台 AddQueryPredicates 一致：比较/LIKE/空值/区间 + AND/OR 连接。
/// </summary>
internal static class ChooserConditionBuilder
{
    private const int MaxConditions = 20;

    /// <summary>
    /// 构建条件谓词并写入 command 参数。返回 null 表示无条件；
    /// 字段/运算符/数量非法时抛 ArgumentException。
    /// </summary>
    public static string? Build(
        IReadOnlyList<UnifiedChooserCondition>? conditions,
        IReadOnlyDictionary<string, string> columnExpressions,
        SqlCommand command)
    {
        if (conditions is null || conditions.Count == 0) return null;
        if (conditions.Count > MaxConditions)
            throw new ArgumentException($"高级查询条件不能超过 {MaxConditions} 个。");

        var parts = new List<string>();
        foreach (var condition in conditions)
        {
            var field = condition.Field?.Trim();
            if (string.IsNullOrWhiteSpace(field) || !columnExpressions.TryGetValue(field, out var column))
                throw new ArgumentException($"无效查询字段：{condition.Field}");

            var op = condition.Operator?.Trim().ToLowerInvariant() ?? string.Empty;
            var name = $"@qc{command.Parameters.Count}";
            string expression;
            switch (op)
            {
                case "eq": case "ne": case "gt": case "gte": case "lt": case "lte":
                    var sqlOperator = op switch { "eq" => "=", "ne" => "<>", "gt" => ">", "gte" => ">=", "lt" => "<", _ => "<=" };
                    expression = $"{column} {sqlOperator} {name}";
                    command.Parameters.AddWithValue(name, condition.Value ?? string.Empty);
                    break;
                case "contains": case "notcontains": case "startswith": case "endswith":
                    var value = condition.Value ?? string.Empty;
                    var pattern = op is "contains" or "notcontains" ? $"%{value}%" : op == "startswith" ? $"{value}%" : $"%{value}";
                    expression = $"{column} {(op == "notcontains" ? "NOT LIKE" : "LIKE")} {name}";
                    command.Parameters.AddWithValue(name, pattern);
                    break;
                case "empty":
                    expression = $"({column} IS NULL OR {column}='')";
                    break;
                case "notempty":
                    expression = $"({column} IS NOT NULL AND {column}<>'')";
                    break;
                case "between":
                    expression = $"{column} BETWEEN {name} AND {name}b";
                    command.Parameters.AddWithValue(name, condition.Value ?? string.Empty);
                    command.Parameters.AddWithValue(name + "b", condition.ValueTo ?? string.Empty);
                    break;
                default:
                    throw new ArgumentException($"无效查询运算符：{condition.Operator}");
            }
            // 首行也带 "AND " 前缀，最后统一去掉，保证逻辑连接词只出现在后续条件上
            parts.Add((parts.Count > 0 && condition.Logic.Equals("or", StringComparison.OrdinalIgnoreCase) ? "OR " : "AND ") + expression);
        }

        // 首行去掉前缀 "AND "/"OR "，整体括号包裹（与工作台 AddQueryPredicates 一致）
        parts[0] = parts[0][4..];
        return "(" + string.Join(' ', parts) + ")";
    }
}
