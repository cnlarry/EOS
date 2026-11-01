using EOS.API.Data;
using EOS.API.Features.Assistant.Metrics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 口径维护的真库用例：新增候选 → 改内容（版本递增）→ 确认（记批核人）→ 改回候选 → 删除。
/// 用自造标识并在 finally 里清理，不碰任何既有口径。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class ReportMetricAdminRepositoryLiveTests
{
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
            var match = System.Text.RegularExpressions.Regex.Match(text,
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

    private static ReportMetricAdminDraft Draft(string metricId, string status = "CANDIDATE", string definition = "SUM(AMOUNT_TAX)") =>
        new(metricId, "冒烟口径", definition, "COP_ORDER_D", "ORDER_DATE,CLIENT_ID", "销售",
            """{"table":"COP_ORDER_M","on":["ORDER_TYPE","ORDER_NO"],"conditions":[{"column":"APPROVED","op":"=","value":true}]}""",
            "真库用例自造，用来验证口径维护链路。", status);

    [Fact]
    public async Task Create_Update_Confirm_Reopen_Delete_RoundTrip()
    {
        var connections = Connections();
        var repository = new ReportMetricAdminRepository(connections);
        var validator = new ReportMetricAdminValidator(new SysMetricSchemaProbe(connections));
        var metricId = $"smoke_metric_{Guid.NewGuid():N}"[..24];

        try
        {
            await validator.ValidateAsync(Draft(metricId), CancellationToken.None);
            Assert.False(await repository.ExistsAsync(metricId, CancellationToken.None));
            await repository.CreateAsync(Draft(metricId), "test", CancellationToken.None);

            var created = (await repository.ListAsync(CancellationToken.None))
                .Single(item => item.MetricId == metricId);
            Assert.Equal("CANDIDATE", created.ConfirmStatus);
            Assert.Equal(1, created.Version);
            Assert.Null(created.ApprovedBy);

            // 改内容：版本递增
            Assert.True(await repository.UpdateAsync(metricId, Draft(metricId, definition: "SUM(QTY)"), "test",
                CancellationToken.None));
            var updated = (await repository.ListAsync(CancellationToken.None)).Single(item => item.MetricId == metricId);
            Assert.Equal(2, updated.Version);
            Assert.Equal("SUM(QTY)", updated.Definition);

            // 确认：记批核人/时间
            Assert.True(await repository.UpdateAsync(metricId, Draft(metricId, status: "CONFIRMED"), "test",
                CancellationToken.None));
            var confirmed = (await repository.ListAsync(CancellationToken.None)).Single(item => item.MetricId == metricId);
            Assert.Equal("CONFIRMED", confirmed.ConfirmStatus);
            Assert.Equal("test", confirmed.ApprovedBy);
            Assert.NotNull(confirmed.ApprovedTime);

            // 已确认口径不允许删除（可能被报表与结论引用）
            Assert.False(await repository.DeleteAsync(metricId, CancellationToken.None));

            // 改回候选后可以删
            Assert.True(await repository.UpdateAsync(metricId, Draft(metricId), "test", CancellationToken.None));
            Assert.True(await repository.DeleteAsync(metricId, CancellationToken.None));
            Assert.False(await repository.ExistsAsync(metricId, CancellationToken.None));
        }
        finally
        {
            // 兜底清理：无论断言是否失败都不留自造口径在库里
            await repository.DeleteAsync(metricId, CancellationToken.None);
        }
    }
}
