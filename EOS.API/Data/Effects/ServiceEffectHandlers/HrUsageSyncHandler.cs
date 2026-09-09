using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// hr-usage-sync: on approval the used-worktime counters on another document's
/// lines are accumulated from this document's lines (ported from P_WF_HR_APPLY
/// and P_WF_HR_WORKTIME, which share one shape with two target scopes):
///   - 180206: HR_ENACTMENT_D.USED_* for the document month
///     (COUNT_MONTH = convert(varchar(6), COUNT_DATE, 112));
///   - 180207: HR_APPLY_D.USED_* for the exact document date.
/// Per-employee SUM aggregation applies (decision H1: the legacy emp-only join
/// let later lines overwrite earlier ones for the same employee; summing is the
/// intended accumulate semantics). Deapprove subtracts the same sums
/// (reverse kind "auto-reverse"). Missing target rows mean zero rows, never an
/// error — matching the legacy temp-table join. All identifiers come from
/// closed configuration checked against physical columns.
/// </summary>
public sealed class HrUsageSyncHandler : IEffectServiceHandler
{
    public string EffectKey => "hr-usage-sync";

    private static readonly string[] QuantitySlots = new[] { "1", "2", "3", "4" };

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("hr-usage-sync 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("hr-usage-sync 需要主子表形态。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("hr-usage-sync 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var spec = HrUsageSyncSpec.Parse(root, plan, columns);

        int sign;
        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
        {
            sign = 1;
        }
        else
        {
            var kind = ReverseKind(context);
            if (!kind.Equals("auto-reverse", StringComparison.OrdinalIgnoreCase))
                throw new EffectConfigException($"hr-usage-sync 解批 reverse.kind 仅支持 auto-reverse（当前 '{kind}'）。");
            sign = -1;
        }

        var countDate = await ReadCountDateAsync(context, token);
        if (countDate is null)
            return 0;
        var parameters = new List<EffectSqlParameter>
        {
            new("@cd", countDate),
            new("@sign", sign),
        };
        var sql = BuildUpdate(spec, plan, context.MasterKeyValues, parameters);
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
    }

    private static async Task<object?> ReadCountDateAsync(ServiceEffectContext context, CancellationToken token)
    {
        var plan = context.Plan;
        var parameters = new List<EffectSqlParameter>();
        var sql = $"SELECT M.{ServiceEffectSql.Q("COUNT_DATE")} "
            + $"FROM dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M "
            + $"WHERE {ServiceEffectSql.MasterKeyFilter(plan, context.MasterKeyValues, parameters)}";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : value;
    }

    /// <summary>
    /// One UPDATE joining target lines to the target master (month or exact date)
    /// and to this document's per-employee summed quantities. The legacy temp-table
    /// pair collapses into a single set statement with identical row effects.
    /// </summary>
    internal static string BuildUpdate(
        HrUsageSyncSpec spec,
        ModuleEffectPlan plan,
        IReadOnlyList<string> keyValues,
        List<EffectSqlParameter> parameters)
    {
        var q = ServiceEffectSql.Q;
        var sets = spec.Pairs.Select(pair =>
            $"T.{q(pair.Target)} = ISNULL(T.{q(pair.Target)}, 0) + s.{q("w" + pair.Slot)} * @sign");
        var sums = spec.Pairs.Select(pair =>
            $"SUM(ISNULL(D.{q(pair.Source)}, 0)) {q("w" + pair.Slot)}");
        var detailJoin = ServiceEffectSql.SameNameKeyJoin(plan, "D");
        var masterWhere = ServiceEffectSql.MasterKeyFilter(plan, keyValues, parameters);
        var period = spec.ByMonth
            ? $"MM.{q("COUNT_MONTH")} = CONVERT(varchar(6), @cd, 112)"
            : $"MM.{q("COUNT_DATE")} = @cd";
        return $"UPDATE T SET {string.Join(", ", sets)} "
            + $"FROM dbo.{q(spec.TargetTable)} T "
            + $"JOIN dbo.{q(spec.TargetMaster)} MM "
            + $"ON MM.{q(spec.TargetTypeColumn)} = T.{q(spec.TargetTypeColumn)} "
            + $"AND MM.{q(spec.TargetNoColumn)} = T.{q(spec.TargetNoColumn)} "
            + $"JOIN (SELECT D.{q("EMP_ID")} {q("e")}, {string.Join(", ", sums)} "
            + $"FROM dbo.{q(plan.DetailTable!)} D JOIN dbo.{q(plan.MasterTable!)} M ON {detailJoin} "
            + $"WHERE {masterWhere} GROUP BY D.{q("EMP_ID")}) s ON s.{q("e")} = T.{q("EMP_ID")} "
            + $"WHERE {period}";
    }

    private static string ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse
            || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            throw new EffectConfigException("hr-usage-sync 解批缺少 reverse.kind，禁止无守卫执行。");
        return kind.GetString()!;
    }

    internal static string[] Slots() => QuantitySlots;
}

