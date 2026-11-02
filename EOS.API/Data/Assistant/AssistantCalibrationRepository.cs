using System.Data;
using System.Text.Json;
using EOS.API.Features.Assistant.Quality;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Assistant;

/// <summary>一条校准样本里的单个条款标签（人给的）。</summary>
public sealed record AssistantCalibrationLabel(string Clause, string Verdict, string? Evidence);

/// <summary>
/// 一条校准样本（3106 的 `judge` 层）。
/// <paramref name="Problem"/> 非空表示这行解析不了（JSON 坏了或形状不对）：报告里照样列出来，
/// 因为"有一条样本读不出来"本身就是需要处理的事实，不该被静默丢掉。
/// </summary>
public sealed record AssistantCalibrationSample(
    string CaseId,
    long? MessageId,
    bool Reviewed,
    IReadOnlyList<AssistantCalibrationLabel> Labels,
    string? Problem = null);

/// <summary>判官对某条回答某条款已落库的结论（只取当前条款版本）。</summary>
public sealed record AssistantCalibrationVerdict(
    long MessageId,
    string Clause,
    string Verdict,
    string? Evidence,
    string JudgeModel,
    DateTimeOffset JudgedAt);

/// <summary>
/// 校准样本的读取（**只读**：这一页不写任何东西）。
///
/// <para>
/// 与评估运行的区别：运行器跑的是"样本自身是否完整"，而这里要的是"人与判官是否一致"——
/// 后者需要**同一批回答的判官结论**，落在 `ASSISTANT_ANSWER_VERDICT` 里，两表按 `messageId` 对齐。
/// </para>
/// </summary>
public interface IAssistantCalibrationRepository
{
    /// <summary>全部 `judge` 层样本（退役状态除外，退役样本不该再参与校准）。</summary>
    Task<IReadOnlyList<AssistantCalibrationSample>> LoadSamplesAsync(CancellationToken token);

    /// <summary>这些回答在**当前条款版本**下的判官结论。</summary>
    Task<IReadOnlyList<AssistantCalibrationVerdict>> LoadVerdictsAsync(
        IReadOnlyCollection<long> messageIds, string rubricVersion, CancellationToken token);
}

/// <inheritdoc />
public sealed class AssistantCalibrationRepository(DbConnectionFactory connections) : IAssistantCalibrationRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantCalibrationSample>> LoadSamplesAsync(CancellationToken token)
    {
        const string sql = """
            SELECT CASE_ID, PAYLOAD_JSON
            FROM dbo.ASSISTANT_EVAL_CASE
            WHERE LAYER = N'judge'
              AND ISNULL(REVIEW_STATUS, N'') <> N'RETIRED'
            ORDER BY CASE_ID;
            """;

        var samples = new List<AssistantCalibrationSample>();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var caseId = reader.GetString(0);
            var payload = reader.IsDBNull(1) ? null : reader.GetString(1);
            samples.Add(Parse(caseId, payload));
        }

        return samples;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantCalibrationVerdict>> LoadVerdictsAsync(
        IReadOnlyCollection<long> messageIds, string rubricVersion, CancellationToken token)
    {
        if (messageIds.Count == 0) return [];

        var verdicts = new List<AssistantCalibrationVerdict>();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        // 分批：样本再多也不该让 IN 列表无限长（SQL Server 参数上限 2100）。
        foreach (var chunk in messageIds.Distinct().Chunk(500))
        {
            var names = chunk.Select((_, index) => "@M" + index.ToString()).ToList();
            var sql = $"""
                SELECT MESSAGE_ID, CLAUSE, VERDICT, EVIDENCE, JUDGE_MODEL, JUDGED_TIME
                FROM dbo.ASSISTANT_ANSWER_VERDICT
                WHERE RUBRIC_VERSION = @RubricVersion
                  AND MESSAGE_ID IN ({string.Join(", ", names)})
                ORDER BY MESSAGE_ID, CLAUSE, JUDGED_TIME DESC;
                """;
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@RubricVersion", rubricVersion);
            for (var index = 0; index < chunk.Length; index++)
            {
                command.Parameters.AddWithValue(names[index], chunk[index]);
            }

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                verdicts.Add(new AssistantCalibrationVerdict(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc))));
            }
        }

        return verdicts;
    }

    /// <summary>
    /// 解析样本正文。**宽容读取、明确报问题**：坏行不抛，但会带着原因出现在报告里——
    /// 一条读不出来的样本如果被静默丢掉，"一致率"会凭空变好看。
    /// </summary>
    private static AssistantCalibrationSample Parse(string caseId, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AssistantCalibrationSample(caseId, null, false, [], "PAYLOAD_JSON 为空");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new AssistantCalibrationSample(caseId, null, false, [], "正文不是合法 JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new AssistantCalibrationSample(caseId, null, false, [], "正文不是 JSON 对象");
            }

            long? messageId = null;
            if (root.TryGetProperty("messageId", out var messageElement))
            {
                if (messageElement.ValueKind == JsonValueKind.Number && messageElement.TryGetInt64(out var number))
                {
                    messageId = number;
                }
                else if (messageElement.ValueKind == JsonValueKind.String
                         && long.TryParse(messageElement.GetString(), out var parsed))
                {
                    messageId = parsed;
                }
            }

            var reviewed = root.TryGetProperty("reviewed", out var reviewedElement)
                           && reviewedElement.ValueKind == JsonValueKind.True;

            var labels = new List<AssistantCalibrationLabel>();
            if (root.TryGetProperty("labels", out var labelsElement) && labelsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in labelsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var clause = Text(item, "clause")?.Trim().ToUpperInvariant();
                    var verdict = Text(item, "verdict")?.Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(clause) || string.IsNullOrWhiteSpace(verdict)) continue;
                    labels.Add(new AssistantCalibrationLabel(clause, verdict, Text(item, "evidence")));
                }
            }

            return new AssistantCalibrationSample(caseId, messageId, reviewed, labels);
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
