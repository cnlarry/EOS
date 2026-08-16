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
                "hr-worktime" => await HrWorktimeAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-check-stock" => await InvCheckStockAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "sfc-plan" => await SfcPlanAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-quote" => await PurQuoteAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-receive" => await PurReceiveAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-apply" => await PurApplyAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-purchase" => await PurPurchaseAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-pay" => await PurPayAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-prepay" => await PurPrepayAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "curr" => await CurrAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "bom-stru" => await BomStruAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "sysqr-default" => await SysqrDefaultAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "sysdg" => await SysdgAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "sysdl" => await SysdlAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "employee-card" => await EmployeeCardAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-in" => await InvOccurInAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-out" => await InvOccurOutAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-transfer" => await InvOccurTransferAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-scrap" => await InvOccurScrapAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-adjust" => await InvOccurAdjustAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-init" => await InvOccurInitAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-get" => await MocGetAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-produce" => await MocProduceAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "moc-product-in" => await MocProductInAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "moc-product-out" => await MocProductOutAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "sfc-process" => await SfcProcessAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "sfc-daily" => await SfcDailyAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-return" => await CopReturnAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-fitout" => await CopFitoutAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "cop-fitin" => await CopFitinAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "cop-back" => await CopBackAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "pur-cancel" => await PurCancelAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
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

    /// <summary>
    /// 工序排程（2708）AfterSave：SFC_PLAN_MORE 展开到 SFC_PLAN_D（补工序行、QTY 归零回填、
    /// 标准时间/产能/工序类型/USE_STAND_TIME/PRODUCE_NO 后缀），等价 P_SFC_PLAN_After_Save。
    /// </summary>
    private static async Task<SprocResult> SfcPlanAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var (typeColumn, noColumn) = KeyColumns(pkColumns);
        var type = keyValues[0]; var no = keyValues[1];
        // 1. 补不存在的工序行（MORE × PROCESS_D）
        var maxSerial = await ScalarIntAsync(connection, transaction,
            "SELECT ISNULL(MAX(SERIAL_NO),0) FROM dbo.SFC_PLAN_D WHERE PLAN_TYPE=@Type AND PLAN_NO=@No;",
            type, no, token);
        var missingRows = new List<(string ProNo, string ProcedureId, decimal Qty)>();
        await using (var read = new SqlCommand("""
            SELECT m.PRO_NO, d.PROCEDURE_ID, SUM(m.QTY*d.PROCESS_QTY*d.PERSON_HOUR_UNIT) QTY
            FROM dbo.SFC_PLAN_MORE m
            INNER JOIN dbo.SFC_PROCESS_D d ON d.PRO_NO=m.PRO_NO
            WHERE m.PLAN_TYPE=@Type AND m.PLAN_NO=@No
              AND d.PRO_NO+d.PROCEDURE_ID NOT IN
                  (SELECT PRO_NO+PROCEDURE_ID FROM dbo.SFC_PLAN_D WHERE PLAN_TYPE=@Type AND PLAN_NO=@No)
            GROUP BY m.PLAN_TYPE, m.PLAN_NO, m.PRO_NO, d.PROCEDURE_ID;
            """, connection, transaction))
        {
            read.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            read.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await using var reader = await read.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                missingRows.Add((reader.GetString(0).Trim(), reader.GetString(1).Trim(), Convert.ToDecimal(reader.GetValue(2))));
        }
        foreach (var row in missingRows)
        {
            maxSerial++;
            await using var insert = new SqlCommand("""
                INSERT INTO dbo.SFC_PLAN_D (PLAN_TYPE, PLAN_NO, SERIAL_NO, PRO_NO, PROCEDURE_ID, QTY, CLIENT_ID)
                SELECT @Type, @No, @Serial, @ProNo, @ProcedureId, @Qty, p.CLIENT_ID
                FROM dbo.PRODUCT p WHERE p.PRO_NO=@ProNo;
                """, connection, transaction);
            insert.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            insert.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            insert.Parameters.Add("@Serial", SqlDbType.Int).Value = maxSerial;
            insert.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = row.ProNo;
            insert.Parameters.Add("@ProcedureId", SqlDbType.NVarChar, 30).Value = row.ProcedureId;
            insert.Parameters.Add("@Qty", SqlDbType.Decimal).Value = row.Qty;
            await insert.ExecuteNonQueryAsync(token);
        }
        // 2. QTY 归零回填（含标准时间/产能/工时）
        await using (var zero = new SqlCommand(
            "UPDATE dbo.SFC_PLAN_D SET QTY=0 WHERE PLAN_TYPE=@Type AND PLAN_NO=@No;", connection, transaction))
        {
            zero.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            zero.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await zero.ExecuteNonQueryAsync(token);
        }
        await using (var backfill = new SqlCommand("""
            UPDATE d SET d.QTY=s.QTY, d.PRODUCE_QTY=s.PRODUCE_QTY, d.HOURS=s.HOURS, d.PERSON_UNIT_HOUR=s.PERSON_UNIT_HOUR
            FROM dbo.SFC_PLAN_D d
            INNER JOIN (SELECT m.PLAN_TYPE, m.PLAN_NO, m.PRO_NO, d.PROCEDURE_ID, d.STANDARD_TIME, d.PERSON_UNIT_HOUR,
                               SUM(m.PRODUCE_QTY) PRODUCE_QTY, SUM(m.QTY) QTY,
                               CASE WHEN ISNULL(d.STANDARD_TIME,0)>0 THEN MAX(d.STANDARD_TIME)
                                    ELSE SUM(m.QTY*d.PROCESS_QTY*d.PERSON_HOUR_UNIT) END HOURS
                        FROM dbo.SFC_PLAN_MORE m
                        INNER JOIN dbo.SFC_PROCESS_D d ON d.PRO_NO=m.PRO_NO
                        WHERE m.PLAN_TYPE=@Type AND m.PLAN_NO=@No
                        GROUP BY m.PLAN_TYPE, m.PLAN_NO, m.PRO_NO, d.PROCEDURE_ID, d.STANDARD_TIME, d.PERSON_UNIT_HOUR) s
              ON d.PLAN_TYPE=s.PLAN_TYPE AND d.PLAN_NO=s.PLAN_NO AND d.PRO_NO=s.PRO_NO AND d.PROCEDURE_ID=s.PROCEDURE_ID
            WHERE d.PLAN_TYPE=@Type AND d.PLAN_NO=@No;
            """, connection, transaction))
        {
            backfill.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            backfill.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await backfill.ExecuteNonQueryAsync(token);
        }
        // 3. 工序类型回填
        await using (var procType = new SqlCommand("""
            UPDATE d SET d.PROCEDURE_TYPE_ID=p.PROCEDURE_TYPE_ID
            FROM dbo.SFC_PLAN_D d INNER JOIN dbo.SFC_PROCEDURE p ON p.PROCEDURE_ID=d.PROCEDURE_ID
            WHERE d.PLAN_TYPE=@Type AND d.PLAN_NO=@No;
            """, connection, transaction))
        {
            procType.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            procType.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await procType.ExecuteNonQueryAsync(token);
        }
        // 4. USE_STAND_TIME=1 工序 QTY/PRODUCE_QTY=1
        await using (var stdTime = new SqlCommand("""
            UPDATE d SET d.QTY=1, d.PRODUCE_QTY=1
            FROM dbo.SFC_PLAN_D d INNER JOIN dbo.SFC_PROCEDURE p ON p.PROCEDURE_ID=d.PROCEDURE_ID
            WHERE p.USE_STAND_TIME=1 AND d.PLAN_TYPE=@Type AND d.PLAN_NO=@No;
            """, connection, transaction))
        {
            stdTime.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            stdTime.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await stdTime.ExecuteNonQueryAsync(token);
        }
        // 5. PRODUCE_NO 后缀（MORE 生产单号后 4 位）
        await using (var produceNo = new SqlCommand("""
            UPDATE d SET d.PRODUCE_NO=RTRIM(ISNULL(d.PRODUCE_NO,''))+RIGHT(RTRIM(ISNULL(m.PRODUCE_NO,'')),4)
            FROM dbo.SFC_PLAN_D d INNER JOIN dbo.SFC_PLAN_MORE m
              ON m.PLAN_TYPE=d.PLAN_TYPE AND m.PLAN_NO=d.PLAN_NO AND m.PRO_NO=d.PRO_NO
            WHERE d.PLAN_TYPE=@Type AND d.PLAN_NO=@No;
            """, connection, transaction))
        {
            produceNo.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
            produceNo.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
            await produceNo.ExecuteNonQueryAsync(token);
        }
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

    /// <summary>
    /// 货币资料（110103）AfterSave：本位币唯一 + 本位币汇率必须为 1（等价 P_CURR_After_Save）。
    /// 仅校验，无落库副作用。
    /// </summary>
    private static async Task<SprocResult> CurrAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 1 || keyValues.Count < 1) return new(false, "货币资料领域规则缺少主键。");
        var currId = (keyValues[0] ?? string.Empty).Trim();
        bool? isBase; decimal? rate;
        await using (var read = new SqlCommand(
            "SELECT IS_BASE, CURR_RATE FROM dbo.CURR WHERE CURR_ID=@CurrId;", connection, transaction))
        {
            read.Parameters.Add("@CurrId", SqlDbType.NChar, 10).Value = currId;
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return new(true, null);
            isBase = reader.IsDBNull(0) ? null : reader.GetBoolean(0);
            rate = reader.IsDBNull(1) ? null : Convert.ToDecimal(reader.GetValue(1));
        }
        if (isBase != true) return new(true, null);
        string? otherBase;
        await using (var other = new SqlCommand(
            "SELECT TOP 1 LTRIM(RTRIM(CURR_ID)) FROM dbo.CURR WHERE CURR_ID<>@CurrId AND IS_BASE=1;",
            connection, transaction))
        {
            other.Parameters.Add("@CurrId", SqlDbType.NChar, 10).Value = currId;
            otherBase = (string?)await other.ExecuteScalarAsync(token);
        }
        if (!string.IsNullOrEmpty(otherBase))
            return new(false, $"已将币别 [{otherBase}] 设为本位币，不能存在两种本位币");
        if (rate is null || rate.Value != 1m)
            return new(false, "本位币汇率只能为1");
        return new(true, null);
    }

    /// <summary>
    /// 产品BOM表（1204）AfterSave：品号/元件/底数/循环引用校验 + 长宽旧值回填
    /// （等价 P_BOM_STRU_After_Save；底数检查按 PRO_NO 限定——旧 SP 缺 PRO_NO 条件，
    /// 疑似笔误会误拦全表，见台账待顾问确认）。
    /// </summary>
    private static async Task<SprocResult> BomStruAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 1 || keyValues.Count < 1) return new(false, "产品BOM领域规则缺少主键。");
        var proNo = (keyValues[0] ?? string.Empty).Trim();
        // 1. 品号存在
        await using (var product = new SqlCommand(
            "SELECT TOP 1 1 FROM dbo.PRODUCT WHERE PRO_NO=@ProNo;", connection, transaction))
        {
            product.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
            if (await product.ExecuteScalarAsync(token) is null)
                return new(false, "产品编号不存在。 ");
        }
        // 2. 元件编号存在（最多列 10 个序号）
        var elements = await ReadBomMissingAsync(connection, transaction,
            "SELECT TOP 11 SERIAL_NO FROM dbo.BOM_STRU_D d WHERE PRO_NO=@ProNo " +
            "AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.ELEMENT_PRO_NO) ORDER BY SERIAL_NO;",
            proNo, token);
        if (elements is not null) return new(false, "以下序号项元件编号不存在 \r\n" + elements);
        // 3. 底数不能小于等于 0（按当前产品限定；旧 SP 全局扫描疑似笔误）
        var baseQty = await ReadBomMissingAsync(connection, transaction,
            "SELECT TOP 11 SERIAL_NO FROM dbo.BOM_STRU_D WHERE PRO_NO=@ProNo AND BASE_QTY<=0 ORDER BY SERIAL_NO;",
            proNo, token);
        if (baseQty is not null) return new(false, "以下序号项元件底数不能小于0 \r\n" + baseQty);
        // 4. 循环引用（受控调用 P_BOM_CHECK，保留 SP：递归 BOM 走查复杂，不移植）
        await using (var check = new SqlCommand("dbo.P_BOM_CHECK", connection, transaction)
        {
            CommandType = CommandType.StoredProcedure,
        })
        {
            check.Parameters.Add("@ProNo", SqlDbType.NVarChar, 50).Value = proNo;
            var ok = check.Parameters.Add("@ok", SqlDbType.Int);
            ok.Direction = ParameterDirection.Output;
            var errCode = check.Parameters.Add("@errCode", SqlDbType.NVarChar, 50);
            errCode.Direction = ParameterDirection.Output;
            await check.ExecuteNonQueryAsync(token);
            if (ok.Value is not int okValue || okValue != 1)
                return new(false, "以下元件在BOM结构中循环使用 \r\n" + Convert.ToString(errCode.Value));
        }
        // 5. 长宽旧值回填
        await using (var backfill = new SqlCommand("""
            UPDATE m SET m.P_LENGTH_OLD=p.P_LENGTH, m.P_WIDTH_OLD=p.P_WIDTH
            FROM dbo.BOM_STRU_M m INNER JOIN dbo.PRODUCT p ON p.PRO_NO=m.PRO_NO
            WHERE m.PRO_NO=@ProNo;
            """, connection, transaction))
        {
            backfill.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
            await backfill.ExecuteNonQueryAsync(token);
        }
        return new(true, null);
    }

    private static async Task<string?> ReadBomMissingAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, string proNo, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        var lines = new List<string>();
        while (await reader.ReadAsync(token)) lines.Add(Convert.ToInt32(reader.GetValue(0)).ToString());
        return lines.Count > 0 ? string.Join("\r\n", lines.Take(10)) : null;
    }

    /// <summary>
    /// 报表过滤条件设置（2205）AfterSave：条件定义保存后清空用户报表条件记忆
    /// （等价 P_SYSQR_DEFAULT_After_Save；旧 SP 的 @@ERROR 判断为死代码，不移植）。
    /// </summary>
    private static async Task<SprocResult> SysqrDefaultAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 1 || keyValues.Count < 1
            || !int.TryParse((keyValues[0] ?? string.Empty).Trim(), out var mIdx))
            return new(true, null); // 主键非数字：无 M_IDX 可清（与旧 SP 读取失败行为一致）
        await using var clear = new SqlCommand("DELETE FROM dbo.SYSQR_USER WHERE M_IDX=@MIdx;", connection, transaction);
        clear.Parameters.Add("@MIdx", SqlDbType.Int).Value = mIdx;
        await clear.ExecuteNonQueryAsync(token);
        return new(true, null);
    }

    /// <summary>
    /// 用户组管理（2305）AfterSave：清理组权限孤儿行（等价 P_SYSDG_After_Save；
    /// 作用于全部组，旧 SP 未使用主键）。注释掉的「新报表权限补齐」为死代码，不移植。
    /// </summary>
    private static async Task<SprocResult> SysdgAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        await using (var cleanGroup = new SqlCommand(
            "DELETE FROM dbo.SYSDH WHERE M_IDX NOT IN (SELECT M_IDX FROM dbo.MODULES);",
            connection, transaction))
        {
            await cleanGroup.ExecuteNonQueryAsync(token);
        }
        await using (var cleanReport = new SqlCommand("""
            DELETE FROM dbo.SYSDH_REPORT
            WHERE M_IDX NOT IN (SELECT M_IDX FROM dbo.MODULES) OR REPORT_ID NOT IN (SELECT REPORT_ID FROM dbo.REPORT);
            """, connection, transaction))
        {
            await cleanReport.ExecuteNonQueryAsync(token);
        }
        return new(true, null);
    }

    /// <summary>
    /// 用户权限设定（2306）AfterSave：默认组入组 + 报表权限补齐 + 个人权限孤儿清理
    /// （等价 P_SYSDL_After_Save；@user_id 取自 SYSDD，无个人权限行时跳过入组/报表补齐，
    /// 孤儿清理仍全局执行——与旧 SP 一致）。
    /// </summary>
    private static async Task<SprocResult> SysdlAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 1 || keyValues.Count < 1) return new(false, "用户权限领域规则缺少主键。");
        var keyUser = (keyValues[0] ?? string.Empty).Trim();
        string? userId;
        await using (var read = new SqlCommand(
            "SELECT TOP 1 LTRIM(RTRIM(USER_ID)) FROM dbo.SYSDD WHERE USER_ID=@UserId;",
            connection, transaction))
        {
            read.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = keyUser;
            userId = (string?)await read.ExecuteScalarAsync(token);
        }
        if (!string.IsNullOrEmpty(userId))
        {
            // 1. 默认组入组
            string? groupId;
            await using (var readGroup = new SqlCommand(
                "SELECT LTRIM(RTRIM(ISNULL(G_IDX,''))) FROM dbo.SYSDL WHERE USER_ID=@UserId;",
                connection, transaction))
            {
                readGroup.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId;
                groupId = (string?)await readGroup.ExecuteScalarAsync(token);
            }
            if (!string.IsNullOrWhiteSpace(groupId))
            {
                await using var join = new SqlCommand("""
                    INSERT INTO dbo.SYSDG_USER (G_IDX, USER_ID)
                    SELECT @GIdx, @UserId
                    WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSDG_USER WHERE G_IDX=@GIdx AND USER_ID=@UserId);
                    """, connection, transaction);
                join.Parameters.Add("@GIdx", SqlDbType.NChar, 10).Value = groupId;
                join.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId;
                await join.ExecuteNonQueryAsync(token);
            }
            // 2. 报表权限补齐（有 REPORT_TAG=1 个人模块的报表，预览/打印/导出默认开通）
            await using var reports = new SqlCommand("""
                INSERT INTO dbo.SYSDD_REPORT (USER_ID, M_IDX, REPORT_ID, PREVIEW_TAG, PRINT_TAG, EXPORT_TAG, DATA_FILTER)
                SELECT m.USER_ID, r.R_M_IDX, r.REPORT_ID, 1, 1, 1, ''
                FROM dbo.SYSDL m CROSS JOIN dbo.REPORT r
                WHERE m.USER_ID=@UserId
                  AND r.REPORT_ID NOT IN (SELECT REPORT_ID FROM dbo.SYSDD_REPORT WHERE USER_ID=@UserId)
                  AND r.R_M_IDX IN (SELECT M_IDX FROM dbo.SYSDD WHERE REPORT_TAG=1 AND USER_ID=@UserId);
                """, connection, transaction);
            reports.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId;
            await reports.ExecuteNonQueryAsync(token);
        }
        // 3. 个人权限孤儿清理（全局，与旧 SP 一致）
        await using (var cleanDd = new SqlCommand(
            "DELETE FROM dbo.SYSDD WHERE M_IDX NOT IN (SELECT M_IDX FROM dbo.MODULES);",
            connection, transaction))
        {
            await cleanDd.ExecuteNonQueryAsync(token);
        }
        await using (var cleanDdReport = new SqlCommand("""
            DELETE FROM dbo.SYSDD_REPORT
            WHERE M_IDX NOT IN (SELECT M_IDX FROM dbo.MODULES) OR REPORT_ID NOT IN (SELECT REPORT_ID FROM dbo.REPORT);
            """, connection, transaction))
        {
            await cleanDdReport.ExecuteNonQueryAsync(token);
        }
        return new(true, null);
    }

    /// <summary>
    /// 员工发卡（180208）AfterSave：失效日期不得早于生效日期 + 旧卡到期日联动
    /// （等价 P_Employee_Card_After_Save：同卡号其它员工旧卡、同员工其它卡到期 =
    /// 生效日前一天；注释掉的重复占用校验为死代码，不移植）。
    /// </summary>
    private static async Task<SprocResult> EmployeeCardAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "员工发卡领域规则缺少主键。");
        var empId = (keyValues[0] ?? string.Empty).Trim();
        var cardId = (keyValues[1] ?? string.Empty).Trim();
        DateTime? beginDate; DateTime? endDate;
        await using (var read = new SqlCommand(
            "SELECT BEGIN_DATE, END_DATE FROM dbo.HR_EMPLOYEE_CARD WHERE EMP_ID=@EmpId AND CARD_ID=@CardId;",
            connection, transaction))
        {
            read.Parameters.Add("@EmpId", SqlDbType.NChar, 10).Value = empId;
            read.Parameters.Add("@CardId", SqlDbType.NChar, 20).Value = cardId;
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return new(true, null);
            beginDate = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
            endDate = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        }
        if (endDate < beginDate) return new(false, "失于日期应在生效日期后");
        if (beginDate is not null)
        {
            var expires = beginDate.Value.AddDays(-1);
            await using (var updCard = new SqlCommand("""
                UPDATE dbo.HR_EMPLOYEE_CARD SET END_DATE=@Expires
                WHERE CARD_ID=@CardId AND EMP_ID<>@EmpId AND (END_DATE IS NULL OR END_DATE>=@BeginDate);
                """, connection, transaction))
            {
                updCard.Parameters.Add("@Expires", SqlDbType.DateTime).Value = expires;
                updCard.Parameters.Add("@CardId", SqlDbType.NChar, 20).Value = cardId;
                updCard.Parameters.Add("@EmpId", SqlDbType.NChar, 10).Value = empId;
                updCard.Parameters.Add("@BeginDate", SqlDbType.DateTime).Value = beginDate.Value;
                await updCard.ExecuteNonQueryAsync(token);
            }
            await using (var updEmp = new SqlCommand("""
                UPDATE dbo.HR_EMPLOYEE_CARD SET END_DATE=@Expires
                WHERE EMP_ID=@EmpId AND CARD_ID<>@CardId AND (END_DATE IS NULL OR END_DATE>=@BeginDate);
                """, connection, transaction))
            {
                updEmp.Parameters.Add("@Expires", SqlDbType.DateTime).Value = expires;
                updEmp.Parameters.Add("@EmpId", SqlDbType.NChar, 10).Value = empId;
                updEmp.Parameters.Add("@CardId", SqlDbType.NChar, 20).Value = cardId;
                updEmp.Parameters.Add("@BeginDate", SqlDbType.DateTime).Value = beginDate.Value;
                await updEmp.ExecuteNonQueryAsync(token);
            }
        }
        return new(true, null);
    }

    /// <summary>库存单 AfterSave 通用校验：库别/产品/批号（等价 P_INV_OCCUR_*_After_Save，纯校验无写）。</summary>
    private static async Task<SprocResult> InvOccurValidateAsync(
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

    private static Task<SprocResult> InvOccurInAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_IN_D", false, token);

    private static Task<SprocResult> InvOccurOutAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_OUT_D", false, token);

    private static Task<SprocResult> InvOccurTransferAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_TRANSFER_D", true, token);

    private static Task<SprocResult> InvOccurScrapAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_SCRAP_D", true, token);

    private static Task<SprocResult> InvOccurAdjustAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_ADJUST_D", false, token);

    private static Task<SprocResult> InvOccurInitAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => InvOccurValidateAsync(connection, transaction, pkColumns, keyValues, "INV_OCCUR_INIT_D", false, token);

    /// <summary>
    /// 生产链单据 AfterSave 通用明细校验：按（类型/单号）限定明细表，逐条检查，返回首个失败。
    /// 等价旧 SP 的游标+RAISERROR 模式（最多列 10 个序号）。
    /// </summary>
    private static async Task<SprocResult> ValidateDetailAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues,
        string detailTable, string typeColumn, string noColumn,
        IReadOnlyList<(string WhereClause, string Message)> checks, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "单据领域规则缺少主键。");
        if (detailTable.Length == 0 || detailTable.Length > 64 || !detailTable.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return new(false, "明细表名非法。");
        if (typeColumn.Length == 0 || typeColumn.Length > 64 || !typeColumn.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return new(false, "类型列名非法。");
        if (noColumn.Length == 0 || noColumn.Length > 64 || !noColumn.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return new(false, "单号列名非法。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        foreach (var (whereClause, message) in checks)
        {
            await using var cmd = new SqlCommand($"""
                SELECT TOP 11 SERIAL_NO FROM dbo.[{detailTable}] t
                WHERE t.[{typeColumn}]=@Type AND t.[{noColumn}]=@No AND {whereClause} ORDER BY SERIAL_NO;
                """, connection, transaction);
            cmd.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            cmd.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            await using var reader = await cmd.ExecuteReaderAsync(token);
            var lines = new List<string>();
            while (await reader.ReadAsync(token)) lines.Add(Convert.ToInt32(reader.GetValue(0)).ToString());
            if (lines.Count > 0) return new(false, message + "\r\n" + string.Join("\r\n", lines.Take(10)));
        }
        return new(true, null);
    }

    /// <summary>模块是否启用 ERROR_NO_SAVE（旧 SP 决定是否执行 P_*_CHECK 的开关）。</summary>
    private static async Task<bool> HasErrorNoSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId, CancellationToken token)
    {
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 1 FROM dbo.MODULES WHERE M_IDX=@ModuleId AND ERROR_NO_SAVE=1;", connection, transaction);
        cmd.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return await cmd.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>生产领料单（P_MOC_GET）AfterSave：库别/产品/批号校验（旧 SP 合并逻辑已注释，有效代码仅校验）。</summary>
    private static Task<SprocResult> MocGetAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "MOC_GET_D", "GET_TYPE", "GET_NO",
            [
                ("NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=t.DEPOT_ID)", "以下序号项库别编号不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);

    /// <summary>制令单（P_MOC_PRODUCE）AfterSave：CHECK 分支 + 订单存在 + 明细产品存在 + 明细订单号回填。</summary>
    private static async Task<SprocResult> MocProduceAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "制令单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (await HasErrorNoSaveAsync(connection, transaction, moduleId, token))
        {
            // 等价 P_MOC_PRODUCE_CHECK（SYSSS 开关门控）
            if (await ExistsAsync(connection, transaction,
                "SELECT TOP 1 1 FROM dbo.SYSSS WHERE PRODUCE_ORDER_TAG=1;", type, no, token)
                && await ExistsAsync(connection, transaction,
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
                "SELECT TOP 1 1 FROM dbo.SYSSS WHERE PRODUCE_PLAN_MOC_TAG=1;", type, no, token)
                && await ExistsAsync(connection, transaction,
                """
                SELECT TOP 1 1 FROM dbo.MOC_PLAN_MOC od
                INNER JOIN dbo.MOC_PRODUCE_M pr
                  ON pr.PLAN_TYPE=od.PLAN_TYPE AND pr.PLAN_NO=od.PLAN_NO AND pr.PLAN_SERIAL_NO=od.SERIAL_NO
                WHERE pr.PRODUCE_TYPE=@Type AND pr.PRODUCE_NO=@No
                  AND od.REQUIRE_QTY < ISNULL(od.PRODUCE_QTY,0)+ISNULL(pr.QTY,0);
                """, type, no, token))
                return new(false, "生产数量超出生产计划数量");
        }
        await using (var order = new SqlCommand("""
            SELECT TOP 1 1 FROM dbo.MOC_PRODUCE_M m
            WHERE m.PRODUCE_TYPE=@Type AND m.PRODUCE_NO=@No AND ISNULL(m.ORDER_NO,'')<>''
              AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_D d
                              WHERE d.ORDER_TYPE=m.ORDER_TYPE AND d.ORDER_NO=m.ORDER_NO AND d.SERIAL_NO=m.ORDER_SERIAL_NO);
            """, connection, transaction))
        {
            order.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            order.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            if (await order.ExecuteScalarAsync(token) is not null)
                return new(false, "订单不存在  \r\n");
        }
        var product = await ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "MOC_PRODUCE_D", "PRODUCE_TYPE", "PRODUCE_NO",
            [("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 ")], token);
        if (!product.Success) return product;
        await using (var backfill = new SqlCommand("""
            UPDATE d SET d.ORDER_TYPE=m.ORDER_TYPE, d.ORDER_NO=m.ORDER_NO, d.ORDER_SERIAL_NO=m.ORDER_SERIAL_NO
            FROM dbo.MOC_PRODUCE_D d INNER JOIN dbo.MOC_PRODUCE_M m
              ON m.PRODUCE_TYPE=d.PRODUCE_TYPE AND m.PRODUCE_NO=d.PRODUCE_NO
            WHERE d.PRODUCE_TYPE=@Type AND d.PRODUCE_NO=@No;
            """, connection, transaction))
        {
            backfill.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            backfill.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            await backfill.ExecuteNonQueryAsync(token);
        }
        return new(true, null);
    }

    /// <summary>生产入库单（P_MOC_PRODUCT_IN）AfterSave：CHECK 分支 + 制令/库别/产品/批号校验。</summary>
    private static async Task<SprocResult> MocProductInAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "生产入库领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (await HasErrorNoSaveAsync(connection, transaction, moduleId, token))
        {
            // 等价 P_MOC_PRODUCT_IN_CHECK：按制令聚合的入库量不能超制令生产量
            var exceeded = await ReadStringsAsync(connection, transaction,
                """
                SELECT TOP 11 t.PRODUCE_NO FROM
                (SELECT PRODUCE_TYPE, PRODUCE_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                 FROM dbo.MOC_PRODUCT_IN_D
                 WHERE PRODUCT_IN_TYPE=@Type AND PRODUCT_IN_NO=@No GROUP BY PRODUCE_TYPE, PRODUCE_NO) t
                INNER JOIN dbo.MOC_PRODUCE_M m ON m.PRODUCE_TYPE=t.PRODUCE_TYPE AND m.PRODUCE_NO=t.PRODUCE_NO
                WHERE (t.QTY+ISNULL(m.FINISHED_QTY,0)+ISNULL(m.SCRAP_IN_QTY,0) > ISNULL(m.QTY,0)
                    OR t.SPARE_QTY+ISNULL(m.FINISHED_SPARE_QTY,0)+ISNULL(m.SCRAP_IN_SPARE_QTY,0) > ISNULL(m.SPARE_QTY,0));
                """, type, no, token);
            if (exceeded.Count > 0)
                return new(false, "以下生产单入库数量超出制令生产 \r\n" + string.Join("  ", exceeded.Take(10)));
        }
        return await ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "MOC_PRODUCT_IN_D", "PRODUCT_IN_TYPE", "PRODUCT_IN_NO",
            [
                ("NOT EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_M c WHERE c.PRODUCE_TYPE=t.PRODUCE_TYPE AND c.PRODUCE_NO=t.PRODUCE_NO)", "以下序号项制令单不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=t.DEPOT_ID)", "以下序号项库别编号不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);
    }

    /// <summary>生产出库单（P_MOC_PRODUCT_OUT）AfterSave：CHECK 分支 + 制令/库别/产品/批号校验。</summary>
    private static async Task<SprocResult> MocProductOutAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "生产出库领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (await HasErrorNoSaveAsync(connection, transaction, moduleId, token))
        {
            // 等价 P_MOC_PRODUCT_OUT_CHECK：FITOUT_TAG=1 走制令可出库，否则走订单可出库
            var fitout = await ExistsAsync(connection, transaction,
                "SELECT TOP 1 1 FROM dbo.SYSSS WHERE FITOUT_TAG=1;", type, no, token);
            var exceeded = await ReadStringsAsync(connection, transaction, fitout
                ? """
                  SELECT TOP 11 t.PRODUCE_NO FROM
                  (SELECT PRODUCE_TYPE, PRODUCE_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                   FROM dbo.MOC_PRODUCT_OUT_D
                   WHERE PRODUCT_OUT_TYPE=@Type AND PRODUCT_OUT_NO=@No GROUP BY PRODUCE_TYPE, PRODUCE_NO) t
                  INNER JOIN dbo.MOC_PRODUCE_M m ON m.PRODUCE_TYPE=t.PRODUCE_TYPE AND m.PRODUCE_NO=t.PRODUCE_NO
                  WHERE (t.QTY+ISNULL(m.FINISHED_FITOUT_QTY,0) > ISNULL(m.FINISHED_QTY,0)
                      OR t.SPARE_QTY+ISNULL(m.FINISHED_FITOUT_SPARE_QTY,0) > ISNULL(m.FINISHED_SPARE_QTY,0));
                  """
                : """
                  SELECT TOP 11 t.PRODUCE_NO FROM
                  (SELECT PRODUCE_TYPE, PRODUCE_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                   FROM dbo.MOC_PRODUCT_OUT_D
                   WHERE PRODUCT_OUT_TYPE=@Type AND PRODUCT_OUT_NO=@No GROUP BY PRODUCE_TYPE, PRODUCE_NO) t
                  INNER JOIN dbo.MOC_PRODUCE_M m ON m.PRODUCE_TYPE=t.PRODUCE_TYPE AND m.PRODUCE_NO=t.PRODUCE_NO
                  WHERE (t.QTY+ISNULL(m.FINISHED_SEND_QTY,0) > ISNULL(m.FINISHED_QTY,0)
                      OR t.SPARE_QTY+ISNULL(m.FINISHED_SEND_SPARE_QTY,0) > ISNULL(m.FINISHED_SPARE_QTY,0));
                  """, type, no, token);
            if (exceeded.Count > 0)
                return new(false, (fitout ? "以下生产单出库数量超出制令可出库 \r\n" : "以下生产单出库数量超出订单可出库 \r\n")
                    + string.Join("  ", exceeded.Take(10)));
        }
        return await ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "MOC_PRODUCT_OUT_D", "PRODUCT_OUT_TYPE", "PRODUCT_OUT_NO",
            [
                ("NOT EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_M c WHERE c.PRODUCE_TYPE=t.PRODUCE_TYPE AND c.PRODUCE_NO=t.PRODUCE_NO)", "以下序号项制令单不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=t.DEPOT_ID)", "以下序号项库别编号不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);
    }

    /// <summary>产品制程（P_SFC_PROCESS）AfterSave：产品存在 + 固定时间不能为 0。</summary>
    private static async Task<SprocResult> SfcProcessAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 1 || keyValues.Count < 1) return new(false, "产品制程领域规则缺少主键。");
        var proNo = (keyValues[0] ?? string.Empty).Trim();
        await using (var product = new SqlCommand(
            "SELECT TOP 1 1 FROM dbo.SFC_PROCESS_M t INNER JOIN dbo.PRODUCT p ON p.PRO_NO=t.PRO_NO WHERE t.PRO_NO=@ProNo;",
            connection, transaction))
        {
            product.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
            if (await product.ExecuteScalarAsync(token) is null)
                return new(false, "产品编号不存在 \r\n");
        }
        await using (var std = new SqlCommand("""
            SELECT TOP 1 1 FROM dbo.SFC_PROCESS_D t
            WHERE t.PRO_NO=@ProNo AND t.STANDARD_TIME_TAG=1 AND ISNULL(t.STANDARD_TIME,0)=0;
            """, connection, transaction))
        {
            std.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
            if (await std.ExecuteScalarAsync(token) is not null)
                return new(false, "产品编号使用固定时间时，固定时间不能为0 \r\n");
        }
        return new(true, null);
    }

    /// <summary>生产记录单（P_SFC_DAILY）AfterSave：制令制程存在 + 不超制程允许生产最大数量。</summary>
    private static async Task<SprocResult> SfcDailyAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "生产记录单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        var process = await ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "SFC_DAILY_D", "DAILY_TYPE", "DAILY_NO",
            [("NOT EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_PROCESS_D c WHERE c.PRODUCE_TYPE=t.PRODUCE_TYPE AND c.PRODUCE_NO=t.PRODUCE_NO AND c.PROCEDURE_ID=t.PROCEDURE_ID)", "以下序号项制令制程不存在 ")], token);
        if (!process.Success) return process;
        await using (var qty = new SqlCommand("""
            SELECT TOP 11 d.SERIAL_NO
            FROM dbo.SFC_DAILY_D d
            JOIN (SELECT d2.PRODUCE_TYPE, d2.PRODUCE_NO, d2.PROCEDURE_ID,
                         MAX(ISNULL(p.PROCESS_OVER_QTY,0)) PROCESS_OVER_QTY,
                         MAX(ISNULL(p.FINISHED_PLAN_QTY,0)) FINISHED_PLAN_QTY,
                         SUM(ISNULL(d2.FINISHED_QTY,0)) DAILY_QTY
                  FROM dbo.SFC_DAILY_D d2
                  JOIN dbo.MOC_PRODUCE_PROCESS_D p
                    ON p.PRODUCE_TYPE=d2.PRODUCE_TYPE AND p.PRODUCE_NO=d2.PRODUCE_NO AND p.PROCEDURE_ID=d2.PROCEDURE_ID
                  WHERE d2.DAILY_TYPE=@Type AND d2.DAILY_NO=@No
                  GROUP BY d2.PRODUCE_TYPE, d2.PRODUCE_NO, d2.PROCEDURE_ID) g
              ON g.PRODUCE_TYPE=d.PRODUCE_TYPE AND g.PRODUCE_NO=d.PRODUCE_NO AND g.PROCEDURE_ID=d.PROCEDURE_ID
            WHERE d.DAILY_TYPE=@Type AND d.DAILY_NO=@No
              AND g.PROCESS_OVER_QTY < g.FINISHED_PLAN_QTY + g.DAILY_QTY
            ORDER BY d.SERIAL_NO;
            """, connection, transaction))
        {
            qty.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            qty.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            await using var reader = await qty.ExecuteReaderAsync(token);
            var lines = new List<string>();
            while (await reader.ReadAsync(token)) lines.Add(Convert.ToInt32(reader.GetValue(0)).ToString());
            if (lines.Count > 0)
                return new(false, "以下序号项数量超过制令制程允许生产最大数量 \r\n" + string.Join("\r\n", lines.Take(10)));
        }
        return new(true, null);
    }

    /// <summary>客户退货单（P_COP_RETURN）AfterSave：客户相符/订单存在/订单序号产品相符/产品/批号校验。</summary>
    private static Task<SprocResult> CopReturnAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "COP_RETURN_D", "RETURN_TYPE", "RETURN_NO",
            [
                ("EXISTS (SELECT 1 FROM dbo.COP_ORDER_M o JOIN dbo.COP_RETURN_M m ON m.RETURN_TYPE=@Type AND m.RETURN_NO=@No WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO AND o.CLIENT_ID<>m.CLIENT_ID)", "以下序号项退货单与订单客户不符 "),
                ("ISNULL(t.ORDER_TYPE,'')<>'' AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_M o WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO)", "以下序号项订单不存在 "),
                ("ISNULL(t.ORDER_TYPE,'')<>'' AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_D o WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO AND o.SERIAL_NO=t.ORDER_SERIAL_NO AND o.PRO_NO=t.PRO_NO)", "以下序号项订单序号与产品编号不相符 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);

    /// <summary>备货单（P_COP_FITOUT）AfterSave：CHECK 分支（SYSSS 门控）+ 客户/订单/产品/批号校验。</summary>
    private static async Task<SprocResult> CopFitoutAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "备货单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (await HasErrorNoSaveAsync(connection, transaction, moduleId, token))
        {
            var check = await CopFitoutCheckAsync(connection, transaction, type, no, token);
            if (check is not null) return check;
        }
        return await ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "COP_FITOUT_D", "FITOUT_TYPE", "FITOUT_NO",
            [
                ("EXISTS (SELECT 1 FROM dbo.COP_ORDER_M o JOIN dbo.COP_FITOUT_M m ON m.FITOUT_TYPE=@Type AND m.FITOUT_NO=@No WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO AND o.CLIENT_ID<>m.CLIENT_ID)", "以下序号项送货单与订单客户不符 "),
                ("ISNULL(t.ORDER_TYPE,'')<>'' AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_M o WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO)", "以下序号项订单不存在 "),
                ("ISNULL(t.ORDER_TYPE,'')<>'' AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_D o WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO AND o.SERIAL_NO=t.ORDER_SERIAL_NO AND o.PRO_NO=t.PRO_NO)", "以下序号项订单序号与产品编号不相符 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号! "),
            ], token);
    }

    /// <summary>备货返仓单（P_COP_FITIN）AfterSave：CHECK 分支（SYSSS 门控）+ 客户/订单/产品/批号校验。</summary>
    private static async Task<SprocResult> CopFitinAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "备货返仓单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (await HasErrorNoSaveAsync(connection, transaction, moduleId, token))
        {
            var check = await CopFitinCheckAsync(connection, transaction, type, no, token);
            if (check is not null) return check;
        }
        return await ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "COP_FITIN_D", "FITIN_TYPE", "FITIN_NO",
            [
                ("EXISTS (SELECT 1 FROM dbo.COP_ORDER_M o JOIN dbo.COP_FITIN_M m ON m.FITIN_TYPE=@Type AND m.FITIN_NO=@No WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO AND o.CLIENT_ID<>m.CLIENT_ID)", "以下序号项与订单客户不符 "),
                ("ISNULL(t.ORDER_TYPE,'')<>'' AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_M o WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO)", "以下序号项订单不存在 "),
                ("ISNULL(t.ORDER_TYPE,'')<>'' AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_D o WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO AND o.SERIAL_NO=t.ORDER_SERIAL_NO AND o.PRO_NO=t.PRO_NO)", "以下序号项订单序号与产品编号不相符 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);
    }

    /// <summary>客户退料单（P_COP_BACK）AfterSave：CHECK 分支（退料不超订单）+ 客户/订单/产品/批号校验。</summary>
    private static async Task<SprocResult> CopBackAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "客户退料单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (await HasErrorNoSaveAsync(connection, transaction, moduleId, token))
        {
            var check = await CopBackCheckAsync(connection, transaction, type, no, token);
            if (check is not null) return check;
        }
        return await ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "COP_BACK_D", "BACK_TYPE", "BACK_NO",
            [
                ("EXISTS (SELECT 1 FROM dbo.COP_ORDER_M o JOIN dbo.COP_BACK_M m ON m.BACK_TYPE=@Type AND m.BACK_NO=@No WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO AND o.CLIENT_ID<>m.CLIENT_ID)", "以下序号项退料单与订单客户不符 "),
                ("ISNULL(t.ORDER_TYPE,'')<>'' AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_M o WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO)", "以下序号项订单不存在 "),
                ("ISNULL(t.ORDER_TYPE,'')<>'' AND NOT EXISTS (SELECT 1 FROM dbo.COP_ORDER_D o WHERE o.ORDER_TYPE=t.ORDER_TYPE AND o.ORDER_NO=t.ORDER_NO AND o.SERIAL_NO=t.ORDER_SERIAL_NO AND o.PRO_NO=t.PRO_NO)", "以下序号项订单序号与产品编号不相符 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);
    }

    /// <summary>采购退料单（P_PUR_CANCEL）AfterSave：6 项引用校验 + 退料不超收料。</summary>
    private static async Task<SprocResult> PurCancelAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "采购退料单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        var validated = await ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "PUR_CANCEL_D", "CANCEL_TYPE", "CANCEL_NO",
            [
                ("EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_M o JOIN dbo.PUR_CANCEL_M m ON m.CANCEL_TYPE=@Type AND m.CANCEL_NO=@No WHERE o.PURCHASE_TYPE=t.PURCHASE_TYPE AND o.PURCHASE_NO=t.PURCHASE_NO AND o.SUPPLIER_ID<>m.SUPPLIER_ID)", "以下序号项收料单与采购单厂商不符 "),
                ("ISNULL(t.PURCHASE_TYPE,'')<>'' AND NOT EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_M o WHERE o.PURCHASE_TYPE=t.PURCHASE_TYPE AND o.PURCHASE_NO=t.PURCHASE_NO)", "以下序号项采购订单不存在 "),
                ("ISNULL(t.PURCHASE_TYPE,'')<>'' AND NOT EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_D o WHERE o.PURCHASE_TYPE=t.PURCHASE_TYPE AND o.PURCHASE_NO=t.PURCHASE_NO AND o.SERIAL_NO=t.PURCHASE_SERIAL_NO AND o.PRO_NO=t.PRO_NO)", "以下序号项采购订单序号与产品编号不相符 "),
                ("ISNULL(t.RECEIVE_TYPE,'')<>'' AND NOT EXISTS (SELECT 1 FROM dbo.PUR_RECEIVE_D o WHERE o.RECEIVE_TYPE=t.RECEIVE_TYPE AND o.RECEIVE_NO=t.RECEIVE_NO AND o.SERIAL_NO=t.RECEIVE_SERIAL_NO AND o.PRO_NO=t.PRO_NO)", "以下序号项送货单序号与产品编号不相符 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);
        if (!validated.Success) return validated;
        var qtyCheck = await PurCancelQtyCheckAsync(connection, transaction, type, no, token);
        return qtyCheck ?? new(true, null);
    }

    /// <summary>P_COP_BACK_CHECK 内联：退料不超订单数量。</summary>
    private static async Task<SprocResult?> CopBackCheckAsync(
        SqlConnection connection, SqlTransaction transaction, string type, string no, CancellationToken token)
    {
        var rows = await FindLinesAsync(connection, transaction,
            """
            SELECT od.ORDER_NO, od.QTY, od.FINISHED_SEND_QTY, od.BACK_MATERIAL, od.BACK_BAD, sd.QTY
            FROM dbo.COP_ORDER_D od
            INNER JOIN (SELECT ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                        FROM dbo.COP_BACK_D WHERE BACK_TYPE=@Type AND BACK_NO=@No
                        GROUP BY ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO) sd
              ON od.ORDER_TYPE=sd.ORDER_TYPE AND od.ORDER_NO=sd.ORDER_NO AND od.SERIAL_NO=sd.ORDER_SERIAL_NO
            WHERE ISNULL(od.FINISHED_SEND_QTY,0)+ISNULL(od.BACK_MATERIAL,0)+ISNULL(od.BACK_BAD,0)+sd.QTY > od.QTY;
            """, type, no, token,
            line: r => $"{r.GetString(0).Trim()}    {Convert.ToString(r.GetValue(1))}    {Convert.ToString(r.GetValue(2))}    {Convert.ToString(r.GetValue(3))}    {Convert.ToString(r.GetValue(4))}    {Convert.ToString(r.GetValue(5))}");
        return rows is null
            ? null
            : new(false, "以下退料已超出订单数量\r\n 订单单号  订单数量  已送数量  已退数量  已退次品  单据数量\r\n" + rows);
    }

    /// <summary>P_COP_FITOUT_CHECK 内联：已备货不超订单/工单完工数量（SYSSS 门控）。</summary>
    private static async Task<SprocResult?> CopFitoutCheckAsync(
        SqlConnection connection, SqlTransaction transaction, string type, string no, CancellationToken token)
    {
        if (await ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.SYSSS WHERE FITOUT_ORDER_TAG=1;", type, no, token))
        {
            var rows = await FindLinesAsync(connection, transaction,
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
        if (await ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.SYSSS WHERE FITOUT_PRODUCE_TAG=1;", type, no, token))
        {
            var rows = await FindLinesAsync(connection, transaction,
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
    private static async Task<SprocResult?> CopFitinCheckAsync(
        SqlConnection connection, SqlTransaction transaction, string type, string no, CancellationToken token)
    {
        // 1. 返仓不超备货
        var rows = await FindLinesAsync(connection, transaction,
            """
            SELECT od.FITOUT_NO, od.QTY, od.FINISHED_QTY, od.RETURN_QTY, sd.QTY, od.SPARE_QTY, od.FINISHED_SPARE_QTY, od.RETURN_SPARE_QTY, sd.SPARE_QTY
            FROM dbo.COP_FITOUT_D od
            INNER JOIN (SELECT FITOUT_TYPE, FITOUT_NO, FITOUT_SERIAL_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                        FROM dbo.COP_FITIN_D WHERE FITIN_TYPE=@Type AND FITIN_NO=@No
                        GROUP BY FITOUT_TYPE, FITOUT_NO, FITOUT_SERIAL_NO) sd
              ON od.FITOUT_TYPE=sd.FITOUT_TYPE AND od.FITOUT_NO=sd.FITOUT_NO AND od.SERIAL_NO=sd.FITOUT_SERIAL_NO
            WHERE (ISNULL(od.QTY,0)-ISNULL(od.FINISHED_QTY,0)-ISNULL(od.RETURN_QTY,0) < sd.QTY
                OR ISNULL(od.SPARE_QTY,0)-ISNULL(od.FINISHED_SPARE_QTY,0)-ISNULL(od.RETURN_SPARE_QTY,0)-sd.SPARE_QTY < 0);
            """, type, no, token,
            line: r => string.Join("    ", Enumerable.Range(0, r.FieldCount).Select(i => (Convert.ToString(r.GetValue(i)) ?? string.Empty).Trim())));
        if (rows is not null)
            return new(false, "以下返仓已超出备货单数量\r\n备货单号   数量  已送货   已返仓数量  单据数量  备品  已送备品  已返仓备品  单据备品\r\n" + rows);
        // 2-4. 送货/调拨不超备货（SYSSS 门控）
        if (await ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.SYSSS WHERE FITOUT_ORDER_TAG=1;", type, no, token))
        {
            rows = await FindLinesAsync(connection, transaction,
                """
                SELECT od.ORDER_NO, od.FINISHED_FITOUT_QTY, od.FINISHED_SEND_QTY, sd.QTY, od.FINISHED_FITOUT_SPARE_QTY, od.FINISHED_SPARE_QTY, sd.SPARE_QTY
                FROM dbo.COP_ORDER_D od
                INNER JOIN (SELECT ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                            FROM dbo.COP_FITIN_D WHERE FITIN_TYPE=@Type AND FITIN_NO=@No
                            GROUP BY ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO) sd
                  ON od.ORDER_TYPE=sd.ORDER_TYPE AND od.ORDER_NO=sd.ORDER_NO AND od.SERIAL_NO=sd.ORDER_SERIAL_NO
                WHERE (ISNULL(od.FINISHED_FITOUT_QTY,0)-sd.QTY < ISNULL(od.FINISHED_SEND_QTY,0)
                    OR ISNULL(od.FINISHED_FITOUT_SPARE_QTY,0)-sd.SPARE_QTY < ISNULL(od.FINISHED_SPARE_QTY,0));
                """, type, no, token,
                line: r => string.Join("    ", Enumerable.Range(0, r.FieldCount).Select(i => (Convert.ToString(r.GetValue(i)) ?? string.Empty).Trim())));
            if (rows is not null)
                return new(false, "以下会出现已送货数量超出已备货数量\r\n订单号  备货数量  已送货  单据数量  备货备品  已送备品  单据备品\r\n" + rows);
        }
        if (await ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.SYSSS WHERE FITOUT_PRODUCE_TAG=1;", type, no, token))
        {
            rows = await FindLinesAsync(connection, transaction,
                """
                SELECT od.PRODUCE_NO, od.FINISHED_FITOUT_QTY, od.FINISHED_SEND_QTY, sd.QTY, od.FINISHED_FITOUT_SPARE_QTY, od.FINISHED_SEND_SPARE_QTY, sd.SPARE_QTY
                FROM dbo.MOC_PRODUCE_M od
                INNER JOIN (SELECT PRODUCE_TYPE, PRODUCE_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                            FROM dbo.COP_FITIN_D WHERE FITIN_TYPE=@Type AND FITIN_NO=@No
                            GROUP BY PRODUCE_TYPE, PRODUCE_NO) sd
                  ON od.PRODUCE_TYPE=sd.PRODUCE_TYPE AND od.PRODUCE_NO=sd.PRODUCE_NO
                WHERE (ISNULL(od.FINISHED_FITOUT_QTY,0)-sd.QTY < ISNULL(od.FINISHED_SEND_QTY,0)
                    OR ISNULL(od.FINISHED_FITOUT_SPARE_QTY,0)-sd.SPARE_QTY < ISNULL(od.FINISHED_SEND_SPARE_QTY,0));
                """, type, no, token,
                line: r => string.Join("    ", Enumerable.Range(0, r.FieldCount).Select(i => (Convert.ToString(r.GetValue(i)) ?? string.Empty).Trim())));
            if (rows is not null)
                return new(false, "以下会出现已送货数量超出已备货数量\r\n工单号  备货数量  已送货  单据数量  备货备品  已送备品  单据备品\r\n" + rows);
        }
        if (await ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.SYSSS WHERE FITOUT_PRODUCE_TRANSFER_TAG=1;", type, no, token))
        {
            rows = await FindLinesAsync(connection, transaction,
                """
                SELECT od.PRODUCE_NO, od.FINISHED_FITOUT_QTY, od.FINISHED_TRANSFER_QTY, sd.QTY, od.FINISHED_FITOUT_SPARE_QTY, od.FINISHED_TRANSFER_SPARE_QTY, sd.SPARE_QTY
                FROM dbo.MOC_PRODUCE_M od
                INNER JOIN (SELECT PRODUCE_TYPE, PRODUCE_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
                            FROM dbo.COP_FITIN_D WHERE FITIN_TYPE=@Type AND FITIN_NO=@No
                            GROUP BY PRODUCE_TYPE, PRODUCE_NO) sd
                  ON od.PRODUCE_TYPE=sd.PRODUCE_TYPE AND od.PRODUCE_NO=sd.PRODUCE_NO
                WHERE (ISNULL(od.FINISHED_FITOUT_QTY,0)-sd.QTY < ISNULL(od.FINISHED_TRANSFER_QTY,0)
                    OR ISNULL(od.FINISHED_FITOUT_SPARE_QTY,0)-sd.SPARE_QTY < ISNULL(od.FINISHED_TRANSFER_SPARE_QTY,0));
                """, type, no, token,
                line: r => string.Join("    ", Enumerable.Range(0, r.FieldCount).Select(i => (Convert.ToString(r.GetValue(i)) ?? string.Empty).Trim())));
            if (rows is not null)
                return new(false, "以下会出现已调拔数量超出已备货数量\r\n工单号  备货数量  已调拔  单据数量  备货备品  已调拔备品  单据备品\r\n" + rows);
        }
        return null;
    }

    /// <summary>P_PUR_CANCEL 退料不超收料（旧 SP 展示「已收=退货」为显示 bug，此处按意图展示 RECEIVE）。</summary>
    private static async Task<SprocResult?> PurCancelQtyCheckAsync(
        SqlConnection connection, SqlTransaction transaction, string type, string no, CancellationToken token)
    {
        var rows = await FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO,
                   SUM(CASE WHEN p.SRC='P' THEN p.QTY END) PUR_QTY,
                   SUM(CASE WHEN p.SRC='R' THEN p.QTY END) REC_QTY,
                   SUM(CASE WHEN p.SRC='C' THEN p.QTY END) RET_QTY,
                   SUM(CASE WHEN p.SRC='P' THEN p.SPARE_QTY END) PUR_SPARE_QTY,
                   SUM(CASE WHEN p.SRC='R' THEN p.SPARE_QTY END) REC_SPARE_QTY,
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
            HAVING SUM(CASE WHEN p.SRC='C' THEN p.QTY END) > SUM(CASE WHEN p.SRC='R' THEN p.QTY END)
                OR SUM(CASE WHEN p.SRC='C' THEN p.SPARE_QTY END) > SUM(CASE WHEN p.SRC='R' THEN p.SPARE_QTY END);
            """, type, no, token,
            line: r => string.Join("    ", Enumerable.Range(0, r.FieldCount).Select(i => (Convert.ToString(r.GetValue(i)) ?? string.Empty).Trim())));
        return rows is null
            ? null
            : new(false, "以下序号项退料数量大于收料\r\n 序号  采购数量   已收  退料   采购备品   已收备品  退备品\r\n" + rows);
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
