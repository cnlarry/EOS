using System.Runtime.CompilerServices;
using System.Text.Json;
using Agent.Harness.Abstractions;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using Microsoft.Extensions.Logging;
using EosChatModel = EOS.API.Features.Assistant.ModelAccess.IChatModel;
using EosCompletedCall = EOS.API.Features.Assistant.ModelAccess.CompletedToolCall;
using EosDelta = EOS.API.Features.Assistant.ModelAccess.ChatDelta;
using EosFragment = EOS.API.Features.Assistant.ModelAccess.ProposedToolCallFragment;
using EosMessage = EOS.API.Features.Assistant.ModelAccess.ChatMessage;
using EosRole = EOS.API.Features.Assistant.ModelAccess.ChatRole;
using EosToolDefinition = EOS.API.Features.Assistant.ModelAccess.ToolDefinition;
using EosUsage = EOS.API.Features.Assistant.ModelAccess.ChatUsage;
using HarnessChatModel = Agent.Harness.Abstractions.IChatModel;
using HarnessDelta = Agent.Harness.Abstractions.ChatDelta;
using HarnessFragment = Agent.Harness.Abstractions.ProposedToolCallFragment;
using HarnessMessage = Agent.Harness.Abstractions.ChatMessage;
using HarnessToolDefinition = Agent.Harness.Abstractions.ToolDefinition;
using HarnessUsage = Agent.Harness.Abstractions.ChatUsage;

namespace EOS.API.Features.Assistant.Engines;

/// <summary>
/// 把 EOS 的受控工具包成 Harness 的 <see cref="ITool"/>。
///
/// <para>
/// **授权不在这里**：EOS 的工具实现内部按 <c>userId</c> 重新授权（EXEC_TAG / DATA_FILTER / 字段隐藏
/// 全在工具内部生效），这正是"<c>IToolPolicy</c> 只是执行前门禁、不是权限依据"的落地方式。
/// </para>
///
/// <para>
/// 但 EOS 工具依赖的两个**服务端注入**必须在这里补齐：页面处境（当前单据/未保存字段）与
/// 本次调用身份（会话 + <c>CallId</c>，写入类动作据此推导幂等键）。模型既看不到也无从伪造它们。
/// </para>
/// </summary>
internal sealed class EosToolAdapter(
    IAssistantTool tool,
    string userId,
    long sessionId,
    PageContext? pageContext,
    int digestLength,
    ILogger logger) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: tool.Name,
        Description: tool.Description,
        ParametersJson: tool.ParametersJson,
        Risk: ToRisk(tool.Risk),
        // 只读与可并行都由风险分级推导：读工具之间没有依赖，写类必须按模型给的顺序来。
        ReadOnly: tool.Risk == AssistantToolRisk.Read,
        AllowParallel: tool.Risk == AssistantToolRisk.Read);

    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 模型给出的参数不是合法 JSON：工具一行都不执行，回喂一句可执行的改正提示。
            // 摘要用稳定标记，事后能按它检索"模型产出了坏参数"这一类问题。
            if (invocation.Arguments.ValueKind == JsonValueKind.Object
                && invocation.Arguments.TryGetProperty(
                    Agent.Harness.Maf.ChatModelChatClient.InvalidArgumentsKey, out _))
            {
                logger.LogWarning(
                    "助手工具参数非法 session={SessionId} tool={Tool} args={Arguments}",
                    sessionId, tool.Name, Truncate(invocation.Arguments.GetRawText(), 200));
                return ToolResult.Deny("工具参数格式错误，请修正后重试。", "error:invalid_arguments");
            }

            if (tool is IPageContextTool aware && pageContext is not null && !pageContext.IsEmpty)
            {
                aware.UsePageContext(pageContext);
            }

            if (tool is IToolCallContextTool callAware)
            {
                // MAF 的 CallId 是幂等键的唯一服务端来源。为空只可能是引擎接线故障，
                // 此时用一次性标识兜底：宁可这次写入不能重放，也不能让两次调用共用同一个键。
                callAware.UseToolCallContext(
                    sessionId,
                    string.IsNullOrEmpty(invocation.CallId) ? Guid.NewGuid().ToString("N") : invocation.CallId);
            }

            var result = await tool
                .ExecuteAsync(userId, invocation.Arguments, cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "助手工具执行 session={SessionId} tool={Tool} ok={Ok}", sessionId, tool.Name, result.Ok);

            return new ToolResult(
                result.Ok, result.ContentForModel, result.Draft, Truncate(result.ContentForModel, digestLength));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "助手工具参数非法 session={SessionId} tool={Tool}", sessionId, tool.Name);
            return ToolResult.Deny("工具参数格式错误，请修正后重试。", "error:invalid_arguments");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "助手工具异常 session={SessionId} tool={Tool}", sessionId, tool.Name);
            return ToolResult.Deny("工具执行出现内部错误，请换一种问法或稍后重试。", "error:exception");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static ToolRisk ToRisk(AssistantToolRisk risk) => risk switch
    {
        AssistantToolRisk.Read => ToolRisk.Read,
        AssistantToolRisk.Draft => ToolRisk.Draft,
        AssistantToolRisk.Write => ToolRisk.Write,
        _ => ToolRisk.AdminWrite,
    };
}

