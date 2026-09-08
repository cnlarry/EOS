using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// balance-adjust: client/supplier usable-credit and prepay adjustments plus bank
/// balance movements, with per-side currency rate conversion (ported from the legacy
/// workflow procedures). Amount source = the module master's AMOUNT_TAX, falling back
/// to AMOUNT; a master without either is a hard configuration error. "net-replace"
/// (order-change release-and-reoccupy) has no master amount column and is refused
/// until its evidence base is complete.
/// </summary>
public sealed class BalanceAdjustHandler : IEffectServiceHandler
{
    public string EffectKey => "balance-adjust";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("balance-adjust 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null)
            throw new EffectConfigException("balance-adjust 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        // The reverse structure states the deapprove semantics explicitly: the
        // net-replace change documents leave the credit untouched on deapprove
        // (legacy no-op), so a none/no-reverse kind short-circuits the whole step.
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)
            && ReverseKind(context) is "none" or "no-reverse")
        {
            return 0;
        }

        var amountLocal = BuildAmountExpression(plan, columns);
        // Approve applies the configured direction; deapprove applies the inverse
        // (usable quota: occupy=−/release=+ on approve, flipped on deapprove).
        var sign = context.ExecutionEvent == EffectEvent.Deapprove ? -1 : 1;
        var affected = 0;

        foreach (var (property, tableName, keyColumn) in new[]
                 {
                     ("client", "CLIENT", "CLIENT_ID"),
                     ("supplier", "SUPPLIER", "SUPPLIER_ID"),
                 })
        {
            if (!root.TryGetProperty(property, out var branch) || branch.ValueKind != JsonValueKind.Object)
                continue;
            if (!columns.Contains(tableName) || !columns.Contains(plan.MasterTable + "." + keyColumn))
                throw new EffectConfigException($"balance-adjust {property} 分支缺表或主表缺少 {keyColumn}。");

            if (branch.TryGetProperty("credit", out var credit))
            {
                var operation = credit.GetString()?.Trim().ToLowerInvariant()
                    ?? throw new EffectConfigException("balance-adjust credit 必须是字符串。");
                if (operation == "net-replace")
                {
                    affected += await NetReplaceAsync(context, tableName, keyColumn, columns, token);
                }
                else
                {
                    var delta = operation switch
                    {
                        "occupy" => -sign,
                        "release" => sign,
                        _ => throw new EffectConfigException("balance-adjust credit 仅支持 occupy/release/net-replace。"),
                    };
                    affected += await AdjustAsync(context, tableName, keyColumn, "CREDIT_LIMIT_NUM", amountLocal, delta, token);
                }
            }
            if (branch.TryGetProperty("prepay", out var prepay))
            {
                var operation = prepay.GetString()?.Trim().ToLowerInvariant()
                    ?? throw new EffectConfigException("balance-adjust prepay 必须是字符串。");
                var delta = operation switch
                {
                    "increase" => sign,
                    "decrease" => -sign,
                    _ => throw new EffectConfigException("balance-adjust prepay 仅支持 increase/decrease。"),
                };
                affected += await AdjustAsync(context, tableName, keyColumn, "PREPAY_SUM", amountLocal, delta, token);
            }
        }

