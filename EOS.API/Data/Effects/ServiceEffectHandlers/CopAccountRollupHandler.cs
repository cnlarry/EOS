using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// cop-account-rollup: 应收对帐单保存后的主表金额汇总（原 `CopDomainRules.CopAccountAfterSaveAsync` 的
/// 末段，来源旧过程 `P_COP_ACCOUNT_After_Save` 的
/// `update COP_ACCOUNT_M set AMOUNT=d.AMOUNT, TAX_SUM=d.TAX_SUM, AMOUNT_TAX=d.AMOUNT_TAX,
///  SUM_AMOUNT=d.AMOUNT_TAX+OTHER_PRICE, QTY_TOTAL=d.QTY_ALL from (...) d`）：
/// 明细按单据键聚合后回写主表 金额/税额/价税合计/数量合计，含税总额 ＝ 价税合计 + 其它费用。
/// 与旧过程逐字一致：明细为空时**不回写**（走内连接而非标量子查询），"其它费用"不做空值兜底。
/// 参数闭合：两张表 + 各列名 + 舍入位数，全部校验为物理列；单据键值只作参数传入。
/// </summary>
public sealed class CopAccountRollupHandler : IEffectServiceHandler
{
    public string EffectKey => "cop-account-rollup";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("cop-account-rollup 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("cop-account-rollup 缺少单据主键值。");
        if (string.IsNullOrWhiteSpace(context.Plan.MasterTable))
            throw new EffectConfigException("cop-account-rollup 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan.MasterTable, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;
        // 汇总语句里主表与派生表同名单据键列并存，外层条件必须带别名限定
        var scope = "m." + Q(config.Master.TypeField) + "=@type AND m." + Q(config.Master.NoField) + "=@no";

        var rollup = "UPDATE m SET m." + Q(config.Master.AmountField) + "=s.AMOUNT_SUM, m."
            + Q(config.Master.TaxSumField) + "=s.TAX_SUM_SUM, m." + Q(config.Master.AmountTaxField)
            + "=s.AMOUNT_TAX_SUM, m." + Q(config.Master.SumAmountField) + "=s.AMOUNT_TAX_SUM+m."
            + Q(config.Master.OtherPriceField) + ", m." + Q(config.Master.QtyTotalField)
            + "=s.QTY_SUM FROM dbo." + Q(context.Plan.MasterTable) + " m INNER JOIN (SELECT d."
            + Q(config.Master.TypeField) + ", d." + Q(config.Master.NoField) + ", ROUND(SUM(d."
            + Q(config.Detail.AmountTaxField) + "),@digits) AMOUNT_TAX_SUM, ROUND(SUM(d."
            + Q(config.Detail.AmountField) + "),@digits) AMOUNT_SUM, ROUND(SUM(d." + Q(config.Detail.TaxSumField)
            + "),@digits) TAX_SUM_SUM, ROUND(SUM(d." + Q(config.Detail.QtyField)
            + "),@digits) QTY_SUM FROM dbo." + Q(config.Detail.Table) + " d WHERE d." + Q(config.Master.TypeField)
            + "=@type AND d." + Q(config.Master.NoField) + "=@no GROUP BY d." + Q(config.Master.TypeField) + ", d."
            + Q(config.Master.NoField) + ") s ON s." + Q(config.Master.TypeField) + "=m."
            + Q(config.Master.TypeField) + " AND s." + Q(config.Master.NoField) + "=m." + Q(config.Master.NoField)
            + " WHERE " + scope + ";";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, rollup,
            [
                new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
                new EffectSqlParameter("@digits", config.RoundDigits),
            ], token);
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    /// <summary>参数解析（fail-closed：两张表的每个列名都必须物理存在，舍入位数 0..6）。</summary>
    internal static CopAccountRollupConfig Parse(JsonElement root, string masterTable, ISet<string> columns)
    {
        var master = Section(root, "master");
        var detail = Section(root, "detail");
        var config = new CopAccountRollupConfig(
            new CopAccountRollupMaster(Required(master, "typeField"), Required(master, "noField"),
                Required(master, "amountField"), Required(master, "taxSumField"), Required(master, "amountTaxField"),
                Required(master, "sumAmountField"), Required(master, "otherPriceField"),
                Required(master, "qtyTotalField")),
            new CopAccountRollupDetail(Required(detail, "table"), Required(detail, "qtyField"),
                Required(detail, "amountField"), Required(detail, "taxSumField"), Required(detail, "amountTaxField")),
            root.TryGetProperty("roundDigits", out var digits) && digits.ValueKind == JsonValueKind.Number
                && digits.TryGetInt32(out var value) ? value : 2);
        if (config.RoundDigits is < 0 or > 6)
            throw new EffectConfigException("cop-account-rollup 的 roundDigits 必须在 0..6 之间。");
        foreach (var (table, column) in new[]
                 {
                     (masterTable, config.Master.TypeField), (masterTable, config.Master.NoField),
                     (masterTable, config.Master.AmountField), (masterTable, config.Master.TaxSumField),
                     (masterTable, config.Master.AmountTaxField), (masterTable, config.Master.SumAmountField),
                     (masterTable, config.Master.OtherPriceField), (masterTable, config.Master.QtyTotalField),
                     (config.Detail.Table, config.Master.TypeField), (config.Detail.Table, config.Master.NoField),
                     (config.Detail.Table, config.Detail.QtyField), (config.Detail.Table, config.Detail.AmountField),
                     (config.Detail.Table, config.Detail.TaxSumField),
                     (config.Detail.Table, config.Detail.AmountTaxField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"cop-account-rollup 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static JsonElement Section(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new EffectConfigException($"cop-account-rollup 缺少对象字段 {name}。");

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"cop-account-rollup 缺少字符串字段 {name}。");
}

internal sealed record CopAccountRollupMaster(string TypeField, string NoField, string AmountField,
    string TaxSumField, string AmountTaxField, string SumAmountField, string OtherPriceField, string QtyTotalField);
internal sealed record CopAccountRollupDetail(string Table, string QtyField, string AmountField, string TaxSumField,
    string AmountTaxField);
internal sealed record CopAccountRollupConfig(CopAccountRollupMaster Master, CopAccountRollupDetail Detail,
    int RoundDigits);
