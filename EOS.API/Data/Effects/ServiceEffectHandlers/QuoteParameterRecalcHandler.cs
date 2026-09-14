using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// quote-parameter-recalc: recalculates the quote cost parameters (COP_QUOTE_PARAMETER)
/// for the materials quoted by the current document, using the highest tax-adjusted
/// price across all confirmed, in-effect supplier quotes for each material. Ported from
/// P_WF_PUR_QUOTE; approve and deapprove share the same recalc-confirmed semantics.
/// All identifiers come from closed configuration checked against physical columns;
/// only the document key values travel as parameters.
/// </summary>
public sealed class QuoteParameterRecalcHandler : IEffectServiceHandler
{
    public string EffectKey => "quote-parameter-recalc";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("quote-parameter-recalc 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("quote-parameter-recalc 需要主子表形态。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("quote-parameter-recalc 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var cfg = QuoteParameterConfig.Parse(root, plan, columns);

        var qType = "@qt";
        var qNo = "@qn";
        var parameters = new List<EffectSqlParameter>
        {
            new(qType, context.MasterKeyValues[0]),
            new(qNo, context.MasterKeyValues[1]),
        };
        var master = plan.MasterTable!;
        var detail = plan.DetailTable!;

        // Highest tax-adjusted price per material across confirmed, in-effect quotes
        // that also appear on this document.
        var quotePrice = $"(SELECT d.PRO_NO, MAX(d.PRICE*d.CURR_RATE*(CASE d.TAX_TYPE WHEN 'I' THEN 1/(1+d.TAX_RATE/100) ELSE 1 END)) QUOTE_PRICE "
            + $"FROM dbo.{ServiceEffectSql.Q(master)} m JOIN dbo.{ServiceEffectSql.Q(detail)} d "
            + $"ON m.QUOTE_TYPE=d.QUOTE_TYPE AND m.QUOTE_NO=d.QUOTE_NO "
            + $"WHERE m.CONFIRM_TAG=1 AND m.IN_EFFECT_DATE>=CAST(GETDATE() AS date) "
            + $"AND EXISTS (SELECT 1 FROM dbo.{ServiceEffectSql.Q(detail)} x "
            + $"WHERE x.QUOTE_TYPE={qType} AND x.QUOTE_NO={qNo} AND x.PRO_NO=d.PRO_NO) "
            + "GROUP BY d.PRO_NO)";

        var sets = new List<string>
        {
            "MATERIAL_SUM=ROUND(Q.QUOTE_PRICE,3)",
            "PRICE=CASE WHEN ISNULL(P.MATERIAL_PCT,0)<>0 THEN ROUND(Q.QUOTE_PRICE/P.MATERIAL_PCT*100,3) ELSE 0 END",
        };
        var feeAssignments = new List<string>();
        foreach (var fee in cfg.FeeFields)
        {
            var pct = fee.Replace("_SUM", "", StringComparison.Ordinal) + "_PCT";
            feeAssignments.Add($"{ServiceEffectSql.Q(fee)}=ROUND(P.PRICE*P.{ServiceEffectSql.Q(pct)}/100,3)");
        }
        var update = $"UPDATE P SET {string.Join(", ", sets)} "
            + $"FROM dbo.{ServiceEffectSql.Q(cfg.TargetTable)} P JOIN {quotePrice} Q ON P.STUFF_ID=Q.PRO_NO";
        var affected = await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, update, parameters, token);

        // Insert cost parameter rows for materials quoted by this document that do not
        // have one yet; the material price is the current document line value.
        var insert = $"INSERT INTO dbo.{ServiceEffectSql.Q(cfg.TargetTable)} (STUFF_ID, MATERIAL_SUM, MATERIAL_PCT, PRICE) "
            + $"SELECT q.PRO_NO, ROUND(q.PRICE*q.CURR_RATE*(CASE q.TAX_TYPE WHEN 'I' THEN 1/(1+q.TAX_RATE/100) ELSE 1 END),3), 100, "
            + $"ROUND(q.PRICE*q.CURR_RATE*(CASE q.TAX_TYPE WHEN 'I' THEN 1/(1+q.TAX_RATE/100) ELSE 1 END),3) "
            + $"FROM dbo.{ServiceEffectSql.Q(detail)} q JOIN dbo.STUFF s ON q.PRO_NO=s.STUFF_ID "
            + $"WHERE q.QUOTE_TYPE={qType} AND q.QUOTE_NO={qNo} "
            + $"AND NOT EXISTS (SELECT 1 FROM dbo.{ServiceEffectSql.Q(cfg.TargetTable)} P WHERE P.STUFF_ID=q.PRO_NO)";
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, insert, parameters, token);

