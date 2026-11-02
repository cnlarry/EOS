using System.Globalization;
using EOS.API.Data.Assistant;

namespace EOS.API.Features.Assistant.Evaluation;

/// <summary>一轮运行：抬头 + 逐用例结论 + 按分诊的汇总。</summary>
public sealed record AssistantEvalRunReport(
    AssistantEvalRunDto Run,
    IReadOnlyList<AssistantEvalResultDto> Results)
{
    /// <summary>失败行的分诊分布（按 `FIX_AT` 计数，多的在前）——"下一步该补哪个入口"看的正是这张表。</summary>
    public IReadOnlyList<AssistantEvalFixBucket> FailuresByFixAt() =>
        Results.Where(result => result.Outcome == AssistantEvalRunner.OutcomeFail)
            .GroupBy(result => string.IsNullOrWhiteSpace(result.FixAt) ? "unknown" : result.FixAt!, StringComparer.Ordinal)
            .Select(group => new AssistantEvalFixBucket(group.Key, group.Count()))
            .OrderByDescending(bucket => bucket.Count)
            .ThenBy(bucket => bucket.FixAt, StringComparer.Ordinal)
            .ToList();
}

/// <summary>失败落点的一格计数。</summary>
public sealed record AssistantEvalFixBucket(string FixAt, int Count);

/// <summary>
/// 应用内评估运行器（ADR-046 缺口 #4）：把库内用例（3106）跑一轮**确定性检查**，逐用例落库。
///
/// <para>
/// 它跑的是 <see cref="AssistantEvalCaseValidator"/>——判"用例与当前系统有没有漂移"，
/// **不调用模型、不判回答对错**。这样运维可以随时跑、反复跑、零费用；
/// 真正的回答质量仍要判官（ADR-044），那条路给的是"模型今天行不行"，
/// 这条路给的是"尺子本身还准不准"。两者缺一不可，但**不能混成一张报告**。
/// </para>
///
/// <para>
/// 一轮的抬头先落 `RUNNING`，收尾时才写计数：中途抛错也能看出"这一轮没跑完"，
/// 而不是留下一份看起来像结果的半截数据。
/// </para>
/// </summary>
public sealed class AssistantEvalRunner(IAssistantEvalRepository repository)
{
    public const string OutcomePass = "PASS";
    public const string OutcomeFail = "FAIL";
    public const string OutcomeSkip = "SKIP";

    public const string StatusSucceeded = "SUCCEEDED";
    public const string StatusFailed = "FAILED";

    /// <summary>说明写进抬头 `NOTE`：让"这一轮为什么没跑成"留在库里，而不只在日志里。</summary>
    public const string SucceededNote = "确定性层：只判用例与当前系统是否漂移，不调用模型。";

