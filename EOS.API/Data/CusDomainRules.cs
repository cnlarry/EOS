using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Customs, quality and sample domain rules executed after module saves (export/import
/// declarations, manual and account documents, quality analysis, sample stock-out).
/// Methods run inside the caller's transaction.
/// </summary>
public static class CusDomainRules
{
    /// <summary>海关对帐单（P_CUS_ACCOUNT）AfterSave：CHECK 分支 + 客户/送退货存在 + 明细重量金额与主表汇总。</summary>


    /// <summary>海关对帐单（P_CUS_ACCOUNT）AfterSave：CHECK 分支 + 客户/送退货存在 + 明细重量金额与主表汇总。</summary>
    public static async Task<SprocResult> CusAccountAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "海关对帐单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (await DomainRuleService.HasErrorNoSaveAsync(connection, transaction, moduleId, token))
        {
            var send = await DomainRuleService.FindLinesAsync(connection, transaction,
                """
                SELECT od.SEND_NO, od.QTY, od.FINISHED_QTY, sd.QTY
                FROM dbo.COP_SEND_D od
                INNER JOIN (SELECT S_R_TYPE, S_R_NO, S_R_SERIAL_NO, SUM(QTY) QTY
                            FROM dbo.CUS_ACCOUNT_D WHERE ACCOUNT_TYPE=@Type AND ACCOUNT_NO=@No
                            GROUP BY S_R_TYPE, S_R_NO, S_R_SERIAL_NO) sd
                  ON od.SEND_TYPE=sd.S_R_TYPE AND od.SEND_NO=sd.S_R_NO AND od.SERIAL_NO=sd.S_R_SERIAL_NO
                WHERE ISNULL(od.FINISHED_QTY,0)+sd.QTY > ISNULL(od.QTY,0);
                """, type, no, token,
                line: r => $"{r.GetString(0).Trim()}    {Convert.ToString(r.GetValue(1))}    {Convert.ToString(r.GetValue(2))}    {Convert.ToString(r.GetValue(3))}");
            if (send is not null) return new(false, "以下对帐已超出送货单数量\r\n 送货单号  送货数量  已对帐数量  单据数量\r\n" + send);
            var ret = await DomainRuleService.FindLinesAsync(connection, transaction,
                """
                SELECT od.RETURN_NO, od.QTY, od.FINISHED_QTY, sd.QTY
                FROM dbo.COP_RETURN_D od
                INNER JOIN (SELECT S_R_TYPE, S_R_NO, S_R_SERIAL_NO, SUM(QTY) QTY
                            FROM dbo.CUS_ACCOUNT_D WHERE ACCOUNT_TYPE=@Type AND ACCOUNT_NO=@No
                            GROUP BY S_R_TYPE, S_R_NO, S_R_SERIAL_NO) sd
                  ON od.RETURN_TYPE=sd.S_R_TYPE AND od.RETURN_NO=sd.S_R_NO AND od.SERIAL_NO=sd.S_R_SERIAL_NO
                WHERE ISNULL(od.FINISHED_QTY,0)+sd.QTY > ISNULL(od.QTY,0);
                """, type, no, token,
                line: r => $"{r.GetString(0).Trim()}    {Convert.ToString(r.GetValue(1))}    {Convert.ToString(r.GetValue(2))}    {Convert.ToString(r.GetValue(3))}");
            if (ret is not null) return new(false, "以下对帐已超出退货单数量\r\n 退货单号  退货数量  已对帐数量  单据数量\r\n" + ret);
        }
        // 客户存在
        // 明细重量/金额 + 主表汇总
        await using (var detail = new SqlCommand("""
            UPDATE d SET d.SUTTLE=p.SUTTLE, d.CUS_QTY=d.QTY*ISNULL(p.SUTTLE,0),
                   d.GROSS_WEIGHT=p.GROSS_WEIGHT, d.CUS_GROSS_QTY=d.QTY*ISNULL(p.GROSS_WEIGHT,0)
            FROM dbo.CUS_ACCOUNT_D d INNER JOIN dbo.PRODUCT p ON p.PRO_NO=d.PRO_NO
            WHERE d.ACCOUNT_TYPE=@Type AND d.ACCOUNT_NO=@No;
            """, connection, transaction))
        {
            detail.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            detail.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            await detail.ExecuteNonQueryAsync(token);
        }
        await using (var accountQty = new SqlCommand(
            "UPDATE dbo.CUS_ACCOUNT_D SET ACCOUNT_QTY=CUS_QTY WHERE ACCOUNT_TYPE=@Type AND ACCOUNT_NO=@No AND ISNULL(ACCOUNT_QTY,0)=0;",
            connection, transaction))
        {
            accountQty.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            accountQty.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            await accountQty.ExecuteNonQueryAsync(token);
        }
        await using (var master = new SqlCommand("""
            UPDATE m SET m.AMOUNT=d.AMOUNT, m.TAX_SUM=d.TAX_SUM, m.AMOUNT_TAX=d.AMOUNT_TAX,
                   m.SUM_AMOUNT=d.AMOUNT_TAX+ISNULL(m.OTHER_PRICE,0), m.QTY_TOTAL=d.QTY_ALL,
                   m.CUS_QTY=d.CUS_QTY, m.CUS_GROSS_QTY=d.CUS_GROSS_QTY,
                   m.PROCESS_AMOUNT=d.CUS_QTY*ISNULL(m.PROCESS_PRICE,0)
            FROM dbo.CUS_ACCOUNT_M m
            INNER JOIN (SELECT ACCOUNT_TYPE, ACCOUNT_NO, ROUND(SUM(AMOUNT_TAX),2) AMOUNT_TAX, ROUND(SUM(AMOUNT),2) AMOUNT,
                               ROUND(SUM(TAX_SUM),2) TAX_SUM, ROUND(SUM(QTY),2) QTY_ALL,
                               ROUND(SUM(ACCOUNT_QTY),2) CUS_QTY, ROUND(SUM(CUS_GROSS_QTY),2) CUS_GROSS_QTY
                        FROM dbo.CUS_ACCOUNT_D WHERE ACCOUNT_TYPE=@Type AND ACCOUNT_NO=@No
                        GROUP BY ACCOUNT_TYPE, ACCOUNT_NO) d
              ON d.ACCOUNT_TYPE=m.ACCOUNT_TYPE AND d.ACCOUNT_NO=m.ACCOUNT_NO
            WHERE m.ACCOUNT_TYPE=@Type AND m.ACCOUNT_NO=@No;
            """, connection, transaction))
        {
            master.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            master.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            await master.ExecuteNonQueryAsync(token);
        }
        return new(true, null);
    }

    /// <summary>品质日分析单（P_QC_ANALYSIS）AfterSave：CHECK 分支 + 制令/产品存在。</summary>
}

