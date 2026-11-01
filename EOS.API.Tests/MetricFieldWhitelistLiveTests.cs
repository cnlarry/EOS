using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Features.Assistant.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 口径计算列白名单的真库用例：确认它取自**权限口径**（字段级禁看/成本/保密/物理列），
/// 而不是该用户的列选择。
///
/// 背景：`sales_amount`（SUM(AMOUNT_TAX) over COP_ORDER_D）曾因授权账号的列选择里没有
/// 「总金额」而被判"字段不可见"，口径直接算不出来——列选择只是显示偏好，不是授权。
/// 用例把这条钉住：同一张表上，工作台定义字段视图缺列时，口径白名单仍须给出该列。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class MetricFieldWhitelistLiveTests
{
    private const int SalesOrderModuleId = 1405;
    private const string SourceTable = "COP_ORDER_D";
    private const string AggregateColumn = "AMOUNT_TAX";

    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text,
                "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_ERP_CONN\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static DbConnectionFactory Connections()
    {
        if (ConnectionString.Value is null)
        {
            throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        return new DbConnectionFactory(config);
    }

    [Fact]
    public async Task Whitelist_Keeps_Aggregate_Column_That_Column_Selection_Omits()
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections,
            NullLogger<WorkbenchDefinitionProvider>.Instance);
        var builder = new WorkbenchDefinitionBuilder(connections, provider,
            TestFormCapability.None, TestBusinessConfig.Reader(connections),
            NullLogger<WorkbenchDefinitionBuilder>.Instance);
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definition = await builder.GetDefinitionAsync(SalesOrderModuleId, "admin", "A", true, true,
            empty, empty, CancellationToken.None);
        Assert.NotNull(definition);

        // 前提：该账号的列选择把明细字段收窄了（这是本用例的对照组，不是断言目标）。
        // 若这条前提不再成立（例如有人给 admin 勾上了「总金额」），说明对照样本失效，
        // 需要换一个列选择更窄的账号或换口径，而不是删掉这条断言。
        Assert.DoesNotContain(definition!.DetailFields, field =>
            string.Equals(field.Key, AggregateColumn, StringComparison.OrdinalIgnoreCase));

        var whitelist = new MetricFieldWhitelist(connections);
        var columns = await whitelist.ReadAsync(SourceTable, true, true, empty, CancellationToken.None);
        Assert.Contains(AggregateColumn, columns);
        Assert.Contains(definition.MasterPkOrder, columns.Contains);
    }

    [Fact]
    public async Task Validator_Accepts_Sales_Amount_On_Sales_Order_Module()
    {
        // 口径的维度 ORDER_DATE / CLIENT_ID 在主表 COP_ORDER_M 上，指标列在明细 COP_ORDER_D 上：
        // 校验必须承认"指标在明细、维度在主表"这种既有登记形态。
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections,
            NullLogger<WorkbenchDefinitionProvider>.Instance);
        var builder = new WorkbenchDefinitionBuilder(connections, provider,
            TestFormCapability.None, TestBusinessConfig.Reader(connections),
            NullLogger<WorkbenchDefinitionBuilder>.Instance);
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definition = await builder.GetDefinitionAsync(SalesOrderModuleId, "admin", "A", true, true,
            empty, empty, CancellationToken.None);
        Assert.NotNull(definition);

        var repository = new MetricRepository(connections);
        var metric = await repository.GetAsync("sales_amount", CancellationToken.None);
        Assert.NotNull(metric);
        var parse = MetricExpressionParser.Parse(metric!.Definition);
        Assert.True(parse.Ok, parse.Error);

        var probe = new SysMetricSchemaProbe(connections);
        var validator = new MetricDefinitionValidator(probe);
        var whitelist = new MetricFieldWhitelist(connections);
        var sourceAllowed = await whitelist.ReadAsync(metric.SourceTable, true, true, empty, CancellationToken.None);
        var masterAllowed = await whitelist.ReadAsync(definition!.MasterTable!, true, true, empty,
            CancellationToken.None);

        var result = await validator.ValidateAsync(new MetricValidationInput(
            parse.Expression!, metric.SourceTable, sourceAllowed, metric.DimensionKeys, metric.RowFilter,
            definition.MasterTable, masterAllowed), CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.RowFilter);
    }
}
