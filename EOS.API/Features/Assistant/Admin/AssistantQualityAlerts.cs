using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Quality;

namespace EOS.API.Features.Assistant.Admin;

/// <summary>
/// 一条质量告警。<paramref name="Severity"/> 取 <c>critical</c> / <c>warning</c>：
/// critical 只有一类（越界承诺），它的目标恒为 0，出现即红线被击穿。
/// </summary>
public sealed record AssistantQualityAlert(string Code, string Severity, string Title, string Message)
{
    public bool Critical => string.Equals(Severity, "critical", StringComparison.Ordinal);
}

/// <summary>
/// 判官的质量告警（ADR-044 §5 J3）：**只告警、不阻断**。
///
/// <para>
/// 三条来自 ADR，加两条"测量本身是否在跑"：
/// <list type="bullet">
/// <item><c>JUDGE_OVERREACH</c>：越界承诺 &gt; 0（**红线，恒 0，不参数化**）；</item>
/// <item><c>JUDGE_CITATION_LOW</c>：口径标注率低于告警下限；</item>
/// <item><c>JUDGE_UNSURE_HIGH</c>：无法判定率高于告警上限；</item>
/// <item><c>JUDGE_DAILY_CAP</c>：今日判定已达上限——**没有新结论不是"没问题"**；</item>
/// <item><c>JUDGE_DISABLED</c>：判定被关掉——看板上的数字会停住，必须说出来。</item>
/// </list>
/// </para>
///
/// <para>
/// **样本不足时不报比率类告警**：3 条里 1 条不通过就报"标注率 67%"，运维很快就会学会无视它。
/// 越界承诺不受此限——红线不看样本量，出现一次就要处理。
/// </para>
///
/// <para>
/// 这里是**纯计算**：阈值由参数目录给，指标由 <see cref="AssistantJudgeMetrics"/> 给，
/// 它自己既不读库也不写库，所以看板与后台任务能用同一段判断（不会出现"页面报警、后台不报"）。
/// </para>
/// </summary>
public static class AssistantQualityAlerts
{
    public const string Overreach = "JUDGE_OVERREACH";
    public const string CitationLow = "JUDGE_CITATION_LOW";
    public const string UnsureHigh = "JUDGE_UNSURE_HIGH";
    public const string DailyCap = "JUDGE_DAILY_CAP";
    public const string Disabled = "JUDGE_DISABLED";

    public static IReadOnlyList<AssistantQualityAlert> Evaluate(
        AssistantJudgeMetrics metrics, AssistantJudgeLimitsOptions limits, bool cappedToday)
    {
        var alerts = new List<AssistantQualityAlert>();

        if (!limits.Enabled)
        {
            alerts.Add(new AssistantQualityAlert(
                Disabled, "warning", "回答质量判定已关闭",
                "3105 的 JUDGE_ENABLED = 0：看板上的数字会停住，这不是「没问题」。"));
        }

        // 红线：不看样本量、不看比例，出现一次就报。
        if (metrics.OverreachCount > 0)
        {
            alerts.Add(new AssistantQualityAlert(
                Overreach, "critical", "越界承诺",
                $"有 {metrics.OverreachCount} 条回答声称做了不可代签的动作（批核/解批/结案/权限授予）。"
                + "目标恒为 0：先停用相关能力面并复盘，不得只标红不处置。"));
        }

        if (metrics.Judged < limits.AlertMinSamples)
        {
            return alerts;
        }

        if (metrics.CitationRate is { } citation && citation * 100 < limits.AlertCitationMinPct)
        {
            alerts.Add(new AssistantQualityAlert(
                CitationLow, "warning", "口径标注率偏低",
                $"口径标注率 {citation:P1} 低于告警下限 {limits.AlertCitationMinPct}%（判 {metrics.Judged} 条）。"
                + "先查条款文本是否写不清或判官模型是否换了——不要调低这条线。"));
        }

        if (metrics.UnsureRate is { } unsure && unsure * 100 > limits.AlertUnsureMaxPct)
        {
            alerts.Add(new AssistantQualityAlert(
                UnsureHigh, "warning", "无法判定率偏高",
                $"无法判定率 {unsure:P1} 高于告警上限 {limits.AlertUnsureMaxPct}%（判 {metrics.Judged} 条）。"
                + "通常是条款写得判不出来，或判官在拿不准时乱判。"));
        }

        if (cappedToday)
        {
            alerts.Add(new AssistantQualityAlert(
                DailyCap, "warning", "今日判定已达上限",
                $"今日判定次数或成本已达上限（{limits.DailyMaxCalls} 次 / {limits.DailyCostCapYuan:0.####} 元）："
                + "此后不再产出新结论——这是「没判」，不是「没问题」。"));
        }

        return alerts;
    }
}
