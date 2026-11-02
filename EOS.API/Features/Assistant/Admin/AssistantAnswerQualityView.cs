using EOS.API.Data;
using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Quality;

namespace EOS.API.Features.Assistant.Admin;

/// <summary>
/// 「回答质量」区块：四个数字 + 逐条款计数 + 判定用量（ADR-044 §4.5）。
///
/// <para>
/// 四个数字**不在这里定义**：它们由 <see cref="AssistantJudgeMetrics"/> 一处算出，看板只是把
/// 库里已汇总的条款计数喂进去。这样离线回放（内存结果）与看板（落库结果）口径不可能分叉。
/// </para>
/// </summary>
/// <param name="Judged">窗口内判过的回答条数（回答数，不是条款数）。</param>
/// <param name="Clauses">逐条款的 PASS/FAIL/NA/UNSURE 计数。</param>
/// <param name="CitationRate">口径标注率（A：PASS/(PASS+FAIL)）。</param>
/// <param name="ScopeRate">范围一致率（B）。</param>
/// <param name="OverreachCount">越界承诺数（D 的 FAIL 绝对条数，目标恒 0）。</param>
/// <param name="UnsureRate">无法判定率（UNSURE/(PASS+FAIL+UNSURE)）。</param>
/// <param name="RubricVersion">当前条款版本（只统计这一版的结论；历史版本的结论不混进来）。</param>
/// <param name="JudgeModels">窗口内实际用过的判官模型（多于一个说明中途换过）。</param>
/// <param name="SpentYuan">窗口内判定花费（元），记在系统账户 __judge__。</param>
/// <param name="Calls">窗口内判定调用次数。</param>
/// <param name="DailyCalls">今日已判次数。</param>
/// <param name="DailyMaxCalls">今日次数上限（参数）。</param>
/// <param name="DailySpentYuan">今日已花（元）。</param>
/// <param name="DailyCostCapYuan">今日成本上限（参数）。</param>
/// <param name="Capped">今日是否已达上限（次数或成本）——达上限时"没有新结论"不是"没问题"。</param>
public sealed record AssistantAnswerQualityBlock(
    int Judged,
    IReadOnlyList<AssistantJudgeClauseStat> Clauses,
    double? CitationRate,
    double? ScopeRate,
    int OverreachCount,
    double? UnsureRate,
    string RubricVersion,
    IReadOnlyList<string> JudgeModels,
    decimal SpentYuan,
    int Calls,
    int DailyCalls,
    int DailyMaxCalls,
    decimal DailySpentYuan,
    decimal DailyCostCapYuan,
    bool Capped);

/// <summary>把「落库汇总 + 今日台账 + 判官参数」拼成看板区块。</summary>
public static class AssistantAnswerQualityView
{
    public static AssistantAnswerQualityBlock Build(
        AssistantVerdictSummary summary, AssistantLedgerDay today, AssistantJudgeLimitsOptions limits) =>
        Build(AssistantJudgeMetrics.FromTallies(summary.JudgedMessages, summary.Clauses),
            summary, today, limits);

    /// <summary>同一份计算，指标已由调用方算好时直接传进来（看板要同时用它算告警）。</summary>
    public static AssistantAnswerQualityBlock Build(
        AssistantJudgeMetrics metrics, AssistantVerdictSummary summary,
        AssistantLedgerDay today, AssistantJudgeLimitsOptions limits)
    {
        var dailySpent = today.SpentMicroYuan / AssistantCost.MicroYuanPerYuan;

        return new AssistantAnswerQualityBlock(
            metrics.Judged,
            metrics.Clauses,
            metrics.CitationRate,
            metrics.ScopeRate,
            metrics.OverreachCount,
            metrics.UnsureRate,
            AssistantJudgeRubric.CurrentVersion,
            summary.JudgeModels,
            summary.SpentMicroYuan / AssistantCost.MicroYuanPerYuan,
            summary.JudgeCalls,
            today.Requests,
            limits.DailyMaxCalls,
            dailySpent,
            limits.DailyCostCapYuan,
            today.Requests >= limits.DailyMaxCalls || dailySpent >= limits.DailyCostCapYuan);
    }
}
