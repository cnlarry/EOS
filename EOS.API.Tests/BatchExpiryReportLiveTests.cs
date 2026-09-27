using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 临期/过期报表（P3）：阈值必须**真的读系统参数**（改了参数清单范围跟着变），
/// 且报表口径与库内直查逐行一致。
///
/// 与其余真库用例的差别：报表仓储自带连接（读不到未提交的行），夹具必须提交，
/// 因此只用自己的料号/库别，收尾按料号精确清干净并断言零残留；
/// 阈值改动（系统参数）在 finally 里还原成原值。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class BatchExpiryReportLiveTests
{
    private const string ReportId = "INV_Batch_Expiry_1";
    private const string Module = "1303";
    private const string Product = "ADR25RPPRO";
    private const string Depot = "ADR25RPDP";
    private const string ExpiryDaysKey = "EXPIRY_ALERT_DAYS";

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    [Fact]
    public async Task 阈值来自系统参数_改小改大清单范围随之变化()
    {
        await WithFixtureAsync(async repository =>
        {
            var original = await ReadAlertDaysAsync();

            await SetAlertDaysAsync(30);
            var at30 = await RunReportAsync(repository, includeExpired: "0", unmanagedOnly: "0");
            Assert.Equal(new[] { "ADR25RPLOT_NEAR" }, BatchNos(at30));

            // 阈值改小到 1 天：5 天后到期的那批不再算临期
            await SetAlertDaysAsync(1);
            var at1 = await RunReportAsync(repository, includeExpired: "0", unmanagedOnly: "0");
            Assert.Empty(at1);

            // 阈值改大到 3650 天：100 天后到期的也进来了 —— 证明读的是参数，不是硬编码
            await SetAlertDaysAsync(3650);
            var atLarge = await RunReportAsync(repository, includeExpired: "0", unmanagedOnly: "0");
            Assert.Equal(new[] { "ADR25RPLOT_NEAR", "ADR25RPLOT_FAR" }, BatchNos(atLarge));

            await SetAlertDaysAsync(original);
        });
    }

    [Fact]
    public async Task 含过期与只看不受管控两项筛选各自成立()
    {
        await WithFixtureAsync(async repository =>
        {
            await SetAlertDaysAsync(30);

            var withoutExpired = await RunReportAsync(repository, includeExpired: "0", unmanagedOnly: "0");
            Assert.DoesNotContain("ADR25RPLOT_EXPIRED", BatchNos(withoutExpired));

            var withExpired = await RunReportAsync(repository, includeExpired: "1", unmanagedOnly: "0");
            Assert.Equal(new[] { "ADR25RPLOT_EXPIRED", "ADR25RPLOT_NEAR" }, BatchNos(withExpired));

            // "不受管控"是开档 3 之前要补齐的作业清单：单列出来，不混在临期里
            var unmanaged = await RunReportAsync(repository, includeExpired: "0", unmanagedOnly: "1");
            Assert.Equal(new[] { "ADR25RPLOT_NONE" }, BatchNos(unmanaged));
            Assert.Equal("不受管控", unmanaged[0]["EXPIRY_STATE"]);
        });
    }

    /// <summary>报表口径与**独立写的库内直查**逐行一致（不是把注册表的 SQL 抄一遍来自证）。</summary>
    [Fact]
    public async Task 报表与库内直查逐行一致()
    {
        await WithFixtureAsync(async repository =>
        {
            await SetAlertDaysAsync(30);
            var rows = await RunReportAsync(repository, includeExpired: "1", unmanagedOnly: "0");

            var direct = await DirectAsync();
            Assert.Equal(direct.Count, rows.Count);
            for (var index = 0; index < direct.Count; index++)
            {
                Assert.Equal(direct[index].BatchNo, rows[index]["BATCH_NO"]);
                Assert.Equal(direct[index].ProductNo, rows[index]["PRO_NO"]);
                Assert.Equal(direct[index].DepotId, rows[index]["DEPOT_ID"]);
                Assert.Equal(direct[index].RemainingDays, Convert.ToInt32(rows[index]["REMAINING_DAYS"]));
                Assert.Equal(direct[index].Quantity, Convert.ToDouble(rows[index]["QTY"]), 3);
                Assert.Equal(direct[index].State, rows[index]["EXPIRY_STATE"]);
            }
        });
    }

    private sealed record DirectRow(
        string BatchNo, string ProductNo, string DepotId, int RemainingDays, double Quantity, string State);

    /// <summary>独立口径的直查：只查本用例自造的料号，算式按业务定义手写。</summary>
    private static async Task<IReadOnlyList<DirectRow>> DirectAsync()
    {
        await using var connection = new SqlConnection(RequireConnection());
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(d.BATCH_NO)), LTRIM(RTRIM(d.PRO_NO)), LTRIM(RTRIM(d.DEPOT_ID)),
                   DATEDIFF(day, CAST(GETDATE() AS date), m.EFFECT_DATE),
                   ISNULL(d.QTY, 0),
                   CASE WHEN m.EFFECT_DATE < CAST(GETDATE() AS date) THEN N'已过期' ELSE N'正常' END
            FROM dbo.INV_PRO_DEPOT d
            JOIN dbo.INV_BATCH_M m ON m.PRO_NO = d.PRO_NO AND LTRIM(RTRIM(ISNULL(m.BATCH_NO, N''))) = LTRIM(RTRIM(ISNULL(d.BATCH_NO, N'')))
            WHERE d.PRO_NO = @Pro AND ISNULL(d.QTY, 0) <> 0 AND m.EFFECT_DATE IS NOT NULL
              AND DATEDIFF(day, CAST(GETDATE() AS date), m.EFFECT_DATE) <= 30
            ORDER BY DATEDIFF(day, CAST(GETDATE() AS date), m.EFFECT_DATE), d.BATCH_NO;
            """, connection);
        command.Parameters.AddWithValue("@Pro", Product);
        var rows = new List<DirectRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new DirectRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetDouble(4), reader.GetString(5)));
        }
        return rows;
    }

    private static IReadOnlyList<string> BatchNos(IReadOnlyList<Dictionary<string, object?>> rows) =>
        rows.Select(row => (string)row["BATCH_NO"]!).ToList();

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> RunReportAsync(
        ReportRepository repository, string includeExpired, string unmanagedOnly)
    {
        var aggregate = ReportAggregateRegistry.Find(ReportId);
        Assert.NotNull(aggregate);
        var definition = new ReportDefinition(
            ModuleId: int.Parse(Module), Title: "批次效期与临期清单", MasterTable: "INV_PRO_DEPOT",
            DetailTable: null, Conditions: [], Columns: [], MasterPkOrder: [], SortFields: [])
        {
            Aggregate = aggregate,
        };
        var request = new ReportQueryRequest(
            new Dictionary<int, string?> { [1] = includeExpired, [2] = unmanagedOnly },
            new Dictionary<int, string?>());
        var result = await repository.QueryAsync(definition, request, 1, 200, null, CancellationToken.None);
        return result.Rows.ToList();
    }

    private static async Task<int> ReadAlertDaysAsync()
    {
        await using var connection = new SqlConnection(RequireConnection());
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT ISNULL(PARAM_VALUE, N'') FROM dbo.SYSSS WHERE OWNER_MODULE = 110111 AND PARAM_KEY = @Key;",
            connection);
        command.Parameters.AddWithValue("@Key", ExpiryDaysKey);
        return int.Parse((string)(await command.ExecuteScalarAsync())!);
    }

    private static async Task SetAlertDaysAsync(int days)
    {
        await using var connection = new SqlConnection(RequireConnection());
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "UPDATE dbo.SYSSS SET PARAM_VALUE = @Value WHERE OWNER_MODULE = 110111 AND PARAM_KEY = @Key;",
            connection);
        command.Parameters.AddWithValue("@Value", days.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@Key", ExpiryDaysKey);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>
    /// 提交式夹具：自造料号 + 自造库别 + 四个批次/余额行（过期 / 5 天后 / 100 天后 / 不受管控），
    /// 跑完按料号精确清理并断言零残留。
    /// </summary>
    private static async Task WithFixtureAsync(Func<ReportRepository, Task> body)
    {
        var connectionString = RequireConnection();
        var today = DateTime.Today;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            await CleanupAsync(connection);
            await ExecuteAsync(connection, """
                INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH, UNIT_ID)
                    VALUES (@Pro, N'ADR25RP 临期料件', 1, N'PCS');
                INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR25RP 临期仓');
                INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, SEQ_NO, STATUS)
                    VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', 0, N'A');
                INSERT INTO dbo.INV_BATCH_M (BATCH_NO, PRO_NO, IN_SUM, OUT_SUM, EFFECT_DATE, CREATE_PERSON, CREATE_DATE, CONFIRM_TAG, FINISHED_TAG, CI)
                    VALUES (N'ADR25RPLOT_EXPIRED', @Pro, 5, 0, @Expired, N'ADR25RP', SYSDATETIME(), 0, 0, N''),
                           (N'ADR25RPLOT_NEAR', @Pro, 3, 0, @Near, N'ADR25RP', SYSDATETIME(), 0, 0, N''),
                           (N'ADR25RPLOT_FAR', @Pro, 7, 0, @Far, N'ADR25RP', SYSDATETIME(), 0, 0, N''),
                           (N'ADR25RPLOT_NONE', @Pro, 2, 0, NULL, N'ADR25RP', SYSDATETIME(), 0, 0, N'');
                INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, USEABLE_QTY, INIT_QTY, COST_PRICE, COST_AMOUNT)
                    VALUES (@Pro, @Depot, N'-', N'ADR25RPLOT_EXPIRED', 5, 5, 0, 1, 5),
                           (@Pro, @Depot, N'-', N'ADR25RPLOT_NEAR', 3, 3, 0, 1, 3),
                           (@Pro, @Depot, N'-', N'ADR25RPLOT_FAR', 7, 7, 0, 1, 7),
                           (@Pro, @Depot, N'-', N'ADR25RPLOT_NONE', 2, 2, 0, 1, 2);
                """,
                ("@Pro", Product), ("@Depot", Depot),
                ("@Expired", today.AddDays(-1)), ("@Near", today.AddDays(5)), ("@Far", today.AddDays(100)));

            var repository = new ReportRepository(
                PolicyServiceFactory.Connections(connectionString), NullLogger<ReportRepository>.Instance);
            await body(repository);
        }
        finally
        {
            await CleanupAsync(connection);
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.INV_PRO_DEPOT WHERE PRO_NO = @Pro;", connection);
            command.Parameters.AddWithValue("@Pro", Product);
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
    }

    /// <summary>按**自己造的料号**清干净（不按库别整体删：库别上的行不一定都是本用例造的）。</summary>
    private static async Task CleanupAsync(SqlConnection connection) => await ExecuteAsync(connection, """
        DELETE FROM dbo.INV_PRO_DEPOT WHERE PRO_NO = @Pro;
        DELETE FROM dbo.INV_BATCH_M WHERE PRO_NO = @Pro;
        DELETE FROM dbo.PRODUCT WHERE PRO_NO = @Pro;
        DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID = @Depot;
        DELETE FROM dbo.DEPOT WHERE DEPOT_ID = @Depot;
        """, ("@Pro", Product), ("@Depot", Depot));

    private static async Task ExecuteAsync(
        SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
