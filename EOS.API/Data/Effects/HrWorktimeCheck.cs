using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>
/// hr-worktime-check: 工时录入单（180207）保存期的"本月加班工时不得超过已申请加班工时"判据
/// （原 `HrDomainRules.HrWorktimeAfterSaveAsync`）。判据形状＝**两张表各自按员工聚合后逐员工比较**
/// （本单工时明细按员工汇总 vs 同一出勤日所属月的加班申请明细按员工汇总），
/// 现有校验模板的聚合都在"本单/被引用行"范围内，表达不了跨单据的同键聚合 ⇒ 走 `custom-validation`。
///
/// 门控：业务设置表 `HR_SETUP.REQUIRE_ENACTMENT=1` 时才判（设置关闭即放行，与旧实现一致）。
/// **移植时按意图修正的一处缺陷（已登记）**：旧实现的左侧聚合写成 `HR_WORKTIME_M.EMP_ID/OVERTIME/...`，
/// 而这两列都不在工时主表上（在明细 `HR_WORKTIME_D` 上）⇒ 门控一旦打开，旧实现必抛
/// "列名无效"（500）而不是给出校验结论；本移植改为按**明细** `EMP_ID` 分组（即原意），
/// 并把"申请侧"缺失员工的显示值按 0 呈现（旧实现在该分支同样会抛异常）。
/// 表名与列名全部来自闭合参数并逐项校验为物理列；单据键值只作参数传入。
/// </summary>
internal static class HrWorktimeCheck
{
    public const string HandlerKey = "hr-worktime-check";

