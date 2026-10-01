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

    /// <summary>
    /// 采样温度。<c>null</c> = 不传该参数，用厂商默认。
    ///
    /// <para>
    /// 与 <c>BaseUrl</c>/<c>Model</c> 一样属于**传输层参数**：当管理员在模型管理页（3102）
    /// 配了模型，这些值由那一行覆盖；表为空时退回这里的配置。
    /// </para>
    /// </summary>
    public decimal? Temperature { get; set; }

    /// <summary>单次回复的最大输出 token。<c>null</c> = 不传，用厂商默认。</summary>
    public int? MaxTokens { get; set; }

    /// <summary>会话结束自动提炼：done 事件后异步提炼候选记忆，失败静默。运维可关闭。</summary>
    public bool EnableAutoDistill { get; set; } = true;

    /// <summary>成本限额与熔断。</summary>
    public Governance.AssistantCostOptions Cost { get; set; } = new();

    /// <summary>
    /// 注入给每轮对话的系统提示词。
    ///
    /// <para>
    /// 输出格式部分是**与界面渲染面的契约**：抽屉按 Markdown（GFM 子集）渲染助手回答
    /// （<c>EOS.Web/src/features/assistant/markdown.tsx</c>，只产出 React 元素、不解析 HTML）。
    /// 若这里不约定格式，模型会按各自习惯输出，界面只能把标记原样显示成纯文本。
    /// </para>
    /// </summary>
    public string SystemPrompt { get; set; } =
        "你是 EOS ERP 的工作助手。用简体中文简洁、专业地回答；"
        + "不确定的事实要说明不确定性，不要编造单据号、金额或系统功能。"
        + "回答用 Markdown 组织（界面会渲染成富文本）：结论先行；分点内容用列表；"
        + "多字段对照用表格（表头一行 + |---| 分隔行）；字段名、表名、单号用 `反引号` 标出。"
        + "只输出 Markdown 正文，不要输出 HTML 标签，也不要给整段回答套代码块。";
}
