using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>
/// hr-apply-check: 加班申请单（180206）保存期的两条判据（原 `HrDomainRules.HrApplyAfterSaveAsync`）：
///   ① **先决条件型拒绝**——本单出勤日所属月份尚未维护「每月出勤参数」时不放行
///      （文案带 `yyyyMM`，参数里用 `{month}` 占位）；
///   ② **跨单据聚合比较**——当月（该出勤日所在月）**全部加班申请单**按员工累计的加班工时，
///      不得超过当月出勤参数明细给该员工的额度（额度行缺失且累计大于零同样拒绝）。
/// 两条都超出既有模板的表达范围（聚合跨单据、文案含动态月份）⇒ 走 `custom-validation`。
/// "每日每人一单"由同模块既有的 `duplicate-check` 实例承担，本实现不重复。
/// 表名与列名全部来自闭合参数并逐项校验为物理列；单据键值只作参数传入。
/// </summary>
internal static class HrApplyCheck
{
    public const string HandlerKey = "hr-apply-check";

    /// <summary>文案里代表"出勤日所属月份（yyyyMM）"的占位符。</summary>
    private const string MonthToken = "{month}";

    public static async Task<string?> CheckAsync(
        CustomValidationContext context, JsonElement root, CancellationToken token)
    {
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("hr-apply-check 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;

        var countDate = await ReadCountDateAsync(context, config, type, no, token);
        if (countDate is null) return null;

        var reject = config.Messages.MissingParams.Replace(MonthToken, countDate.Value.ToString("yyyyMM"));
        var countMonth = await ReadCountMonthAsync(context, config, countDate.Value, token);
        if (string.IsNullOrWhiteSpace(countMonth)) return reject;

        var (enactmentType, enactmentNo) = await ReadEnactmentKeyAsync(context, config, countMonth, token);
        if (enactmentType is null || enactmentNo is null) return reject;

        var lines = await ReadExceededLinesAsync(context, config, type, no, countDate.Value, enactmentType, enactmentNo, token);
        return lines is null ? null : config.Messages.Exceeded + lines;
    }

    private static async Task<DateTime?> ReadCountDateAsync(
        CustomValidationContext context, HrApplyCheckConfig c, string type, string no, CancellationToken token)
    {
        var sql = $"SELECT {Q(c.Apply.DateField)} FROM dbo.{Q(c.Apply.Table)}"
                + $" WHERE {Q(c.Apply.TypeField)}=@Type AND {Q(c.Apply.NoField)}=@No;";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@Type", type);
        command.Parameters.AddWithValue("@No", no);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToDateTime(value);
    }

    private static async Task<string?> ReadCountMonthAsync(
        CustomValidationContext context, HrApplyCheckConfig c, DateTime countDate, CancellationToken token)
    {
        var sql = $"SELECT LTRIM(RTRIM(ISNULL({Q(c.Enactment.MonthField)},''))) FROM dbo.{Q(c.Enactment.Table)}"
                + $" WHERE {Q(c.Enactment.MonthField)}=CONVERT(varchar(6),@CountDate,112);";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@CountDate", countDate);
        return (string?)await command.ExecuteScalarAsync(token);
    }

    private static async Task<(string? Type, string? No)> ReadEnactmentKeyAsync(
        CustomValidationContext context, HrApplyCheckConfig c, string countMonth, CancellationToken token)
    {
        var sql = $"SELECT TOP 1 LTRIM(RTRIM({Q(c.Enactment.TypeField)})), LTRIM(RTRIM({Q(c.Enactment.NoField)}))"
                + $" FROM dbo.{Q(c.Enactment.Table)} WHERE {Q(c.Enactment.MonthField)}=@CountMonth;";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@CountMonth", countMonth);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? (reader.GetString(0), reader.GetString(1)) : (null, null);
    }

    private static async Task<string?> ReadExceededLinesAsync(
        CustomValidationContext context, HrApplyCheckConfig c, string type, string no, DateTime countDate,
        string enactmentType, string enactmentNo, CancellationToken token)
    {
        var apply = Q(c.Apply.Table);
        var applyDetail = Q(c.Apply.DetailTable);
        var enactment = Q(c.Enactment.Table);
        var enactmentDetail = Q(c.Enactment.DetailTable);
        var detailJoin = $"d.{Q(c.Apply.TypeField)}=m.{Q(c.Apply.TypeField)}"
                       + $" AND d.{Q(c.Apply.NoField)}=m.{Q(c.Apply.NoField)}";

        var sql = $"""
            SELECT t.{Q(c.Apply.EmpField)}, ISNULL(e.{Q(c.Enactment.OverTimeField)},0),
                   ISNULL(e.{Q(c.Enactment.RestOverTimeField)},0), ISNULL(e.{Q(c.Enactment.HolidayOverTimeField)},0),
                   t.OT, t.ROT, t.HOT
            FROM (
                SELECT d.{Q(c.Apply.EmpField)} AS EMP_ID, SUM(d.{Q(c.Apply.OverTimeField)}) AS OT,
                       SUM(d.{Q(c.Apply.RestOverTimeField)}) AS ROT,
                       SUM(d.{Q(c.Apply.HolidayOverTimeField)}) AS HOT
                FROM dbo.{apply} m INNER JOIN dbo.{applyDetail} d ON {detailJoin}
                WHERE YEAR(m.{Q(c.Apply.DateField)})=YEAR(@CountDate) AND MONTH(m.{Q(c.Apply.DateField)})=MONTH(@CountDate)
                  AND d.{Q(c.Apply.EmpField)} IN (SELECT {Q(c.Apply.EmpField)} FROM dbo.{applyDetail}
                                                   WHERE {Q(c.Apply.TypeField)}=@Type AND {Q(c.Apply.NoField)}=@No)
                GROUP BY d.{Q(c.Apply.EmpField)}
            ) t
            LEFT JOIN dbo.{enactmentDetail} e
              ON e.{Q(c.Enactment.EmpField)}=t.EMP_ID AND e.{Q(c.Enactment.TypeField)}=@EnaType
             AND e.{Q(c.Enactment.NoField)}=@EnaNo
            WHERE ISNULL(e.{Q(c.Enactment.OverTimeField)},0) < t.OT
               OR ISNULL(e.{Q(c.Enactment.RestOverTimeField)},0) < t.ROT
               OR ISNULL(e.{Q(c.Enactment.HolidayOverTimeField)},0) < t.HOT
               OR (e.{Q(c.Enactment.EmpField)} IS NULL AND (t.OT>0 OR t.ROT>0 OR t.HOT>0));
            """;

        var lines = new List<string>();
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@CountDate", countDate);
        command.Parameters.AddWithValue("@Type", type);
        command.Parameters.AddWithValue("@No", no);
        command.Parameters.AddWithValue("@EnaType", enactmentType);
        command.Parameters.AddWithValue("@EnaNo", enactmentNo);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            lines.Add($"{Str(reader, 0)}  {Num(reader, 1)}  {Num(reader, 2)}  {Num(reader, 3)}"
                      + $"  已录入  {Num(reader, 4)}  {Num(reader, 5)}  {Num(reader, 6)}");
        }
        return lines.Count == 0 ? null : string.Join("\r\n", lines.Take(10));
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    private static string Num(SqlDataReader reader, int index)
        => Convert.ToDouble(reader.GetValue(index)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Str(SqlDataReader reader, int index)
        => reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString()?.Trim() ?? string.Empty;

    /// <summary>参数解析（fail-closed：两张主表与两张明细表的全部列名必须物理存在、两条文案非空）。</summary>
    internal static HrApplyCheckConfig Parse(JsonElement root, ISet<string> columns)
    {
        const string label = "hr-apply-check";
        var apply = Section(root, "apply", label);
        var enactment = Section(root, "enactment", label);
        var messages = Section(root, "messages", label);
        var config = new HrApplyCheckConfig(
            new HrApplyFields(Required(apply, "table"), Required(apply, "detailTable"),
                Required(apply, "typeField"), Required(apply, "noField"), Required(apply, "dateField"),
                Required(apply, "empField"), Required(apply, "overTimeField"),
                Required(apply, "restOverTimeField"), Required(apply, "holidayOverTimeField")),
            new HrEnactmentFields(Required(enactment, "table"), Required(enactment, "detailTable"),
                Required(enactment, "typeField"), Required(enactment, "noField"), Required(enactment, "monthField"),
                Required(enactment, "empField"), Required(enactment, "overTimeField"),
                Required(enactment, "restOverTimeField"), Required(enactment, "holidayOverTimeField")),
            new HrApplyMessages(RequiredVerbatim(messages, "missingParams"), RequiredVerbatim(messages, "exceeded")));
        foreach (var (table, column) in new[]
                 {
                     (config.Apply.Table, config.Apply.TypeField),
                     (config.Apply.Table, config.Apply.NoField),
                     (config.Apply.Table, config.Apply.DateField),
                     (config.Apply.DetailTable, config.Apply.TypeField),
                     (config.Apply.DetailTable, config.Apply.NoField),
                     (config.Apply.DetailTable, config.Apply.EmpField),
                     (config.Apply.DetailTable, config.Apply.OverTimeField),
                     (config.Apply.DetailTable, config.Apply.RestOverTimeField),
                     (config.Apply.DetailTable, config.Apply.HolidayOverTimeField),
                     (config.Enactment.Table, config.Enactment.TypeField),
                     (config.Enactment.Table, config.Enactment.NoField),
                     (config.Enactment.Table, config.Enactment.MonthField),
                     (config.Enactment.DetailTable, config.Enactment.TypeField),
                     (config.Enactment.DetailTable, config.Enactment.NoField),
                     (config.Enactment.DetailTable, config.Enactment.EmpField),
                     (config.Enactment.DetailTable, config.Enactment.OverTimeField),
                     (config.Enactment.DetailTable, config.Enactment.RestOverTimeField),
                     (config.Enactment.DetailTable, config.Enactment.HolidayOverTimeField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"{label} 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static JsonElement Section(JsonElement root, string name, string label)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new EffectConfigException($"{label} 缺少对象字段 {name}。");

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"hr-apply-check 缺少字符串字段 {name}。");

    /// <summary>文案按**逐字**取值（不裁剪）：旧文案自带换行，裁剪会改变用户看到的排版。</summary>
    private static string RequiredVerbatim(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new EffectConfigException($"hr-apply-check 缺少字符串字段 {name}。");
}

internal sealed record HrApplyFields(string Table, string DetailTable, string TypeField, string NoField,
    string DateField, string EmpField, string OverTimeField, string RestOverTimeField, string HolidayOverTimeField);
internal sealed record HrEnactmentFields(string Table, string DetailTable, string TypeField, string NoField,
    string MonthField, string EmpField, string OverTimeField, string RestOverTimeField, string HolidayOverTimeField);
internal sealed record HrApplyMessages(string MissingParams, string Exceeded);
internal sealed record HrApplyCheckConfig(HrApplyFields Apply, HrEnactmentFields Enactment, HrApplyMessages Messages);
