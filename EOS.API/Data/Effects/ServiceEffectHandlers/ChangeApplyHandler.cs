using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// order-change-apply: on approval the referenced order detail lines are overwritten
/// from the change document (quantities, prices, plan dates, finished counters) and the
/// tax-split amounts are recomputed, then the order master totals are re-aggregated.
/// Ported from P_WF_COP_ORDER_CHANGE. Deapprove is a no-op (reverse kind "none"),
/// matching the legacy procedure which leaves the order untouched on deapprove.
/// All identifiers come from closed configuration checked against physical columns;
/// only key values travel as parameters.
/// </summary>
public sealed class OrderChangeApplyHandler : IEffectServiceHandler
{
    public string EffectKey => "order-change-apply";

    public Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token) =>
        ChangeApplyExecutor.ExecuteAsync(context, token);
}

internal static class ChangeApplyExecutor
{
    internal static readonly IReadOnlySet<string> ChangeAmountColumns =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AMOUNT", "AMOUNT_TAX", "TAX_SUM" };
    public static async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("order-change-apply 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("order-change-apply 需要主子表形态。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("order-change-apply 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var cfg = ChangeApplyConfig.Parse(root, plan, columns);

        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
        {
            return await ApproveAsync(context, cfg, token);
        }
        return await DeapproveAsync(context, token);
    }

    private static async Task<int> ApproveAsync(ServiceEffectContext context, ChangeApplyConfig cfg, CancellationToken token)
    {
        var plan = context.Plan;
        var parameters = new List<EffectSqlParameter>();
        var changeWhere = ServiceEffectSql.MasterKeyFilter(plan, context.MasterKeyValues, parameters);
        var changeType = context.MasterKeyValues[0];
        var changeNo = context.MasterKeyValues[1];
        parameters.Add(new EffectSqlParameter("@ct", changeType));
        parameters.Add(new EffectSqlParameter("@cn", changeNo));

        // Read the referenced order key from the change master.
        var orderType = "@ot";
        var orderNo = "@on";
        string? orderTypeValue;
        string? orderNoValue;
        var read = $"SELECT M.ORDER_TYPE, M.ORDER_NO FROM dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M WHERE {changeWhere}";
        await using (var command = new SqlCommand(read, context.Connection, context.Transaction))
        {
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                throw new EffectConfigException("order-change-apply 读不到变更主表行。");
            orderTypeValue = reader.IsDBNull(0) ? null : reader.GetString(0).Trim();
            orderNoValue = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
        }
        if (string.IsNullOrWhiteSpace(orderTypeValue) || string.IsNullOrWhiteSpace(orderNoValue))
            throw new EffectConfigException("order-change-apply 变更主表缺少原单键。");

        // Overwrite the referenced order detail lines.
        var copy = new List<string>();
        foreach (var field in cfg.DetailFields)
        {
            if (ChangeApplyExecutor.ChangeAmountColumns.Contains(field))
            {
                continue;
            }
            var sourceColumn = field.Equals("PRE_SEND_DATE", StringComparison.OrdinalIgnoreCase)
                ? "PRE_DELIVERY_DATE"
                : field;
            copy.Add($"T.{ServiceEffectSql.Q(field)} = d.{ServiceEffectSql.Q(sourceColumn)}");
        }
        copy.AddRange(AmountAssignments("T", "d"));
        var detailSql = $"UPDATE T SET {string.Join(", ", copy)} "
            + $"FROM dbo.{ServiceEffectSql.Q(cfg.DetailTarget)} T JOIN dbo.{ServiceEffectSql.Q(plan.DetailTable!)} d "
            + $"ON T.ORDER_TYPE=d.ORDER_TYPE AND T.ORDER_NO=d.ORDER_NO AND T.SERIAL_NO=d.{ServiceEffectSql.Q(cfg.SerialColumn)} "
            + $"WHERE d.{ServiceEffectSql.Q(plan.MasterPkOrder[0])}=@ct "
            + $"AND d.{ServiceEffectSql.Q(plan.MasterPkOrder[1])}=@cn";
        var affected = await ExecRawAsync(context, detailSql, parameters, token);

        if (cfg.Totals)
        {
            parameters.Add(new EffectSqlParameter(orderType, orderTypeValue));
            parameters.Add(new EffectSqlParameter(orderNo, orderNoValue));
            var sub = $"(SELECT T.ORDER_TYPE, T.ORDER_NO, SUM(T.AMOUNT*T.CURR_RATE) AMOUNT, "
                + $"SUM(T.AMOUNT_TAX*T.CURR_RATE) AMOUNT_TAX, SUM(T.TAX_SUM*T.CURR_RATE) TAX_SUM "
                + $"FROM dbo.{ServiceEffectSql.Q(cfg.DetailTarget)} T "
                + $"WHERE T.ORDER_TYPE=@ot AND T.ORDER_NO=@on "
                + "GROUP BY T.ORDER_TYPE, T.ORDER_NO) S";
            var totalsSql = $"UPDATE {ServiceEffectSql.Q(cfg.MasterTarget)} SET "
                + $"AMOUNT=ROUND(S.AMOUNT/{ServiceEffectSql.Q(cfg.MasterTarget)}.CURR_RATE,2), "
                + $"AMOUNT_TAX=ROUND(S.AMOUNT_TAX/{ServiceEffectSql.Q(cfg.MasterTarget)}.CURR_RATE,2), "
                + $"TAX_SUM=ROUND(S.TAX_SUM/{ServiceEffectSql.Q(cfg.MasterTarget)}.CURR_RATE,2) "
                + $"FROM {sub} WHERE {ServiceEffectSql.Q(cfg.MasterTarget)}.ORDER_TYPE=S.ORDER_TYPE "
                + $"AND {ServiceEffectSql.Q(cfg.MasterTarget)}.ORDER_NO=S.ORDER_NO";
            affected += await ExecRawAsync(context, totalsSql, parameters, token);
        }
        return affected;
    }

    private static Task<int> DeapproveAsync(ServiceEffectContext context, CancellationToken token)
    {
        // reverse kind "none": the legacy order-change procedure is a no-op on deapprove.
        var kind = ReverseKind(context);
        if (kind is not null && kind.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(0);
        }
        if (kind is not null && kind.Equals("no-reverse", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(0);
        }
        throw new EffectConfigException($"order-change-apply 解批 reverse.kind '{kind}' 不受支持（仅 none/no-reverse）。");
    }

    private static async Task<int> ExecRawAsync(
        ServiceEffectContext context, string sql, IReadOnlyList<EffectSqlParameter> parameters, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static IReadOnlyList<string> AmountAssignments(string targetAlias, string sourceAlias)
    {
        var baseExpr = $"ROUND({sourceAlias}.QTY * {sourceAlias}.PRICE * COALESCE({targetAlias}.REBATE, 0) / 100, 2)";
        return new[]
        {
            $"{targetAlias}.AMOUNT = CASE {targetAlias}.TAX_TYPE WHEN 'I' THEN ROUND({baseExpr} / (1 + {targetAlias}.TAX_RATE/100), 2) ELSE {baseExpr} END",
            $"{targetAlias}.AMOUNT_TAX = CASE {targetAlias}.TAX_TYPE WHEN 'O' THEN ROUND({baseExpr} * (1 + {targetAlias}.TAX_RATE/100), 2) ELSE {baseExpr} END",
            $"{targetAlias}.TAX_SUM = CASE {targetAlias}.TAX_TYPE WHEN 'N' THEN 0 WHEN 'O' THEN ROUND({baseExpr} * {targetAlias}.TAX_RATE/100, 2) WHEN 'I' THEN ROUND({baseExpr} * {targetAlias}.TAX_RATE/100 / (1 + {targetAlias}.TAX_RATE/100), 2) END",
        };
    }

    private static string? ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            return null;
        return kind.GetString();
    }
}

internal sealed record ChangeApplyConfig(
    string MasterTarget,
    string DetailTarget,
    string[] DetailFields,
    string SerialColumn,
    bool Totals)
{
    public static ChangeApplyConfig Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("order-change-apply 参数必须是 JSON 对象。");
        if (!root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("order-change-apply.detail 必须是对象。");
        var fields = StrArr(detail, "fields");
        if (fields.Length == 0)
            throw new EffectConfigException("order-change-apply.detail.fields 不能为空。");
        var totals = root.TryGetProperty("totals", out var t) && t.ValueKind == JsonValueKind.True;
        var serialColumn = root.TryGetProperty("serialColumn", out var sc) && sc.ValueKind == JsonValueKind.String
            ? sc.GetString()!.Trim()
            : "ORDER_SERIAL_NO";

        var masterTarget = plan.MasterTable!.Replace("_CHANGE", "", StringComparison.OrdinalIgnoreCase);
        var detailTarget = plan.DetailTable!.Replace("_CHANGE", "", StringComparison.OrdinalIgnoreCase);
        if (!columns.Contains(masterTarget) || !columns.Contains(detailTarget))
            throw new EffectConfigException($"order-change-apply 目标表不存在：{masterTarget}/{detailTarget}。");
        foreach (var field in fields)
        {
            var sourceColumn = field.Equals("PRE_SEND_DATE", StringComparison.OrdinalIgnoreCase) ? "PRE_DELIVERY_DATE" : field;
            if (!columns.Contains(detailTarget + "." + field) && !ChangeApplyExecutor.ChangeAmountColumns.Contains(field))
                throw new EffectConfigException($"order-change-apply 目标列不存在：{detailTarget}.{field}。");
            if (!columns.Contains(plan.DetailTable + "." + sourceColumn) && !ChangeApplyExecutor.ChangeAmountColumns.Contains(field))
                throw new EffectConfigException($"order-change-apply 源列不存在：{plan.DetailTable}.{sourceColumn}。");
        }
        foreach (var reference in new[]
        {
            plan.MasterTable + ".ORDER_TYPE", plan.MasterTable + ".ORDER_NO",
            plan.DetailTable + ".ORDER_TYPE", plan.DetailTable + ".ORDER_NO", plan.DetailTable + "." + serialColumn,
            detailTarget + ".ORDER_TYPE", detailTarget + ".ORDER_NO", detailTarget + ".SERIAL_NO",
            detailTarget + ".REBATE", detailTarget + ".TAX_TYPE", detailTarget + ".TAX_RATE",
            detailTarget + ".QTY", detailTarget + ".PRICE", detailTarget + ".CURR_RATE",
            masterTarget + ".ORDER_TYPE", masterTarget + ".ORDER_NO", masterTarget + ".CURR_RATE",
        })
        {
            if (!columns.Contains(reference))
                throw new EffectConfigException($"order-change-apply 列不存在：{reference}。");
        }
        return new ChangeApplyConfig(masterTarget, detailTarget, fields, serialColumn, totals);
    }

    private static string[] StrArr(JsonElement e, string n)
    {
        if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new EffectConfigException($"order-change-apply.{n} 必须是非空字段名数组。");
            list.Add(item.GetString()!.Trim());
        }
        return list.ToArray();
    }
}