    public static async Task<string?> CheckAsync(
        CustomValidationContext context, JsonElement root, CancellationToken token)
    {
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("hr-worktime-check 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, columns);

        if (!await GateOnAsync(context, config, token)) return null;

        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;
        var lines = await LinesAsync(context, BuildSql(config), type, no, token);
        return lines is null ? null : config.Message + lines;
    }

    private static string BuildSql(HrWorktimeCheckConfig c)
    {
        var worktime = Q(c.Worktime.Table);
        var worktimeDetail = Q(c.Worktime.DetailTable);
        var apply = Q(c.Apply.Table);
        var applyDetail = Q(c.Apply.DetailTable);

        var scope = $"m.{Q(c.Worktime.TypeField)}=@Type AND m.{Q(c.Worktime.NoField)}=@No";
        var detailJoin = $"d.{Q(c.Worktime.TypeField)}=m.{Q(c.Worktime.TypeField)}"
                       + $" AND d.{Q(c.Worktime.NoField)}=m.{Q(c.Worktime.NoField)}";
        var applyJoin = $"d.{Q(c.Apply.TypeField)}=m.{Q(c.Apply.TypeField)}"
                      + $" AND d.{Q(c.Apply.NoField)}=m.{Q(c.Apply.NoField)}";

        // 本单工时：按明细员工分组求和（旧实现误用主表列，见类注释）
        var own = $"""
            SELECT d.{Q(c.Worktime.EmpField)} AS EMP_ID, SUM(d.{Q(c.Worktime.OverTimeField)}) AS OT,
                   SUM(d.{Q(c.Worktime.RestOverTimeField)}) AS ROT,
                   SUM(d.{Q(c.Worktime.HolidayOverTimeField)}) AS HOT
            FROM dbo.{worktime} m INNER JOIN dbo.{worktimeDetail} d ON {detailJoin}
            WHERE {scope}
            GROUP BY d.{Q(c.Worktime.EmpField)}
            """;

        // 本单出勤日所属月的加班申请：按申请明细员工分组求和
        var applied = $"""
            SELECT d.{Q(c.Apply.EmpField)} AS EMP_ID, SUM(d.{Q(c.Apply.OverTimeField)}) AS OT,
                   SUM(d.{Q(c.Apply.RestOverTimeField)}) AS ROT,
                   SUM(d.{Q(c.Apply.HolidayOverTimeField)}) AS HOT
            FROM dbo.{apply} m INNER JOIN dbo.{applyDetail} d ON {applyJoin}
            WHERE m.{Q(c.Apply.DateField)}=(SELECT TOP 1 wm.{Q(c.Worktime.DateField)} FROM dbo.{worktime} wm
                                            WHERE wm.{Q(c.Worktime.TypeField)}=@Type
                                              AND wm.{Q(c.Worktime.NoField)}=@No)
            GROUP BY d.{Q(c.Apply.EmpField)}
            """;

        return $"""
            SELECT w.EMP_ID, ISNULL(a.OT,0), ISNULL(a.ROT,0), ISNULL(a.HOT,0), w.OT, w.ROT, w.HOT
            FROM ({own}) w
            LEFT JOIN ({applied}) a ON a.EMP_ID=w.EMP_ID
            WHERE w.OT > ISNULL(a.OT,0) OR w.ROT > ISNULL(a.ROT,0) OR w.HOT > ISNULL(a.HOT,0);
            """;
    }

    private static async Task<bool> GateOnAsync(
        CustomValidationContext context, HrWorktimeCheckConfig config, CancellationToken token)
    {
        var sql = $"SELECT TOP 1 1 FROM dbo.{Q(config.Setup.Table)} WHERE {Q(config.Setup.FlagField)}=1;";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<string?> LinesAsync(
        CustomValidationContext context, string sql, string type, string no, CancellationToken token)
    {
        var lines = new List<string>();
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@Type", type);
        command.Parameters.AddWithValue("@No", no);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            lines.Add($"{Str(reader, 0)}  {Num(reader, 1)}  {Num(reader, 2)}  {Num(reader, 3)}"
                      + $"  已录入   {Num(reader, 4)}  {Num(reader, 5)}  {Num(reader, 6)}");
        }
        return lines.Count == 0 ? null : string.Join("\r\n", lines);
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    private static string Num(SqlDataReader reader, int index)
        => Convert.ToDouble(reader.GetValue(index)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Str(SqlDataReader reader, int index)
        => reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString()?.Trim() ?? string.Empty;

    /// <summary>参数解析（fail-closed：三张表与全部列名必须物理存在、文案非空）。</summary>
    internal static HrWorktimeCheckConfig Parse(JsonElement root, ISet<string> columns)
    {
        const string label = "hr-worktime-check";
        var worktime = Section(root, "worktime", label);
        var apply = Section(root, "apply", label);
        var setup = Section(root, "setup", label);
        var config = new HrWorktimeCheckConfig(
            new HrWorktimeFields(Required(worktime, "table"), Required(worktime, "detailTable"),
                Required(worktime, "typeField"), Required(worktime, "noField"), Required(worktime, "dateField"),
                Required(worktime, "empField"), Required(worktime, "overTimeField"),
                Required(worktime, "restOverTimeField"), Required(worktime, "holidayOverTimeField")),
            new HrApplyAggregateFields(Required(apply, "table"), Required(apply, "detailTable"),
                Required(apply, "typeField"), Required(apply, "noField"), Required(apply, "dateField"),
                Required(apply, "empField"), Required(apply, "overTimeField"),
                Required(apply, "restOverTimeField"), Required(apply, "holidayOverTimeField")),
            new HrSetupGateFields(Required(setup, "table"), Required(setup, "flagField")),
            RequiredVerbatim(root, "message"));
        foreach (var (table, column) in new[]
                 {
                     (config.Worktime.Table, config.Worktime.TypeField),
                     (config.Worktime.Table, config.Worktime.NoField),
                     (config.Worktime.Table, config.Worktime.DateField),
                     (config.Worktime.DetailTable, config.Worktime.TypeField),
                     (config.Worktime.DetailTable, config.Worktime.NoField),
                     (config.Worktime.DetailTable, config.Worktime.EmpField),
                     (config.Worktime.DetailTable, config.Worktime.OverTimeField),
                     (config.Worktime.DetailTable, config.Worktime.RestOverTimeField),
                     (config.Worktime.DetailTable, config.Worktime.HolidayOverTimeField),
                     (config.Apply.Table, config.Apply.TypeField),
                     (config.Apply.Table, config.Apply.NoField),
                     (config.Apply.Table, config.Apply.DateField),
                     (config.Apply.DetailTable, config.Apply.TypeField),
                     (config.Apply.DetailTable, config.Apply.NoField),
                     (config.Apply.DetailTable, config.Apply.EmpField),
                     (config.Apply.DetailTable, config.Apply.OverTimeField),
                     (config.Apply.DetailTable, config.Apply.RestOverTimeField),
                     (config.Apply.DetailTable, config.Apply.HolidayOverTimeField),
                     (config.Setup.Table, config.Setup.FlagField),
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
                : throw new EffectConfigException($"hr-worktime-check 缺少字符串字段 {name}。");

    /// <summary>文案按**逐字**取值（不裁剪）：旧文案自带换行，裁剪会改变用户看到的排版。</summary>
    private static string RequiredVerbatim(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new EffectConfigException($"hr-worktime-check 缺少字符串字段 {name}。");
}

internal sealed record HrWorktimeFields(string Table, string DetailTable, string TypeField, string NoField,
    string DateField, string EmpField, string OverTimeField, string RestOverTimeField, string HolidayOverTimeField);
internal sealed record HrApplyAggregateFields(string Table, string DetailTable, string TypeField, string NoField,
    string DateField, string EmpField, string OverTimeField, string RestOverTimeField, string HolidayOverTimeField);
internal sealed record HrSetupGateFields(string Table, string FlagField);
internal sealed record HrWorktimeCheckConfig(HrWorktimeFields Worktime, HrApplyAggregateFields Apply,
    HrSetupGateFields Setup, string Message);
