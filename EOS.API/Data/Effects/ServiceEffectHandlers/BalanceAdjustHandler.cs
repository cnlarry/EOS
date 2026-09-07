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
                    throw new EffectConfigException(
                        "balance-adjust credit=net-replace（变更单净替换）语义证据未齐，禁止执行；请改走定制效果或补齐证据后注册。");
                var delta = operation switch
                {
                    "occupy" => -sign,
                    "release" => sign,
                    _ => throw new EffectConfigException("balance-adjust credit 仅支持 occupy/release/net-replace。"),
                };
                affected += await AdjustAsync(context, tableName, keyColumn, "CREDIT_LIMIT_NUM", amountLocal, delta, token);
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