/// <summary>
/// 把 EOS 的 <see cref="EosChatModel"/> 包成 Harness 的同名契约。
///
/// <para>
/// 两个契约**逐字段相同**（Harness 的契约就是从 EOS 抄的），所以这层只是类型转换，不含任何行为——
/// 一旦它开始做别的事（重试、改写、补提示词），就说明有人把引擎语义塞进了适配层。
/// </para>
/// </summary>
internal sealed class EosChatModelAdapter(EosChatModel inner) : HarnessChatModel
{
    /// <summary>
    /// 各次模型调用的耗时之和。Harness 的用量事件不带耗时（MAF 的 UsageDetails 只有 token），
    /// 而 EOS 的落库列要它；这里在类型转换时顺手累加，供宿主取用。
    /// </summary>
    public int ElapsedMs { get; private set; }

    public string ModelName => inner.ModelName;

    public bool IsConfigured => inner.IsConfigured;

    public async IAsyncEnumerable<HarnessDelta> StreamAsync(
        IReadOnlyList<HarnessMessage> messages,
        IReadOnlyList<HarnessToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var eosMessages = messages.Select(ToEosMessage).ToArray();
        var eosTools = tools?
            .Select(tool => new EosToolDefinition(tool.Name, tool.Description, tool.ParametersJson))
            .ToArray();

        await foreach (var delta in inner
            .StreamAsync(eosMessages, eosTools, cancellationToken)
            .ConfigureAwait(false))
        {
            if (delta.Usage is not null)
            {
                ElapsedMs += delta.Usage.ElapsedMs;
            }

            yield return ToHarnessDelta(delta);
        }
    }

    private static EosMessage ToEosMessage(HarnessMessage message) => new(
        Role: message.Role switch
        {
            Agent.Harness.Abstractions.ChatRole.System => EosRole.System,
            Agent.Harness.Abstractions.ChatRole.Assistant => EosRole.Assistant,
            Agent.Harness.Abstractions.ChatRole.Tool => EosRole.Tool,
            _ => EosRole.User,
        },
        Content: message.Content,
        ToolCalls: message.ToolCalls is null
            ? null
            : [.. message.ToolCalls.Select(call => new EosCompletedCall(call.Id, call.Name, call.ArgumentsJson))],
        ToolCallId: message.ToolCallId);

    private static HarnessDelta ToHarnessDelta(EosDelta delta) => new(
        ContentDelta: delta.ContentDelta,
        Usage: delta.Usage is null
            ? null
            : new HarnessUsage(delta.Usage.PromptTokens, delta.Usage.CompletionTokens, delta.Usage.ElapsedMs),
        ToolCallDeltas: delta.ToolCallDeltas is null
            ? null
            : [.. delta.ToolCallDeltas.Select(fragment =>
                new HarnessFragment(fragment.Index, fragment.Id, fragment.Name, fragment.ArgumentsFragment))],
        FinishReason: delta.FinishReason,
        ReasoningDelta: delta.ReasoningDelta);
}

/// <summary>把 EOS 的一条消息转成 Harness 的回合消息（灰度路径复用已组装好的上下文，不重新拼提示词）。</summary>
internal static class EosMessageMapper
{
    public static AgentMessage ToHarness(EosMessage message) => new(
        Role: message.Role switch
        {
            EosRole.System => AgentRole.System,
            EosRole.Assistant => AgentRole.Assistant,
            EosRole.Tool => AgentRole.Tool,
            _ => AgentRole.User,
        },
        Content: message.Content,
        ToolCalls: message.ToolCalls is null
            ? null
            : [.. message.ToolCalls.Select(call =>
                new Agent.Harness.Abstractions.CompletedToolCall(call.Id, call.Name, call.ArgumentsJson))],
        ToolCallId: message.ToolCallId);
}

/// <summary>
/// 引擎执行前的策略门禁：**全放行**。
///
/// <para>
/// 这不是放松权限：EOS 的授权在工具内部（按当前用户重新判定，写类动作还走统一表单的既有写管线），
/// <see cref="IToolPolicy"/> 在架构上只是"执行前门禁"，不是权限依据。
/// 不可代理的批核族在 EOS 侧根本没有注册项，因此也不存在"这里放行就等于允许"的东西。
/// </para>
/// </summary>
internal sealed class AllowAllToolPolicy : IToolPolicy
{
    public ValueTask<ToolDecision> EvaluateAsync(
        ToolDescriptor tool,
        ToolInvocation invocation,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ToolDecision>(new ToolDecision.Allow());
}
