namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// 工作助手**运行期配置的一份快照**，同时也是所有参数的**代码默认值**。
///
/// <para>
/// <c>new AssistantSettings()</c> 的字段值就是默认值：全局策略（提示词、日上限、单价兜底、熔断）
/// 由 3105 的设置表按需覆盖（<c>AssistantSettingKeys.Apply</c> 就是唯一入口，界面上的"默认值"提示
/// 也取自这里，所以不会出现"界面显示 5 元、代码其实已经改了"的漂移）；传输层字段（端点、模型标识、
/// 超时、温度、窗口、工具能力、单价）由 3102 的模型行覆盖。
/// </para>
///
/// <para>
/// **它不再从 <c>IConfiguration</c> 绑定**（ADR-030 §8）：助手配置一律由管理面持有。
/// 未配置当前模型（或那一把密钥没配）时助手功能不可用，其余 API 不受影响。
/// </para>
/// </summary>
public sealed class AssistantSettings
{
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

    /// <summary>
    /// 当前模型的**上下文窗口**（token）。<c>null</c> = 未知，按保守默认处理。
    ///
    /// <para>
    /// **会被消费**：<c>ChatService</c> 用它推算一轮能带多少历史消息（此前是硬编码"最多 40 条、
    /// 完全不看 token"，长会话可能直接顶穿窗口）。算小了只是少带点历史，算大了会被厂商 400，
    /// 所以这里与预设目录都取偏保守的值。
    /// </para>
    /// </summary>
    public int? ContextWindow { get; set; }

    /// <summary>
    /// 当前模型是否支持工具调用。默认 true。
    ///
    /// <para>
    /// **会被消费**：不支持的模型仍然带 <c>tools</c> 去请求，厂商会直接报错，而错误信息通常只说
    /// 参数不合法，看不出是模型选错了。
    /// </para>
    /// </summary>
    public bool SupportsTools { get; set; } = true;

    /// <summary>
    /// 当前模型的单价（元 / 百万 token）。<c>null</c> = 用 <see cref="Cost"/> 里的全局兜底价。
    ///
    /// <para>
    /// **会被消费**：台账结算（<c>SettleAsync</c> 写入的钱）与用量看板都按它算。没有它的话，
    /// 换个单价差十倍的模型，限额熔断仍然按同一套单价判定——**成本归因是错的**。
    /// 留空退回兜底而不是按 0 元算：0 元会让日限额形同虚设。
    /// </para>
    /// </summary>
    public decimal? InputPerMillionYuan { get; set; }

    /// <summary>当前模型的输出单价。计算口径同 <see cref="InputPerMillionYuan"/>。</summary>
    public decimal? OutputPerMillionYuan { get; set; }

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
