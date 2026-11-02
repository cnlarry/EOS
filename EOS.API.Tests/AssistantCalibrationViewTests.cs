using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Admin;
using EOS.API.Features.Assistant.Quality;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 判官校准读数（ADR-044 §5 J2）：把"用户给的标签"与"库内判官结论"对起来算一致率。
///
/// <para>
/// 这里**不调模型、不连库**：考的是口径本身——草稿不计入、缺 messageId 不计入、
/// 取同一回答同一条款的最近一份结论、NA/UNSURE 也算一种判断（不同就是不同）。
/// 这些规则一旦放宽，一致率会自己变好看，而这正是最危险的一类"指标优化"。
/// </para>
/// </summary>
public sealed class AssistantCalibrationViewTests
{
    private const long MessageId = 34092;

    [Fact]
    public void 只算已审阅且能对上的样本_草稿不计入()
    {
        var samples = new List<AssistantCalibrationSample>
        {
            Sample("JG-01", reviewed: true, labels: Labels(("A", "FAIL"), ("B", "PASS"))),
            Sample("JG-02", reviewed: false, labels: Labels(("A", "FAIL"), ("B", "PASS"))),
        };
        var verdicts = new List<AssistantCalibrationVerdict>
        {
            Verdict("A", "FAIL"), Verdict("B", "PASS"),
        };

        var report = AssistantCalibrationView.Build(samples, verdicts);

        Assert.Equal(2, report.Samples);
        Assert.Equal(1, report.Reviewed);
        Assert.Equal(1, report.Compared);
        Assert.Equal(0, report.Uncompared);
        Assert.Equal(1.0, report.AgreementRate);
        Assert.Empty(report.Mismatches);
    }

    [Fact]
    public void 人判FAIL而判官判PASS是漏报_单独计数()
    {
        var samples = new List<AssistantCalibrationSample>
        {
            Sample("JG-01", reviewed: true, labels: Labels(("A", "FAIL"), ("B", "PASS"))),
            Sample("JG-02", reviewed: true, labels: Labels(("A", "PASS"), ("B", "PASS"))),
        };
        var verdicts = new List<AssistantCalibrationVerdict>
        {
            Verdict("A", "PASS", messageId: MessageId),
            Verdict("A", "FAIL", messageId: MessageId + 1),
            Verdict("B", "PASS"),
        };

        var report = AssistantCalibrationView.Build(samples, verdicts);

        Assert.Equal(2, report.Compared);
        Assert.Equal(1, report.DangerousCount);
        var mismatch = Assert.Single(report.Mismatches, item => item.Dangerous);
        Assert.Equal("A", mismatch.Clause);
        Assert.Equal(AssistantJudgeVerdicts.Fail, mismatch.HumanVerdict);
        Assert.Equal(AssistantJudgeVerdicts.Pass, mismatch.JudgeVerdict);
        // 两边的话都得留着：改条款时要看人为什么这么判、判官引的又是哪一句
        Assert.Equal("人说不是这样", mismatch.HumanEvidence);
        Assert.Equal("判官说是这样", mismatch.JudgeEvidence);
        Assert.False(string.IsNullOrWhiteSpace(mismatch.JudgeModel));
    }

    [Fact]
    public void 同一回答同一条款有多份结论时取最近一份()
    {
        // 换过判官模型：新用 v4-pro 重判过，旧 flash 的结论不该盖住它
        var samples = new List<AssistantCalibrationSample>
        {
            Sample("JG-01", reviewed: true, labels: Labels(("A", "FAIL"))),
        };
        var verdicts = new List<AssistantCalibrationVerdict>
        {
            new(MessageId, "A", "FAIL", "新结论", "deepseek/deepseek-v4-pro",
                DateTimeOffset.UtcNow),
            new(MessageId, "A", "PASS", "旧结论", "deepseek/deepseek-flash",
                DateTimeOffset.UtcNow.AddDays(-3)),
        };

        var report = AssistantCalibrationView.Build(samples, verdicts);

        Assert.Equal(1.0, report.AgreementRate);
        Assert.Empty(report.Mismatches);
        Assert.Contains("deepseek/deepseek-v4-pro", report.JudgeModels);
    }

