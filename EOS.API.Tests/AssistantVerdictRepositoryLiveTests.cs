using EOS.API.Data;
using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Quality;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 判定结论的**落库往返**（ADR-044 J1）：写一次判定 → 按会话/条款读回来 → 汇总四个数字 → 清理。
///
/// <para>
/// 用**自造的会话与消息**（编号带测试前缀），不借任何真实数据；判官模型用固定输出的桩，
/// 所以这条用例**不调用模型、不产生费用**，只考"存与读"是否一致。
/// </para>
///
/// <para>
/// 跑法：<c>dotnet test EOS.API.Tests/EOS.API.Tests.csproj --filter FullyQualifiedName~AssistantVerdictRepositoryLiveTests</c>，
/// 前置 <c>$env:MSSQL_ERP_CONN</c>；未配置即失败（不静默跳过，与本仓其它真库用例一致）。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantVerdictRepositoryLiveTests
{
    private const string UserId = "__judge_live_test__";

    [Fact]
    public async Task 结论落库后能按会话与条款筛选并汇总()
    {
        var connectionString = ResolveConnectionString()
            ?? throw new InvalidOperationException("本用例需要 MSSQL_ERP_CONN（真库）；未配置即失败，不静默跳过。");

        var connections = PolicyServiceFactory.Connections(connectionString);
        var repository = new AssistantVerdictRepository(connections);
        var judgeRepository = new AssistantJudgeRepository(connections);
        var limits = new AssistantJudgeLimitsOptions();

        long sessionId = 0;
        long messageId = 0;
        try
        {
            (sessionId, messageId) = await SeedConversationAsync(connections);
            var transcripts = await judgeRepository.LoadRecentAnswersAsync(200, limits, CancellationToken.None);
            var transcript = transcripts.SingleOrDefault(item => item.MessageId == messageId);
            Assert.NotNull(transcript);

            var judge = new AssistantAnswerJudge(new FixedJudgeModel("""
                {"verdicts":[
                  {"clause":"A","verdict":"FAIL","evidence":"合计 139,022,210.42"},
                  {"clause":"B","verdict":"FAIL","evidence":"全部时间"},
                  {"clause":"C","verdict":"NA"},
                  {"clause":"D","verdict":"PASS","evidence":"只出了一张请求卡"},
                  {"clause":"E","verdict":"PASS","evidence":"18,080"},
                  {"clause":"F","verdict":"NA"}
                ]}
                """));
            var outcome = await judge.JudgeAsync(transcript!, limits, CancellationToken.None);
            await repository.SaveAsync(outcome, CancellationToken.None);

            // 重复落库是幂等的：同一条回答 + 同一条款版本 + 同一模型只有一份结论
            await repository.SaveAsync(outcome, CancellationToken.None);

            var page = await repository.LoadPageAsync(
                new AssistantVerdictFilter(
                    DateTimeOffset.UtcNow.AddDays(-1), Clause: null, SessionId: sessionId,
                    Verdict: null, Page: 1, PageSize: 50),
                CancellationToken.None);

            Assert.Equal(AssistantJudgeRubric.Current.Clauses.Count, page.Total);
            Assert.All(page.Items, item =>
            {
                Assert.Equal(messageId, item.MessageId);
                Assert.Equal(sessionId, item.SessionId);
                Assert.Equal(UserId, item.UserId);
                Assert.True(item.CurrentRubric);
            });

            // 按条款筛选：只剩 A 一条，且证据逐字保留
            var clausePage = await repository.LoadPageAsync(
                new AssistantVerdictFilter(
                    DateTimeOffset.UtcNow.AddDays(-1), Clause: "A", SessionId: sessionId,
                    Verdict: null, Page: 1, PageSize: 50),
                CancellationToken.None);
            var only = Assert.Single(clausePage.Items);
            Assert.Equal(AssistantJudgeVerdicts.Fail, only.Verdict);
            Assert.Equal("合计 139,022,210.42", only.Evidence);

            // 汇总与逐条读数一致：判过 1 条回答、6 行结论，D 的 FAIL 为 0
            var summary = await repository.LoadSummaryAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);
            Assert.True(summary.JudgedMessages >= 1);
            Assert.True(summary.VerdictRows >= AssistantJudgeRubric.Current.Clauses.Count);
            Assert.Contains("stub/live-test-model", summary.JudgeModels);
        }
        finally
        {
            await CleanupAsync(connections, sessionId, messageId);
        }
    }

    private static async Task<(long SessionId, long MessageId)> SeedConversationAsync(DbConnectionFactory connections)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        long sessionId;
        await using (var command = new Microsoft.Data.SqlClient.SqlCommand("""
            INSERT INTO dbo.ASSISTANT_SESSION (USER_ID, TITLE, CREATED_TIME, LAST_ACTIVE_AT)
            OUTPUT INSERTED.ID
            VALUES (@UserId, N'判官巡回用例（可删）', SYSUTCDATETIME(), SYSUTCDATETIME());
            """, connection))
        {
            command.Parameters.AddWithValue("@UserId", UserId);
            sessionId = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        async Task<long> InsertMessageAsync(int role, string content, string? toolCalls)
        {
            await using var command = new Microsoft.Data.SqlClient.SqlCommand("""
                INSERT INTO dbo.ASSISTANT_MESSAGE (SESSION_ID, ROLE, CONTENT, TOOL_CALLS_JSON, CREATED_TIME)
                OUTPUT INSERTED.ID
                VALUES (@SessionId, @Role, @Content, @ToolCalls, SYSUTCDATETIME());
                """, connection);
            command.Parameters.AddWithValue("@SessionId", sessionId);
            command.Parameters.AddWithValue("@Role", role);
            command.Parameters.AddWithValue("@Content", content);
            command.Parameters.AddWithValue("@ToolCalls", (object?)toolCalls ?? DBNull.Value);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        await InsertMessageAsync(1, "9 月的营业额是多少？", null);
        var messageId = await InsertMessageAsync(
            2,
            "合计 139,022,210.42（全部时间，未说明口径）。",
            """[{"Name":"resolve_metric","ResultDigest":"total=139022210.42","Ok":true}]""");

        return (sessionId, messageId);
    }

    private static async Task CleanupAsync(DbConnectionFactory connections, long sessionId, long messageId)
    {
        if (sessionId == 0) return;

        await using var connection = connections.Create();
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("""
            DELETE FROM dbo.ASSISTANT_ANSWER_VERDICT WHERE SESSION_ID = @SessionId;
            DELETE FROM dbo.ASSISTANT_MESSAGE WHERE SESSION_ID = @SessionId;
            DELETE FROM dbo.ASSISTANT_SESSION WHERE ID = @SessionId AND USER_ID = @UserId;
            """, connection);
        command.Parameters.AddWithValue("@SessionId", sessionId);
        command.Parameters.AddWithValue("@UserId", UserId);
        await command.ExecuteNonQueryAsync();

        // 兜底：按消息号再清一次（会话删除失败时不留结论孤儿）
        if (messageId != 0)
        {
            await using var fallback = new Microsoft.Data.SqlClient.SqlCommand(
                "DELETE FROM dbo.ASSISTANT_ANSWER_VERDICT WHERE MESSAGE_ID = @MessageId;", connection);
            fallback.Parameters.AddWithValue("@MessageId", messageId);
            await fallback.ExecuteNonQueryAsync();
        }
    }

    private static string? ResolveConnectionString() =>
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN") is { Length: > 0 } value ? value : null;

    /// <summary>固定输出的判官模型：只用例考"存与读"，不考模型水平，也不产生费用。</summary>
    private sealed class FixedJudgeModel(string response) : IAssistantJudgeModel
    {
        public string Descriptor => "stub/live-test-model";

        public bool FellBackToChat => false;

        public Task<AssistantJudgeCompletion> CompleteAsync(string system, string user, CancellationToken token) =>
            Task.FromResult(new AssistantJudgeCompletion(response, 1, 1, Estimated: false, CostYuan: 0));
    }
}
