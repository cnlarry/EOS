using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// cus-account-sync: 海关对帐单保存后的"明细取产品单重/毛重 + 明细对帐数量补零 + 主表汇总"三步链
/// （原 `CusDomainRules.CusAccountAfterSaveAsync`，其判据来源为旧过程 `P_CUS_ACCOUNT_After_Save`）：
///   ① 明细 单重/毛重 取产品档案，对帐数量/毛重数量 ＝ 数量 × 单重/毛重；
///   ② 对帐数量为 0（含空）的明细补为对帐数量；
///   ③ 主表 金额/税额/价税合计/数量合计/对帐数量/毛重数量 按明细求和（按配置位数 ROUND），
///      含税总额 ＝ 价税合计 + 其它费用，加工金额 ＝ 对帐数量 × 加工单价。
/// 与旧过程逐字一致：求和结果与"其它费用/加工单价"的运算**不做空值兜底**（任一侧为空则整体为空），
/// 与旧实现同为单条 UPDATE、右值取更新前的行值。
/// 参数闭合：三张表与各列名分组声明，全部校验为物理列；单据键值只作参数传入。
/// </summary>
public sealed class CusAccountSyncHandler : IEffectServiceHandler
{
    public string EffectKey => "cus-account-sync";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("cus-account-sync 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("cus-account-sync 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;
        var keyParameters = new[]
        {
            new EffectSqlParameter("@type", type),
            new EffectSqlParameter("@no", no),
        };
        var scope = Q(config.Master.TypeField) + "=@type AND " + Q(config.Master.NoField) + "=@no";
        // 汇总语句里主表与派生表同名单据键列并存，外层条件必须带别名限定
        var masterScope = "m." + Q(config.Master.TypeField) + "=@type AND m." + Q(config.Master.NoField) + "=@no";
        var affected = 0;

        // ① 明细单重/毛重取产品档案，对帐数量与毛重数量按数量换算
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
            "UPDATE d SET d." + Q(config.Detail.SuttleField) + "=p." + Q(config.Product.SuttleField)
            + ", d." + Q(config.Detail.CusQtyField) + "=d." + Q(config.Detail.QtyField) + "*p." + Q(config.Product.SuttleField)
            + ", d." + Q(config.Detail.GrossWeightField) + "=p." + Q(config.Product.GrossWeightField)
            + ", d." + Q(config.Detail.CusGrossQtyField) + "=d." + Q(config.Detail.QtyField) + "*p."
            + Q(config.Product.GrossWeightField)
            + " FROM dbo." + Q(config.Detail.Table) + " d INNER JOIN dbo." + Q(config.Product.Table) + " p ON p."
            + Q(config.Product.ProductField) + "=d." + Q(config.Detail.ProductField)
            + " WHERE d." + Q(config.Master.TypeField) + "=@type AND d." + Q(config.Master.NoField) + "=@no;",
            keyParameters, token);