    [Fact]
    public void 没有可比对的样本时一致率是空而不是零()
    {
        var report = AssistantCalibrationView.Build([], []);

        Assert.Null(report.AgreementRate);
        Assert.Equal(0, report.Compared);
        Assert.Equal(AssistantJudgeRubric.CurrentVersion, report.RubricVersion);
    }

    [Fact]
    public void 一条都没审阅时说明原因为什么是零()
    {
        var samples = new List<AssistantCalibrationSample>
        {
            Sample("JG-01", reviewed: false, labels: Labels(("A", "PASS"))),
            Sample("JG-02", reviewed: false, labels: Labels(("A", "PASS"))),
        };

        var report = AssistantCalibrationView.Build(samples, []);

        Assert.Equal(2, report.Samples);
        Assert.Equal(0, report.Reviewed);
        Assert.Null(report.AgreementRate);
        Assert.Contains(report.Notes, note => note.Contains("都还没审阅", StringComparison.Ordinal));
    }

    [Fact]
    public void 缺messageId或判官还没判过时算未比对并说明原因()
    {
        var samples = new List<AssistantCalibrationSample>
        {
            new("JG-01", null, true, Labels(("A", "PASS"))),
            Sample("JG-02", reviewed: true, labels: Labels(("A", "PASS")), messageId: 999_999),
        };

        var report = AssistantCalibrationView.Build(samples, []);

        Assert.Equal(2, report.Reviewed);
        Assert.Equal(0, report.Compared);
        Assert.Equal(2, report.Uncompared);
        Assert.Null(report.AgreementRate);
        Assert.Contains(report.Notes, note => note.Contains("messageId", StringComparison.Ordinal));
        Assert.Contains(report.Notes, note => note.Contains("还没有对回答 999999", StringComparison.Ordinal));
    }

    [Fact]
    public void 无法解析的样本进注释而不是被丢掉()
    {
        var samples = new List<AssistantCalibrationSample>
        {
            new("JG-01", MessageId, true, [], Problem: "正文不是合法 JSON"),
        };

        var report = AssistantCalibrationView.Build(samples, []);

        Assert.Equal(1, report.Reviewed);
        Assert.Equal(1, report.Uncompared);
        Assert.Contains(report.Notes, note => note.Contains("正文不是合法 JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void NA与UNSURE也算一种判断_不同就是不一致()
    {
        var samples = new List<AssistantCalibrationSample>
        {
            Sample("JG-01", reviewed: true, labels: Labels(("A", "NA"), ("B", "UNSURE"))),
        };
        var verdicts = new List<AssistantCalibrationVerdict>
        {
            Verdict("A", "PASS"), Verdict("B", "PASS"),
        };

        var report = AssistantCalibrationView.Build(samples, verdicts);

        Assert.Equal(0.0, report.AgreementRate);
        Assert.Equal(2, report.Mismatches.Count);
        // "人判 NA、判官判 PASS"不是漏报（人没说要 FAIL），不进危险计数
        Assert.Equal(0, report.DangerousCount);
    }

    private static AssistantCalibrationSample Sample(
        string caseId, bool reviewed, IReadOnlyList<AssistantCalibrationLabel> labels,
        long messageId = MessageId) =>
        new(caseId, messageId, reviewed, labels);

    private static AssistantCalibrationLabel[] Labels(params (string Clause, string Verdict)[] items) =>
        [.. items.Select(item => new AssistantCalibrationLabel(item.Clause, item.Verdict, EvidenceOf(item.Verdict)))];

    private static string? EvidenceOf(string verdict) =>
        verdict is AssistantJudgeVerdicts.Pass or AssistantJudgeVerdicts.Fail
            ? (verdict == AssistantJudgeVerdicts.Fail ? "人说不是这样" : "回答原文一句")
            : null;

    private static AssistantCalibrationVerdict Verdict(
        string clause, string verdict, long messageId = MessageId) =>
        new(messageId, clause, verdict,
            verdict is AssistantJudgeVerdicts.Pass or AssistantJudgeVerdicts.Fail ? "判官说是这样" : null,
            "stub/model", DateTimeOffset.UtcNow);
}
