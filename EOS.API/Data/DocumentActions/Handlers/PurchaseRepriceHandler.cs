using System.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions.Handlers;

// `purchase-reprice`（重新取价）：用户在采购单上点一下，按厂商计价重算本单明细的单价与金额。
//
// 与旧系统同一件事（`PUR/Purchase.aspx.cs` 的 `btnCalc` → `P_PUR_PURCHASE_GETPRICE`），三处有意不同：
//   ① 已注释掉的"依材质取价"分支不复活——旧实现注释掉它是有原因的（按 STUFF_ID 配对会把
//      不同料号的计价串到一起），只保留"依品号"分支；
//   ② 主表本位币汇率为 0 或空时拒绝而不是除零——旧实现直接 `AMOUNT/CURR_RATE`，汇率坏掉时
//      整单金额会被算成 NULL 或报错，fail-closed 拦在前面；
//   ③ 逐行回写而不是整表 UPDATE：没有厂商计价的行保留原价（只重算金额），有计价的行才改价。
//      整表回写会把"无计价行的手工价"一并推平，这正是重算类操作最容易被做坏的地方。
//
// 与保存期 `pur-purchase-sync` 的关系：保存期动作在每次保存时按明细已有单价重算金额；
// 本操作做的是它前面的那一步——把单价先从厂商计价取回来。两者金额公式同一套，算出来一致。
// 不要求已批核：取价的正常时机恰恰是批核之前（草稿先取价再送审）。
// 已完工结案（FINISHED_TAG=1）的单子拒绝——结案后的价格是结算凭据，不能再动。
internal sealed class PurchaseRepriceHandler : IDocumentUserAction, IDocumentActionPlacement
{
    public const string ActionKey = "purchase-reprice";

    private const string MasterTable = "PUR_PURCHASE_M";
    private const string DetailTable = "PUR_PURCHASE_D";
    private const string PriceTable = "SUPPLIER_PRICE_D";
    private const string TypeField = "PURCHASE_TYPE";
    private const string NoField = "PURCHASE_NO";
    private const string SupplierField = "SUPPLIER_ID";
    private const string MasterRateField = "CURR_RATE";
    private const string FinishedField = "FINISHED_TAG";
    private const string ProField = "PRO_NO";
    private const string UnitField = "UNIT_ID";
    private const string CurrField = "CURR_ID";
    private const string TaxIdField = "TAX_ID";
    private const string TaxTypeField = "TAX_TYPE";
    private const string TaxRateField = "TAX_RATE";
    private const string PriceField = "PRICE";
    private const string RebateField = "REBATE";
    private const string SupplierProField = "SUPPLIER_PRO_NO";
    private const string QtyField = "QTY";
    private const string AmountField = "AMOUNT";
    private const string AmountTaxField = "AMOUNT_TAX";
    private const string TaxSumField = "TAX_SUM";

    public string Key => ActionKey;

    public string Label => "重新取价";

    /// <summary>改的是明细单价与金额，按钮落在子表标题栏。</summary>
    public string Placement => DocumentActionPlacements.Detail;

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var definition = context.Definition;
        if (definition.DetailTable is null)
        {
            throw new InvalidOperationException("该模块没有明细表，无法重新取价。");
        }

        var q = ServiceEffectSql.Q;
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        foreach (var (table, column) in new[]
                 {
                     (MasterTable, TypeField), (MasterTable, NoField),
                     (MasterTable, SupplierField), (MasterTable, MasterRateField),
                     (MasterTable, FinishedField),
                     (MasterTable, AmountField), (MasterTable, AmountTaxField), (MasterTable, TaxSumField),
                     (DetailTable, TypeField), (DetailTable, NoField),
                     (DetailTable, ProField), (DetailTable, UnitField), (DetailTable, CurrField),
                     (DetailTable, TaxIdField), (DetailTable, TaxTypeField), (DetailTable, TaxRateField),
                     (DetailTable, PriceField), (DetailTable, RebateField), (DetailTable, SupplierProField),
                     (DetailTable, QtyField),
                     (DetailTable, AmountField), (DetailTable, AmountTaxField), (DetailTable, TaxSumField),
                     (PriceTable, SupplierField), (PriceTable, ProField), (PriceTable, UnitField),
                     (PriceTable, CurrField), (PriceTable, TaxIdField),
                     (PriceTable, PriceField), (PriceTable, TaxTypeField), (PriceTable, TaxRateField),
                     (PriceTable, CurrRateField), (PriceTable, RebateField), (PriceTable, SupplierProField),
                 })
        {
            if (!columns.Contains(table + "." + column))
            {
                throw new InvalidOperationException($"重新取价需要 {table}.{column}，该模块或计价表没有这一列，请联系管理员调整。");
            }
        }

