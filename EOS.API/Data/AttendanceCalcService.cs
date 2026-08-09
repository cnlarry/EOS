using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// 考勤计算引擎核心（阶段 6.2，方案 A 首批规则）：
/// 排班班次解析（含调休对调日）、签卡覆盖、加班申请上限、休/节假日分类、
/// 上班/加班/休息/节假日工时计算、迟到早退清理、幂等重算。
/// 覆盖规则：1/2/8(简化 ON1·OUT1)/11/12/13/14/17/20；
/// 请假/出差/放假裁剪、打卡窗口分钟级匹配（3-7/9/10/15/16）留待规则确认后扩展。
/// 全部值参数化，日期日列名由白名单生成。
/// </summary>
public sealed class AttendanceCalcService(
    DbConnectionFactory connections,
    ILogger<AttendanceCalcService> logger)
{
    private static readonly Regex DayColumn = new("^DAY_(0[1-9]|[12][0-9]|3[01])$", RegexOptions.Compiled);

    public sealed record CalcResult(int EmployeeCount, int DiaryRows, int Updated, int SkippedNoTimeType);

    public async Task<CalcResult> CalculateAsync(
        DateTime startDate,
        DateTime endDate,
        IReadOnlyList<string> empIds,
        string? deptId,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        // 员工集合（指定员工或部门及下级）
        var employees = new List<string>();
        if (empIds.Count > 0)
        {
            var distinct = empIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var placeholders = string.Join(',', distinct.Select((_, i) => $"@e{i}"));
            await using var command = new SqlCommand(
                $"SELECT LTRIM(RTRIM(EMP_ID)) FROM dbo.HR_EMPLOYEE WHERE IF_SHOW=1 AND LTRIM(RTRIM(EMP_ID)) IN ({placeholders});",
                connection);
            for (var i = 0; i < distinct.Count; i++)
                command.Parameters.Add($"@e{i}", SqlDbType.NChar, 10).Value = distinct[i];
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) employees.Add(reader.GetString(0));
        }
        else if (!string.IsNullOrWhiteSpace(deptId))
        {
            await using var command = new SqlCommand(
                "SELECT LTRIM(RTRIM(EMP_ID)) FROM dbo.HR_EMPLOYEE WHERE IF_SHOW=1 AND DEPT_ID IN (SELECT DEPT_ID FROM dbo.f_get_under_depts(@Dept));",
                connection);
            command.Parameters.Add("@Dept", SqlDbType.NVarChar, 50).Value = deptId.Trim();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) employees.Add(reader.GetString(0));
        }
        if (employees.Count == 0) return new CalcResult(0, 0, 0, 0);

        // 配置与参照数据（参数化，一次性加载）
        var setup = await LoadSetupAsync(connection, token);
        var holidays = await LoadHolidaysAsync(connection, startDate, endDate, token);
        var timeTypes = await LoadTimeTypesAsync(connection, token);
        var exchanges = await LoadExchangesAsync(connection, startDate, endDate, token);
        var signs = await LoadSignsAsync(connection, startDate, endDate, token);
        var applies = await LoadAppliesAsync(connection, startDate, endDate, token);
        var employeeSet = employees.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var diaryRows = 0;
        var updated = 0;
        var skippedNoTimeType = 0;
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            var dayColumn = $"DAY_{date.Day:00}";
            if (!DayColumn.IsMatch(dayColumn)) continue;
            var month = date.ToString("yyyyMM", CultureInfo.InvariantCulture);
            var planByEmployee = await LoadPlanDayAsync(connection, month, dayColumn, employees, token);
            var dateStr = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            foreach (var empId in employees)
            {
                // 调休：当天在调休区间内 → 用 END_DAY 的班次
                var timeTypeId = planByEmployee.GetValueOrDefault(empId);
                var exchange = exchanges.FirstOrDefault(e =>
                    e.EmpId.Equals(empId, StringComparison.OrdinalIgnoreCase) &&
                    (e.FirstDay == dateStr || e.EndDay == dateStr));
                if (exchange is not null)
                {
                    var exchangeMonth = DateTime.Parse(exchange.EndDay, CultureInfo.InvariantCulture).ToString("yyyyMM", CultureInfo.InvariantCulture);
                    var exchangeDay = $"DAY_{DateTime.Parse(exchange.EndDay, CultureInfo.InvariantCulture).Day:00}";
                    timeTypeId = await LoadPlanDayAsync(connection, exchangeMonth, exchangeDay, [empId], token)
                        .ContinueWith(t => t.Result.GetValueOrDefault(empId), token);
                }
                if (string.IsNullOrWhiteSpace(timeTypeId) || !timeTypes.TryGetValue(timeTypeId.Trim(), out var tt))
                {
                    skippedNoTimeType++;
                    continue;
                }

                var isHoliday = holidays.Any(h => date >= h.Start && date <= h.End);
                var isWeekendRest = (setup.SatRestDay && date.DayOfWeek == DayOfWeek.Saturday)
                                 || (setup.SunRestDay && date.DayOfWeek == DayOfWeek.Sunday);
                var sign = signs.FirstOrDefault(s =>
                    s.EmpId.Equals(empId, StringComparison.OrdinalIgnoreCase) && s.CountDate == dateStr);
                var apply = applies.FirstOrDefault(a =>
                    a.EmpId.Equals(empId, StringComparison.OrdinalIgnoreCase) && a.CountDate == dateStr);

                var on1 = sign?.On1 ?? tt.InTime1;
                var out1 = sign?.Out1 ?? tt.OutTime1;
                var workHours = tt.WorkHours1;
                var addHours = tt.AddHours1;
                // 加班申请上限（有申请记录时按分段截断）
                if (apply is not null)
                {
                    if (isHoliday) addHours = Math.Min(addHours, apply.HolidayOvertime);
                    else if (isWeekendRest) addHours = Math.Min(addHours, apply.RestOvertime);
                    else addHours = Math.Min(addHours, apply.Overtime);
                }
                double worktime = 0, overtime = 0, restOvertime = 0, holidayOvertime = 0;
                if (isHoliday) holidayOvertime += addHours;
                else if (isWeekendRest) restOvertime += addHours;
                else { worktime += workHours; overtime += addHours; }

                await using var update = new SqlCommand("""
                    UPDATE dbo.HRM_DIARY SET TIMETYPE_ID=@TimeType, ON1=@On1, OUT1=@Out1,
                        WORKTIME=@Worktime, OVERTIME=@Overtime, REST_OVERTIME=@RestOvertime,
                        HOLIDAY_OVERTIME=@HolidayOvertime, REMARK=@Remark
                    WHERE COUNT_DATE=@Date AND LTRIM(RTRIM(EMP_ID))=@EmpId;
                    """, connection);
                update.Parameters.Add("@TimeType", SqlDbType.NChar, 10).Value = timeTypeId.Trim();
                update.Parameters.Add("@On1", SqlDbType.Char, 5).Value = (object?)on1 ?? DBNull.Value;
                update.Parameters.Add("@Out1", SqlDbType.Char, 5).Value = (object?)out1 ?? DBNull.Value;
                update.Parameters.Add("@Worktime", SqlDbType.Float).Value = worktime > 0 ? worktime : DBNull.Value;
                update.Parameters.Add("@Overtime", SqlDbType.Float).Value = overtime > 0 ? overtime : DBNull.Value;
                update.Parameters.Add("@RestOvertime", SqlDbType.Float).Value = restOvertime > 0 ? restOvertime : DBNull.Value;
                update.Parameters.Add("@HolidayOvertime", SqlDbType.Float).Value = holidayOvertime > 0 ? holidayOvertime : DBNull.Value;
                update.Parameters.Add("@Remark", SqlDbType.NVarChar, 500).Value = exchange is not null ? "调休" : (sign is not null ? "签卡" : (object)DBNull.Value);
                update.Parameters.Add("@Date", SqlDbType.SmallDateTime).Value = date.Date;
                update.Parameters.Add("@EmpId", SqlDbType.NChar, 10).Value = empId;
                var affected = await update.ExecuteNonQueryAsync(token);
                if (affected > 0) updated++;
                diaryRows++;
            }
        }
        logger.LogInformation("考勤计算完成 employees={Employees} rows={Rows} updated={Updated} noTimeType={NoTimeType}",
            employees.Count, diaryRows, updated, skippedNoTimeType);
        return new CalcResult(employees.Count, diaryRows, updated, skippedNoTimeType);
    }

    private sealed record SetupFlags(bool SatRestDay, bool SunRestDay);

    private static async Task<SetupFlags> LoadSetupAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT ISNULL(CAST(SAT_REST_DAY AS int),0), ISNULL(CAST(SUN_REST_DAY AS int),0) FROM dbo.HR_SETUP;", connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return new SetupFlags(false, false);
        return new SetupFlags(reader.GetInt32(0) == 1, reader.GetInt32(1) == 1);
    }

    private static async Task<List<(DateTime Start, DateTime End)>> LoadHolidaysAsync(
        SqlConnection connection, DateTime start, DateTime end, CancellationToken token)
    {
        var result = new List<(DateTime, DateTime)>();
        await using var command = new SqlCommand("""
            SELECT START_DATE, END_DATE FROM dbo.HR_HOLIDAY
            WHERE (START_DATE>=@Start AND START_DATE<=@End) OR (START_DATE<=@Start AND END_DATE>=@Start);
            """, connection);
        command.Parameters.Add("@Start", SqlDbType.SmallDateTime).Value = start.Date;
        command.Parameters.Add("@End", SqlDbType.SmallDateTime).Value = end.Date;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add((reader.GetDateTime(0), reader.GetDateTime(1)));
        return result;
    }

    private sealed record TimeTypeRow(string InTime1, string OutTime1, double WorkHours1, double AddHours1);

    private static async Task<Dictionary<string, TimeTypeRow>> LoadTimeTypesAsync(SqlConnection connection, CancellationToken token)
    {
        var result = new Dictionary<string, TimeTypeRow>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(TIMETYPE_ID)), LTRIM(RTRIM(ISNULL(IN_TIME1,''))), LTRIM(RTRIM(ISNULL(OUT_TIME1,''))),
                   ISNULL(CAST(WORK_HOURS1 AS float),0), ISNULL(CAST(ADD_HOURS1 AS float),0)
            FROM dbo.HR_TIMETYPE;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result[reader.GetString(0)] = new TimeTypeRow(
                reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4));
        return result;
    }

    private sealed record ExchangeRow(string EmpId, string FirstDay, string EndDay);
    private static async Task<List<ExchangeRow>> LoadExchangesAsync(
        SqlConnection connection, DateTime start, DateTime end, CancellationToken token)
    {
        var result = new List<ExchangeRow>();
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(d.EMP_ID)), CONVERT(varchar(10),d.FIRST_DAY,120), CONVERT(varchar(10),d.END_DAY,120)
            FROM dbo.HR_EXCHANGE_M m INNER JOIN dbo.HR_EXCHANGE_D d
              ON d.EXCHANGE_TYPE=m.EXCHANGE_TYPE AND d.EXCHANGE_NO=m.EXCHANGE_NO
            WHERE m.CONFIRM_TAG=1 AND ((d.FIRST_DAY>=@Start AND d.FIRST_DAY<=@End) OR (d.END_DAY>=@Start AND d.END_DAY<=@End));
            """, connection);
        command.Parameters.Add("@Start", SqlDbType.SmallDateTime).Value = start.Date;
        command.Parameters.Add("@End", SqlDbType.SmallDateTime).Value = end.Date;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new ExchangeRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    private sealed record SignRow(string EmpId, string CountDate, string? On1, string? Out1);
    private static async Task<List<SignRow>> LoadSignsAsync(
        SqlConnection connection, DateTime start, DateTime end, CancellationToken token)
    {
        var result = new List<SignRow>();
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(d.EMP_ID)), CONVERT(varchar(10),d.COUNT_DATE,120),
                   NULLIF(LTRIM(RTRIM(ISNULL(d.ON1,''))),''), NULLIF(LTRIM(RTRIM(ISNULL(d.OUT1,''))),'')
            FROM dbo.HR_SIGN_M m INNER JOIN dbo.HR_SIGN_D d
              ON d.SIGN_TYPE=m.SIGN_TYPE AND d.SIGN_NO=m.SIGN_NO
            WHERE m.CONFIRM_TAG=1 AND d.COUNT_DATE>=@Start AND d.COUNT_DATE<=@End;
            """, connection);
        command.Parameters.Add("@Start", SqlDbType.SmallDateTime).Value = start.Date;
        command.Parameters.Add("@End", SqlDbType.SmallDateTime).Value = end.Date;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new SignRow(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return result;
    }

    private sealed record ApplyRow(string EmpId, string CountDate, double Overtime, double RestOvertime, double HolidayOvertime);
    private static async Task<List<ApplyRow>> LoadAppliesAsync(
        SqlConnection connection, DateTime start, DateTime end, CancellationToken token)
    {
        var result = new List<ApplyRow>();
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(d.EMP_ID)), CONVERT(varchar(10),m.COUNT_DATE,120),
                   ISNULL(CAST(d.OVERTIME AS float),0), ISNULL(CAST(d.REST_OVERTIME AS float),0), ISNULL(CAST(d.HOLIDAY_OVERTIME AS float),0)
            FROM dbo.HR_APPLY_M m INNER JOIN dbo.HR_APPLY_D d
              ON d.APPLY_TYPE=m.APPLY_TYPE AND d.APPLY_NO=m.APPLY_NO
            WHERE m.CONFIRM_TAG=1 AND m.COUNT_DATE>=@Start AND m.COUNT_DATE<=@End;
            """, connection);
        command.Parameters.Add("@Start", SqlDbType.SmallDateTime).Value = start.Date;
        command.Parameters.Add("@End", SqlDbType.SmallDateTime).Value = end.Date;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new ApplyRow(reader.GetString(0), reader.GetString(1),
                reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4)));
        return result;
    }

    private static async Task<Dictionary<string, string>> LoadPlanDayAsync(
        SqlConnection connection, string month, string dayColumn, IReadOnlyList<string> employees, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (employees.Count == 0) return result;
        await using var command = new SqlCommand($"""
            SELECT LTRIM(RTRIM(pd.EMP_ID)), LTRIM(RTRIM(ISNULL(pd.[{dayColumn}],'')))
            FROM dbo.HRM_PLAN_D pd
            INNER JOIN dbo.HRM_PLAN_M pm ON pm.PLAN_TYPE=pd.PLAN_TYPE AND pm.PLAN_NO=pd.PLAN_NO AND pm.CONFIRM_TAG=1
            WHERE pm.COUNT_MONTH=@Month AND pd.EMP_ID IN ({string.Join(',', employees.Select((_, i) => $"@e{i}"))})
              AND pd.[{dayColumn}] IS NOT NULL AND LTRIM(RTRIM(pd.[{dayColumn}]))<>'';
            """, connection);
        command.Parameters.Add("@Month", SqlDbType.Char, 6).Value = month;
        for (var i = 0; i < employees.Count; i++)
            command.Parameters.Add($"@e{i}", SqlDbType.NChar, 10).Value = employees[i];
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            if (!result.ContainsKey(reader.GetString(0)))
                result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }
}
