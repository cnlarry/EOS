using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// CONVERT_FUNCTION 运行时消费（P4a）：对带受控转换函数的字段，把原始值经
/// 白名单函数（参数 = 字段值）批量转换为展示值。函数名必须先通过
/// RestrictedExpressionService.ConvertFunctionRegistry 校验（发布时已强制），
/// 运行时不在注册表内一律不执行（保留原值，防呆）。
/// </summary>
public static class ConvertFunctionResolver
{
    /// <summary>对行的指定字段应用转换（原地替换 row[field]）。</summary>
    public static async Task ApplyAsync(
        SqlConnection connection,
        string field,
        string function,
        IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken token)
    {
        if (rows.Count == 0 || string.IsNullOrWhiteSpace(function)
            || !RestrictedExpressionService.ConvertFunctionRegistry.ContainsKey(function.Trim()))
            return;
        var distinct = rows
            .Select(row => row.GetValueOrDefault(field))
            .Where(value => value is not null)
            .Select(value => Convert.ToString(value)!.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0) return;
        var map = await BuildMapAsync(connection, function.Trim(), distinct, token);
        foreach (var row in rows)
        {
            var raw = row.GetValueOrDefault(field);
            if (raw is null) continue;
            var key = Convert.ToString(raw)!.Trim();
            if (map.TryGetValue(key, out var converted)) row[field] = converted;
        }
    }

    /// <summary>构建 原始值 → 转换值 映射（逐值参数化调用白名单函数，批量去重）。</summary>
    public static async Task<Dictionary<string, object?>> BuildMapAsync(
        SqlConnection connection,
        string function,
        IReadOnlyList<string> values,
        CancellationToken token)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (values.Count == 0 || string.IsNullOrWhiteSpace(function)
            || !RestrictedExpressionService.ConvertFunctionRegistry.ContainsKey(function.Trim()))
            return result;
        foreach (var value in values.Distinct(StringComparer.Ordinal))
        {
            await using var command = new SqlCommand($"SELECT dbo.[{function.Trim()}](@p);", connection);
            command.Parameters.AddWithValue("@p", value);
            result[value] = await command.ExecuteScalarAsync(token);
        }
        return result;
    }
}
