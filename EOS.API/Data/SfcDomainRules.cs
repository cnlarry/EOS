using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Shop-floor control rules executed after module saves (plan row backfill, process
/// validation, daily-record quantity limits). Methods run inside the caller's transaction.
/// </summary>
public static class SfcDomainRules
{
    /// <summary>
    /// Plan save: appends missing process rows from the plan-more table, backfills quantities,
    /// hours and procedure types, normalizes fixed-time rows, and appends the produce-number suffix.
    /// </summary>
    public static async Task<SprocResult> SfcPlanAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var type = keyValues[0]; var no = keyValues[1];
        var maxSerial = await DomainRuleService.ScalarIntAsync(connection, transaction,
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

    /// <summary>Process save: requires an existing product and a positive fixed time when fixed time is used.</summary>
    public static async Task<SprocResult> SfcProcessAfterSaveAsync(
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

    /// <summary>
    /// Daily-record save: requires existing process entries and rejects quantities above the
    /// maximum allowed by the production process.
    /// </summary>
    public static async Task<SprocResult> SfcDailyAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "生产记录单领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        var process = await DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
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
}
