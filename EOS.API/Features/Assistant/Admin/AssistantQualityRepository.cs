using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Admin;

/// <summary>质量看板的一条消息样本（只取聚合需要的列，不取正文）。</summary>
public sealed record AssistantQualityMessage(
    long SessionId,
    DateTimeOffset CreatedAt,
    int Role,
    int PromptTokens,
    int CompletionTokens,
    int ElapsedMs,
    int? Feedback,
    string? ToolCallsJson);

public sealed record AssistantQualityUsage(long SpentMicroYuan, int Requests);

public interface IAssistantQualityRepository
{
    Task<IReadOnlyList<AssistantQualityMessage>> LoadMessagesAsync(DateTimeOffset since, CancellationToken token);
    Task<AssistantQualityUsage> LoadUsageAsync(DateTimeOffset sinceDate, CancellationToken token);
}

/// <summary>
/// 质量看板的只读聚合源：消息表 + 用量日表。
/// 只读、按时间窗取数；不取消息正文与工具参数，避免看板成为第二个数据出口。
/// </summary>
public sealed class AssistantQualityRepository(DbConnectionFactory connections) : IAssistantQualityRepository
{
    public async Task<IReadOnlyList<AssistantQualityMessage>> LoadMessagesAsync(
        DateTimeOffset since, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT SESSION_ID, CREATED_TIME, ROLE, PROMPT_TOKENS, COMPLETION_TOKENS,
                   ELAPSED_MS, FEEDBACK, TOOL_CALLS_JSON
            FROM dbo.ASSISTANT_MESSAGE WITH (NOLOCK)
            WHERE CREATED_TIME >= @Since;
            """, connection);
        command.Parameters.Add("@Since", SqlDbType.DateTime2).Value = since.UtcDateTime;

        var rows = new List<AssistantQualityMessage>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            int Int(int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
            rows.Add(new AssistantQualityMessage(
                Convert.ToInt64(reader.GetValue(0)),
                new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(reader.GetValue(1)), DateTimeKind.Utc)),
                Int(2), Int(3), Int(4), Int(5),
                reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    public async Task<AssistantQualityUsage> LoadUsageAsync(
        DateTimeOffset sinceDate, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT ISNULL(SUM(SPENT_MICROYUAN), 0), ISNULL(SUM(REQUESTS), 0)
            FROM dbo.ASSISTANT_USAGE_DAY WITH (NOLOCK)
            WHERE USER_ID = N'*' AND USAGE_DATE >= @Since;
            """, connection);
        command.Parameters.Add("@Since", SqlDbType.Date).Value = sinceDate.UtcDateTime.Date;

        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new AssistantQualityUsage(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)))
            : new AssistantQualityUsage(0, 0);
    }
}
