using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// System and security domain rules executed after module saves (currency base rate,
/// BOM structure validation, user/group permission cleanup). Methods run inside the
/// caller's transaction and report a success flag plus a user-facing message on failure.
/// </summary>
public static class SysDomainRules
{
    /// <summary>
    /// BOM structure save: validates the product and element numbers, requires a positive
    /// base quantity, rejects cyclic references, and backfills historical length/width columns.
    /// </summary>
    public static async Task<SprocResult> BomStruAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 1 || keyValues.Count < 1) return new(false, "产品BOM领域规则缺少主键。");
        var proNo = (keyValues[0] ?? string.Empty).Trim();
        await using (var product = new SqlCommand(
            "SELECT TOP 1 1 FROM dbo.PRODUCT WHERE PRO_NO=@ProNo;", connection, transaction))
        {
            product.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
            if (await product.ExecuteScalarAsync(token) is null)
                return new(false, "产品编号不存在。 ");
        }
        var elements = await ReadBomMissingAsync(connection, transaction,
            "SELECT TOP 11 SERIAL_NO FROM dbo.BOM_STRU_D d WHERE PRO_NO=@ProNo " +
            "AND NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=d.ELEMENT_PRO_NO) ORDER BY SERIAL_NO;",
            proNo, token);
        if (elements is not null) return new(false, "以下序号项元件编号不存在 \r\n" + elements);
        var baseQty = await ReadBomMissingAsync(connection, transaction,
            "SELECT TOP 11 SERIAL_NO FROM dbo.BOM_STRU_D WHERE PRO_NO=@ProNo AND BASE_QTY<=0 ORDER BY SERIAL_NO;",
            proNo, token);
        if (baseQty is not null) return new(false, "以下序号项元件底数不能小于0 \r\n" + baseQty);
        var cycle = await FindBomCycleAsync(connection, transaction, proNo, token);
        if (cycle is not null)
            return new(false, "以下元件在BOM结构中循环使用 \r\n" + cycle);
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

    /// <summary>
    /// BOM 成环检测（原 `P_BOM_CHECK`）：从该产品出发按层展开（第 N 层 = 以第 N-1 层元件的
    /// 产品号继续展开），只要某一层出现"元件号 = 本产品"的行即判为循环，回报该行的产品号。
    /// 语句与遍历方式逐条对照原过程本体：逐层扫描、层内任取一行（原实现是 `TOP 1` 无 `ORDER BY`）、
    /// 产品号按原过程的 `NVARCHAR(50)` 变量口径取值（`BOM_STRU_D.PRO_NO` 是 `NCHAR(30)`，定长补空格
    /// 因此原样保留——`CONVERT(NVARCHAR(50), …)` 复刻该赋值口径）。
    /// **唯一有意差异**：原过程在没有"回到根"的环（如根→X→Y→X）时会无限展开、永远挂住保存；
    /// 这里到第 100 层即判定为循环（fail-closed），把"挂死"换成"拒绝并报循环"。
    /// </summary>
    internal const string BomCycleSql = """
        DECLARE @ok INT = 1, @errCode NVARCHAR(50), @step INT = 1;
        IF OBJECT_ID('tempdb..#BomCycleLevel') IS NOT NULL DROP TABLE #BomCycleLevel;
        SELECT @step AS StepNo, PRO_NO, ELEMENT_PRO_NO INTO #BomCycleLevel FROM dbo.BOM_STRU_D WHERE PRO_NO = @ProNo;
        WHILE 1 = 1
        BEGIN
            SELECT TOP 1 @errCode = CONVERT(NVARCHAR(50), PRO_NO)
            FROM #BomCycleLevel WHERE ELEMENT_PRO_NO = @ProNo AND StepNo = @step;
            IF ISNULL(@errCode, N'') <> N''
            BEGIN
                SELECT @ok = 0;
                BREAK;
            END
            SELECT @step = @step + 1;
            INSERT INTO #BomCycleLevel
            SELECT @step, b.PRO_NO, b.ELEMENT_PRO_NO
            FROM dbo.BOM_STRU_D b, #BomCycleLevel t
            WHERE b.PRO_NO = t.ELEMENT_PRO_NO AND t.StepNo = @step - 1;
            IF @@ROWCOUNT <= 0 BREAK;
            IF @step >= 100
            BEGIN
                SELECT @ok = 0,
                       @errCode = (SELECT TOP 1 CONVERT(NVARCHAR(50), PRO_NO) FROM #BomCycleLevel WHERE StepNo = @step);
                BREAK;
            END
        END
        SELECT @ok AS Ok, @errCode AS ErrCode;
        """;

    /// <summary>返回循环元件所在行的产品号（无环返回 null）；供保存期校验与真库对拍共用。</summary>
    internal static async Task<string?> FindBomCycleAsync(
        SqlConnection connection, SqlTransaction transaction, string proNo, CancellationToken token)
    {
        await using var command = new SqlCommand(BomCycleSql, connection, transaction);
        command.Parameters.Add("@ProNo", SqlDbType.NVarChar, 50).Value = proNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var ok = reader.IsDBNull(0) ? 1 : Convert.ToInt32(reader.GetValue(0));
        if (ok == 1) return null;
        return reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
    }

    /// <summary>User-group save: removes group permission rows whose module or report no longer exists.</summary>
    public static async Task<SprocResult> SysdgAfterSaveAsync(
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
}
