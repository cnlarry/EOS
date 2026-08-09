using Microsoft.Data.SqlClient;
using System.Data;

namespace EOS.API.Data;

/// <summary>
/// 确定性领域规则服务（阶段 5：把旧 AFTERSAVE_SP/UPDATE_SP 等价逻辑移植为 C# 领域代码）。
/// 当前实现：
/// - purchase-due（170201 应付货款单）：数量校验（等价 P_PUR_DUE_CHECK，收料/退料不超量）
///   + 主表金额汇总（等价 P_PUR_DUE_After_Save：AMOUNT/TAX_SUM/AMOUNT_TAX/SUM_AMOUNT/QTY_TOTAL，ROUND 2）。
/// 表名/列名来自服务端元数据与常量，值全部参数化；与受控 SP 调用同事务。
/// </summary>
public sealed class DomainRuleService(ILogger<DomainRuleService> logger)
{
    /// <summary>
    /// 执行领域规则（保存后）。返回 false 表示业务校验失败（message 给用户）。
    /// </summary>
    public async Task<SprocResult> RunAfterSaveAsync(
        string ruleName,
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
    {
        try
        {
            return ruleName.ToLowerInvariant() switch
            {
                "purchase-due" => await PurchaseDueAfterSaveAsync(connection, transaction, definition, pkColumns, keyValues, token),
                "cop-receipt" => await CopReceiptAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-prepay" => await CopPrepayAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-quote" => await CopQuoteAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-account" => await CopAccountAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-order" => await CopOrderAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-quote" => await PurQuoteAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-receive" => await PurReceiveAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-apply" => await PurApplyAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-pay" => await PurPayAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-prepay" => await PurPrepayAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                _ => new(false, $"未登记的领域规则：{ruleName}"),
            };
        }
        catch (SqlException ex)
        {
            logger.LogWarning("领域规则执行异常 rule={Rule} message={Message}", ruleName, ex.Message);
            return new(false, ex.Message);
        }
    }

    private static async Task<SprocResult> PurchaseDueAfterSaveAsync(
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

        // 1. 数量校验：对帐明细不可超出收料/退料单数量（等价 P_PUR_DUE_CHECK）
        var receiveErrors = await FindExceededAsync(connection, transaction, dueType, dueNo, token,
            detailTable: "PUR_DUE_D", detailTypeColumn: "DUE_TYPE", detailNoColumn: "DUE_NO",
            detailGroupTypeColumn: "R_C_TYPE", detailGroupNoColumn: "R_C_NO", detailGroupSerialColumn: "R_C_SERIAL_NO",
            sourceTable: "PUR_RECEIVE_D", sourceTypeColumn: "RECEIVE_TYPE", sourceNoColumn: "RECEIVE_NO");
        if (receiveErrors is not null)
            return new(false, "以下对帐已超出收料单数量\r\n 收料单号  收料数量  已对帐数量  单据数量\r\n" + receiveErrors);
        var cancelErrors = await FindExceededAsync(connection, transaction, dueType, dueNo, token,
            detailTable: "PUR_DUE_D", detailTypeColumn: "DUE_TYPE", detailNoColumn: "DUE_NO",
            detailGroupTypeColumn: "R_C_TYPE", detailGroupNoColumn: "R_C_NO", detailGroupSerialColumn: "R_C_SERIAL_NO",
            sourceTable: "PUR_CANCEL_D", sourceTypeColumn: "CANCEL_TYPE", sourceNoColumn: "CANCEL_NO");
        if (cancelErrors is not null)
            return new(false, "以下对帐已超出退料单数量\r\n 退料单号  退料数量  已对帐数量  单据数量\r\n" + cancelErrors);

        // 2. 主表金额汇总（等价 P_PUR_DUE_After_Save：ROUND 2）
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

    /// <summary>收料单（1607）AfterSave：采购单一致性 + 批号要求 + 不超采购数量。</summary>
    private static async Task<SprocResult> PurReceiveAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        // 采购单与厂商相符
        var supplierMismatch = await FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO FROM dbo.PUR_PURCHASE_M o
            INNER JOIN dbo.PUR_RECEIVE_D d ON o.PURCHASE_TYPE=d.PURCHASE_TYPE AND o.PURCHASE_NO=d.PURCHASE_NO
            INNER JOIN dbo.PUR_RECEIVE_M m ON m.RECEIVE_TYPE=d.RECEIVE_TYPE AND m.RECEIVE_NO=d.RECEIVE_NO
            WHERE m.RECEIVE_TYPE=@Type AND m.RECEIVE_NO=@No AND o.SUPPLIER_ID<>m.SUPPLIER_ID;
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (supplierMismatch is not null)
            return new(false, "以下序号项收料单与采购单厂商不符 \r\n" + supplierMismatch);
        // 采购单存在
        var purchaseMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_RECEIVE_D d
            WHERE RECEIVE_TYPE=@Type AND RECEIVE_NO=@No AND ISNULL(PURCHASE_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_M o WHERE o.PURCHASE_TYPE=d.PURCHASE_TYPE AND o.PURCHASE_NO=d.PURCHASE_NO);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (purchaseMissing is not null)
            return new(false, "以下序号项采购订单不存在 \r\n" + purchaseMissing);
        // 采购订单序号与产品编号相符
        var purchaseProduct = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_RECEIVE_D d
            WHERE RECEIVE_TYPE=@Type AND RECEIVE_NO=@No AND ISNULL(PURCHASE_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_D o
                              WHERE o.PURCHASE_TYPE=d.PURCHASE_TYPE AND o.PURCHASE_NO=d.PURCHASE_NO
                                AND o.SERIAL_NO=d.PURCHASE_SERIAL_NO AND o.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (purchaseProduct is not null)
            return new(false, "以下序号项采购订单序号与产品编号不相符 \r\n" + purchaseProduct);
        // 产品编号存在
        var productMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_RECEIVE_D d
            WHERE RECEIVE_TYPE=@Type AND RECEIVE_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (productMissing is not null)
            return new(false, "以下序号项产品编号不存在 \r\n" + productMissing);
        // 需批号产品必须填写 BATCH_NO
        var batchMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_RECEIVE_D d
            WHERE RECEIVE_TYPE=@Type AND RECEIVE_NO=@No AND ISNULL(BATCH_NO,'')=''
              AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO AND p.MANAGE_BATCH=1);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (batchMissing is not null)
            return new(false, "以下序号项需要输入批号 \r\n" + batchMissing);
        // 收料数量不超出采购数量（+0.1 容差）
        var overReceive = await FindLinesAsync(connection, transaction,
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
    private static async Task<SprocResult> PurApplyAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var productMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_APPLY_D d
            WHERE APPLY_TYPE=@Type AND APPLY_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (productMissing is not null)
            return new(false, "以下序号项产品编号不存在 \r\n" + productMissing);

        // 无 MORE 行时仅清零 REQUIRE_QTY（与旧 SP 一致，主流程幂等）
        var moreCount = await ExistsAsync(connection, transaction,
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
        var maxSerial = await ScalarIntAsync(connection, transaction,
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
        var missingProducts = await ReadStringsAsync(connection, transaction, """
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
        // 4. 申购数量分配（等价旧游标：按 PRO_NO, SERIAL_NO 顺序从明细 QTY 分配）
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
                await UpdateMoreQtyAsync(connection, transaction, type, no, row.SerialNo, row.RequireQty, token);
                remaining -= row.RequireQty;
            }
            else if (remaining > 0)
            {
                await UpdateMoreQtyAsync(connection, transaction, type, no, row.SerialNo, remaining, token);
                remaining -= row.RequireQty;
            }
        }
        // 5. 主表 ORDER_NO / PRODUCE_NO 汇总
        await UpdateDistinctFieldAsync(connection, transaction, type, no, "ORDER_NO", "PUR_APPLY_MORE", "ORDER_NO", token);
        await UpdateDistinctFieldAsync(connection, transaction, type, no, "PRODUCE_NO", "PUR_APPLY_MORE", "PRODUCE_NO", token);
        return new(true, null);
    }

    private static async Task<int> ScalarIntAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, string type, string no, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        var value = await command.ExecuteScalarAsync(token);
        return value is null ? 0 : Convert.ToInt32(value);
    }

    private static async Task<List<string>> ReadStringsAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, string type, string no, CancellationToken token)
    {
        var result = new List<string>();
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task UpdateMoreQtyAsync(
        SqlConnection connection, SqlTransaction transaction,
        string type, string no, int serialNo, decimal qty, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            UPDATE dbo.PUR_APPLY_MORE SET QTY=@Qty
            WHERE APPLY_TYPE=@Type AND APPLY_NO=@No AND SERIAL_NO=@Serial;
            """, connection, transaction);
        command.Parameters.Add("@Qty", SqlDbType.Decimal).Value = qty;
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        command.Parameters.Add("@Serial", SqlDbType.Int).Value = serialNo;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task UpdateDistinctFieldAsync(
        SqlConnection connection, SqlTransaction transaction,
        string type, string no, string masterColumn, string moreTable, string moreColumn, CancellationToken token)
    {
        var values = await ReadStringsAsync(connection, transaction,
            $"SELECT DISTINCT LTRIM(RTRIM(ISNULL([{moreColumn}],''))) FROM dbo.[{moreTable}] " +
            $"WHERE APPLY_TYPE=@Type AND APPLY_NO=@No AND ISNULL([{moreColumn}],'')<>'' ORDER BY 1;",
            type, no, token);
        if (values.Count == 0) return;
        await using var command = new SqlCommand(
            $"UPDATE dbo.PUR_APPLY_M SET [{masterColumn}]=@Value WHERE APPLY_TYPE=@Type AND APPLY_NO=@No;",
            connection, transaction);
        command.Parameters.Add("@Value", SqlDbType.NVarChar, 300).Value = string.Join(',', values);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>客户订单（1405）AfterSave：订单检查（P_COP_ORDER_CHECK 六规则）+ 客户/报价/产品一致性。</summary>
    private static async Task<SprocResult> CopOrderAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];

        // 1. 客户交易天数（SYSSS.CLIENT_DAYS；任一缺失跳过）
        var tradeDays = await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.COP_ORDER_M m
            CROSS JOIN (SELECT TOP 1 CLIENT_DAYS FROM dbo.SYSSS) s
            JOIN dbo.CLIENT c ON c.CLIENT_ID=m.CLIENT_ID
            WHERE m.ORDER_TYPE=@Type AND m.ORDER_NO=@No
              AND s.CLIENT_DAYS IS NOT NULL AND c.LAST_TRADE_DATE IS NOT NULL
              AND s.CLIENT_DAYS < DATEDIFF(day, c.LAST_TRADE_DATE, m.ORDER_DATE);
            """, type, no, token);
        if (tradeDays) return new(false, "已超过客户交易天数");
        // 2. 最低订单金额（MIN_ORDER_AMOUNT>0 才启用）
        var minOrder = await FindLinesAsync(connection, transaction,
            """
            SELECT c.MIN_ORDER_AMOUNT, c.CURR_ID
            FROM dbo.COP_ORDER_M m JOIN dbo.CLIENT c ON c.CLIENT_ID=m.CLIENT_ID
            LEFT JOIN dbo.CURR r ON r.CURR_ID=c.CURR_ID
            WHERE m.ORDER_TYPE=@Type AND m.ORDER_NO=@No AND ISNULL(c.MIN_ORDER_AMOUNT,0) > 0
              AND c.MIN_ORDER_AMOUNT * ISNULL(r.CURR_RATE,1) > ISNULL(m.AMOUNT_TAX,0) * ISNULL(m.CURR_RATE,1);
            """, type, no, token,
            line: r => $"{Convert.ToDouble(r.GetValue(0))}{r.GetString(1).Trim()}");
        if (minOrder is not null)
            return new(false, "总金额小于客户最低订单额:" + minOrder);
        // 3. 客户信用余额（CREDIT_LIMIT_NUM 为 NULL 时不启用——旧系统 NULL 比较语义）
        var credit = await FindLinesAsync(connection, transaction,
            """
            SELECT c.CREDIT_LIMIT_NUM * ISNULL(r.CURR_RATE,1) - ISNULL(m.AMOUNT_TAX,0) * ISNULL(m.CURR_RATE,1), c.CURR_ID
            FROM dbo.COP_ORDER_M m JOIN dbo.CLIENT c ON c.CLIENT_ID=m.CLIENT_ID
            LEFT JOIN dbo.CURR r ON r.CURR_ID=c.CURR_ID
            WHERE m.ORDER_TYPE=@Type AND m.ORDER_NO=@No
              AND c.CREDIT_LIMIT_NUM * ISNULL(r.CURR_RATE,1) < ISNULL(m.AMOUNT_TAX,0) * ISNULL(m.CURR_RATE,1);
            """, type, no, token,
            line: r => $"{Math.Round(Convert.ToDouble(r.GetValue(0)), 2)}{r.GetString(1).Trim()}");
        if (credit is not null)
            return new(false, "客户信用余额不足：" + credit);
        // 4. 产品交易天数（SYSSS.PRODUCT_DAYS；客户交易天数缺失时旧系统跳过本检查）
        var productDays = await FindLinesAsync(connection, transaction,
            """
            SELECT TOP 10 p.PRO_NO FROM dbo.COP_ORDER_D o
            JOIN dbo.COP_ORDER_M m ON m.ORDER_TYPE=o.ORDER_TYPE AND m.ORDER_NO=o.ORDER_NO
            JOIN dbo.CLIENT c ON c.CLIENT_ID=m.CLIENT_ID
            JOIN dbo.PRODUCT p ON p.PRO_NO=o.PRO_NO
            CROSS JOIN (SELECT TOP 1 PRODUCT_DAYS FROM dbo.SYSSS) s
            WHERE o.ORDER_TYPE=@Type AND o.ORDER_NO=@No
              AND s.PRODUCT_DAYS IS NOT NULL AND c.LAST_TRADE_DATE IS NOT NULL
              AND s.PRODUCT_DAYS < DATEDIFF(day, p.LAST_TRADE_DATE, m.ORDER_DATE);
            """, type, no, token, line: r => r.GetString(0).Trim());
        if (productDays is not null)
            return new(false, "以下产品编号已超出产品交易天数限制\r\n" + productDays);
        // 5. 产品计价有效期（CLIENT_PRICE_D.IN_EFFECT_DATE >= 订单日期）
        var priceExpired = await FindLinesAsync(connection, transaction,
            """
            SELECT DISTINCT a.PRO_NO FROM dbo.COP_ORDER_D a
            JOIN dbo.COP_ORDER_M m ON m.ORDER_TYPE=a.ORDER_TYPE AND m.ORDER_NO=a.ORDER_NO
            JOIN dbo.CLIENT_PRICE_D p ON p.CLIENT_ID=m.CLIENT_ID AND p.PRO_NO=a.PRO_NO
            WHERE a.ORDER_TYPE=@Type AND a.ORDER_NO=@No AND p.IN_EFFECT_DATE < m.ORDER_DATE;
            """, type, no, token, line: r => r.GetString(0).Trim());
        if (priceExpired is not null)
            return new(false, "以下产品计价已过有效期\r\n" + priceExpired);
        // 6. 最小生产数量（QTY < MIN_PRODUCE_QTY）
        var minProduce = await FindLinesAsync(connection, transaction,
            """
            SELECT TOP 10 p.PRO_NO FROM dbo.COP_ORDER_D o
            JOIN dbo.PRODUCT p ON p.PRO_NO=o.PRO_NO
            WHERE o.ORDER_TYPE=@Type AND o.ORDER_NO=@No AND o.QTY < ISNULL(p.MIN_PRODUCE_QTY,0);
            """, type, no, token, line: r => r.GetString(0).Trim());
        if (minProduce is not null)
            return new(false, "以下产品编号订单量低于最小生产要求数量\r\n" + minProduce);

        // AfterSave 校验：客户存在
        var clientOk = await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.COP_ORDER_M m JOIN dbo.CLIENT c ON c.CLIENT_ID=m.CLIENT_ID
            WHERE m.ORDER_TYPE=@Type AND m.ORDER_NO=@No AND c.BUSINESS_TAG=0;
            """, type, no, token);
        if (!clientOk) return new(false, "客户编号不存在或已停止交易。");
        // 客户订单号不重复（排除自身）
        var duplicateOrderNo = await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.COP_ORDER_M
            WHERE CLIENT_ORDER_NO=(SELECT CLIENT_ORDER_NO FROM dbo.COP_ORDER_M WHERE ORDER_TYPE=@Type AND ORDER_NO=@No)
              AND ISNULL(CLIENT_ORDER_NO,'')<>'' AND NOT (ORDER_TYPE=@Type AND ORDER_NO=@No);
            """, type, no, token);
        if (duplicateOrderNo) return new(false, "客户订单号重复。");
        // 预交日期 >= 订单日期
        var preSendLines = await FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO FROM dbo.COP_ORDER_D d
            JOIN dbo.COP_ORDER_M m ON m.ORDER_TYPE=d.ORDER_TYPE AND m.ORDER_NO=d.ORDER_NO
            WHERE d.ORDER_TYPE=@Type AND d.ORDER_NO=@No AND d.PRE_SEND_DATE < m.ORDER_DATE;
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (preSendLines is not null)
            return new(false, "以下序号项预交日期小于订单日期 \r\n" + preSendLines);
        // 报价单与订单客户一致
        var quoteMismatch = await FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO FROM dbo.COP_QUOTE_M q
            INNER JOIN dbo.COP_ORDER_D d ON q.QUOTE_TYPE=d.QUOTE_TYPE AND q.QUOTE_NO=d.QUOTE_NO
            INNER JOIN dbo.COP_ORDER_M m ON m.ORDER_TYPE=d.ORDER_TYPE AND m.ORDER_NO=d.ORDER_NO
            WHERE m.ORDER_TYPE=@Type AND m.ORDER_NO=@No AND q.CLIENT_ID<>m.CLIENT_ID;
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (quoteMismatch is not null)
            return new(false, "以下序号项报价单与订单客户不符 \r\n" + quoteMismatch);
        // 报价单存在
        var quoteMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_ORDER_D d
            WHERE ORDER_TYPE=@Type AND ORDER_NO=@No AND ISNULL(QUOTE_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_QUOTE_M q WHERE q.QUOTE_TYPE=d.QUOTE_TYPE AND q.QUOTE_NO=d.QUOTE_NO);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (quoteMissing is not null)
            return new(false, "以下序号项报价单不存在 \r\n" + quoteMissing);
        // 报价单序号与产品编号相符
        var quoteProduct = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_ORDER_D d
            WHERE ORDER_TYPE=@Type AND ORDER_NO=@No AND ISNULL(QUOTE_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_QUOTE_D q
                              WHERE q.QUOTE_TYPE=d.QUOTE_TYPE AND q.QUOTE_NO=d.QUOTE_NO
                                AND q.SERIAL_NO=d.QUOTE_SERIAL_NO AND q.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (quoteProduct is not null)
            return new(false, "以下序号项报价单序号与产品编号不相符 \r\n" + quoteProduct);
        // 产品编号存在
        var productMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_ORDER_D d
            WHERE ORDER_TYPE=@Type AND ORDER_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (productMissing is not null)
            return new(false, "以下序号项产品编号不存在 \r\n" + productMissing);
        return new(true, null);
    }

    /// <summary>厂商报价单（1604）AfterSave：厂商校验 + 询价单一致性校验（镜像 cop-quote，厂商侧）。</summary>
    private static async Task<SprocResult> PurQuoteAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var supplierOk = await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.PUR_QUOTE_M m JOIN dbo.SUPPLIER c ON c.SUPPLIER_ID=m.SUPPLIER_ID
            WHERE m.QUOTE_TYPE=@Type AND m.QUOTE_NO=@No AND c.BUSINESS_TAG=0;
            """, type, no, token);
        if (!supplierOk) return new(false, "厂商编号不存在或已停止交易。");
        var mismatchLines = await FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO
            FROM dbo.PUR_CHAFFER_M q
            INNER JOIN dbo.PUR_QUOTE_D d ON q.CHAFFER_TYPE=d.CHAFFER_TYPE AND q.CHAFFER_NO=d.CHAFFER_NO
            INNER JOIN dbo.PUR_QUOTE_M m ON m.QUOTE_TYPE=d.QUOTE_TYPE AND m.QUOTE_NO=d.QUOTE_NO
            WHERE m.QUOTE_TYPE=@Type AND m.QUOTE_NO=@No AND q.SUPPLIER_ID<>m.SUPPLIER_ID;
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (mismatchLines is not null)
            return new(false, "以下序号项询价单与报价单厂商不符 \r\n" + mismatchLines);
        var missingLines = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_QUOTE_D d
            WHERE QUOTE_TYPE=@Type AND QUOTE_NO=@No AND ISNULL(CHAFFER_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.PUR_CHAFFER_M q WHERE q.CHAFFER_TYPE=d.CHAFFER_TYPE AND q.CHAFFER_NO=d.CHAFFER_NO);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (missingLines is not null)
            return new(false, "以下序号项询价单不存在 \r\n" + missingLines);
        // 询价单序号与产品编号相符
        var productMismatch = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_QUOTE_D d
            WHERE QUOTE_TYPE=@Type AND QUOTE_NO=@No AND ISNULL(CHAFFER_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.PUR_CHAFFER_D q
                              WHERE q.CHAFFER_TYPE=d.CHAFFER_TYPE AND q.CHAFFER_NO=d.CHAFFER_NO
                                AND q.SERIAL_NO=d.CHAFFER_SERIAL_NO AND q.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (productMismatch is not null)
            return new(false, "以下序号项询价单序号与产品编号不相符 \r\n" + productMismatch);
        return new(true, null);
    }

    private static async Task<string?> FindExceededAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string dueType,
        string dueNo,
        CancellationToken token,
        string detailTable,
        string detailTypeColumn,
        string detailNoColumn,
        string detailGroupTypeColumn,
        string detailGroupNoColumn,
        string detailGroupSerialColumn,
        string sourceTable,
        string sourceTypeColumn,
        string sourceNoColumn)
    {
        var sql = $"""
            SELECT od.{sourceNoColumn}, od.QTY, od.FINISHED_QTY, sd.QTY AS MY_QTY
            FROM dbo.[{sourceTable}] od
            INNER JOIN (
                SELECT [{detailGroupTypeColumn}], [{detailGroupNoColumn}], [{detailGroupSerialColumn}], SUM(QTY) QTY
                FROM dbo.[{detailTable}]
                WHERE [{detailTypeColumn}]=@Type AND [{detailNoColumn}]=@No
                GROUP BY [{detailGroupTypeColumn}], [{detailGroupNoColumn}], [{detailGroupSerialColumn}]
            ) sd
              ON od.{sourceTypeColumn}=sd.[{detailGroupTypeColumn}] AND od.{sourceNoColumn}=sd.[{detailGroupNoColumn}]
             AND od.SERIAL_NO=sd.[{detailGroupSerialColumn}]
            WHERE od.FINISHED_QTY+sd.QTY > od.QTY;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = dueType;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = dueNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        var lines = new List<string>();
        while (await reader.ReadAsync(token))
        {
            var no = reader.GetString(0).Trim();
            var qty = Convert.ToDouble(reader.GetValue(1));
            var finished = Convert.ToDouble(reader.GetValue(2));
            var myQty = Convert.ToDouble(reader.GetValue(3));
            lines.Add($"{no}    {qty}    {finished}    {myQty}");
        }
        return lines.Count > 0 ? string.Join("\r\n", lines) : null;
    }

    /// <summary>客户报价单（1416）AfterSave：客户校验 + 询价单一致性校验。</summary>
    private static async Task<SprocResult> CopQuoteAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var clientOk = await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.COP_QUOTE_M m JOIN dbo.CLIENT c ON c.CLIENT_ID=m.CLIENT_ID
            WHERE m.QUOTE_TYPE=@Type AND m.QUOTE_NO=@No AND c.BUSINESS_TAG=0;
            """, type, no, token);
        if (!clientOk) return new(false, "客户编号不存在或已停止交易。");
        // 询价单与报价单客户不符
        var mismatchLines = await FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO
            FROM dbo.COP_CHAFFER_M q
            INNER JOIN dbo.COP_QUOTE_D d ON q.CHAFFER_TYPE=d.CHAFFER_TYPE AND q.CHAFFER_NO=d.CHAFFER_NO
            INNER JOIN dbo.COP_QUOTE_M m ON m.QUOTE_TYPE=d.QUOTE_TYPE AND m.QUOTE_NO=d.QUOTE_NO
            WHERE m.QUOTE_TYPE=@Type AND m.QUOTE_NO=@No AND q.CLIENT_ID<>m.CLIENT_ID;
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (mismatchLines is not null)
            return new(false, "以下序号项询价单与报价单客户不符 \r\n" + mismatchLines);
        // 询价单不存在
        var missingLines = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_QUOTE_D d
            WHERE QUOTE_TYPE=@Type AND QUOTE_NO=@No AND ISNULL(CHAFFER_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_CHAFFER_M q WHERE q.CHAFFER_TYPE=d.CHAFFER_TYPE AND q.CHAFFER_NO=d.CHAFFER_NO);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (missingLines is not null)
            return new(false, "以下序号项询价单不存在 \r\n" + missingLines);
        return new(true, null);
    }

    /// <summary>应收货款单（170101）AfterSave：对帐不超送/退货量 + 客户校验 + 单证存在性 + 金额汇总。</summary>
    private static async Task<SprocResult> CopAccountAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var sendErrors = await FindExceededAsync(connection, transaction, type, no, token,
            detailTable: "COP_ACCOUNT_D", detailTypeColumn: "ACCOUNT_TYPE", detailNoColumn: "ACCOUNT_NO",
            detailGroupTypeColumn: "S_R_TYPE", detailGroupNoColumn: "S_R_NO", detailGroupSerialColumn: "S_R_SERIAL_NO",
            sourceTable: "COP_SEND_D", sourceTypeColumn: "SEND_TYPE", sourceNoColumn: "SEND_NO");
        if (sendErrors is not null)
            return new(false, "以下对帐已超出送货单数量\r\n 送货单号  送货数量  已对帐数量  单据数量\r\n" + sendErrors);
        var returnErrors = await FindExceededAsync(connection, transaction, type, no, token,
            detailTable: "COP_ACCOUNT_D", detailTypeColumn: "ACCOUNT_TYPE", detailNoColumn: "ACCOUNT_NO",
            detailGroupTypeColumn: "S_R_TYPE", detailGroupNoColumn: "S_R_NO", detailGroupSerialColumn: "S_R_SERIAL_NO",
            sourceTable: "COP_RETURN_D", sourceTypeColumn: "RETURN_TYPE", sourceNoColumn: "RETURN_NO");
        if (returnErrors is not null)
            return new(false, "以下对帐已超出退货单数量\r\n 退货单号  退货数量  已对帐数量  单据数量\r\n" + returnErrors);

        var clientOk = await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.COP_ACCOUNT_M m JOIN dbo.CLIENT c ON c.CLIENT_ID=m.CLIENT_ID
            WHERE m.ACCOUNT_TYPE=@Type AND m.ACCOUNT_NO=@No AND c.BUSINESS_TAG=0;
            """, type, no, token);
        if (!clientOk) return new(false, "客户编号不存在或已停止交易。");
        // 送/退货单存在性（明细引用的送/退货行必须存在）
        var missingLines = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_ACCOUNT_D d
            WHERE ACCOUNT_TYPE=@Type AND ACCOUNT_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_SEND_D s
                              WHERE s.SEND_TYPE=d.S_R_TYPE AND s.SEND_NO=d.S_R_NO AND s.SERIAL_NO=d.S_R_SERIAL_NO)
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_RETURN_D s
                              WHERE s.RETURN_TYPE=d.S_R_TYPE AND s.RETURN_NO=d.S_R_NO AND s.SERIAL_NO=d.S_R_SERIAL_NO);
            """, type, no, token, line: r => r.GetInt32(0).ToString());
        if (missingLines is not null)
            return new(false, "以下序号项送/退货单不存在 \r\n" + missingLines);

