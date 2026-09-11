using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// callback-reprice: on an approval, the delivery/return (or receive/cancel) lines that a
/// callback document references are re-priced at the callback price and the source document
/// totals are recomputed. Closed semantics ported from P_WF_COP_CALLBACK / P_WF_PUR_CALLBACK:
///   - line amount = QTY x callback PRICE x REBATE / 100, split by TAX_TYPE into
///     AMOUNT / AMOUNT_TAX / TAX_SUM (all ROUND(,2)) — the callback documents only carry the
///     price, the target line keeps its own QTY / REBATE / TAX_TYPE / TAX_RATE;
///   - master totals recomputed as SUM(line amount x CURR_RATE) / master CURR_RATE;
///   - optional line marks (e.g. CALLBACK_TYPE/NO/…, CLIENT_ACCEPT_NO) are written back;
///   - optional duplicate guard: approving against an already-marked line is rejected;
///   - deapprove either clears the marks (amounts kept as final) or is a no-op.
/// All identifiers come from closed configuration checked against physical columns.
/// </summary>
public sealed class CallbackRepriceHandler : IEffectServiceHandler
{
    public string EffectKey => "callback-reprice";

    private readonly EffectPhysicalColumns _columns = new();

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("callback-reprice 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null || plan.MasterPkOrder.Count < 2)
            throw new EffectConfigException("callback-reprice 需要单据双键主表形态（类型+单号）。");
        var columns = await _columns.LoadAsync(context.Connection, token, context.Transaction);
        var cfg = CallbackConfig.Parse(root, plan, columns);
        var isApprove = context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save;
        var callbackType = context.MasterKeyValues.Count > 0 ? context.MasterKeyValues[0] : null;
        var callbackNo = context.MasterKeyValues.Count > 1 ? context.MasterKeyValues[1] : null;
        if (callbackType is null || callbackNo is null)
            throw new EffectConfigException("callback-reprice 缺少单据主键值。");

        var affected = 0;
        foreach (var target in cfg.Targets)
        {
            if (isApprove)
            {
                if (cfg.DuplicateGuardMarked)
                    await GuardUnmarkedAsync(context, plan, target, callbackType, callbackNo, token);
                affected += await RepriceDetailAsync(context, plan, target, cfg, callbackType, callbackNo, token);
                affected += await RecalcMasterAsync(context, plan, target, callbackType, callbackNo, token);
            }
            else if (cfg.Deapprove == "clear-mark")
            {
                affected += await ClearMarksAsync(context, plan, target, callbackType, callbackNo, token);
            }
            // deapprove "none" is a no-op, mirroring the purchase callback procedure.
        }
        return affected;
    }

    private static async Task GuardUnmarkedAsync(
        ServiceEffectContext context,
        ModuleEffectPlan plan,
        CallbackTarget target,
        string type,
        string no,
        CancellationToken token)
    {
        var detail = ServiceEffectSql.Q(target.Detail);
        var typeCol = ServiceEffectSql.Q(target.TypeCol);
        var noCol = ServiceEffectSql.Q(target.NoCol);
        var serialCol = ServiceEffectSql.Q(target.SerialCol);
        var sql = $"SELECT TOP 1 1 FROM dbo.{detail} D "
            + "JOIN dbo.COP_CALLBACK_D R ON D." + typeCol + "=R.S_R_TYPE AND D." + noCol + "=R.S_R_NO AND D." + serialCol + "=R.S_R_SERIAL_NO "
            + "WHERE R.CALLBACK_TYPE=@ct AND R.CALLBACK_NO=@cn AND D.CALLBACK_NO IS NOT NULL AND D.CALLBACK_NO<>''";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@ct", type);
        command.Parameters.AddWithValue("@cn", no);
        if (await command.ExecuteScalarAsync(token) is not null)
            throw new EffectValidationException("以下序号项送、退货已有回执");
    }

