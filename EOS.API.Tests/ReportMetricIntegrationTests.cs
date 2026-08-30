using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Tools;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// P7 最小语义层集成测试：REPORT_METRIC 表 + EnumMetricsTool 实库枚举口径清单。
/// 验证迁移 027 建表/种子口径、API 可枚举（Agent 消费路径）。
/// 需要测试连接串（EOS_ERP_TEST_CONNECTION 或 codex config），否则跳过。
/// </summary>
public class ReportMetricIntegrationTests
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    [Fact]
    public async Task MetricTable_HasSeedMetrics_AndToolCanEnumerate()
    {
        if (ConnectionString.Value is null) return;

        // 1. 表存在且含种子口径（迁移 027）
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var countCommand = new SqlCommand("SELECT COUNT(*) FROM dbo.REPORT_METRIC WITH (NOLOCK);", connection);
        var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
        Assert.True(count > 0, "REPORT_METRIC 应有种子口径（迁移 027）");

        // 2. EnumMetricsTool 实库枚举（Agent 消费路径）
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        var tool = new EnumMetricsTool(new DbConnectionFactory(config));
        var args = JsonDocument.Parse("{}").RootElement;
        var result = await tool.ExecuteAsync("admin", args, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Contains("销售额", result.ContentForModel);
        Assert.Contains("采购金额", result.ContentForModel);

        // 3. 按域过滤
        var filtered = await tool.ExecuteAsync("admin", JsonDocument.Parse("""{"domain":"库存"}""").RootElement, CancellationToken.None);
        Assert.True(filtered.Ok);
        Assert.Contains("库存数量", filtered.ContentForModel);
        Assert.DoesNotContain("销售额", filtered.ContentForModel);
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = System.Text.RegularExpressions.Regex.Match(text,
                "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_CONNECTION_STRING\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }
}