    /// <summary>
    /// 跑一轮。`layers` 由调用方按 <see cref="AssistantEvalCaseValidator.Layers"/> 校验后传入。
    /// </summary>
    public async Task<AssistantEvalRunReport> RunAsync(
        IReadOnlyList<string> layers, string runBy, CancellationToken token)
    {
        var selected = layers
            .Select(layer => (layer ?? string.Empty).Trim().ToLowerInvariant())
            .Where(layer => AssistantEvalCaseValidator.Layers.Contains(layer, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (selected.Count == 0)
        {
            throw new ArgumentException("至少选择一个有效的评估层。", nameof(layers));
        }

        var runId = await repository.StartRunAsync(string.Join(",", selected), runBy, token);
        try
        {
            var cases = await repository.LoadCasesAsync(selected, token);
            var lookups = await repository.LoadLookupsAsync(token);
            var results = cases
                .Select(item => Evaluate(item, lookups))
                .Select(item => Trim(item))
                .ToList();

            await repository.FinishRunAsync(runId, results, StatusSucceeded, SucceededNote, token);
            await repository.PruneAsync(AssistantEvalRepository.KeepRuns, token);

            var run = await repository.GetRunAsync(runId, token)
                      ?? throw new InvalidOperationException($"运行 {runId} 刚写完却读不到。");
            return new AssistantEvalRunReport(run, results);
        }
        catch (Exception exception)
        {
            // 落一笔"这轮没跑成"：留空比丢一条记录更难查——运维看到 RUNNING 会以为还在跑。
            try
            {
                var note = exception is OperationCanceledException
                    ? "运行被取消。"
                    : $"运行中断：{exception.GetType().Name}";
                await repository.FinishRunAsync(runId, [], StatusFailed, Truncate(note, 500), token);
            }
            catch
            {
                // 收尾失败不再掩盖原始异常。
            }

            throw;
        }
    }

    /// <summary>把一条用例的检查结果折成一行结论（任何一条失败即整条失败）。</summary>
    public static AssistantEvalResultDto Evaluate(AssistantEvalCaseRecord record, AssistantEvalLookups? lookups)
    {
        var checks = AssistantEvalCaseValidator.Validate(record, lookups);
        var failures = checks.Where(check => check.Outcome == AssistantEvalOutcome.Failed).ToList();
        var passed = checks.Count(check => check.Outcome == AssistantEvalOutcome.Passed);
        var skipped = checks.Where(check => check.Outcome == AssistantEvalOutcome.Skipped).ToList();

        var outcome = failures.Count > 0
            ? OutcomeFail
            // 未审阅的校准样本：结构可以完整，但"还没人认可"不能算通过——
            // 跳过不等于通过，这里同样适用（否则一轮跑完会读到"6 条全过"，而其实一条都没校）。
            : skipped.Any(check => check.Code == "JUDGE_NOT_REVIEWED")
                ? OutcomeSkip
                : passed > 0 ? OutcomePass : OutcomeSkip;

        var reason = outcome switch
        {
            OutcomeFail => string.Join("；", failures.Select(failure => $"[{failure.Code}] {failure.Message}")),
            OutcomeSkip => string.Join("；", skipped.Select(check => check.Message)),
            _ => null,
        };

        return new AssistantEvalResultDto(
            record.CaseId,
            record.Layer,
            outcome,
            passed,
            Truncate(reason, 1000),
            record.FixAt,
            record.FixSuggestion,
            record.FixPermission);
    }

    /// <summary>按库内列的宽度截断：宁可少后半句，也不让整轮因为一句长话写不进去。</summary>
    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    private static AssistantEvalResultDto Trim(AssistantEvalResultDto result) => result with
    {
        Reason = Truncate(result.Reason, 1000),
        FixSuggestion = Truncate(result.FixSuggestion, 300),
    };

    /// <summary>给人看的层名（界面与文档共用一处，避免两边各写一套中文名）。</summary>
    public static string LayerLabel(string layer) => layer switch
    {
        "situation" => "处境",
        "diagnosis" => "诊断",
        "action" => "动作",
        "config" => "配置",
        "authz" => "越权",
        "inference" => "推断",
        "functional" => "检索",
        "business" => "回答",
        "judge" => "判官校准",
        _ => layer,
    };

    /// <summary>落点名的中文（分诊十类，见 ADR-046 §3）。</summary>
    public static string FixAtLabel(string fixAt) => fixAt switch
    {
        "case" => "评估用例",
        "param" => "助手设置（参数）",
        "prompt" => "助手设置（提示词/规则）",
        "tool" => "工具描述（代码）",
        "metric" => "指标口径",
        "metadata" => "元数据管理",
        "rule" => "模块动作/校验规则",
        "kb" => "知识库",
        "data" => "业务数据",
        "code" => "开发商（缺陷单）",
        _ => fixAt,
    };

    /// <summary>轮次的人读标签。</summary>
    public static string RunLabel(AssistantEvalRunDto run) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"#{run.RunId} {run.StartedTime:yyyy-MM-dd HH:mm} · {run.RunBy} · 失败 {run.Failed}/{run.CaseTotal}");
}
