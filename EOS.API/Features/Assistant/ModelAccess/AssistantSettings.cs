namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// 工作助手模型接入配置。未配置 ApiKey 时助手功能降级，
/// 其余 API 不受影响。
/// </summary>
public sealed class AssistantSettings
{
    public const string SectionName = "Assistant";

    /// <summary>供应商端点根（OpenAI 兼容；DeepSeek 默认 https://api.deepseek.com）。</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    /// <summary>API Key。空 = 未配置（chat 端点以 AI_MODEL_NOT_CONFIGURED 快速失败）。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>对话模型名。</summary>
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>单次调用总时长上限（秒），覆盖流式全程。</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>会话结束自动提炼（M9）：done 事件后异步提炼候选记忆，失败静默。运维可关闭。</summary>
    public bool EnableAutoDistill { get; set; } = true;

    /// <summary>成本限额与熔断（M7）。</summary>
    public Governance.AssistantCostOptions Cost { get; set; } = new();

    /// <summary>注入给每轮对话的系统提示词。</summary>
    public string SystemPrompt { get; set; } =
        "你是 EOS ERP 的工作助手。用简体中文简洁、专业地回答；"
        + "不确定的事实要说明不确定性，不要编造单据号、金额或系统功能。";
}
