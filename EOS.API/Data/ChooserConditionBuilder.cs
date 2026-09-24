using EOS.API.Data.Query;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 统一选择器高级查询条件 → 参数化 SQL 谓词。
/// 字段名只来自数据源白名单（调用方传入「字段 → SQL 表达式」映射，编译期常量），值全部参数化。
/// 算子表、条件数上限与逻辑连接词的拼装由 <see cref="QueryConditionRenderer"/> 统一提供，
/// 与工作台列表查询**同一份**，不得在此另立一套。
/// 非法输入抛 ArgumentException（→ 400）。
/// </summary>
internal static class ChooserConditionBuilder
{
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
        if (conditions.Count > QueryConditionRenderer.MaxConditions)
            throw new ArgumentException($"高级查询条件不能超过 {QueryConditionRenderer.MaxConditions} 个。");

        var parts = new List<string>();
        foreach (var condition in conditions)
        {
            var field = condition.Field?.Trim();
            if (string.IsNullOrWhiteSpace(field) || !columnExpressions.TryGetValue(field, out var column))
                throw new ArgumentException($"无效查询字段：{condition.Field}");

            var name = $"@qc{command.Parameters.Count}";
            var expression = QueryConditionRenderer.Render(
                column, condition.Operator, condition.Value, condition.ValueTo, name, command);
            parts.Add(QueryConditionRenderer.Prefix(parts.Count > 0, condition.Logic, expression));
        }

        return QueryConditionRenderer.Combine(parts);
    }
}