        if (root.TryGetProperty("bank", out var bank))
        {
            var direction = bank.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()!.Trim().ToUpperInvariant()
                : throw new EffectConfigException("balance-adjust bank 缺少 direction。");
            if (direction is not ("IN" or "OUT"))
                throw new EffectConfigException("balance-adjust bank.direction 仅允许 IN/OUT。");
            if (!columns.Contains("BANK.AMOUNT") || !columns.Contains(plan.MasterTable + ".BANK_ID"))
                throw new EffectConfigException("balance-adjust bank 分支缺 BANK.AMOUNT 或主表 BANK_ID。");
            var delta = direction == "IN" ? sign : -sign;
            await CheckBankBalanceAsync(context, amountLocal, delta, token);
            affected += await AdjustAsync(context, "BANK", "BANK_ID", "AMOUNT", amountLocal, delta, token);
        }
        return affected;
    }

    /// <summary>Document amount in local currency: master AMOUNT_TAX (fallback AMOUNT) × master CURR_RATE when present.</summary>
    private static string BuildAmountExpression(ModuleEffectPlan plan, ISet<string> columns)
    {
        var amountColumn = columns.Contains(plan.MasterTable + ".AMOUNT_TAX") ? "AMOUNT_TAX"
            : columns.Contains(plan.MasterTable + ".AMOUNT") ? "AMOUNT"
            : throw new EffectConfigException($"主表 {plan.MasterTable} 缺少 AMOUNT_TAX/AMOUNT 金额列，balance-adjust 无法取值。");
        var rate = columns.Contains(plan.MasterTable + ".CURR_RATE")
            ? "ISNULL(M.[CURR_RATE], 1)"
            : "1";
        return $"ROUND(M.{ServiceEffectSql.Q(amountColumn)} * {rate}, 2)";
    }

    /// <summary>Side balance must not go below zero after a deduction (legacy behavior).</summary>
    private static async Task CheckBankBalanceAsync(
        ServiceEffectContext context,
        string amountExpression,
        int delta,
        CancellationToken token)
    {
        if (delta >= 0)
            return;
        var sql = "SELECT TOP 1 1 FROM dbo.[BANK] T JOIN dbo." + ServiceEffectSql.Q(context.Plan.MasterTable!) + " M "
            + "ON T.[BANK_ID] = M.[BANK_ID] WHERE T.[AMOUNT] + " + amountExpression + " * " + delta + " < 0";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        if (await command.ExecuteScalarAsync(token) is not null)
            throw new EffectValidationException("银行结存不足");
    }

    /// <summary>Additive side update with currency conversion: col = (col*rate + amount*Δ)/rate (round 2).</summary>
    /// <summary>
    /// credit=net-replace: the change document replaces the referenced order/purchase
    /// amounts, so the party credit limit is adjusted by the net foreign-currency delta
    /// (new order amount minus the old amounts captured on the change lines). Ported
    /// from the two-step credit re-occupation in P_WF_COP_ORDER_CHANGE / P_WF_PUR_PURCHASE_CHANGE.
    /// </summary>
    private static async Task<int> NetReplaceAsync(
        ServiceEffectContext context,
        string partyTable,
        string partyColumn,
        ISet<string> columns,
        CancellationToken token)
    {
        var plan = context.Plan;
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("balance-adjust net-replace 需要主子表形态。");
        var originalTable = plan.MasterTable.Replace("_CHANGE", "", StringComparison.OrdinalIgnoreCase);
        var purchaseSide = originalTable.StartsWith("PUR_", StringComparison.OrdinalIgnoreCase);
        var pkPrefix = purchaseSide ? "PURCHASE" : "ORDER";
        var pk0 = pkPrefix + "_TYPE";
        var pk1 = pkPrefix + "_NO";
        var serialCol = pkPrefix + "_SERIAL_NO";
        foreach (var reference in new[]
        {
            originalTable, originalTable + "." + pk0, originalTable + "." + pk1,
            originalTable + ".AMOUNT_TAX", originalTable + ".CURR_RATE", originalTable + ".SERIAL_NO",
            originalTable + ".REBATE", originalTable + ".TAX_TYPE", originalTable + ".TAX_RATE",
            plan.DetailTable + "." + pk0, plan.DetailTable + "." + pk1, plan.DetailTable + "." + serialCol,
            plan.DetailTable + ".OLD_QTY", plan.DetailTable + ".OLD_PRICE",
            plan.MasterTable + "." + pk0, plan.MasterTable + "." + pk1,
            partyTable + "." + partyColumn, partyTable + ".CURR_ID",
        })
        {
            if (!columns.Contains(reference))
                throw new EffectConfigException($"balance-adjust net-replace 列不存在：{reference}。");
        }
        if (plan.MasterPkOrder.Count < 2
            || !columns.Contains(plan.DetailTable + "." + plan.MasterPkOrder[0])
            || !columns.Contains(plan.DetailTable + "." + plan.MasterPkOrder[1]))
        {
            throw new EffectConfigException("balance-adjust net-replace 变更明细缺少变更单主键列。");
        }

        var parameters = new List<EffectSqlParameter>();
        var masterWhere = ServiceEffectSql.MasterKeyFilter(plan, context.MasterKeyValues, parameters);
        var party = "@party";
        var ref0 = "@k0";
        var ref1 = "@k1";
        var read = $"SELECT M.{ServiceEffectSql.Q(partyColumn)}, M.{ServiceEffectSql.Q(pk0)}, M.{ServiceEffectSql.Q(pk1)} "
            + $"FROM dbo.{ServiceEffectSql.Q(plan.MasterTable)} M WHERE {masterWhere}";
        await using var readCommand = new SqlCommand(read, context.Connection, context.Transaction);
        foreach (var parameter in parameters)
            readCommand.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var readReader = await readCommand.ExecuteReaderAsync(token);
        if (!await readReader.ReadAsync(token))
            throw new EffectConfigException("balance-adjust net-replace 读不到变更主表行。");
        var partyValue = readReader.IsDBNull(0) ? null : readReader.GetString(0).Trim();
        var ref0Value = readReader.IsDBNull(1) ? null : readReader.GetString(1).Trim();
        var ref1Value = readReader.IsDBNull(2) ? null : readReader.GetString(2).Trim();
        if (string.IsNullOrWhiteSpace(ref0Value) || string.IsNullOrWhiteSpace(ref1Value))
            throw new EffectConfigException("balance-adjust net-replace 变更主表缺少原单键。");
        parameters.Add(new EffectSqlParameter(party, partyValue));
        parameters.Add(new EffectSqlParameter(ref0, ref0Value));
        parameters.Add(new EffectSqlParameter(ref1, ref1Value));

        var current = $"SELECT AMOUNT_TAX, CURR_RATE FROM dbo.{ServiceEffectSql.Q(originalTable)} "
            + $"WHERE {ServiceEffectSql.Q(pk0)}=@k0 AND {ServiceEffectSql.Q(pk1)}=@k1";
        double newForeign;
        double newRate;
        await using (var currentCommand = new SqlCommand(current, context.Connection, context.Transaction))
        {
            foreach (var parameter in parameters)
                currentCommand.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using var currentReader = await currentCommand.ExecuteReaderAsync(token);
            if (!await currentReader.ReadAsync(token))
                throw new EffectConfigException("balance-adjust net-replace 读不到原单主表行。");
            newForeign = (currentReader.IsDBNull(0) ? 0d : currentReader.GetDouble(0))
                * (currentReader.IsDBNull(1) ? 1d : currentReader.GetDouble(1));
            newRate = currentReader.IsDBNull(1) ? 1d : currentReader.GetDouble(1);
        }

        var oldSql = $"SELECT ROUND(SUM(ROUND(CASE T.TAX_TYPE WHEN 'O' "
            + $"THEN d.OLD_QTY*d.OLD_PRICE*COALESCE(T.REBATE,0)/100*(1+T.TAX_RATE/100) "
            + $"ELSE d.OLD_QTY*d.OLD_PRICE*COALESCE(T.REBATE,0)/100 END,2)),2) "
            + $"FROM dbo.{ServiceEffectSql.Q(plan.DetailTable)} d "
            + $"JOIN dbo.{ServiceEffectSql.Q(originalTable)} T "
            + $"ON T.{ServiceEffectSql.Q(pk0)}=d.{ServiceEffectSql.Q(pk0)} "
            + $"AND T.{ServiceEffectSql.Q(pk1)}=d.{ServiceEffectSql.Q(pk1)} AND T.SERIAL_NO=d.{ServiceEffectSql.Q(serialCol)} "
            + $"WHERE d.{ServiceEffectSql.Q(plan.MasterPkOrder[0])}=@mk0 AND d.{ServiceEffectSql.Q(plan.MasterPkOrder[1])}=@mk1";
        double oldAmountTax;
        await using (var oldCommand = new SqlCommand(oldSql, context.Connection, context.Transaction))
        {
            foreach (var parameter in parameters)
                oldCommand.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            var scalar = await oldCommand.ExecuteScalarAsync(token);
            oldAmountTax = scalar is null or DBNull ? 0d : Convert.ToDouble(scalar);
        }
        var delta = newForeign - oldAmountTax * newRate;

        var rate = "@rate";
        var rateSql = $"SELECT COALESCE(c.CURR_RATE, 1) FROM dbo.{ServiceEffectSql.Q(partyTable)} p "
            + $"LEFT JOIN dbo.CURR c ON c.CURR_ID=p.CURR_ID WHERE p.{ServiceEffectSql.Q(partyColumn)}={party}";
        double partyRate;
        await using (var rateCommand = new SqlCommand(rateSql, context.Connection, context.Transaction))
        {
            foreach (var parameter in parameters)
                rateCommand.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            var scalar = await rateCommand.ExecuteScalarAsync(token);
            partyRate = scalar is null or DBNull ? 1d : Convert.ToDouble(scalar);
        }
        if (partyRate == 0d)
        {
            partyRate = 1d;
        }

        parameters.Add(new EffectSqlParameter(rate, partyRate));
        parameters.Add(new EffectSqlParameter("@delta", delta));
        var update = $"UPDATE dbo.{ServiceEffectSql.Q(partyTable)} SET "
            + $"CREDIT_LIMIT_NUM=ROUND((CREDIT_LIMIT_NUM*{rate} - @delta)/{rate}, 2) "
            + $"WHERE {ServiceEffectSql.Q(partyColumn)}={party}";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, update, parameters, token);
    }
    private static string? ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            return null;
        return kind.GetString();
    }

    private static async Task<int> AdjustAsync(
        ServiceEffectContext context,
        string tableName,
        string keyColumn,
        string valueColumn,
        string amountExpression,
        int delta,
        CancellationToken token)
    {
        // Side currency rate comes from the target row's own currency; the additive
        // update keeps the stored value expressed in that currency.
        var rate = "ISNULL((SELECT c.[CURR_RATE] FROM dbo.[CURR] c WHERE c.[CURR_ID]=T.[CURR_ID]), 1)";
        var parameters = new List<EffectSqlParameter>();
        var where = ServiceEffectSql.MasterKeyFilter(context.Plan, context.MasterKeyValues, parameters);
        var sql = $"UPDATE T SET T.{ServiceEffectSql.Q(valueColumn)} = ROUND((T.{ServiceEffectSql.Q(valueColumn)} * {rate} + {amountExpression} * {delta}) / {rate}, 2) "
            + $"FROM dbo.{ServiceEffectSql.Q(tableName)} T JOIN dbo.{ServiceEffectSql.Q(context.Plan.MasterTable!)} M "
            + $"ON T.{ServiceEffectSql.Q(keyColumn)} = M.{ServiceEffectSql.Q(keyColumn)} WHERE {where}";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
    }
}