        var type = context.KeyValues.Count > 0 ? context.KeyValues[0].Trim() : string.Empty;
        var no = context.KeyValues.Count > 1 ? context.KeyValues[1].Trim() : string.Empty;
        if (type.Length == 0 || no.Length == 0)
        {
            throw new InvalidOperationException("缺少单据主键，禁止无条件取价。");
        }

        var (supplier, masterRate, finished) = await ReadMasterAsync(context, type, no, token);
        if (finished)
        {
            throw new InvalidOperationException("该采购单已完工结案，单价是结算凭据，不能重新取价。");
        }
        if (supplier.Length == 0)
        {
            throw new InvalidOperationException("采购单未填厂商，无法按厂商计价取价。");
        }
        if (masterRate is null || Math.Abs(masterRate.Value) < 0.0000001)
        {
            throw new InvalidOperationException("主表本位币汇率缺失或为零，此时重算主表金额会除零，已拒绝。");
        }

        var rows = await ReadDetailRowsAsync(context, type, no, token);
        if (rows.Count == 0)
        {
            return new DocumentActionResult(DocumentActionOutcome.Message, "该采购单还没有明细，无需取价。");
        }

        // 探路与执行走同一事务：逐行取价 + 逐行重算金额都在调用方事务里，
        // 框架"执行后回滚"即可兜住探路，处理器不必自行分支。
        var priced = 0;
        foreach (var row in rows)
        {
            var current = await FetchPriceAsync(context, supplier, row, token);
            if (!ReferenceEquals(current, row))
            {
                priced++;
            }
            await RecomputeRowAsync(context, type, no, current, token);
        }
        await RollupMasterAsync(context, type, no, masterRate.Value, token);

