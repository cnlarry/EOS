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
                "cop-send" => await CopSendAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-work" => await MocWorkAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-work-in" => await MocWorkInAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-callback" => await PurCallbackAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-produce-change" => await MocProduceChangeAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-purchase-change" => await PurPurchaseChangeAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "sam-out" => await SamOutAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-order-change" => await CopOrderChangeAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-callback" => await CopCallbackAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-produce" => await MocProduceAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "hr-worktime" => await HrWorktimeAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-product-out" => await MocProductOutAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-check-stock" => await InvCheckStockAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-quote" => await PurQuoteAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-receive" => await PurReceiveAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-apply" => await PurApplyAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-purchase" => await PurPurchaseAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
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

    /// <summary>库存盘点单（130101）AfterSave：库别/产品存在 + 盘点数不小于 0。</summary>
    private static async Task<SprocResult> InvCheckStockAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var depotMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.INV_CHECK_STOCK_D d
            WHERE CHECK_STOCK_TYPE=@Type AND CHECK_STOCK_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=d.DEPOT_ID);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (depotMissing is not null) return new(false, "以下序号项库别编号不存在 \r\n" + depotMissing);
        var productMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.INV_CHECK_STOCK_D d
            WHERE CHECK_STOCK_TYPE=@Type AND CHECK_STOCK_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (productMissing is not null) return new(false, "以下序号项产品编号不存在 \r\n" + productMissing);
        var negative = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.INV_CHECK_STOCK_D d
            WHERE CHECK_STOCK_TYPE=@Type AND CHECK_STOCK_NO=@No AND CHECK_QTY<0;
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (negative is not null) return new(false, "以下序号项盘点数小于0 \r\n" + negative);
        return new(true, null);
    }

    /// <summary>送货回执（1413）AfterSave：送/退货已有回执校验。</summary>
    private static async Task<SprocResult> CopCallbackAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var lines = await FindLinesAsync(connection, transaction,
            """
            SELECT c.SERIAL_NO FROM dbo.COP_SEND_D s
            INNER JOIN dbo.COP_CALLBACK_D c
              ON s.SEND_TYPE=c.S_R_TYPE AND s.SEND_NO=c.S_R_NO AND s.SERIAL_NO=c.S_R_SERIAL_NO
            WHERE c.CALLBACK_TYPE=@Type AND c.CALLBACK_NO=@No AND ISNULL(s.CALLBACK_NO,'')<>''
            UNION ALL
            SELECT c.SERIAL_NO FROM dbo.COP_RETURN_D s
            INNER JOIN dbo.COP_CALLBACK_D c
              ON s.RETURN_TYPE=c.S_R_TYPE AND s.RETURN_NO=c.S_R_NO AND s.SERIAL_NO=c.S_R_SERIAL_NO
            WHERE c.CALLBACK_TYPE=@Type AND c.CALLBACK_NO=@No AND ISNULL(s.CALLBACK_NO,'')<>'';
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString() + "    ");
        return lines is null
            ? new(true, null)
            : new(false, "以下序号项送、退货已有回执\r\n" + lines);
    }

    /// <summary>制令单（1522）AfterSave：生产数量不超订单/计划 + 订单/产品校验 + 明细订单号回填。</summary>
    private static async Task<SprocResult> MocProduceAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        if (await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.COP_ORDER_D od
            INNER JOIN dbo.MOC_PRODUCE_M pr
              ON pr.ORDER_TYPE=od.ORDER_TYPE AND pr.ORDER_NO=od.ORDER_NO AND pr.ORDER_SERIAL_NO=od.SERIAL_NO
            WHERE pr.PRODUCE_TYPE=@Type AND pr.PRODUCE_NO=@No
              AND (od.PLAN_QTY < ISNULL(od.FINISHED_PLAN_QTY,0)+ISNULL(pr.QTY,0)
                OR od.PLAN_SPARE_QTY < ISNULL(od.FINISHED_PLAN_SPARE_QTY,0)+ISNULL(pr.SPARE_QTY,0));
            """, type, no, token))
            return new(false, "生产数量或备品生产数量超出订单数量");
        if (await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.MOC_PLAN_MOC od
            INNER JOIN dbo.MOC_PRODUCE_M pr
              ON pr.PLAN_TYPE=od.PLAN_TYPE AND pr.PLAN_NO=od.PLAN_NO AND pr.PLAN_SERIAL_NO=od.SERIAL_NO
            WHERE pr.PRODUCE_TYPE=@Type AND pr.PRODUCE_NO=@No
              AND od.REQUIRE_QTY < ISNULL(od.PRODUCE_QTY,0)+ISNULL(pr.QTY,0);
            """, type, no, token))
            return new(false, "生产数量超出生产计划数量");
        // 订单存在性（ORDER_NO 非空时）
        var orderMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT m.ORDER_SERIAL_NO FROM dbo.MOC_PRODUCE_M m
            WHERE m.PRODUCE_TYPE=@Type AND m.PRODUCE_NO=@No AND ISNULL(m.ORDER_NO,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_D d
                              WHERE d.ORDER_TYPE=m.ORDER_TYPE AND d.ORDER_NO=m.ORDER_NO AND d.SERIAL_NO=m.ORDER_SERIAL_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (orderMissing is not null) return new(false, "订单不存在  \r\n" + orderMissing);
        var productMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.MOC_PRODUCE_D d
            WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (productMissing is not null) return new(false, "以下序号项产品编号不存在 \r\n" + productMissing);
        // 明细订单号回填
        await using var syncOrder = new SqlCommand("""
            UPDATE d SET d.ORDER_TYPE=m.ORDER_TYPE, d.ORDER_NO=m.ORDER_NO, d.ORDER_SERIAL_NO=m.ORDER_SERIAL_NO
            FROM dbo.MOC_PRODUCE_D d INNER JOIN dbo.MOC_PRODUCE_M m
              ON m.PRODUCE_TYPE=d.PRODUCE_TYPE AND m.PRODUCE_NO=d.PRODUCE_NO
            WHERE d.PRODUCE_TYPE=@Type AND d.PRODUCE_NO=@No;
            """, connection, transaction);
        syncOrder.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        syncOrder.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await syncOrder.ExecuteNonQueryAsync(token);
        return new(true, null);
    }

    /// <summary>工时录入（180207）AfterSave：HR_SETUP.REQUIRE_ENACTMENT=1 时校验加班不超申请（当前环境=0，跳过）。</summary>
    private static async Task<SprocResult> HrWorktimeAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var requireEnactment = await ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.HR_SETUP WHERE REQUIRE_ENACTMENT=1;", keyValues[0], keyValues[1], token);
        if (!requireEnactment) return new(true, null);
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var lines = await FindLinesAsync(connection, transaction,
            """
            SELECT a.EMP_ID, a.OVERTIME, a.REST_OVERTIME, a.HOLIDAY_OVERTIME, w.OVERTIME, w.REST_OVERTIME, w.HOLIDAY_OVERTIME
            FROM (SELECT m.EMP_ID, SUM(m.OVERTIME) OVERTIME, SUM(m.REST_OVERTIME) REST_OVERTIME, SUM(m.HOLIDAY_OVERTIME) HOLIDAY_OVERTIME
                  FROM dbo.HR_WORKTIME_M m INNER JOIN dbo.HR_WORKTIME_D d
                    ON d.WORKTIME_TYPE=m.WORKTIME_TYPE AND d.WORKTIME_NO=m.WORKTIME_NO
                  WHERE m.WORKTIME_TYPE=@Type AND m.WORKTIME_NO=@No
                  GROUP BY m.EMP_ID) w
            LEFT JOIN (SELECT d.EMP_ID, SUM(d.OVERTIME) OVERTIME, SUM(d.REST_OVERTIME) REST_OVERTIME, SUM(d.HOLIDAY_OVERTIME) HOLIDAY_OVERTIME
                       FROM dbo.HR_APPLY_M m INNER JOIN dbo.HR_APPLY_D d
                         ON d.APPLY_TYPE=m.APPLY_TYPE AND d.APPLY_NO=m.APPLY_NO
                       WHERE m.COUNT_DATE=(SELECT TOP 1 COUNT_DATE FROM dbo.HR_WORKTIME_M WHERE WORKTIME_TYPE=@Type AND WORKTIME_NO=@No)
                       GROUP BY d.EMP_ID) a ON a.EMP_ID=w.EMP_ID
            WHERE w.OVERTIME > ISNULL(a.OVERTIME,0) OR w.REST_OVERTIME > ISNULL(a.REST_OVERTIME,0)
               OR w.HOLIDAY_OVERTIME > ISNULL(a.HOLIDAY_OVERTIME,0);
            """, type, no, token,
            line: r => $"{r.GetString(0).Trim()}  {Convert.ToDouble(r.GetValue(1))}  {Convert.ToDouble(r.GetValue(2))}  {Convert.ToDouble(r.GetValue(3))}  已录入   {Convert.ToDouble(r.GetValue(4))}  {Convert.ToDouble(r.GetValue(5))}  {Convert.ToDouble(r.GetValue(6))}");
        return lines is null
            ? new(true, null)
            : new(false, "以下人员时间超出:\r\n工号---加班时--休息日加班时--节假日加班时\r\n" + lines);
    }

    /// <summary>返工单（1515）AfterSave：出库不超制令可出库 + 制令/库别/产品/批号校验。</summary>
    private static async Task<SprocResult> MocProductOutAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var fitoutTag = await ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.SYSSS WHERE FITOUT_TAG=1;", keyValues[0], keyValues[1], token);
        var exceeded = await FindLinesAsync(connection, transaction,
            fitoutTag
                ? """
                  SELECT t.PRODUCE_NO FROM (SELECT PRODUCE_TYPE, PRODUCE_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                                            FROM dbo.MOC_PRODUCT_OUT_D WHERE PRODUCT_OUT_TYPE=@Type AND PRODUCT_OUT_NO=@No
                                            GROUP BY PRODUCE_TYPE, PRODUCE_NO) t
                  INNER JOIN dbo.MOC_PRODUCE_M m ON m.PRODUCE_TYPE=t.PRODUCE_TYPE AND m.PRODUCE_NO=t.PRODUCE_NO
                  WHERE t.QTY+ISNULL(m.FINISHED_FITOUT_QTY,0) > ISNULL(m.FINISHED_QTY,0)
                     OR t.SPARE_QTY+ISNULL(m.FINISHED_FITOUT_SPARE_QTY,0) > ISNULL(m.FINISHED_SPARE_QTY,0);
                  """
                : """
                  SELECT t.PRODUCE_NO FROM (SELECT PRODUCE_TYPE, PRODUCE_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                                            FROM dbo.MOC_PRODUCT_OUT_D WHERE PRODUCT_OUT_TYPE=@Type AND PRODUCT_OUT_NO=@No
                                            GROUP BY PRODUCE_TYPE, PRODUCE_NO) t
                  INNER JOIN dbo.MOC_PRODUCE_M m ON m.PRODUCE_TYPE=t.PRODUCE_TYPE AND m.PRODUCE_NO=t.PRODUCE_NO
                  WHERE t.QTY+ISNULL(m.FINISHED_SEND_QTY,0) > ISNULL(m.FINISHED_QTY,0)
                     OR t.SPARE_QTY+ISNULL(m.FINISHED_SEND_SPARE_QTY,0) > ISNULL(m.FINISHED_SPARE_QTY,0);
                  """,
            type, no, token, line: r => r.GetString(0).Trim() + "  ");
        if (exceeded is not null)
            return new(false, "以下生产单出库数量超出制令可出库 \r\n" + exceeded);
        var produceMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.MOC_PRODUCT_OUT_D d
            WHERE PRODUCT_OUT_TYPE=@Type AND PRODUCT_OUT_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_M c
                              WHERE c.PRODUCE_TYPE=d.PRODUCE_TYPE AND c.PRODUCE_NO=d.PRODUCE_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (produceMissing is not null) return new(false, "以下序号项制令单不存在 \r\n" + produceMissing);
        var depotMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.MOC_PRODUCT_OUT_D d
            WHERE PRODUCT_OUT_TYPE=@Type AND PRODUCT_OUT_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=d.DEPOT_ID);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (depotMissing is not null) return new(false, "以下序号项库别编号不存在 \r\n" + depotMissing);
        var productMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.MOC_PRODUCT_OUT_D d
            WHERE PRODUCT_OUT_TYPE=@Type AND PRODUCT_OUT_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (productMissing is not null) return new(false, "以下序号项产品编号不存在 \r\n" + productMissing);
        var batchMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.MOC_PRODUCT_OUT_D d
            WHERE PRODUCT_OUT_TYPE=@Type AND PRODUCT_OUT_NO=@No AND ISNULL(BATCH_NO,'')=''
              AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO AND p.MANAGE_BATCH=1);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (batchMissing is not null) return new(false, "以下序号项需要输入批号 \r\n" + batchMissing);
        return new(true, null);
    }

    /// <summary>工序工单（2705）AfterSave：工序工单不超制程数量。</summary>
    private static async Task<SprocResult> MocWorkAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var lines = await FindLinesAsync(connection, transaction,
            """
            SELECT od.PRODUCE_TYPE, od.PRODUCE_NO, od.PROCESS_QTY, od.FINISHED_PLAN_QTY, sd.PROCESS_QTY
            FROM dbo.MOC_PRODUCE_PROCESS_D od
            INNER JOIN (SELECT PRODUCE_TYPE, PRODUCE_NO, PRODUCE_SERIAL_NO, SUM(PROCESS_QTY) PROCESS_QTY
                        FROM dbo.MOC_WORK_D WHERE WORK_TYPE=@Type AND WORK_NO=@No
                        GROUP BY PRODUCE_TYPE, PRODUCE_NO, PRODUCE_SERIAL_NO) sd
              ON od.PRODUCE_TYPE=sd.PRODUCE_TYPE AND od.PRODUCE_NO=sd.PRODUCE_NO AND od.SERIAL_NO=sd.PRODUCE_SERIAL_NO
            WHERE od.FINISHED_PLAN_QTY+sd.PROCESS_QTY > od.PROCESS_QTY;
            """, type, no, token,
            line: r => $"{r.GetString(0).Trim()}    {r.GetString(1).Trim()}    {Convert.ToDouble(r.GetValue(2))}    {Convert.ToDouble(r.GetValue(3))}    {Convert.ToDouble(r.GetValue(4))}");
        return lines is null
            ? new(true, null)
            : new(false, "以下工序工单超出工单制程数量\r\n工单单别   单号   数量   已下工序工单数量   单据数量\r\n" + lines);
    }

    /// <summary>工序完工单（2706）AfterSave：完工不超工序工单数量。</summary>
    private static async Task<SprocResult> MocWorkInAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var lines = await FindLinesAsync(connection, transaction,
            """
            SELECT od.WORK_TYPE, od.WORK_NO, od.PROCESS_QTY+ISNULL(od.ULLAGE_QTY,0), ISNULL(od.FINISHED_IN_QTY,0), sd.QTY
            FROM dbo.MOC_WORK_D od
            INNER JOIN (SELECT WORK_TYPE, WORK_NO, WORK_SERIAL_NO, SUM(QTY) QTY
                        FROM dbo.MOC_WORK_IN_D WHERE WORK_IN_TYPE=@Type AND WORK_IN_NO=@No
                        GROUP BY WORK_TYPE, WORK_NO, WORK_SERIAL_NO) sd
              ON od.WORK_TYPE=sd.WORK_TYPE AND od.WORK_NO=sd.WORK_NO AND od.SERIAL_NO=sd.WORK_SERIAL_NO
            WHERE od.FINISHED_IN_QTY+sd.QTY > od.PROCESS_QTY+ISNULL(od.ULLAGE_QTY,0);
            """, type, no, token,
            line: r => $"{r.GetString(0).Trim()}    {r.GetString(1).Trim()}    {Convert.ToDouble(r.GetValue(2))}    {Convert.ToDouble(r.GetValue(3))}    {Convert.ToDouble(r.GetValue(4))}");
        return lines is null
            ? new(true, null)
            : new(false, "以下入库超出工序工单数量\r\n工序工单单别   单号   数量   已入库数量   单据数量\r\n" + lines);
    }

    /// <summary>收料核价单（1610）AfterSave：厂商校验。</summary>
    private static async Task<SprocResult> PurCallbackAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var ok = await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.PUR_CALLBACK_M m JOIN dbo.SUPPLIER c ON c.SUPPLIER_ID=m.SUPPLIER_ID
            WHERE m.CALLBACK_TYPE=@Type AND m.CALLBACK_NO=@No AND c.BUSINESS_TAG=0;
            """, type, no, token);
        return ok ? new(true, null) : new(false, "厂商编号不存在或已停止交易。");
    }

    /// <summary>制造命令单变更（1509）AfterSave：原单已批核 + 变更量不小于已生产/已领料。</summary>
    private static async Task<SprocResult> MocProduceChangeAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        if (await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.MOC_PRODUCE_M m
            INNER JOIN dbo.MOC_PRODUCE_CHANGE_M c ON c.PRODUCE_TYPE=m.PRODUCE_TYPE AND c.PRODUCE_NO=m.PRODUCE_NO
            WHERE c.CHANGE_PRODUCE_TYPE=@Type AND c.CHANGE_PRODUCE_NO=@No AND m.CONFIRM_TAG=0;
            """, type, no, token))
            return new(false, "生产单未批核，不可变更");
        if (await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.MOC_PRODUCE_M m
            INNER JOIN dbo.MOC_PRODUCE_CHANGE_M c ON c.PRODUCE_TYPE=m.PRODUCE_TYPE AND c.PRODUCE_NO=m.PRODUCE_NO
            WHERE c.CHANGE_PRODUCE_TYPE=@Type AND c.CHANGE_PRODUCE_NO=@No
              AND (ISNULL(m.FINISHED_QTY,0)>ISNULL(c.QTY,0) OR ISNULL(m.FINISHED_SPARE_QTY,0)>ISNULL(c.SPARE_QTY,0));
            """, type, no, token))
            return new(false, "变更后以下序号项数量小于已生产数量");
        var lines = await FindLinesAsync(connection, transaction,
            """
            SELECT oc.SERIAL_NO FROM dbo.MOC_PRODUCE_D od
            INNER JOIN dbo.MOC_PRODUCE_CHANGE_D oc
              ON oc.PRODUCE_TYPE=od.PRODUCE_TYPE AND oc.PRODUCE_NO=od.PRODUCE_NO AND oc.PRODUCE_SERIAL_NO=od.SERIAL_NO
            WHERE oc.CHANGE_PRODUCE_TYPE=@Type AND oc.CHANGE_PRODUCE_NO=@No AND oc.NEED_QTY < ISNULL(od.USED_QTY,0);
            """, type, no, token, line: r => "    " + Convert.ToInt32(r.GetValue(0)).ToString());
        return lines is null
            ? new(true, null)
            : new(false, "变更后以下序号项应领料数量小于制令已领料\r\n" + lines);
    }

    /// <summary>采购单变更（1609）AfterSave：原单已批核 + 变更量不小于已收货。</summary>
    private static async Task<SprocResult> PurPurchaseChangeAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        if (await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.PUR_PURCHASE_M m
            INNER JOIN dbo.PUR_PURCHASE_CHANGE_M c ON c.PURCHASE_TYPE=m.PURCHASE_TYPE AND c.PURCHASE_NO=m.PURCHASE_NO
            WHERE c.CHANGE_PURCHASE_TYPE=@Type AND c.CHANGE_PURCHASE_NO=@No AND m.CONFIRM_TAG=0;
            """, type, no, token))
            return new(false, "采购单未批核，不可变更");
        var lines = await FindLinesAsync(connection, transaction,
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
    private static async Task<SprocResult> SamOutAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var lines = await FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO FROM dbo.SAM_OUT_D d
            INNER JOIN dbo.SAMPLE_PRO p ON p.PRO_NO=d.PRO_NO
            WHERE d.OUT_TYPE=@Type AND d.OUT_NO=@No AND d.QTY > p.QTY;
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString() + "    ");
        return lines is null
            ? new(true, null)
            : new(false, "以下序号项样品库存不足 \r\n" + lines);
    }

    /// <summary>客户订单变更（1418）AfterSave：原单已批核 + 变更量校验 + 订单号唯一。</summary>
    private static async Task<SprocResult> CopOrderChangeAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        if (await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.COP_ORDER_M m
            INNER JOIN dbo.COP_ORDER_CHANGE_M c ON c.ORDER_TYPE=m.ORDER_TYPE AND c.ORDER_NO=m.ORDER_NO
            WHERE c.CHANGE_ORDER_TYPE=@Type AND c.CHANGE_ORDER_NO=@No AND m.CONFIRM_TAG=0;
            """, type, no, token))
            return new(false, "订单未批核，不可变更");
        var lines = await FindLinesAsync(connection, transaction,
            """
            SELECT oc.SERIAL_NO FROM dbo.COP_ORDER_D od
            INNER JOIN dbo.COP_ORDER_CHANGE_D oc
              ON oc.ORDER_TYPE=od.ORDER_TYPE AND oc.ORDER_NO=od.ORDER_NO AND oc.ORDER_SERIAL_NO=od.SERIAL_NO
            WHERE oc.CHANGE_ORDER_TYPE=@Type AND oc.CHANGE_ORDER_NO=@No
              AND (oc.QTY < ISNULL(od.FINISHED_SEND_QTY,0) OR oc.SPARE_QTY < ISNULL(od.FINISHED_SPARE_QTY,0)
                OR oc.QTY < ISNULL(od.FINISHED_PRODUCE_QTY,0) OR oc.SPARE_QTY < ISNULL(od.FINISHED_PRODUCE_SPARE_QTY,0));
            """, type, no, token, line: r => "    " + Convert.ToInt32(r.GetValue(0)).ToString());
        if (lines is not null)
            return new(false, "变更后以下序号项订单数量小于已完工或已送货数量\r\n" + lines);
        var planLines = await FindLinesAsync(connection, transaction,
            """
            SELECT oc.SERIAL_NO FROM dbo.COP_ORDER_D od
            INNER JOIN dbo.COP_ORDER_CHANGE_D oc
              ON oc.ORDER_TYPE=od.ORDER_TYPE AND oc.ORDER_NO=od.ORDER_NO AND oc.ORDER_SERIAL_NO=od.SERIAL_NO
            WHERE oc.CHANGE_ORDER_TYPE=@Type AND oc.CHANGE_ORDER_NO=@No AND oc.PLAN_QTY < ISNULL(od.FINISHED_PRODUCE_QTY,0);
            """, type, no, token, line: r => "    " + Convert.ToInt32(r.GetValue(0)).ToString());
        if (planLines is not null)
            return new(false, "变更后以下序号项计划生产数量小于已下生产单数量\r\n" + planLines);
        // 客户订单号不重复
        if (await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.COP_ORDER_M
            WHERE CLIENT_ORDER_NO=(SELECT CLIENT_ORDER_NO FROM dbo.COP_ORDER_CHANGE_M
                                   WHERE CHANGE_ORDER_TYPE=@Type AND CHANGE_ORDER_NO=@No)
              AND ISNULL(CLIENT_ORDER_NO,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_CHANGE_M c
                              WHERE c.ORDER_TYPE=COP_ORDER_M.ORDER_TYPE AND c.ORDER_NO=COP_ORDER_M.ORDER_NO
                                AND c.CHANGE_ORDER_TYPE=@Type AND c.CHANGE_ORDER_NO=@No);
            """, type, no, token))
            return new(false, "客户订单号重复。");
        return new(true, null);
    }

    /// <summary>
    /// 送货单（1406）AfterSave：排程/订单量校验（SYSSS 标志门控）+ 库存可用校验 +
    /// 订单一致性 + 批号 + 30 天日期 + mo_no 标记。
    /// </summary>
    private static async Task<SprocResult> CopSendAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var flags = await ReadSysssFlagsAsync(connection, transaction, token);

        // P_COP_SEND_CHECK：排程量校验（无条件）
        var shipmentLines = await FindLinesAsync(connection, transaction,
            """
            SELECT od.SHIPMENT_NO, od.QTY, od.FINISHED_QTY, sd.QTY
            FROM dbo.COP_SHIPMENT_D od
            INNER JOIN (SELECT SHIPMENT_TYPE, SHIPMENT_NO, SHIPMENT_SERIAL_NO, SUM(QTY) QTY
                        FROM dbo.COP_SEND_D WHERE SEND_TYPE=@Type AND SEND_NO=@No
                        GROUP BY SHIPMENT_TYPE, SHIPMENT_NO, SHIPMENT_SERIAL_NO) sd
              ON od.SHIPMENT_TYPE=sd.SHIPMENT_TYPE AND od.SHIPMENT_NO=sd.SHIPMENT_NO
             AND od.SERIAL_NO=sd.SHIPMENT_SERIAL_NO
            WHERE od.FINISHED_QTY+sd.QTY > od.QTY;
            """, type, no, token,
            line: r => $"{r.GetString(0).Trim()}    {Convert.ToDouble(r.GetValue(1))}    {Convert.ToDouble(r.GetValue(2))}    {Convert.ToDouble(r.GetValue(3))}");
        if (shipmentLines is not null)
            return new(false, "以下会出现已送货数量超出排程数量\r\n排程单号   数量  已送数量  单据数量\r\n" + shipmentLines);
        // 订单量校验（SEND_ORDER_TAG=1）
        if (flags.GetValueOrDefault("SEND_ORDER_TAG") == 1)
        {
            var orderLines = await FindLinesAsync(connection, transaction,
                """
                SELECT od.ORDER_NO, od.QTY, od.SPARE_QTY, od.FINISHED_SEND_QTY, od.FINISHED_SPARE_QTY, sd.QTY, sd.SPARE_QTY
                FROM dbo.COP_ORDER_D od
                INNER JOIN (SELECT ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                            FROM dbo.COP_SEND_D WHERE SEND_TYPE=@Type AND SEND_NO=@No
                            GROUP BY ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO) sd
                  ON od.ORDER_TYPE=sd.ORDER_TYPE AND od.ORDER_NO=sd.ORDER_NO AND od.SERIAL_NO=sd.ORDER_SERIAL_NO
                WHERE od.FINISHED_SEND_QTY+ISNULL(od.BACK_MATERIAL,0)+ISNULL(od.BACK_BAD,0)+sd.QTY > od.QTY
                   OR od.FINISHED_SPARE_QTY+sd.SPARE_QTY > ISNULL(od.SPARE_QTY,0);
                """, type, no, token,
                line: r => $"{r.GetString(0).Trim()}    {Convert.ToDouble(r.GetValue(1))}    {Convert.ToDouble(r.GetValue(3))}    {Convert.ToDouble(r.GetValue(5))}    {Convert.ToDouble(r.GetValue(2))}    {Convert.ToDouble(r.GetValue(4))}    {Convert.ToDouble(r.GetValue(6))}");
            if (orderLines is not null)
                return new(false, "以下会出现订单已送货数量超出订单数量\r\n订单单号   数量  已送数量  单据数量  备品  已送备品  单据备品\r\n" + orderLines);
        }

        // 订单一致性：客户相符 / 订单存在 / 序号产品相符 / 产品存在 / 批号
        var clientMismatch = await FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO FROM dbo.COP_ORDER_M o
            INNER JOIN dbo.COP_SEND_D d ON o.ORDER_TYPE=d.ORDER_TYPE AND o.ORDER_NO=d.ORDER_NO
            INNER JOIN dbo.COP_SEND_M m ON m.SEND_TYPE=d.SEND_TYPE AND m.SEND_NO=d.SEND_NO
            WHERE m.SEND_TYPE=@Type AND m.SEND_NO=@No AND o.CLIENT_ID<>m.CLIENT_ID;
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (clientMismatch is not null)
            return new(false, "以下序号项送货单与订单客户不符 \r\n" + clientMismatch);
        var orderMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_SEND_D d
            WHERE SEND_TYPE=@Type AND SEND_NO=@No AND ISNULL(ORDER_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_M o WHERE o.ORDER_TYPE=d.ORDER_TYPE AND o.ORDER_NO=d.ORDER_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (orderMissing is not null)
            return new(false, "以下序号项订单不存在 \r\n" + orderMissing);
        var orderProduct = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_SEND_D d
            WHERE SEND_TYPE=@Type AND SEND_NO=@No AND ISNULL(ORDER_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_D o
                              WHERE o.ORDER_TYPE=d.ORDER_TYPE AND o.ORDER_NO=d.ORDER_NO
                                AND o.SERIAL_NO=d.ORDER_SERIAL_NO AND o.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (orderProduct is not null)
            return new(false, "以下序号项订单序号与产品编号不相符 \r\n" + orderProduct);
        var productMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_SEND_D d
            WHERE SEND_TYPE=@Type AND SEND_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (productMissing is not null)
            return new(false, "以下序号项产品编号不存在 \r\n" + productMissing);
        var batchMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_SEND_D d
            WHERE SEND_TYPE=@Type AND SEND_NO=@No AND ISNULL(BATCH_NO,'')=''
              AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO AND p.MANAGE_BATCH=1);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (batchMissing is not null)
            return new(false, "以下序号项需要输入批号 \r\n" + batchMissing);
        // 送货日期不能小于建立日期 30 天
        var dateTooOld = await ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.COP_SEND_M WHERE SEND_TYPE=@Type AND SEND_NO=@No AND DATEDIFF(day, SEND_DATE, CREATE_DATE)>30;",
            type, no, token);
        if (dateTooOld) return new(false, "送货日期不能小于建立日期30天");

        // 库存可用校验（SEND_TAG=1）：库别存在 + 库存数量 + 批号库存
        if (flags.GetValueOrDefault("SEND_TAG") == 1)
        {
            var depotMissing = await FindLinesAsync(connection, transaction,
                """
                SELECT SERIAL_NO, DEPOT_ID FROM dbo.COP_SEND_D d
                WHERE SEND_TYPE=@Type AND SEND_NO=@No AND ISNULL(DEPOT_ID,'')<>''
                  AND NOT EXISTS (SELECT 1 FROM dbo.DEPOT dp WHERE dp.DEPOT_ID=d.DEPOT_ID);
                """, type, no, token,
                line: r => $"{r.GetInt32(0)}    {r.GetString(1).Trim()}");
            if (depotMissing is not null)
                return new(false, "以下库别不存在\r\n序号----库别\r\n" + depotMissing);
            var stockLines = await FindLinesAsync(connection, transaction,
                """
                SELECT a.PRO_NO, a.DEPOT_ID, a.QTY, ISNULL(b.QTY,0)
                FROM (SELECT d.PRO_NO, d.DEPOT_ID,
                             SUM((d.QTY+ISNULL(d.SPARE_QTY,0)) *
                                 CASE p.UNIT_ID WHEN d.UNIT_ID THEN 1
                                      WHEN p.UNIT_ID_1 THEN ISNULL(p.UNIT_RATE_1,0)
                                      WHEN p.UNIT_ID_2 THEN ISNULL(p.UNIT_RATE_2,0)
                                      WHEN p.UNIT_ID_3 THEN ISNULL(p.UNIT_RATE_3,0)
                                      WHEN p.UNIT_ID_4 THEN ISNULL(p.UNIT_RATE_4,0) ELSE 0 END) AS QTY
                      FROM dbo.COP_SEND_D d
                      INNER JOIN dbo.PRODUCT p ON p.PRO_NO=d.PRO_NO
                      WHERE d.SEND_TYPE=@Type AND d.SEND_NO=@No
                      GROUP BY d.PRO_NO, d.DEPOT_ID) a
                LEFT JOIN dbo.INV_PRO_DEPOT b ON b.PRO_NO=a.PRO_NO AND b.DEPOT_ID=a.DEPOT_ID
                WHERE a.QTY > ISNULL(b.QTY,0);
                """, type, no, token,
                line: r => $"{r.GetString(0).Trim()}    {r.GetString(1).Trim()}    {Convert.ToDouble(r.GetValue(2))}    {Convert.ToDouble(r.GetValue(3))}    {Convert.ToDouble(r.GetValue(2))-Convert.ToDouble(r.GetValue(3))}");
            if (stockLines is not null)
                return new(false, "库存数量不足\r\n料号---------------库别----出库数量----库存数量---不足数量\r\n" + stockLines);
            var batchStock = await FindLinesAsync(connection, transaction,
                """
                SELECT a.PRO_NO, a.BATCH_NO, a.QTY-(ISNULL(b.IN_SUM,0)-ISNULL(b.OUT_SUM,0))
                FROM (SELECT d.PRO_NO, d.BATCH_NO,
                             (d.QTY+ISNULL(d.SPARE_QTY,0)) *
                                 CASE p.UNIT_ID WHEN d.UNIT_ID THEN 1
                                      WHEN p.UNIT_ID_1 THEN ISNULL(p.UNIT_RATE_1,0)
                                      WHEN p.UNIT_ID_2 THEN ISNULL(p.UNIT_RATE_2,0)
                                      WHEN p.UNIT_ID_3 THEN ISNULL(p.UNIT_RATE_3,0)
                                      WHEN p.UNIT_ID_4 THEN ISNULL(p.UNIT_RATE_4,0) ELSE 0 END AS QTY
                      FROM dbo.COP_SEND_D d
                      INNER JOIN dbo.PRODUCT p ON p.PRO_NO=d.PRO_NO
                      WHERE d.SEND_TYPE=@Type AND d.SEND_NO=@No AND ISNULL(d.BATCH_NO,'')<>'') a
                LEFT JOIN dbo.INV_BATCH_M b ON b.BATCH_NO=a.BATCH_NO AND b.PRO_NO=a.PRO_NO
                WHERE a.QTY > (ISNULL(b.IN_SUM,0)-ISNULL(b.OUT_SUM,0));
                """, type, no, token,
                line: r => $"{r.GetString(0).Trim()}    {r.GetString(1).Trim()}    {Convert.ToDouble(r.GetValue(2))}");
            if (batchStock is not null)
                return new(false, "批号库存数量不足\r\n" + batchStock);
        }

        // mo_no 标记：每个品号取最大 CLIENT_ORDER_NO 行标记 showbaozhuang
        await using (var moUpdate = new SqlCommand("""
            UPDATE d SET d.mo_no='showbaozhuang'
            FROM dbo.COP_SEND_D d
            INNER JOIN (SELECT PRO_NO, MAX(CLIENT_ORDER_NO) MAX_CLIENT_ORDER_NO FROM dbo.COP_SEND_D
                        WHERE SEND_TYPE=@Type AND SEND_NO=@No GROUP BY PRO_NO) max_d
              ON d.PRO_NO=max_d.PRO_NO AND d.CLIENT_ORDER_NO=max_d.MAX_CLIENT_ORDER_NO
            WHERE d.SEND_TYPE=@Type AND d.SEND_NO=@No;
            """, connection, transaction))
        {
            moUpdate.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            moUpdate.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await moUpdate.ExecuteNonQueryAsync(token);
        }
        return new(true, null);
    }

    private static async Task<Dictionary<string, int>> ReadSysssFlagsAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand("""
            SELECT TOP 1 ISNULL(CAST(SEND_TAG AS int),0), ISNULL(CAST(SEND_ORDER_TAG AS int),0)
            FROM dbo.SYSSS;
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token))
        {
            result["SEND_TAG"] = reader.GetInt32(0);
            result["SEND_ORDER_TAG"] = reader.GetInt32(1);
        }
        return result;
    }

    /// <summary>
    /// 采购单（1606）AfterSave：厂商/计价有效期/预交日期/申购单/产品校验 +
    /// PUR_PURCHASE_MORE 同步（补明细、单价回填、金额重算 I/O/N、数量分配、单号汇总）。
    /// </summary>
    private static async Task<SprocResult> PurPurchaseAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var supplierOk = await ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.PUR_PURCHASE_M m JOIN dbo.SUPPLIER c ON c.SUPPLIER_ID=m.SUPPLIER_ID
            WHERE m.PURCHASE_TYPE=@Type AND m.PURCHASE_NO=@No AND c.BUSINESS_TAG=0;
            """, type, no, token);
        if (!supplierOk) return new(false, "厂商编号不存在或已停止交易。");
        // 产品计价有效期
        var priceExpired = await FindLinesAsync(connection, transaction,
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
        var deliveryLines = await FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO FROM dbo.PUR_PURCHASE_D d
            INNER JOIN dbo.PUR_PURCHASE_M m ON m.PURCHASE_TYPE=d.PURCHASE_TYPE AND m.PURCHASE_NO=d.PURCHASE_NO
            WHERE d.PURCHASE_TYPE=@Type AND d.PURCHASE_NO=@No AND d.PLAN_DELIVERY_DATE < m.PURCHASE_DATE;
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (deliveryLines is not null)
            return new(false, "以下序号项预交日期小于采购单日期 \r\n" + deliveryLines);
        // 申购单存在
        var applyMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_PURCHASE_D d
            WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No AND ISNULL(APPLY_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.PUR_APPLY_M q WHERE q.APPLY_TYPE=d.APPLY_TYPE AND q.APPLY_NO=d.APPLY_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (applyMissing is not null)
            return new(false, "以下序号项申购单不存在 \r\n" + applyMissing);
        // 申购单序号与产品编号相符
        var applyProduct = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_PURCHASE_D d
            WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No AND ISNULL(APPLY_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.PUR_APPLY_D q
                              WHERE q.APPLY_TYPE=d.APPLY_TYPE AND q.APPLY_NO=d.APPLY_NO
                                AND q.SERIAL_NO=d.APPLY_SERIAL_NO AND q.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (applyProduct is not null)
            return new(false, "以下序号项申购单序号与产品编号不相符 \r\n" + applyProduct);
        // 产品编号存在
        var productMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_PURCHASE_D d
            WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (productMissing is not null)
            return new(false, "以下序号项产品编号不存在 \r\n" + productMissing);

        var hasMore = await ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.PUR_PURCHASE_MORE WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;", type, no, token);
        if (hasMore)
        {
            // a. 补明细行
            var maxSerial = await ScalarIntAsync(connection, transaction,
                "SELECT ISNULL(MAX(SERIAL_NO),0) FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;",
                type, no, token);
            var missingProducts = await ReadStringsAsync(connection, transaction, """
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
            // e. 明细金额重算（I/O/N 税公式，对齐旧 SP 与 AmountCalculator）
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
            // g. 数量分配（等价旧游标）
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
        await UpdateDistinctFieldAsync(connection, transaction, type, no, "ORDER_NO", "PUR_PURCHASE_MORE", "ORDER_NO",
            moreTypeColumn: "PURCHASE_TYPE", moreNoColumn: "PURCHASE_NO", token);
        await UpdateDistinctFieldAsync(connection, transaction, type, no, "PRODUCE_NO", "PUR_PURCHASE_MORE", "PRODUCE_NO",
            moreTypeColumn: "PURCHASE_TYPE", moreNoColumn: "PURCHASE_NO", token);
        return new(true, null);
    }

    private static async Task UpdatePurchaseMoreQtyAsync(
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
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (supplierMismatch is not null)
            return new(false, "以下序号项收料单与采购单厂商不符 \r\n" + supplierMismatch);
        // 采购单存在
        var purchaseMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_RECEIVE_D d
            WHERE RECEIVE_TYPE=@Type AND RECEIVE_NO=@No AND ISNULL(PURCHASE_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_M o WHERE o.PURCHASE_TYPE=d.PURCHASE_TYPE AND o.PURCHASE_NO=d.PURCHASE_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
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
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (purchaseProduct is not null)
            return new(false, "以下序号项采购订单序号与产品编号不相符 \r\n" + purchaseProduct);
        // 产品编号存在
        var productMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_RECEIVE_D d
            WHERE RECEIVE_TYPE=@Type AND RECEIVE_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (productMissing is not null)
            return new(false, "以下序号项产品编号不存在 \r\n" + productMissing);
        // 需批号产品必须填写 BATCH_NO
        var batchMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_RECEIVE_D d
            WHERE RECEIVE_TYPE=@Type AND RECEIVE_NO=@No AND ISNULL(BATCH_NO,'')=''
              AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO AND p.MANAGE_BATCH=1);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
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
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
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
        await UpdateDistinctFieldAsync(connection, transaction, type, no, "ORDER_NO", "PUR_APPLY_MORE", "ORDER_NO",
            moreTypeColumn: "APPLY_TYPE", moreNoColumn: "APPLY_NO", token);
        await UpdateDistinctFieldAsync(connection, transaction, type, no, "PRODUCE_NO", "PUR_APPLY_MORE", "PRODUCE_NO",
            moreTypeColumn: "APPLY_TYPE", moreNoColumn: "APPLY_NO", token);
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
        string type, string no, string masterColumn, string moreTable, string moreColumn,
        string moreTypeColumn, string moreNoColumn, CancellationToken token)
    {
        var values = await ReadStringsAsync(connection, transaction,
            $"SELECT DISTINCT LTRIM(RTRIM(ISNULL([{moreColumn}],''))) FROM dbo.[{moreTable}] " +
            $"WHERE [{moreTypeColumn}]=@Type AND [{moreNoColumn}]=@No AND ISNULL([{moreColumn}],'')<>'' ORDER BY 1;",
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
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (preSendLines is not null)
            return new(false, "以下序号项预交日期小于订单日期 \r\n" + preSendLines);
        // 报价单与订单客户一致
        var quoteMismatch = await FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO FROM dbo.COP_QUOTE_M q
            INNER JOIN dbo.COP_ORDER_D d ON q.QUOTE_TYPE=d.QUOTE_TYPE AND q.QUOTE_NO=d.QUOTE_NO
            INNER JOIN dbo.COP_ORDER_M m ON m.ORDER_TYPE=d.ORDER_TYPE AND m.ORDER_NO=d.ORDER_NO
            WHERE m.ORDER_TYPE=@Type AND m.ORDER_NO=@No AND q.CLIENT_ID<>m.CLIENT_ID;
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (quoteMismatch is not null)
            return new(false, "以下序号项报价单与订单客户不符 \r\n" + quoteMismatch);
        // 报价单存在
        var quoteMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_ORDER_D d
            WHERE ORDER_TYPE=@Type AND ORDER_NO=@No AND ISNULL(QUOTE_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_QUOTE_M q WHERE q.QUOTE_TYPE=d.QUOTE_TYPE AND q.QUOTE_NO=d.QUOTE_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
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
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (quoteProduct is not null)
            return new(false, "以下序号项报价单序号与产品编号不相符 \r\n" + quoteProduct);
        // 产品编号存在
        var productMissing = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_ORDER_D d
            WHERE ORDER_TYPE=@Type AND ORDER_NO=@No
              AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
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
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (mismatchLines is not null)
            return new(false, "以下序号项询价单与报价单厂商不符 \r\n" + mismatchLines);
        var missingLines = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.PUR_QUOTE_D d
            WHERE QUOTE_TYPE=@Type AND QUOTE_NO=@No AND ISNULL(CHAFFER_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.PUR_CHAFFER_M q WHERE q.CHAFFER_TYPE=d.CHAFFER_TYPE AND q.CHAFFER_NO=d.CHAFFER_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
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
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
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
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (mismatchLines is not null)
            return new(false, "以下序号项询价单与报价单客户不符 \r\n" + mismatchLines);
        // 询价单不存在
        var missingLines = await FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_QUOTE_D d
            WHERE QUOTE_TYPE=@Type AND QUOTE_NO=@No AND ISNULL(CHAFFER_TYPE,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_CHAFFER_M q WHERE q.CHAFFER_TYPE=d.CHAFFER_TYPE AND q.CHAFFER_NO=d.CHAFFER_NO);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
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
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
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
