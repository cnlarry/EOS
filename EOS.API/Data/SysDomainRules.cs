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
    /// <summary>Currency save: only one currency may be marked as base and its rate must be 1.</summary>
    public static async Task<SprocResult> CurrAfterSaveAsync(
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
    /// BOM structure save: validates the product and element numbers, requires a positive
    /// base quantity, rejects cyclic references, and backfills legacy length/width columns.
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
        // Cycle detection delegates to the stored procedure because recursive BOM walking
        // is complex to express safely in C#.
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

    /// <summary>
    /// User-rights save: joins the default group, then cleans orphaned personal permission
    /// rows whose module or report no longer exists. Report permissions resolve from
    /// SYSDD.REPORT_TAG at read time, so no materialized expansion is maintained here.
    /// </summary>
    public static async Task<SprocResult> SysdlAfterSaveAsync(
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
        }
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
