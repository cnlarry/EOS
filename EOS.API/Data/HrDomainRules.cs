using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Human-resource domain rules executed after module saves (employee, contract, safety, certification, plan, wage, apply and work-time documents). Methods run inside the caller's transaction.
/// </summary>
public static class HrDomainRules
{

    /// <summary>
    /// 货币资料（110103）AfterSave：本位币唯一 + 本位币汇率必须为 1。
    /// 仅校验，无落库副作用。
    /// </summary>

    /// <summary>工资表（P_HR_WAGE_LZ）AfterSave：离职工资表先删同月旧档，再按每月每人一份校验。</summary>
    public static async Task<SprocResult> HrWageAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        string masterTable, string detailTable,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "工资表领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        string? countMonth;
        await using (var read = new SqlCommand(
            $"SELECT LTRIM(RTRIM(ISNULL(COUNT_MONTH,''))) FROM dbo.[{masterTable}] WHERE WAGE_TYPE=@Type AND WAGE_NO=@No;",
            connection, transaction))
        {
            read.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            read.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            countMonth = (string?)await read.ExecuteScalarAsync(token);
        }
        if (string.IsNullOrWhiteSpace(countMonth)) return new(true, null);
        await using (var clean = new SqlCommand($"""
            DELETE d FROM dbo.[{detailTable}] d
            WHERE EXISTS (SELECT 1 FROM dbo.[{masterTable}] m
                          WHERE m.COUNT_MONTH=@CountMonth AND m.WAGE_TYPE=d.WAGE_TYPE AND m.WAGE_NO=d.WAGE_NO)
              AND NOT (d.WAGE_TYPE=@Type AND d.WAGE_NO=@No)
              AND d.EMP_ID IN (SELECT EMP_ID FROM dbo.[{detailTable}] WHERE WAGE_TYPE=@Type AND WAGE_NO=@No);
            """, connection, transaction))
        {
            clean.Parameters.Add("@CountMonth", SqlDbType.NChar, 6).Value = countMonth;
            clean.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            clean.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            await clean.ExecuteNonQueryAsync(token);
        }
        var dup = await FindMonthDupAsync(connection, transaction,
            $"""
            SELECT d.EMP_ID FROM dbo.[{masterTable}] m
            INNER JOIN dbo.[{detailTable}] d ON d.WAGE_TYPE=m.WAGE_TYPE AND d.WAGE_NO=m.WAGE_NO
            WHERE m.COUNT_MONTH=@CountMonth
            GROUP BY d.EMP_ID HAVING COUNT(*)>1;
            """, type, no, countMonth, token,
            line: r => r.GetString(0).Trim() + "\t");
        return dup is null
            ? new(true, null)
            : new(false, "以下人员当月工资表重复 \r\n" + dup);
    }

    /// <summary>按（类型/单号/月份）收集明细行的辅助。</summary>


    /// <summary>按（类型/单号/月份）收集明细行的辅助。</summary>
    public static async Task<string?> FindMonthDupAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        string type, string no, string countMonth, CancellationToken token, Func<SqlDataReader, string> line)
    {
        await using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
        cmd.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
        cmd.Parameters.Add("@CountMonth", SqlDbType.NChar, 6).Value = countMonth;
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var lines = new List<string>();
        while (await reader.ReadAsync(token)) lines.Add(line(reader));
        return lines.Count > 0 ? string.Join("\r\n", lines.Take(10)) : null;
    }

    /// <summary>加班申请单（P_HR_APPLY）AfterSave：每日每人一单 + 不超过每月加班额。</summary>


    /// <summary>
    /// 加班申请单（P_HR_APPLY）AfterSave：不超过每月加班额（先决条件是当月出勤参数已维护）。
    /// "每日每人一单"已由校验目录的 duplicate-check 实例承担（180206 SAVE）。
    /// </summary>
    public static async Task<SprocResult> HrApplyAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "加班申请领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        DateTime? countDate;
        string? enaType = null; string? enaNo = null;
        await using (var read = new SqlCommand(
            "SELECT COUNT_DATE FROM dbo.HR_APPLY_M WHERE APPLY_TYPE=@Type AND APPLY_NO=@No;", connection, transaction))
        {
            read.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            read.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            var v = await read.ExecuteScalarAsync(token);
            countDate = v is null or DBNull ? null : Convert.ToDateTime(v);
        }
        if (countDate is null) return new(true, null);
        // 每月加班额（当月出勤参数限额）
        string? countMonth;
        await using (var month = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(COUNT_MONTH,''))) FROM dbo.HR_ENACTMENT_M WHERE COUNT_MONTH=CONVERT(varchar(6),@CountDate,112);",
            connection, transaction))
        {
            month.Parameters.Add("@CountDate", SqlDbType.DateTime).Value = countDate.Value;
            countMonth = (string?)await month.ExecuteScalarAsync(token);
        }
        // 硬规则：必须先维护当月出勤参数才允许提交加班申请（未生成即拒绝，不再静默放行）
        if (string.IsNullOrWhiteSpace(countMonth))
        {
            return new(false,
                $"未生成 {countDate.Value:yyyyMM} 月度出勤参数，加班申请不能保存；请先在「每月出勤参数」维护本月的加班额度。");
        }
        await using (var ena = new SqlCommand(
            "SELECT TOP 1 LTRIM(RTRIM(ENACTMENT_TYPE)), LTRIM(RTRIM(ENACTMENT_NO)) FROM dbo.HR_ENACTMENT_M WHERE COUNT_MONTH=@CountMonth;",
            connection, transaction))
        {
            ena.Parameters.Add("@CountMonth", SqlDbType.NChar, 6).Value = countMonth;
            await using var reader = await ena.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token)) { enaType = reader.GetString(0); enaNo = reader.GetString(1); }
        }
        if (enaType is null)
        {
            return new(false,
                $"未生成 {countDate.Value:yyyyMM} 月度出勤参数，加班申请不能保存；请先在「每月出勤参数」维护本月的加班额度。");
        }
        string? exceeded = null;
        await using (var cmd = new SqlCommand("""
            SELECT t.EMP_ID, ISNULL(e.OVERTIME,0), ISNULL(e.REST_OVERTIME,0), ISNULL(e.HOLIDAY_OVERTIME,0),
                   t.OVERTIME, t.REST_OVERTIME, t.HOLIDAY_OVERTIME
            FROM (
                SELECT d.EMP_ID, SUM(d.OVERTIME) OVERTIME, SUM(d.REST_OVERTIME) REST_OVERTIME, SUM(d.HOLIDAY_OVERTIME) HOLIDAY_OVERTIME
                FROM dbo.HR_APPLY_M m INNER JOIN dbo.HR_APPLY_D d ON d.APPLY_TYPE=m.APPLY_TYPE AND d.APPLY_NO=m.APPLY_NO
                WHERE YEAR(m.COUNT_DATE)=YEAR(@CountDate) AND MONTH(m.COUNT_DATE)=MONTH(@CountDate)
                  AND d.EMP_ID IN (SELECT EMP_ID FROM dbo.HR_APPLY_D WHERE APPLY_TYPE=@Type AND APPLY_NO=@No)
                GROUP BY d.EMP_ID
            ) t LEFT JOIN dbo.HR_ENACTMENT_D e
              ON e.EMP_ID=t.EMP_ID AND e.ENACTMENT_TYPE=@EnaType AND e.ENACTMENT_NO=@EnaNo
            WHERE ISNULL(e.OVERTIME,0) < t.OVERTIME OR ISNULL(e.REST_OVERTIME,0) < t.REST_OVERTIME
               OR ISNULL(e.HOLIDAY_OVERTIME,0) < t.HOLIDAY_OVERTIME
               OR (e.EMP_ID IS NULL AND (t.OVERTIME>0 OR t.REST_OVERTIME>0 OR t.HOLIDAY_OVERTIME>0));
            """, connection, transaction))
        {
            cmd.Parameters.Add("@CountDate", SqlDbType.DateTime).Value = countDate.Value;
            cmd.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            cmd.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            cmd.Parameters.Add("@EnaType", SqlDbType.NChar, 10).Value = enaType;
            cmd.Parameters.Add("@EnaNo", SqlDbType.NChar, 20).Value = enaNo;
            await using var reader = await cmd.ExecuteReaderAsync(token);
            var lines = new List<string>();
            while (await reader.ReadAsync(token))
                lines.Add($"{reader.GetString(0).Trim()}  {Convert.ToString(reader.GetValue(1))}  {Convert.ToString(reader.GetValue(2))}  {Convert.ToString(reader.GetValue(3))}  已录入  {Convert.ToString(reader.GetValue(4))}  {Convert.ToString(reader.GetValue(5))}  {Convert.ToString(reader.GetValue(6))}");
            if (lines.Count > 0) exceeded = string.Join("\r\n", lines.Take(10));
        }
        return exceeded is null
            ? new(true, null)
            : new(false, "以下人员时间超出:\r\n工号--加班时--休息日加班时--节假日加班时\r\n" + exceeded);
    }

    /// <summary>借出单（P_INV_LOAN）AfterSave：库别/产品/批号校验。</summary>

    public static async Task<SprocResult> HrWorktimeAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var requireEnactment = await DomainRuleService.ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.HR_SETUP WHERE REQUIRE_ENACTMENT=1;", keyValues[0], keyValues[1], token);
        if (!requireEnactment) return new(true, null);
        var type = keyValues[0]; var no = keyValues[1];
        var lines = await DomainRuleService.FindLinesAsync(connection, transaction,
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
}
