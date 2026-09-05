using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// M7 原子扣减并发测试（需真库 + 迁移 045）。
/// 无连接或台账表未就绪时测试失败而非跳过——静默跳过会让 CI 把"未验证"误读为"已验证"。
/// </summary>
public sealed class AssistantUsageLedgerTests
{
    private static string? TestConnection() =>
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");

    private static async Task<bool> LedgerReadyAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT 1 WHERE OBJECT_ID(N'dbo.ASSISTANT_USAGE_DAY', N'U') IS NOT NULL;",
            connection);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task CleanupAsync(string connectionString, string userId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "DELETE FROM dbo.ASSISTANT_USAGE_DAY WHERE USER_ID IN (@UserId, N'*') AND USAGE_DATE = CAST(SYSUTCDATETIME() AS date);",
            connection);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ConcurrentReserves_NeverBreachCap()
    {
        var connectionString = TestConnection()
            ?? throw new InvalidOperationException(
                "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");
        if (!await LedgerReadyAsync(connectionString))
            throw new InvalidOperationException(
                "dbo.ASSISTANT_USAGE_DAY 不存在（迁移 045 未执行）；未就绪即失败（跳过≠已验证）。");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionString,
            })
            .Build();
        var repository = new AssistantUsageRepository(new DbConnectionFactory(config));
        var userId = "test-cap-" + Guid.NewGuid().ToString("N")[..8];
        var day = DateTimeOffset.UtcNow.Date;
        await CleanupAsync(connectionString, userId);
        try
        {
            const long reserve = 10_000;
            const long cap = 50_000;
            var results = new bool[10];
            await Parallel.ForEachAsync(Enumerable.Range(0, 10), async (index, token) =>
            {
                results[index] = await repository.TryReserveAsync(
                    userId, day, reserve, cap, 1_000_000_000, token);
            });

            Assert.Equal(5, results.Count(succeeded => succeeded));
            await repository.SettleAsync(userId, day, reserve, reserve, completed: true, CancellationToken.None);
            var usage = await repository.GetUserDailyUsageAsync(userId, day, CancellationToken.None);
            Assert.Equal(1, usage.Requests);
        }
        finally
        {
            await CleanupAsync(connectionString, userId);
        }
    }
}