        // ② 对帐数量为空或为零的明细补为对帐数量
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
            "UPDATE dbo." + Q(config.Detail.Table) + " SET " + Q(config.Detail.AccountQtyField) + "="
            + Q(config.Detail.CusQtyField) + " WHERE " + scope + " AND ISNULL("
            + Q(config.Detail.AccountQtyField) + ",0)=0;", keyParameters, token);

        // ③ 主表汇总（明细先按单据键聚合成一行；子查询的列全部带别名限定，避免与外层主表同名同列产生歧义）
        var rollup = "UPDATE m SET m." + Q(config.Master.AmountField) + "=s.AMOUNT_SUM, m."
            + Q(config.Master.TaxSumField) + "=s.TAX_SUM_SUM, m." + Q(config.Master.AmountTaxField)
            + "=s.AMOUNT_TAX_SUM, m." + Q(config.Master.SumAmountField) + "=s.AMOUNT_TAX_SUM+m."
            + Q(config.Master.OtherPriceField) + ", m." + Q(config.Master.QtyTotalField) + "=s.QTY_SUM, m."
            + Q(config.Master.CusQtyField) + "=s.ACCOUNT_QTY_SUM, m." + Q(config.Master.CusGrossQtyField)
            + "=s.CUS_GROSS_QTY_SUM, m." + Q(config.Master.ProcessAmountField) + "=s.ACCOUNT_QTY_SUM*m."
            + Q(config.Master.ProcessPriceField) + " FROM dbo." + Q(config.Master.Table) + " m INNER JOIN (SELECT d."
            + Q(config.Master.TypeField) + ", d." + Q(config.Master.NoField) + ", ROUND(SUM(d."
            + Q(config.Detail.AmountField) + "),@digits) AMOUNT_SUM, ROUND(SUM(d." + Q(config.Detail.TaxSumField)
            + "),@digits) TAX_SUM_SUM, ROUND(SUM(d." + Q(config.Detail.AmountTaxField) + "),@digits) AMOUNT_TAX_SUM, ROUND(SUM(d."
            + Q(config.Detail.QtyField) + "),@digits) QTY_SUM, ROUND(SUM(d." + Q(config.Detail.AccountQtyField)
            + "),@digits) ACCOUNT_QTY_SUM, ROUND(SUM(d." + Q(config.Detail.CusGrossQtyField)
            + "),@digits) CUS_GROSS_QTY_SUM FROM dbo." + Q(config.Detail.Table) + " d WHERE d."
            + Q(config.Master.TypeField) + "=@type AND d." + Q(config.Master.NoField) + "=@no GROUP BY d."
            + Q(config.Master.TypeField) + ", d." + Q(config.Master.NoField) + ") s ON s." + Q(config.Master.TypeField)
            + "=m." + Q(config.Master.TypeField) + " AND s." + Q(config.Master.NoField) + "=m." + Q(config.Master.NoField)
            + " WHERE " + masterScope + ";";
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, rollup,
            [new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
             new EffectSqlParameter("@digits", config.RoundDigits)], token);
        return affected;
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    /// <summary>参数解析（fail-closed：三张表的每个列名都必须物理存在）。</summary>
    internal static CusAccountSyncConfig Parse(JsonElement root, ISet<string> columns)
    {
        var master = Section(root, "master");
        var detail = Section(root, "detail");
        var product = Section(root, "product");
        var config = new CusAccountSyncConfig(
            new CusAccountMaster(Required(master, "table"), Required(master, "typeField"), Required(master, "noField"),
                Required(master, "amountField"), Required(master, "taxSumField"), Required(master, "amountTaxField"),
                Required(master, "sumAmountField"), Required(master, "qtyTotalField"), Required(master, "cusQtyField"),
                Required(master, "cusGrossQtyField"), Required(master, "processAmountField"),
                Required(master, "otherPriceField"), Required(master, "processPriceField")),
            new CusAccountDetail(Required(detail, "table"), Required(detail, "productField"),
                Required(detail, "qtyField"), Required(detail, "suttleField"), Required(detail, "cusQtyField"),
                Required(detail, "grossWeightField"), Required(detail, "cusGrossQtyField"),
                Required(detail, "accountQtyField"), Required(detail, "amountField"), Required(detail, "taxSumField"),
                Required(detail, "amountTaxField")),
            new CusAccountProduct(Required(product, "table"), Required(product, "productField"),
                Required(product, "suttleField"), Required(product, "grossWeightField")),
            root.TryGetProperty("roundDigits", out var digits) && digits.ValueKind == JsonValueKind.Number
                && digits.TryGetInt32(out var value) ? value : 2);
        if (config.RoundDigits is < 0 or > 6)
            throw new EffectConfigException("cus-account-sync 的 roundDigits 必须在 0..6 之间。");
        foreach (var (table, column) in new[]
                 {
                     (config.Master.Table, config.Master.TypeField), (config.Master.Table, config.Master.NoField),
                     (config.Master.Table, config.Master.AmountField), (config.Master.Table, config.Master.TaxSumField),
                     (config.Master.Table, config.Master.AmountTaxField), (config.Master.Table, config.Master.SumAmountField),
                     (config.Master.Table, config.Master.QtyTotalField), (config.Master.Table, config.Master.CusQtyField),
                     (config.Master.Table, config.Master.CusGrossQtyField),
                     (config.Master.Table, config.Master.ProcessAmountField),
                     (config.Master.Table, config.Master.OtherPriceField),
                     (config.Master.Table, config.Master.ProcessPriceField),
                     (config.Detail.Table, config.Master.TypeField), (config.Detail.Table, config.Master.NoField),
                     (config.Detail.Table, config.Detail.ProductField), (config.Detail.Table, config.Detail.QtyField),
                     (config.Detail.Table, config.Detail.SuttleField), (config.Detail.Table, config.Detail.CusQtyField),
                     (config.Detail.Table, config.Detail.GrossWeightField),
                     (config.Detail.Table, config.Detail.CusGrossQtyField),
                     (config.Detail.Table, config.Detail.AccountQtyField),
                     (config.Detail.Table, config.Detail.AmountField), (config.Detail.Table, config.Detail.TaxSumField),
                     (config.Detail.Table, config.Detail.AmountTaxField),
                     (config.Product.Table, config.Product.ProductField),
                     (config.Product.Table, config.Product.SuttleField),
                     (config.Product.Table, config.Product.GrossWeightField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"cus-account-sync 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static JsonElement Section(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new EffectConfigException($"cus-account-sync 缺少对象字段 {name}。");

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"cus-account-sync 缺少字符串字段 {name}。");
}

internal sealed record CusAccountMaster(string Table, string TypeField, string NoField, string AmountField,
    string TaxSumField, string AmountTaxField, string SumAmountField, string QtyTotalField, string CusQtyField,
    string CusGrossQtyField, string ProcessAmountField, string OtherPriceField, string ProcessPriceField);
internal sealed record CusAccountDetail(string Table, string ProductField, string QtyField, string SuttleField,
    string CusQtyField, string GrossWeightField, string CusGrossQtyField, string AccountQtyField, string AmountField,
    string TaxSumField, string AmountTaxField);
internal sealed record CusAccountProduct(string Table, string ProductField, string SuttleField, string GrossWeightField);
internal sealed record CusAccountSyncConfig(
    CusAccountMaster Master, CusAccountDetail Detail, CusAccountProduct Product, int RoundDigits);
