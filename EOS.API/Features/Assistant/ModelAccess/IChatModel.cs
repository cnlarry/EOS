namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>对话消息角色（模型侧语义，与库内 ROLE 枚举 1/2/3 对应；Tool 仅存在于单轮内存上下文，不落库）。</summary>
public enum ChatRole
{
    System = 3,
    User = 1,
    Assistant = 2,
    Tool = 4,
}

/// <summary>一条进入模型的完整消息。ToolCalls/ToolCallId 分别用于 assistant-with-tool-calls 与 tool 结果消息。</summary>
public sealed record ChatMessage(
    ChatRole Role,
    string Content,
    IReadOnlyList<CompletedToolCall>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>
/// 模型流式增量。ContentDelta 为空表示本片无文本；ToolCallDeltas 为 function-call 分片
/// （arguments 按 Index 顺序拼接）；Usage 非空表示本次调用结束并附带用量统计；
/// FinishReason 为 "stop"/"tool_calls" 等原始语义。
/// </summary>
/// <param name="ContentDelta">文本增量（可为空）。</param>
/// <param name="Usage">用量收尾帧。</param>
/// <param name="ToolCallDeltas">工具调用分片。</param>
/// <param name="FinishReason">完成原因（原样透传，如 stop / tool_calls）。</param>
public sealed record ChatDelta(
    string? ContentDelta,
    ChatUsage? Usage,
    IReadOnlyList<ProposedToolCallFragment>? ToolCallDeltas = null,
    string? FinishReason = null);

/// <summary>单次调用的 token 用量与计时。</summary>
public sealed record ChatUsage(int PromptTokens, int CompletionTokens, int ElapsedMs);

/// <summary>发给模型的受控工具声明（JSON Schema 由服务端硬编码，绝不接受客户端传入）。</summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersJson);

/// <summary>流中的工具调用分片（OpenAI 兼容 delta.tool_calls 元素）。</summary>
public sealed record ProposedToolCallFragment(int Index, string? Id, string? Name, string? ArgumentsFragment);

/// <summary>聚合完成的工具调用（assistant 消息回喂与执行使用）。</summary>
public sealed record CompletedToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// 模型接入抽象（ADR-007 §4）：供应商无关的最小流式接口。
/// 实现只负责出网调用与协议解析，不做权限判断、不落库、不组装提示词、不执行工具。
/// </summary>
public interface IChatModel
{
    /// <summary>实际模型名（写入 ASSISTANT_MESSAGE.MODEL_NAME 与审计）。</summary>
    string ModelName { get; }

    /// <summary>
    /// 是否可用（ApiKey 等配置齐备）。不可用时 ChatService 以 AI_MODEL_NOT_CONFIGURED 快速失败，
    /// 不影响 API 其余功能。
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>流式补全：按序产出文本/工具调用增量与 usage 收尾帧。</summary>
    IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken cancellationToken);
}
