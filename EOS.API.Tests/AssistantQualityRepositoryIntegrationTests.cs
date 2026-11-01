using EOS.API.Data;
using EOS.API.Features.Assistant.Admin;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 质量看板的取数层真库用例：只验证 SQL 能读、列类型不会抛（ROLE 是 tinyint，
/// 不能用 GetInt32 硬读）。聚合口径由 <see cref="AssistantQualityViewTests"/> 覆盖。
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantQualityRepositoryIntegrationTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");

    private static AssistantQualityRepository Repository()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString,
            })
            .Build();
        return new AssistantQualityRepository(new DbConnectionFactory(config));
    }

    [Fact]
    public async Task Load_Messages_Reads_Real_Rows_Without_Type_Cast_Errors()
    {
        Assert.False(string.IsNullOrWhiteSpace(ConnectionString), "需要 MSSQL_ERP_CONN 才能跑真库用例。");

        var messages = await Repository().LoadMessagesAsync(
            DateTimeOffset.UtcNow.AddDays(-30), CancellationToken.None);

        Assert.NotNull(messages);
        Assert.All(messages, message => Assert.True(message.Role is 1 or 2 or 3));
    }

    [Fact]
    public async Task Load_Usage_Reads_Global_Row()
    {
        Assert.False(string.IsNullOrWhiteSpace(ConnectionString), "需要 MSSQL_ERP_CONN 才能跑真库用例。");

        var usage = await Repository().LoadUsageAsync(
            DateTimeOffset.UtcNow.AddDays(-30), CancellationToken.None);

        Assert.True(usage.SpentMicroYuan >= 0);
        Assert.True(usage.Requests >= 0);
    }
}
