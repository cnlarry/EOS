namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>对话消息角色（模型侧语义，与库内 ROLE 枚举 1/2/3 对应）。</summary>
public enum ChatRole
{
    System = 3,
    User = 1,
    Assistant = 2,
}

/// <summary>一条进入模型的完整消息。</summary>
public sealed record ChatMessage(ChatRole Role, string Content);

/// <summary>
/// 模型流式增量。ContentDelta 为空表示本片无文本（如 usage 收尾帧）；
/// Usage 非空表示本次调用结束并附带用量统计。
/// </summary>
public sealed record ChatDelta(string? ContentDelta, ChatUsage? Usage);

/// <summary>单次调用的 token 用量与计时。</summary>
public sealed record ChatUsage(int PromptTokens, int CompletionTokens, int ElapsedMs);

/// <summary>
/// 模型接入抽象（ADR-007 §4）：供应商无关的最小流式接口。
/// M2 只有多轮纯文本对话；M3 工具调用在此契约上扩展（新增 tool-call delta 类型），
/// 不改既有消费方。实现只负责出网调用，不做权限判断、不落库、不组装提示词。
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

    /// <summary>流式补全：按序产出文本增量，结束时产出带 Usage 的收尾帧。</summary>
    IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken);
}
