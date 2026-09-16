using System.Data;
using Microsoft.Data.SqlClient;

using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// Procurement domain rules executed after module saves (quotes, purchase orders, receipts, applies, payments, prepayments, cancellations and due documents). Methods run inside the caller's transaction.
/// </summary>
public static class PurDomainRules
{

    public static async Task<SprocResult> PurchaseDueAfterSaveAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
    {
        if (pkColumns.Count < 2 || definition.MasterTable is not { } master)
            return new(false, "应付货款单领域规则缺少主键或主表定义。");
        var typeColumn = pkColumns[0];
        var noColumn = pkColumns[1];
        var dueType = keyValues[0];
        var dueNo = keyValues[1];

        // 1. 数量校验：对帐明细不可超出收料/退料单数量
        var receiveErrors = await DomainRuleService.FindExceededAsync(connection, transaction, dueType, dueNo, token,
            detailTable: "PUR_DUE_D", detailTypeColumn: "DUE_TYPE", detailNoColumn: "DUE_NO",
            detailGroupTypeColumn: "R_C_TYPE", detailGroupNoColumn: "R_C_NO", detailGroupSerialColumn: "R_C_SERIAL_NO",
            sourceTable: "PUR_RECEIVE_D", sourceTypeColumn: "RECEIVE_TYPE", sourceNoColumn: "RECEIVE_NO");
        if (receiveErrors is not null)
            return new(false, "以下对帐已超出收料单数量\r\n 收料单号  收料数量  已对帐数量  单据数量\r\n" + receiveErrors);
        var cancelErrors = await DomainRuleService.FindExceededAsync(connection, transaction, dueType, dueNo, token,
            detailTable: "PUR_DUE_D", detailTypeColumn: "DUE_TYPE", detailNoColumn: "DUE_NO",
            detailGroupTypeColumn: "R_C_TYPE", detailGroupNoColumn: "R_C_NO", detailGroupSerialColumn: "R_C_SERIAL_NO",
            sourceTable: "PUR_CANCEL_D", sourceTypeColumn: "CANCEL_TYPE", sourceNoColumn: "CANCEL_NO");
        if (cancelErrors is not null)
            return new(false, "以下对帐已超出退料单数量\r\n 退料单号  退料数量  已对帐数量  单据数量\r\n" + cancelErrors);

        // 2. 主表金额汇总
        const string sql = """
            UPDATE m
            SET m.AMOUNT=d.AMOUNT, m.TAX_SUM=d.TAX_SUM, m.AMOUNT_TAX=d.AMOUNT_TAX,
                m.SUM_AMOUNT=d.AMOUNT_TAX+ISNULL(m.OTHER_PRICE,0), m.QTY_TOTAL=d.QTY_TOTAL
            FROM dbo.PUR_DUE_M m
            INNER JOIN (
                SELECT DUE_TYPE, DUE_NO,
                       ROUND(SUM(AMOUNT_TAX),2) AS AMOUNT_TAX,
                       ROUND(SUM(AMOUNT),2) AS AMOUNT,
                       ROUND(SUM(TAX_SUM),2) AS TAX_SUM,
                       ROUND(SUM(QTY),2) AS QTY_TOTAL
                FROM dbo.PUR_DUE_D
                WHERE DUE_TYPE=@Type AND DUE_NO=@No
                GROUP BY DUE_TYPE, DUE_NO
            ) d ON m.DUE_TYPE=d.DUE_TYPE AND m.DUE_NO=d.DUE_NO
            WHERE m.DUE_TYPE=@Type AND m.DUE_NO=@No;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = dueType;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = dueNo;
        await command.ExecuteNonQueryAsync(token);
        return new(true, null);
    }

    public static async Task<SprocResult> PurCancelAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "采购退料单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        var validated = await DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "PUR_CANCEL_D", "CANCEL_TYPE", "CANCEL_NO",
            [
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);
        if (!validated.Success) return validated;
        var qtyCheck = await PurCancelQtyCheckAsync(connection, transaction, type, no, token);
        return qtyCheck ?? new(true, null);
    }

    public static async Task<SprocResult?> PurCancelQtyCheckAsync(
        SqlConnection connection, SqlTransaction transaction, string type, string no, CancellationToken token)
    {
        var rows = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO,
                   SUM(CASE WHEN p.SRC='P' THEN p.QTY END) PUR_QTY,
                   ISNULL(SUM(CASE WHEN p.SRC='R' THEN p.QTY END),0) REC_QTY,
                   SUM(CASE WHEN p.SRC='C' THEN p.QTY END) RET_QTY,
                   SUM(CASE WHEN p.SRC='P' THEN p.SPARE_QTY END) PUR_SPARE_QTY,
                   ISNULL(SUM(CASE WHEN p.SRC='R' THEN p.SPARE_QTY END),0) REC_SPARE_QTY,
                   SUM(CASE WHEN p.SRC='C' THEN p.SPARE_QTY END) RET_SPARE_QTY
            FROM dbo.PUR_CANCEL_D d
            INNER JOIN (
                SELECT PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO AS PURCHASE_SERIAL_NO, QTY, SPARE_QTY, 'P' SRC FROM dbo.PUR_PURCHASE_D
                UNION ALL
                SELECT PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, QTY, SPARE_QTY, 'R' SRC FROM dbo.PUR_RECEIVE_D
                UNION ALL
                SELECT PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, QTY, SPARE_QTY, 'C' SRC FROM dbo.PUR_CANCEL_D
            ) p ON p.PURCHASE_TYPE=d.PURCHASE_TYPE AND p.PURCHASE_NO=d.PURCHASE_NO AND p.PURCHASE_SERIAL_NO=d.PURCHASE_SERIAL_NO
            WHERE d.CANCEL_TYPE=@Type AND d.CANCEL_NO=@No
            GROUP BY d.SERIAL_NO
            HAVING SUM(CASE WHEN p.SRC='C' THEN p.QTY END) > ISNULL(SUM(CASE WHEN p.SRC='R' THEN p.QTY END),0)
                OR SUM(CASE WHEN p.SRC='C' THEN p.SPARE_QTY END) > ISNULL(SUM(CASE WHEN p.SRC='R' THEN p.SPARE_QTY END),0);
            """, type, no, token,
            line: r => string.Join("    ", Enumerable.Range(0, r.FieldCount).Select(i => (Convert.ToString(r.GetValue(i)) ?? string.Empty).Trim())));
        return rows is null
            ? null
            : new(false, "以下序号项退料数量大于收料\r\n 序号  采购数量   已收  退料   采购备品   已收备品  退备品\r\n" + rows);
    }

    /// <summary>员工基本资料（P_HR_EMPLOYEE）AfterSave：工号唯一（他人占用且 STATE<>5 即拒绝）。</summary>


    /// <summary>制造命令单变更（1509）AfterSave：原单已批核 + 变更量不小于已生产/已领料。</summary>


    /// <summary>采购单变更（1609）AfterSave：原单已批核 + 变更量不小于已收货。</summary>
    public static async Task<SprocResult> PurPurchaseChangeAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        if (await DomainRuleService.ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.PUR_PURCHASE_M m
            INNER JOIN dbo.PUR_PURCHASE_CHANGE_M c ON c.PURCHASE_TYPE=m.PURCHASE_TYPE AND c.PURCHASE_NO=m.PURCHASE_NO
            WHERE c.CHANGE_PURCHASE_TYPE=@Type AND c.CHANGE_PURCHASE_NO=@No AND m.CONFIRM_TAG=0;
            """, type, no, token))
            return new(false, "采购单未批核，不可变更");
        var lines = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT oc.SERIAL_NO FROM dbo.PUR_PURCHASE_D od
            INNER JOIN dbo.PUR_PURCHASE_CHANGE_D oc
              ON oc.PURCHASE_TYPE=od.PURCHASE_TYPE AND oc.PURCHASE_NO=od.PURCHASE_NO AND oc.PURCHASE_SERIAL_NO=od.SERIAL_NO
            WHERE oc.CHANGE_PURCHASE_TYPE=@Type AND oc.CHANGE_PURCHASE_NO=@No AND oc.QTY < ISNULL(od.RECEIVE_QTY,0);
            """, type, no, token, line: r => "    " + Convert.ToInt32(r.GetValue(0)).ToString());
        return lines is null
            ? new(true, null)
            : new(false, "变更后以下序号项采购单数量小于已收货数量\r\n" + lines);
    }

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


    /// <summary>收料单（1607）AfterSave：采购单一致性 + 批号要求 + 不超采购数量。</summary>
    public static async Task<SprocResult> PurReceiveAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        // 需批号产品必须填写 BATCH_NO
        var batchMissing = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_RECEIVE_D d
            WHERE RECEIVE_TYPE=@Type AND RECEIVE_NO=@No AND ISNULL(BATCH_NO,'')=''
              AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO AND p.MANAGE_BATCH=1);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (batchMissing is not null)
            return new(false, "以下序号项需要输入批号 \r\n" + batchMissing);
        // 收料数量不超出采购数量（+0.1 容差）
        var overReceive = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT i.PURCHASE_SERIAL_NO, o.QTY, ISNULL(o.RECEIVE_QTY,0), i.QTY
            FROM (SELECT PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, MAX(SERIAL_NO) SERIAL_NO, SUM(QTY) QTY
                  FROM dbo.PUR_RECEIVE_D WHERE RECEIVE_TYPE=@Type AND RECEIVE_NO=@No
                  GROUP BY PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO) i
            INNER JOIN dbo.PUR_PURCHASE_D o
              ON o.PURCHASE_TYPE=i.PURCHASE_TYPE AND o.PURCHASE_NO=i.PURCHASE_NO AND o.SERIAL_NO=i.PURCHASE_SERIAL_NO
            WHERE i.QTY > o.QTY-ISNULL(o.RECEIVE_QTY,0)+0.1;
            """, type, no, token,
            line: r => $"{Convert.ToInt32(r.GetValue(0))}    {Convert.ToDouble(r.GetValue(1))}    {Convert.ToDouble(r.GetValue(2))}    {Convert.ToDouble(r.GetValue(3))}");
        if (overReceive is not null)
            return new(false, "以下项收料数量超出采购数量\r\n序号  采购数量  已收数量  单据数量\r\n" + overReceive);
        return new(true, null);
    }

    /// <summary>
    /// 请购单（1615）AfterSave：产品存在性 + PUR_APPLY_MORE 汇总同步
    /// （补明细行、REQUIRE_QTY/LOST_QTY 回填、订单/生产单号、申购数量分配）。
    /// </summary>


    /// <summary>
    /// 请购单（1615）AfterSave：产品存在性 + PUR_APPLY_MORE 汇总同步
    /// （补明细行、REQUIRE_QTY/LOST_QTY 回填、订单/生产单号、申购数量分配）。
    /// </summary>
    public static async Task<SprocResult> PurApplyAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];

        // 无 MORE 行时仅清零 REQUIRE_QTY
        var moreCount = await DomainRuleService.ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.PUR_APPLY_MORE WHERE APPLY_TYPE=@Type AND APPLY_NO=@No;", type, no, token);
        await using (var clearReq = new SqlCommand(
            "UPDATE dbo.PUR_APPLY_D SET REQUIRE_QTY=0 WHERE APPLY_TYPE=@Type AND APPLY_NO=@No;",
            connection, transaction))
        {
            clearReq.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            clearReq.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await clearReq.ExecuteNonQueryAsync(token);
        }
        if (!moreCount) return new(true, null);

        // 1. 补明细行（MORE 中不在明细的产品）
        var maxSerial = await DomainRuleService.ScalarIntAsync(connection, transaction,
            "SELECT ISNULL(MAX(SERIAL_NO),0) FROM dbo.PUR_APPLY_D WHERE APPLY_TYPE=@Type AND APPLY_NO=@No;",
            type, no, token);
        var moreRows = new List<(int SerialNo, string ProNo, decimal RequireQty)>();
        await using (var read = new SqlCommand("""
            SELECT SERIAL_NO, LTRIM(RTRIM(PRO_NO)), REQUIRE_QTY FROM dbo.PUR_APPLY_MORE
            WHERE APPLY_TYPE=@Type AND APPLY_NO=@No ORDER BY PRO_NO, SERIAL_NO;
            """, connection, transaction))
        {
            read.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            read.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await using var reader = await read.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                moreRows.Add((reader.GetInt32(0), reader.GetString(1), Convert.ToDecimal(reader.GetValue(2))));
        }
        var missingProducts = await DomainRuleService.ReadStringsAsync(connection, transaction, """
            SELECT DISTINCT LTRIM(RTRIM(m.PRO_NO)) FROM dbo.PUR_APPLY_MORE m
            WHERE m.APPLY_TYPE=@Type AND m.APPLY_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PUR_APPLY_D d WHERE d.APPLY_TYPE=m.APPLY_TYPE AND d.APPLY_NO=m.APPLY_NO AND d.PRO_NO=m.PRO_NO);
            """, type, no, token);
        if (missingProducts.Count > 0)
        {
            foreach (var proNo in missingProducts)
            {
                maxSerial++;
                await using var insert = new SqlCommand("""
                    INSERT INTO dbo.PUR_APPLY_D (APPLY_TYPE, APPLY_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY, UNIT_ID)
                    SELECT @Type, @No, @Serial, p.PRO_NO, p.DEPOT_ID, t.REQUIRE_QTY, p.UNIT_ID
                    FROM (SELECT @Req AS REQUIRE_QTY) t
                    LEFT JOIN dbo.PRODUCT p ON p.PRO_NO=@ProNo;
                    """, connection, transaction);
                insert.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
                insert.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
                insert.Parameters.Add("@Serial", SqlDbType.Int).Value = maxSerial;
                insert.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
                insert.Parameters.Add("@Req", SqlDbType.Decimal).Value =
                    moreRows.Where(r => r.ProNo == proNo).Sum(r => r.RequireQty);
                await insert.ExecuteNonQueryAsync(token);
            }
        }
        // 2. REQUIRE_QTY/LOST_QTY 回填
        await using (var syncReq = new SqlCommand("""
            UPDATE d SET REQUIRE_QTY=s.REQUIRE_QTY, LOST_QTY=s.LOST_QTY
            FROM dbo.PUR_APPLY_D d
            INNER JOIN (SELECT APPLY_TYPE, APPLY_NO, PRO_NO, SUM(REQUIRE_QTY) REQUIRE_QTY, SUM(LOST_QTY) LOST_QTY
                        FROM dbo.PUR_APPLY_MORE WHERE APPLY_TYPE=@Type AND APPLY_NO=@No
                        GROUP BY APPLY_TYPE, APPLY_NO, PRO_NO) s
              ON d.APPLY_TYPE=s.APPLY_TYPE AND d.APPLY_NO=s.APPLY_NO AND d.PRO_NO=s.PRO_NO
            WHERE d.APPLY_TYPE=@Type AND d.APPLY_NO=@No;
            """, connection, transaction))
        {
            syncReq.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            syncReq.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await syncReq.ExecuteNonQueryAsync(token);
        }
        // 3. 订单字段回填（客户单号/预交日期/客户代号）
        await using (var syncOrder = new SqlCommand("""
            UPDATE d SET d.ORDER_TYPE=d2.ORDER_TYPE, d.ORDER_NO=d2.ORDER_NO,
                d.ORDER_SERIAL_NO=d2.SERIAL_NO, d.CLIENT_PRO_NO=d2.CLIENT_PRO_NO,
                d.CLIENT_ORDER_NO=d2.CLIENT_ORDER_NO, d.USED_DATE=d2.PRE_SEND_DATE,
                d.CLIENT_ID=c.CLIENT_ID
            FROM dbo.PUR_APPLY_D d
            INNER JOIN dbo.PUR_APPLY_MORE m
              ON m.APPLY_TYPE=d.APPLY_TYPE AND m.APPLY_NO=d.APPLY_NO AND m.PRO_NO=d.PRO_NO
            INNER JOIN dbo.COP_ORDER_D d2
              ON d2.ORDER_TYPE=m.ORDER_TYPE AND d2.ORDER_NO=m.ORDER_NO AND d2.SERIAL_NO=m.ORDER_SERIAL_NO
            INNER JOIN dbo.COP_ORDER_M c
              ON c.ORDER_TYPE=d2.ORDER_TYPE AND c.ORDER_NO=d2.ORDER_NO
            WHERE d.APPLY_TYPE=@Type AND d.APPLY_NO=@No;
            """, connection, transaction))
        {
            syncOrder.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            syncOrder.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await syncOrder.ExecuteNonQueryAsync(token);
        }
        // 4. 申购数量分配
        var detailQty = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using (var readQty = new SqlCommand("""
            SELECT LTRIM(RTRIM(PRO_NO)), QTY FROM dbo.PUR_APPLY_D WHERE APPLY_TYPE=@Type AND APPLY_NO=@No;
            """, connection, transaction))
        {
            readQty.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            readQty.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await using var reader = await readQty.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                detailQty[reader.GetString(0)] = Convert.ToDecimal(reader.GetValue(1));
        }
        await using (var clearMore = new SqlCommand(
            "UPDATE dbo.PUR_APPLY_MORE SET QTY=0 WHERE APPLY_TYPE=@Type AND APPLY_NO=@No;", connection, transaction))
        {
            clearMore.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            clearMore.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await clearMore.ExecuteNonQueryAsync(token);
        }
        string? currentPro = null;
        var remaining = 0m;
        foreach (var row in moreRows)
        {
            if (!string.Equals(currentPro, row.ProNo, StringComparison.OrdinalIgnoreCase))
            {
                currentPro = row.ProNo;
                remaining = detailQty.GetValueOrDefault(row.ProNo);
            }
            if (remaining > row.RequireQty)
            {
                await DomainRuleService.UpdateMoreQtyAsync(connection, transaction, type, no, row.SerialNo, row.RequireQty, token);
                remaining -= row.RequireQty;
            }
            else if (remaining > 0)
            {
                await DomainRuleService.UpdateMoreQtyAsync(connection, transaction, type, no, row.SerialNo, remaining, token);
                remaining -= row.RequireQty;
            }
        }
        // 5. 主表 ORDER_NO / PRODUCE_NO 汇总
        await DomainRuleService.UpdateDistinctFieldAsync(connection, transaction, "PUR_APPLY_M", "APPLY_TYPE", "APPLY_NO",
            type, no, "ORDER_NO", "PUR_APPLY_MORE", "ORDER_NO",
            moreTypeColumn: "APPLY_TYPE", moreNoColumn: "APPLY_NO", token);
        await DomainRuleService.UpdateDistinctFieldAsync(connection, transaction, "PUR_APPLY_M", "APPLY_TYPE", "APPLY_NO",
            type, no, "PRODUCE_NO", "PUR_APPLY_MORE", "PRODUCE_NO",
            moreTypeColumn: "APPLY_TYPE", moreNoColumn: "APPLY_NO", token);
        return new(true, null);
    }


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

    /// <summary>预付帐款单（170203）AfterSave：厂商校验 + 预付不超采购金额 + 金额汇总。</summary>


    /// <summary>预付帐款单（170203）AfterSave：预付不超采购金额 + 金额汇总。</summary>
    public static async Task<SprocResult> PurPrepayAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = DomainRuleService.KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        // 采购单引用联动校验：填了采购单号就必须同时填单别/序号（三者要么全填、要么全不填），
        // 支持"无采购单预付"（打样/合作开发等场景），避免存半截引用。
        var incompleteRef = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO
            FROM dbo.PUR_PREPAY_D d
            WHERE d.PREPAY_TYPE=@Type AND d.PREPAY_NO=@No
              AND (LTRIM(RTRIM(ISNULL(d.PURCHASE_NO,'')))<>''
                   AND (LTRIM(RTRIM(ISNULL(d.PURCHASE_TYPE,'')))='' OR d.PURCHASE_SERIAL_NO IS NULL OR d.PURCHASE_SERIAL_NO=0));
            """, type, no, token,
            line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (incompleteRef is not null)
            return new(false, "以下序号项已填采购单号，但未填采购单别或采购序号（三者为一体）：\r\n" + incompleteRef);
        // 预付金额不能超出采购金额（仅对填了采购单引用的明细行生效；无采购单的明细行跳过）
        var purchaseErrors = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT i.PURCHASE_SERIAL_NO, o.AMOUNT, o.FINISHED_AMOUNT, i.AMOUNT
            FROM (SELECT PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, SUM(AMOUNT) AMOUNT
                  FROM dbo.PUR_PREPAY_D WHERE PREPAY_TYPE=@Type AND PREPAY_NO=@No
                  GROUP BY PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO) i
            INNER JOIN dbo.PUR_PURCHASE_D o
              ON o.PURCHASE_TYPE=i.PURCHASE_TYPE AND o.PURCHASE_NO=i.PURCHASE_NO AND o.SERIAL_NO=i.PURCHASE_SERIAL_NO
            WHERE i.AMOUNT > o.AMOUNT-ISNULL(o.FINISHED_AMOUNT,0);
            """, type, no, token,
            line: r => $"{Convert.ToInt32(r.GetValue(0))}    {Convert.ToDouble(r.GetValue(1))}    {Convert.ToDouble(r.GetValue(2))}    {Convert.ToDouble(r.GetValue(3))}");
        if (purchaseErrors is not null)
            return new(false, "以下项预付金额超出采购金额\r\n序号  采购金额  已收金额  单据金额\r\n" + purchaseErrors);
        await using var amount = new SqlCommand($"""
            UPDATE m SET AMOUNT=ROUND((SELECT SUM(AMOUNT) FROM dbo.PUR_PREPAY_D
                WHERE PREPAY_TYPE=@Type AND PREPAY_NO=@No),3)
            FROM dbo.PUR_PREPAY_M m WHERE m.[{typeColumn}]=@Type AND m.[{noColumn}]=@No;
            """, connection, transaction);
        amount.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        amount.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await amount.ExecuteNonQueryAsync(token);
        return new(true, null);
    }

}
