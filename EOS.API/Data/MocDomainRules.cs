using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Manufacturing domain rules executed after module saves (work orders, work-in moves, produce transactions, plan/BOM and process changes). Methods run inside the caller's transaction.
/// </summary>
public static class MocDomainRules
{

    /// <summary>生产领料单（P_MOC_GET）AfterSave：库别/产品/批号校验（旧 SP 合并逻辑已注释，有效代码仅校验）。</summary>
    public static Task<SprocResult> MocGetAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "MOC_GET_D", "GET_TYPE", "GET_NO",
            [
                ("NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=t.DEPOT_ID)", "以下序号项库别编号不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);

    /// <summary>制令单（P_MOC_PRODUCE）AfterSave：CHECK 分支 + 订单存在 + 明细产品存在 + 明细订单号回填。</summary>


    /// <summary>制令单（P_MOC_PRODUCE）AfterSave：CHECK 分支 + 订单存在 + 明细产品存在 + 明细订单号回填。</summary>
    public static async Task<SprocResult> MocProduceAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "制令单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (await DomainRuleService.HasErrorNoSaveAsync(connection, transaction, moduleId, token))
        {
            // 等价 P_MOC_PRODUCE_CHECK（SYSSS 开关门控）
            if (await DomainRuleService.ExistsAsync(connection, transaction,
                "SELECT TOP 1 1 FROM dbo.SYSSS WHERE PRODUCE_ORDER_TAG=1;", type, no, token)
                && await DomainRuleService.ExistsAsync(connection, transaction,
                """
                SELECT TOP 1 1 FROM dbo.COP_ORDER_D od
                INNER JOIN dbo.MOC_PRODUCE_M pr
                  ON pr.ORDER_TYPE=od.ORDER_TYPE AND pr.ORDER_NO=od.ORDER_NO AND pr.ORDER_SERIAL_NO=od.SERIAL_NO
                WHERE pr.PRODUCE_TYPE=@Type AND pr.PRODUCE_NO=@No
                  AND (od.PLAN_QTY < ISNULL(od.FINISHED_PLAN_QTY,0)+ISNULL(pr.QTY,0)
                    OR od.PLAN_SPARE_QTY < ISNULL(od.FINISHED_PLAN_SPARE_QTY,0)+ISNULL(pr.SPARE_QTY,0));
                """, type, no, token))
                return new(false, "生产数量或备品生产数量超出订单数量");
            if (await DomainRuleService.ExistsAsync(connection, transaction,
                "SELECT TOP 1 1 FROM dbo.SYSSS WHERE PRODUCE_PLAN_MOC_TAG=1;", type, no, token)
                && await DomainRuleService.ExistsAsync(connection, transaction,
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
        var product = await DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
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


    /// <summary>生产入库单（P_MOC_PRODUCT_IN）AfterSave：CHECK 分支 + 制令/库别/产品/批号校验。</summary>
    public static async Task<SprocResult> MocProductInAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "生产入库领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (await DomainRuleService.HasErrorNoSaveAsync(connection, transaction, moduleId, token))
        {
            // 等价 P_MOC_PRODUCT_IN_CHECK：按制令聚合的入库量不能超制令生产量
            var exceeded = await DomainRuleService.ReadStringsAsync(connection, transaction,
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
        return await DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "MOC_PRODUCT_IN_D", "PRODUCT_IN_TYPE", "PRODUCT_IN_NO",
            [
                ("NOT EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_M c WHERE c.PRODUCE_TYPE=t.PRODUCE_TYPE AND c.PRODUCE_NO=t.PRODUCE_NO)", "以下序号项制令单不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=t.DEPOT_ID)", "以下序号项库别编号不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);
    }

    /// <summary>生产出库单（P_MOC_PRODUCT_OUT）AfterSave：CHECK 分支 + 制令/库别/产品/批号校验。</summary>


    /// <summary>生产出库单（P_MOC_PRODUCT_OUT）AfterSave：CHECK 分支 + 制令/库别/产品/批号校验。</summary>
    public static async Task<SprocResult> MocProductOutAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "生产出库领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (await DomainRuleService.HasErrorNoSaveAsync(connection, transaction, moduleId, token))
        {
            // 等价 P_MOC_PRODUCT_OUT_CHECK：FITOUT_TAG=1 走制令可出库，否则走订单可出库
            var fitout = await DomainRuleService.ExistsAsync(connection, transaction,
                "SELECT TOP 1 1 FROM dbo.SYSSS WHERE FITOUT_TAG=1;", type, no, token);
            var exceeded = await DomainRuleService.ReadStringsAsync(connection, transaction, fitout
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
        return await DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "MOC_PRODUCT_OUT_D", "PRODUCT_OUT_TYPE", "PRODUCT_OUT_NO",
            [
                ("NOT EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_M c WHERE c.PRODUCE_TYPE=t.PRODUCE_TYPE AND c.PRODUCE_NO=t.PRODUCE_NO)", "以下序号项制令单不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.DEPOT c WHERE c.DEPOT_ID=t.DEPOT_ID)", "以下序号项库别编号不存在 "),
                ("NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO)", "以下序号项产品编号不存在 "),
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);
    }


    /// <summary>工单BOM（P_MOC_BOM_STRU）AfterSave：孤儿主/明细清理循环（等价旧 SP 的 while 循环）。</summary>
    public static async Task<SprocResult> MocBomStruAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 3 || keyValues.Count < 3) return new(false, "工单BOM领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        var proNo = (keyValues[2] ?? string.Empty).Trim();
        string? rootProNo;
        await using (var read = new SqlCommand(
            "SELECT TOP 1 LTRIM(RTRIM(ISNULL(PRO_NO,''))) FROM dbo.MOC_PRODUCE_M WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No;",
            connection, transaction))
        {
            read.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            read.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            rootProNo = (string?)await read.ExecuteScalarAsync(token);
        }
        rootProNo ??= string.Empty;
        while (true)
        {
            await using var delM = new SqlCommand("""
                DELETE m FROM dbo.MOC_BOM_STRU_M m
                WHERE m.PRODUCE_TYPE=@Type AND m.PRODUCE_NO=@No AND m.PRO_NO<>@ProNo AND m.PRO_NO<>@RootProNo
                  AND m.PRO_NO NOT IN (SELECT ELEMENT_PRO_NO FROM dbo.MOC_BOM_STRU_D WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No);
                """, connection, transaction);
            delM.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            delM.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            delM.Parameters.Add("@ProNo", SqlDbType.NChar, 30).Value = proNo;
            delM.Parameters.Add("@RootProNo", SqlDbType.NChar, 30).Value = rootProNo;
            var mRows = await delM.ExecuteNonQueryAsync(token);
            await using var delD = new SqlCommand("""
                DELETE d FROM dbo.MOC_BOM_STRU_D d
                WHERE d.PRODUCE_TYPE=@Type AND d.PRODUCE_NO=@No
                  AND d.PRO_NO NOT IN (SELECT PRO_NO FROM dbo.MOC_BOM_STRU_M WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No);
                """, connection, transaction);
            delD.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            delD.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            var dRows = await delD.ExecuteNonQueryAsync(token);
            if (mRows == 0 && dRows == 0) break;
        }
        return new(true, null);
    }

    /// <summary>生产计划（P_MOC_PLAN）AfterSave：ERROR_NO_SAVE 门控的生产计划不超订单检查。</summary>


    /// <summary>生产计划（P_MOC_PLAN）AfterSave：ERROR_NO_SAVE 门控的生产计划不超订单检查。</summary>
    public static async Task<SprocResult> MocPlanAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "生产计划领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (!await DomainRuleService.HasErrorNoSaveAsync(connection, transaction, moduleId, token))
            return new(true, null);
        // 等价 P_MOC_PLAN_CHECK：计划数量/备品不超订单剩余
        var rows = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT a.SERIAL_NO, b.QTY, b.DO_PLAN_QTY, a.QTY, b.SPARE_QTY, b.DO_PLAN_SPARE_QTY, a.SPARE_QTY
            FROM dbo.MOC_PLAN_D a
            INNER JOIN dbo.COP_ORDER_D b ON b.ORDER_TYPE=a.ORDER_TYPE AND b.ORDER_NO=a.ORDER_NO AND b.SERIAL_NO=a.ORDER_SERIAL_NO
            WHERE a.PLAN_TYPE=@Type AND a.PLAN_NO=@No
              AND (a.QTY > ISNULL(b.QTY,0)-ISNULL(b.DO_PLAN_QTY,0)
                OR a.SPARE_QTY > ISNULL(b.QTY,0)-ISNULL(b.DO_PLAN_SPARE_QTY,0));
            """, type, no, token,
            line: r => string.Join("    ", Enumerable.Range(0, r.FieldCount).Select(i => (Convert.ToString(r.GetValue(i)) ?? string.Empty).Trim())));
        return rows is null
            ? new(true, null)
            : new(false, "以下序号项生产计划超出订单\r\n序号  订单数量  已计划数  本次数量  订单备品  已计划备品  本次备品\r\n" + rows);
    }

    /// <summary>工单制程（P_MOC_PRODUCE_PROCESS）AfterSave：制令存在校验。</summary>


    /// <summary>工单制程（P_MOC_PRODUCE_PROCESS）AfterSave：制令存在校验。</summary>
    public static async Task<SprocResult> MocProduceProcessAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "工单制程领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 1 FROM dbo.MOC_PRODUCE_PROCESS_M t INNER JOIN dbo.MOC_PRODUCE_M c ON c.PRODUCE_TYPE=t.PRODUCE_TYPE AND c.PRODUCE_NO=t.PRODUCE_NO WHERE t.PRODUCE_TYPE=@Type AND t.PRODUCE_NO=@No;",
            connection, transaction);
        cmd.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
        cmd.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
        return await cmd.ExecuteScalarAsync(token) is null
            ? new(false, "制令单不存在。 ")
            : new(true, null);
    }

    /// <summary>工序发料单（P_MOC_WORK_OUT）AfterSave：ERROR_NO_SAVE 门控的出库不超工序入库检查。</summary>


    /// <summary>工序发料单（P_MOC_WORK_OUT）AfterSave：ERROR_NO_SAVE 门控的出库不超工序入库检查。</summary>
    public static async Task<SprocResult> MocWorkOutAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "工序发料领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (!await DomainRuleService.HasErrorNoSaveAsync(connection, transaction, moduleId, token))
            return new(true, null);
        var rows = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT od.WORK_TYPE, od.WORK_NO, od.PROCESS_QTY, od.FINISHED_OUT_QTY, sd.QTY
            FROM dbo.MOC_WORK_D od
            INNER JOIN dbo.MOC_WORK_OUT_D sd ON sd.WORK_TYPE=od.WORK_TYPE AND sd.WORK_NO=od.WORK_NO AND sd.WORK_SERIAL_NO=od.SERIAL_NO
            WHERE sd.WORK_OUT_TYPE=@Type AND sd.WORK_OUT_NO=@No
              AND ISNULL(od.FINISHED_OUT_QTY,0) + ISNULL(sd.QTY,0) > ISNULL(od.FINISHED_IN_QTY,0);
            """, type, no, token,
            line: r => $"{r.GetString(0).Trim()}    {r.GetString(1).Trim()}    {Convert.ToString(r.GetValue(2))}    {Convert.ToString(r.GetValue(3))}    {Convert.ToString(r.GetValue(4))}");
        return rows is null
            ? new(true, null)
            : new(false, "以下出库超出工序工单入库数量\r\n工序工单单别   单号   数量   已入库数量   单据数量\r\n" + rows);
    }

    /// <summary>模具单据空转规则（P_MOU_APPLY / P_MOU_ACCEPT：旧 SP 无有效副作用）。</summary>


    /// <summary>工序工单（2705）AfterSave：工序工单不超制程数量。</summary>
    public static async Task<SprocResult> MocWorkAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        var lines = await DomainRuleService.FindLinesAsync(connection, transaction,
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


    /// <summary>工序完工单（2706）AfterSave：完工不超工序工单数量。</summary>
    public static async Task<SprocResult> MocWorkInAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        var lines = await DomainRuleService.FindLinesAsync(connection, transaction,
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

    public static async Task<SprocResult> MocProduceChangeAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        if (await DomainRuleService.ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.MOC_PRODUCE_M m
            INNER JOIN dbo.MOC_PRODUCE_CHANGE_M c ON c.PRODUCE_TYPE=m.PRODUCE_TYPE AND c.PRODUCE_NO=m.PRODUCE_NO
            WHERE c.CHANGE_PRODUCE_TYPE=@Type AND c.CHANGE_PRODUCE_NO=@No AND m.CONFIRM_TAG=0;
            """, type, no, token))
            return new(false, "生产单未批核，不可变更");
        if (await DomainRuleService.ExistsAsync(connection, transaction,
            """
            SELECT TOP 1 1 FROM dbo.MOC_PRODUCE_M m
            INNER JOIN dbo.MOC_PRODUCE_CHANGE_M c ON c.PRODUCE_TYPE=m.PRODUCE_TYPE AND c.PRODUCE_NO=m.PRODUCE_NO
            WHERE c.CHANGE_PRODUCE_TYPE=@Type AND c.CHANGE_PRODUCE_NO=@No
              AND (ISNULL(m.FINISHED_QTY,0)>ISNULL(c.QTY,0) OR ISNULL(m.FINISHED_SPARE_QTY,0)>ISNULL(c.SPARE_QTY,0));
            """, type, no, token))
            return new(false, "变更后以下序号项数量小于已生产数量");
        var lines = await DomainRuleService.FindLinesAsync(connection, transaction,
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
}
