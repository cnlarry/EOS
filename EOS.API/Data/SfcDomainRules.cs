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

    /// <summary>产品制程（P_SFC_PROCESS）AfterSave：固定时间不得为 0 由校验目录（line-require）承担。</summary>

    /// <summary>生产记录单（P_SFC_DAILY）AfterSave：完工数量不超制令制程允许最大数量由校验目录承担。</summary>
}
