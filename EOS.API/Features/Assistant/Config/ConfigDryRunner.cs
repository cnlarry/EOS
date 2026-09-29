using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Models;

namespace EOS.API.Features.Assistant.Config;

/// <summary>
/// 配置改动的**预演与自检**：能预演的调真实预演（同一事务内跑完整条链路后无条件回滚），
/// 不能预演的**逐项显式标注原因**。
///
/// <para>
/// 覆盖缺口必须被说出来，这是本类的核心约束：预演只覆盖批核生效与解批，保存阶段的效果链、
/// 字段与数据来源的元数据改动都没有可跑的链路。若把它们静默当成"已校验"，用户会以为
/// "点过预演就没事了"——那比不预演更危险。
/// </para>
///
/// <para>
/// 预演**不新增第二套实现**：它调用配置面已交付的预演服务，报告形状与预演端点完全一致。
/// </para>
/// </summary>
public sealed class ConfigDryRunner(
    ModuleBusinessConfigRepository moduleConfig,
    WorkbenchDefinitionProvider definitions,
    EffectSimulationService simulation,
    ILogger<ConfigDryRunner> logger)
{
    /// <summary>预演只认这两个事件（与预演端点的闭集同源）。</summary>
    private static readonly HashSet<string> SimulatableEvents =
        new(["APPROVE_EFFECT", "DEAPPROVE"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 对一批改动项做预演/自检。<paramref name="itemIds"/> 为空表示全部项，只对被选中的项跑真实预演。
    /// </summary>
    public async Task<ConfigDryRunReport> DryRunAsync(
        ConfigChangePlan plan,
        ConfigCloneRequest request,
        IReadOnlyList<string>? itemIds,
        string userId,
        CancellationToken token)
    {
        var selected = Select(plan, itemIds);
        var steps = new List<ConfigDryRunStep>(selected.Count);
        var simulations = new List<EffectSimulationOutcome>();
        var notes = new List<string>();

        WorkbenchDefinition? definition = null;
        string? definitionFailure = null;
        SaveModuleBusinessConfigRequest? draft = null;
        var recordKey = request.RecordKey;
        foreach (var item in selected)
        {
            var eventCode = (item.Payload as ModuleActionPayload)?.Source.EventCode?.Trim().ToUpperInvariant();
            if (eventCode is null || !SimulatableEvents.Contains(eventCode))
            {
                // 不可预演：原因来自规划（保存阶段缺口 / 字段与数据来源不在覆盖内），原样带出。
                steps.Add(new ConfigDryRunStep(item.Id, item.Label, false,
                    item.PreviewNote ?? "无法预演：这类改动不在预演覆盖内。"));
                continue;
            }
            if (recordKey is not { Count: > 0 })
            {
                steps.Add(new ConfigDryRunStep(item.Id, item.Label, false,
                    "无法预演：需要一张真实单据的主键才能跑预演，本次没有提供。"));
                continue;
            }

            if (definition is null && definitionFailure is null)
            {
                (definition, definitionFailure) = ResolveDefinition(request.TargetModuleId, recordKey);
            }
            if (definitionFailure is not null)
            {
                steps.Add(new ConfigDryRunStep(item.Id, item.Label, false, definitionFailure));
                continue;
            }

            draft ??= await BuildDraftAsync(request.TargetModuleId, selected, token);
            if (draft is null)
            {
                steps.Add(new ConfigDryRunStep(item.Id, item.Label, false,
                    "无法预演：读不到目标模块的既有配置，拿不到可预演的配置草稿。"));
                continue;
            }

            // 草稿预演：只替换配置段（动作链 + 校验规则），其余（字段、表、主键、引擎开关）仍取已发布快照。
            var simDefinition = definition! with
            {
                BusinessActions = JsonSerializer.SerializeToElement(draft.Actions),
                ValidationRules = JsonSerializer.SerializeToElement(draft.ValidationRules),
            };
            try
            {
                var report = await simulation.SimulateAsync(
                    simDefinition, eventCode, recordKey,
                    approve: string.Equals(eventCode, "APPROVE_EFFECT", StringComparison.Ordinal),
                    userId, token, configSource: "draft");
                var summary = Summarize(report);
                steps.Add(new ConfigDryRunStep(item.Id, item.Label, true, summary));
                simulations.Add(new EffectSimulationOutcome(item.Id, item.Label, true, summary, report));
            }
            catch (EffectSimulationService.UnsupportedModuleException unsupported)
            {
                steps.Add(new ConfigDryRunStep(item.Id, item.Label, false,
                    $"无法预演：这条路径本就不执行效果链（{unsupported.Code}）。{unsupported.Message}"));
            }
            catch (EffectSimulationService.TimeoutException)
            {
                steps.Add(new ConfigDryRunStep(item.Id, item.Label, false,
                    "无法预演：预演超时（事务已回滚，库内无变化），请缩小单据范围后重试。"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "配置改动预演失败 surface={Surface} item={Item}", plan.Surface, item.Id);
                steps.Add(new ConfigDryRunStep(item.Id, item.Label, false, $"无法预演：{exception.Message}"));
            }
        }

        var notPreviewable = steps.Count(step => !step.Previewable);
        notes.Add(notPreviewable == 0
            ? $"本次 {steps.Count} 项改动全部完成预演（预演在同一事务内跑完后无条件回滚，不落库）。"
            : $"本次 {steps.Count} 项改动中有 {notPreviewable} 项**无法预演**，已逐项标注原因"
                + "（不可预演的改动没有被预演过，不能当成已校验）。");
        if (simulations.Any(item => item.Report is { RolledBack: false }))
        {
            notes.Add("⚠ 有一次预演未能回滚，请立即联系维护方核对库内数据。");
        }

        return new ConfigDryRunReport(steps, simulations, notes, steps.Count - notPreviewable, notPreviewable);
    }

    /// <summary>定位预演用的已发布定义：没有发布快照或主键个数不符时给出可读原因（不猜）。</summary>
    private (WorkbenchDefinition? Definition, string? Failure) ResolveDefinition(
        int? moduleId, IReadOnlyList<string> recordKey)
    {
        if (moduleId is not int id)
        {
            return (null, "无法预演：效果面的改动需要目标模块 ID 才能定位已发布定义。");
        }
        if (!definitions.TryGetBaseline(id, out var baseline, out var version))
        {
            return (null, "无法预演：该模块还没有已发布的定义快照，请先发布后再预演。");
        }
        if (baseline.MasterPkOrder.Count == 0)
        {
            return (null, "无法预演：该模块没有主表，无法定位单据。");
        }
        if (recordKey.Count != baseline.MasterPkOrder.Count)
        {
            return (null, $"无法预演：单据主键个数与模块主键不一致（需要 {baseline.MasterPkOrder.Count} 个）。");
        }
        return (baseline with { DefinitionVersion = version }, null);
    }

    /// <summary>
    /// 预演用的配置草稿：目标模块的既有配置 + 本次改动项的覆盖。
    /// 它只用于预演（同一个事务内跑完回滚），不落库——真正的写入走保存端点。
    /// </summary>
    private async Task<SaveModuleBusinessConfigRequest?> BuildDraftAsync(
        int? moduleId, IReadOnlyList<ConfigChangeItem> selected, CancellationToken token)
    {
        if (moduleId is not int id)
        {
            return null;
        }
        var current = await moduleConfig.GetAsync(id, token);
        if (current is null)
        {
            return null;
        }
        var actions = current.Actions.OrderBy(action => action.Seq).ToList();
        var nextSeq = actions.Count == 0 ? 1 : actions.Max(action => action.Seq) + 1;
        foreach (var payload in selected.Select(item => item.Payload).OfType<ModuleActionPayload>())
        {
            if (payload.TargetSeq is int seq && actions.FindIndex(action => action.Seq == seq) is var index and >= 0)
            {
                actions[index] = payload.Source with { Seq = actions[index].Seq };
                continue;
            }
            actions.Add(payload.Source with { Seq = nextSeq++ });
        }
        return new SaveModuleBusinessConfigRequest([.. actions], [.. current.ValidationRules]);
    }

    private static IReadOnlyList<ConfigChangeItem> Select(ConfigChangePlan plan, IReadOnlyList<string>? itemIds)
        => itemIds is not { Count: > 0 }
            ? plan.Items
            : [.. plan.Items.Where(item => itemIds.Contains(item.Id, StringComparer.Ordinal))];

    /// <summary>把预演报告讲成人话：先给结论（三道闸与计数），再逐步骤给一行。</summary>
    private static string Summarize(EffectSimulationReportDto report)
    {
        var summary = new StringBuilder();
        summary.Append(report.Precondition.Passed
            ? "预演：前置守卫通过"
            : $"预演：前置守卫未通过（{report.Precondition.Code}）{report.Precondition.Message}");
        if (report.Precondition.Passed)
        {
            summary.Append(report.Validation.Passed
                ? "，校验闸通过"
                : $"，校验闸拦下：{report.Validation.Message}");
        }
        summary.Append($"；共 {report.Counts.Total} 步"
            + $"（执行 {report.Counts.Ran}、跳过 {report.Counts.Skipped}、失败 {report.Counts.Failed}）");
        summary.Append(report.RolledBack ? "；已回滚，库内无变化。" : "；**未能回滚**，请立即联系维护方。");
        foreach (var step in report.Effects)
        {
            summary.AppendLine()
                .Append($"- 第 {step.Seq} 步 {step.EffectName ?? step.EffectKey}（{OutcomeLabel(step.Outcome)}）："
                    + $"影响 {step.RowsAffected} 行")
                .Append(step.SkipReason is null ? string.Empty : $"，跳过原因：{step.SkipReason}")
                .Append(step.Message is null ? string.Empty : $"，失败：{step.Message}");
        }
        return summary.ToString().TrimEnd();
    }

    private static string OutcomeLabel(string outcome) => outcome switch
    {
        "ran" => "已执行",
        "skipped" => "已跳过",
        "failed" => "失败",
        _ => outcome,
    };
}
