using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Inventory domain rules executed after module saves (stock counts, loans and returns). Methods run inside the caller's transaction.
/// 库别与产品编号的存在性、批管品批号必填均已由校验目录（reference-exists / line-require）承担；
/// 这里只保留目录表达不了的判据（盘点数为负、借出/返还的库存腿）。
/// </summary>
public static class InvDomainRules
{
    public static async Task<SprocResult> InvCheckStockAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        var negative = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.INV_CHECK_STOCK_D d
            WHERE CHECK_STOCK_TYPE=@Type AND CHECK_STOCK_NO=@No AND CHECK_QTY<0;
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (negative is not null) return new(false, "以下序号项盘点数小于0 \r\n" + negative);
        return new(true, null);
    }

    /// <summary>借出单（P_INV_LOAN）AfterSave：批号条件必填。</summary>
    public static Task<SprocResult> InvLoanAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "INV_LOAN_D", "LOAN_TYPE", "LOAN_NO",
            [
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);

    /// <summary>返还单（P_INV_RETURN）AfterSave：批号条件必填。</summary>
    public static Task<SprocResult> InvReturnAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "INV_RETURN_D", "RETURN_TYPE", "RETURN_NO",
            [
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);
}
