using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Features.Assistant.Parameters;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 行为规则段的真库用例（ADR-046）：库内文本与代码默认值逐字一致（防两边漂移）、
/// 锁定段（红线）拒绝写入、未锁定段可改并能还原。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class PromptSegmentRepositoryLiveTests
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
    public async Task Database_Segments_Match_Code_Defaults()
    {
        var repository = new PromptSegmentRepository(Connections());
        var rows = (await repository.ListEnabledAsync(CancellationToken.None))
            .ToDictionary(segment => segment.Key, StringComparer.Ordinal);

        Assert.True(rows.Count >= PromptSegmentDefaults.All.Count,
            $"库内只有 {rows.Count} 段，少于代码默认的 {PromptSegmentDefaults.All.Count} 段（迁移 463 未落库？）");

        foreach (var expected in PromptSegmentDefaults.All)
        {
            Assert.True(rows.TryGetValue(expected.Key, out var actual), $"缺少规则段：{expected.Key}");
            Assert.Equal(expected.Text, actual!.Text);
            Assert.Equal(expected.IsLocked, actual.IsLocked);
            Assert.Equal(expected.RequiredTools, actual.RequiredTools);
        }
    }

    [Fact]
    public async Task Locked_Segment_Rejects_Update()
    {
        var repository = new PromptSegmentRepository(Connections());
        var before = (await repository.ListEnabledAsync(CancellationToken.None))
            .Single(segment => segment.Key == "METRIC_USAGE");

        var status = await repository.UpdateTextAsync("METRIC_USAGE", "这条红线不该被改掉", "test", CancellationToken.None);

        Assert.Equal(PromptSegmentUpdateStatus.Locked, status);
        var after = (await repository.ListEnabledAsync(CancellationToken.None))
            .Single(segment => segment.Key == "METRIC_USAGE");
        Assert.Equal(before.Text, after.Text);
    }

    [Fact]
    public async Task Unlocked_Segment_Can_Update_And_Restore()
    {
        var repository = new PromptSegmentRepository(Connections());
        var original = (await repository.ListEnabledAsync(CancellationToken.None))
            .Single(segment => segment.Key == "METADATA_SEARCH").Text;

        try
        {
            var changed = original + "（用例临时改动）";
            Assert.Equal(PromptSegmentUpdateStatus.Updated,
                await repository.UpdateTextAsync("METADATA_SEARCH", changed, "test", CancellationToken.None));
            var readBack = (await repository.ListEnabledAsync(CancellationToken.None))
                .Single(segment => segment.Key == "METADATA_SEARCH").Text;
            Assert.Equal(changed, readBack);
        }
        finally
        {
            await repository.UpdateTextAsync("METADATA_SEARCH", original, "test", CancellationToken.None);
        }

        var restored = (await repository.ListEnabledAsync(CancellationToken.None))
            .Single(segment => segment.Key == "METADATA_SEARCH").Text;
        Assert.Equal(original, restored);
    }
}
