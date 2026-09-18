using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// wage-month-doc-prune: 工资表保存后"先删同月旧档、再判每人每月一份"这一对**有序步骤**。
/// 之所以两步放在同一处理器里：引擎的保存链固定"先校验目录、后效果动作"，而本族判重的
/// 正确性依赖**删除已经发生**（离职补发会替换同月同人的旧明细），若拆成"校验规则 + 删除动作"
/// 会先判重后删除、把本应被删除化解的情形误判为重复（迁移 082 记录过该假拒绝）。
/// 语义与原 `HrDomainRules.HrWageAfterSaveAsync` 逐字一致：
///   ① 取本单 `COUNT_MONTH`，为空则不动作；
///   ② 删除同月其它单据中、与本单员工重叠的明细行；
///   ③ 再按"同月每员工一份"判重，命中即抛 `EffectValidationException`（消息与旧实现逐字一致）。
/// 参数闭合：两表 + 四个列名 + 重复文案，全部来自配置；主键值只作参数传入。
/// </summary>
public sealed class WageMonthDocPruneHandler : IEffectServiceHandler
{
    public string EffectKey => "wage-month-doc-prune";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("wage-month-doc-prune 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("wage-month-doc-prune 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;

        var month = await ReadMonthAsync(context, config, type, no, token);
        if (string.IsNullOrWhiteSpace(month)) return 0;

        var prune = "DELETE d FROM dbo." + ServiceEffectSql.Q(config.DetailTable) + " d"
            + " WHERE EXISTS (SELECT 1 FROM dbo." + ServiceEffectSql.Q(config.MasterTable) + " m"
            + " WHERE m." + ServiceEffectSql.Q(config.MonthField) + "=@month"
            + " AND m." + ServiceEffectSql.Q(config.TypeField) + "=d." + ServiceEffectSql.Q(config.TypeField)
            + " AND m." + ServiceEffectSql.Q(config.NoField) + "=d." + ServiceEffectSql.Q(config.NoField) + ")"
            + " AND NOT (d." + ServiceEffectSql.Q(config.TypeField) + "=@type AND d." + ServiceEffectSql.Q(config.NoField) + "=@no)"
            + " AND d." + ServiceEffectSql.Q(config.EmpField) + " IN (SELECT " + ServiceEffectSql.Q(config.EmpField)
            + " FROM dbo." + ServiceEffectSql.Q(config.DetailTable)
            + " WHERE " + ServiceEffectSql.Q(config.TypeField) + "=@type AND " + ServiceEffectSql.Q(config.NoField) + "=@no);";
        var affected = await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, prune,
            [
                new EffectSqlParameter("@month", month),
                new EffectSqlParameter("@type", type),
                new EffectSqlParameter("@no", no),
            ], token);

        var duplicateSql = "SELECT d." + ServiceEffectSql.Q(config.EmpField)
            + " FROM dbo." + ServiceEffectSql.Q(config.MasterTable) + " m"
            + " INNER JOIN dbo." + ServiceEffectSql.Q(config.DetailTable) + " d ON d."
            + ServiceEffectSql.Q(config.TypeField) + "=m." + ServiceEffectSql.Q(config.TypeField) + " AND d."
            + ServiceEffectSql.Q(config.NoField) + "=m." + ServiceEffectSql.Q(config.NoField)
            + " WHERE m." + ServiceEffectSql.Q(config.MonthField) + "=@month"
            + " GROUP BY d." + ServiceEffectSql.Q(config.EmpField) + " HAVING COUNT(*)>1;";
        var duplicates = new List<string>();
        await using (var command = new SqlCommand(duplicateSql, context.Connection, context.Transaction))
        {
            command.Parameters.AddWithValue("@month", month);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                duplicates.Add((reader.GetValue(0)?.ToString() ?? string.Empty).Trim() + "\t");
        }
        if (duplicates.Count > 0)
            throw new EffectValidationException(config.DuplicateMessage + string.Join("\r\n", duplicates.Take(10)));
        return affected;
    }

    private static async Task<string?> ReadMonthAsync(
        ServiceEffectContext context, WageMonthDocPruneConfig config, string type, string no, CancellationToken token)
    {
        var sql = "SELECT LTRIM(RTRIM(ISNULL(" + ServiceEffectSql.Q(config.MonthField) + ",''))) FROM dbo."
            + ServiceEffectSql.Q(config.MasterTable) + " WHERE " + ServiceEffectSql.Q(config.TypeField)
            + "=@type AND " + ServiceEffectSql.Q(config.NoField) + "=@no;";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@no", no);
        return await command.ExecuteScalarAsync(token) as string;
    }

    /// <summary>参数解析（fail-closed：所有列名必须是物理列；重复文案非空）。</summary>
    internal static WageMonthDocPruneConfig Parse(JsonElement root, ISet<string> columns)
    {
        var config = new WageMonthDocPruneConfig(
            Required(root, "masterTable"), Required(root, "detailTable"), Required(root, "typeField"),
            Required(root, "noField"), Required(root, "monthField"), Required(root, "empField"),
            root.TryGetProperty("duplicateMessage", out var message) && message.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(message.GetString())
                    ? message.GetString()!
                    : "以下人员当月工资表重复 \r\n");
        foreach (var (table, column) in new[]
                 {
                     (config.MasterTable, config.TypeField), (config.MasterTable, config.NoField),
                     (config.MasterTable, config.MonthField), (config.DetailTable, config.TypeField),
                     (config.DetailTable, config.NoField), (config.DetailTable, config.EmpField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"wage-month-doc-prune 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static string Required(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"wage-month-doc-prune 缺少字符串字段 {name}。");
}

internal sealed record WageMonthDocPruneConfig(
    string MasterTable,
    string DetailTable,
    string TypeField,
    string NoField,
    string MonthField,
    string EmpField,
    string DuplicateMessage);
