using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// order/purchase/produce-change-apply: on approval the referenced business document is
/// overwritten from the change document — header fields copied from the change master,
/// detail lines copied from the change detail (quantities, prices, plan dates, finished
/// counters) with the tax-split amounts recomputed, then master totals re-aggregated.
/// Ported from P_WF_COP_ORDER_CHANGE / P_WF_PUR_PURCHASE_CHANGE / P_WF_MOC_PRODUCE_CHANGE.
/// Deapprove is a no-op (reverse kind "none"), matching the legacy procedures.
/// All identifiers come from closed configuration checked against physical columns.
/// </summary>
public sealed class OrderChangeApplyHandler : IEffectServiceHandler
{
    public string EffectKey => "order-change-apply";

    public Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token) =>
        ChangeApplyExecutor.ExecuteAsync(context, token);
}

public sealed class PurchaseChangeApplyHandler : IEffectServiceHandler
{
    public string EffectKey => "purchase-change-apply";

    public Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token) =>
        ChangeApplyExecutor.ExecuteAsync(context, token);
}

public sealed class ProduceChangeApplyHandler : IEffectServiceHandler
{
    public string EffectKey => "produce-change-apply";

    public Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token) =>
        ChangeApplyExecutor.ExecuteAsync(context, token);
}

internal static class ChangeApplyExecutor
{
    public static async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("change-apply 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("change-apply 需要主子表形态。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("change-apply 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var cfg = ChangeApplyConfig.Parse(root, plan, columns);

        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
        {
            return await ApproveAsync(context, cfg, columns, token);
        }
        // reverse kind "none": the legacy change procedures leave the target untouched
        // on deapprove.
        var kind = ReverseKind(context);
        if (kind is "none" or "no-reverse")
        {
            return 0;
        }
        throw new EffectConfigException($"change-apply 解批 reverse.kind '{kind}' 不受支持（仅 none/no-reverse）。");
    }

    private static async Task<int> ApproveAsync(
        ServiceEffectContext context,
        ChangeApplyConfig cfg,
        ISet<string> columns,
        CancellationToken token)
    {
        var plan = context.Plan;
        var parameters = new List<EffectSqlParameter>();
        var masterWhere = ServiceEffectSql.MasterKeyFilter(plan, context.MasterKeyValues, parameters);
        var affected = 0;

        // Produce-change (1509) re-projects MRP expected-in/expected-get and the plan /
        // order planned quantities around the document overwrite: release the old
        // occupancy while the produce still holds original values, then reoccupy the new
        // values after the copy (same transaction). Only the referenced produce (keys on
        // the change master) is touched, mirroring P_WF_MOC_PRODUCE_CHANGE.
        ProduceReference? produceReference = null;
        if (cfg.ProjectionMode is not null)
        {
            ProduceChangeProjection.ValidateColumns(cfg, columns);
            produceReference = await ProduceChangeProjection.ReadReferenceAsync(context, cfg, token);
            if (produceReference is not null)
            {
                affected += await ProduceChangeProjection.ExecuteStatementsAsync(
                    context, cfg, produceReference, reoccupy: false, columns, token);
            }
        }

        if (cfg.MasterFields.Count > 0)
        {
            var masterSets = string.Join(", ", cfg.MasterFields.Select(field =>
                $"T.{ServiceEffectSql.Q(field)} = M.{ServiceEffectSql.Q(field)}"));
            var masterSql = $"UPDATE T SET {masterSets} "
                + $"FROM dbo.{ServiceEffectSql.Q(cfg.MasterTarget)} T "
                + $"JOIN dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M "
                + $"ON T.{ServiceEffectSql.Q(cfg.PrefixType)}=M.{ServiceEffectSql.Q(cfg.PrefixType)} "
                + $"AND T.{ServiceEffectSql.Q(cfg.PrefixNo)}=M.{ServiceEffectSql.Q(cfg.PrefixNo)} "
                + $"WHERE {masterWhere}";
            affected += await ExecRawAsync(context, masterSql, parameters, token);
        }

        var copy = new List<string>();
        foreach (var pair in cfg.DetailFieldPairs)
        {
            copy.Add($"T.{ServiceEffectSql.Q(pair.Target)} = d.{ServiceEffectSql.Q(pair.Source)}");
        }
        copy.AddRange(AmountAssignments(cfg.HasAmount, "T", "d"));
        var detailSql = $"UPDATE T SET {string.Join(", ", copy)} "
            + $"FROM dbo.{ServiceEffectSql.Q(cfg.DetailTarget)} T JOIN dbo.{ServiceEffectSql.Q(plan.DetailTable!)} d "
            + $"ON T.{ServiceEffectSql.Q(cfg.PrefixType)}=d.{ServiceEffectSql.Q(cfg.PrefixType)} "
            + $"AND T.{ServiceEffectSql.Q(cfg.PrefixNo)}=d.{ServiceEffectSql.Q(cfg.PrefixNo)} "
            + $"AND T.SERIAL_NO=d.{ServiceEffectSql.Q(cfg.SerialColumn)} "
            + $"WHERE d.{ServiceEffectSql.Q(plan.MasterPkOrder[0])}=@mk0 "
            + $"AND d.{ServiceEffectSql.Q(plan.MasterPkOrder[1])}=@mk1";
        affected += await ExecRawAsync(context, detailSql, parameters, token);

        if (cfg.Totals)
        {
            affected += await RecalcMasterTotalsAsync(context, cfg, parameters, token);
        }

        if (produceReference is not null)
        {
            affected += await ProduceChangeProjection.ExecuteStatementsAsync(
                context, cfg, produceReference, reoccupy: true, columns, token);
        }
        return affected;
    }

    private static async Task<int> RecalcMasterTotalsAsync(
        ServiceEffectContext context, ChangeApplyConfig cfg, List<EffectSqlParameter> parameters, CancellationToken token)
    {
        var plan = context.Plan;
        var prefixType = ServiceEffectSql.Q(cfg.PrefixType);
        var prefixNo = ServiceEffectSql.Q(cfg.PrefixNo);
        var sub = $"(SELECT T.{prefixType}, T.{prefixNo}, SUM(T.AMOUNT*T.CURR_RATE) AMOUNT, "
            + $"SUM(T.AMOUNT_TAX*T.CURR_RATE) AMOUNT_TAX, SUM(T.TAX_SUM*T.CURR_RATE) TAX_SUM "
            + $"FROM dbo.{ServiceEffectSql.Q(cfg.DetailTarget)} T "
            + $"WHERE T.{prefixType}=@ot AND T.{prefixNo}=@on "
            + $"GROUP BY T.{prefixType}, T.{prefixNo}) S";
        var sql = $"UPDATE {ServiceEffectSql.Q(cfg.MasterTarget)} SET "
            + $"AMOUNT=ROUND(S.AMOUNT/{ServiceEffectSql.Q(cfg.MasterTarget)}.CURR_RATE,2), "
            + $"AMOUNT_TAX=ROUND(S.AMOUNT_TAX/{ServiceEffectSql.Q(cfg.MasterTarget)}.CURR_RATE,2), "
            + $"TAX_SUM=ROUND(S.TAX_SUM/{ServiceEffectSql.Q(cfg.MasterTarget)}.CURR_RATE,2) "
            + $"FROM {sub} WHERE {ServiceEffectSql.Q(cfg.MasterTarget)}.{prefixType}=S.{prefixType} "
            + $"AND {ServiceEffectSql.Q(cfg.MasterTarget)}.{prefixNo}=S.{prefixNo}";
        // Resolve the referenced original document key from the change master; the
        // master key parameters were already added by the caller's MasterKeyFilter.
        await using var read = new SqlCommand(
            $"SELECT M.{prefixType}, M.{prefixNo} FROM dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M "
            + $"WHERE M.{ServiceEffectSql.Q(plan.MasterPkOrder[0])}=@mk0 AND M.{ServiceEffectSql.Q(plan.MasterPkOrder[1])}=@mk1",
            context.Connection, context.Transaction);
        foreach (var parameter in parameters)
            read.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await read.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new EffectConfigException("change-apply 读不到变更主表行。");
        var typeValue = reader.IsDBNull(0) ? null : reader.GetString(0).Trim();
        var noValue = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
        await reader.DisposeAsync();
        if (string.IsNullOrWhiteSpace(typeValue) || string.IsNullOrWhiteSpace(noValue))
            throw new EffectConfigException("change-apply 变更主表缺少原单键。");
        parameters.Add(new EffectSqlParameter("@ot", typeValue));
        parameters.Add(new EffectSqlParameter("@on", noValue));
        return await ExecRawAsync(context, sql, parameters, token);
    }

    private static async Task<int> ExecRawAsync(
        ServiceEffectContext context, string sql, IReadOnlyList<EffectSqlParameter> parameters, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static IReadOnlyList<string> AmountAssignments(bool hasAmount, string targetAlias, string sourceAlias)
    {
        if (!hasAmount)
            return Array.Empty<string>();
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

internal sealed record ChangeApplyFieldPair(string Target, string Source);

internal sealed record ChangeApplyConfig(
    string MasterTarget,
    string DetailTarget,
    string PrefixType,
    string PrefixNo,
    string SerialColumn,
    IReadOnlyList<string> MasterFields,
    IReadOnlyList<ChangeApplyFieldPair> DetailFieldPairs,
    bool Totals,
    bool HasAmount,
    string? ProjectionMode)
{
    public static ChangeApplyConfig Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("change-apply 参数必须是 JSON 对象。");
        var masterFields = root.TryGetProperty("master", out var master) && master.ValueKind == JsonValueKind.Object
            ? StrArr(master, "fields")
            : Array.Empty<string>();
        if (!root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("change-apply.detail 必须是对象。");
        var detailFields = StrArr(detail, "fields");
        if (detailFields.Length == 0)
            throw new EffectConfigException("change-apply.detail.fields 不能为空。");
        var totals = root.TryGetProperty("totals", out var t) && t.ValueKind == JsonValueKind.True;

        var masterTarget = plan.MasterTable!.Replace("_CHANGE", "", StringComparison.OrdinalIgnoreCase);
        var detailTarget = plan.DetailTable!.Replace("_CHANGE", "", StringComparison.OrdinalIgnoreCase);
        if (!columns.Contains(masterTarget) || !columns.Contains(detailTarget))
            throw new EffectConfigException($"change-apply 目标表不存在：{masterTarget}/{detailTarget}。");

        var entity = detailTarget.EndsWith("_D", StringComparison.OrdinalIgnoreCase)
            ? detailTarget[..^2]
            : detailTarget.EndsWith("_M", StringComparison.OrdinalIgnoreCase)
                ? detailTarget[..^2]
                : detailTarget;
        var prefix = entity[(entity.LastIndexOf('_') + 1)..];
        var prefixType = prefix + "_TYPE";
        var prefixNo = prefix + "_NO";
        var serialColumn = prefix + "_SERIAL_NO";

        foreach (var field in masterFields)
            if (!columns.Contains(masterTarget + "." + field) || !columns.Contains(plan.MasterTable + "." + field))
                throw new EffectConfigException($"change-apply 主表字段不存在：{field}。");

        var pairs = new List<ChangeApplyFieldPair>();
        foreach (var field in detailFields)
        {
            if (field is "AMOUNT" or "AMOUNT_TAX" or "TAX_SUM")
            {
                // recomputed from QTY/PRICE/TAX on the target row, not copied
                continue;
            }
            var sourceColumn = field.Equals("PRE_SEND_DATE", StringComparison.OrdinalIgnoreCase)
                ? "PRE_DELIVERY_DATE"
                : field;
            if (!columns.Contains(detailTarget + "." + field) || !columns.Contains(plan.DetailTable + "." + sourceColumn))
                throw new EffectConfigException($"change-apply 明细字段不存在：{detailTarget}.{field}/{sourceColumn}。");
            pairs.Add(new ChangeApplyFieldPair(field, sourceColumn));
        }

        var required = new[]
        {
            plan.MasterTable + "." + prefixType, plan.MasterTable + "." + prefixNo,
            plan.DetailTable + "." + prefixType, plan.DetailTable + "." + prefixNo,
            plan.DetailTable + "." + serialColumn,
            detailTarget + "." + prefixType, detailTarget + "." + prefixNo, detailTarget + ".SERIAL_NO",
            masterTarget + "." + prefixType, masterTarget + "." + prefixNo,
        };
        foreach (var reference in required)
            if (!columns.Contains(reference))
                throw new EffectConfigException($"change-apply 列不存在：{reference}。");
        var hasAmount = detailFields.Any(field => field is "AMOUNT" or "AMOUNT_TAX" or "TAX_SUM");
        if (hasAmount)
        {
            foreach (var reference in new[]
            {
                detailTarget + ".QTY", detailTarget + ".PRICE", detailTarget + ".REBATE",
                detailTarget + ".TAX_TYPE", detailTarget + ".TAX_RATE", detailTarget + ".CURR_RATE",
                masterTarget + ".CURR_RATE",
            })
            {
                if (!columns.Contains(reference))
                    throw new EffectConfigException($"change-apply 金额列不存在：{reference}。");
            }
        }
        var projectionMode = ParseProjectionMode(root);
        return new ChangeApplyConfig(masterTarget, detailTarget, prefixType, prefixNo, serialColumn,
            masterFields, pairs, totals && hasAmount && columns.Contains(detailTarget + ".AMOUNT"),
            hasAmount, projectionMode);
    }

    /// <summary>
    /// Optional produce-change projection ("net-replace"): before the overwrite the old
    /// occupancy on PRODUCT / MOC_PLAN_MOC / COP_ORDER_D is released and after the
    /// overwrite the new values are occupied again, all inside the apply transaction.
    /// Any other mode is a configuration error (closed set).
    /// </summary>
    private static string? ParseProjectionMode(JsonElement root)
    {
        if (!root.TryGetProperty("projection", out var projection))
            return null;
        if (projection.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("change-apply 净替换投影 projection 必须是 JSON 对象。");
        var mode = projection.TryGetProperty("mode", out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim().ToLowerInvariant()
            : "net-replace";
        if (mode != "net-replace")
            throw new EffectConfigException($"change-apply 净替换投影 mode '{mode}' 不受支持（仅 net-replace）。");
        return mode;
    }

    private static string[] StrArr(JsonElement e, string n)
    {
        if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new EffectConfigException($"change-apply.{n} 必须是非空字段名数组。");
            list.Add(item.GetString()!.Trim());
        }
        return list.ToArray();
    }
}