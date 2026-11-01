using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 引用数据的批核门槛（铁律）：**被引用的外部数据，只要来源表有 APPROVED 列，只能是已批核的行**。
///
/// 判据与模块是否启用批核无关，只看列在不在——历史数据里存在"模块没开批核、数据却带 APPROVED=1/
/// 0"的形状，按模块标志位推断会漏。落点在查询层（选择器读数统一经过的那一处），不靠逐条配置。
///
/// 真库驱动：合成样本证明不了"库里真有未批核的行、也确实被挡住了"。
/// 需要 MSSQL_ERP_CONN。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class ReferenceApprovalLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static WorkbenchChooserService CreateChooser() =>
        new(PolicyServiceFactory.Connections(ConnectionString),
            new WorkbenchScopeFilter(new EOS.API.Telemetry.ApiMetrics()),
            NullLogger<WorkbenchChooserService>.Instance);

    /// <summary>找一张既被选择器引用、又有未批核行的来源表（真库里现成存在，不强造数据）。</summary>
    private static async Task<(string Table, int Total, int Approved)> FindPartiallyApprovedSourceAsync(
        CancellationToken token)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        // 候选：被 FIELD_DATASOURCE 引用、有 APPROVED 列、且有未批核行
        const string sql = """
            SELECT TOP 1 t.name FROM sys.columns c
            JOIN sys.objects t ON t.object_id = c.object_id AND t.type = 'U'
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN (SELECT DISTINCT LTRIM(RTRIM(SOURCE_T_ID)) AS T FROM dbo.FIELD_DATASOURCE) d ON d.T = t.name
            WHERE s.name = N'dbo' AND c.name = N'APPROVED'
              AND EXISTS (SELECT 1 FROM sys.columns p WHERE p.object_id = t.object_id AND p.name = N'PRO_NO')
            ORDER BY t.name;
            """;
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        var table = await command.ExecuteScalarAsync(token) as string;
        Assert.False(string.IsNullOrWhiteSpace(table), "库里没有被选择器引用、带 APPROVED 且含品号列的表，本用例失去取样对象。");

        await using var count = new Microsoft.Data.SqlClient.SqlCommand(
            $"SELECT COUNT_BIG(*), SUM(CASE WHEN APPROVED = 1 THEN 1 ELSE 0 END) FROM dbo.[{table}];", connection);
        await using var reader = await count.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        return (table!, Convert.ToInt32(reader.GetInt64(0)), reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1)));
    }

    [Fact]
    public async Task 选择器只返回已批核的来源行()
    {
        var token = CancellationToken.None;
        var (table, total, approved) = await FindPartiallyApprovedSourceAsync(token);
        Assert.True(approved > 0 && approved < total,
            $"取样表 {table} 需要「部分已批核」（total={total}, approved={approved}）才能判别。");

        var result = await CreateChooser().GetChooserOptionsAsync(
            table, keyword: null, filterField: null, returnItems: null,
            masterValues: null, detailValues: null, conditions: null,
            canViewCost: true, canViewSecrecy: true, deniedFields: new HashSet<string>(),
            dataFilter: null, filterStruct: new ChooserFilterStruct("AND", []), sortField: null, sortDirection: null,
            page: 1, pageSize: 200, execTag: "Z", scopeModuleId: null, formModuleId: 0,
            userId: "ADMIN", token);

        Assert.NotNull(result);
        Assert.Equal(approved, result!.Total);
        Assert.DoesNotContain(result.Rows, row => row.ContainsKey("APPROVED") && row["APPROVED"] is false);
    }

    [Fact]
    public async Task 没有批核列的表不受影响()
    {
        var token = CancellationToken.None;
        // 取一张被选择器引用、但**没有** APPROVED 列的表：判据是"看列在不在"，不该误伤。
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        const string sql = """
            SELECT TOP 1 t.name FROM sys.objects t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN (SELECT DISTINCT LTRIM(RTRIM(SOURCE_T_ID)) AS T FROM dbo.FIELD_DATASOURCE) d ON d.T = t.name
            WHERE s.name = N'dbo' AND t.type = 'U'
              AND NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = N'APPROVED')
              -- 选择器要能选出显示列：没有默认列配置的表会因「显示列数为 0」提前返回空，
              -- 那样测的就不是本用例要验的东西。
              AND EXISTS (SELECT 1 FROM dbo.SYS_DEFAULT_COLUMN dc WHERE LTRIM(RTRIM(dc.T_ID)) = t.name)
            ORDER BY t.name;
            """;
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        var table = await command.ExecuteScalarAsync(token) as string;
        Assert.False(string.IsNullOrWhiteSpace(table), "库里没有「被引用但无 APPROVED 列」的表，本用例失去取样对象。");

        var result = await CreateChooser().GetChooserOptionsAsync(
            table!, keyword: null, filterField: null, returnItems: null,
            masterValues: null, detailValues: null, conditions: null,
            canViewCost: true, canViewSecrecy: true, deniedFields: new HashSet<string>(),
            dataFilter: null, filterStruct: new ChooserFilterStruct("AND", []), sortField: null, sortDirection: null,
            page: 1, pageSize: 200, execTag: "Z", scopeModuleId: null, formModuleId: 0,
            userId: "ADMIN", token);

        Assert.NotNull(result);
        await using var count = new Microsoft.Data.SqlClient.SqlCommand(
            $"SELECT COUNT_BIG(*) FROM dbo.[{table}];", connection);
        var expected = (int)(long)(await count.ExecuteScalarAsync(token) ?? 0L);
        Assert.Equal(Math.Min(expected, 200), result!.Rows.Count);
    }
}


