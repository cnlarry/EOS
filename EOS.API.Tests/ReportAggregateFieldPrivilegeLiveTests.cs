using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 汇总报表（聚合数据源）的字段级权限：成本/保密列与主表派生的列走同一套口径，双向验证。
///
/// 聚合列是 SQL 算出来的派生列，没有 <c>FIELDS</c> 行可供反查权限位，因此"漏标"只能表现为
/// **静默多给列**——接口不报错、也不 403，只是无权用户多看到一列单价。这类缺口靠读代码看不出来，
/// 必须用真实配额度的双向断言钉住：无权限时看不到，有权限时照常看到（缺任何一向都不成立）。
///
/// 取数条件偏向库里既有的一对（库别, 料件）只读取样：不写入任何行，因此无需夹具清理。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class ReportAggregateFieldPrivilegeLiveTests
{
    /// <summary>库存日报的宿主模块（报表承载页）与报表编号。</summary>
    private const int Module = 139901;
    private const string ReportId = "INV_Pro_Depot_1";

    private static readonly string[] PriceColumns = ["PRICE_Q", "PRICE_J", "PRICE_X"];

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private static ReportRepository CreateRepository()
        => new(PolicyServiceFactory.Connections(RequireConnection()), NullLogger<ReportRepository>.Instance);

    [Fact]
    public async Task 无成本权限时单价列在定义与查询结果中都不出现()
    {
        var repository = CreateRepository();
        var definition = await GetDefinitionAsync(repository, canViewCost: false);
        var rows = await RunAsync(repository, definition);

        Assert.DoesNotContain(definition.Columns, column => IsPrice(column.Key));
        Assert.Equal(15, definition.Columns.Count);
        foreach (var row in rows)
            foreach (var key in PriceColumns)
                Assert.False(row.ContainsKey(key), $"无成本权限时查询结果不得含 {key}");
    }

    [Fact]
    public async Task 有成本权限时单价列照常可见()
    {
        var repository = CreateRepository();
        var definition = await GetDefinitionAsync(repository, canViewCost: true);
        var rows = await RunAsync(repository, definition);

        foreach (var key in PriceColumns)
            Assert.Contains(definition.Columns, column => column.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(18, definition.Columns.Count);
        foreach (var row in rows)
            foreach (var key in PriceColumns)
                Assert.True(row.ContainsKey(key), $"有成本权限时查询结果应含 {key}");
    }

    private static bool IsPrice(string key) => PriceColumns.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static async Task<ReportDefinition> GetDefinitionAsync(ReportRepository repository, bool canViewCost)
    {
        var definition = await repository.GetDefinitionAsync(Module, "admin", canViewCost, canViewSecrecy: true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), ReportId, CancellationToken.None);
        Assert.NotNull(definition);
        return definition!;
    }

    /// <summary>
    /// 限定到库里既有的一对（库别, 料件）再取数：既保证首页有行（不至于让"行里没有单价列"变成空断言），
    /// 又把聚合范围收在一行上，避免无条件下跑遍全部库存组合。
    /// </summary>
    private static async Task<IReadOnlyList<Dictionary<string, object?>>> RunAsync(
        ReportRepository repository, ReportDefinition definition)
    {
        Assert.Equal("aggregate", definition.DataSource);
        var (depotId, productNo) = await SamplePairAsync();
        var request = new ReportQueryRequest(
            new Dictionary<int, string?>
            {
                [1] = depotId,
                [3] = productNo,
                [4] = "1900-01-01",
            },
            new Dictionary<int, string?>
            {
                [1] = depotId,
                [3] = productNo,
                [4] = "2999-12-31",
            });
        var result = await repository.QueryAsync(definition, request, 1, 50, dataFilter: null, CancellationToken.None);
        Assert.NotEmpty(result.Rows);
        return result.Rows;
    }

    private static async Task<(string DepotId, string ProductNo)> SamplePairAsync()
    {
        await using var connection = new SqlConnection(RequireConnection());
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT TOP 1 LTRIM(RTRIM(i.DEPOT_ID)), LTRIM(RTRIM(i.PRO_NO))
            FROM dbo.INV_PRO_DEPOT i WITH (NOLOCK)
            JOIN dbo.PRODUCT pr WITH (NOLOCK) ON pr.PRO_NO = i.PRO_NO
            WHERE pr.PRO_TYPE = '3'
            ORDER BY i.DEPOT_ID, i.PRO_NO;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "库存余额表里没有可用的（库别, 料件）取样行，无法验证聚合列权限。");
        return (reader.GetString(0), reader.GetString(1));
    }
}
