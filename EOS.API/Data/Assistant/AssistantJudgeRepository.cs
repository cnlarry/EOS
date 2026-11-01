using System.Text.Json;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.Quality;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Assistant;

/// <summary>
/// 判分的输入来源：从已落库的会话里取"最近若干条回答 + 它们各自的问句与工具摘要"。
///
/// <para>
/// **只读、只取最小必要字段**（ADR-044 §4.1）：问句、回答正文、工具调用摘要、时间。
/// 不取明细行值、不取附件、不跨会话拼接；判官判的是"有没有如实回答"，不是"数据对不对"。
/// </para>
///
/// <para>
/// 权限门不在这里：判分是系统行为（由后台任务触发），不是某个用户的读取动作；
/// 判官也因此**不注入 <c>IPermissionService</c>**（门禁钉住这一条）。
/// </para>
/// </summary>
public interface IAssistantJudgeRepository
{
    /// <summary>最近 <paramref name="take"/> 条有内容的助手回答，新的在前。</summary>
    Task<IReadOnlyList<AssistantJudgeTranscript>> LoadRecentAnswersAsync(int take, CancellationToken token);
}

/// <summary>判分输入的 SQL Server 实现。</summary>
public sealed class AssistantJudgeRepository(DbConnectionFactory connections) : IAssistantJudgeRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantJudgeTranscript>> LoadRecentAnswersAsync(
        int take, CancellationToken token)
    {
        // 问句取"同一会话里、这条回答之前最近的一条用户消息"：助手可能连答多轮，
        // 但每条回答对应的落库上下文是它自己那一问一答，不做跨轮拼接。
        const string sql = """
            ;WITH answers AS (
                SELECT TOP (@Take)
                       m.ID, m.SESSION_ID, m.CONTENT, m.TOOL_CALLS_JSON, m.CREATED_TIME
                FROM dbo.ASSISTANT_MESSAGE m
                WHERE m.ROLE = 2
                  AND NULLIF(LTRIM(RTRIM(m.CONTENT)), N'') IS NOT NULL
                ORDER BY m.ID DESC
            )
            SELECT a.ID,
                   a.SESSION_ID,
                   ISNULL(s.USER_ID, N''),
                   a.CONTENT,
                   a.TOOL_CALLS_JSON,
                   a.CREATED_TIME,
                   ISNULL((
                       SELECT TOP 1 q.CONTENT
                       FROM dbo.ASSISTANT_MESSAGE q
                       WHERE q.SESSION_ID = a.SESSION_ID AND q.ROLE = 1 AND q.ID < a.ID
                       ORDER BY q.ID DESC), N'') AS QUESTION
            FROM answers a
            LEFT JOIN dbo.ASSISTANT_SESSION s ON s.ID = a.SESSION_ID
            ORDER BY a.ID DESC;
            """;

        var transcripts = new List<AssistantJudgeTranscript>();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Take", Math.Clamp(take, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var answer = reader.GetString(3);
            transcripts.Add(new AssistantJudgeTranscript(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(6),
                answer,
                ParseToolCalls(reader.IsDBNull(4) ? null : reader.GetString(4)),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)))
            {
                AnswerTruncated = answer.Length > AssistantActionLimits.MaxJudgeAnswerChars,
            });
        }

        return transcripts;
    }

    /// <summary>
    /// 解析 <c>TOOL_CALLS_JSON</c>：库内是 <c>[{Name, ArgumentsJson, ResultDigest, Ok}]</c>（PascalCase）。
    /// 只取名字、成败与结果摘要——参数与完整返回不进判定输入。
    /// </summary>
    private static IReadOnlyList<AssistantJudgeToolCall> ParseToolCalls(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var calls = new List<AssistantJudgeToolCall>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = Read(item, "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                calls.Add(new AssistantJudgeToolCall(
                    name,
                    Read(item, "ResultDigest"),
                    item.TryGetProperty("Ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? ok.GetBoolean()
                        : null));
            }

            return calls;
        }
        catch (JsonException)
        {
            // 摘要坏了就当"没有工具摘要"：判官会因此判不了 E 条款，而不是拿半份数据下结论。
            return [];
        }
    }

    private static string? Read(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
