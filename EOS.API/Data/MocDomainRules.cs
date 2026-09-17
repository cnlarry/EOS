using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Manufacturing domain rules executed after module saves (work orders, work-in moves, produce transactions, plan/BOM and process changes). Methods run inside the caller's transaction.
/// </summary>
public static class MocDomainRules
{

    /// <summary>生产领料单（P_MOC_GET）AfterSave：批号条件必填。</summary>
    public static Task<SprocResult> MocGetAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "MOC_GET_D", "GET_TYPE", "GET_NO",
            [
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);

    /// <summary>制令单（P_MOC_PRODUCE）AfterSave：CHECK 分支 + 明细订单号回填。</summary>
    public static async Task<SprocResult> MocProduceAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "制令单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
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

    /// <summary>生产出库单（P_MOC_PRODUCT_OUT）AfterSave：CHECK 分支 + 制令/库别/产品/批号校验。</summary>


    /// <summary>生产出库单（P_MOC_PRODUCT_OUT）AfterSave：出库数量与批号必填由校验目录承担。</summary>


    /// <summary>工单BOM（P_MOC_BOM_STRU）AfterSave：孤儿主/明细清理循环。</summary>
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


    /// <summary>生产计划（P_MOC_PLAN）AfterSave：计划量不超订单由校验目录（qty-not-exceed）承担。</summary>
    public static Task<SprocResult> MocPlanAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => Task.FromResult(new SprocResult(true, null));

    /// <summary>工单制程（P_MOC_PRODUCE_PROCESS）AfterSave：制令存在校验。</summary>


    /// <summary>工序发料单（P_MOC_WORK_OUT）AfterSave：出库不超工序工单入库由校验目录（qty-not-exceed）承担。</summary>
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
