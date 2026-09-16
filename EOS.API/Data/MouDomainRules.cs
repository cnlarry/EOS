using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Mould domain rules executed after module saves (mould batch, get, check, production, batch-in and assessment documents). Methods run inside the caller's transaction.
/// </summary>
public static class MouDomainRules
{
    public static Task<SprocResult> MouldNoopAfterSaveAsync(CancellationToken token)
        => Task.FromResult(new SprocResult(true, null));

    /// <summary>量产模具完工（P_MOU_BATCH）AfterSave：申请数量不超承认单可申请数量。</summary>


    /// <summary>量产模具完工（P_MOU_BATCH）AfterSave：申请数量不超承认单可申请数量。</summary>
    public static async Task<SprocResult> MouBatchAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "量产模具完工领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        await using var cmd = new SqlCommand("""
            SELECT TOP 1 1 FROM dbo.MOU_BATCH_M d
            INNER JOIN dbo.MOU_ACCEPT_M m ON m.ACCEPT_TYPE=d.ACCEPT_TYPE AND m.ACCEPT_NO=d.ACCEPT_NO
            WHERE d.BATCH_TYPE=@Type AND d.BATCH_NO=@No
              AND ISNULL(m.QTY,0) < ISNULL(m.FINISHED_QTY,0) + ISNULL(d.QTY,0);
            """, connection, transaction);
        cmd.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
        cmd.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
        return await cmd.ExecuteScalarAsync(token) is not null
            ? new(false, "申请数量已超过承认单可申请数量")
            : new(true, null);
    }

    /// <summary>模房领料/耗料单（P_MOU_GET / P_MOU_GET2）AfterSave：批号条件必填。</summary>
    public static Task<SprocResult> MouGetAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "MOU_GET_D", "GET_TYPE", "GET_NO",
            [
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);

    public static Task<SprocResult> MouGet2AfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "MOU_GET2_D", "GET_TYPE", "GET_NO",
            [
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);

    /// <summary>产品模具对照表（P_MOU_PRO）AfterSave：产品/模具存在 + 所用模具汇总（按产品限定—— 全局更新疑似笔误）。</summary>
    public static async Task<SprocResult> MouProAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 1 || keyValues.Count < 1) return new(false, "产品模具对照领域规则缺少主键。");
        var proNo = (keyValues[0] ?? string.Empty).Trim();
        await using var product = new SqlCommand(
            "SELECT TOP 1 1 FROM dbo.PRODUCT WHERE PRO_NO=@ProNo;", connection, transaction);
        product.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
        if (await product.ExecuteScalarAsync(token) is null)
            return new(false, "产品编号不存在");
        await using (var mould = new SqlCommand("""
            SELECT TOP 11 SERIAL_NO FROM dbo.MOU_PRO_D t
            WHERE t.PRO_NO=@ProNo
              AND NOT EXISTS (SELECT 1 FROM dbo.MOU_MOULD m WHERE m.MOULD_ID=t.MOULD_ID)
            ORDER BY SERIAL_NO;
            """, connection, transaction))
        {
            mould.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
            await using var reader = await mould.ExecuteReaderAsync(token);
            var lines = new List<string>();
            while (await reader.ReadAsync(token)) lines.Add(Convert.ToInt32(reader.GetValue(0)).ToString());
            if (lines.Count > 0)
                return new(false, "以下序号项模具编号不存在 \r\n" + string.Join("\r\n", lines.Take(10)));
        }
        await using var update = new SqlCommand(
            "UPDATE dbo.MOU_PRO_M SET MOULD_IDS=dbo.f_get_pro_moulds(PRO_NO) WHERE PRO_NO=@ProNo;", connection, transaction);
        update.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
        await update.ExecuteNonQueryAsync(token);
        return new(true, null);
    }

    /// <summary>量产模入库（P_MOU_BATCHIN）AfterSave：ERROR_NO_SAVE 门控的不超完工未入检查。</summary>
    public static async Task<SprocResult> MouBatchinAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "量产模入库领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        if (!await DomainRuleService.HasErrorNoSaveAsync(connection, transaction, moduleId, token))
            return new(true, null);
        var rows = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT d.SERIAL_NO
            FROM dbo.MOU_BATCH_M m
            INNER JOIN (SELECT BATCH_TYPE, BATCH_NO, MAX(SERIAL_NO) SERIAL_NO, SUM(QTY) QTY
                        FROM dbo.MOU_BATCHIN_D WHERE BATCHIN_TYPE=@Type AND BATCHIN_NO=@No
                        GROUP BY BATCH_TYPE, BATCH_NO) d
              ON m.BATCH_TYPE=d.BATCH_TYPE AND m.BATCH_NO=d.BATCH_NO
            WHERE ISNULL(m.QTY,0) < ISNULL(m.FINISHED_QTY,0) + d.QTY;
            """, type, no, token,
            line: r => Convert.ToInt32(r.GetValue(0)).ToString() + "    ");
        return rows is null
            ? new(true, null)
            : new(false, "以下序号项量产模入库不能大于模具完工未入数量\r\n" + rows);
    }

    /// <summary>出口报关单（P_CUS_EXPORT）AfterSave：ERROR_NO_SAVE 门控的报关不超合同检查。</summary>
}