        // Recompute the fee breakdown for every cost parameter touched by this document.
        if (feeAssignments.Count > 0)
        {
            var recalc = $"UPDATE P SET {string.Join(", ", feeAssignments)} "
                + $"FROM dbo.{ServiceEffectSql.Q(cfg.TargetTable)} P "
                + $"WHERE EXISTS (SELECT 1 FROM dbo.{ServiceEffectSql.Q(detail)} d "
                + $"WHERE d.QUOTE_TYPE={qType} AND d.QUOTE_NO={qNo} AND d.PRO_NO=P.STUFF_ID)";
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, recalc, parameters, token);
        }
        return affected;
    }
}

internal sealed record QuoteParameterConfig(string TargetTable, string Mode, string[] FeeFields)
{
    public static QuoteParameterConfig Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("quote-parameter-recalc 参数必须是 JSON 对象。");
        var target = Req(root, "targetTable");
        var mode = Req(root, "mode");
        if (!mode.Equals("recalc-confirmed", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"quote-parameter-recalc.mode 仅支持 recalc-confirmed，收到 '{mode}'。");
        var feeFields = StrArr(root, "feeFields");
        if (feeFields.Length == 0)
            throw new EffectConfigException("quote-parameter-recalc.feeFields 不能为空。");

        foreach (var table in new[] { target, "STUFF", plan.MasterTable!, plan.DetailTable! })
            if (!columns.Contains(table))
                throw new EffectConfigException($"quote-parameter-recalc 表不存在：{table}。");
        var required = new[]
        {
            target + ".STUFF_ID", target + ".MATERIAL_SUM", target + ".MATERIAL_PCT", target + ".PRICE",
            plan.MasterTable + ".QUOTE_TYPE", plan.MasterTable + ".QUOTE_NO",
            plan.MasterTable + ".CONFIRM_TAG", plan.MasterTable + ".IN_EFFECT_DATE",
            plan.DetailTable + ".PRO_NO", plan.DetailTable + ".PRICE", plan.DetailTable + ".CURR_RATE",
            plan.DetailTable + ".TAX_TYPE", plan.DetailTable + ".TAX_RATE", plan.DetailTable + ".QUOTE_TYPE",
            plan.DetailTable + ".QUOTE_NO", "STUFF.STUFF_ID",
        };
        foreach (var reference in required)
            if (!columns.Contains(reference))
                throw new EffectConfigException($"quote-parameter-recalc 列不存在：{reference}。");
        var feeList = new List<string>();
        foreach (var fee in feeFields)
        {
            var pct = fee.Replace("_SUM", "", StringComparison.Ordinal) + "_PCT";
            if (!columns.Contains(target + "." + fee))
                throw new EffectConfigException($"quote-parameter-recalc 费用列不存在：{target}.{fee}。");
            // MATERIAL_SUM and PRICE carry their own formulas and are not fee
            // breakdown entries; every other declared fee field must have its
            // percentage column present, otherwise the recalc SQL would fail at run
            // time and the config would silently drop the fee.
            if (fee.Equals("MATERIAL_SUM", StringComparison.OrdinalIgnoreCase)
                || fee.Equals("PRICE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!columns.Contains(target + "." + pct))
                throw new EffectConfigException($"quote-parameter-recalc 费用字段缺少百分比列：{target}.{pct}（feeFields 声明 {fee}）。");
            feeList.Add(fee);
        }
        return new QuoteParameterConfig(target, mode, feeList.ToArray());
    }

    private static string Req(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : throw new EffectConfigException($"quote-parameter-recalc 缺少字符串字段 '{n}'。");

    private static string[] StrArr(JsonElement e, string n)
    {
        if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new EffectConfigException($"quote-parameter-recalc.{n} 必须是非空字段名数组。");
            list.Add(item.GetString()!.Trim());
        }
        return list.ToArray();
    }
}