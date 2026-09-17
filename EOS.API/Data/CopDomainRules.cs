using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Customer-order domain rules executed after module saves (quotes, orders, deliveries, returns, fit-out, callback and account documents). Methods run inside the caller's transaction.
/// </summary>
public static class CopDomainRules
{
    /// <summary>备货单（P_COP_FITOUT）AfterSave：CHECK 分支（SYSSS 门控）+ 批号条件必填。</summary>
    public static async Task<SprocResult> CopFitoutAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "备货单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (await DomainRuleService.HasErrorNoSaveAsync(connection, transaction, moduleId, token))
        {
            var check = await CopFitoutCheckAsync(connection, transaction, type, no, token);
            if (check is not null) return check;
        }
        return await DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "COP_FITOUT_D", "FITOUT_TYPE", "FITOUT_NO",
            [
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号! "),
            ], token);
    }

    /// <summary>采购退料单（P_PUR_CANCEL）AfterSave：6 项引用校验 + 退料不超收料。</summary>


    /// <summary>P_COP_FITOUT_CHECK 内联：已备货不超订单/工单完工数量（SYSSS 门控）。</summary>


    /// <summary>P_COP_FITOUT_CHECK 内联：已备货不超订单/工单完工数量（SYSSS 门控）。</summary>
    public static async Task<SprocResult?> CopFitoutCheckAsync(
        SqlConnection connection, SqlTransaction transaction, string type, string no, CancellationToken token)
    {
        if (await DomainRuleService.ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.SYSSS WHERE FITOUT_ORDER_TAG=1;", type, no, token))
        {
            var rows = await DomainRuleService.FindLinesAsync(connection, transaction,
                """
                SELECT od.ORDER_NO, od.QTY, od.FINISHED_FITOUT_QTY, sd.QTY, od.SPARE_QTY, od.FINISHED_FITOUT_SPARE_QTY, sd.SPARE_QTY
                FROM dbo.COP_ORDER_D od
                INNER JOIN (SELECT ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                            FROM dbo.COP_FITOUT_D WHERE FITOUT_TYPE=@Type AND FITOUT_NO=@No
                            GROUP BY ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO) sd
                  ON od.ORDER_TYPE=sd.ORDER_TYPE AND od.ORDER_NO=sd.ORDER_NO AND od.SERIAL_NO=sd.ORDER_SERIAL_NO
                WHERE (ISNULL(od.FINISHED_FITOUT_QTY,0)+sd.QTY > ISNULL(od.QTY,0)
                    OR ISNULL(od.FINISHED_FITOUT_SPARE_QTY,0)+sd.SPARE_QTY > ISNULL(od.SPARE_QTY,0));
                """, type, no, token,
                line: r => string.Join("    ", Enumerable.Range(0, r.FieldCount).Select(i => (Convert.ToString(r.GetValue(i)) ?? string.Empty).Trim())));
            if (rows is not null)
                return new(false, "以下会出现已备货数量超出订单数量\r\n订单号   数量   已备货数量  单据数量  备品  已备货备品  单据备品\r\n" + rows);
        }
        if (await DomainRuleService.ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.SYSSS WHERE FITOUT_PRODUCE_TAG=1;", type, no, token))
        {
            var rows = await DomainRuleService.FindLinesAsync(connection, transaction,
                """
                SELECT od.PRODUCE_NO, od.FINISHED_QTY, od.FINISHED_FITOUT_QTY, sd.QTY, od.FINISHED_SPARE_QTY, od.FINISHED_FITOUT_SPARE_QTY, sd.SPARE_QTY
                FROM dbo.MOC_PRODUCE_M od
                INNER JOIN (SELECT PRODUCE_TYPE, PRODUCE_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                            FROM dbo.COP_FITOUT_D WHERE FITOUT_TYPE=@Type AND FITOUT_NO=@No
                            GROUP BY PRODUCE_TYPE, PRODUCE_NO) sd
                  ON od.PRODUCE_TYPE=sd.PRODUCE_TYPE AND od.PRODUCE_NO=sd.PRODUCE_NO
                WHERE (ISNULL(od.FINISHED_FITOUT_QTY,0)+sd.QTY > ISNULL(od.FINISHED_QTY,0)
                    OR ISNULL(od.FINISHED_FITOUT_SPARE_QTY,0)+sd.SPARE_QTY > ISNULL(od.FINISHED_SPARE_QTY,0));
                """, type, no, token,
                line: r => string.Join("    ", Enumerable.Range(0, r.FieldCount).Select(i => (Convert.ToString(r.GetValue(i)) ?? string.Empty).Trim())));
            if (rows is not null)
                return new(false, "以下会出现已备货数量超出工单完工数量\r\n工单号   数量   已备货数量  单据数量  备品  已备货备品  单据备品\r\n" + rows);
        }
        return null;
    }

    /// <summary>P_COP_FITIN_CHECK 内联：返仓不超备货/送货不超备货（订单/工单/调拨，SYSSS 门控）。</summary>

    /// <summary>P_PUR_CANCEL 退料不超收料。</summary>


    /// <summary>送货回执（1413）AfterSave：送/退货已有回执校验。</summary>
    public static async Task<SprocResult> CopCallbackAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        var lines = await DomainRuleService.FindLinesAsync(connection, transaction,
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

    /// <summary>工时录入（180207）AfterSave：HR_SETUP.REQUIRE_ENACTMENT=1 时校验加班不超申请（当前环境=0，跳过）。</summary>


    /// <summary>客户订单变更（1418）AfterSave：原单已批核 + 变更量校验 + 订单号唯一。</summary>
    public static async Task<SprocResult> CopOrderChangeAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        if (await DomainRuleService.ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.COP_ORDER_M m
            INNER JOIN dbo.COP_ORDER_CHANGE_M c ON c.ORDER_TYPE=m.ORDER_TYPE AND c.ORDER_NO=m.ORDER_NO
            WHERE c.CHANGE_ORDER_TYPE=@Type AND c.CHANGE_ORDER_NO=@No AND m.CONFIRM_TAG=0;
            """, type, no, token))
            return new(false, "订单未批核，不可变更");
        var lines = await DomainRuleService.FindLinesAsync(connection, transaction,
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
        var planLines = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT oc.SERIAL_NO FROM dbo.COP_ORDER_D od
            INNER JOIN dbo.COP_ORDER_CHANGE_D oc
              ON oc.ORDER_TYPE=od.ORDER_TYPE AND oc.ORDER_NO=od.ORDER_NO AND oc.ORDER_SERIAL_NO=od.SERIAL_NO
            WHERE oc.CHANGE_ORDER_TYPE=@Type AND oc.CHANGE_ORDER_NO=@No AND oc.PLAN_QTY < ISNULL(od.FINISHED_PRODUCE_QTY,0);
            """, type, no, token, line: r => "    " + Convert.ToInt32(r.GetValue(0)).ToString());
        if (planLines is not null)
            return new(false, "变更后以下序号项计划生产数量小于已下生产单数量\r\n" + planLines);
        // 客户订单号不重复
        if (await DomainRuleService.ExistsAsync(connection, transaction,
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


    /// <summary>
    /// 送货单（1406）AfterSave：排程/订单量校验（SYSSS 标志门控）+ 库存可用校验 +
    /// 订单一致性 + 批号 + 30 天日期 + mo_no 标记。
    /// </summary>
    public static async Task<SprocResult> CopSendAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        var flags = await ReadSysssFlagsAsync(connection, transaction, token);

        // 批号必填（订单一致性/产品存在性由校验目录承接）
        var batchMissing = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT SERIAL_NO FROM dbo.COP_SEND_D d
            WHERE SEND_TYPE=@Type AND SEND_NO=@No AND ISNULL(BATCH_NO,'')=''
              AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.PRO_NO AND p.MANAGE_BATCH=1);
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (batchMissing is not null)
            return new(false, "以下序号项需要输入批号 \r\n" + batchMissing);
        // 送货日期不能小于建立日期 30 天
        var dateTooOld = await DomainRuleService.ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.COP_SEND_M WHERE SEND_TYPE=@Type AND SEND_NO=@No AND DATEDIFF(day, SEND_DATE, CREATE_DATE)>30;",
            type, no, token);
        if (dateTooOld) return new(false, "送货日期不能小于建立日期30天");

        // 库存可用校验（SEND_TAG=1）：库别存在 + 库存数量 + 批号库存
        if (flags.GetValueOrDefault("SEND_TAG") == 1)
        {
            var depotMissing = await DomainRuleService.FindLinesAsync(connection, transaction,
                """
                SELECT SERIAL_NO, DEPOT_ID FROM dbo.COP_SEND_D d
                WHERE SEND_TYPE=@Type AND SEND_NO=@No AND ISNULL(DEPOT_ID,'')<>''
                  AND NOT EXISTS (SELECT 1 FROM dbo.DEPOT dp WHERE dp.DEPOT_ID=d.DEPOT_ID);
                """, type, no, token,
                line: r => $"{r.GetInt32(0)}    {r.GetString(1).Trim()}");
            if (depotMissing is not null)
                return new(false, "以下库别不存在\r\n序号----库别\r\n" + depotMissing);
            var stockLines = await DomainRuleService.FindLinesAsync(connection, transaction,
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
            var batchStock = await DomainRuleService.FindLinesAsync(connection, transaction,
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

    public static async Task<Dictionary<string, int>> ReadSysssFlagsAsync(
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


    /// <summary>客户订单（1405）AfterSave：订单检查（P_COP_ORDER_CHECK 六规则）+ 客户/报价/产品一致性。</summary>
    public static async Task<SprocResult> CopOrderAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];

        // 1. 客户交易天数（SYSSS.CLIENT_DAYS；任一缺失跳过）
        var tradeDays = await DomainRuleService.ExistsAsync(connection, transaction,
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
        var minOrder = await DomainRuleService.FindLinesAsync(connection, transaction,
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
        // 3. 客户信用余额（CREDIT_LIMIT_NUM 为 NULL 时不启用—— NULL 比较语义）
        var credit = await DomainRuleService.FindLinesAsync(connection, transaction,
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
        // 4. 产品交易天数（SYSSS.PRODUCT_DAYS；客户交易天数缺失时跳过本检查）
        var productDays = await DomainRuleService.FindLinesAsync(connection, transaction,
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
        var priceExpired = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT DISTINCT a.PRO_NO FROM dbo.COP_ORDER_D a
            JOIN dbo.COP_ORDER_M m ON m.ORDER_TYPE=a.ORDER_TYPE AND m.ORDER_NO=a.ORDER_NO
            JOIN dbo.CLIENT_PRICE_D p ON p.CLIENT_ID=m.CLIENT_ID AND p.PRO_NO=a.PRO_NO
            WHERE a.ORDER_TYPE=@Type AND a.ORDER_NO=@No AND p.IN_EFFECT_DATE < m.ORDER_DATE;
            """, type, no, token, line: r => r.GetString(0).Trim());
        if (priceExpired is not null)
            return new(false, "以下产品计价已过有效期\r\n" + priceExpired);
        // 6. 最小生产数量（QTY < MIN_PRODUCE_QTY）
        var minProduce = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT TOP 10 p.PRO_NO FROM dbo.COP_ORDER_D o
            JOIN dbo.PRODUCT p ON p.PRO_NO=o.PRO_NO
            WHERE o.ORDER_TYPE=@Type AND o.ORDER_NO=@No AND o.QTY < ISNULL(p.MIN_PRODUCE_QTY,0);
            """, type, no, token, line: r => r.GetString(0).Trim());
        if (minProduce is not null)
            return new(false, "以下产品编号订单量低于最小生产要求数量\r\n" + minProduce);

        // 客户订单号不重复（排除自身）
        var duplicateOrderNo = await DomainRuleService.ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.COP_ORDER_M
            WHERE CLIENT_ORDER_NO=(SELECT CLIENT_ORDER_NO FROM dbo.COP_ORDER_M WHERE ORDER_TYPE=@Type AND ORDER_NO=@No)
              AND ISNULL(CLIENT_ORDER_NO,'')<>'' AND NOT (ORDER_TYPE=@Type AND ORDER_NO=@No);
            """, type, no, token);
        if (duplicateOrderNo) return new(false, "客户订单号重复。");
        // 预交日期 >= 订单日期
        var preSendLines = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO FROM dbo.COP_ORDER_D d
            JOIN dbo.COP_ORDER_M m ON m.ORDER_TYPE=d.ORDER_TYPE AND m.ORDER_NO=d.ORDER_NO
            WHERE d.ORDER_TYPE=@Type AND d.ORDER_NO=@No AND d.PRE_SEND_DATE < m.ORDER_DATE;
            """, type, no, token, line: r => Convert.ToInt32(r.GetValue(0)).ToString());
        if (preSendLines is not null)
            return new(false, "以下序号项预交日期小于订单日期 \r\n" + preSendLines);
        return new(true, null);
    }

    /// <summary>厂商报价单（1604）AfterSave：厂商校验 + 询价单一致性校验（镜像 cop-quote，厂商侧）。</summary>


    /// <summary>应收货款单（170101）AfterSave：对帐不超送/退货量 + 客户校验 + 单证存在性 + 金额汇总。</summary>


    /// <summary>应收货款单（170101）AfterSave：对帐不超送/退货量 + 客户校验 + 单证存在性 + 金额汇总。</summary>
    public static async Task<SprocResult> CopAccountAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = DomainRuleService.KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        var sendErrors = await DomainRuleService.FindExceededAsync(connection, transaction, type, no, token,
            detailTable: "COP_ACCOUNT_D", detailTypeColumn: "ACCOUNT_TYPE", detailNoColumn: "ACCOUNT_NO",
            detailGroupTypeColumn: "S_R_TYPE", detailGroupNoColumn: "S_R_NO", detailGroupSerialColumn: "S_R_SERIAL_NO",
            sourceTable: "COP_SEND_D", sourceTypeColumn: "SEND_TYPE", sourceNoColumn: "SEND_NO");
        if (sendErrors is not null)
            return new(false, "以下对帐已超出送货单数量\r\n 送货单号  送货数量  已对帐数量  单据数量\r\n" + sendErrors);
        var returnErrors = await DomainRuleService.FindExceededAsync(connection, transaction, type, no, token,
            detailTable: "COP_ACCOUNT_D", detailTypeColumn: "ACCOUNT_TYPE", detailNoColumn: "ACCOUNT_NO",
            detailGroupTypeColumn: "S_R_TYPE", detailGroupNoColumn: "S_R_NO", detailGroupSerialColumn: "S_R_SERIAL_NO",
            sourceTable: "COP_RETURN_D", sourceTypeColumn: "RETURN_TYPE", sourceNoColumn: "RETURN_NO");
        if (returnErrors is not null)
            return new(false, "以下对帐已超出退货单数量\r\n 退货单号  退货数量  已对帐数量  单据数量\r\n" + returnErrors);

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

    /// <summary>收款单（170102）AfterSave：预收汇总 + 实收校验 + 对帐不超收。</summary>


    /// <summary>收款单（170102）AfterSave：预收汇总 + 实收校验 + 对帐不超收。</summary>
    public static async Task<SprocResult> CopReceiptAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = DomainRuleService.KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        // 预收冲抵汇总
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
        var negative = await DomainRuleService.ExistsAsync(connection, transaction,
            $"SELECT TOP 1 1 FROM dbo.COP_RECEIPT_M WHERE [{typeColumn}]=@Type AND [{noColumn}]=@No AND RECEIVE_SUM<0;",
            type, no, token);
        if (negative) return new(false, "实收金额不能为负数");
        // 实收不能大于应收-折扣-预收冲帐
        var exceedMaster = await DomainRuleService.ExistsAsync(connection, transaction,
            $"SELECT TOP 1 1 FROM dbo.COP_RECEIPT_M WHERE [{typeColumn}]=@Type AND [{noColumn}]=@No AND RECEIVE_SUM>AMOUNT_TAX-REBATE_SUM-PREPAY_SUM+0.1;",
            type, no, token);
        if (exceedMaster) return new(false, "实收金额 不能大于 应收金额-现金折扣-预收冲帐");
        // 对帐单已收款不能大于应收款
        var accountErrors = await DomainRuleService.FindLinesAsync(connection, transaction,
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

    /// <summary>预收帐款单（170103）AfterSave：金额汇总。</summary>
    public static async Task<SprocResult> CopPrepayAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = DomainRuleService.KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
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
}
