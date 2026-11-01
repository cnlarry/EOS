using EOS.API.Features.Assistant.Admin;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 质量看板聚合：工具调用按类型统计、拒绝/未知工具按稳定标记计、工具轮数分桶、
/// token/成本/反馈汇总；历史坏 JSON 不能让看板 500。
/// </summary>
public sealed class AssistantQualityViewTests
{
    private static AssistantQualityMessage Msg(
        long session, DateTimeOffset at, int role, string? tools = null,
        int prompt = 10, int completion = 5, int elapsed = 100, int? feedback = null) =>
        new(session, at, role, prompt, completion, elapsed, feedback, tools);

    [Fact]
    public void Aggregates_Tools_Rounds_Tokens_And_Feedback()
    {
        var since = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var messages = new[]
        {
            Msg(1, since.AddHours(1), 1, prompt: 0, completion: 0, elapsed: 0),
            Msg(1, since.AddHours(1), 2,
                """[{"Name":"search_records","ResultDigest":"ok","Ok":true},{"Name":"list_document_actions","ResultDigest":"ok","Ok":true}]""",
                prompt: 100, completion: 20, elapsed: 200, feedback: 1),
            Msg(2, since.AddDays(1), 2,
                """[{"Name":"apply_record_action","ResultDigest":"rejected:unknown_tool","Ok":false}]""",
                prompt: 50, completion: 10, elapsed: 400, feedback: -1),
        };

        var report = AssistantQualityView.Build(
            7, since, messages, new AssistantQualityUsage(1_234_000, 2));

        Assert.Equal(2, report.Overview.Sessions);
        Assert.Equal(1, report.Overview.UserMessages);
        Assert.Equal(2, report.Overview.AssistantMessages);
        Assert.Equal(3, report.Overview.ToolCalls);
        Assert.Equal(2, report.Overview.SucceededCalls);
        Assert.Equal(1, report.Overview.FailedCalls);
        Assert.Equal(0, report.Overview.UnknownOutcomeCalls);
        Assert.Equal(1, report.Overview.RejectedCalls);
        Assert.Equal(1, report.Overview.UnknownToolCalls);
        Assert.Equal(150, report.Overview.PromptTokens);
        Assert.Equal(30, report.Overview.CompletionTokens);
        Assert.Equal(300, report.Overview.AverageElapsedMs);
        Assert.Equal(1, report.Overview.PositiveFeedback);
        Assert.Equal(1, report.Overview.NegativeFeedback);
        Assert.Equal(1.234m, report.SpentYuan);
        Assert.Equal(2, report.Requests);

        Assert.Equal(3, report.Tools.Count);
        Assert.Contains(report.Tools, tool =>
            tool.Name == "list_document_actions" && tool.Calls == 1 && tool.Succeeded == 1);
        Assert.Contains(report.Tools, tool =>
            tool.Name == "apply_record_action" && tool.Failed == 1 && tool.Rejected == 1 && tool.Unknown == 1);

        Assert.Equal(2, report.Daily.Count);
        Assert.Equal(2, report.Daily[0].ToolCalls);
        Assert.Equal(1, report.Daily[1].Failed);

        Assert.Equal(1, report.CallBuckets.Single(bucket => bucket.CallsPerReply == 2).Replies);
        Assert.Equal(1, report.CallBuckets.Single(bucket => bucket.CallsPerReply == 1).Replies);
    }

    [Fact]
    public void Bad_Tool_Json_Is_Treated_As_No_Calls()
    {
        var since = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var report = AssistantQualityView.Build(
            7, since,
            [Msg(1, since, 2, "{not-json", prompt: 5, completion: 2)],
            new AssistantQualityUsage(0, 0));

        Assert.Equal(0, report.Overview.ToolCalls);
        Assert.Equal(1, report.Overview.AssistantMessages);
        Assert.Equal(0, report.CallBuckets.Single().CallsPerReply);
    }

    [Fact]
    public void Empty_Window_Returns_Zeroes()
    {
        var since = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var report = AssistantQualityView.Build(7, since, [], new AssistantQualityUsage(0, 0));

        Assert.Equal(0, report.Overview.Sessions);
        Assert.Empty(report.Tools);
        Assert.Empty(report.Daily);
        Assert.Empty(report.CallBuckets);
    }
}
