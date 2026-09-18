using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Human-resource batch jobs (card batch, attendance generation, wage-driven attendance
/// adjustment). Each operation is self-contained over the ERP connection and returns a
/// result record; controllers validate request shape and permissions before calling.
/// The wage-driven adjustment runs P_HRM_WAGE_CALC before clearing diary fields because
/// the procedure manages its own transaction scope.
/// </summary>
public sealed class HumanResourceJobsService(DbConnectionFactory connections)
{
    private static readonly Regex DayColumn = new("^DAY_(0[1-9]|[12][0-9]|3[01])$", RegexOptions.Compiled);
    private static readonly Regex MonthKey = new("^\\d{6}$", RegexOptions.Compiled);

    public sealed record CardBatchResult(int Updated, int Inserted);

    public sealed record AttendanceGenerateResult(
        string Mode, DateTime StartDate, DateTime EndDate, int EmployeeCount, int Inserted, int Filled);

    public sealed record WageAdjustResult(int WageCalcRuns, int AdjustedEmployees, int ClearedDiaryRows);

    /// <summary>Upserts HR_EMPLOYEE_CARD rows inside one transaction; executor is written to audit columns.</summary>
    public async Task<CardBatchResult> BatchCardsAsync(
        IReadOnlyList<CardBatchItem> cards,
        DateTime startDate,
        DateTime? endDate,
        string executor,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var now = DateTime.Now;
        var updated = 0;
        var inserted = 0;
        foreach (var card in cards)
        {
            var empId = (card.EmpId ?? "").Trim();
            var cardId = (card.CardId ?? "").Trim();
            if (empId.Length == 0 || cardId.Length == 0) continue;
            const string existsSql = "SELECT TOP 1 1 FROM dbo.HR_EMPLOYEE_CARD WITH (NOLOCK) WHERE EMP_ID=@e AND CARD_ID=@c;";
            await using var existsCommand = new SqlCommand(existsSql, connection, transaction);
            existsCommand.Parameters.Add("@e", SqlDbType.NVarChar, 30).Value = empId;
            existsCommand.Parameters.Add("@c", SqlDbType.NVarChar, 30).Value = cardId;
            var exists = await existsCommand.ExecuteScalarAsync(token) is not null;
            if (exists)
            {
                await using var update = new SqlCommand(
                    "UPDATE dbo.HR_EMPLOYEE_CARD SET BEGIN_DATE=@bd,END_DATE=@ed,LAST_UPDATE_BY=@u,LAST_UPDATE_DATE=@d WHERE EMP_ID=@e AND CARD_ID=@c;", connection, transaction);
                update.Parameters.Add("@bd", SqlDbType.DateTime).Value = startDate;
                update.Parameters.Add("@ed", SqlDbType.DateTime).Value = (object?)endDate ?? DBNull.Value;
                update.Parameters.Add("@u", SqlDbType.NVarChar, 50).Value = executor;
                update.Parameters.Add("@d", SqlDbType.DateTime).Value = now;
                update.Parameters.Add("@e", SqlDbType.NVarChar, 30).Value = empId;
                update.Parameters.Add("@c", SqlDbType.NVarChar, 30).Value = cardId;
                await update.ExecuteNonQueryAsync(token);
                updated++;
            }
            else
            {
                await using var insert = new SqlCommand(
                    "INSERT INTO dbo.HR_EMPLOYEE_CARD (EMP_ID,CARD_ID,BEGIN_DATE,END_DATE,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE) VALUES (@e,@c,@bd,@ed,@u,@d,@u,@d);", connection, transaction);
                insert.Parameters.Add("@e", SqlDbType.NVarChar, 30).Value = empId;
                insert.Parameters.Add("@c", SqlDbType.NVarChar, 30).Value = cardId;
                insert.Parameters.Add("@bd", SqlDbType.DateTime).Value = startDate;
                insert.Parameters.Add("@ed", SqlDbType.DateTime).Value = (object?)endDate ?? DBNull.Value;
                insert.Parameters.Add("@u", SqlDbType.NVarChar, 50).Value = executor;
                insert.Parameters.Add("@d", SqlDbType.DateTime).Value = now;
                await insert.ExecuteNonQueryAsync(token);
                inserted++;
            }
            // 发新卡即作废旧卡：同一卡号的其他持卡人、同员工名下其他卡（与 180208 同一实现）
            await CardSiblingCloseHandler.CloseAsync(connection, transaction, CardSiblingCloseHandler.CardBatchConfig,
                cardId, empId, startDate, token);
        }
        await transaction.CommitAsync(token);
        return new CardBatchResult(updated, inserted);
    }

    /// <summary>
    /// Creates blank HRM_DIARY rows for the target employees/department over the date range and
    /// fills first-shift times from approved plans. Employees resolve to active records; returns
    /// EmployeeCount=0 when no employee matched.
    /// </summary>
    public async Task<AttendanceGenerateResult> GenerateAttendanceAsync(
        DateTime startDate,
        DateTime endDate,
        string mode,
        string? deptId,
        IReadOnlyList<string>? requestedEmpIds,
        CancellationToken token)
    {
        var empIds = (requestedEmpIds ?? [])
            .Select(id => (id ?? "").Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var createTemp = new SqlCommand("CREATE TABLE #emps(EMP_ID nchar(10) PRIMARY KEY);", connection, transaction))
                await createTemp.ExecuteNonQueryAsync(token);
            int employeeCount;
            if (empIds.Count > 0)
            {
                var existing = await ResolveEmployeeIdsAsync(connection, transaction, empIds, token);
                foreach (var id in existing)
                {
                    await using var insert = new SqlCommand("INSERT INTO #emps(EMP_ID) VALUES(@id);", connection, transaction);
                    insert.Parameters.Add("@id", SqlDbType.NChar, 10).Value = id;
                    await insert.ExecuteNonQueryAsync(token);
                }
                employeeCount = existing.Count;
            }
            else
            {
                await using var byDept = new SqlCommand(
                    "INSERT INTO #emps(EMP_ID) SELECT e.EMP_ID FROM dbo.HR_EMPLOYEE e WHERE e.IF_SHOW=1 AND e.DEPT_ID IN (SELECT DEPT_ID FROM dbo.f_get_under_depts(@dept));", connection, transaction);
                byDept.Parameters.Add("@dept", SqlDbType.NVarChar, 50).Value = deptId!.Trim();
                employeeCount = await byDept.ExecuteNonQueryAsync(token);
            }
            var inserted = 0;
            var filled = 0;
            if (employeeCount > 0)
            {
                for (var date = startDate; date <= endDate; date = date.AddDays(1))
                {
                    await using var skeleton = new SqlCommand(
                        "INSERT INTO dbo.HRM_DIARY(COUNT_DATE,EMP_ID) SELECT @date,EMP_ID FROM #emps e WHERE NOT EXISTS (SELECT 1 FROM dbo.HRM_DIARY d WHERE d.COUNT_DATE=@date AND d.EMP_ID=e.EMP_ID);", connection, transaction);
                    skeleton.Parameters.Add("@date", SqlDbType.SmallDateTime).Value = date;
                    inserted += await skeleton.ExecuteNonQueryAsync(token);
                    var dayColumn = $"DAY_{date.Day:00}";
                    if (DayColumn.IsMatch(dayColumn))
                    {
                        await using var fill = new SqlCommand(
                            $"""
                            UPDATE d SET d.ON1=t.IN_TIME1,d.OUT1=t.OUT_TIME1,d.TIMETYPE_ID=t.TIMETYPE_ID
                            FROM dbo.HRM_DIARY d
                            INNER JOIN dbo.HRM_PLAN_D pd ON pd.EMP_ID=d.EMP_ID
                            INNER JOIN dbo.HRM_PLAN_M pm ON pm.PLAN_TYPE=pd.PLAN_TYPE AND pm.PLAN_NO=pd.PLAN_NO AND pm.CONFIRM_TAG=1
                            INNER JOIN dbo.HRM_TIMETYPE t ON t.TIMETYPE_ID=pd.[{dayColumn}]
                            WHERE d.COUNT_DATE=@date AND pm.COUNT_MONTH=CONVERT(varchar(6),@date,112) AND pd.[{dayColumn}] IS NOT NULL AND LTRIM(RTRIM(pd.[{dayColumn}]))<>'';
                            """, connection, transaction);
                        fill.Parameters.Add("@date", SqlDbType.SmallDateTime).Value = date;
                        filled += await fill.ExecuteNonQueryAsync(token);
                    }
                }
            }
            await transaction.CommitAsync(token);
            return new AttendanceGenerateResult(mode, startDate, endDate, employeeCount, inserted, filled);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>
    /// Adjusts attendance for employees whose wage detail shows a deduction (WAGE_ADD&lt;0):
    /// clears holiday overtime, then rest overtime, then regular overtime, then work time.
    /// Returns null when HR_SETUP has no configured wage adjustment columns.
    /// </summary>
    public async Task<WageAdjustResult?> AdjustWageAttendanceAsync(string month, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var wageFields = await ReadWageAdjustConfigAsync(connection, token);
        if (wageFields is null) return null;
        // P_HRM_WAGE_CALC manages its own transaction scope, so wage calculation and the
        // attendance adjustment run with auto-commit (mirroring the page behavior).
        var calcCount = await RunWageCalcForMonthAsync(connection, null, month, token);
        var adjustments = await LoadWageAdjustmentsAsync(connection, null, month, wageFields, token);
        var affected = await AdjustDiaryByWageAsync(connection, null, adjustments, wageFields, token);
        return new WageAdjustResult(calcCount, adjustments.Count, affected);
    }

    private sealed record WageAdjustConfig(string Add, string Work, string Over, string Rest, string Holiday, string WorkTime, string OverTime, string RestTime, string HoliTime);

    private static async Task<WageAdjustConfig?> ReadWageAdjustConfigAsync(SqlConnection connection, CancellationToken token)
    {
        const string sql = "SELECT LTRIM(RTRIM(ISNULL(WAGE_ADD,''))),LTRIM(RTRIM(ISNULL(WAGE_WORK,''))),LTRIM(RTRIM(ISNULL(WAGE_OVER,''))),LTRIM(RTRIM(ISNULL(WAGE_REST,''))),LTRIM(RTRIM(ISNULL(WAGE_HOLIDAY,''))),LTRIM(RTRIM(ISNULL(WAGE_WORKTIME,''))),LTRIM(RTRIM(ISNULL(WAGE_OVERTIME,''))),LTRIM(RTRIM(ISNULL(WAGE_RESTTIME,''))),LTRIM(RTRIM(ISNULL(WAGE_HOLITIME,''))) FROM dbo.HR_SETUP;";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var config = new WageAdjustConfig(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8));
        await reader.CloseAsync();
        var fields = new[] { config.Add, config.Work, config.Over, config.Rest, config.Holiday, config.WorkTime, config.OverTime, config.RestTime, config.HoliTime };
        if (fields.Any(string.IsNullOrWhiteSpace)) return null;
        if (fields.Any(field => !WorkbenchSql.Identifier.IsMatch(field))) return null;
        var valid = await GetWageDetailColumnsAsync(connection, token);
        return fields.All(valid.Contains) ? config : null;
    }

    private static async Task<HashSet<string>> GetWageDetailColumnsAsync(SqlConnection connection, CancellationToken token)
    {
        const string sql = "SELECT c.name FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=N'HRM_WAGE_D' ORDER BY c.column_id;";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token)) columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<int> RunWageCalcForMonthAsync(SqlConnection connection, SqlTransaction? transaction, string month, CancellationToken token)
    {
        const string listSql = "SELECT LTRIM(RTRIM(WAGE_TYPE)),LTRIM(RTRIM(WAGE_NO)) FROM dbo.HRM_WAGE_M WITH (NOLOCK) WHERE LTRIM(RTRIM(COUNT_MONTH))=@month;";
        var types = new List<(string Type, string No)>();
        await using (var listCommand = new SqlCommand(listSql, connection, transaction))
        {
            listCommand.Parameters.Add("@month", SqlDbType.NVarChar, 10).Value = month;
            await using var reader = await listCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) types.Add((reader.GetString(0), reader.GetString(1)));
        }
        var runs = 0;
        foreach (var (type, no) in types)
        {
            await using var calc = new SqlCommand("EXEC dbo.P_HRM_WAGE_CALC @wage_type=@t,@wage_no=@n,@emp_ids=@e,@calc_mode=@m,@if_secrecy=@s;", connection, transaction);
            calc.Parameters.Add("@t", SqlDbType.NVarChar, 20).Value = type;
            calc.Parameters.Add("@n", SqlDbType.NVarChar, 30).Value = no;
            calc.Parameters.Add("@e", SqlDbType.NVarChar, 100).Value = "";
            calc.Parameters.Add("@m", SqlDbType.NVarChar, 5).Value = WageCalcMode;
            calc.Parameters.Add("@s", SqlDbType.Int).Value = 0;
            await calc.ExecuteNonQueryAsync(token);
            runs++;
        }
        return runs;
    }

    /// <summary>P_HRM_WAGE_CALC calculation mode: A = full recalculation (fixed by the page).</summary>
    private const string WageCalcMode = "A";

    private sealed record WageAdjustment(string EmpId, double Add, double Work, double Over, double Rest, double Holiday, double WorkT, double OverT, double RestT, double HoliT);

    private static async Task<IReadOnlyList<WageAdjustment>> LoadWageAdjustmentsAsync(
        SqlConnection connection, SqlTransaction? transaction, string month, WageAdjustConfig c, CancellationToken token)
    {
        var sql = $"""
            SELECT LTRIM(RTRIM(d.EMP_ID)),d.[{c.Add}],d.[{c.Work}],d.[{c.Over}],d.[{c.Rest}],d.[{c.Holiday}],
                   d.[{c.WorkTime}],d.[{c.OverTime}],d.[{c.RestTime}],d.[{c.HoliTime}]
            FROM dbo.HRM_WAGE_D d
            INNER JOIN dbo.HRM_WAGE_M m ON m.WAGE_TYPE=d.WAGE_TYPE AND m.WAGE_NO=d.WAGE_NO
            WHERE LTRIM(RTRIM(m.COUNT_MONTH))=@month AND d.[{c.Add}]<0;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@month", SqlDbType.NVarChar, 10).Value = month;
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<WageAdjustment>();
        while (await reader.ReadAsync(token))
        {
            rows.Add(new WageAdjustment(
                reader.GetString(0), GetDouble(reader, 1), GetDouble(reader, 2), GetDouble(reader, 3), GetDouble(reader, 4), GetDouble(reader, 5),
                GetDouble(reader, 6), GetDouble(reader, 7), GetDouble(reader, 8), GetDouble(reader, 9)));
        }
        return rows;
    }

    private static double GetDouble(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToDouble(reader.GetValue(ordinal));

    private static async Task<int> AdjustDiaryByWageAsync(
        SqlConnection connection, SqlTransaction? transaction, IReadOnlyList<WageAdjustment> adjustments, WageAdjustConfig c, CancellationToken token)
    {
        if (adjustments.Count == 0) return 0;
        // Employee set goes into a temp table with parameterized writes.
        await using (var createTemp = new SqlCommand("CREATE TABLE #wage_emps(EMP_ID nchar(10) PRIMARY KEY);", connection, transaction))
            await createTemp.ExecuteNonQueryAsync(token);
        foreach (var wage in adjustments)
        {
            await using var insert = new SqlCommand("INSERT INTO #wage_emps(EMP_ID) VALUES(@id);", connection, transaction);
            insert.Parameters.Add("@id", SqlDbType.NChar, 10).Value = wage.EmpId;
            await insert.ExecuteNonQueryAsync(token);
        }
        var diaryRows = await LoadDiaryRowsAsync(connection, transaction, c, token);
        var timeTypes = await LoadTimeTypesAsync(connection, transaction, token);
        var affected = 0;
        foreach (var wage in adjustments)
        {
            var rows = diaryRows.Where(row => row.EmpId == wage.EmpId).OrderByDescending(row => row.CountDate).ToList();
            var dAdd = wage.Add;
            // Clear holiday overtime, then rest overtime, then regular overtime, then work time.
            if (dAdd < 10 && wage.Holiday > 0 && wage.HoliT > 0)
                foreach (var row in rows.Where(r => r.HolidayOvertime > 0).ToList())
                {
                    if (dAdd >= 10) break;
                    dAdd += wage.Holiday / wage.HoliT * row.HolidayOvertime;
                    await ClearDiaryRow(connection, transaction, row, "holiday", timeTypes, token);
                    affected++;
                }
            if (dAdd < 10 && wage.Rest > 0 && wage.RestT > 0)
                foreach (var row in rows.Where(r => r.RestOvertime > 0).ToList())
                {
                    if (dAdd >= 10) break;
                    dAdd += wage.Rest / wage.RestT * row.RestOvertime;
                    await ClearDiaryRow(connection, transaction, row, "rest", timeTypes, token);
                    affected++;
                }
            if (dAdd < 10 && wage.Over > 0 && wage.OverT > 0)
                foreach (var row in rows.Where(r => r.Overtime > 0).ToList())
                {
                    if (dAdd >= 10) break;
                    dAdd += wage.Over / wage.OverT * row.Overtime;
                    await ClearDiaryRow(connection, transaction, row, "over", timeTypes, token);
                    affected++;
                }
            if (dAdd < 10 && wage.Work > 0 && wage.WorkT > 0)
                foreach (var row in rows.Where(r => r.Worktime > 0).ToList())
                {
                    if (dAdd >= 10) break;
                    dAdd += wage.Work / wage.WorkT * row.Worktime;
                    await ClearDiaryRow(connection, transaction, row, "work", timeTypes, token);
                    affected++;
                }
        }
        return affected;
    }

    private sealed record DiaryRow(string EmpId, DateTime CountDate, string? TimeTypeId, double Worktime, double Overtime, double RestOvertime, double HolidayOvertime);

    private static async Task<IReadOnlyList<DiaryRow>> LoadDiaryRowsAsync(
        SqlConnection connection, SqlTransaction? transaction, WageAdjustConfig c, CancellationToken token)
    {
        var sql = $"""
            SELECT LTRIM(RTRIM(d.EMP_ID)),d.COUNT_DATE,LTRIM(RTRIM(ISNULL(d.TIMETYPE_ID,''))),ISNULL(d.WORKTIME,0),ISNULL(d.OVERTIME,0),ISNULL(d.REST_OVERTIME,0),ISNULL(d.HOLIDAY_OVERTIME,0)
            FROM dbo.HRM_DIARY d WITH (NOLOCK)
            INNER JOIN #wage_emps w ON w.EMP_ID=d.EMP_ID
            WHERE (ISNULL(d.WORKTIME,0)>0 OR ISNULL(d.OVERTIME,0)>0 OR ISNULL(d.REST_OVERTIME,0)>0 OR ISNULL(d.HOLIDAY_OVERTIME,0)>0);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<DiaryRow>();
        while (await reader.ReadAsync(token))
            rows.Add(new DiaryRow(reader.GetString(0), reader.GetDateTime(1), reader.IsDBNull(2) ? null : reader.GetString(2), GetDouble(reader, 3), GetDouble(reader, 4), GetDouble(reader, 5), GetDouble(reader, 6)));
        return rows;
    }

    private static async Task<Dictionary<string, bool[]>> LoadTimeTypesAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken token)
    {
        const string sql = "SELECT LTRIM(RTRIM(TIMETYPE_ID)),ISNULL(IF_OVERTIME1,0),ISNULL(IF_OVERTIME2,0),ISNULL(IF_OVERTIME3,0),ISNULL(IF_OVERTIME4,0) FROM dbo.HRM_TIMETYPE WITH (NOLOCK);";
        await using var command = new SqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        var map = new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
            map[reader.GetString(0)] = new[] { !reader.IsDBNull(1) && reader.GetBoolean(1), !reader.IsDBNull(2) && reader.GetBoolean(2), !reader.IsDBNull(3) && reader.GetBoolean(3), !reader.IsDBNull(4) && reader.GetBoolean(4) };
        return map;
    }

    private static async Task ClearDiaryRow(
        SqlConnection connection, SqlTransaction? transaction, DiaryRow row, string mode, IReadOnlyDictionary<string, bool[]> timeTypes, CancellationToken token)
    {
        var fullClear = new[] { "TIMETYPE_ID", "ON1", "ON2", "ON3", "ON4", "OUT1", "OUT2", "OUT3", "OUT4",
            "BE_LATE_FOR1", "BE_LATE_FOR2", "BE_LATE_FOR3", "BE_LATE_FOR4", "BE_LATE_FOR",
            "LEAVE_EARLY1", "LEAVE_EARLY2", "LEAVE_EARLY3", "LEAVE_EARLY4", "LEAVE_EARLY",
            "LATE_TIMES", "LEAVE_EARLY_TIMES", "ON_DUTY_TIME", "SIGN_IN" };
        var segments = new List<string>();
        if (mode == "holiday") segments.AddRange(fullClear.Prepend("HOLIDAY_OVERTIME"));
        else if (mode == "rest") segments.AddRange(fullClear.Prepend("REST_OVERTIME"));
        else if (mode == "work") segments.AddRange(fullClear.Prepend("WORKTIME"));
        else
        {
            segments.Add("OVERTIME");
            if (!string.IsNullOrWhiteSpace(row.TimeTypeId) && timeTypes.TryGetValue(row.TimeTypeId, out var flags))
                for (var i = 0; i < 4; i++)
                    if (flags[i])
                        segments.AddRange(new[] { $"ON{i + 1}", $"OUT{i + 1}", $"BE_LATE_FOR{i + 1}", $"LEAVE_EARLY{i + 1}" });
        }
        var sets = string.Join(',', segments.Select(column => $"[{column}]=NULL"));
        if (mode is "holiday" or "rest" or "work") sets += ",REMARK=''";
        var sql = $"UPDATE dbo.HRM_DIARY SET {sets} WHERE EMP_ID=@e AND COUNT_DATE=@d;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@e", SqlDbType.NChar, 10).Value = row.EmpId;
        command.Parameters.Add("@d", SqlDbType.SmallDateTime).Value = row.CountDate;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<IReadOnlyList<string>> ResolveEmployeeIdsAsync(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> empIds, CancellationToken token)
    {
        var result = new List<string>();
        foreach (var id in empIds)
        {
            await using var check = new SqlCommand("SELECT TOP 1 1 FROM dbo.HR_EMPLOYEE WHERE EMP_ID=@id AND IF_SHOW=1;", connection, transaction);
            check.Parameters.Add("@id", SqlDbType.NChar, 10).Value = id;
            if (await check.ExecuteScalarAsync(token) is not null) result.Add(id);
        }
        return result;
    }
}
