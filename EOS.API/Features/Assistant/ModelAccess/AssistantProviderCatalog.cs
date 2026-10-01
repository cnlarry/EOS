namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// 预设模型：系统"知道"的一个模型及其参考参数。
///
/// <para>
/// 价格刻意留空（<c>null</c> = 用全局兜底单价）：各家的实际单价随账号折扣、缓存命中与时段变动，
/// 把一串看起来很权威的数字写进代码，比留空更容易误导——留空至少会让界面明确告诉你"当前用的是兜底价"。
/// 上下文窗口与最大输出是厂商文档里的量级，取**偏保守**的值：宁可比真实窗口小一点，
/// 也不能大——算大了会带着超长上下文去请求，厂商直接 400。
/// </para>
/// </summary>
public sealed record AssistantModelPreset(
    string ModelCode,
    string DisplayName,
    int? ContextWindow,
    int? MaxOutputTokens,
    bool SupportsTools,
    decimal? InputPerMillionYuan = null,
    decimal? OutputPerMillionYuan = null,
    decimal? DefaultTemperature = null,
    string? Remark = null);

/// <summary>
/// 预设供应商：一个接入点（端点 + 密钥环境变量名 + 默认超时）及其可用模型清单。
///
/// <para>
/// 界面上的用法是：选一个供应商 → 带出端点与它的模型清单 → 勾选要启用的模型批量落库。
/// 落库之后一切都可以改：价格与窗口会随厂商调整，预设给的是**初值**而不是权威。
/// </para>
/// </summary>
public sealed record AssistantProviderPreset(
    string Code,
    string DisplayName,
    string BaseUrl,
    string SuggestedApiKeyEnvVar,
    int TimeoutSeconds,
    IReadOnlyList<AssistantModelPreset> Models,
    string? Remark = null);

/// <summary>
/// 主流供应商与模型的**参考目录**（代码内置，不进数据库）。
///
/// <para>
/// 为什么不写种子数据：表的语义是"用户配了什么"，目录是"系统知道什么"。预设一旦落库，
/// 既绕开了"初始状态必须配置才能用"这条规则（表非空即等于已配置），又会让升级变成改数据而不是改代码。
/// </para>
///
/// <para>
/// **这份目录需要定期复核**：厂商会加模型、改窗口、改名。但它不是权威——用户在界面上可以改要落库的
/// 每一个值，也可以选"自定义"自己填。改这里的收益是"下次别人添加时初值更准"。
/// </para>
/// </summary>
public static class AssistantProviderCatalog
{
    /// <summary>所有预设。它们都走 OpenAI 兼容协议，因此共用同一个客户端实现。</summary>
    public static IReadOnlyList<AssistantProviderPreset> All { get; } =
    [
        new(
            Code: "deepseek",
            DisplayName: "DeepSeek 开放平台",
            BaseUrl: "https://api.deepseek.com",
            SuggestedApiKeyEnvVar: "EOS_ASSISTANT_KEY_DEEPSEEK",
            TimeoutSeconds: 300,
            Remark: "推理模型（reasoner）首次响应可能较慢，可在模型行上单独调大超时。",
            Models:
            [
                new("deepseek-chat", "DeepSeek Chat（通用对话）", ContextWindow: 65536, MaxOutputTokens: 8192, SupportsTools: true,
                    Remark: "通用对话与工具调用。"),
                new("deepseek-reasoner", "DeepSeek Reasoner（推理）", ContextWindow: 65536, MaxOutputTokens: 8192, SupportsTools: true,
                    Remark: "长链推理；响应更慢，建议在模型行上把超时调大（如 600 秒）。"),
            ]),
        new(
            Code: "openai",
            DisplayName: "OpenAI",
            BaseUrl: "https://api.openai.com/v1",
            SuggestedApiKeyEnvVar: "EOS_ASSISTANT_KEY_OPENAI",
            TimeoutSeconds: 300,
            Models:
            [
                new("gpt-4o", "GPT-4o", ContextWindow: 131072, MaxOutputTokens: 16384, SupportsTools: true),
                new("gpt-4o-mini", "GPT-4o mini", ContextWindow: 131072, MaxOutputTokens: 16384, SupportsTools: true,
                    Remark: "便宜且够用，适合工具调用密集的场景。"),
            ]),
        new(
            Code: "dashscope",
            DisplayName: "阿里云百炼（通义千问）",
            BaseUrl: "https://dashscope.aliyuncs.com/compatible-mode/v1",
            SuggestedApiKeyEnvVar: "EOS_ASSISTANT_KEY_DASHSCOPE",
            TimeoutSeconds: 300,
            Remark: "走百炼的 OpenAI 兼容模式端点。",
            Models:
            [
                new("qwen-plus", "通义千问 Plus", ContextWindow: 131072, MaxOutputTokens: 8192, SupportsTools: true),
                new("qwen-max", "通义千问 Max", ContextWindow: 32768, MaxOutputTokens: 8192, SupportsTools: true),
            ]),
        new(
            Code: "zhipu",
            DisplayName: "智谱 AI",
            BaseUrl: "https://open.bigmodel.cn/api/paas/v4",
            SuggestedApiKeyEnvVar: "EOS_ASSISTANT_KEY_ZHIPU",
            TimeoutSeconds: 300,
            Models:
            [
                new("glm-4-plus", "GLM-4 Plus", ContextWindow: 131072, MaxOutputTokens: 8192, SupportsTools: true),
                new("glm-4-flash", "GLM-4 Flash", ContextWindow: 131072, MaxOutputTokens: 8192, SupportsTools: true,
                    Remark: "轻量版。"),
            ]),
        new(
            Code: "moonshot",
            DisplayName: "月之暗面（Kimi）",
            BaseUrl: "https://api.moonshot.cn/v1",
            SuggestedApiKeyEnvVar: "EOS_ASSISTANT_KEY_MOONSHOT",
            TimeoutSeconds: 300,
            Models:
            [
                new("kimi-k2-0905-preview", "Kimi K2", ContextWindow: 131072, MaxOutputTokens: 16384, SupportsTools: true),
                new("moonshot-v1-32k", "Moonshot v1 32K", ContextWindow: 32768, MaxOutputTokens: 8192, SupportsTools: true),
            ]),
        new(
            Code: "custom",
            DisplayName: "自定义（其它 OpenAI 兼容厂商）",
            // **刻意留空**：自定义厂商没有已知端点。填一个像模像样的假地址比留空更危险——
            // 留空会被控制器的"端点不能为空"当场拦下，假地址则有可能被人一路保存下去。
            BaseUrl: string.Empty,
            SuggestedApiKeyEnvVar: "EOS_ASSISTANT_KEY_CUSTOM",
            TimeoutSeconds: 300,
            Remark: "端点、模型名与参数都由你自己填。",
            Models: []),
    ];

    /// <summary>按 Code 找预设；找不到返回 null（界面提示"未知供应商，请选自定义"）。</summary>
    public static AssistantProviderPreset? Find(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : All.FirstOrDefault(item => string.Equals(item.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 供应商是否受支持。**落库时以这份目录为准**：库里存的是 CODE，而 CODE 决定了用哪个客户端实现。
    /// 允许目录里没有的 CODE 没有意义——那样存下来的是一个没人认得的字符串。
    /// </summary>
    public static bool IsSupported(string? code) => Find(code) is not null;

    /// <summary>允许的 CODE 清单（用于校验失败时给出可选项）。</summary>
    public static IReadOnlyList<string> SupportedCodes { get; } = [.. All.Select(item => item.Code)];
}