        // 主表金额汇总（ROUND 2，SUM_AMOUNT=AMOUNT_TAX+OTHER_PRICE，QTY_TOTAL=SUM(QTY)）
        const string sql = """
            UPDATE m
            SET m.AMOUNT=d.AMOUNT, m.TAX_SUM=d.TAX_SUM, m.AMOUNT_TAX=d.AMOUNT_TAX,
                m.SUM_AMOUNT=d.AMOUNT_TAX+ISNULL(m.OTHER_PRICE,0), m.QTY_TOTAL=d.QTY_ALL
            FROM dbo.COP_ACCOUNT_M m
            INNER JOIN (
                SELECT ACCOUNT_TYPE, ACCOUNT_NO,
                       ROUND(SUM(AMOUNT_TAX),2) AS AMOUNT_TAX,
                       ROUND(SUM(AMOUNT),2) AS AMOUNT,
                       ROUND(SUM(TAX_SUM),2) AS TAX_SUM,
                       ROUND(SUM(QTY),2) AS QTY_ALL
                FROM dbo.COP_ACCOUNT_D
                WHERE ACCOUNT_TYPE=@Type AND ACCOUNT_NO=@No
                GROUP BY ACCOUNT_TYPE, ACCOUNT_NO
            ) d ON m.ACCOUNT_TYPE=d.ACCOUNT_TYPE AND m.ACCOUNT_NO=d.ACCOUNT_NO
            WHERE m.ACCOUNT_TYPE=@Type AND m.ACCOUNT_NO=@No;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await command.ExecuteNonQueryAsync(token);
        return new(true, null);
    }

    private static (string TypeColumn, string NoColumn) KeyColumns(IReadOnlyList<string> pkColumns)
        => (pkColumns[0], pkColumns[1]);

    /// <summary>收款单（170102）AfterSave：预收汇总 + 实收校验 + 对帐不超收。</summary>
    private static async Task<SprocResult> CopReceiptAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        // 预收冲抵汇总（与旧 SP 一致：AMOUNT_TAX 未提交时为 NULL，NULL 算术保持 NULL，
        // 负值校验不触发——旧系统语义，金额在批核/后续环节补全）
        await using var prepaySum = new SqlCommand($"""
            UPDATE m SET PREPAY_SUM=(SELECT SUM(PREPAY_AMOUNT) FROM dbo.COP_RECEIPT_PREPAY
                WHERE RECEIPT_TYPE=@Type AND RECEIPT_NO=@No),
                RECEIVE_SUM=AMOUNT_TAX-REBATE_SUM-(SELECT SUM(PREPAY_AMOUNT) FROM dbo.COP_RECEIPT_PREPAY
                WHERE RECEIPT_TYPE=@Type AND RECEIPT_NO=@No),
                LAST_UPDATE_DATE=GETDATE()
            FROM dbo.COP_RECEIPT_M m WHERE m.[{typeColumn}]=@Type AND m.[{noColumn}]=@No;
            """, connection, transaction);
        prepaySum.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        prepaySum.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await prepaySum.ExecuteNonQueryAsync(token);

        // 实收金额不能为负数
        var negative = await ExistsAsync(connection, transaction,
            $"SELECT TOP 1 1 FROM dbo.COP_RECEIPT_M WHERE [{typeColumn}]=@Type AND [{noColumn}]=@No AND RECEIVE_SUM<0;",
            type, no, token);
        if (negative) return new(false, "实收金额不能为负数");
        // 实收不能大于应收-折扣-预收冲帐
        var exceedMaster = await ExistsAsync(connection, transaction,
            $"SELECT TOP 1 1 FROM dbo.COP_RECEIPT_M WHERE [{typeColumn}]=@Type AND [{noColumn}]=@No AND RECEIVE_SUM>AMOUNT_TAX-REBATE_SUM-PREPAY_SUM+0.1;",
            type, no, token);
        if (exceedMaster) return new(false, "实收金额 不能大于 应收金额-现金折扣-预收冲帐");
        // 对帐单已收款不能大于应收款
        var accountErrors = await FindLinesAsync(connection, transaction,
            """
            SELECT od.ACCOUNT_NO, od.SUM_AMOUNT, od.RECEIVE_AMOUNT, sd.RECEIVE_AMOUNT
            FROM dbo.COP_ACCOUNT_M od
            INNER JOIN (SELECT ACCOUNT_TYPE, ACCOUNT_NO, SUM(RECEIVE_AMOUNT) RECEIVE_AMOUNT
                        FROM dbo.COP_RECEIPT_D WHERE RECEIPT_TYPE=@Type AND RECEIPT_NO=@No
                        GROUP BY ACCOUNT_TYPE, ACCOUNT_NO) sd
              ON od.ACCOUNT_TYPE=sd.ACCOUNT_TYPE AND od.ACCOUNT_NO=sd.ACCOUNT_NO
            WHERE od.RECEIVE_AMOUNT + sd.RECEIVE_AMOUNT > od.SUM_AMOUNT;
            """, type, no, token,
            line: r => $"{r.GetString(0).Trim()}       {Convert.ToDouble(r.GetValue(1))}          {Convert.ToDouble(r.GetValue(2))}          {Convert.ToDouble(r.GetValue(3))}");
        if (accountErrors is not null)
            return new(false, "以下会出现对帐单已收款大于应收款\r\n对帐单号     应收款       已收款       本次收款\r\n" + accountErrors);
        return new(true, null);
    }

    /// <summary>预收帐款单（170103）AfterSave：客户校验 + 金额汇总。</summary>
    private static async Task<SprocResult> CopPrepayAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var clientOk = await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.COP_PREPAY_M m JOIN dbo.CLIENT c ON c.CLIENT_ID=m.CLIENT_ID
            WHERE m.PREPAY_TYPE=@Type AND m.PREPAY_NO=@No AND c.BUSINESS_TAG=0;
            """, type, no, token);
        if (!clientOk) return new(false, "客户编号不存在或已停止交易。");
        await using var amount = new SqlCommand($"""
            UPDATE m SET AMOUNT=ROUND((SELECT SUM(AMOUNT) FROM dbo.COP_PREPAY_D
                WHERE PREPAY_TYPE=@Type AND PREPAY_NO=@No),3)
            FROM dbo.COP_PREPAY_M m WHERE m.[{typeColumn}]=@Type AND m.[{noColumn}]=@No;
            """, connection, transaction);
        amount.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        amount.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await amount.ExecuteNonQueryAsync(token);
        return new(true, null);
    }

    /// <summary>付款单（170202）AfterSave：预付汇总 + 实付校验 + 对帐不超付。</summary>
    private static async Task<SprocResult> PurPayAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
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

        var negative = await ExistsAsync(connection, transaction,
            $"SELECT TOP 1 1 FROM dbo.PUR_PAY_M WHERE [{typeColumn}]=@Type AND [{noColumn}]=@No AND PAYOUT_SUM<0;",
            type, no, token);
        if (negative) return new(false, "实付金额不能为负数");
        var exceedMaster = await ExistsAsync(connection, transaction,
            $"SELECT TOP 1 1 FROM dbo.PUR_PAY_M WHERE [{typeColumn}]=@Type AND [{noColumn}]=@No AND PAYOUT_SUM>AMOUNT_TAX-REBATE_SUM-PREPAY_SUM;",
            type, no, token);
        if (exceedMaster) return new(false, "实付金额 不能大于 应付金额-现金折扣-预付冲帐");
        var dueErrors = await FindLinesAsync(connection, transaction,
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
    private static async Task<SprocResult> PurPrepayAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var supplierOk = await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.PUR_PREPAY_M m JOIN dbo.SUPPLIER c ON c.SUPPLIER_ID=m.SUPPLIER_ID
            WHERE m.PREPAY_TYPE=@Type AND m.PREPAY_NO=@No AND c.BUSINESS_TAG=0;
            """, type, no, token);
        if (!supplierOk) return new(false, "厂商编号不存在或已停止交易。");
        // 预付金额不能超出采购金额
        var purchaseErrors = await FindLinesAsync(connection, transaction,
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

    private static async Task<bool> ExistsAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, string type, string no, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<string?> FindLinesAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, string type, string no, CancellationToken token,
        Func<SqlDataReader, string> line)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await using var reader = await command.ExecuteReaderAsync(token);
        var lines = new List<string>();
        while (await reader.ReadAsync(token)) lines.Add(line(reader));
        return lines.Count > 0 ? string.Join("\r\n", lines) : null;
    }
}
