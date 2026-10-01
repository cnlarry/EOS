using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Situation;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.Diagnosis;

/// <summary>
/// 对象级诊断：围绕**一条记录**把证据按价值顺序串成因果链，并给出可核对的结论。
///
/// <para>
/// 四条硬约束：**只读**（不产生任何写路径）；**证据不足就说证据不足**（列出已收集与缺失的证据，
/// 不给猜测性根因）；**不下发原文**（表达式与审计明细原文一律不进输出）；**权限问题必须明说**
/// （"你没有批核权限"不是泄露——它不涉及别人数据的存在性，见防探测边界的判定问句）。
/// </para>
/// <para>
/// 证据段的**输出顺序就是优先级**，段内的规则顺序按阶段（SAVE 为绝对主力）与序号稳定排列。
/// </para>
/// </summary>
public sealed class RecordDiagnosisService(
    IRecordDiagnosisReader reader,
    AssistantSituationBudget budget,
    IOptions<AssistantDiagnosisOptions> options,
    ILogger<RecordDiagnosisService> logger)
{
    /// <summary>阶段展示顺序：保存阶段占实测规则数的 89%，放最前。</summary>
    private static readonly IReadOnlyList<string> StageOrder = ["SAVE", "APPROVE", "DEAPPROVE", "DELETE"];

    // 归因文案里逐条列出的动作权限项上限（其余指向 blockers 段）：
    // 取值来自 3105 的参数 DIAG_ACTION_SUMMARY_LIMIT（它原先写死在这里，一个配置入口都没有）

    public async Task<RecordDiagnosisOutcome> DiagnoseAsync(
        string userId, DiagnosisContext context, CancellationToken token)
    {
        var facts = await reader.LoadAsync(userId, context, token);
        if (facts is null)
        {
            logger.LogInformation("助手诊断未命中记录 module={ModuleId}", context.Definition.ModuleId);
            return RecordDiagnosisOutcome.NotFound;
        }

        var limits = options.Value;
        var truncated = new List<string>();
        var conflicts = new List<string>();

        var ordered = OrderRules(facts.Rules);
        var hits = ResolveHits(ordered, facts.Signals, conflicts);
        var guards = OrderGuards(facts.Fields, context.AttemptedFields);
        var blockers = DiagnosisActionEvaluator.Evaluate(new DiagnosisActionFacts(
            context.Permission,
            userId,
            facts.Confirmed,
            facts.Finished,
            facts.Flow is not null,
            facts.Flow?.State,
            facts.Flow is not null && string.Equals(facts.Flow.StartUser, userId, StringComparison.OrdinalIgnoreCase),
            facts.Flow is not null && facts.Flow.PendingApprovers.Contains(userId, StringComparer.OrdinalIgnoreCase)));

        var missing = BuildMissing(facts, ordered, conflicts);
        var verdict = BuildVerdict(
            ordered, hits, guards, blockers, context.AttemptedFields, conflicts, missing,
            options.Value.MaxActionSummary);

        var document = new RecordDiagnosisDocument(
            new DiagnosisTarget(
                context.Definition.ModuleId, context.Definition.Title, facts.RecordKey, facts.RecordLabel),
            CapValidation(ordered, hits, limits, budget, truncated),
            Cap(guards.Select(ToEntry).ToList(), limits.MaxFieldGuards, DiagnosisSegments.FieldGuard, truncated),
            Cap(blockers.Select(blocker => new DiagnosisBlockerEntry(blocker.Code, blocker.Message, blocker.Source)).ToList(),
                limits.MaxBlockers, DiagnosisSegments.Blockers, truncated),
            Cap(facts.Provenance.Select(fact => new DiagnosisProvenanceEntry(
                    fact.Field, fact.Label, fact.Origin, fact.SourceRef)).ToList(),
                limits.MaxProvenance, DiagnosisSegments.Provenance, truncated),
            Cap(facts.Effects.Select(fact => new DiagnosisEffectEntry(
                    fact.EffectKey, fact.TargetTable, fact.TargetField, fact.OpCode)).ToList(),
                limits.MaxEffects, DiagnosisSegments.Effects, truncated),
            facts.Flow is null
                ? null
                : new DiagnosisFlowEntry(facts.Flow.State, facts.Flow.CurrentStep, facts.Flow.PendingApprovers),
            facts.LastFailure is null
                ? null
                : new DiagnosisFailureEntry(
                    facts.LastFailure.OccurredAt, facts.LastFailure.ErrorCode,
                    facts.LastFailure.CorrelationId, facts.LastFailure.Summary),
            verdict,
            new DiagnosisEvidenceEntry(BuildCollected(facts, ordered, hits, guards, blockers), missing),
            BuildCaveat(facts, verdict, truncated, conflicts));

        return RecordDiagnosisOutcome.Of(document);
    }

    /// <summary>规则按阶段（SAVE 优先）与序号排列；同键同阶段时按配置顺序，保证输出稳定。</summary>
    private static IReadOnlyList<(DiagnosisRule Rule, int Index)> OrderRules(IReadOnlyList<DiagnosisRule> rules) =>
        [.. rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .OrderBy(item => StageRank(item.Rule.Stage))
            .ThenBy(item => item.Rule.Seq)
            .ThenBy(item => item.Index)];

    private static int StageRank(string stage)
    {
        for (var index = 0; index < StageOrder.Count; index++)
        {
            if (StageOrder[index].Equals(stage, StringComparison.OrdinalIgnoreCase)) return index;
        }

        return StageOrder.Count;
    }

    /// <summary>
    /// 把"判据命中"事实折回登记规则：**只认身份（阶段 / 键 / 序号）**，命中不了就如实记为证据冲突，
    /// 绝不拿"看起来像"的规则顶替（那正是错误归因的来源）。
    /// </summary>
    private static Dictionary<int, string?> ResolveHits(
        IReadOnlyList<(DiagnosisRule Rule, int Index)> ordered,
        IReadOnlyList<DiagnosisRuleSignal> signals,
        List<string> conflicts)
    {
        var hits = new Dictionary<int, string?>();
        foreach (var signal in signals)
        {
            var match = ordered.FirstOrDefault(item => Matches(item.Rule, signal.Stage, signal.ValidationKey, signal.Seq));
            if (match.Rule is null)
            {
                match = ordered.FirstOrDefault(item => Matches(item.Rule, signal.Stage, signal.ValidationKey, null));
            }

            if (match.Rule is null)
            {
                conflicts.Add($"判据命中 {signal.Stage}/{signal.ValidationKey}#{signal.Seq} 在已发布定义里没有对应规则（配置可能已变更）");
                continue;
            }

            hits[match.Index] = signal.Detail;
        }

        return hits;
    }

    private static bool Matches(DiagnosisRule rule, string stage, string key, int? seq) =>
        rule.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase)
        && rule.ValidationKey.Equals(key, StringComparison.OrdinalIgnoreCase)
        && (seq is null || rule.Seq == seq);

    /// <summary>尝试改动/被问到的字段排在前面，其次是引擎维护列，最后是其余只读保护。</summary>
    private static IReadOnlyList<DiagnosisFieldFact> OrderGuards(
        IReadOnlyList<DiagnosisFieldFact> fields, IReadOnlyList<string> attemptedFields)
    {
        var attempted = new HashSet<string>(attemptedFields, StringComparer.OrdinalIgnoreCase);
        return [.. fields
            .Where(field => field.BlocksWrite)
            .OrderBy(field => attempted.Contains(field.Field) ? 0 : DiagnosisFieldGuardText.IsEngineMaintained(field.Field) ? 1 : 2)];
    }

    private static DiagnosisFieldGuardEntry ToEntry(DiagnosisFieldFact fact) =>
        new(fact.Field, fact.Label, fact.Reason, fact.Source);

    /// <summary>
    /// 校验段：命中判据的规则一定保留（否则结论会引用一条看不见的规则），
    /// 其余按阶段顺序补足上限，最后仍按阶段顺序输出。
    /// </summary>
    private static IReadOnlyList<DiagnosisValidationEntry>? CapValidation(
        IReadOnlyList<(DiagnosisRule Rule, int Index)> ordered,
        IReadOnlyDictionary<int, string?> hits,
        AssistantDiagnosisOptions limits,
        AssistantSituationBudget budget,
        List<string> truncated)
    {
        if (ordered.Count == 0) return null;
        var entries = ordered.Select(item => new DiagnosisValidationEntry(
            item.Rule.ValidationKey,
            item.Rule.Stage,
            item.Rule.Seq,
            item.Rule.Enabled,
            budget.Clip(item.Rule.Message, limits.MaxTextLength),
            budget.Clip(item.Rule.Trigger, limits.MaxTextLength),
            hits.ContainsKey(item.Index),
            budget.Clip(hits.GetValueOrDefault(item.Index), limits.MaxTextLength))).ToList();

        if (entries.Count <= limits.MaxValidationRules) return entries;

        truncated.Add($"validation 仅列出 {limits.MaxValidationRules} 条（共 {entries.Count} 条，命中判据优先保留）");
        var kept = Enumerable.Range(0, entries.Count)
            .Where(index => entries[index].Hit)
            .Concat(Enumerable.Range(0, entries.Count).Where(index => !entries[index].Hit))
            .Take(limits.MaxValidationRules)
            .ToHashSet();
        return [.. entries.Where((_, index) => kept.Contains(index))];
    }

    private static IReadOnlyList<T>? Cap<T>(
        IReadOnlyList<T> items, int limit, string segment, List<string> truncated)
    {
        if (items.Count == 0) return null;
        if (items.Count <= limit) return items;
        truncated.Add($"{segment} 仅列出前 {limit} 条（共 {items.Count} 条）");
        return [.. items.Take(limit)];
    }

    private static DiagnosisVerdictEntry BuildVerdict(
        IReadOnlyList<(DiagnosisRule Rule, int Index)> ordered,
        IReadOnlyDictionary<int, string?> hits,
        IReadOnlyList<DiagnosisFieldFact> guards,
        IReadOnlyList<DiagnosisBlockerFact> blockers,
        IReadOnlyList<string> attemptedFields,
        IReadOnlyList<string> conflicts,
        IReadOnlyList<string> missing,
        int actionSummaryLimit)
    {
        // 状态类阻塞最根本：它对该记录的任何改动都成立。
        var state = blockers.FirstOrDefault(blocker => DiagnosisActionEvaluator.StateCodes.Contains(blocker.Code));
        if (state is not null)
        {
            return new DiagnosisVerdictEntry(
                DiagnosisClasses.Blockers, state.Message, null, null, null, null, NextStepFor(state.Code));
        }

        // 字段保护：先答"你正在改的字段"，其次是引擎维护列（服务端也不认人工改）。
        var attempted = new HashSet<string>(attemptedFields, StringComparer.OrdinalIgnoreCase);
        var guard = guards.FirstOrDefault(field => attempted.Contains(field.Field))
            ?? guards.FirstOrDefault(field => DiagnosisFieldGuardText.IsEngineMaintained(field.Field));
        if (guard is not null)
        {
            return new DiagnosisVerdictEntry(
                DiagnosisClasses.FieldGuard, guard.Reason, null, null, null, guard.Field,
                "该字段由系统维护：请走对应的业务动作（库存移动 / 月结 / 批核效果链等），不要人工改。");
        }

        foreach (var (rule, index) in ordered)
        {
            if (!rule.Enabled || !hits.TryGetValue(index, out var detail)) continue;
            return new DiagnosisVerdictEntry(
                DiagnosisClasses.Validation,
                string.IsNullOrWhiteSpace(rule.Message) ? $"校验规则 {rule.ValidationKey} 未通过。" : rule.Message,
                rule.ValidationKey, rule.Stage, rule.Seq, detail,
                "按该规则的要求补齐单据数据后再保存。");
        }

        // 判据命中事实与登记规则对不上（配置可能已变更）：有阻塞事实却说不出名字时**不猜**。
        if (conflicts.Count > 0)
        {
            return new DiagnosisVerdictEntry(
                DiagnosisClasses.Unknown, DiagnosisMessages.InsufficientEvidence, null, null, null, null,
                string.Join("；", conflicts));
        }

        // 动作权限类阻塞：动作相关，只在没有更具体原因时作为结论说出——但说出就必须明说（逐条列出动作与原因）。
        var action = blockers
            .Where(blocker => !DiagnosisActionEvaluator.StateCodes.Contains(blocker.Code))
            .ToList();
        if (action.Count > 0)
        {
            var listed = action.Take(actionSummaryLimit).ToList();
            var summary = string.Join("；", listed.Select(blocker => $"{blocker.Action}：{blocker.Message}"));
            var rest = action.Count > listed.Count ? $"（另有 {action.Count - listed.Count} 项不可执行，见 blockers 段）" : string.Empty;
            return new DiagnosisVerdictEntry(
                DiagnosisClasses.Blockers,
                $"没有查到这张单本身的校验或状态阻塞。按你当前的权限：{summary}{rest}",
                null, null, null, null,
                "若要办的是某个具体动作，请说明是哪一个；逐条原因见 blockers 段。");
        }

        return new DiagnosisVerdictEntry(
            DiagnosisClasses.Unknown,
            DiagnosisMessages.InsufficientEvidence,
            null, null, null, null,
            missing.Count == 0
                ? DiagnosisMessages.InsufficientNextStep
                : $"缺少证据：{string.Join("；", missing)}");
    }

    private static string NextStepFor(string code) => code switch
    {
        LifecycleEditGuards.FinishedCode => "先取消结案，再修改。",
        LifecycleEditGuards.ConfirmedCode => "先解批，再修改。",
        LifecycleEditGuards.FlowInProgressCode => "先撤回流程，再修改。",
        "NOT_CURRENT_APPROVER" => "请当前待办人办理，或等流程流转到你这步。",
        "NOT_FLOW_STARTER" => "撤回只能由流程发起人操作。",
        _ => "请让有该动作权限的人操作，或申请对应权限。",
    };

    private static List<string> BuildMissing(
        RecordDiagnosisFacts facts,
        IReadOnlyList<(DiagnosisRule Rule, int Index)> ordered,
        List<string> conflicts)
    {
        var missing = new List<string>(facts.Missing);
        if (ordered.Any(item => item.Rule.Enabled) && facts.Signals.Count == 0)
        {
            missing.Add("校验判据的命中事实（除可只读判定的类别外，其余只能在保存时由引擎判定）");
        }

        missing.AddRange(conflicts.Select(conflict => $"证据冲突：{conflict}"));
        return missing;
    }

    private static IReadOnlyList<string> BuildCollected(
        RecordDiagnosisFacts facts,
        IReadOnlyList<(DiagnosisRule Rule, int Index)> ordered,
        IReadOnlyDictionary<int, string?> hits,
        IReadOnlyList<DiagnosisFieldFact> guards,
        IReadOnlyList<DiagnosisBlockerFact> blockers)
    {
        var collected = new List<string>
        {
            $"validation:模块登记 {ordered.Count} 条校验规则（阶段 SAVE/APPROVE/DEAPPROVE/DELETE）",
            $"validation:本次取到 {hits.Count} 条判据命中事实",
            $"fieldGuard:{guards.Count} 个字段不可写",
            $"blockers:{blockers.Count} 条（状态类 {(blockers.Count(blocker => DiagnosisActionEvaluator.StateCodes.Contains(blocker.Code)))} 条）",
            $"provenance:{facts.Provenance.Count} 个字段有登记来源",
            $"effects:{facts.Effects.Count} 条效果行",
            facts.Flow is null ? "flow:模块未配置审批流程（按需段不展开）" : "flow:已取到流程实例状态",
            facts.LastFailure is null ? "lastFailure:未取到该记录的失败审计" : "lastFailure:已取到该记录最近一次失败",
        };
        return collected;
    }

    private static string? BuildCaveat(
        RecordDiagnosisFacts facts,
        DiagnosisVerdictEntry verdict,
        List<string> truncated,
        List<string> conflicts)
    {
        var notes = new List<string>();
        if (verdict.CauseClass == DiagnosisClasses.Unknown)
        {
            notes.Add($"{DiagnosisMessages.InsufficientEvidence}——结论只基于上列证据，不猜测根因。");
        }

        if (facts.Signals.Count == 0)
        {
            notes.Add("未取到校验判据命中事实：上列规则只说明该模块登记了什么，不代表这张单此刻一定被拦下。");
        }

        if (truncated.Count > 0) notes.Add(string.Join("；", truncated) + "。");
        if (conflicts.Count > 0) notes.Add(string.Join("；", conflicts) + "。");
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }
}
