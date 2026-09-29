using System.Text;
using System.Text.Json;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Config;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 配置改动的**讲人话**渲染：工具回喂模型的文本与对照卡上的文字同源。
/// 参数级的深度渲染不在这里（那由配置页既有的渲染函数负责），这里只给结论与逐项摘要。
/// </summary>
internal static class ConfigWriteText
{
    public static string RenderPlan(ConfigChangePlan plan)
    {
        if (plan.BlockedCode is not null)
        {
            return $"无法规划这次配置改动（{plan.BlockedCode}）：{plan.BlockedMessage}";
        }
        var text = new StringBuilder($"照「{plan.SourceLabel}」配「{plan.TargetLabel}」：{plan.Items.Count} 项有差异。");
        if (plan.Items.Count == 0)
        {
            text.Append("两边已经一致，没有需要改的地方。");
        }
        foreach (var item in plan.Items)
        {
            text.AppendLine().Append($"- {item.Label}（{item.Target}）");
            foreach (var change in item.Changes)
            {
                text.AppendLine().Append(
                    $"    {change.Label}：{change.OldValue ?? "（无）"} → {change.NewValue ?? "（清空）"}");
            }
            if (item.Impacts.Count > 0)
            {
                text.AppendLine().Append($"    影响面：{string.Join("；", item.Impacts)}");
            }
            if (item.PreviewNote is { Length: > 0 } note)
            {
                // 预演覆盖情况逐项说清：能预演的要说"还需要一张真实单据"，不能预演的要说清为什么。
                text.AppendLine().Append($"    {note}");
            }
        }
        foreach (var note in plan.Notes)
        {
            text.AppendLine().Append($"备注：{note}");
        }
        return text.ToString().TrimEnd();
    }

    public static string RenderDryRun(ConfigDryRunReport report)
    {
        var text = new StringBuilder("预演/自检结果：");
        foreach (var step in report.Steps)
        {
            text.AppendLine().Append(step.Previewable ? "- 已预演 " : "- 未预演 ").Append(step.Label).Append("：")
                .Append(step.Note);
        }
        foreach (var note in report.Notes)
        {
            text.AppendLine().Append($"备注：{note}");
        }
        return text.ToString().TrimEnd();
    }

    public static string RenderApply(ConfigApplyResult result)
    {
        if (result.BlockedCode is not null)
        {
            return $"无法应用这次配置改动（{result.BlockedCode}）：{result.BlockedMessage}";
        }
        var applied = result.Items.Count(item => item.Applied);
        var text = new StringBuilder(
            $"配置改动已应用：成功 {applied} 项、失败 {result.Items.Count - applied} 项（目标：{result.TargetLabel}）。");
        foreach (var item in result.Items)
        {
            text.AppendLine().Append($"- {item.Target}：")
                .Append(item.Applied ? "已写入。" : $"未写入（{item.Code}）{item.Message}");
        }
        foreach (var note in result.Notes)
        {
            text.AppendLine().Append($"备注：{note}");
        }
        return text.ToString().TrimEnd();
    }
}

/// <summary>配置写工具的公共部分：参数契约与"讲人话"渲染都取自同一份实现。</summary>
public abstract class ConfigWriteToolBase : AssistantToolBase
{
    public override string ParametersJson => AssistantConfigArguments.ParametersJson;

    protected static string Serialize(object payload) => JsonSerializer.Serialize(payload, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// clone_module_config：照 A 配 B —— 算出逐项对照（旧值/新值/影响面）并成卡。
/// 规划是纯读，不落库；卡上每一项都标明这类改动能不能预演。
/// </summary>
public sealed class CloneModuleConfigTool(ConfigWriteService writes) : ConfigWriteToolBase
{
    public const string ToolName = "clone_module_config";

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Draft;

    public override string Description =>
        "照 A 模块（或 A 表）把 B 模块（或 B 表）的同类配置配好：字段、数据来源、自定义按钮、效果键四类。"
        + "只覆盖点名的对象集合，不整表重排；产出逐项旧值/新值/影响面对照卡，逐项可勾选后再应用。"
        + "权限授予类配置（谁能批什么单、谁能用哪个按钮）不在助手能力面内，永不代为执行。";

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var request = AssistantConfigArguments.TryParse(arguments, out var error);
        if (request is null)
        {
            return ToolExecutionResult.Deny(error);
        }
        var plan = await writes.PlanAsync(userId, request, token);
        var draft = ConfigWriteDtos.ToDiff(plan, request, dryRun: null);
        // 规划被拒（未开放 / 无权 / 参数不足）时如实返回，不假装成功。
        return new ToolExecutionResult(
            plan.BlockedCode is null, ConfigWriteText.RenderPlan(plan) + "\n" + Serialize(draft), draft);
    }
}

/// <summary>
/// preview_config_change：对一批配置改动做预演/自检。
/// 能预演的走真实预演（同一事务内跑完回滚），不能预演的逐项标注原因——覆盖率 100%，不含糊。
/// </summary>
public sealed class PreviewConfigChangeTool(ConfigWriteService writes) : ConfigWriteToolBase
{
    public const string ToolName = "preview_config_change";

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Draft;

