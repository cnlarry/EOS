using EOS.API.Data;
using EOS.API.Data.Assistant;
using EOS.API.Errors;
using EOS.API.Features.Assistant.Admin;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Quality;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 工作助手质量看板：工具使用、拒绝/未知工具、工具轮数、token 与成本，以及**回答质量**（ADR-044）。
/// 只读聚合，按 3102（模型与用量）的 Setup 位授权；不返回消息正文与工具参数。
///
/// <para>
/// 结论列表只回**证据原文与备注**（判 PASS/FAIL 必须逐字引用回答里的一句），不回整段回答——
/// 看板要能解释"为什么这条被判失败"，但不该变成第二个读全文的出口。
/// </para>
///
/// <para>
/// **写入口只有一个**：手动触发一轮判定（<c>POST judge</c>）。它不改任何业务数据，
/// 只花判分预算；与后台扫描调的是同一个 <see cref="AssistantJudgeRunner"/>。
/// </para>
/// </summary>
[ApiController, Authorize, Route("api/v1/admin/assistant/quality")]
public sealed class AssistantQualityController(
    IAssistantQualityRepository repository,
    IAssistantVerdictRepository verdicts,
    IAssistantCalibrationRepository calibration,
    IAssistantUsageRepository usage,
    IAssistantEffectiveParameters effectiveParameters,
    AssistantJudgeRunner judgeRunner,
    IPermissionService permissions,
    CurrentUserContext userContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int days = 7, CancellationToken token = default)
    {
        if (!await CanSetupAsync(token)) return Forbid();

        var window = Math.Clamp(days, 1, 90);
        var since = StartOfWindow(window);
        var messages = await repository.LoadMessagesAsync(since, token);
        var usageWindow = await repository.LoadUsageAsync(since, token);

        var report = AssistantQualityView.Build(window, since, messages, usageWindow);

        var summary = await verdicts.LoadSummaryAsync(since, token);
        var today = await usage.GetLedgerDayAsync(
            AssistantJudgeAccounts.SystemUserId, TodayUtc(), token);
        var limits = (await effectiveParameters.ForAsync(
            AssistantJudgeAccounts.SystemUserId, null, token)).Judge;

        // 指标只算一次：区块与告警共用，避免两处口径漂移
        var metrics = AssistantJudgeMetrics.FromTallies(summary.JudgedMessages, summary.Clauses);
        var block = AssistantAnswerQualityView.Build(metrics, summary, today, limits);

        return Ok(report with
        {
            AnswerQuality = block,
            Alerts = AssistantQualityAlerts.Evaluate(metrics, limits, block.Capped),
        });
    }

    /// <summary>按条款/会话/结论筛选判定结论（分页）。</summary>
    [HttpGet("verdicts")]
    public async Task<IActionResult> Verdicts(
        [FromQuery] int days = 7,
        [FromQuery] string? clause = null,
        [FromQuery] long? sessionId = null,
        [FromQuery] string? verdict = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken token = default)
    {
        if (!await CanSetupAsync(token)) return Forbid();

        var window = Math.Clamp(days, 1, 90);
        var normalizedClause = NormalizeClause(clause);
        if (normalizedClause is not null && !IsKnownClause(normalizedClause))
        {
            return BadRequest(ApiProblem.BadRequest(
                $"条款编号必须是 {string.Join('/', AssistantJudgeRubric.Current.Clauses.Select(item => item.Code))} 之一。",
                "JUDGE_CLAUSE_INVALID"));
        }

        var normalizedVerdict = NormalizeVerdict(verdict);
        if (normalizedVerdict is not null && !AssistantJudgeVerdicts.IsKnown(normalizedVerdict))
        {
            return BadRequest(ApiProblem.BadRequest(
                "结论只能是 PASS / FAIL / NA / UNSURE 之一。", "JUDGE_VERDICT_INVALID"));
        }

        var result = await verdicts.LoadPageAsync(
            new AssistantVerdictFilter(
                StartOfWindow(window), normalizedClause, sessionId, normalizedVerdict, page, pageSize),
            token);
        return Ok(result);
    }

    /// <summary>
    /// 判官校准读数：「用户维护的样本标签」与「库内判官结论」的一致率与不一致清单。
    ///
    /// <para>
    /// **只读、不调模型、零费用**：样本来自 3106 的 `judge` 层，结论来自 `ASSISTANT_ANSWER_VERDICT`，
    /// 两表按 `messageId` 对齐。它回答的是"这把尺子准不准"，而 3107 那轮跑的是"样本自身是否完整"——
    /// 两件事分开算，谁也不替谁下结论。
    /// </para>
    /// </summary>
    [HttpGet("calibration")]
    public async Task<IActionResult> Calibration(CancellationToken token)
    {
        if (!await CanSetupAsync(token)) return Forbid();

        var samples = await calibration.LoadSamplesAsync(token);
        var messageIds = samples
            .Where(sample => sample.MessageId is not null)
            .Select(sample => sample.MessageId!.Value)
            .Distinct()
            .ToList();
        var verdicts = await calibration.LoadVerdictsAsync(
            messageIds, AssistantJudgeRubric.CurrentVersion, token);

        return Ok(AssistantCalibrationView.Build(samples, verdicts));
    }

    /// <summary>
    /// 手动跑一轮判定。<paramref name="limit"/> 只用于"先小跑几条看看"，
    /// 上限仍由 <c>JUDGE_BATCH_SIZE</c> 约束（手动触发不能绕过批量上限）。
    /// </summary>
    [HttpPost("judge")]
    public async Task<IActionResult> Judge([FromQuery] int? limit = null, CancellationToken token = default)
    {
        if (!await CanSetupAsync(token)) return Forbid();

        var result = await judgeRunner.RunAsync(limit, token);
        return Ok(result);
    }

    private async Task<bool> CanSetupAsync(CancellationToken token)
    {
        var permission = await permissions.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.ModelUsage, token);
        return permission is not null && permission.CanSetup;
    }

    private static DateTimeOffset TodayUtc() =>
        new(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);

    private static DateTimeOffset StartOfWindow(int days) =>
        new(DateTimeOffset.UtcNow.Date.AddDays(-(days - 1)), TimeSpan.Zero);

    private static string? NormalizeClause(string? clause) =>
        string.IsNullOrWhiteSpace(clause) ? null : clause.Trim().ToUpperInvariant();

    private static bool IsKnownClause(string clause) =>
        AssistantJudgeRubric.Current.Clauses.Any(
            item => string.Equals(item.Code, clause, StringComparison.Ordinal));

    private static string? NormalizeVerdict(string? verdict) =>
        string.IsNullOrWhiteSpace(verdict) ? null : verdict.Trim().ToUpperInvariant();
}
