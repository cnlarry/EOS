using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// pur-purchase-sync: 采购单保存后的"待购表（`PUR_PURCHASE_MORE`）汇总同步"整链
/// （原 `PurDomainRules.PurPurchaseAfterSaveAsync` 的写段）：
///   ① 待购表中尚未成行的产品按 `SUM(REQUIRE_QTY)` 补明细行（序号递增，仓库/数量/单位取产品档案与待购合计）；
///   ② 主表 币别/汇率/税种/税率/税别 带到本单全部明细；
///   ③ 厂商计价（同厂商+产品+单位+币别+税别+税种）回填 单价/税率/汇率/折扣；
///   ④ 明细 应购数量 清零后按产品汇总回填；
///   ⑤ 明细 金额/价税合计/税额 按税种 I/O/N 公式重算；
///   ⑥ 主表 金额/价税合计/税额 = 明细按汇率折算后求和 ÷ 主表汇率（ROUND 2）；
///   ⑦ 数量分配：待购表 QTY 清零后按 `(产品, 序号)` 顺序把明细 QTY 逐行分给待购行；
///   ⑧ 主表 采购订单号/生产单号 = 待购表去重非空值按序串联（全空不回写）。
/// 无待购行时只做 ⑧（与既有实现一致）。①与⑦需逐行处理，故在事务内读取后循环执行。
/// 参数闭合：五张表（主表/明细/待购/产品/厂商计价）与各列名分组声明，全部校验为物理列。
/// </summary>
public sealed class PurPurchaseSyncHandler : IEffectServiceHandler
{
    public string EffectKey => "pur-purchase-sync";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("pur-purchase-sync 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("pur-purchase-sync 缺少单据主键值。");
        if (string.IsNullOrWhiteSpace(context.Plan.MasterTable))
            throw new EffectConfigException("pur-purchase-sync 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan.MasterTable, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;
        var master = Q(context.Plan.MasterTable);
        var detail = Q(config.Detail.Table);
        var more = Q(config.More.Table);
        var m = config.Master;
        var d = config.Detail;
        var scope = $"{Q(m.TypeField)}=@type AND {Q(m.NoField)}=@no";
        var detailScope = $"d.{Q(m.TypeField)}=@type AND d.{Q(m.NoField)}=@no";
        var parameters = new[]
        {
            new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
        };
        var affected = 0;

        var hasMore = await ScalarAsync(context,
            "SELECT TOP 1 1 FROM dbo." + more + " WHERE " + scope + ";", type, no, token) is not null;
        if (hasMore)
        {
            // ① 补明细行
            var maxSerial = Convert.ToInt32(await ScalarAsync(context,
                "SELECT ISNULL(MAX(" + Q(d.SerialField) + "),0) FROM dbo." + detail + " WHERE " + scope + ";",
                type, no, token) ?? 0);
            var moreQty = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            await using (var read = new SqlCommand("SELECT LTRIM(RTRIM(" + Q(d.ProductField) + ")), SUM("
                + Q(config.More.RequireQtyField) + ") FROM dbo." + more + " WHERE " + scope + " GROUP BY "
                + Q(d.ProductField) + ";", context.Connection, context.Transaction))
            {
                read.Parameters.AddWithValue("@type", type);
                read.Parameters.AddWithValue("@no", no);
                await using var reader = await read.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    moreQty[reader.GetString(0)] = Convert.ToDecimal(reader.GetValue(1));
            }
            var missing = new List<string>();
            await using (var read = new SqlCommand("SELECT DISTINCT LTRIM(RTRIM(m." + Q(d.ProductField) + ")) FROM dbo."
                + more + " m WHERE m." + Q(m.TypeField) + "=@type AND m." + Q(m.NoField) + "=@no AND NOT EXISTS (SELECT 1 FROM dbo."
                + detail + " x WHERE x." + Q(m.TypeField) + "=m." + Q(m.TypeField) + " AND x." + Q(m.NoField)
                + "=m." + Q(m.NoField) + " AND x." + Q(d.ProductField) + "=m." + Q(d.ProductField) + ");",
                context.Connection, context.Transaction))
            {
                read.Parameters.AddWithValue("@type", type);
                read.Parameters.AddWithValue("@no", no);
                await using var reader = await read.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) missing.Add(reader.GetString(0));
            }
            foreach (var proNo in missing)
            {
                maxSerial++;
                var insert = "INSERT INTO dbo." + detail + " (" + Q(m.TypeField) + "," + Q(m.NoField) + ","
                    + Q(d.SerialField) + "," + Q(d.ProductField) + "," + Q(config.Product.DepotField) + ","
                    + Q(d.QtyField) + "," + Q(d.ReceiveQtyField) + "," + Q(d.UnitField) + ") SELECT @type, @no, @serial,"
                    + " p." + Q(config.Product.ProductField) + ", (SELECT p2." + Q(config.Product.DepotField)
                    + " FROM dbo." + Q(config.Product.Table) + " p2 WHERE p2." + Q(config.Product.ProductField)
                    + "=@product), @qty, 0, (SELECT p3." + Q(config.Product.UnitField) + " FROM dbo."
                    + Q(config.Product.Table) + " p3 WHERE p3." + Q(config.Product.ProductField)
                    + "=@product) FROM dbo." + Q(config.Product.Table) + " p WHERE p." + Q(config.Product.ProductField)
                    + "=@product;";
                affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, insert,
                    [
                        new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
                        new EffectSqlParameter("@serial", maxSerial), new EffectSqlParameter("@product", proNo),
                        new EffectSqlParameter("@qty", moreQty.TryGetValue(proNo, out var moreValue) ? moreValue : 0m),
                    ], token);
            }

            // ② 主表币别/税率带到明细
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
                "UPDATE d SET d." + Q(d.CurrencyField) + "=mm." + Q(m.CurrencyField) + ", d." + Q(d.CurrencyRateField)
                + "=mm." + Q(m.CurrencyRateField) + ", d." + Q(d.TaxTypeField) + "=mm." + Q(m.TaxTypeField) + ", d."
                + Q(d.TaxRateField) + "=mm." + Q(m.TaxRateField) + ", d." + Q(d.TaxIdField) + "=mm." + Q(m.TaxIdField)
                + " FROM dbo." + detail + " d INNER JOIN dbo." + master + " mm ON mm." + Q(m.TypeField) + "=d."
                + Q(m.TypeField) + " AND mm." + Q(m.NoField) + "=d." + Q(m.NoField) + " WHERE " + detailScope + ";",
                parameters, token);

