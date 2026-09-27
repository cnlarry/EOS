using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 原子扣减并发测试（需真库 + 迁移 045）。
/// 无连接或台账表未就绪时测试失败而非跳过——静默跳过会让 CI 把"未验证"误读为"已验证"。
/// </summary>
public sealed class AssistantUsageLedgerTests
{
    private static string? TestConnection() =>
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");

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

    /// <summary>读当日台账行（该用户那一行）：REQUESTS / 预扣余额 / 实扣累计。</summary>
    private static async Task<(int Requests, long Reserved, long Spent)> ReadDayRowAsync(
        string connectionString, string userId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT REQUESTS, RESERVED_MICROYUAN, SPENT_MICROYUAN
            FROM dbo.ASSISTANT_USAGE_DAY
            WHERE USER_ID = @UserId AND USAGE_DATE = CAST(SYSUTCDATETIME() AS date);
            """, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2))
            : (0, 0L, 0L);
    }

    [Fact]
    public async Task ConcurrentReserves_NeverBreachCap()
    {
        var connectionString = TestConnection()
            ?? throw new InvalidOperationException(
                "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
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
        // 用真正的 DateTimeOffset（不是 .Date 那个 Kind=Unspecified 的 DateTime）：
        // 后者经隐式转换会被当成本地时间，再折回 UTC 会跨到前一天，导致台账行落在昨天的键上。
        var day = DateTimeOffset.UtcNow;
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
            // 结算记账落在 ASSISTANT_USAGE_DAY（REQUESTS/RESERVED/SPENT），而
            // GetUserDailyUsageAsync 读的是**消息日志**（ROLE=2 的条数）——两者口径不同，
            // 故这里直接断台账行：恰好结算一笔 ⇒ 计数 +1、预扣归还 1 笔、实扣落账 1 笔。
            var (requests, reserved, spent) = await ReadDayRowAsync(connectionString, userId);
            Assert.Equal(1, requests);
            Assert.Equal(4 * reserve, reserved);
            Assert.Equal(reserve, spent);
        }
        finally
        {
            await CleanupAsync(connectionString, userId);
        }
    }
}
