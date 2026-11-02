using System.Data;
using EOS.API.Features.Assistant.Quality;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Assistant;

/// <summary>一条判定结论（看板按条款/会话筛选时的行）。</summary>
public sealed record AssistantVerdictRow(
    long VerdictId,
    long MessageId,
    long SessionId,
    string UserId,
    string Clause,
    string Verdict,
    string? Evidence,
    string? Note,
    string RubricVersion,
    string JudgeModel,
    bool Fallback,
    DateTimeOffset JudgedAt)
{
    /// <summary>是否属于当前条款版本（历史版本的结论在页面上标出来，避免与现行口径混看）。</summary>
    public bool CurrentRubric { get; init; }
}

/// <summary>回答质量的落库侧汇总：条款计数 + 判定用量（成本记在系统账户 `__judge__`）。</summary>
public sealed record AssistantVerdictSummary(
    int JudgedMessages,
    int VerdictRows,
    IReadOnlyList<AssistantJudgeClauseTally> Clauses,
    IReadOnlyList<string> JudgeModels,
    long SpentMicroYuan,
    int JudgeCalls);

/// <summary>按条款/会话筛选的一页结论。</summary>
public sealed record AssistantVerdictPage(
    IReadOnlyList<AssistantVerdictRow> Items, int Total, int Page, int PageSize);

/// <summary>结论列表的筛选条件（服务端校验取值范围，不信任前端）。</summary>
public sealed record AssistantVerdictFilter(
    DateTimeOffset Since, string? Clause, long? SessionId, string? Verdict, int Page, int PageSize);

/// <summary>
/// 判定结论的存储与只读聚合（ADR-044 §4.3 / §4.5）。
///
/// <para>
/// **只写自己的表、只读自己的表 + 用量日表**：不写业务表，也不写业务审计
/// （verdict 不是用户行为，不进业务审计）。与判官的分工是"判官纯函数、结论我落库"——
/// 这样将来把打分运行搬到别处时，存储仍然留在这里。
/// </para>
/// </summary>
public interface IAssistantVerdictRepository
{
    /// <summary>落一次判定的全部条款结论（一个事务内，整条回答要么全落要么不落）。</summary>
    Task SaveAsync(AssistantJudgeOutcome outcome, CancellationToken token);

    /// <summary>窗口内的条款计数与判定用量。</summary>
    Task<AssistantVerdictSummary> LoadSummaryAsync(DateTimeOffset since, CancellationToken token);

    /// <summary>按条款/会话筛选的分页结论。</summary>
    Task<AssistantVerdictPage> LoadPageAsync(AssistantVerdictFilter filter, CancellationToken token);

    /// <summary>清理过期结论（一次最多删 <paramref name="take"/> 行，避免一次锁太多）。</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int take, CancellationToken token);
}

/// <inheritdoc />
public sealed class AssistantVerdictRepository(DbConnectionFactory connections) : IAssistantVerdictRepository
{
    /// <summary>证据列 / 备注列的物理长度：超长截断而不是让整条结论写不进去。</summary>
    private const int EvidenceMaxChars = 1_000;

    private const int NoteMaxChars = 400;