            // ③ 厂商计价回填单价
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
                "UPDATE d SET d." + Q(d.PriceField) + "=p." + Q(config.SupplierPrice.PriceField) + ", d."
                + Q(d.TaxRateField) + "=p." + Q(config.SupplierPrice.TaxRateField) + ", d." + Q(d.CurrencyRateField)
                + "=p." + Q(config.SupplierPrice.CurrencyRateField) + ", d." + Q(d.RebateField) + "=p."
                + Q(config.SupplierPrice.RebateField) + " FROM dbo." + detail + " d INNER JOIN dbo." + master
                + " mm ON mm." + Q(m.TypeField) + "=d." + Q(m.TypeField) + " AND mm." + Q(m.NoField) + "=d."
                + Q(m.NoField) + " INNER JOIN dbo." + Q(config.SupplierPrice.Table) + " p ON p."
                + Q(config.SupplierPrice.SupplierField) + "=mm." + Q(m.SupplierField) + " AND p."
                + Q(config.SupplierPrice.ProductField) + "=d." + Q(d.ProductField) + " AND p."
                + Q(config.SupplierPrice.UnitField) + "=d." + Q(d.UnitField) + " AND p."
                + Q(config.SupplierPrice.CurrencyField) + "=d." + Q(d.CurrencyField) + " AND p."
                + Q(config.SupplierPrice.TaxIdField) + "=d." + Q(d.TaxIdField) + " AND p."
                + Q(config.SupplierPrice.TaxTypeField) + "=d." + Q(d.TaxTypeField) + " WHERE " + detailScope + ";",
                parameters, token);

            // ④ 应购数量清零并回填
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
                "UPDATE dbo." + detail + " SET " + Q(d.RequireQtyField) + "=0 WHERE " + scope + ";", parameters, token);
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
                "UPDATE d SET d." + Q(d.RequireQtyField) + "=s.REQUIRE_SUM FROM dbo." + detail + " d INNER JOIN (SELECT m."
                + Q(m.TypeField) + ", m." + Q(m.NoField) + ", m." + Q(d.ProductField) + ", SUM(m."
                + Q(config.More.RequireQtyField) + ") REQUIRE_SUM FROM dbo." + more + " m WHERE m." + Q(m.TypeField)
                + "=@type AND m." + Q(m.NoField) + "=@no GROUP BY m." + Q(m.TypeField) + ", m." + Q(m.NoField) + ", m."
                + Q(d.ProductField) + ") s ON d." + Q(m.TypeField) + "=s." + Q(m.TypeField) + " AND d." + Q(m.NoField)
                + "=s." + Q(m.NoField) + " AND d." + Q(d.ProductField) + "=s." + Q(d.ProductField) + " WHERE "
                + detailScope + ";", parameters, token);

            // ⑤ 明细金额按税种公式重算（I/O/N 与既有实现同形）
            var qty = "QTY_ALIAS";
            var taxType = "d." + Q(d.TaxTypeField);
            var qtySql = "d." + Q(d.QtyField);
            var priceSql = "d." + Q(d.PriceField);
            var rebateSql = "ISNULL(d." + Q(d.RebateField) + ",100)/100";
            var rateSql = "ISNULL(d." + Q(d.TaxRateField) + ",0)/100";
            _ = qty; _ = taxType;
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
                "UPDATE d SET d." + Q(d.AmountField) + "=CASE " + taxType + " WHEN 'I' THEN ROUND(("
                + qtySql + "*" + priceSql + "*" + rebateSql + ")/(1+" + rateSql + "),2) ELSE ROUND(" + qtySql + "*"
                + priceSql + "*" + rebateSql + ",2) END, " + "d." + Q(d.AmountTaxField) + "=CASE " + taxType
                + " WHEN 'O' THEN ROUND(" + qtySql + "*" + priceSql + "*" + rebateSql + "*(1+" + rateSql + "),2) ELSE ROUND("
                + qtySql + "*" + priceSql + "*" + rebateSql + ",2) END, " + "d." + Q(d.TaxSumField) + "=CASE " + taxType
                + " WHEN 'N' THEN 0 WHEN 'O' THEN ROUND(" + qtySql + "*" + priceSql + "*" + rebateSql + "*" + rateSql
                + ",2) WHEN 'I' THEN ROUND(" + qtySql + "*" + priceSql + "*" + rebateSql + "*" + rateSql + "/(1+" + rateSql
                + "),2) ELSE 0 END FROM dbo." + detail + " d WHERE " + detailScope + ";", parameters, token);

            // ⑥ 主表金额汇总（明细×汇率 求和 ÷ 主表汇率）
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
                "UPDATE mm SET mm." + Q(m.AmountField) + "=ROUND(s.AMOUNT_SUM/mm." + Q(m.CurrencyRateField) + ",2), mm."
                + Q(m.AmountTaxField) + "=ROUND(s.AMOUNT_TAX_SUM/mm." + Q(m.CurrencyRateField) + ",2), mm."
                + Q(m.TaxSumField) + "=ROUND(s.TAX_SUM_SUM/mm." + Q(m.CurrencyRateField) + ",2) FROM dbo." + master
                + " mm INNER JOIN (SELECT d." + Q(m.TypeField) + ", d." + Q(m.NoField) + ", SUM(d." + Q(d.AmountField)
                + "*d." + Q(d.CurrencyRateField) + ") AMOUNT_SUM, SUM(d." + Q(d.AmountTaxField) + "*d."
                + Q(d.CurrencyRateField) + ") AMOUNT_TAX_SUM, SUM(d." + Q(d.TaxSumField) + "*d." + Q(d.CurrencyRateField)
                + ") TAX_SUM_SUM FROM dbo." + detail + " d WHERE d." + Q(m.TypeField) + "=@type AND d." + Q(m.NoField)
                + "=@no GROUP BY d." + Q(m.TypeField) + ", d." + Q(m.NoField) + ") s ON mm." + Q(m.TypeField) + "=s."
                + Q(m.TypeField) + " AND mm." + Q(m.NoField) + "=s." + Q(m.NoField) + " WHERE mm." + Q(m.TypeField)
                + "=@type AND mm." + Q(m.NoField) + "=@no;", parameters, token);

            // ⑦ 数量分配
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
                "UPDATE dbo." + more + " SET " + Q(config.More.QtyField) + "=0 WHERE " + scope + ";", parameters, token);
            var detailQty = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            await using (var read = new SqlCommand("SELECT LTRIM(RTRIM(" + Q(d.ProductField) + ")), " + Q(d.QtyField)
                + " FROM dbo." + detail + " WHERE " + scope + ";", context.Connection, context.Transaction))
            {
                read.Parameters.AddWithValue("@type", type);
                read.Parameters.AddWithValue("@no", no);
                await using var reader = await read.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    detailQty[reader.GetString(0)] = Convert.ToDecimal(reader.GetValue(1));
            }
            var moreRows = new List<(int Serial, string Product, decimal RequireQty)>();
            await using (var read = new SqlCommand("SELECT " + Q(d.SerialField) + ", LTRIM(RTRIM(" + Q(d.ProductField)
                + ")), " + Q(config.More.RequireQtyField) + " FROM dbo." + more + " WHERE " + scope + " ORDER BY "
                + Q(d.ProductField) + ", " + Q(d.SerialField) + ";", context.Connection, context.Transaction))
            {
                read.Parameters.AddWithValue("@type", type);
                read.Parameters.AddWithValue("@no", no);
                await using var reader = await read.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    moreRows.Add((Convert.ToInt32(reader.GetValue(0)), reader.GetString(1), Convert.ToDecimal(reader.GetValue(2))));
            }
            string? currentProduct = null;
            var remaining = 0m;
            foreach (var row in moreRows)
            {
                if (!string.Equals(currentProduct, row.Product, StringComparison.OrdinalIgnoreCase))
                {
                    currentProduct = row.Product;
                    remaining = detailQty.TryGetValue(row.Product, out var qtyValue) ? qtyValue : 0m;
                }
                var assign = remaining > row.RequireQty ? row.RequireQty : remaining > 0 ? remaining : (decimal?)null;
                if (assign is null) continue;
                affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
                    "UPDATE dbo." + more + " SET " + Q(config.More.QtyField) + "=@qty WHERE " + scope + " AND "
                    + Q(d.SerialField) + "=@serial;",
                    [
                        new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
                        new EffectSqlParameter("@serial", row.Serial),
                        new EffectSqlParameter("@qty", assign.Value),
                    ], token);
                remaining -= row.RequireQty;
            }
        }

        // ⑧ 主表单号串联（全空不回写）
        foreach (var (target, source) in new[]
                 {
                     (m.OrderNoField, config.More.OrderNoField),
                     (m.ProduceNoField, config.More.ProduceNoField),
                 })
        {
            affected += await UpdateDistinctAsync(context, master, m.TypeField, m.NoField, target, more, source,
                type, no, token);
        }
        return affected;
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    private static async Task<object?> ScalarAsync(
        ServiceEffectContext context, string sql, string type, string no, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@no", no);
        return await command.ExecuteScalarAsync(token);
    }

    private static async Task<int> UpdateDistinctAsync(ServiceEffectContext context, string masterTable,
        string typeField, string noField, string target, string moreTable, string source, string type, string no,
        CancellationToken token)
    {
        var values = new List<string>();
        await using (var command = new SqlCommand("SELECT DISTINCT LTRIM(RTRIM(" + Q(source) + ")) FROM dbo."
            + moreTable + " WHERE " + Q(typeField) + "=@type AND " + Q(noField) + "=@no AND ISNULL("
            + Q(source) + ",'')<>'' ORDER BY 1;", context.Connection, context.Transaction))
        {
            command.Parameters.AddWithValue("@type", type);
            command.Parameters.AddWithValue("@no", no);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) values.Add(reader.GetString(0));
        }
        if (values.Count == 0) return 0;
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
            "UPDATE dbo." + masterTable + " SET " + Q(target) + "=@value WHERE " + Q(typeField) + "=@type AND "
            + Q(noField) + "=@no;",
            [
                new EffectSqlParameter("@value", string.Join(',', values)),
                new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
            ], token);
    }

    /// <summary>参数解析（fail-closed：五张表的每个列名都必须物理存在）。</summary>
    internal static PurPurchaseSyncConfig Parse(JsonElement root, string masterTable, ISet<string> columns)
    {
        const string label = "pur-purchase-sync";
        var master = Section(root, "master", label);
        var detail = Section(root, "detail", label);
        var more = Section(root, "more", label);
        var product = Section(root, "product", label);
        var supplierPrice = Section(root, "supplierPrice", label);
        var config = new PurPurchaseSyncConfig(
            new PurPurchaseSyncMaster(Required(master, "typeField"), Required(master, "noField"),
                Required(master, "currencyField"), Required(master, "currencyRateField"),
                Required(master, "taxTypeField"), Required(master, "taxRateField"), Required(master, "taxIdField"),
                Required(master, "supplierField"), Required(master, "amountField"),
                Required(master, "amountTaxField"), Required(master, "taxSumField"),
                Required(master, "orderNoField"), Required(master, "produceNoField")),
            new PurPurchaseSyncDetail(Required(detail, "table"), Required(detail, "productField"),
                Required(detail, "serialField"), Required(detail, "qtyField"), Required(detail, "receiveQtyField"),
                Required(detail, "unitField"), Required(detail, "currencyField"), Required(detail, "currencyRateField"),
                Required(detail, "taxTypeField"), Required(detail, "taxRateField"), Required(detail, "taxIdField"),
                Required(detail, "priceField"), Required(detail, "rebateField"), Required(detail, "requireQtyField"),
                Required(detail, "amountField"), Required(detail, "amountTaxField"), Required(detail, "taxSumField")),
            new PurPurchaseSyncMore(Required(more, "table"), Required(more, "qtyField"),
                Required(more, "requireQtyField"), Required(more, "orderNoField"),
                Required(more, "produceNoField")),
            new PurPurchaseSyncProduct(Required(product, "table"), Required(product, "productField"),
                Required(product, "depotField"), Required(product, "unitField")),
            new PurPurchaseSyncSupplierPrice(Required(supplierPrice, "table"),
                Required(supplierPrice, "supplierField"), Required(supplierPrice, "productField"),
                Required(supplierPrice, "unitField"), Required(supplierPrice, "currencyField"),
                Required(supplierPrice, "taxIdField"), Required(supplierPrice, "taxTypeField"),
                Required(supplierPrice, "priceField"), Required(supplierPrice, "taxRateField"),
                Required(supplierPrice, "currencyRateField"), Required(supplierPrice, "rebateField")));
        foreach (var (table, column) in new[]
                 {
                     (masterTable, config.Master.TypeField), (masterTable, config.Master.NoField),
                     (masterTable, config.Master.CurrencyField), (masterTable, config.Master.CurrencyRateField),
                     (masterTable, config.Master.TaxTypeField), (masterTable, config.Master.TaxRateField),
                     (masterTable, config.Master.TaxIdField), (masterTable, config.Master.SupplierField),
                     (masterTable, config.Master.AmountField), (masterTable, config.Master.AmountTaxField),
                     (masterTable, config.Master.TaxSumField), (masterTable, config.Master.OrderNoField),
                     (masterTable, config.Master.ProduceNoField),
                     (config.Detail.Table, config.Master.TypeField), (config.Detail.Table, config.Master.NoField),
                     (config.Detail.Table, config.Detail.ProductField), (config.Detail.Table, config.Detail.SerialField),
                     (config.Detail.Table, config.Detail.QtyField), (config.Detail.Table, config.Detail.ReceiveQtyField),
                     (config.Detail.Table, config.Detail.UnitField), (config.Detail.Table, config.Detail.CurrencyField),
                     (config.Detail.Table, config.Detail.CurrencyRateField),
                     (config.Detail.Table, config.Detail.TaxTypeField), (config.Detail.Table, config.Detail.TaxRateField),
                     (config.Detail.Table, config.Detail.TaxIdField), (config.Detail.Table, config.Detail.PriceField),
                     (config.Detail.Table, config.Detail.RebateField),
                     (config.Detail.Table, config.Detail.RequireQtyField),
                     (config.Detail.Table, config.Detail.AmountField),
                     (config.Detail.Table, config.Detail.AmountTaxField),
                     (config.Detail.Table, config.Detail.TaxSumField),
                     (config.More.Table, config.Master.TypeField), (config.More.Table, config.Master.NoField),
                     (config.More.Table, config.Detail.ProductField), (config.More.Table, config.More.QtyField),
                     (config.More.Table, config.More.RequireQtyField), (config.More.Table, config.More.OrderNoField),
                     (config.More.Table, config.More.ProduceNoField),
                     (config.Product.Table, config.Product.ProductField),
                     (config.Product.Table, config.Product.DepotField),
                     (config.Product.Table, config.Product.UnitField),
                     (config.SupplierPrice.Table, config.SupplierPrice.SupplierField),
                     (config.SupplierPrice.Table, config.SupplierPrice.ProductField),
                     (config.SupplierPrice.Table, config.SupplierPrice.UnitField),
                     (config.SupplierPrice.Table, config.SupplierPrice.CurrencyField),
                     (config.SupplierPrice.Table, config.SupplierPrice.TaxIdField),
                     (config.SupplierPrice.Table, config.SupplierPrice.TaxTypeField),
                     (config.SupplierPrice.Table, config.SupplierPrice.PriceField),
                     (config.SupplierPrice.Table, config.SupplierPrice.TaxRateField),
                     (config.SupplierPrice.Table, config.SupplierPrice.CurrencyRateField),
                     (config.SupplierPrice.Table, config.SupplierPrice.RebateField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"{label} 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static JsonElement Section(JsonElement root, string name, string label)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new EffectConfigException($"{label} 缺少对象字段 {name}。");

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"pur-purchase-sync 缺少字符串字段 {name}。");
}

internal sealed record PurPurchaseSyncMaster(string TypeField, string NoField, string CurrencyField,
    string CurrencyRateField, string TaxTypeField, string TaxRateField, string TaxIdField, string SupplierField,
    string AmountField, string AmountTaxField, string TaxSumField, string OrderNoField, string ProduceNoField);
internal sealed record PurPurchaseSyncDetail(string Table, string ProductField, string SerialField, string QtyField,
    string ReceiveQtyField, string UnitField, string CurrencyField, string CurrencyRateField, string TaxTypeField,
    string TaxRateField, string TaxIdField, string PriceField, string RebateField, string RequireQtyField,
    string AmountField, string AmountTaxField, string TaxSumField);
internal sealed record PurPurchaseSyncMore(string Table, string QtyField, string RequireQtyField, string OrderNoField,
    string ProduceNoField);
internal sealed record PurPurchaseSyncProduct(string Table, string ProductField, string DepotField, string UnitField);
internal sealed record PurPurchaseSyncSupplierPrice(string Table, string SupplierField, string ProductField,
    string UnitField, string CurrencyField, string TaxIdField, string TaxTypeField, string PriceField,
    string TaxRateField, string CurrencyRateField, string RebateField);
internal sealed record PurPurchaseSyncConfig(PurPurchaseSyncMaster Master, PurPurchaseSyncDetail Detail,
    PurPurchaseSyncMore More, PurPurchaseSyncProduct Product, PurPurchaseSyncSupplierPrice SupplierPrice);
