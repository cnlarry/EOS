using EOS.API.Data;
using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Admin;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Quality;

namespace EOS.API.Services;

/// <summary>
/// 判官的后台扫描：按 <c>JUDGE_SCAN_INTERVAL_MINUTES</c> 周期跑一轮判定，并顺手清理过期结论。
///
/// <para>
/// 与 <see cref="ReportInboxScheduler"/> 同一形态（进程内定时轮询、无 Hangfire/Quartz）：
/// 判定是离线批处理，**与对话请求完全解耦**——对话不等判定，判定也不因对话失败而中断。
/// 参数每轮现读，所以"关掉判官"或"改采样率"不需要重启（与 3105 其它参数同口径）。
/// </para>
///
/// <para>
/// 失败只记 Warning：判分失败不能影响任何人的正常使用，但**也不能静默**——异常进日志，
/// 看板上的"今日已判"数字会停住，两处一起看才知道是"没判"而不是"没问题"。
/// </para>
/// </summary>
public sealed class AssistantJudgeScheduler(
    IServiceProvider serviceProvider,
    ILogger<AssistantJudgeScheduler> logger) : BackgroundService
{
    /// <summary>参数读不到时的重试间隔（判官自己的参数在库读不出来时，用这个固定退避）。</summary>
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(10);

    /// <summary>当前处于告警状态的告警码（进程内，只用于"变了才写日志"）。</summary>
    private readonly HashSet<string> _activeAlerts = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("判官后台扫描已启动（间隔随 JUDGE_SCAN_INTERVAL_MINUTES 现读）。");

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = FallbackInterval;
            try
            {
                using var scope = serviceProvider.CreateScope();
                var effective = scope.ServiceProvider.GetRequiredService<IAssistantEffectiveParameters>();
                var limits = (await effective.ForAsync(
                    AssistantJudgeAccounts.SystemUserId, null, stoppingToken)).Judge;

                interval = TimeSpan.FromMinutes(Math.Max(1, limits.ScanIntervalMinutes));

                if (limits.Enabled)
                {
                    var verdicts = scope.ServiceProvider.GetRequiredService<IAssistantVerdictRepository>();
                    var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, limits.RetentionDays));
                    var removed = await verdicts.DeleteOlderThanAsync(cutoff, 5_000, stoppingToken);
                    if (removed > 0)
                    {
                        logger.LogInformation(
                            "判官清理过期结论 {Removed} 条（保留 {Days} 天）。", removed, limits.RetentionDays);
                    }

                    var runner = scope.ServiceProvider.GetRequiredService<AssistantJudgeRunner>();
                    await runner.RunAsync(null, stoppingToken);
                    await LogAlertChangesAsync(scope.ServiceProvider, limits, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "判官后台扫描异常，下个周期重试。");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 把质量告警的**状态变化**写进日志（新增的报 Warning，消失的报 Information）。
    ///
    /// <para>
    /// 只在变化时写：每 10 分钟重复报同一条告警，运维两天就学会过滤它了。
    /// 状态是**进程内**的（不做表）：它只是"这条日志写过没有"的记账，重启后重报一次无害，
    /// 而落一张表就多一处需要清理的真相。看板上的告警是实时算的，以它为准。
    /// </para>
    /// </summary>
    private async Task LogAlertChangesAsync(
        IServiceProvider provider, AssistantJudgeLimitsOptions limits, CancellationToken token)
    {
        var verdicts = provider.GetRequiredService<IAssistantVerdictRepository>();
        var usage = provider.GetRequiredService<IAssistantUsageRepository>();

        var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, limits.WindowDays));
        var summary = await verdicts.LoadSummaryAsync(since, token);
        var today = await usage.GetLedgerDayAsync(
            AssistantJudgeAccounts.SystemUserId,
            new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero), token);
        var metrics = AssistantJudgeMetrics.FromTallies(summary.JudgedMessages, summary.Clauses);
        var dailySpent = today.SpentMicroYuan / AssistantCost.MicroYuanPerYuan;
        var capped = today.Requests >= limits.DailyMaxCalls || dailySpent >= limits.DailyCostCapYuan;

        var active = AssistantQualityAlerts.Evaluate(metrics, limits, capped)
            .ToDictionary(alert => alert.Code, alert => alert, StringComparer.Ordinal);

        foreach (var alert in active.Values.Where(alert => !_activeAlerts.Contains(alert.Code)))
        {
            if (alert.Critical)
            {
                logger.LogError("质量告警[{Code}] {Title}：{Message}", alert.Code, alert.Title, alert.Message);
            }
            else
            {
                logger.LogWarning("质量告警[{Code}] {Title}：{Message}", alert.Code, alert.Title, alert.Message);
            }
        }

        foreach (var code in _activeAlerts.Where(code => !active.ContainsKey(code)).ToList())
        {
            logger.LogInformation("质量告警[{Code}] 已解除。", code);
        }

        _activeAlerts.Clear();
        foreach (var code in active.Keys) _activeAlerts.Add(code);
    }
}