    private static async Task<int> RepriceDetailAsync(
        ServiceEffectContext context,
        ModuleEffectPlan plan,
        CallbackTarget target,
        CallbackConfig cfg,
        string type,
        string no,
        CancellationToken token)
    {
        var detail = ServiceEffectSql.Q(target.Detail);
        var typeCol = ServiceEffectSql.Q(target.TypeCol);
        var noCol = ServiceEffectSql.Q(target.NoCol);
        var serialCol = ServiceEffectSql.Q(target.SerialCol);
        var set = new List<string>();
        var cbAlias = "R";
        foreach (var copy in target.CopyFromCallback)
            set.Add($"D.{ServiceEffectSql.Q(copy)} = {cbAlias}.{ServiceEffectSql.Q(copy)}");
        var amountExpr = $"ROUND(D.QTY * R.PRICE * COALESCE(D.REBATE,0) / 100, 2)";
        foreach (var amount in target.AmountTo)
        {
            var expr = amount.ToUpperInvariant() switch
            {
                "AMOUNT" => $"CASE D.TAX_TYPE WHEN 'I' THEN ROUND({amountExpr} / (1 + D.TAX_RATE/100), 2) ELSE {amountExpr} END",
                "AMOUNT_TAX" => $"CASE D.TAX_TYPE WHEN 'O' THEN ROUND({amountExpr} * (1 + D.TAX_RATE/100), 2) ELSE {amountExpr} END",
                "TAX_SUM" => $"CASE D.TAX_TYPE WHEN 'N' THEN 0 WHEN 'O' THEN ROUND({amountExpr} * D.TAX_RATE/100, 2) WHEN 'I' THEN ROUND({amountExpr} * D.TAX_RATE/100 / (1 + D.TAX_RATE/100), 2) END",
                _ => throw new EffectConfigException($"callback-reprice 金额列 '{amount}' 不受支持。"),
            };
            set.Add($"D.{ServiceEffectSql.Q(amount)} = {expr}");
        }
        var markFromDoc = target.MarkFromDoc.ToArray();
        var markFromCb = target.MarkFromCallback.ToArray();
        if (markFromDoc.Length > 0)
        {
            foreach (var mark in markFromDoc)
            {
                var column = mark.ToUpperInvariant();
                set.Add(column.Contains("SERIAL") ? $"D.{ServiceEffectSql.Q(mark)} = R.SERIAL_NO"
                    : column.Contains("TYPE") ? $"D.{ServiceEffectSql.Q(mark)} = @docType"
                    : $"D.{ServiceEffectSql.Q(mark)} = @docNo");
            }
        }
        foreach (var mark in markFromCb)
            set.Add($"D.{ServiceEffectSql.Q(mark)} = R.{ServiceEffectSql.Q(mark)}");
        if (set.Count == 0)
            throw new EffectConfigException("callback-reprice 目标无任何可更新字段。");

        var sql = $"UPDATE D SET {string.Join(", ", set)} "
            + $"FROM dbo.{detail} D JOIN dbo.{ServiceEffectSql.Q(cfg.CallbackDetail)} R "
            + $"ON D.{typeCol}=R.S_R_TYPE AND D.{noCol}=R.S_R_NO AND D.{serialCol}=R.S_R_SERIAL_NO "
            + $"WHERE R.{ServiceEffectSql.Q(cfg.CallbackKeyCols[0])}=@docType AND R.{ServiceEffectSql.Q(cfg.CallbackKeyCols[1])}=@docNo";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@docType", type);
        command.Parameters.AddWithValue("@docNo", no);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int> RecalcMasterAsync(
        ServiceEffectContext context,
        ModuleEffectPlan plan,
        CallbackTarget target,
        string type,
        string no,
        CancellationToken token)
    {
        var master = ServiceEffectSql.Q(target.Master);
        var detail = ServiceEffectSql.Q(target.Detail);
        var typeCol = ServiceEffectSql.Q(target.TypeCol);
        var noCol = ServiceEffectSql.Q(target.NoCol);
        var serialCol = ServiceEffectSql.Q(target.SerialCol);
        var cbDetail = ServiceEffectSql.Q(plan.MasterTable!.Replace("_M", "_D"));
        var cbType = ServiceEffectSql.Q(plan.MasterPkOrder[0]);
        var cbNo = ServiceEffectSql.Q(plan.MasterPkOrder[1]);
        var sub = $"(SELECT D.{typeCol}, D.{noCol}, "
            + $"SUM(D.AMOUNT*D.CURR_RATE) AMOUNT, SUM(D.AMOUNT_TAX*D.CURR_RATE) AMOUNT_TAX, SUM(D.TAX_SUM*D.CURR_RATE) TAX_SUM "
            + $"FROM dbo.{detail} D WHERE EXISTS (SELECT 1 FROM dbo.{cbDetail} R "
            + $"WHERE R.{cbType}=@mt AND R.{cbNo}=@mn AND D.{typeCol}=R.S_R_TYPE AND D.{noCol}=R.S_R_NO) "
            + $"GROUP BY D.{typeCol}, D.{noCol}) S";
        var sql = $"UPDATE {master} SET AMOUNT=ROUND(S.AMOUNT / {master}.CURR_RATE, 2), "
            + $"AMOUNT_TAX=ROUND(S.AMOUNT_TAX / {master}.CURR_RATE, 2), TAX_SUM=ROUND(S.TAX_SUM / {master}.CURR_RATE, 2) "
            + $"FROM {sub} WHERE {master}.{typeCol}=S.{typeCol} AND {master}.{noCol}=S.{noCol}";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@mt", type);
        command.Parameters.AddWithValue("@mn", no);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int> ClearMarksAsync(
        ServiceEffectContext context,
        ModuleEffectPlan plan,
        CallbackTarget target,
        string type,
        string no,
        CancellationToken token)
    {
        var detail = ServiceEffectSql.Q(target.Detail);
        var typeCol = ServiceEffectSql.Q(target.TypeCol);
        var noCol = ServiceEffectSql.Q(target.NoCol);
        var serialCol = ServiceEffectSql.Q(target.SerialCol);
        var assignments = new List<string>();
        foreach (var mark in target.MarkFromDoc)
        {
            var column = mark.ToUpperInvariant();
            assignments.Add($"D.{ServiceEffectSql.Q(mark)} = " + (column.Contains("SERIAL") ? "0" : "''"));
        }
        foreach (var mark in target.MarkFromCallback)
            assignments.Add($"D.{ServiceEffectSql.Q(mark)} = ''");
        if (assignments.Count == 0)
            return 0;
        // Locate the marked lines through the callback detail's S_R_* references, the same
        // join the approval uses — the callback key and the target line key are different
        // columns (CALLBACK_TYPE/NO vs SEND_TYPE/NO), so matching on the target key columns
        // would miss every row.
        var cbDetail = ServiceEffectSql.Q(plan.MasterTable!.Replace("_M", "_D"));
        var cbType = ServiceEffectSql.Q(plan.MasterPkOrder[0]);
        var cbNo = ServiceEffectSql.Q(plan.MasterPkOrder[1]);
        var sql = $"UPDATE D SET {string.Join(", ", assignments)} "
            + $"FROM dbo.{detail} D JOIN dbo.{cbDetail} R "
            + $"ON D.{typeCol}=R.S_R_TYPE AND D.{noCol}=R.S_R_NO AND D.{serialCol}=R.S_R_SERIAL_NO "
            + $"WHERE R.{cbType}=@ct AND R.{cbNo}=@cn";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@ct", type);
        command.Parameters.AddWithValue("@cn", no);
        return await command.ExecuteNonQueryAsync(token);
    }
}

/// <summary>Parsed callback-reprice configuration with closed shapes.</summary>
internal sealed record CallbackConfig(
    string CallbackDetail,
    string[] CallbackKeyCols,
    IReadOnlyList<CallbackTarget> Targets,
    bool DuplicateGuardMarked,
    string Deapprove)
{
    public static CallbackConfig Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("callback-reprice 参数必须是 JSON 对象。");
        var targets = new List<CallbackTarget>();
        foreach (var group in new[] { "sendTargets", "returnTargets" })
        {
            if (!root.TryGetProperty(group, out var array) || array.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new EffectConfigException($"callback-reprice.{group} 项必须是对象。");
                var detail = Req(item, "detail");
                var master = Req(item, "master");
                var typeCol = Req(item, "typeCol");
                var noCol = Req(item, "noCol");
                var serialCol = Req(item, "serialCol");
                var copy = StrArr(item, "copyFromCallback");
                var amount = StrArr(item, "amountTo");
                var markDoc = StrArr(item, "markFromDoc");
                var markCb = StrArr(item, "markFromCallback");
                if (amount.Length == 0 && copy.Length == 0 && markDoc.Length == 0 && markCb.Length == 0)
                    throw new EffectConfigException($"callback-reprice.{group} 目标无字段，禁止空更新。");
                foreach (var f in new[] { typeCol, noCol, serialCol }.Concat(copy).Concat(amount))
                    if (!columns.Contains(plan.MasterTable + "." + f) && !columns.Contains(detail + "." + f))
                        throw new EffectConfigException($"callback-reprice 列不存在：{detail}.{f}。");
                targets.Add(new CallbackTarget(detail, master, typeCol, noCol, serialCol, copy, amount, markDoc, markCb));
            }
        }
        if (targets.Count == 0)
            throw new EffectConfigException("callback-reprice 未配置任何目标表。");
        var dup = root.TryGetProperty("duplicateGuardMarked", out var d) && d.ValueKind == JsonValueKind.True;
        var deapprove = root.TryGetProperty("deapprove", out var dp) && dp.ValueKind == JsonValueKind.String
            ? dp.GetString()!
            : "none";
        if (deapprove is not ("clear-mark" or "none"))
            throw new EffectConfigException($"callback-reprice.deapprove 仅允许 clear-mark/none，收到 '{deapprove}'。");
        var cbDetail = plan.MasterTable!.Replace("_M", "_D");
        return new CallbackConfig(cbDetail, new[] { plan.MasterPkOrder[0], plan.MasterPkOrder[1] }, targets, dup, deapprove);
    }

    private static string Req(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : throw new EffectConfigException($"callback-reprice 缺少字符串字段 '{n}'。");

    private static string[] StrArr(JsonElement e, string n)
    {
        if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new EffectConfigException($"callback-reprice.{n} 必须是非空字段名数组。");
            list.Add(item.GetString()!.Trim());
        }
        return list.ToArray();
    }
}

internal sealed record CallbackTarget(
    string Detail,
    string Master,
    string TypeCol,
    string NoCol,
    string SerialCol,
    string[] CopyFromCallback,
    string[] AmountTo,
    string[] MarkFromDoc,
    string[] MarkFromCallback);
