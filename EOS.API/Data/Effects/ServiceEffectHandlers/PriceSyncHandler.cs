using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// client/supplier-price-sync: on approval the party price master row is ensured and
/// the quote detail lines are upserted into the party price detail table — existing
/// rows are updated while preserving OLD_PRICE/OLD_PRICE_DATE, missing rows are
/// inserted. On deapprove the stored old price is restored and the quote references
/// cleared, with a latest-quote guard on the customer side. Ported from
/// P_WF_COP_QUOTE / P_WF_PUR_QUOTE. All identifiers come from closed configuration
/// checked against physical columns; only key values travel as parameters.
/// </summary>
public sealed class ClientPriceSyncHandler : IEffectServiceHandler
{
    public string EffectKey => "client-price-sync";

    public Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token) =>
        PriceSyncExecutor.ExecuteAsync(context, token);
}

public sealed class SupplierPriceSyncHandler : IEffectServiceHandler
{
    public string EffectKey => "supplier-price-sync";

    public Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token) =>
        PriceSyncExecutor.ExecuteAsync(context, token);
}

internal static class PriceSyncExecutor
{
    public static async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("price-sync 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("price-sync 需要主子表形态。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("price-sync 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var cfg = PriceSyncConfig.Parse(root, plan, columns);

        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
        {
            return await ApproveAsync(context, cfg, token);
        }
        return await DeapproveAsync(context, cfg, token);
    }

    private static async Task<int> ApproveAsync(ServiceEffectContext context, PriceSyncConfig cfg, CancellationToken token)
    {
        var plan = context.Plan;
        var parameters = new List<EffectSqlParameter>();
        var masterWhere = ServiceEffectSql.MasterKeyFilter(plan, context.MasterKeyValues, parameters);
        var system = "@system";
        parameters.Add(new EffectSqlParameter(system, "SYSTEM"));

        // Ensure a party price master row exists for the quoting party.
        var ensureMaster = $"INSERT INTO dbo.{ServiceEffectSql.Q(cfg.Master)} ({ServiceEffectSql.Q(cfg.PartyColumn)}, CREATE_PERSON, CREATE_DATE) "
            + $"SELECT M.{ServiceEffectSql.Q(cfg.PartyColumn)}, {system}, M.QUOTE_DATE "
            + $"FROM dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M WHERE {masterWhere} "
            + $"AND NOT EXISTS (SELECT 1 FROM dbo.{ServiceEffectSql.Q(cfg.Master)} P WHERE P.{ServiceEffectSql.Q(cfg.PartyColumn)}=M.{ServiceEffectSql.Q(cfg.PartyColumn)})";
        var affected = await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, ensureMaster, parameters, token);

        // Overwrite existing party price lines with the quoted values.
        var keyJoin = $"{ServiceEffectSql.Q(cfg.PartyColumn)}, PRO_NO, CURR_ID, TAX_ID, TAX_TYPE, UNIT_ID, REBATE"
            .Split(',')
            .Select(column => $"P.{ServiceEffectSql.Q(column.Trim())}=Q.{ServiceEffectSql.Q(column.Trim())}")
            .Append($"P.{ServiceEffectSql.Q(cfg.PartyColumn)}=M.{ServiceEffectSql.Q(cfg.PartyColumn)}")
            .ToList();
        var update = $"UPDATE P SET "
            + $"P.UNIT_ID=Q.UNIT_ID, P.CURR_ID=Q.CURR_ID, P.CURR_RATE=Q.CURR_RATE, P.TAX_ID=Q.TAX_ID, "
            + $"P.TAX_RATE=Q.TAX_RATE, P.TAX_TYPE=Q.TAX_TYPE, P.PRICE=Q.PRICE, P.PROCESS_PRICE=Q.PROCESS_PRICE, "
            + $"P.{ServiceEffectSql.Q(cfg.PartyProNo)}=Q.{ServiceEffectSql.Q(cfg.PartyProNo)}, P.REBATE=Q.REBATE, "
            + $"P.VERIFY_PRICE_DATE=M.QUOTE_DATE, P.OLD_PRICE=P.PRICE, P.OLD_PRICE_DATE=P.VERIFY_PRICE_DATE, "
            + $"P.{ServiceEffectSql.Q(cfg.QuoteRefs[0])}=M.QUOTE_TYPE, P.{ServiceEffectSql.Q(cfg.QuoteRefs[1])}=M.QUOTE_NO, "
            + $"P.{ServiceEffectSql.Q(cfg.QuoteRefs[2])}=Q.SERIAL_NO, P.IN_EFFECT_DATE=M.IN_EFFECT_DATE, P.REMARK=Q.REMARK "
            + $"FROM dbo.{ServiceEffectSql.Q(cfg.Detail)} P "
            + $"JOIN dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M ON {masterWhere} "
            + $"JOIN dbo.{ServiceEffectSql.Q(plan.DetailTable!)} Q ON {ServiceEffectSql.SameNameKeyJoin(plan, "Q")} "
            + $"WHERE {string.Join(" AND ", keyJoin)}"
            + (cfg.OverwriteIfNewer ? " AND M.QUOTE_DATE>=P.VERIFY_PRICE_DATE" : string.Empty);
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, update, parameters, token);

