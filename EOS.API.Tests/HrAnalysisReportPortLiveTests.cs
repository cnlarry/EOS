using System.Data;
using System.Globalization;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// HR 分析报表族（`P_RPT_HR_*`，7 个过程）移植为受控聚合数据源的真库对拍：
/// 在同一批（隔离造数的）数据上分别执行「原过程」与「注册表 SQL」，逐行逐列比较结论。
/// 原过程侧的本体取自 SSDT 快照（按报表编号推导文件名），因此过程下线后证据依然成立。
/// 差异只在两处、且属有意：① 考勤分析表的请假/迟到名单旧实现用游标拼接（顺序不确定、带尾空格），
/// 移植按工号排序、去尾空格 ⇒ 按名字集合比较；② 输出行序不参与比较（集合语义）。
/// 整段在事务内进行，结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class HrAnalysisReportPortLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const string DeptA = "ADR12RNA";
    private const string DeptB = "ADR12RNB";
    private const string AttendanceReportId = "HR_Diary_1";

    /// <summary>过程正文缓存（同一测试类内多次取用）。</summary>
    private static readonly Dictionary<string, string> LegacyBodies = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task 人力状况分析表_移植实现与原过程逐行一致()
        => await AssertReportMatchesLegacyAsync("HR_Employee_1", ["DEPT_ID"], []);

    [Theory]
    [InlineData("HR_Employee_3", "PROVINCE_ID")]
    [InlineData("HR_Employee_4", "NATION_ID")]
    [InlineData("HR_Employee_5", "DIPLOMA_ID")]
    public async Task 人力状况维度分析表_移植实现与原过程逐行一致(string reportId, string dimension)
        => await AssertReportMatchesLegacyAsync(reportId, ["DEPT_ID", dimension], []);

    [Theory]
    [InlineData("HR_Employee_6")]
    [InlineData("HR_Employee_7")]
    public async Task 人力状况年龄段分析表_移植实现与原过程逐行一致(string reportId)
        => await AssertReportMatchesLegacyAsync(reportId, ["DEPT_ID", "AGE_ID"], []);

    [Fact]
    public async Task 考勤分析表_移植实现与原过程逐行一致()
        => await AssertReportMatchesLegacyAsync(AttendanceReportId, ["DEPT_ID"], ["QINGJIA", "CHIDAO"]);

    /// <summary>
    /// 在事务内造一批隔离数据（两个部门 + 覆盖各年龄段/工龄段/在职状态的员工 + 考勤记录），
    /// 两侧读出后比较。造数使用固定前缀，回滚后不留痕迹。
    /// </summary>
    private static async Task AssertReportMatchesLegacyAsync(
        string reportId,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> listColumns)
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);

            var aggregate = ReportAggregateRegistry.Find(reportId);
            Assert.NotNull(aggregate);

            // 全区间：考勤分析表的过程要求日期参数（COUNT_DATE 为 smalldatetime，上限 2079-06-06），
            // 其余报表忽略多余取值
            string[] wide = ["19000101", "20781231"];
            var legacy = await RunLegacyAsync(connection, transaction, reportId, token, wide);
            var ported = await RunPortedAsync(connection, transaction, aggregate!, token, wide);
            AssertSameRows(reportId, legacy, ported, keyColumns, listColumns);

            // 收窄到只覆盖部分考勤记录的区间再比一次
            string[] narrow = ["20240101", "20240131"];
            var legacyRange = await RunLegacyAsync(connection, transaction, reportId, token, narrow);
            var portedRange = await RunPortedAsync(connection, transaction, aggregate!, token, narrow);
            AssertSameRows($"{reportId}(日期区间)", legacyRange, portedRange, keyColumns, listColumns);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        // 数据不存在时补部门；员工与考勤固定前缀，回滚前不与既有数据冲突
        await ExecuteAsync(connection, transaction, """
            IF NOT EXISTS(SELECT 1 FROM dbo.DEPT WHERE DEPT_ID=@A)
                INSERT INTO dbo.DEPT (DEPT_ID, DEPT_NAME) VALUES (@A, N'ADR12 部门A');
            IF NOT EXISTS(SELECT 1 FROM dbo.DEPT WHERE DEPT_ID=@B)
                INSERT INTO dbo.DEPT (DEPT_ID, DEPT_NAME) VALUES (@B, N'ADR12 部门B');
            DELETE FROM dbo.HR_DIARY WHERE EMP_ID LIKE 'ADR12RN%';
            DELETE FROM dbo.HR_EMPLOYEE WHERE EMP_ID LIKE 'ADR12RN%';
            """, token, ("@A", DeptA), ("@B", DeptB));

        // 员工：覆盖在职/离职、生日为空、本月入职/离职、各年龄段与工龄段
        var employees = new (string EmpId, string Dept, string Name, string State, string? Birthday, string? InDate, string? Dimission, string? Province, string? Nation, string? Diploma)[]
        {
            ("ADR12RN01", DeptA, "甲一", "1", "1990-05-06", "2015-03-01", null, "P01", "N01", "D01"),
            ("ADR12RN02", DeptA, "乙二", "2", "2008-11-20", "2023-06-15", null, "P02", "N01", "D02"),
            ("ADR12RN03", DeptA, "丙三", null!, null, null, null, null, null, null),
            ("ADR12RN04", DeptB, "丁四", "1", "1985-01-02", "2005-01-02", "2024-01-10", "P01", "N02", "D01"),
            ("ADR12RN05", DeptB, "戊五", "4", "1998-07-08", "2024-01-20", null, "P02", "N02", "D03"),
            ("ADR12RN06", DeptB, "己六", "5", "1970-02-03", "2020-09-01", "2024-01-25", "P01", "N01", "D01"),
        };
        foreach (var employee in employees)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dbo.HR_EMPLOYEE (EMP_ID, DEPT_ID, EMP_NAME, STATE, BIRTHDAY, IN_DATE, DIMISSION_DATE, PROVINCE_ID, NATION_ID, DIPLOMA_ID)
                VALUES (@EmpId, @Dept, @Name, @State, @Birthday, @InDate, @Dimission, @Province, @Nation, @Diploma);
                """, token,
                ("@EmpId", employee.EmpId), ("@Dept", employee.Dept), ("@Name", employee.Name),
                ("@State", employee.State is null ? null : short.Parse(employee.State, CultureInfo.InvariantCulture)),
                ("@Birthday", employee.Birthday is null ? null : DateTime.Parse(employee.Birthday, CultureInfo.InvariantCulture)),
                ("@InDate", employee.InDate is null ? null : DateTime.Parse(employee.InDate, CultureInfo.InvariantCulture)),
                ("@Dimission", employee.Dimission is null ? null : DateTime.Parse(employee.Dimission, CultureInfo.InvariantCulture)),
                ("@Province", employee.Province), ("@Nation", employee.Nation), ("@Diploma", employee.Diploma));
        }

        // 考勤：区间内出勤/加班（实到）、请假备注、迟到次数
        await ExecuteAsync(connection, transaction, """
            INSERT INTO dbo.HR_DIARY (EMP_ID, COUNT_DATE, WORKTIME, OVERTIME, LATE_TIMES, REMARK) VALUES
                ('ADR12RN01', '2024-01-05', 8, 0, 0, NULL),
                ('ADR12RN02', '2024-01-05', 0, 2, 3, N'{请假}事假'),
                ('ADR12RN04', '2024-01-08', 8, 1, 0, N'{请假}年假'),
                ('ADR12RN05', '2024-02-08', 8, 0, 2, NULL);
            """, token);
    }

    /// <summary>
    /// 跑「原过程」侧：过程本体从 SSDT 快照（`EOS.Database/dbo/Stored Procedures/`）现场读取后
    /// 作为批处理执行 —— 快照是旧行为的版本化存档，因此过程从库里下线后对拍证据依然成立，
    /// 且不必把旧过程正文抄进代码（避免再造一份"翻译来的 C#"）。
    /// 文件名按报表编号推导（`P_RPT_<REPORT_ID>`），代码里不出现过程全名。
    /// </summary>
    private static async Task<List<Dictionary<string, string?>>> RunLegacyAsync(
        SqlConnection connection, SqlTransaction transaction, string reportId, CancellationToken token, IReadOnlyList<string> parameters)
    {
        // 过程体里的临时表按"过程作用域"在建它的批结束时释放；改用 sp_executesql 执行即等价
        // （动态批内的 #temp 随批结束自动释放），避免同一会话里第二次执行撞"已存在 #temp"。
        var diary = reportId.Equals(AttendanceReportId, StringComparison.OrdinalIgnoreCase);
        var batch = diary
            ? "DECLARE @date1 nvarchar(20)=@p1, @date2 nvarchar(20)=@p2;\nEXEC sp_executesql @body, N'@date1 nvarchar(20), @date2 nvarchar(20)', @date1, @date2;"
            : "EXEC sp_executesql @body;";
        await using var command = new SqlCommand(batch, connection, transaction);
        command.Parameters.Add("@body", SqlDbType.NVarChar, -1).Value = LoadLegacyBody(reportId);
        if (diary)
        {
            command.Parameters.AddWithValue("@p1", parameters.Count > 0 ? parameters[0] : DBNull.Value);
            command.Parameters.AddWithValue("@p2", parameters.Count > 1 ? parameters[1] : DBNull.Value);
        }
        return await ReadAllAsync(command, token);
    }

    /// <summary>读取 SSDT 快照中的过程正文（去掉 `CREATE PROCEDURE … AS` 头，保留过程体）。</summary>
    private static string LoadLegacyBody(string reportId)
    {
        if (LegacyBodies.TryGetValue(reportId, out var cached)) return cached;
        var sproc = "P_RPT_" + reportId.ToUpperInvariant();
        var path = Path.Combine(RepoRoot(), "EOS.Database", "dbo", "Stored Procedures", $"{sproc}.sql");
        Assert.True(File.Exists(path), $"缺少 SSDT 快照：{path}");
        var text = File.ReadAllText(path);
        // 头部形态两种：参数与 `AS` 分行写，或 `CREATE PROCEDURE dbo.X AS <body>` 一行到底
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"(?is)^\s*(?:--[^\n]*\n\s*)*CREATE\s+PROCEDURE\s+[^\s(]+.*?\bAS\b");
        Assert.True(match.Success, $"{sproc} 快照缺少 CREATE PROCEDURE ... AS 头");
        var body = text[match.Length..].TrimStart('\r', '\n');
        LegacyBodies[reportId] = body;
        return body;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EOS.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static Task<List<Dictionary<string, string?>>> RunPortedAsync(
        SqlConnection connection, SqlTransaction transaction, ReportAggregate aggregate, CancellationToken token, IReadOnlyList<string> parameters)
    {
        var command = ReportRepository.BuildAggregateSql(aggregate, []);
        return RunAsync(connection, transaction, command, CommandType.Text, token, parameters, aggregate);
    }

    private static async Task<List<Dictionary<string, string?>>> RunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string commandText,
        CommandType commandType,
        CancellationToken token,
        IReadOnlyList<string> parameters,
        ReportAggregate? aggregate = null)
    {
        await using var command = new SqlCommand(commandText, connection, transaction) { CommandType = commandType };
        if (aggregate is not null)
        {
            for (var i = 0; i < aggregate.Parameters.Count; i++)
            {
                var raw = i < parameters.Count ? parameters[i] : null;
                command.Parameters.AddWithValue($"@{aggregate.Parameters[i].Name}", (object?)raw ?? DBNull.Value);
            }
        }
        return await ReadAllAsync(command, token);
    }

    private static async Task<List<Dictionary<string, string?>>> ReadAllAsync(SqlCommand command, CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<Dictionary<string, string?>>();
        while (await reader.ReadAsync(token))
        {
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Format(reader.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }

    private static string? Format(object value)
        => value switch
        {
            string text => text.TrimEnd(),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            double number => number.ToString(CultureInfo.InvariantCulture),
            float number => number.ToString(CultureInfo.InvariantCulture),
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };

    private static void AssertSameRows(
        string label,
        List<Dictionary<string, string?>> legacy,
        List<Dictionary<string, string?>> ported,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> listColumns)
    {
        // 两侧列集合一致（列名与顺序都来自各自实现，顺序差异不算差异，但列集合必须相同）
        Assert.Equal(legacy.FirstOrDefault()?.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase),
            ported.FirstOrDefault()?.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));

        string KeyOf(Dictionary<string, string?> row) => string.Join('|', keyColumns.Select(column => row.GetValueOrDefault(column) ?? string.Empty));

        var legacyRows = legacy.ToDictionary(KeyOf, row => row, StringComparer.OrdinalIgnoreCase);
        var portedRows = ported.ToDictionary(KeyOf, row => row, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(legacyRows.Count, portedRows.Count);
        Assert.Equal(legacyRows.Keys.OrderBy(item => item), portedRows.Keys.OrderBy(item => item));

        foreach (var (key, legacyRow) in legacyRows)
        {
            var portedRow = portedRows[key];
            // 已登记的有意差异：旧实现用游标 `update #temp ... where DEPT_ID=@dept` 回填占比，
            // @dept 为 NULL 时永不命中 ⇒ 无部门分组（脏数据）的占比恒为 NULL；
            // 移植按窗口函数统一计算，同一分组得到真实占比。此处把差异钉住而不是放过。
            var emptyDept = string.IsNullOrWhiteSpace(legacyRow.GetValueOrDefault("DEPT_ID"));
            foreach (var column in legacyRow.Keys)
            {
                if (emptyDept && column.Equals("MAN_PERCENT", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Null(legacyRow[column]);
                    Assert.NotNull(portedRow[column]);
                    continue;
                }
                if (listColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    // 名单列：旧实现用游标拼接（顺序不确定、带尾空格）⇒ 按名字集合比较
                    Assert.Equal(Names(legacyRow[column]), Names(portedRow[column]));
                    continue;
                }
                Assert.True(legacyRow[column] == portedRow[column],
                    $"{label} 行 {key} 列 {column}：旧=[{legacyRow[column]}] 新=[{portedRow[column]}]");
            }
        }
    }

    private static string Names(string? value)
        => string.Join('|', (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(item => item, StringComparer.Ordinal));

    private static async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
