using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed record DailyUsage(int Requests, long PromptTokens, long CompletionTokens);

public interface IAssistantUsageRepository
{
    Task<DailyUsage> GetUserDailyUsageAsync(string userId, DateTimeOffset dayStartUtc, CancellationToken token);
    Task<DailyUsage> GetGlobalDailyUsageAsync(DateTimeOffset dayStartUtc, CancellationToken token);
    Task<IReadOnlyList<(string UserId, DailyUsage Usage)>> GetPerUserDailyUsageAsync(
        DateTimeOffset dayStartUtc, int top, CancellationToken token);
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