    /// <inheritdoc />
    public async Task SaveAsync(AssistantJudgeOutcome outcome, CancellationToken token)
    {
        if (outcome.Verdicts.Count == 0) return;

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        foreach (var verdict in outcome.Verdicts)
        {
            await using var command = new SqlCommand("""
                IF NOT EXISTS (
                    SELECT 1 FROM dbo.ASSISTANT_ANSWER_VERDICT WITH (UPDLOCK, HOLDLOCK)
                    WHERE MESSAGE_ID = @MessageId AND CLAUSE = @Clause
                      AND RUBRIC_VERSION = @RubricVersion AND JUDGE_MODEL = @JudgeModel)
                    INSERT INTO dbo.ASSISTANT_ANSWER_VERDICT
                        (MESSAGE_ID, SESSION_ID, USER_ID, CLAUSE, VERDICT, EVIDENCE, NOTE,
                         RUBRIC_VERSION, JUDGE_MODEL, JUDGE_FALLBACK, JUDGED_TIME)
                    VALUES
                        (@MessageId, @SessionId, @UserId, @Clause, @Verdict, @Evidence, @Note,
                         @RubricVersion, @JudgeModel, @Fallback, SYSUTCDATETIME());
                """, connection, transaction);
            command.Parameters.Add("@MessageId", SqlDbType.BigInt).Value = outcome.MessageId;
            command.Parameters.Add("@SessionId", SqlDbType.BigInt).Value = outcome.SessionId;
            command.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = outcome.UserId;
            command.Parameters.Add("@Clause", SqlDbType.NVarChar, 2).Value = verdict.Clause;
            command.Parameters.Add("@Verdict", SqlDbType.NVarChar, 6).Value = verdict.Verdict;
            command.Parameters.Add("@Evidence", SqlDbType.NVarChar, EvidenceMaxChars).Value =
                (object?)Truncate(verdict.Evidence, EvidenceMaxChars) ?? DBNull.Value;
            command.Parameters.Add("@Note", SqlDbType.NVarChar, NoteMaxChars).Value =
                (object?)Truncate(verdict.Note, NoteMaxChars) ?? DBNull.Value;
            command.Parameters.Add("@RubricVersion", SqlDbType.NVarChar, 16).Value = outcome.RubricVersion;
            command.Parameters.Add("@JudgeModel", SqlDbType.NVarChar, 128).Value =
                Truncate(outcome.JudgeModel, 128)!;
            command.Parameters.Add("@Fallback", SqlDbType.Bit).Value = outcome.FellBackToChat;
            await command.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
    }

    /// <inheritdoc />
    public async Task<AssistantVerdictSummary> LoadSummaryAsync(DateTimeOffset since, CancellationToken token)
    {
        var rubricVersion = AssistantJudgeRubric.CurrentVersion;

        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        int judgedMessages, verdictRows;
        var tallies = new List<AssistantJudgeClauseTally>();
        var models = new List<string>();
        await using (var command = new SqlCommand("""
            SELECT COUNT_BIG(DISTINCT MESSAGE_ID), COUNT_BIG(1)
            FROM dbo.ASSISTANT_ANSWER_VERDICT
            WHERE JUDGED_TIME >= @Since AND RUBRIC_VERSION = @RubricVersion;
            """, connection))
        {
            command.Parameters.Add("@Since", SqlDbType.DateTime2).Value = since.UtcDateTime;
            command.Parameters.Add("@RubricVersion", SqlDbType.NVarChar, 16).Value = rubricVersion;
            await using var reader = await command.ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            judgedMessages = Convert.ToInt32(reader.GetValue(0));
            verdictRows = Convert.ToInt32(reader.GetValue(1));
        }

        await using (var command = new SqlCommand("""
            SELECT CLAUSE,
                   SUM(CASE WHEN VERDICT = N'PASS' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN VERDICT = N'FAIL' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN VERDICT = N'NA' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN VERDICT = N'UNSURE' THEN 1 ELSE 0 END)
            FROM dbo.ASSISTANT_ANSWER_VERDICT
            WHERE JUDGED_TIME >= @Since AND RUBRIC_VERSION = @RubricVersion
            GROUP BY CLAUSE;
            """, connection))
        {
            command.Parameters.Add("@Since", SqlDbType.DateTime2).Value = since.UtcDateTime;
            command.Parameters.Add("@RubricVersion", SqlDbType.NVarChar, 16).Value = rubricVersion;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                tallies.Add(new AssistantJudgeClauseTally(
                    reader.GetString(0),
                    Convert.ToInt32(reader.GetValue(1)),
                    Convert.ToInt32(reader.GetValue(2)),
                    Convert.ToInt32(reader.GetValue(3)),
                    Convert.ToInt32(reader.GetValue(4))));
            }
        }

        await using (var command = new SqlCommand("""
            SELECT DISTINCT JUDGE_MODEL
            FROM dbo.ASSISTANT_ANSWER_VERDICT
            WHERE JUDGED_TIME >= @Since AND RUBRIC_VERSION = @RubricVersion
            ORDER BY JUDGE_MODEL;
            """, connection))
        {
            command.Parameters.Add("@Since", SqlDbType.DateTime2).Value = since.UtcDateTime;
            command.Parameters.Add("@RubricVersion", SqlDbType.NVarChar, 16).Value = rubricVersion;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) models.Add(reader.GetString(0));
        }

