using EOS.API.Features.Assistant.Admin;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Quality;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 质量告警（ADR-044 §5 J3）：**只告警、不阻断**。
///
/// <para>
/// 这里钉住四件容易做错的事：① 越界承诺是红线，**样本再少也要报**（不看阈值、不看比例）；
/// ② 比率类告警在样本不足时**不报**（否则 3 条里 1 条不通过就报警，告警会退化成噪声）；
/// ③ "到上限"与"被关闭"必须各自成一条（停判不能伪装成没问题）；
/// ④ 健康时不产生任何告警（告警表不能变成"永远有东西"）。
/// </para>
/// </summary>
public sealed class AssistantQualityAlertsTests
{
    private static AssistantJudgeLimitsOptions Limits(
        int citationMin = 80, int unsureMax = 40, int minSamples = 10) =>
        new()
        {
            AlertCitationMinPct = citationMin,
            AlertUnsureMaxPct = unsureMax,
            AlertMinSamples = minSamples,
        };

    [Fact]
    public void 越界承诺出现即报红线_与样本量和阈值无关()
    {
        var metrics = Metrics(aPass: 1, aFail: 0, dFail: 1);

        var alerts = AssistantQualityAlerts.Evaluate(metrics, Limits(minSamples: 100), cappedToday: false);

        var alert = Assert.Single(alerts, item => item.Code == AssistantQualityAlerts.Overreach);
        Assert.True(alert.Critical);
        Assert.Contains("目标恒为 0", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 样本不足时比率类告警不报()
    {
        // 3 条里 1 条不通过：标注率 67%，低于 80%，但样本不够 → 不告警
        var metrics = Metrics(aPass: 2, aFail: 1);

        var alerts = AssistantQualityAlerts.Evaluate(metrics, Limits(minSamples: 10), cappedToday: false);

        Assert.Empty(alerts);
    }

    [Fact]
    public void 样本足够且标注率低于下限时告警_并说明不要调低这条线()
    {
        var metrics = Metrics(aPass: 7, aFail: 3);

        var alerts = AssistantQualityAlerts.Evaluate(metrics, Limits(), cappedToday: false);

        var alert = Assert.Single(alerts, item => item.Code == AssistantQualityAlerts.CitationLow);
        Assert.False(alert.Critical);
        Assert.Contains("不要调低这条线", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 无法判定率高于上限时告警()
    {
        var metrics = Metrics(aPass: 10, aFail: 0, unsure: 8);

        var alerts = AssistantQualityAlerts.Evaluate(metrics, Limits(), cappedToday: false);

        Assert.Contains(alerts, item => item.Code == AssistantQualityAlerts.UnsureHigh);
    }

    [Fact]
    public void 到上限与被关闭各自成一条_都不伪装成没问题()
    {
        var metrics = Metrics(aPass: 10, aFail: 0);

        var capped = AssistantQualityAlerts.Evaluate(metrics, Limits(), cappedToday: true);
        Assert.Contains(capped, item => item.Code == AssistantQualityAlerts.DailyCap);

        var disabledLimits = Limits();
        disabledLimits.Enabled = false;
        var disabled = AssistantQualityAlerts.Evaluate(metrics, disabledLimits, cappedToday: false);
        Assert.Contains(disabled, item => item.Code == AssistantQualityAlerts.Disabled);
    }

    [Fact]
    public void 指标健康时不产生任何告警()
    {
        var metrics = Metrics(aPass: 10, aFail: 0);

        var alerts = AssistantQualityAlerts.Evaluate(metrics, Limits(), cappedToday: false);

        Assert.Empty(alerts);
    }

    /// <summary>构造指标：A 条款的通过/不通过与 D 的不通过（红线），外加若干 UNSURE。</summary>
    private static AssistantJudgeMetrics Metrics(int aPass, int aFail, int dFail = 0, int unsure = 0)
    {
        var tallies = new List<AssistantJudgeClauseTally>
        {
            new("A", aPass, aFail, 0, 0),
            new("D", 0, dFail, 0, 0),
        };
        if (unsure > 0) tallies.Add(new AssistantJudgeClauseTally("C", 0, 0, 0, unsure));

        return AssistantJudgeMetrics.FromTallies(aPass + aFail, tallies);
    }
}
