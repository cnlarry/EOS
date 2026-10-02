namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// 模型调用失败的**类别**。分类不是为了措辞好看，而是为了两件实际的事：
///
/// <para>
/// ① **用户能不能自己解决**：密钥失效、余额不足、超出上下文窗口属于"可自助修复"——
/// 文案必须说清去哪儿修（3102 模型与用量、或新建会话）；限流、超时、供应商不可用属于"需等待"，
/// 文案只该说"稍后重试"。把这两类混成一句"模型调用失败"，用户唯一能做的就是来找我们。
/// </para>
///
/// <para>
/// ② **要不要计入失败熔断**：熔断防的是**抖动**（同一件事重试有可能成功）。
/// 配置与计费问题重试多少次都不会好，把它们算成技术抖动，只会在冷却期里把账号挡住，
/// 而问题一个字都没变。
/// </para>
/// </summary>
public enum AssistantModelErrorKind
{
    /// <summary>密钥无效或没有权限（HTTP 401/403）。</summary>
    Unauthorized,

    /// <summary>账号余额或配额不足（HTTP 402，或错误体里点名余额/配额）。</summary>
    InsufficientBalance,

    /// <summary>输入超出模型上下文窗口（HTTP 400/413 + 上下文相关特征）。</summary>
    ContextLengthExceeded,

    /// <summary>被限流（HTTP 429）。</summary>
    RateLimited,

    /// <summary>响应超时（模型层的超时 CTS 触发）。</summary>
    Timeout,

    /// <summary>供应商不可用或网络不可达（HTTP 5xx / 连接失败）。</summary>
    ProviderUnavailable,

    /// <summary>未识别。文案不编原因——编出来的原因会把排障带偏。</summary>
    Unknown,
}

/// <summary>
/// 厂商 / 网络侧的失败，带类别、给前端的稳定错误码与给用户的一句话。
/// 模型接入层只负责"把失败归一成类别"，怎么处置（要不要熔断、要不要告警）由编排层决定。
/// </summary>
public sealed class AssistantModelException(
    AssistantModelErrorKind kind,
    string message,
    Exception? inner = null) : Exception(message, inner)
{
    public AssistantModelErrorKind Kind { get; } = kind;

    /// <summary>可自助修复：重试不会变好，且责任人（管理员或用户自己）能当场处置。</summary>
    public bool SelfServiceable => Kind is AssistantModelErrorKind.Unauthorized
        or AssistantModelErrorKind.InsufficientBalance
        or AssistantModelErrorKind.ContextLengthExceeded;

    /// <summary>给前端的稳定错误码（SSE <c>error.code</c>）。</summary>
    public string Code => Kind switch
    {
        AssistantModelErrorKind.Unauthorized => "AI_MODEL_UNAUTHORIZED",
        AssistantModelErrorKind.InsufficientBalance => "AI_MODEL_INSUFFICIENT_BALANCE",
        AssistantModelErrorKind.ContextLengthExceeded => "AI_CONTEXT_LENGTH_EXCEEDED",
        AssistantModelErrorKind.RateLimited => "AI_RATE_LIMITED",
        AssistantModelErrorKind.Timeout => "AI_MODEL_TIMEOUT",
        AssistantModelErrorKind.ProviderUnavailable => "AI_MODEL_UNAVAILABLE",
        _ => "AI_MODEL_ERROR",
    };

    /// <summary>给用户看的一句话：可自助修复的说清去哪儿修，需等待的不编原因。</summary>
    public string UserMessage => Kind switch
    {
        AssistantModelErrorKind.Unauthorized =>
            "模型密钥无效或没有权限。请管理员在「工作助手管理 → 模型与用量」检查该供应商的密钥。",
        AssistantModelErrorKind.InsufficientBalance =>
            "模型账号余额或配额不足。请管理员在「工作助手管理 → 模型与用量」充值或更换密钥。",
        AssistantModelErrorKind.ContextLengthExceeded =>
            "本次请求超出了模型的上下文窗口。可以新建会话后重试，或请管理员调大该模型的上下文窗口。",
        AssistantModelErrorKind.RateLimited => "模型服务当前限流（请求过于频繁）。请稍后重试。",
        AssistantModelErrorKind.Timeout =>
            "模型服务响应超时。请稍后重试；若长期如此，请管理员调大该模型的超时。",
        AssistantModelErrorKind.ProviderUnavailable => "模型服务暂时不可用。请稍后重试。",
        _ => "模型调用失败，请稍后重试。",
    };

    /// <summary>把任意异常归一到本类型；已归一的原样返回（不重复包装）。</summary>
    public static AssistantModelException From(Exception exception) => exception switch
    {
        AssistantModelException model => model,
        TimeoutException timeout => new(AssistantModelErrorKind.Timeout, "模型服务响应超时。", timeout),
        HttpRequestException http => new(
            AssistantModelErrorKind.ProviderUnavailable, $"模型服务不可达：{http.Message}", http),
        _ => new(AssistantModelErrorKind.Unknown, exception.Message, exception),
    };
}