        long spentMicro = 0;
        var calls = 0;
        await using (var command = new SqlCommand("""
            SELECT ISNULL(SUM(SPENT_MICROYUAN), 0), ISNULL(SUM(REQUESTS), 0)
            FROM dbo.ASSISTANT_USAGE_DAY
            WHERE USER_ID = @UserId AND USAGE_DATE >= @SinceDate;
            """, connection))
        {
            command.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = AssistantJudgeAccounts.SystemUserId;
            command.Parameters.Add("@SinceDate", SqlDbType.Date).Value = since.UtcDateTime.Date;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                spentMicro = Convert.ToInt64(reader.GetValue(0));
                calls = Convert.ToInt32(reader.GetValue(1));
            }
        }

        return new AssistantVerdictSummary(judgedMessages, verdictRows, tallies, models, spentMicro, calls);
    }

    /// <inheritdoc />
    public async Task<AssistantVerdictPage> LoadPageAsync(
        AssistantVerdictFilter filter, CancellationToken token)
    {
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);

        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        const string where = """
            WHERE JUDGED_TIME >= @Since
              AND (@Clause IS NULL OR CLAUSE = @Clause)
              AND (@SessionId IS NULL OR SESSION_ID = @SessionId)
              AND (@Verdict IS NULL OR VERDICT = @Verdict)
            """;

        void Bind(SqlCommand command)
        {
            command.Parameters.Add("@Since", SqlDbType.DateTime2).Value = filter.Since.UtcDateTime;
            command.Parameters.Add("@Clause", SqlDbType.NVarChar, 2).Value = (object?)filter.Clause ?? DBNull.Value;
            command.Parameters.Add("@SessionId", SqlDbType.BigInt).Value = (object?)filter.SessionId ?? DBNull.Value;
            command.Parameters.Add("@Verdict", SqlDbType.NVarChar, 6).Value = (object?)filter.Verdict ?? DBNull.Value;
        }

        var total = 0;
        await using (var command = new SqlCommand(
            $"SELECT COUNT_BIG(1) FROM dbo.ASSISTANT_ANSWER_VERDICT {where};", connection))
        {
            Bind(command);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token)) total = Convert.ToInt32(reader.GetValue(0));
        }

        var items = new List<AssistantVerdictRow>();
        await using (var command = new SqlCommand($"""
            SELECT VERDICT_ID, MESSAGE_ID, SESSION_ID, USER_ID, CLAUSE, VERDICT, EVIDENCE, NOTE,
                   RUBRIC_VERSION, JUDGE_MODEL, JUDGE_FALLBACK, JUDGED_TIME
            FROM dbo.ASSISTANT_ANSWER_VERDICT
            {where}
            ORDER BY JUDGED_TIME DESC, VERDICT_ID DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """, connection))
        {
            Bind(command);
            command.Parameters.Add("@Offset", SqlDbType.Int).Value = (page - 1) * pageSize;
            command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                items.Add(new AssistantVerdictRow(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetBoolean(10),
                    new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc)))
                {
                    CurrentRubric = string.Equals(
                        reader.GetString(8), AssistantJudgeRubric.CurrentVersion, StringComparison.Ordinal),
                });
            }
        }

        return new AssistantVerdictPage(items, total, page, pageSize);
    }

    /// <inheritdoc />
    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int take, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            DELETE TOP (@Take) FROM dbo.ASSISTANT_ANSWER_VERDICT
            WHERE JUDGED_TIME < @Cutoff;
            """, connection);
        command.Parameters.Add("@Take", SqlDbType.Int).Value = Math.Clamp(take, 1, 50_000);
        command.Parameters.Add("@Cutoff", SqlDbType.DateTime2).Value = cutoff.UtcDateTime;
        return await command.ExecuteNonQueryAsync(token);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
