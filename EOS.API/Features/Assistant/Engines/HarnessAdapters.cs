using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
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
/// </summary>
internal sealed class EosToolAdapter(IAssistantTool tool, string userId) : ITool
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
        var result = await tool
            .ExecuteAsync(userId, invocation.Arguments, cancellationToken)
            .ConfigureAwait(false);

        return result.Ok
            ? ToolResult.Success(result.ContentForModel)
            : ToolResult.Deny(result.ContentForModel);
    }

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
/// 这不是放松权限：EOS 的授权在工具内部（按当前用户重新判定），
/// <see cref="IToolPolicy"/> 在架构上只是"执行前门禁"，不是权限依据。
/// 真正的灰度闸门是 <c>ReadOnlyToolsOnly</c>（只暴露只读工具）。
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
