using System.Data;
using Microsoft.Data.SqlClient;

using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// Procurement domain rules executed after module saves (quotes, purchase orders, receipts, applies, payments, prepayments, cancellations and due documents). Methods run inside the caller's transaction.
/// </summary>
public static class PurDomainRules
{


    /// <summary>员工基本资料（P_HR_EMPLOYEE）AfterSave：工号唯一（他人占用且 STATE<>5 即拒绝）。</summary>


    /// <summary>制造命令单变更（1509）AfterSave：原单已批核 + 变更量不小于已生产/已领料。</summary>


    /// <summary>采购单变更（1609）AfterSave：原单已批核与变更量下限由校验目录承担。</summary>

    /// <summary>打样出库单（2404）AfterSave：样品库存校验。</summary>

    public static async Task<SprocResult> PurPurchaseAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        // 产品计价有效期
        var priceExpired = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT DISTINCT a.PRO_NO FROM dbo.PUR_PURCHASE_D a
            INNER JOIN dbo.PUR_PURCHASE_M m ON m.PURCHASE_TYPE=a.PURCHASE_TYPE AND m.PURCHASE_NO=a.PURCHASE_NO
            INNER JOIN dbo.SUPPLIER_PRICE_D p
              ON p.SUPPLIER_ID=m.SUPPLIER_ID AND p.PRO_NO=a.PRO_NO AND p.CURR_ID=a.CURR_ID
             AND p.TAX_ID=a.TAX_ID AND p.TAX_TYPE=a.TAX_TYPE
            WHERE a.PURCHASE_TYPE=@Type AND a.PURCHASE_NO=@No AND p.IN_EFFECT_DATE < m.PURCHASE_DATE;
            """, type, no, token, line: r => r.GetString(0).Trim());
        if (priceExpired is not null)
            return new(false, "以下产品计价已过有效期\r\n" + priceExpired);
        // 预交日期 >= 采购日期
        var deliveryLines = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO FROM dbo.PUR_PURCHASE_D d
            INNER JOIN dbo.PUR_PURCHASE_M m ON m.PURCHASE_TYPE=d.PURCHASE_TYPE AND m.PURCHASE_NO=d.PURCHASE_NO
            WHERE d.PURCHASE_TYPE=@Type AND d.PURCHASE_NO=@No AND d.PLAN_DELIVERY_DATE < m.PURCHASE_DATE;
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (deliveryLines is not null)
            return new(false, "以下序号项预交日期小于采购单日期 \r\n" + deliveryLines);

        var hasMore = await DomainRuleService.ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.PUR_PURCHASE_MORE WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;", type, no, token);
        if (hasMore)
        {
            // a. 补明细行
            var maxSerial = await DomainRuleService.ScalarIntAsync(connection, transaction,
                "SELECT ISNULL(MAX(SERIAL_NO),0) FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;",
                type, no, token);
            var missingProducts = await DomainRuleService.ReadStringsAsync(connection, transaction, """
                SELECT DISTINCT LTRIM(RTRIM(m.PRO_NO)) FROM dbo.PUR_PURCHASE_MORE m
                WHERE m.PURCHASE_TYPE=@Type AND m.PURCHASE_NO=@No
                  AND NOT EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_D d
                                  WHERE d.PURCHASE_TYPE=m.PURCHASE_TYPE AND d.PURCHASE_NO=m.PURCHASE_NO AND d.PRO_NO=m.PRO_NO);
                """, type, no, token);
            var moreQty = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            await using (var readMore = new SqlCommand("""
                SELECT LTRIM(RTRIM(PRO_NO)), SUM(REQUIRE_QTY) FROM dbo.PUR_PURCHASE_MORE
                WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No GROUP BY PRO_NO;
                """, connection, transaction))
            {
                readMore.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                readMore.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                await using var reader = await readMore.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    moreQty[reader.GetString(0)] = Convert.ToDecimal(reader.GetValue(1));
            }
            foreach (var proNo in missingProducts)
            {
                maxSerial++;
                await using var insert = new SqlCommand("""
                    INSERT INTO dbo.PUR_PURCHASE_D (PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY, RECEIVE_QTY, UNIT_ID)
                    SELECT @Type, @No, @Serial, p.PRO_NO, p.DEPOT_ID, @Qty, 0, p.UNIT_ID
                    FROM dbo.PRODUCT p WHERE p.PRO_NO=@ProNo;
                    """, connection, transaction);
                insert.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                insert.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                insert.Parameters.Add("@Serial", SqlDbType.Int).Value = maxSerial;
                insert.Parameters.Add("@Qty", SqlDbType.Decimal).Value = moreQty.GetValueOrDefault(proNo);
                insert.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
                await insert.ExecuteNonQueryAsync(token);
            }
            // b. 主表币别/税率带到明细
            await using (var syncTax = new SqlCommand("""
                UPDATE d SET d.CURR_ID=m.CURR_ID, d.CURR_RATE=m.CURR_RATE, d.TAX_TYPE=m.TAX_TYPE,
                    d.TAX_RATE=m.TAX_RATE, d.TAX_ID=m.TAX_ID
                FROM dbo.PUR_PURCHASE_D d INNER JOIN dbo.PUR_PURCHASE_M m
                  ON m.PURCHASE_TYPE=d.PURCHASE_TYPE AND m.PURCHASE_NO=d.PURCHASE_NO
                WHERE d.PURCHASE_TYPE=@Type AND d.PURCHASE_NO=@No;
                """, connection, transaction))
            {
                syncTax.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                syncTax.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                await syncTax.ExecuteNonQueryAsync(token);
            }
            // c. 厂商计价回填单价
            await using (var syncPrice = new SqlCommand("""
                UPDATE d SET d.PRICE=p.PRICE, d.TAX_RATE=p.TAX_RATE, d.CURR_RATE=p.CURR_RATE, d.REBATE=p.REBATE
                FROM dbo.PUR_PURCHASE_D d
                INNER JOIN dbo.PUR_PURCHASE_M m ON m.PURCHASE_TYPE=d.PURCHASE_TYPE AND m.PURCHASE_NO=d.PURCHASE_NO
                INNER JOIN dbo.SUPPLIER_PRICE_D p
                  ON p.SUPPLIER_ID=m.SUPPLIER_ID AND p.PRO_NO=d.PRO_NO AND p.UNIT_ID=d.UNIT_ID
                 AND p.CURR_ID=d.CURR_ID AND p.TAX_ID=d.TAX_ID AND p.TAX_TYPE=d.TAX_TYPE
                WHERE d.PURCHASE_TYPE=@Type AND d.PURCHASE_NO=@No;
                """, connection, transaction))
            {
                syncPrice.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                syncPrice.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                await syncPrice.ExecuteNonQueryAsync(token);
            }
            // d. REQUIRE_QTY 清零并回填
            await using (var clearReq = new SqlCommand(
                "UPDATE dbo.PUR_PURCHASE_D SET REQUIRE_QTY=0 WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;",
                connection, transaction))
            {
                clearReq.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                clearReq.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                await clearReq.ExecuteNonQueryAsync(token);
            }
            await using (var syncReq = new SqlCommand("""
                UPDATE d SET d.REQUIRE_QTY=s.REQUIRE_QTY
                FROM dbo.PUR_PURCHASE_D d
                INNER JOIN (SELECT PURCHASE_TYPE, PURCHASE_NO, PRO_NO, SUM(REQUIRE_QTY) REQUIRE_QTY
                            FROM dbo.PUR_PURCHASE_MORE WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No
                            GROUP BY PURCHASE_TYPE, PURCHASE_NO, PRO_NO) s
                  ON d.PURCHASE_TYPE=s.PURCHASE_TYPE AND d.PURCHASE_NO=s.PURCHASE_NO AND d.PRO_NO=s.PRO_NO
                WHERE d.PURCHASE_TYPE=@Type AND d.PURCHASE_NO=@No;
                """, connection, transaction))
            {
                syncReq.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                syncReq.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                await syncReq.ExecuteNonQueryAsync(token);
            }
            // e. Recalculate detail amounts (I/O/N tax formulas, same rules as AmountCalculator)
            await using (var calcAmount = new SqlCommand("""
                UPDATE dbo.PUR_PURCHASE_D SET
                    AMOUNT=CASE TAX_TYPE WHEN 'I' THEN ROUND((QTY*PRICE*ISNULL(REBATE,100)/100)/(1+ISNULL(TAX_RATE,0)/100),2)
                                         ELSE ROUND(QTY*PRICE*ISNULL(REBATE,100)/100,2) END,
                    AMOUNT_TAX=CASE TAX_TYPE WHEN 'O' THEN ROUND(QTY*PRICE*ISNULL(REBATE,100)/100*(1+ISNULL(TAX_RATE,0)/100),2)
                                             ELSE ROUND(QTY*PRICE*ISNULL(REBATE,100)/100,2) END,
                    TAX_SUM=CASE TAX_TYPE WHEN 'N' THEN 0
                            WHEN 'O' THEN ROUND(QTY*PRICE*ISNULL(REBATE,100)/100*ISNULL(TAX_RATE,0)/100,2)
                            WHEN 'I' THEN ROUND(QTY*PRICE*ISNULL(REBATE,100)/100*ISNULL(TAX_RATE,0)/100/(1+ISNULL(TAX_RATE,0)/100),2)
                            ELSE 0 END
                WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;
                """, connection, transaction))
            {
                calcAmount.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                calcAmount.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                await calcAmount.ExecuteNonQueryAsync(token);
            }
            // f. 主表金额汇总（明细×汇率 / 主表汇率，ROUND 2）
            await using (var calcMaster = new SqlCommand("""
                UPDATE m SET m.AMOUNT=ROUND(d.AMOUNT/m.CURR_RATE,2), m.AMOUNT_TAX=ROUND(d.AMOUNT_TAX/m.CURR_RATE,2),
                    m.TAX_SUM=ROUND(d.TAX_SUM/m.CURR_RATE,2)
                FROM dbo.PUR_PURCHASE_M m
                INNER JOIN (SELECT PURCHASE_TYPE, PURCHASE_NO,
                                   SUM(AMOUNT*CURR_RATE) AMOUNT, SUM(AMOUNT_TAX*CURR_RATE) AMOUNT_TAX,
                                   SUM(TAX_SUM*CURR_RATE) TAX_SUM
                            FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No
                            GROUP BY PURCHASE_TYPE, PURCHASE_NO) d
                  ON m.PURCHASE_TYPE=d.PURCHASE_TYPE AND m.PURCHASE_NO=d.PURCHASE_NO
                WHERE m.PURCHASE_TYPE=@Type AND m.PURCHASE_NO=@No;
                """, connection, transaction))
            {
                calcMaster.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                calcMaster.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                await calcMaster.ExecuteNonQueryAsync(token);
            }
            // g. 数量分配
            var detailQty = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            await using (var readQty = new SqlCommand("""
                SELECT LTRIM(RTRIM(PRO_NO)), QTY FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;
                """, connection, transaction))
            {
                readQty.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                readQty.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                await using var reader = await readQty.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    detailQty[reader.GetString(0)] = Convert.ToDecimal(reader.GetValue(1));
            }
            await using (var clearMore = new SqlCommand(
                "UPDATE dbo.PUR_PURCHASE_MORE SET QTY=0 WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;",
                connection, transaction))
            {
                clearMore.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                clearMore.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                await clearMore.ExecuteNonQueryAsync(token);
            }
            string? currentPro = null;
            var remaining = 0m;
            await using (var readMore = new SqlCommand("""
                SELECT SERIAL_NO, LTRIM(RTRIM(PRO_NO)), REQUIRE_QTY FROM dbo.PUR_PURCHASE_MORE
                WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No ORDER BY PRO_NO, SERIAL_NO;
                """, connection, transaction))
            {
                readMore.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                readMore.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                await using var reader = await readMore.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var serial = reader.GetInt32(0);
                    var proNo = reader.GetString(1);
                    var requireQty = Convert.ToDecimal(reader.GetValue(2));
                    if (!string.Equals(currentPro, proNo, StringComparison.OrdinalIgnoreCase))
                    {
                        currentPro = proNo;
                        remaining = detailQty.GetValueOrDefault(proNo);
                    }
                    if (remaining > requireQty)
                    {
                        await UpdatePurchaseMoreQtyAsync(connection, transaction, type, no, serial, requireQty, token);
                        remaining -= requireQty;
                    }
                    else if (remaining > 0)
                    {
                        await UpdatePurchaseMoreQtyAsync(connection, transaction, type, no, serial, remaining, token);
                        remaining -= requireQty;
                    }
                }
            }
        }
        // 主表 ORDER_NO / PRODUCE_NO 汇总（MORE 有值才更新）
        await DomainRuleService.UpdateDistinctFieldAsync(connection, transaction, "PUR_PURCHASE_M", "PURCHASE_TYPE", "PURCHASE_NO",
            type, no, "ORDER_NO", "PUR_PURCHASE_MORE", "ORDER_NO",
            moreTypeColumn: "PURCHASE_TYPE", moreNoColumn: "PURCHASE_NO", token);
        await DomainRuleService.UpdateDistinctFieldAsync(connection, transaction, "PUR_PURCHASE_M", "PURCHASE_TYPE", "PURCHASE_NO",
            type, no, "PRODUCE_NO", "PUR_PURCHASE_MORE", "PRODUCE_NO",
            moreTypeColumn: "PURCHASE_TYPE", moreNoColumn: "PURCHASE_NO", token);
        return new(true, null);
    }



    public static async Task UpdatePurchaseMoreQtyAsync(
        SqlConnection connection, SqlTransaction transaction,
        string type, string no, int serialNo, decimal qty, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            UPDATE dbo.PUR_PURCHASE_MORE SET QTY=@Qty
            WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No AND SERIAL_NO=@Serial;
            """, connection, transaction);
        command.Parameters.Add("@Qty", SqlDbType.Decimal).Value = qty;
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        command.Parameters.Add("@Serial", SqlDbType.Int).Value = serialNo;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>收料单（1607）AfterSave：采购单一致性 + 批号要求 + 不超采购数量。</summary>




    /// <summary>预付帐款单（170203）AfterSave：三件套完整性 + 金额比较 + 主表 AMOUNT 汇总。</summary>


    public static async Task<SprocResult> PurPayAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = DomainRuleService.KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        await using var prepaySum = new SqlCommand($"""
            UPDATE m SET PREPAY_SUM=(SELECT SUM(PREPAY_AMOUNT) FROM dbo.PUR_PAY_PREPAY
                WHERE PAY_TYPE=@Type AND PAY_NO=@No),
                PAYOUT_SUM=AMOUNT_TAX-REBATE_SUM-(SELECT SUM(PREPAY_AMOUNT) FROM dbo.PUR_PAY_PREPAY
                WHERE PAY_TYPE=@Type AND PAY_NO=@No),
                LAST_UPDATE_DATE=GETDATE()
            FROM dbo.PUR_PAY_M m WHERE m.[{typeColumn}]=@Type AND m.[{noColumn}]=@No;
            """, connection, transaction);
        prepaySum.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        prepaySum.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await prepaySum.ExecuteNonQueryAsync(token);

        var negative = await DomainRuleService.ExistsAsync(connection, transaction,
            $"SELECT TOP 1 1 FROM dbo.PUR_PAY_M WHERE [{typeColumn}]=@Type AND [{noColumn}]=@No AND PAYOUT_SUM<0;",
            type, no, token);
        if (negative) return new(false, "实付金额不能为负数");
        var exceedMaster = await DomainRuleService.ExistsAsync(connection, transaction,
            $"SELECT TOP 1 1 FROM dbo.PUR_PAY_M WHERE [{typeColumn}]=@Type AND [{noColumn}]=@No AND PAYOUT_SUM>AMOUNT_TAX-REBATE_SUM-PREPAY_SUM;",
            type, no, token);
        if (exceedMaster) return new(false, "实付金额 不能大于 应付金额-现金折扣-预付冲帐");
        var dueErrors = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT od.DUE_NO, od.SUM_AMOUNT, od.PAYOUT_AMOUNT, sd.PAYOUT_AMOUNT
            FROM dbo.PUR_DUE_M od
            INNER JOIN (SELECT DUE_TYPE, DUE_NO, SUM(PAYOUT_AMOUNT) PAYOUT_AMOUNT
                        FROM dbo.PUR_PAY_D WHERE PAY_TYPE=@Type AND PAY_NO=@No
                        GROUP BY DUE_TYPE, DUE_NO) sd
              ON od.DUE_TYPE=sd.DUE_TYPE AND od.DUE_NO=sd.DUE_NO
            WHERE od.PAYOUT_AMOUNT + sd.PAYOUT_AMOUNT > od.SUM_AMOUNT;
            """, type, no, token,
            line: r => $"{r.GetString(0).Trim()}       {Convert.ToDouble(r.GetValue(1))}          {Convert.ToDouble(r.GetValue(2))}          {Convert.ToDouble(r.GetValue(3))}");
        if (dueErrors is not null)
            return new(false, "以下会出现对帐单已付款大于应付款\r\n对帐单号     应付款       已付款       本次付款\r\n" + dueErrors);
        return new(true, null);
    }

}
