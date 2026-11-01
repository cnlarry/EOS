using System.Data;
using EOS.API.Features.Assistant.Evaluation;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Assistant;

/// <summary>一轮评估运行的抬头（不含逐用例结果）。</summary>
public sealed record AssistantEvalRunDto(
    int RunId,
    string Layers,
    string RunBy,
    DateTime StartedTime,
    DateTime? FinishedTime,
    string Status,
    int CaseTotal,
    int Passed,
    int Failed,
    int Skipped,
    string? Note);

/// <summary>一条逐用例结果。分诊三列是**运行时抄下来的**，不是回查用例表。</summary>
public sealed record AssistantEvalResultDto(
    string CaseId,
    string Layer,
    string Outcome,
    int CheckCount,
    string? Reason,
    string? FixAt,
    string? FixSuggestion,
    string? FixPermission);

/// <summary>
/// 评估运行器的读写（模块 3107，ADR-046 缺口 #4）。
///
/// <para>
/// **权限门不在这里**：调用方（<see cref="Controllers.AssistantAdminController"/>）必须先过
/// 3107 的 CanBrowse / CanSetup。仓储只负责数据。
/// </para>
///
/// <para>
/// 结果行**不级联业务**：它只记录"某一轮里某条用例的结论"。写入走独立事务，
/// 一轮失败（异常）也不会留下半轮的通过计数——抬头先落 RUNNING，收尾时才写计数。
/// </para>
/// </summary>
public interface IAssistantEvalRepository
{
    /// <summary>开一轮（落 RUNNING 抬头），返回运行号。</summary>
    Task<int> StartRunAsync(string layers, string runBy, CancellationToken token);

    /// <summary>按层取待跑用例（已退役的不跑：它们本来就不该再参与评分）。</summary>
    Task<IReadOnlyList<AssistantEvalCaseRecord>> LoadCasesAsync(
        IReadOnlyList<string> layers, CancellationToken token);

    /// <summary>库内元数据快照：模块号全集与启用中的校验规则键。</summary>
    Task<AssistantEvalLookups> LoadLookupsAsync(CancellationToken token);

    /// <summary>写逐用例结果并收尾（计数、状态、结束时间一并落下）。</summary>
    Task FinishRunAsync(
        int runId, IReadOnlyList<AssistantEvalResultDto> results, string status, string? note,
        CancellationToken token);

    /// <summary>最近若干轮（新的在前）。</summary>
    Task<IReadOnlyList<AssistantEvalRunDto>> ListRunsAsync(int take, CancellationToken token);

    /// <summary>一轮的抬头。</summary>
    Task<AssistantEvalRunDto?> GetRunAsync(int runId, CancellationToken token);

    /// <summary>一轮的逐用例结果（失败在前，便于直接看该改什么）。</summary>
    Task<IReadOnlyList<AssistantEvalResultDto>> LoadResultsAsync(int runId, CancellationToken token);

    /// <summary>只保留最近 <paramref name="keep"/> 轮，避免运行记录无限增长。</summary>
    Task PruneAsync(int keep, CancellationToken token);
}

/// <summary>评估运行的 SQL Server 实现。</summary>
public sealed class AssistantEvalRepository(DbConnectionFactory connections) : IAssistantEvalRepository
{
    /// <summary>保留的轮数。运行记录是**自我校准的历史**，不需要无限留；失败原因也随结果行一起清。</summary>
    public const int KeepRuns = 50;

    private const string RunColumns =
        "RUN_ID, LAYERS, RUN_BY, STARTED_TIME, FINISHED_TIME, STATUS, CASE_TOTAL, PASSED, FAILED, SKIPPED, NOTE";

