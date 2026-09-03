using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Inventory domain rules executed after module saves (occurrence in/out/transfer/scrap/adjust/init, stock checks, loans and returns). Methods run inside the caller's transaction.
/// </summary>
public static class InvDomainRules
{
    public static async Task<SprocResult> InvCheckStockAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        var depotMissing = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.INV_CHECK_STOCK_D d
            WHERE CHECK_STOCK_TYPE=@Type AND CHECK_STOCK_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=d.DEPOT_ID);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (depotMissing is not null) return new(false, "以下序号项库别编号不存在 \r\n" + depotMissing);
        var productMissing = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.INV_CHECK_STOCK_D d
            WHERE CHECK_STOCK_TYPE=@Type AND CHECK_STOCK_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (productMissing is not null) return new(false, "以下序号项产品编号不存在 \r\n" + productMissing);
        var negative = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.INV_CHECK_STOCK_D d
            WHERE CHECK_STOCK_TYPE=@Type AND CHECK_STOCK_NO=@No AND CHECK_QTY<0;
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (negative is not null) return new(false, "以下序号项盘点数小于0 \r\n" + negative);
        return new(true, null);
    }

    public static async Task<SprocResult> InvOccurValidateAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues,
        string detailTable, bool checkInDepot, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "库存单据领域规则缺少主键。");
        if (detailTable.Length == 0 || detailTable.Length > 64
            || !detailTable.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return new(false, "库存单据明细表名非法。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        async Task<string?> MissingAsync(string whereClause)
        {
            await using var cmd = new SqlCommand($"""
                SELECT TOP 11 SERIAL_NO FROM dbo.[{detailTable}] t
                WHERE OCCUR_TYPE=@Type AND OCCUR_NO=@No AND {whereClause} ORDER BY SERIAL_NO;
                """, connection, transaction);
            cmd.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            cmd.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            await using var reader = await cmd.ExecuteReaderAsync(token);
            var lines = new List<string>();
            while (await reader.ReadAsync(token)) lines.Add(Convert.ToInt32(reader.GetValue(0)).ToString());
            return lines.Count > 0 ? string.Join("\r\n", lines.Take(10)) : null;
        }
        if (checkInDepot)
        {
            var inDepot = await MissingAsync("NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=t.IN_DEPOT_ID)");
            if (inDepot is not null) return new(false, "以下序号项入库别编号不存在 \r\n" + inDepot);
            var outDepot = await MissingAsync("NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=t.DEPOT_ID)");
            if (outDepot is not null) return new(false, "以下序号项出库别编号不存在 \r\n" + outDepot);
        }
        else
        {
            var depot = await MissingAsync("NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=t.DEPOT_ID)");
            if (depot is not null) return new(false, "以下序号项库别编号不存在 \r\n" + depot);
        }
        var product = await MissingAsync("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)");
        if (product is not null) return new(false, "以下序号项产品编号不存在 \r\n" + product);
        var batch = await MissingAsync(
            "ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)");
        if (batch is not null) return new(false, "以下序号项需要输入批号 \r\n" + batch);
        return new(true, null);
    }



    public static Task<SprocResult> InvOccurInAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_IN_D", false, token);



    public static Task<SprocResult> InvOccurOutAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_OUT_D", false, token);



    public static Task<SprocResult> InvOccurTransferAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_TRANSFER_D", true, token);



    public static Task<SprocResult> InvOccurScrapAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_SCRAP_D", true, token);



    public static Task<SprocResult> InvOccurAdjustAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_ADJUST_D", false, token);



    public static Task<SprocResult> InvOccurInitAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_INIT_D", false, token);

    /// <summary>
    /// 生产链单据 AfterSave 通用明细校验：按（类型/单号）限定明细表，逐条检查，返回首个失败。
    /// 的游标+RAISERROR 模式（最多列 10 个序号）。
    /// </summary>

    public static Task<SprocResult> InvLoanAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "INV_LOAN_D", "LOAN_TYPE", "LOAN_NO",
            [
                ("NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=t.DEPOT_ID)", "以下序号项库别编号不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);

    /// <summary>返还单（P_INV_RETURN）AfterSave：库别/产品/批号校验。</summary>


    /// <summary>返还单（P_INV_RETURN）AfterSave：库别/产品/批号校验。</summary>
    public static Task<SprocResult> InvReturnAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "INV_RETURN_D", "RETURN_TYPE", "RETURN_NO",
            [
                ("NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=t.DEPOT_ID)", "以下序号项库别编号不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);
}
