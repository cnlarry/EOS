using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed record DailyUsage(int Requests, long PromptTokens, long CompletionTokens);

public interface IAssistantUsageRepository
{
    Task<DailyUsage> GetUserDailyUsageAsync(string userId, DateTimeOffset dayStartUtc, CancellationToken token);
    Task<DailyUsage> GetGlobalDailyUsageAsync(DateTimeOffset dayStartUtc, CancellationToken token);
    Task<IReadOnlyList<(string UserId, DailyUsage Usage)>> GetPerUserDailyUsageAsync(
        DateTimeOffset dayStartUtc, int top, CancellationToken token);

    /// <summary>
    /// 原子预留：在同一事务内建行 + 按上限条件扣减（用户行与全局哨兵行同时满足才成功），
    /// 并发请求在此串行化，超限返回 false。金额单位：微元。
    /// </summary>
    Task<bool> TryReserveAsync(
        string userId, DateTimeOffset dayStartUtc, long reserveMicro,
        long userCapMicro, long globalCapMicro, CancellationToken token);

    /// <summary>结算：释放预留并记入实际花费（失败按预留额计入，取消按 0 释放）。</summary>
    Task SettleAsync(
        string userId, DateTimeOffset dayStartUtc, long reserveMicro, long actualMicro,
        bool completed, CancellationToken token);
}

/// <summary>M7 用量聚合：复用 ASSISTANT_MESSAGE 已记录的 token/耗时，按 UTC 自然日聚合。</summary>
public sealed class AssistantUsageRepository(DbConnectionFactory connections) : IAssistantUsageRepository
{
    public Task<DailyUsage> GetUserDailyUsageAsync(string userId, DateTimeOffset dayStartUtc, CancellationToken token) =>
        QueryAsync(
            """
            SELECT COUNT_BIG(1), ISNULL(SUM(CAST(m.PROMPT_TOKENS AS bigint)),0), ISNULL(SUM(CAST(m.COMPLETION_TOKENS AS bigint)),0)
            FROM dbo.ASSISTANT_MESSAGE m WITH (NOLOCK)
            INNER JOIN dbo.ASSISTANT_SESSION s WITH (NOLOCK) ON s.ID = m.SESSION_ID
            WHERE s.USER_ID = @UserId AND m.ROLE = 2 AND m.CREATED_AT >= @DayStart;
            """,
            command => command.Parameters.AddWithValue("@UserId", userId), dayStartUtc, token);

    public Task<DailyUsage> GetGlobalDailyUsageAsync(DateTimeOffset dayStartUtc, CancellationToken token) =>
        QueryAsync(
            """
            SELECT COUNT_BIG(1), ISNULL(SUM(CAST(m.PROMPT_TOKENS AS bigint)),0), ISNULL(SUM(CAST(m.COMPLETION_TOKENS AS bigint)),0)
            FROM dbo.ASSISTANT_MESSAGE m WITH (NOLOCK)
            WHERE m.ROLE = 2 AND m.CREATED_AT >= @DayStart;
            """,
            _ => { }, dayStartUtc, token);