    /// <inheritdoc />
    public async Task<int> StartRunAsync(string layers, string runBy, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.ASSISTANT_EVAL_RUN (LAYERS, RUN_BY, STATUS)
            OUTPUT INSERTED.RUN_ID
            VALUES (@Layers, @RunBy, N'RUNNING');
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Layers", layers);
        cmd.Parameters.AddWithValue("@RunBy", runBy);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantEvalCaseRecord>> LoadCasesAsync(
        IReadOnlyList<string> layers, CancellationToken token)
    {
        if (layers.Count == 0) return [];

        // 层名来自服务端闭集（AssistantEvalCaseValidator.Layers），逐个参数化，不做字符串拼接。
        var names = layers.Select((_, index) => "@Layer" + index.ToString()).ToList();
        var sql = $"""
            SELECT CASE_ID, LAYER, QUESTION, EXPECTED, EXPECT_TOOLS, PAGE_TYPE, PAYLOAD_JSON,
                   FIX_AT, FIX_SUGGESTION, FIX_PERMISSION
            FROM dbo.ASSISTANT_EVAL_CASE
            WHERE LAYER IN ({string.Join(", ", names)})
              AND ISNULL(REVIEW_STATUS, N'') <> N'RETIRED'
            ORDER BY LAYER, CASE_ID;
            """;

        var cases = new List<AssistantEvalCaseRecord>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        for (var index = 0; index < layers.Count; index++)
        {
            cmd.Parameters.AddWithValue(names[index], layers[index]);
        }

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            cases.Add(new AssistantEvalCaseRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return cases;
    }

    /// <inheritdoc />
    public async Task<AssistantEvalLookups> LoadLookupsAsync(CancellationToken token)
    {
        var modules = new HashSet<int>();
        var rules = new HashSet<string>(StringComparer.Ordinal);

        await using var conn = connections.Create();
        await conn.OpenAsync(token);

        await using (var cmd = new SqlCommand("SELECT M_IDX FROM dbo.MODULES;", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token)) modules.Add(reader.GetInt32(0));
        }

        const string ruleSql = """
            SELECT M_IDX, STAGE, VALIDATION_KEY
            FROM dbo.MODULE_VALIDATION_RULE
            WHERE ENABLED = 1;
            """;
        await using (var cmd = new SqlCommand(ruleSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                rules.Add(AssistantEvalLookups.RuleKey(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        return new AssistantEvalLookups(modules, rules);
    }

    /// <inheritdoc />
    public async Task FinishRunAsync(
        int runId, IReadOnlyList<AssistantEvalResultDto> results, string status, string? note,
        CancellationToken token)
    {
        var passed = results.Count(result => result.Outcome == AssistantEvalRunner.OutcomePass);
        var failed = results.Count(result => result.Outcome == AssistantEvalRunner.OutcomeFail);
        var skipped = results.Count(result => result.Outcome == AssistantEvalRunner.OutcomeSkip);

        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var transaction = (SqlTransaction)await conn.BeginTransactionAsync(token);

        const string insertSql = """
            INSERT INTO dbo.ASSISTANT_EVAL_RESULT
                (RUN_ID, CASE_ID, LAYER, OUTCOME, CHECK_COUNT, REASON, FIX_AT, FIX_SUGGESTION, FIX_PERMISSION)
            VALUES
                (@RunId, @CaseId, @Layer, @Outcome, @CheckCount, @Reason, @FixAt, @FixSuggestion, @FixPermission);
            """;
        foreach (var result in results)
        {
            await using var cmd = new SqlCommand(insertSql, conn, transaction);
            cmd.Parameters.AddWithValue("@RunId", runId);
            cmd.Parameters.AddWithValue("@CaseId", result.CaseId);
            cmd.Parameters.AddWithValue("@Layer", result.Layer);
            cmd.Parameters.AddWithValue("@Outcome", result.Outcome);
            cmd.Parameters.AddWithValue("@CheckCount", result.CheckCount);
            cmd.Parameters.AddWithValue("@Reason", (object?)result.Reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FixAt", (object?)result.FixAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FixSuggestion", (object?)result.FixSuggestion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FixPermission", (object?)result.FixPermission ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(token);
        }

        const string updateSql = """
            UPDATE dbo.ASSISTANT_EVAL_RUN
            SET FINISHED_TIME = GETDATE(),
                STATUS = @Status,
                CASE_TOTAL = @Total,
                PASSED = @Passed,
                FAILED = @Failed,
                SKIPPED = @Skipped,
                NOTE = @Note
            WHERE RUN_ID = @RunId;
            """;
        await using (var cmd = new SqlCommand(updateSql, conn, transaction))
        {
            cmd.Parameters.AddWithValue("@Status", status);
            cmd.Parameters.AddWithValue("@Total", results.Count);
            cmd.Parameters.AddWithValue("@Passed", passed);
            cmd.Parameters.AddWithValue("@Failed", failed);
            cmd.Parameters.AddWithValue("@Skipped", skipped);
            cmd.Parameters.AddWithValue("@Note", (object?)note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RunId", runId);
            await cmd.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantEvalRunDto>> ListRunsAsync(int take, CancellationToken token)
    {
        var sql = $"""
            SELECT TOP (@Take) {RunColumns}
            FROM dbo.ASSISTANT_EVAL_RUN
            ORDER BY RUN_ID DESC;
            """;
        var runs = new List<AssistantEvalRunDto>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Take", Math.Clamp(take, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) runs.Add(ReadRun(reader));
        return runs;
    }

    /// <inheritdoc />
    public async Task<AssistantEvalRunDto?> GetRunAsync(int runId, CancellationToken token)
    {
        var sql = $"SELECT {RunColumns} FROM dbo.ASSISTANT_EVAL_RUN WHERE RUN_ID = @RunId;";
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@RunId", runId);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadRun(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantEvalResultDto>> LoadResultsAsync(int runId, CancellationToken token)
    {
        // 失败在前：这一页的主用法是"看完失败清单就知道该改哪几处"。
        const string sql = """
            SELECT CASE_ID, LAYER, OUTCOME, CHECK_COUNT, REASON, FIX_AT, FIX_SUGGESTION, FIX_PERMISSION
            FROM dbo.ASSISTANT_EVAL_RESULT
            WHERE RUN_ID = @RunId
            ORDER BY CASE WHEN OUTCOME = N'FAIL' THEN 0 WHEN OUTCOME = N'SKIP' THEN 2 ELSE 1 END,
                     ISNULL(FIX_AT, N'zz'), LAYER, CASE_ID;
            """;
        var results = new List<AssistantEvalResultDto>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@RunId", runId);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            results.Add(new AssistantEvalResultDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task PruneAsync(int keep, CancellationToken token)
    {
        const string sql = """
            DELETE FROM dbo.ASSISTANT_EVAL_RUN
            WHERE RUN_ID NOT IN (
                SELECT TOP (@Keep) RUN_ID FROM dbo.ASSISTANT_EVAL_RUN ORDER BY RUN_ID DESC
            );
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Keep", Math.Clamp(keep, 1, 500));
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static AssistantEvalRunDto ReadRun(SqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetDateTime(3),
        reader.IsDBNull(4) ? null : reader.GetDateTime(4),
        reader.GetString(5),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetInt32(8),
        reader.GetInt32(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));
}
