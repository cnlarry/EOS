using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Manufacturing domain rules executed after module saves (work orders, work-in moves, produce transactions, plan/BOM and process changes). Methods run inside the caller's transaction.
/// </summary>
public static class MocDomainRules
{
    /// <summary>制令单（P_MOC_PRODUCE）AfterSave：明细订单号回填。</summary>
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
}
