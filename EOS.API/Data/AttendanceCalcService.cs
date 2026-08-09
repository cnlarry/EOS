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

    public sealed record CalcResult(int EmployeeCount, int DiaryRows, int Updated, int SkippedNoTimeType, int SkippedNotActive);

    public async Task<CalcResult> CalculateAsync(
        DateTime startDate,
        DateTime endDate,
        IReadOnlyList<string> empIds,
        string? deptId,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        // 员工集合（指定员工或部门及下级；规则 21 员工筛选：IF_COUNT=0 / IF_SECRECY=1 不参与，
        // 在职期间按日校验 IN_DATE/DIMISSION_DATE；规则 22 部门范围走 f_get_under_depts）。
        var employees = new List<string>();
        var employment = new Dictionary<string, (DateTime? InDate, DateTime? DimissionDate)>(StringComparer.OrdinalIgnoreCase);
        if (empIds.Count > 0)
        {
            var distinct = empIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var placeholders = string.Join(',', distinct.Select((_, i) => $"@e{i}"));
            await using var command = new SqlCommand(
                $"SELECT LTRIM(RTRIM(EMP_ID)), IN_DATE, DIMISSION_DATE FROM dbo.HR_EMPLOYEE " +
                $"WHERE IF_SHOW=1 AND COALESCE(IF_COUNT,1)=1 AND COALESCE(IF_SECRECY,0)=0 AND LTRIM(RTRIM(EMP_ID)) IN ({placeholders});",
                connection);
            for (var i = 0; i < distinct.Count; i++)
                command.Parameters.Add($"@e{i}", SqlDbType.NChar, 10).Value = distinct[i];
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var empId = reader.GetString(0);
                employees.Add(empId);
                employment[empId] = (
                    reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                    reader.IsDBNull(2) ? null : reader.GetDateTime(2));
            }
        }
        else if (!string.IsNullOrWhiteSpace(deptId))
        {
            await using var command = new SqlCommand(
                "SELECT LTRIM(RTRIM(EMP_ID)), IN_DATE, DIMISSION_DATE FROM dbo.HR_EMPLOYEE " +
                "WHERE IF_SHOW=1 AND COALESCE(IF_COUNT,1)=1 AND COALESCE(IF_SECRECY,0)=0 " +
                "AND DEPT_ID IN (SELECT DEPT_ID FROM dbo.f_get_under_depts(@Dept));",
                connection);
            command.Parameters.Add("@Dept", SqlDbType.NVarChar, 50).Value = deptId.Trim();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var empId = reader.GetString(0);
                employees.Add(empId);
                employment[empId] = (
                    reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                    reader.IsDBNull(2) ? null : reader.GetDateTime(2));
            }
        }
        if (employees.Count == 0) return new CalcResult(0, 0, 0, 0, 0);

        // 配置与参照数据（参数化，一次性加载）
        var setup = await LoadSetupAsync(connection, token);
        var holidays = await LoadHolidaysAsync(connection, startDate, endDate, token);
        var timeTypes = await LoadTimeTypesAsync(connection, token);
        var exchanges = await LoadExchangesAsync(connection, startDate, endDate, token);
        var signs = await LoadSignsAsync(connection, startDate, endDate, token);
        var applies = await LoadAppliesAsync(connection, startDate, endDate, token);
        var punches = await LoadPunchesAsync(connection, startDate, endDate, employees, token);
        var planAdjusts = await LoadPlanAdjustsAsync(connection, startDate, endDate, token);
        var recesses = await LoadRecessesAsync(connection, startDate, endDate, token);
        var leaves = await LoadLeavesAsync(connection, startDate, endDate, token);
        var evections = await LoadEvectionsAsync(connection, startDate, endDate, token);
        var employeeSet = employees.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var diaryRows = 0;
        var updated = 0;
        var skippedNoTimeType = 0;
        var skippedNotActive = 0;
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            var dayColumn = $"DAY_{date.Day:00}";
            if (!DayColumn.IsMatch(dayColumn)) continue;
            var month = date.ToString("yyyyMM", CultureInfo.InvariantCulture);
            var planByEmployee = await LoadPlanDayAsync(connection, month, dayColumn, employees, token);
            var dateStr = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            foreach (var empId in employees)
            {
                // 在职期间（规则 21）：入职日晚于计算日或离职日早于计算日的员工当日跳过。
                var empEmployment = employment.GetValueOrDefault(empId);
                if (empEmployment.InDate is DateTime inDate && inDate.Date > date.Date) { skippedNotActive++; continue; }
                if (empEmployment.DimissionDate is DateTime dimissionDate && dimissionDate.Date < date.Date) { skippedNotActive++; continue; }
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
                var planAdjust = planAdjusts.FirstOrDefault(a =>
                    a.EmpId.Equals(empId, StringComparison.OrdinalIgnoreCase) && a.CountDate == dateStr);
                var recess = recesses.FirstOrDefault(r =>
                    r.EmpId.Equals(empId, StringComparison.OrdinalIgnoreCase) && date >= r.Start && date <= r.End);
                var leave = leaves.FirstOrDefault(l =>
                    l.EmpId.Equals(empId, StringComparison.OrdinalIgnoreCase) && date >= l.Start && date <= l.End);
                var evection = evections.FirstOrDefault(e =>
                    e.EmpId.Equals(empId, StringComparison.OrdinalIgnoreCase) && date >= e.Start && date <= e.End);
                var sign = signs.FirstOrDefault(s =>
                    s.EmpId.Equals(empId, StringComparison.OrdinalIgnoreCase) && s.CountDate == dateStr);
                var apply = applies.FirstOrDefault(a =>
                    a.EmpId.Equals(empId, StringComparison.OrdinalIgnoreCase) && a.CountDate == dateStr);

                var remark = exchange is not null ? "调休" : (sign is not null ? "签卡" : (object)DBNull.Value);
                var wholeDayOff = (recess is not null && string.IsNullOrWhiteSpace(recess.StartTime) && string.IsNullOrWhiteSpace(recess.EndTime))
                               || (leave is not null && string.IsNullOrWhiteSpace(leave.StartTime) && string.IsNullOrWhiteSpace(leave.EndTime))
                               || (evection is not null && string.IsNullOrWhiteSpace(evection.StartTime) && string.IsNullOrWhiteSpace(evection.EndTime));
                if (wholeDayOff)
                    remark = evection is not null ? "出差" : (leave is not null ? "请假" : "放假");

                var inTime = new string?[4];
                var outTime = new string?[4];
                var workHours = new double[4];
                var addHours = new double[4];
                var on = new string?[4];
                var outPunch = new string?[4];
                var lateFor = new double[4];
                var leaveEarly = new double[4];
                for (var slot = 0; slot < 4; slot++)
                {
                    inTime[slot] = planAdjust?.InTime[slot] ?? tt.InTime[slot];
                    outTime[slot] = planAdjust?.OutTime[slot] ?? tt.OutTime[slot];
                    workHours[slot] = planAdjust?.WorkHours[slot] ?? tt.WorkHours[slot];
                    addHours[slot] = planAdjust?.AddHours[slot] ?? tt.AddHours[slot];
                    if (wholeDayOff)
                    {
                        workHours[slot] = 0;
                        addHours[slot] = 0;
                        inTime[slot] = null;
                        outTime[slot] = null;
                    }
                    // 优先级：签卡 > 打卡窗口匹配 > 排班时间
                    on[slot] = sign?.On[slot];
                    if (string.IsNullOrWhiteSpace(on[slot]))
                    {
                        var match = !string.IsNullOrWhiteSpace(inTime[slot])
                            ? MatchPunch(punches, empId, date, tt, slot, isIn: true)
                            : null;
                        on[slot] = match?.ToString("HH:mm") ?? inTime[slot];
                    }
                    outPunch[slot] = sign?.Out[slot];
                    if (string.IsNullOrWhiteSpace(outPunch[slot]))
                    {
                        var match = !string.IsNullOrWhiteSpace(outTime[slot])
                            ? MatchPunch(punches, empId, date, tt, slot, isIn: false)
                            : null;
                        outPunch[slot] = match?.ToString("HH:mm") ?? outTime[slot];
                    }
                    // 不需打卡（IN_CHECK/OUT_CHECK）：未打卡时用排班时间填充
                    if (string.IsNullOrWhiteSpace(on[slot]) && tt.InCheck[slot] && !string.IsNullOrWhiteSpace(inTime[slot]))
                        on[slot] = inTime[slot];
                    if (string.IsNullOrWhiteSpace(outPunch[slot]) && tt.OutCheck[slot] && !string.IsNullOrWhiteSpace(outTime[slot]))
                        outPunch[slot] = outTime[slot];
                    if (!string.IsNullOrWhiteSpace(on[slot]) && !string.IsNullOrWhiteSpace(inTime[slot]))
                    {
                        var onMinutes = ParseTime(on[slot]!);
                        var schedIn = ParseTime(inTime[slot]!);
                        if (onMinutes > schedIn + tt.InLateMinute[slot])
                            lateFor[slot] = onMinutes - schedIn;
                    }
                    if (!string.IsNullOrWhiteSpace(outPunch[slot]) && !string.IsNullOrWhiteSpace(outTime[slot]))
                    {
                        var outMinutes = ParseTime(outPunch[slot]!);
                        var schedOut = ParseTime(outTime[slot]!);
                        if (outMinutes < schedOut - tt.OutLateMinute[slot])
                            leaveEarly[slot] = schedOut - outMinutes;
                    }
                }

                double worktime = 0, overtime = 0, restOvertime = 0, holidayOvertime = 0, absent = 0;
                for (var slot = 0; slot < 4; slot++)
                {
                    var dWork = workHours[slot];
                    var dAdd = addHours[slot];
                    if (tt.IsOutFlex[slot] && !string.IsNullOrWhiteSpace(outPunch[slot]) && !string.IsNullOrWhiteSpace(outTime[slot]))
                    {
                        var flexMinutes = ParseTime(outPunch[slot]!) - ParseTime(outTime[slot]!);
                        if (flexMinutes >= 30)
                        {
                            var flexHours = Math.Floor(flexMinutes / 60.0) + (flexMinutes % 60 >= 30 ? 0.5 : 0);
                            if (dAdd > 0) dAdd += flexHours; else dWork += flexHours;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(on[slot]) || string.IsNullOrWhiteSpace(outPunch[slot]))
                    {
                        absent += dWork;
                    }
                    else if (isHoliday)
                    {
                        holidayOvertime += dWork + dAdd;
                    }
                    else if (isWeekendRest)
                    {
                        restOvertime += dWork + dAdd;
                    }
                    else
                    {
                        worktime += dWork;
                        overtime += dAdd;
                    }
                }
                if (apply is not null && tt.IfConfirm)
                {
                    if (isHoliday) holidayOvertime = Math.Min(holidayOvertime, apply.HolidayOvertime);
                    else if (isWeekendRest) restOvertime = Math.Min(restOvertime, apply.RestOvertime);
                    else { overtime = Math.Min(overtime, apply.Overtime); worktime = Math.Min(worktime, apply.Worktime); }
                }
                var lateTimes = lateFor.Count(x => x > 0);
                var leaveEarlyTimes = leaveEarly.Count(x => x > 0);
                var lateForSum = lateFor.Sum();
                var leaveEarlySum = leaveEarly.Sum();

                await using var update = new SqlCommand("""
                    UPDATE dbo.HRM_DIARY SET TIMETYPE_ID=@TimeType,
                        ON1=@On1, OUT1=@Out1, ON2=@On2, OUT2=@Out2, ON3=@On3, OUT3=@Out3, ON4=@On4, OUT4=@Out4,
                        BE_LATE_FOR1=@Late1, BE_LATE_FOR2=@Late2, BE_LATE_FOR3=@Late3, BE_LATE_FOR4=@Late4,
                        LEAVE_EARLY1=@Early1, LEAVE_EARLY2=@Early2, LEAVE_EARLY3=@Early3, LEAVE_EARLY4=@Early4,
                        LATE_TIMES=@LateTimes, BE_LATE_FOR=@LateSum, LEAVE_EARLY_TIMES=@EarlyTimes, LEAVE_EARLY=@EarlySum,
                        ABSENT_TIME=@Absent,
                        WORKTIME=@Worktime, OVERTIME=@Overtime, REST_OVERTIME=@RestOvertime,
                        HOLIDAY_OVERTIME=@HolidayOvertime, REMARK=@Remark
                    WHERE COUNT_DATE=@Date AND LTRIM(RTRIM(EMP_ID))=@EmpId;
                    """, connection);
                update.Parameters.Add("@TimeType", SqlDbType.NChar, 10).Value = timeTypeId.Trim();
                for (var slot = 0; slot < 4; slot++)
                {
                    update.Parameters.Add($"@On{slot + 1}", SqlDbType.Char, 5).Value = (object?)on[slot] ?? DBNull.Value;
                    update.Parameters.Add($"@Out{slot + 1}", SqlDbType.Char, 5).Value = (object?)outPunch[slot] ?? DBNull.Value;
                    update.Parameters.Add($"@Late{slot + 1}", SqlDbType.Float).Value = lateFor[slot] > 0 ? lateFor[slot] : DBNull.Value;
                    update.Parameters.Add($"@Early{slot + 1}", SqlDbType.Float).Value = leaveEarly[slot] > 0 ? leaveEarly[slot] : DBNull.Value;
                }
                update.Parameters.Add("@LateTimes", SqlDbType.Float).Value = lateTimes > 0 ? lateTimes : DBNull.Value;
                update.Parameters.Add("@LateSum", SqlDbType.Float).Value = lateForSum > 0 ? lateForSum : DBNull.Value;
                update.Parameters.Add("@EarlyTimes", SqlDbType.Float).Value = leaveEarlyTimes > 0 ? leaveEarlyTimes : DBNull.Value;
                update.Parameters.Add("@EarlySum", SqlDbType.Float).Value = leaveEarlySum > 0 ? leaveEarlySum : DBNull.Value;
                update.Parameters.Add("@Absent", SqlDbType.Float).Value = absent > 0 ? absent : DBNull.Value;
                update.Parameters.Add("@Worktime", SqlDbType.Float).Value = worktime > 0 ? worktime : DBNull.Value;
                update.Parameters.Add("@Overtime", SqlDbType.Float).Value = overtime > 0 ? overtime : DBNull.Value;
                update.Parameters.Add("@RestOvertime", SqlDbType.Float).Value = restOvertime > 0 ? restOvertime : DBNull.Value;
                update.Parameters.Add("@HolidayOvertime", SqlDbType.Float).Value = holidayOvertime > 0 ? holidayOvertime : DBNull.Value;
                update.Parameters.Add("@Remark", SqlDbType.NVarChar, 500).Value = remark;
                update.Parameters.Add("@Date", SqlDbType.SmallDateTime).Value = date.Date;
                update.Parameters.Add("@EmpId", SqlDbType.NChar, 10).Value = empId;
                var affected = await update.ExecuteNonQueryAsync(token);
                if (affected > 0) updated++;
                diaryRows++;
            }
        }
        logger.LogInformation("考勤计算完成 employees={Employees} rows={Rows} updated={Updated} noTimeType={NoTimeType}",
            employees.Count, diaryRows, updated, skippedNoTimeType);
        return new CalcResult(employees.Count, diaryRows, updated, skippedNoTimeType, skippedNotActive);
    }

    private static double ParseTime(string time)
    {
        var parts = time.Trim().Split(':');
        var hours = parts.Length > 0 && int.TryParse(parts[0], out var h) ? h : 0;
        var minutes = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
        return hours * 60 + minutes;
    }

    private sealed record PunchRow(string EmpId, DateTime Time);

    private static async Task<List<PunchRow>> LoadPunchesAsync(
        SqlConnection connection, DateTime start, DateTime end, IReadOnlyList<string> employees, CancellationToken token)
    {
        var result = new List<PunchRow>();
        if (employees.Count == 0) return result;
        var placeholders = string.Join(',', employees.Select((_, i) => $"@e{i}"));
        await using var command = new SqlCommand($"""
            SELECT LTRIM(RTRIM(c.EMP_ID)), m.COUNT_DATE
            FROM dbo.HR_DIARY_D m
            INNER JOIN dbo.HR_EMPLOYEE_CARD c ON c.CARD_ID=m.CARD_ID AND c.CONFIRM_TAG=1
            WHERE m.COUNT_DATE>=@Start AND m.COUNT_DATE<@End
              AND c.EMP_ID IN ({placeholders})
              AND m.COUNT_DATE>=c.BEGIN_DATE AND (c.END_DATE IS NULL OR m.COUNT_DATE<DATEADD(day,1,c.END_DATE));
            """, connection);
        command.Parameters.Add("@Start", SqlDbType.SmallDateTime).Value = start.Date.AddDays(-1);
        command.Parameters.Add("@End", SqlDbType.SmallDateTime).Value = end.Date.AddDays(1);
        for (var i = 0; i < employees.Count; i++)
            command.Parameters.Add($"@e{i}", SqlDbType.NChar, 10).Value = employees[i];
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new PunchRow(reader.GetString(0), reader.GetDateTime(1)));
        return result;
    }

    private static DateTime? MatchPunch(
        List<PunchRow> punches, string empId, DateTime date, TimeTypeRow tt, int slot, bool isIn)
    {
        var timeText = isIn ? tt.InTime[slot] : tt.OutTime[slot];
        var moreDay = isIn ? tt.InMoreDay[slot] : tt.OutMoreDay[slot];
        var fore = isIn ? tt.InForeMinute[slot] : tt.OutForeMinute[slot];
        var back = isIn ? tt.InBackMinute[slot] : tt.OutBackMinute[slot];
        if (string.IsNullOrWhiteSpace(timeText)) return null;
        var scheduled = date.Date.AddDays(moreDay ? 1 : 0).AddMinutes(ParseTime(timeText));
        var windowStart = scheduled.AddMinutes(-fore);
        var windowEnd = scheduled.AddMinutes(back);
        return punches
            .Where(p => p.EmpId.Equals(empId, StringComparison.OrdinalIgnoreCase)
                     && p.Time >= windowStart && p.Time <= windowEnd)
            .OrderBy(p => p.Time)
            .Select(p => (DateTime?)p.Time)
            .FirstOrDefault();
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

    private sealed record TimeTypeRow(
        string[] InTime, string[] OutTime, double[] WorkHours, double[] AddHours,
        bool[] InMoreDay, bool[] OutMoreDay, int[] InLateMinute, int[] OutLateMinute,
        int[] InForeMinute, int[] InBackMinute, int[] OutForeMinute, int[] OutBackMinute,
        bool[] InCheck, bool[] OutCheck, bool[] IfOvertime, bool[] IsOutFlex, bool IfConfirm);

    private static async Task<Dictionary<string, TimeTypeRow>> LoadTimeTypesAsync(SqlConnection connection, CancellationToken token)
    {
        var result = new Dictionary<string, TimeTypeRow>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(TIMETYPE_ID)),
                   ISNULL(CAST(IF_CONFIRM AS int),0),
                   LTRIM(RTRIM(ISNULL(IN_TIME1,''))),LTRIM(RTRIM(ISNULL(OUT_TIME1,''))),ISNULL(CAST(WORK_HOURS1 AS float),0),ISNULL(CAST(ADD_HOURS1 AS float),0),ISNULL(CAST(IN_MOREDAY1 AS int),0),ISNULL(CAST(OUT_MOREDAY1 AS int),0),ISNULL(CAST(IN_LATE_MINUTE1 AS int),0),ISNULL(CAST(OUT_LATE_MINUTE1 AS int),0),ISNULL(CAST(IN_FORE_MINUTE1 AS int),0),ISNULL(CAST(IN_BACK_MINUTE1 AS int),0),ISNULL(CAST(OUT_FORE_MINUTE1 AS int),0),ISNULL(CAST(OUT_BACK_MINUTE1 AS int),0),ISNULL(CAST(IN_CHECK1 AS int),0),ISNULL(CAST(OUT_CHECK1 AS int),0),ISNULL(CAST(IF_OVERTIME1 AS int),0),ISNULL(CAST(IS_OUT_FLEX1 AS int),0),
                   LTRIM(RTRIM(ISNULL(IN_TIME2,''))),LTRIM(RTRIM(ISNULL(OUT_TIME2,''))),ISNULL(CAST(WORK_HOURS2 AS float),0),ISNULL(CAST(ADD_HOURS2 AS float),0),ISNULL(CAST(IN_MOREDAY2 AS int),0),ISNULL(CAST(OUT_MOREDAY2 AS int),0),ISNULL(CAST(IN_LATE_MINUTE2 AS int),0),ISNULL(CAST(OUT_LATE_MINUTE2 AS int),0),ISNULL(CAST(IN_FORE_MINUTE2 AS int),0),ISNULL(CAST(IN_BACK_MINUTE2 AS int),0),ISNULL(CAST(OUT_FORE_MINUTE2 AS int),0),ISNULL(CAST(OUT_BACK_MINUTE2 AS int),0),ISNULL(CAST(IN_CHECK2 AS int),0),ISNULL(CAST(OUT_CHECK2 AS int),0),ISNULL(CAST(IF_OVERTIME2 AS int),0),ISNULL(CAST(IS_OUT_FLEX2 AS int),0),
                   LTRIM(RTRIM(ISNULL(IN_TIME3,''))),LTRIM(RTRIM(ISNULL(OUT_TIME3,''))),ISNULL(CAST(WORK_HOURS3 AS float),0),ISNULL(CAST(ADD_HOURS3 AS float),0),ISNULL(CAST(IN_MOREDAY3 AS int),0),ISNULL(CAST(OUT_MOREDAY3 AS int),0),ISNULL(CAST(IN_LATE_MINUTE3 AS int),0),ISNULL(CAST(OUT_LATE_MINUTE3 AS int),0),ISNULL(CAST(IN_FORE_MINUTE3 AS int),0),ISNULL(CAST(IN_BACK_MINUTE3 AS int),0),ISNULL(CAST(OUT_FORE_MINUTE3 AS int),0),ISNULL(CAST(OUT_BACK_MINUTE3 AS int),0),ISNULL(CAST(IN_CHECK3 AS int),0),ISNULL(CAST(OUT_CHECK3 AS int),0),ISNULL(CAST(IF_OVERTIME3 AS int),0),ISNULL(CAST(IS_OUT_FLEX3 AS int),0),
                   LTRIM(RTRIM(ISNULL(IN_TIME4,''))),LTRIM(RTRIM(ISNULL(OUT_TIME4,''))),ISNULL(CAST(WORK_HOURS4 AS float),0),ISNULL(CAST(ADD_HOURS4 AS float),0),ISNULL(CAST(IN_MOREDAY4 AS int),0),ISNULL(CAST(OUT_MOREDAY4 AS int),0),ISNULL(CAST(IN_LATE_MINUTE4 AS int),0),ISNULL(CAST(OUT_LATE_MINUTE4 AS int),0),ISNULL(CAST(IN_FORE_MINUTE4 AS int),0),ISNULL(CAST(IN_BACK_MINUTE4 AS int),0),ISNULL(CAST(OUT_FORE_MINUTE4 AS int),0),ISNULL(CAST(OUT_BACK_MINUTE4 AS int),0),ISNULL(CAST(IN_CHECK4 AS int),0),ISNULL(CAST(OUT_CHECK4 AS int),0),ISNULL(CAST(IF_OVERTIME4 AS int),0),ISNULL(CAST(IS_OUT_FLEX4 AS int),0)
            FROM dbo.HR_TIMETYPE;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var idx = 0;
            var inTime = new string[4];
            var outTime = new string[4];
            var workHours = new double[4];
            var addHours = new double[4];
            var inMoreDay = new bool[4];
            var outMoreDay = new bool[4];
            var inLate = new int[4];
            var outLate = new int[4];
            var inFore = new int[4];
            var inBack = new int[4];
            var outFore = new int[4];
            var outBack = new int[4];
            var inCheck = new bool[4];
            var outCheck = new bool[4];
            var ifOvertime = new bool[4];
            var isOutFlex = new bool[4];
            var idxBase = 1;
            for (var slot = 0; slot < 4; slot++)
            {
                inTime[slot] = reader.GetString(1 + idxBase + idx);
                outTime[slot] = reader.GetString(2 + idxBase + idx);
                workHours[slot] = reader.GetDouble(3 + idxBase + idx);
                addHours[slot] = reader.GetDouble(4 + idxBase + idx);
                inMoreDay[slot] = reader.GetInt32(5 + idxBase + idx) == 1;
                outMoreDay[slot] = reader.GetInt32(6 + idxBase + idx) == 1;
                inLate[slot] = reader.GetInt32(7 + idxBase + idx);
                outLate[slot] = reader.GetInt32(8 + idxBase + idx);
                inFore[slot] = reader.GetInt32(9 + idxBase + idx);
                inBack[slot] = reader.GetInt32(10 + idxBase + idx);
                outFore[slot] = reader.GetInt32(11 + idxBase + idx);
                outBack[slot] = reader.GetInt32(12 + idxBase + idx);
                inCheck[slot] = reader.GetInt32(13 + idxBase + idx) == 1;
                outCheck[slot] = reader.GetInt32(14 + idxBase + idx) == 1;
                ifOvertime[slot] = reader.GetInt32(15 + idxBase + idx) == 1;
                isOutFlex[slot] = reader.GetInt32(16 + idxBase + idx) == 1;
                idx += 16;
            }
            var ifConfirm = reader.GetInt32(1) == 1;
            result[reader.GetString(0)] = new TimeTypeRow(inTime, outTime, workHours, addHours,
                inMoreDay, outMoreDay, inLate, outLate, inFore, inBack, outFore, outBack,
                inCheck, outCheck, ifOvertime, isOutFlex, ifConfirm);
        }
        return result;
    }

    private sealed record PlanAdjustRow(string EmpId, string CountDate,
        string?[] InTime, string?[] OutTime, double?[] WorkHours, double?[] AddHours);
    private static async Task<List<PlanAdjustRow>> LoadPlanAdjustsAsync(
        SqlConnection connection, DateTime start, DateTime end, CancellationToken token)
    {
        var result = new List<PlanAdjustRow>();
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(d.EMP_ID)), CONVERT(varchar(10),d.COUNT_DATE,120),
                   NULLIF(LTRIM(RTRIM(ISNULL(d.IN_TIME1,''))),''),NULLIF(LTRIM(RTRIM(ISNULL(d.OUT_TIME1,''))),''),d.WORK_HOURS1,d.ADD_HOURS1,
                   NULLIF(LTRIM(RTRIM(ISNULL(d.IN_TIME2,''))),''),NULLIF(LTRIM(RTRIM(ISNULL(d.OUT_TIME2,''))),''),d.WORK_HOURS2,d.ADD_HOURS2,
                   NULLIF(LTRIM(RTRIM(ISNULL(d.IN_TIME3,''))),''),NULLIF(LTRIM(RTRIM(ISNULL(d.OUT_TIME3,''))),''),d.WORK_HOURS3,d.ADD_HOURS3,
                   NULLIF(LTRIM(RTRIM(ISNULL(d.IN_TIME4,''))),''),NULLIF(LTRIM(RTRIM(ISNULL(d.OUT_TIME4,''))),''),d.WORK_HOURS4,d.ADD_HOURS4
            FROM dbo.HR_ADJUST_M m INNER JOIN dbo.HR_ADJUST_D d
              ON d.ADJUST_TYPE=m.ADJUST_TYPE AND d.ADJUST_NO=m.ADJUST_NO
            WHERE m.CONFIRM_TAG=1 AND d.COUNT_DATE>=@Start AND d.COUNT_DATE<=@End;
            """, connection);
        command.Parameters.Add("@Start", SqlDbType.SmallDateTime).Value = start.Date;
        command.Parameters.Add("@End", SqlDbType.SmallDateTime).Value = end.Date;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var inTime = new string?[4];
            var outTime = new string?[4];
            var workHours = new double?[4];
            var addHours = new double?[4];
            for (var slot = 0; slot < 4; slot++)
            {
                inTime[slot] = reader.IsDBNull(2 + slot * 4) ? null : reader.GetString(2 + slot * 4);
                outTime[slot] = reader.IsDBNull(3 + slot * 4) ? null : reader.GetString(3 + slot * 4);
                workHours[slot] = reader.IsDBNull(4 + slot * 4) ? (double?)null : reader.GetDouble(4 + slot * 4);
                addHours[slot] = reader.IsDBNull(5 + slot * 4) ? (double?)null : reader.GetDouble(5 + slot * 4);
            }
            result.Add(new PlanAdjustRow(reader.GetString(0), reader.GetString(1), inTime, outTime, workHours, addHours));
        }
        return result;
    }

    private sealed record RecessRow(string EmpId, DateTime Start, DateTime End, string? StartTime, string? EndTime);
    private static async Task<List<RecessRow>> LoadRecessesAsync(
        SqlConnection connection, DateTime start, DateTime end, CancellationToken token)
    {
        var result = new List<RecessRow>();
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(d.EMP_ID)), d.START_DATE, d.END_DATE,
                   NULLIF(LTRIM(RTRIM(ISNULL(d.START_TIME,''))),''), NULLIF(LTRIM(RTRIM(ISNULL(d.END_TIME,''))),'')
            FROM dbo.HR_RECESS_M m INNER JOIN dbo.HR_RECESS_D d
              ON d.RECESS_TYPE=m.RECESS_TYPE AND d.RECESS_NO=m.RECESS_NO
            WHERE m.CONFIRM_TAG=1 AND d.START_DATE<=@End AND d.END_DATE>=@Start;
            """, connection);
        command.Parameters.Add("@Start", SqlDbType.SmallDateTime).Value = start.Date;
        command.Parameters.Add("@End", SqlDbType.SmallDateTime).Value = end.Date;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new RecessRow(reader.GetString(0), reader.GetDateTime(1), reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    private sealed record LeaveRow(string EmpId, DateTime Start, DateTime End, string? StartTime, string? EndTime);
    private static async Task<List<LeaveRow>> LoadLeavesAsync(
        SqlConnection connection, DateTime start, DateTime end, CancellationToken token)
    {
        var result = new List<LeaveRow>();
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(d.EMP_ID)), d.START_DATE, d.END_DATE,
                   NULLIF(LTRIM(RTRIM(ISNULL(d.START_TIME,''))),''), NULLIF(LTRIM(RTRIM(ISNULL(d.END_TIME,''))),'')
            FROM dbo.HR_LEAVE_M m INNER JOIN dbo.HR_LEAVE_D d
              ON d.LEAVE_TYPE=m.LEAVE_TYPE AND d.LEAVE_NO=m.LEAVE_NO
            WHERE m.CONFIRM_TAG=1 AND d.START_DATE<=@End AND d.END_DATE>=@Start;
            """, connection);
        command.Parameters.Add("@Start", SqlDbType.SmallDateTime).Value = start.Date;
        command.Parameters.Add("@End", SqlDbType.SmallDateTime).Value = end.Date;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new LeaveRow(reader.GetString(0), reader.GetDateTime(1), reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    private sealed record EvectionRow(string EmpId, DateTime Start, DateTime End, string? StartTime, string? EndTime);
    private static async Task<List<EvectionRow>> LoadEvectionsAsync(
        SqlConnection connection, DateTime start, DateTime end, CancellationToken token)
    {
        var result = new List<EvectionRow>();
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(d.EMP_ID)), d.START_DATE, d.END_DATE,
                   NULLIF(LTRIM(RTRIM(ISNULL(d.START_TIME,''))),''), NULLIF(LTRIM(RTRIM(ISNULL(d.END_TIME,''))),'')
            FROM dbo.HR_EVECTION_M m INNER JOIN dbo.HR_EVECTION_D d
              ON d.EVECTION_TYPE=m.EVECTION_TYPE AND d.EVECTION_NO=m.EVECTION_NO
            WHERE m.CONFIRM_TAG=1 AND d.START_DATE<=@End AND d.END_DATE>=@Start;
            """, connection);
        command.Parameters.Add("@Start", SqlDbType.SmallDateTime).Value = start.Date;
        command.Parameters.Add("@End", SqlDbType.SmallDateTime).Value = end.Date;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new EvectionRow(reader.GetString(0), reader.GetDateTime(1), reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
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

    private sealed record SignRow(string EmpId, string CountDate, string?[] On, string?[] Out);
    private static async Task<List<SignRow>> LoadSignsAsync(
        SqlConnection connection, DateTime start, DateTime end, CancellationToken token)
    {
        var result = new List<SignRow>();
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(d.EMP_ID)), CONVERT(varchar(10),d.COUNT_DATE,120),
                   NULLIF(LTRIM(RTRIM(ISNULL(d.ON1,''))),''),NULLIF(LTRIM(RTRIM(ISNULL(d.OUT1,''))),''),
                   NULLIF(LTRIM(RTRIM(ISNULL(d.ON2,''))),''),NULLIF(LTRIM(RTRIM(ISNULL(d.OUT2,''))),''),
                   NULLIF(LTRIM(RTRIM(ISNULL(d.ON3,''))),''),NULLIF(LTRIM(RTRIM(ISNULL(d.OUT3,''))),''),
                   NULLIF(LTRIM(RTRIM(ISNULL(d.ON4,''))),''),NULLIF(LTRIM(RTRIM(ISNULL(d.OUT4,''))),'')
            FROM dbo.HR_SIGN_M m INNER JOIN dbo.HR_SIGN_D d
              ON d.SIGN_TYPE=m.SIGN_TYPE AND d.SIGN_NO=m.SIGN_NO
            WHERE m.CONFIRM_TAG=1 AND d.COUNT_DATE>=@Start AND d.COUNT_DATE<=@End;
            """, connection);
        command.Parameters.Add("@Start", SqlDbType.SmallDateTime).Value = start.Date;
        command.Parameters.Add("@End", SqlDbType.SmallDateTime).Value = end.Date;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var on = new string?[4];
            var outPunch = new string?[4];
            for (var slot = 0; slot < 4; slot++)
            {
                on[slot] = reader.IsDBNull(2 + slot * 2) ? null : reader.GetString(2 + slot * 2);
                outPunch[slot] = reader.IsDBNull(3 + slot * 2) ? null : reader.GetString(3 + slot * 2);
            }
            result.Add(new SignRow(reader.GetString(0), reader.GetString(1), on, outPunch));
        }
        return result;
    }

    private sealed record ApplyRow(string EmpId, string CountDate,
        double Worktime, double Overtime, double RestOvertime, double HolidayOvertime);
    private static async Task<List<ApplyRow>> LoadAppliesAsync(
        SqlConnection connection, DateTime start, DateTime end, CancellationToken token)
    {
        var result = new List<ApplyRow>();
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(d.EMP_ID)), CONVERT(varchar(10),m.COUNT_DATE,120),
                   ISNULL(CAST(d.WORKTIME AS float),0), ISNULL(CAST(d.OVERTIME AS float),0),
                   ISNULL(CAST(d.REST_OVERTIME AS float),0), ISNULL(CAST(d.HOLIDAY_OVERTIME AS float),0)
            FROM dbo.HR_APPLY_M m INNER JOIN dbo.HR_APPLY_D d
              ON d.APPLY_TYPE=m.APPLY_TYPE AND d.APPLY_NO=m.APPLY_NO
            WHERE m.CONFIRM_TAG=1 AND m.COUNT_DATE>=@Start AND m.COUNT_DATE<=@End;
            """, connection);
        command.Parameters.Add("@Start", SqlDbType.SmallDateTime).Value = start.Date;
        command.Parameters.Add("@End", SqlDbType.SmallDateTime).Value = end.Date;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new ApplyRow(reader.GetString(0), reader.GetString(1),
                reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5)));
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
