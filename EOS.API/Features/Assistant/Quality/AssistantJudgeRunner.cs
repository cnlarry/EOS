using EOS.API.Data;
using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Quality;

/// <summary>一轮判定的结果（后台扫描与手动触发共用）。</summary>
/// <param name="Candidates">本轮取到的待判回答数。</param>
/// <param name="Judged">实际判了几条。</param>
/// <param name="SkippedBySampling">按抽样比例跳过的条数。</param>
/// <param name="SkippedByCap">因日次数/日成本上限没判的条数（与"抽样跳过"分开计数，见下）。</param>
/// <param name="Failed">调用失败条数（失败只记日志，不影响其它条）。</param>
/// <param name="StopReason">为什么这一轮提前结束；正常跑完为 null。</param>
/// <param name="CostYuan">本轮花费（元）。</param>
public sealed record AssistantJudgeRunResult(
    int Candidates,
    int Judged,
    int SkippedBySampling,
    int SkippedByCap,
    int Failed,
    string? StopReason,
    double CostYuan)
{
    public static AssistantJudgeRunResult Disabled { get; } =
        new(0, 0, 0, 0, 0, "未启用（JUDGE_ENABLED = 0）", 0);
}

/// <summary>
/// 判分的执行者：取待判回答 → 抽样 → 判 → 落库 → 记账。
///
/// <para>
/// 它自己**不碰 SQL、不判权限**（仓储与判官各自负责），这样"判分跑在哪个宿主里"不改变业务语义：
/// 后台扫描、管理端手动触发、将来的评估工具调的都是这一段。
/// </para>
///
/// <para>
/// **到上限不是"静默停判"**：<see cref="AssistantJudgeRunResult.StopReason"/> 会带着原因返回，
/// 调用方记日志，看板另有一块"今日已判/上限，已达上限"的明示——停判伪装成"没问题"是这条设计
/// 明确要避免的失败形态（ADR-044 §6）。
/// </para>
/// </summary>
public sealed class AssistantJudgeRunner(
    IAssistantJudgeRepository judgeRepository,
    IAssistantVerdictRepository verdictRepository,
    IAssistantUsageRepository usageRepository,
    IAssistantEffectiveParameters parameters,
    AssistantAnswerJudge judge,
    ILogger<AssistantJudgeRunner> logger)
{
    /// <summary>
    /// 跑一轮。<paramref name="limit"/> 是调用方想判几条（手动触发用），最终取
    /// <c>min(limit, JUDGE_BATCH_SIZE)</c>——手动触发不能绕过批量上限。
    /// </summary>
    public async Task<AssistantJudgeRunResult> RunAsync(int? limit, CancellationToken token)
    {
        // 判分是系统行为：只有全局层（userId 用系统账户，模块无关），不叠加任何人的作用域覆盖。
        var policy = await parameters.ForAsync(AssistantJudgeAccounts.SystemUserId, null, token);
        var limits = policy.Judge;
        if (!limits.Enabled) return AssistantJudgeRunResult.Disabled;

        var batch = Math.Clamp(limit ?? limits.BatchSize, 1, Math.Max(1, limits.BatchSize));
        var now = DateTimeOffset.UtcNow;
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var ledger = await usageRepository.GetLedgerDayAsync(AssistantJudgeAccounts.SystemUserId, dayStart, token);
        var dailyCalls = ledger.Requests;
        var dailyCost = ledger.SpentMicroYuan / (double)AssistantCost.MicroYuanPerYuan;

        var candidates = await judgeRepository.LoadCandidatesAsync(
            now.AddDays(-Math.Max(1, limits.WindowDays)),
            batch,
            AssistantJudgeRubric.CurrentVersion,
            limits,
            token);

        int judged = 0, skippedBySampling = 0, skippedByCap = 0, failed = 0;
        double cost = 0;
        string? stopReason = null;
        Exception? firstFailure = null;
        long firstFailureMessageId = 0;

        for (var index = 0; index < candidates.Count; index++)
        {
            token.ThrowIfCancellationRequested();

            if (dailyCalls >= limits.DailyMaxCalls)
            {
                stopReason = $"已达每日判定次数上限（{limits.DailyMaxCalls} 次），本轮剩余 {candidates.Count - index} 条未判。";
                skippedByCap = candidates.Count - index;
                break;
            }

            if (dailyCost >= (double)limits.DailyCostCapYuan)
            {
                stopReason = $"已达每日判定成本上限（{limits.DailyCostCapYuan:0.####} 元，今日已花 {dailyCost:0.####} 元），"
                    + $"本轮剩余 {candidates.Count - index} 条未判。";
                skippedByCap = candidates.Count - index;
                break;
            }

            var transcript = candidates[index];
            if (!AssistantJudgeSampling.ShouldJudge(transcript, limits))
            {
                skippedBySampling++;
                continue;
            }

            try
            {
                var outcome = await judge.JudgeAsync(transcript, limits, token);
                await verdictRepository.SaveAsync(outcome, token);

                // 记账用独立 token：请求被取消时这一笔已经花掉了，账必须落库。
                await usageRepository.RecordSpendAsync(
                    AssistantJudgeAccounts.SystemUserId, dayStart,
                    AssistantUsageLedger.ToMicroYuan(outcome.Usage.CostYuan), 1, CancellationToken.None);

                judged++;
                dailyCalls++;
                cost += outcome.Usage.CostYuan;
                dailyCost += outcome.Usage.CostYuan;

                logger.LogInformation(
                    "判官完成一条：message={MessageId} model={Model} fallback={Fallback} cost={Cost:0.######} 元",
                    outcome.MessageId, outcome.JudgeModel, outcome.FellBackToChat, outcome.Usage.CostYuan);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failed++;
                // 逐条失败记 Debug，本轮结束再汇总一条 Warning：供应商不可用时每轮 20 条警告
                // 会把日志变成噪声，而运维真正需要知道的是"这一轮一条都没判成"。
                if (firstFailure is null)
                {
                    firstFailure = error;
                    firstFailureMessageId = transcript.MessageId;
                }

                logger.LogDebug(error,
                    "判官判定失败（已跳过该条）：message={MessageId}", transcript.MessageId);
            }
        }

        if (failed > 0)
        {
            logger.LogWarning(firstFailure,
                "判官本轮有 {Failed} 条判定失败（首条 message={MessageId}，判成 {Judged} 条）："
                + "结论不会凭空产生——先看模型供应商是否可用/密钥是否有效。",
                failed, firstFailureMessageId, judged);
        }

        if (stopReason is not null) logger.LogWarning("判官本轮回退：{Reason}", stopReason);

        return new AssistantJudgeRunResult(
            candidates.Count, judged, skippedBySampling, skippedByCap, failed, stopReason, cost);
    }
}