        // Insert party price lines that do not match an existing key yet.
        var insertColumns = string.Join(", ",
            new[] { cfg.PartyColumn, "PRO_NO", "UNIT_ID", "CURR_ID", "CURR_RATE", "TAX_ID", "TAX_RATE", "TAX_TYPE",
                "PRICE", "PROCESS_PRICE", cfg.PartyProNo, "REBATE", "VERIFY_PRICE_DATE", "OLD_PRICE", "OLD_PRICE_DATE",
                cfg.QuoteRefs[0], cfg.QuoteRefs[1], cfg.QuoteRefs[2], "IN_EFFECT_DATE", "REMARK" }
            .Select(ServiceEffectSql.Q));
        var notExists = $"NOT EXISTS (SELECT 1 FROM dbo.{ServiceEffectSql.Q(cfg.Detail)} P "
            + $"WHERE P.{ServiceEffectSql.Q(cfg.PartyColumn)}=M.{ServiceEffectSql.Q(cfg.PartyColumn)} "
            + $"AND P.PRO_NO=Q.PRO_NO AND P.CURR_ID=Q.CURR_ID AND P.TAX_ID=Q.TAX_ID "
            + $"AND P.TAX_TYPE=Q.TAX_TYPE AND P.UNIT_ID=Q.UNIT_ID AND P.REBATE=Q.REBATE)";
        var insert = $"INSERT INTO dbo.{ServiceEffectSql.Q(cfg.Detail)} ({insertColumns}) "
            + $"SELECT M.{ServiceEffectSql.Q(cfg.PartyColumn)}, Q.PRO_NO, Q.UNIT_ID, Q.CURR_ID, Q.CURR_RATE, Q.TAX_ID, Q.TAX_RATE, Q.TAX_TYPE, "
            + $"Q.PRICE, Q.PROCESS_PRICE, Q.{ServiceEffectSql.Q(cfg.PartyProNo)}, Q.REBATE, M.QUOTE_DATE, NULL, NULL, "
            + $"Q.QUOTE_TYPE, Q.QUOTE_NO, Q.SERIAL_NO, M.IN_EFFECT_DATE, Q.REMARK "
            + $"FROM dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M "
            + $"JOIN dbo.{ServiceEffectSql.Q(plan.DetailTable!)} Q ON {ServiceEffectSql.SameNameKeyJoin(plan, "Q")} "
            + $"WHERE {masterWhere} AND {notExists}";
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, insert, parameters, token);
        return affected;
    }

    private static async Task<int> DeapproveAsync(ServiceEffectContext context, PriceSyncConfig cfg, CancellationToken token)
    {
        var kind = ReverseKind(context);
        if (kind is null)
            throw new EffectConfigException("price-sync 解批缺少 reverse.kind，禁止无守卫执行。");
        if (kind.Equals("no-reverse", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (!kind.Equals("restore-old-price", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"price-sync 解批 reverse.kind '{kind}' 不受支持。");

        var plan = context.Plan;
        var parameters = new List<EffectSqlParameter>();
        var masterWhere = ServiceEffectSql.MasterKeyFilter(plan, context.MasterKeyValues, parameters);
        var qType = "@qt";
        var qNo = "@qn";
        parameters.Add(new EffectSqlParameter(qType, context.MasterKeyValues[0]));
        parameters.Add(new EffectSqlParameter(qNo, context.MasterKeyValues[1]));

        // Customer side refuses deapproval when a newer quote already took effect for
        // any product quoted by this document.
        if (cfg.PartyColumn.Equals("CLIENT_ID", StringComparison.OrdinalIgnoreCase))
        {
            var party = "@party";
            var inEffect = "@inEffect";
            var read = $"SELECT M.{ServiceEffectSql.Q(cfg.PartyColumn)}, M.IN_EFFECT_DATE FROM dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M WHERE {masterWhere}";
            await using var command = new SqlCommand(read, context.Connection, context.Transaction);
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                throw new EffectConfigException("price-sync 解批读不到报价主表行。");
            var partyValue = reader.IsDBNull(0) ? null : reader.GetString(0).Trim();
            var inEffectValue = reader.IsDBNull(1) ? null : (object?)reader.GetDateTime(1);
            parameters.Add(new EffectSqlParameter(party, partyValue));
            parameters.Add(new EffectSqlParameter(inEffect, inEffectValue));
            var guardSql = $"SELECT TOP 1 1 FROM dbo.{ServiceEffectSql.Q(cfg.Detail)} P "
                + $"WHERE P.{ServiceEffectSql.Q(cfg.PartyColumn)}={party} AND P.IN_EFFECT_DATE>{inEffect} "
                + $"AND EXISTS (SELECT 1 FROM dbo.{ServiceEffectSql.Q(plan.DetailTable!)} Q "
                + $"WHERE Q.{ServiceEffectSql.Q(cfg.QuoteRefs[0])}={qType} AND Q.{ServiceEffectSql.Q(cfg.QuoteRefs[1])}={qNo} AND Q.PRO_NO=P.PRO_NO)";
            await using (var guard = new SqlCommand(guardSql, context.Connection, context.Transaction))
            {
                foreach (var parameter in parameters)
                    guard.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                if (await guard.ExecuteScalarAsync(token) is not null)
                    throw new EffectValidationException("此报价单中部份产品已有最新报价");
            }
        }

        // Restore the stored old price and clear the quote references.
        var clear = new List<string>
        {
            "PRICE=OLD_PRICE",
            $"{ServiceEffectSql.Q(cfg.QuoteRefs[0])}=''",
            $"{ServiceEffectSql.Q(cfg.QuoteRefs[1])}=''",
            $"{ServiceEffectSql.Q(cfg.QuoteRefs[2])}=0",
        };
        var restore = $"UPDATE P SET {string.Join(", ", clear)} FROM dbo.{ServiceEffectSql.Q(cfg.Detail)} P "
            + $"WHERE P.{ServiceEffectSql.Q(cfg.QuoteRefs[0])}={qType} AND P.{ServiceEffectSql.Q(cfg.QuoteRefs[1])}={qNo}";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, restore, parameters, token);
    }
    private static string? ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            return null;
        return kind.GetString();
    }
}