    public async Task<IReadOnlyList<(string UserId, DailyUsage Usage)>> GetPerUserDailyUsageAsync(
        DateTimeOffset dayStartUtc, int top, CancellationToken token)
    {
        const string sql = """
            SELECT TOP (@Top) s.USER_ID, COUNT_BIG(1),
                   ISNULL(SUM(CAST(m.PROMPT_TOKENS AS bigint)),0), ISNULL(SUM(CAST(m.COMPLETION_TOKENS AS bigint)),0)
            FROM dbo.ASSISTANT_MESSAGE m WITH (NOLOCK)
            INNER JOIN dbo.ASSISTANT_SESSION s WITH (NOLOCK) ON s.ID = m.SESSION_ID
            WHERE m.ROLE = 2 AND m.CREATED_AT >= @DayStart
            GROUP BY s.USER_ID
            ORDER BY ISNULL(SUM(CAST(m.PROMPT_TOKENS AS bigint)),0) + ISNULL(SUM(CAST(m.COMPLETION_TOKENS AS bigint)),0) DESC;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Top", Math.Clamp(top, 1, 100));
        command.Parameters.Add("@DayStart", System.Data.SqlDbType.DateTime2).Value = dayStartUtc.UtcDateTime;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<(string UserId, DailyUsage Usage)>();
        while (await reader.ReadAsync(token))
        {
            result.Add((reader.GetString(0),
                new(Convert.ToInt32(reader.GetValue(1)), Convert.ToInt64(reader.GetValue(2)), Convert.ToInt64(reader.GetValue(3)))));
        }

        return result;
    }

    public async Task<bool> TryReserveAsync(
        string userId, DateTimeOffset dayStartUtc, long reserveMicro,
        long userCapMicro, long globalCapMicro, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await EnsureRowAsync(connection, transaction, userId, dayStartUtc, token);
        await EnsureRowAsync(connection, transaction, "*", dayStartUtc, token);
        var userOk = await ConditionalReserveAsync(connection, transaction, userId, dayStartUtc,
            reserveMicro, userCapMicro, token);
        var globalOk = userOk && await ConditionalReserveAsync(connection, transaction, "*", dayStartUtc,
            reserveMicro, globalCapMicro, token);
        if (userOk && globalOk)
        {
            await transaction.CommitAsync(token);
            return true;
        }

        await transaction.RollbackAsync(token);
        return false;
    }

    public async Task SettleAsync(
        string userId, DateTimeOffset dayStartUtc, long reserveMicro, long actualMicro,
        bool completed, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await EnsureRowAsync(connection, transaction, userId, dayStartUtc, token);
        await EnsureRowAsync(connection, transaction, "*", dayStartUtc, token);
        const string sql = """
            UPDATE dbo.ASSISTANT_USAGE_DAY
            SET RESERVED_MICROYUAN = CASE WHEN RESERVED_MICROYUAN >= @Reserve THEN RESERVED_MICROYUAN - @Reserve ELSE 0 END,
                SPENT_MICROYUAN = SPENT_MICROYUAN + @Actual,
                REQUESTS = REQUESTS + @Completed,
                UPDATED_AT = SYSUTCDATETIME()
            WHERE USER_ID = @UserId AND USAGE_DATE = @Day;
            """;
        foreach (var key in new[] { userId, "*" })
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@Reserve", SqlDbType.BigInt).Value = reserveMicro;
            command.Parameters.Add("@Actual", SqlDbType.BigInt).Value = actualMicro;
            command.Parameters.Add("@Completed", SqlDbType.Int).Value = completed ? 1 : 0;
            command.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = key;
            command.Parameters.Add("@Day", SqlDbType.Date).Value = dayStartUtc.UtcDateTime.Date;
            await command.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
    }

    private static async Task EnsureRowAsync(
        SqlConnection connection, SqlTransaction transaction,
        string userId, DateTimeOffset dayStartUtc, CancellationToken token)
    {
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.ASSISTANT_USAGE_DAY WITH (UPDLOCK, HOLDLOCK)
                           WHERE USER_ID = @UserId AND USAGE_DATE = @Day)
                INSERT INTO dbo.ASSISTANT_USAGE_DAY (USER_ID, USAGE_DATE) VALUES (@UserId, @Day);
            """, connection, transaction);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
        command.Parameters.Add("@Day", SqlDbType.Date).Value = dayStartUtc.UtcDateTime.Date;
        try
        {
            await command.ExecuteNonQueryAsync(token);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            // 并发建行：对方已建，本事务继续即可。
        }
    }

    private static async Task<bool> ConditionalReserveAsync(
        SqlConnection connection, SqlTransaction transaction, string userId,
        DateTimeOffset dayStartUtc, long reserveMicro, long capMicro, CancellationToken token)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.ASSISTANT_USAGE_DAY
            SET RESERVED_MICROYUAN = RESERVED_MICROYUAN + @Reserve,
                UPDATED_AT = SYSUTCDATETIME()
            WHERE USER_ID = @UserId AND USAGE_DATE = @Day
              AND SPENT_MICROYUAN + RESERVED_MICROYUAN + @Reserve <= @Cap;
            """, connection, transaction);
        command.Parameters.Add("@Reserve", SqlDbType.BigInt).Value = reserveMicro;
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
        command.Parameters.Add("@Day", SqlDbType.Date).Value = dayStartUtc.UtcDateTime.Date;
        command.Parameters.Add("@Cap", SqlDbType.BigInt).Value = capMicro;
        return await command.ExecuteNonQueryAsync(token) > 0;
    }

    private async Task<DailyUsage> QueryAsync(
        string sql, Action<SqlCommand> parameters, DateTimeOffset dayStartUtc, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        parameters(command);
        command.Parameters.Add("@DayStart", System.Data.SqlDbType.DateTime2).Value = dayStartUtc.UtcDateTime;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return new(0, 0, 0);
        return new(Convert.ToInt32(reader.GetValue(0)),
            Convert.ToInt64(reader.GetValue(1)), Convert.ToInt64(reader.GetValue(2)));
    }
}