        var unpriced = rows.Count - priced;
        return new DocumentActionResult(DocumentActionOutcome.Refreshed,
            $"已重新取价：明细 {rows.Count} 行，其中 {priced} 行按厂商计价更新单价"
            + (unpriced > 0 ? $"，{unpriced} 行无厂商计价、保留原价" : string.Empty)
            + "；明细与主表金额已按税种重算。");
    }

    private const string CurrRateField = "CURR_RATE";

    private static async Task<(string Supplier, double? MasterRate, bool Finished)> ReadMasterAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT ISNULL({q(SupplierField)}, N''), {q(MasterRateField)}, ISNULL({q(FinishedField)}, 0) "
            + $"FROM dbo.{q(MasterTable)} WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            throw new InvalidOperationException($"找不到采购单 {type}/{no}。");
        }
        return (reader.GetString(0).Trim(),
            reader.IsDBNull(1) ? null : Convert.ToDouble(reader.GetValue(1)),
            !reader.IsDBNull(2) && Convert.ToBoolean(reader.GetValue(2)));
    }

    private sealed record DetailRow(
        string ProNo, string UnitId, string CurrId, string TaxId,
        string TaxType, double TaxRate, double Price, double Rebate, double Qty);

    private static async Task<IReadOnlyList<DetailRow>> ReadDetailRowsAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT LTRIM(RTRIM({q(ProField)})), ISNULL({q(UnitField)}, N''), ISNULL({q(CurrField)}, N''), "
            + $"ISNULL({q(TaxIdField)}, N''), ISNULL({q(TaxTypeField)}, N''), ISNULL({q(TaxRateField)}, 0), "
            + $"ISNULL({q(PriceField)}, 0), ISNULL({q(RebateField)}, 100), ISNULL({q(QtyField)}, 0) "
            + $"FROM dbo.{q(DetailTable)} WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        var rows = new List<DetailRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new DetailRow(
                reader.GetString(0).Trim(), reader.GetString(1).Trim(), reader.GetString(2).Trim(),
                reader.GetString(3).Trim(), reader.GetString(4).Trim().ToUpperInvariant(),
                Convert.ToDouble(reader.GetValue(5)), Convert.ToDouble(reader.GetValue(6)),
                Convert.ToDouble(reader.GetValue(7)), Convert.ToDouble(reader.GetValue(8))));
        }
        return rows;
    }

    /// <summary>
    /// 按（厂商, 料号, 单位, 币别, 税种）取计价行：命中即逐行回写单价七件套并返回更新后的行；
    /// 无计价行返回原行（保留原价，只重算金额）。税率/折扣的空值按旧口径兜 0/100。
    /// </summary>
    private static async Task<DetailRow> FetchPriceAsync(
        DocumentActionContext context, string supplier, DetailRow row, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var query = new SqlCommand(
            $"SELECT TOP 1 {q(PriceField)}, {q(TaxTypeField)}, {q(TaxIdField)}, {q(TaxRateField)}, "
            + $"{q(CurrField)}, {q(CurrRateField)}, {q(RebateField)}, ISNULL({q(SupplierProField)}, N'') "
            + $"FROM dbo.{q(PriceTable)} "
            + $"WHERE LTRIM(RTRIM({q(SupplierField)}))=@supplier AND LTRIM(RTRIM({q(ProField)}))=@pro "
            + $"AND ISNULL({q(UnitField)}, N'')=@unit AND ISNULL({q(CurrField)}, N'')=@curr "
            + $"AND ISNULL({q(TaxIdField)}, N'')=@tax;",
            context.Connection, context.Transaction);
        query.Parameters.Add("@supplier", SqlDbType.NVarChar, 30).Value = supplier;
        query.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = row.ProNo;
        query.Parameters.Add("@unit", SqlDbType.NVarChar, 20).Value = row.UnitId;
        query.Parameters.Add("@curr", SqlDbType.NVarChar, 20).Value = row.CurrId;
        query.Parameters.Add("@tax", SqlDbType.NVarChar, 20).Value = row.TaxId;
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return row;
        }
        var price = Convert.ToDouble(reader.GetValue(0));
        var taxType = (reader.IsDBNull(1) ? string.Empty : reader.GetString(1)).Trim().ToUpperInvariant();
        var taxId = (reader.IsDBNull(2) ? string.Empty : reader.GetString(2)).Trim();
        var taxRate = reader.IsDBNull(3) ? 0d : Convert.ToDouble(reader.GetValue(3));
        var currId = (reader.IsDBNull(4) ? string.Empty : reader.GetString(4)).Trim();
        var currRate = reader.IsDBNull(5) ? 0d : Convert.ToDouble(reader.GetValue(5));
        var rebate = reader.IsDBNull(6) ? 100d : Convert.ToDouble(reader.GetValue(6));
        var supplierPro = reader.GetString(7).Trim();
        await reader.DisposeAsync();

        await using var update = new SqlCommand(
            $"UPDATE dbo.{q(DetailTable)} SET {q(PriceField)}=@price, {q(TaxTypeField)}=@taxType, "
            + $"{q(TaxIdField)}=@taxId, {q(TaxRateField)}=@taxRate, {q(CurrField)}=@currId, "
            + $"{q(CurrRateField)}=@currRate, {q(RebateField)}=@rebate, {q(SupplierProField)}=@supplierPro "
            + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no "
            + $"AND LTRIM(RTRIM({q(ProField)}))=@pro AND ISNULL({q(UnitField)}, N'')=@unit "
            + $"AND ISNULL({q(CurrField)}, N'')=@curr AND ISNULL({q(TaxIdField)}, N'')=@tax;",
            context.Connection, context.Transaction);
        update.Parameters.Add("@price", SqlDbType.Float).Value = price;
        update.Parameters.Add("@taxType", SqlDbType.NVarChar, 20).Value = taxType;
        update.Parameters.Add("@taxId", SqlDbType.NVarChar, 20).Value = taxId;
        update.Parameters.Add("@taxRate", SqlDbType.Float).Value = taxRate;
        update.Parameters.Add("@currId", SqlDbType.NVarChar, 20).Value = currId;
        update.Parameters.Add("@currRate", SqlDbType.Float).Value = currRate;
        update.Parameters.Add("@rebate", SqlDbType.Float).Value = rebate;
        update.Parameters.Add("@supplierPro", SqlDbType.NVarChar, 60).Value = supplierPro;
        update.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = context.KeyValues[0].Trim();
        update.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = context.KeyValues[1].Trim();
        update.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = row.ProNo;
        update.Parameters.Add("@unit", SqlDbType.NVarChar, 20).Value = row.UnitId;
        update.Parameters.Add("@curr", SqlDbType.NVarChar, 20).Value = row.CurrId;
        update.Parameters.Add("@tax", SqlDbType.NVarChar, 20).Value = row.TaxId;
        await update.ExecuteNonQueryAsync(token);
        return row with { TaxType = taxType, TaxRate = taxRate, Price = price, Rebate = rebate };
    }

    /// <summary>
    /// 逐行重算金额：与旧过程逐字一致的税种公式（I 内含税 / O 外含税 / N 不含税），
    /// 用传入行（取价后的最新值）计算，不二次读表。
    /// </summary>
    private static async Task RecomputeRowAsync(
        DocumentActionContext context, string type, string no, DetailRow row, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var line = row.Qty * row.Price * row.Rebate / 100;
        var amount = row.TaxType == "I" && row.TaxRate != 0
            ? Math.Round(line / (1 + row.TaxRate / 100), 2)
            : Math.Round(line, 2);
        var amountTax = row.TaxType == "O"
            ? Math.Round(line * (1 + row.TaxRate / 100), 2)
            : Math.Round(line, 2);
        var taxSum = row.TaxType == "N" ? 0d
            : row.TaxType == "O" ? Math.Round(line * row.TaxRate / 100, 2)
            : Math.Round(line * row.TaxRate / 100 / (1 + row.TaxRate / 100), 2);

        await using var command = new SqlCommand(
            $"UPDATE dbo.{q(DetailTable)} SET {q(AmountField)}=@amount, {q(AmountTaxField)}=@amountTax, "
            + $"{q(TaxSumField)}=@taxSum "
            + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no "
            + $"AND LTRIM(RTRIM({q(ProField)}))=@pro AND ISNULL({q(UnitField)}, N'')=@unit "
            + $"AND ISNULL({q(CurrField)}, N'')=@curr AND ISNULL({q(TaxIdField)}, N'')=@tax;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@amount", SqlDbType.Float).Value = amount;
        command.Parameters.Add("@amountTax", SqlDbType.Float).Value = amountTax;
        command.Parameters.Add("@taxSum", SqlDbType.Float).Value = taxSum;
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = row.ProNo;
        command.Parameters.Add("@unit", SqlDbType.NVarChar, 20).Value = row.UnitId;
        command.Parameters.Add("@curr", SqlDbType.NVarChar, 20).Value = row.CurrId;
        command.Parameters.Add("@tax", SqlDbType.NVarChar, 20).Value = row.TaxId;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>主表金额＝明细按汇率折算求和÷主表汇率（与旧过程一致；主表汇率为 0 已在入口拦下）。</summary>
    private static async Task RollupMasterAsync(
        DocumentActionContext context, string type, string no, double masterRate, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var rate = masterRate.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        await using var command = new SqlCommand(
            $"UPDATE dbo.{q(MasterTable)} SET {q(AmountField)}=ROUND(d.{q(AmountField)}/{rate}, 2), "
            + $"{q(AmountTaxField)}=ROUND(d.{q(AmountTaxField)}/{rate}, 2), "
            + $"{q(TaxSumField)}=ROUND(d.{q(TaxSumField)}/{rate}, 2), "
            + $"{q("LAST_UPDATE_DATE")}=SYSDATETIME() "
            + $"FROM (SELECT SUM(ISNULL({q(AmountField)}, 0)*ISNULL({q(CurrRateField)}, 0)) AS {q(AmountField)}, "
            + $"SUM(ISNULL({q(AmountTaxField)}, 0)*ISNULL({q(CurrRateField)}, 0)) AS {q(AmountTaxField)}, "
            + $"SUM(ISNULL({q(TaxSumField)}, 0)*ISNULL({q(CurrRateField)}, 0)) AS {q(TaxSumField)} "
            + $"FROM dbo.{q(DetailTable)} WHERE {q(TypeField)}=@type AND {q(NoField)}=@no) d "
            + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await command.ExecuteNonQueryAsync(token);
    }
}
