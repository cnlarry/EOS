using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Customer-order domain rules executed after module saves (quotes, orders, deliveries, returns, fit-out, callback and account documents). Methods run inside the caller's transaction.
/// </summary>
public static class CopDomainRules
{
    /// <summary>备货单（1411）AfterSave：量纲校验与批号必填均由校验目录承担。</summary>

    /// <summary>P_COP_FITIN_CHECK 内联：返仓不超备货/送货不超备货（订单/工单/调拨，SYSSS 门控）。</summary>

    /// <summary>P_PUR_CANCEL 退料不超收料。</summary>


    /// <summary>送货回执（1413）AfterSave：送/退货已有回执由校验目录（reference-exists）承担。</summary>

    /// <summary>工时录入（180207）AfterSave：HR_SETUP.REQUIRE_ENACTMENT=1 时校验加班不超申请（当前环境=0，跳过）。</summary>


    /// <summary>客户订单变更（1418）AfterSave：原单批核、变更量下限与订单号唯一由校验目录承担。</summary>

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



    /// <summary>厂商报价单（1604）AfterSave：厂商校验 + 询价单一致性校验（镜像 cop-quote，厂商侧）。</summary>

    /// <summary>收款单（170102）保存后动作已由效果目录承接（cop-receipt-offset）。</summary>
    /// <summary>付款单（170202）保存后动作已由效果目录承接（pur-pay-offset）。</summary>
}