/// <summary>One quantity pair: target USED_* column fed by a source quantity column.</summary>
internal sealed record HrUsagePair(string Target, string Source, string Slot);

/// <summary>Parsed hr-usage-sync configuration with closed shapes.</summary>
internal sealed record HrUsageSyncSpec(
    string TargetTable,
    string TargetMaster,
    string TargetTypeColumn,
    string TargetNoColumn,
    bool ByMonth,
    IReadOnlyList<HrUsagePair> Pairs)
{
    public static HrUsageSyncSpec Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("hr-usage-sync 参数必须是 JSON 对象。");
        var target = Req(root, "targetTable");
        var (targetMaster, typeColumn, noColumn, byMonth) = target.ToUpperInvariant() switch
        {
            "HR_ENACTMENT_D" => ("HR_ENACTMENT_M", "ENACTMENT_TYPE", "ENACTMENT_NO", true),
            "HR_APPLY_D" => ("HR_APPLY_M", "APPLY_TYPE", "APPLY_NO", false),
            _ => throw new EffectConfigException($"hr-usage-sync.targetTable 仅支持 HR_ENACTMENT_D / HR_APPLY_D（当前 '{target}'）。"),
        };
        var targetFields = StrArr(root, "targetFields");
        var sourceFields = StrArr(root, "sourceFields");
        if (targetFields.Length != 4 || sourceFields.Length != 4)
            throw new EffectConfigException("hr-usage-sync.targetFields/sourceFields 必须各为 4 列（工时/加班/休息加班/假日加班）。");
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("hr-usage-sync 需要主子表形态。");
        var scope = root.TryGetProperty("scopeKey", out var scopeValue) && scopeValue.ValueKind == JsonValueKind.Object
            ? scopeValue
            : throw new EffectConfigException("hr-usage-sync.scopeKey 必须是对象。");
        var emp = scope.TryGetProperty("emp", out var empValue) && empValue.ValueKind == JsonValueKind.String
            ? empValue.GetString()!.Trim()
            : throw new EffectConfigException("hr-usage-sync.scopeKey.emp 缺失。");
        if (!emp.Equals("EMP_ID", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"hr-usage-sync.scopeKey.emp 仅支持 EMP_ID（当前 '{emp}'）。");
        var hasMonth = scope.TryGetProperty("month", out var monthValue) && monthValue.ValueKind == JsonValueKind.String;
        var hasDate = scope.TryGetProperty("date", out var dateValue) && dateValue.ValueKind == JsonValueKind.String;
        if (byMonth)
        {
            if (!hasMonth || !monthValue.GetString()!.Trim().Equals("COUNT_MONTH", StringComparison.OrdinalIgnoreCase) || hasDate)
                throw new EffectConfigException("hr-usage-sync 月定位 scopeKey 必须为 {emp:EMP_ID, month:COUNT_MONTH}。");
        }
        else
        {
            if (!hasDate || !dateValue.GetString()!.Trim().Equals("COUNT_DATE", StringComparison.OrdinalIgnoreCase) || hasMonth)
                throw new EffectConfigException("hr-usage-sync 日定位 scopeKey 必须为 {emp:EMP_ID, date:COUNT_DATE}。");
        }
        foreach (var reference in new[]
        {
            target, target + ".EMP_ID",
            targetMaster, targetMaster + "." + typeColumn, targetMaster + "." + noColumn,
            plan.MasterTable, plan.MasterTable + ".COUNT_DATE",
            plan.DetailTable, plan.DetailTable + ".EMP_ID",
        }.Concat(targetFields.Select(field => target + "." + field))
         .Concat(sourceFields.Select(field => plan.DetailTable + "." + field)))
        {
            if (!columns.Contains(reference))
                throw new EffectConfigException($"hr-usage-sync 列不存在：{reference}。");
        }
        if (byMonth)
        {
            if (!columns.Contains(targetMaster + ".COUNT_MONTH"))
                throw new EffectConfigException($"hr-usage-sync 列不存在：{targetMaster}.COUNT_MONTH。");
        }
        else
        {
            if (!columns.Contains(targetMaster + ".COUNT_DATE"))
                throw new EffectConfigException($"hr-usage-sync 列不存在：{targetMaster}.COUNT_DATE。");
        }
        var slots = HrUsageSyncHandler.Slots();
        var pairs = targetFields.Zip(sourceFields, (t, s) => (t, s))
            .Select((item, index) => new HrUsagePair(item.t, item.s, slots[index]))
            .ToList();
        return new HrUsageSyncSpec(target, targetMaster, typeColumn, noColumn, byMonth, pairs);
    }

    private static string Req(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"hr-usage-sync 缺少字符串字段 '{name}'。");

    private static string[] StrArr(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException($"hr-usage-sync.{name} 必须是非空数组。");
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new EffectConfigException($"hr-usage-sync.{name} 存在空字段名。");
            list.Add(item.GetString()!.Trim());
        }
        if (list.Count == 0)
            throw new EffectConfigException($"hr-usage-sync.{name} 必须是非空数组。");
        return list.ToArray();
    }
}
