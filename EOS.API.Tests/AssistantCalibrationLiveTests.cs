using EOS.API.Data;
using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Admin;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Quality;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 校准读数的**真库往返**：一条自造的 judge 层样本 + 一次真判定落库 → 按 messageId 对上一致率。
///
/// <para>
/// 判官模型用固定输出的桩，所以**不调用模型、不产生费用**；数据全部自造（会话/消息/样本），
/// 收尾按自己造的键删干净。这条用例的价值是证明"两表按 messageId 真的能对上"——
/// 那是口径正确的前提，纯函数测试证明不了。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantCalibrationLiveTests
{
    private const string UserId = "__judge_calibration_live__";
    private const string CaseId = "__JG_LIVE__";

    [Fact]
    public async Task 样本标签与判官结论按回答编号对上并算出一致率()
    {
        var connectionString = ResolveConnectionString()
            ?? throw new InvalidOperationException("本用例需要 MSSQL_ERP_CONN（真库）；未配置即失败，不静默跳过。");

        var connections = PolicyServiceFactory.Connections(connectionString);
        var verdicts = new AssistantVerdictRepository(connections);
        var calibration = new AssistantCalibrationRepository(connections);
        var limits = new AssistantJudgeLimitsOptions();

        long sessionId = 0;
        long messageId = 0;
        try
        {
            (sessionId, messageId) = await SeedAsync(connections);

            var transcript = new AssistantJudgeTranscript(
                messageId, sessionId, UserId, "9 月的营业额是多少？",
                "合计 139,022,210.42（全部时间，未说明口径）。",
                [new AssistantJudgeToolCall("resolve_metric", "total=139022210.42", true)],
                DateTimeOffset.UtcNow);
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
            await verdicts.SaveAsync(
                await judge.JudgeAsync(transcript, limits, CancellationToken.None), CancellationToken.None);

            // 人的标签：A/C/E/F 与判官一致，B 判 PASS（判官 FAIL）、D 判 NA（判官 PASS）→ 两条不一致
            await InsertSampleAsync(connections, messageId);

            var samples = await calibration.LoadSamplesAsync(CancellationToken.None);
            var messageIds = samples
                .Where(sample => sample.MessageId is not null)
                .Select(sample => sample.MessageId!.Value)
                .Distinct()
                .ToList();
            var rows = await calibration.LoadVerdictsAsync(
                messageIds, AssistantJudgeRubric.CurrentVersion, CancellationToken.None);
            var report = AssistantCalibrationView.Build(samples, rows);

            Assert.True(report.Samples >= 1);
            Assert.True(report.Reviewed >= 1);
            Assert.True(report.Compared >= 1);
            Assert.NotNull(report.AgreementRate);
            Assert.Contains(report.Mismatches, item => item.MessageId == messageId && item.Clause == "B");
            Assert.Contains(report.Mismatches, item => item.MessageId == messageId && item.Clause == "D");
            // 人判 PASS / 判官判 FAIL 是误报，不是漏报：危险计数必须仍为 0
            Assert.DoesNotContain(report.Mismatches, item => item.MessageId == messageId && item.Dangerous);
        }
        finally
        {
            await CleanupAsync(connections, sessionId, messageId);
        }
    }

    private static async Task<(long SessionId, long MessageId)> SeedAsync(DbConnectionFactory connections)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        long sessionId;
        await using (var command = new Microsoft.Data.SqlClient.SqlCommand("""
            INSERT INTO dbo.ASSISTANT_SESSION (USER_ID, TITLE, CREATED_TIME, LAST_ACTIVE_AT)
            OUTPUT INSERTED.ID
            VALUES (@UserId, N'校准读数用例（可删）', SYSUTCDATETIME(), SYSUTCDATETIME());
            """, connection))
        {
            command.Parameters.AddWithValue("@UserId", UserId);
            sessionId = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        long messageId;
        await using (var command = new Microsoft.Data.SqlClient.SqlCommand("""
            INSERT INTO dbo.ASSISTANT_MESSAGE (SESSION_ID, ROLE, CONTENT, TOOL_CALLS_JSON, CREATED_TIME)
            OUTPUT INSERTED.ID
            VALUES (@SessionId, 2, @Content, @ToolCalls, SYSUTCDATETIME());
            """, connection))
        {
            command.Parameters.AddWithValue("@SessionId", sessionId);
            command.Parameters.AddWithValue("@Content", "合计 139,022,210.42（全部时间，未说明口径）。");
            command.Parameters.AddWithValue("@ToolCalls",
                """[{"Name":"resolve_metric","ResultDigest":"total=139022210.42","Ok":true}]""");
            messageId = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        return (sessionId, messageId);
    }

    private static async Task InsertSampleAsync(DbConnectionFactory connections, long messageId)
    {
        var payload = $$"""
            {"id":"{{CaseId}}","messageId":{{messageId}},"question":"9 月的营业额是多少？",
             "answer":"合计 139,022,210.42（全部时间，未说明口径）。",
             "labels":[
               {"clause":"A","verdict":"FAIL","evidence":"合计 139,022,210.42"},
               {"clause":"B","verdict":"PASS","evidence":"回答里写了时间范围"},
               {"clause":"C","verdict":"NA"},
               {"clause":"D","verdict":"NA"},
               {"clause":"E","verdict":"PASS","evidence":"18,080"},
               {"clause":"F","verdict":"NA"}
             ],
             "reviewed":true,"reviewedBy":"live-test","reviewedAt":"2026-11-02"}
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("""
            IF NOT EXISTS (SELECT 1 FROM dbo.ASSISTANT_EVAL_CASE WHERE CASE_ID = @CaseId)
                INSERT INTO dbo.ASSISTANT_EVAL_CASE
                    (CASE_ID, LAYER, PAYLOAD_JSON, REVIEW_STATUS, ORIGIN, CASE_VERSION,
                     CREATED_BY, CREATED_TIME)
                VALUES (@CaseId, N'judge', @Payload, N'DRAFT', N'live-test', 1, @UserId, GETDATE());
            """, connection);
        command.Parameters.AddWithValue("@CaseId", CaseId);
        command.Parameters.AddWithValue("@Payload", payload);
        command.Parameters.AddWithValue("@UserId", "jglive");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupAsync(DbConnectionFactory connections, long sessionId, long messageId)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using (var command = new Microsoft.Data.SqlClient.SqlCommand("""
            DELETE FROM dbo.ASSISTANT_EVAL_CASE WHERE CASE_ID = @CaseId;
            DELETE FROM dbo.ASSISTANT_ANSWER_VERDICT WHERE SESSION_ID = @SessionId;
            DELETE FROM dbo.ASSISTANT_MESSAGE WHERE SESSION_ID = @SessionId;
            DELETE FROM dbo.ASSISTANT_SESSION WHERE ID = @SessionId AND USER_ID = @UserId;
            """, connection))
        {
            command.Parameters.AddWithValue("@CaseId", CaseId);
            command.Parameters.AddWithValue("@SessionId", sessionId);
            command.Parameters.AddWithValue("@UserId", UserId);
            await command.ExecuteNonQueryAsync();
        }

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