    public override string Description =>
        "对一批配置改动做预演 / 自检：能预演的（批核生效 / 解批的效果链）在真实单据上跑一遍后回滚，"
        + "报告会发生什么；不能预演的（保存阶段的效果链、字段与数据来源的元数据改动）**逐项显式标注原因**，"
        + "不得当成已校验。预演不落库。";

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var request = AssistantConfigArguments.TryParse(arguments, out var error);
        if (request is null)
        {
            return ToolExecutionResult.Deny(error);
        }
        var plan = await writes.PlanAsync(userId, request, token);
        if (plan.BlockedCode is not null)
        {
            return new ToolExecutionResult(false, ConfigWriteText.RenderPlan(plan));
        }
        var items = AssistantConfigArguments.ReadItems(arguments);
        var dryRun = await writes.DryRunAsync(plan, request, items, userId, token);
        var draft = ConfigWriteDtos.ToDiff(plan, request, dryRun);
        var text = ConfigWriteText.RenderPlan(plan) + "\n" + ConfigWriteText.RenderDryRun(dryRun) + "\n"
            + Serialize(draft);
        return new ToolExecutionResult(true, text, draft);
    }
}

/// <summary>
/// apply_config_change：应用用户勾选的配置改动项。
/// 每次执行按当前用户独立重新授权，幂等键由服务端从工具调用身份推导（不在参数 schema 里）。
/// </summary>
public sealed class ApplyConfigChangeTool(
    ConfigWriteService writes,
    CurrentUserContext userContext) : ConfigWriteToolBase, IToolCallContextTool
{
    public const string ToolName = "apply_config_change";

    private long _conversationId;
    private string? _toolCallId;

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Write;

    public override string Description =>
        "应用被勾选的配置改动项（字段 / 数据来源 / 自定义按钮 / 效果键）。"
        + "服务端按当前用户重新授权，写入走各配置面既有的写端点与校验，全程进审计；"
        + "权限授予类配置不在能力面内，无法应用。";

    /// <summary>服务端注入本次工具调用身份，用于推导幂等键；它不构成权限依据。</summary>
    public void UseToolCallContext(long conversationId, string toolCallId)
    {
        _conversationId = conversationId;
        _toolCallId = toolCallId;
    }

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        if (string.IsNullOrEmpty(_toolCallId))
        {
            // 没有工具调用身份就算不出服务端幂等键：宁可拒绝，也不接受一个无法防重的写入。
            return ToolExecutionResult.Deny("缺少本次工具调用的服务端身份，无法生成幂等键，已拒绝执行。");
        }
        var request = AssistantConfigArguments.TryParse(arguments, out var error);
        if (request is null)
        {
            return ToolExecutionResult.Deny(error);
        }
        var items = AssistantConfigArguments.ReadItems(arguments);
        if (items is not { Count: > 0 })
        {
            return ToolExecutionResult.Deny("参数 items 不能为空：请给出要应用的改动项 id（来自对照卡）。");
        }

        var result = await writes.ApplyAsync(
            userId, userContext.EmployeeName, request, items,
            AssistantActionKeySeed.FromToolCall(_conversationId, _toolCallId!), token);
        var draft = ConfigWriteDtos.ToApply(result);
        return new ToolExecutionResult(
            result.Items.Any(item => item.Applied) || result.BlockedCode is not null,
            ConfigWriteText.RenderApply(result) + "\n" + Serialize(draft), draft);
    }
}