internal sealed record PriceSyncConfig(
    string Master,
    string Detail,
    string PartyColumn,
    string PartyProNo,
    string[] QuoteRefs,
    bool OverwriteIfNewer)
{
    public static PriceSyncConfig Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("price-sync 参数必须是 JSON 对象。");
        var master = Req(root, "master");
        var detail = Req(root, "detail");
        var quoteRefs = StrArr(root, "quoteRefs");
        if (quoteRefs.Length != 3)
            throw new EffectConfigException("price-sync.quoteRefs 需要恰好三个字段（类型/单号/序号）。");
        var overwrite = root.TryGetProperty("overwriteIfNewer", out var ow) && ow.ValueKind == JsonValueKind.True;

        if (!master.StartsWith("CLIENT_", StringComparison.OrdinalIgnoreCase)
            && !master.StartsWith("SUPPLIER_", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"price-sync 主表 '{master}' 无法推导主档列。");
        var party = master.StartsWith("CLIENT_", StringComparison.OrdinalIgnoreCase) ? "CLIENT_ID" : "SUPPLIER_ID";
        var partyProNo = detail.StartsWith("CLIENT_", StringComparison.OrdinalIgnoreCase) ? "CLIENT_PRO_NO" : "SUPPLIER_PRO_NO";

        foreach (var table in new[] { master, detail, plan.MasterTable!, plan.DetailTable! })
            if (!columns.Contains(table))
                throw new EffectConfigException($"price-sync 表不存在：{table}。");
        var required = new[]
        {
            master + "." + party, master + ".CREATE_PERSON", master + ".CREATE_DATE",
            detail + "." + party, detail + ".PRO_NO", detail + ".UNIT_ID", detail + ".CURR_ID",
            detail + ".TAX_ID", detail + ".TAX_TYPE", detail + ".REBATE", detail + ".PRICE",
            detail + ".OLD_PRICE", detail + ".VERIFY_PRICE_DATE", detail + ".IN_EFFECT_DATE",
            detail + "." + partyProNo,
            plan.MasterTable + "." + party, plan.MasterTable + ".QUOTE_DATE", plan.MasterTable + ".IN_EFFECT_DATE",
            plan.MasterTable + ".QUOTE_TYPE", plan.MasterTable + ".QUOTE_NO",
            plan.DetailTable + ".SERIAL_NO", plan.DetailTable + ".PRO_NO",
        };
        foreach (var reference in required)
            if (!columns.Contains(reference))
                throw new EffectConfigException($"price-sync 列不存在：{reference}。");
        foreach (var column in quoteRefs)
            if (!columns.Contains(detail + "." + column))
                throw new EffectConfigException($"price-sync 引用列不存在：{detail}.{column}。");

        return new PriceSyncConfig(master, detail, party, partyProNo, quoteRefs, overwrite);
    }

    private static string Req(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : throw new EffectConfigException($"price-sync 缺少字符串字段 '{n}'。");

    private static string[] StrArr(JsonElement e, string n)
    {
        if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new EffectConfigException($"price-sync.{n} 必须是非空字段名数组。");
            list.Add(item.GetString()!.Trim());
        }
        return list.ToArray();
    }
}