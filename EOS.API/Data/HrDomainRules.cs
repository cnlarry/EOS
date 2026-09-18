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
}
