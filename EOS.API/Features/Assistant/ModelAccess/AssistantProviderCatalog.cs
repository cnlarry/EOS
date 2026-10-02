namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// 模型用途。对话模型与嵌入模型走**同一张表、同一个目录**（ADR-031 §3.1），
/// 所以必须能区分：两者的窗口、上限、单价语义完全不同，嵌入还多一个维度。
/// </summary>
public enum AssistantModelKind
{
    /// <summary>对话 / 生成模型。</summary>
    Chat = 0,

    /// <summary>嵌入 / 向量模型。</summary>
    Embedding = 1,
}

/// <summary>
/// 能力三态。**刻意不用 bool**：「核实过做不到」与「还没核实」是两件事——
/// 前者界面该说"这家没有这个端点"，后者该说"没核过，试了失败请手工填"。
/// 把两者压成一个 false，界面就只能含糊其辞，而含糊会让管理员反复试一个注定失败的端点。
/// </summary>
public enum AssistantCapability
{
    /// <summary>已核实支持。</summary>
    Supported = 0,

    /// <summary>已核实不支持（官方文档口径）。</summary>
    Unsupported = 1,

    /// <summary>未核实。</summary>
    Unknown = 2,
}

/// <summary>
/// 认证头样式。**端点、协议、认证头是"能不能发出去"的三件套**，缺一件那家就永远 401：
/// 实测小米 MiMo 用的是 <c>api-key</c> 头而不是 <c>Authorization: Bearer</c>——
/// 把这一行写死在客户端里，那家供应商就再也接不上。
/// </summary>
public enum AssistantAuthStyle
{
    /// <summary><c>Authorization: Bearer &lt;key&gt;</c>（主流 OpenAI 兼容形态）。</summary>
    Bearer = 0,

    /// <summary>自定义请求头（如小米 MiMo 的 <c>api-key</c>）。</summary>
    ApiKeyHeader = 1,

    /// <summary>不带凭据（自建端点，如本机嵌入服务）。</summary>
    None = 2,
}

/// <summary>
/// 预设模型：系统"知道"的一个模型及其参考参数。
///
/// <para>
/// 价格刻意留空（<c>null</c> = 用全局兜底单价）：各家的实际单价随账号折扣、缓存命中与时段变动，
/// 把一串看起来很权威的数字写进代码，比留空更容易误导——留空至少会让界面明确告诉你"当前用的是兜底价"。
/// 上下文窗口与最大输出是厂商文档里的量级，取**偏保守**的值：宁可比真实窗口小一点，
/// 也不能大——算大了会带着超长上下文去请求，厂商直接 400。
/// </para>
///
/// <para>
/// **这份清单是参考初值，不是权威**：型号会过期（已经有实据——旧的 DeepSeek 型号名与厂商当前文档对不上），
/// 所以正式的清单靠"向厂商拉取"得到（ADR-030 §12.3），这里只为常用的型号省一次手工填。
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
    string? Remark = null,
    AssistantModelKind Kind = AssistantModelKind.Chat,
    int? Dimension = null);

/// <summary>
/// 模型接入协议。它是**代码认识的东西**，不是用户填的自由文本：
/// 协议的选型决定客户端实现，供应商行上的 CODE 只是它的入口。
///
/// <para>
/// 目前只有一种（目录里全部供应商都走 OpenAI 兼容 <c>/chat/completions</c>，含自定义），
/// 所以这里只有一个成员——留这个枚举是为了让"换协议要改代码"这件事有一个明确的落点，
/// 而不是散在 <c>ResolvingChatModel</c> 的一行 <c>new</c> 里。
/// </para>
/// </summary>
public enum AssistantModelProtocol
{
    /// <summary>OpenAI 兼容 /chat/completions。</summary>
    OpenAiCompatible = 0,
}

/// <summary>
/// 预设供应商：一个接入点（端点 + 认证头样式 + 密钥环境变量名 + 默认超时 + 协议）及其可用模型清单。
///
/// <para>
/// 界面上的用法是：选一个供应商 → 带出端点与它的模型清单 → 勾选要启用的模型批量落库。
/// 落库之后一切都可以改：价格与窗口会随厂商调整，预设给的是**初值**而不是权威。
/// </para>
///
/// <para>
/// <see cref="Embedding"/> 与 <see cref="ModelListing"/> 是**核实的结论**，不是猜测：
/// 前者决定"这家能不能做嵌入"（DeepSeek / KIMI / 小米都不能），后者决定"能不能拉取型号清单"。
/// 界面据此说人话，而不是让管理员在明知不支持的端点上一遍遍试。
/// </para>
/// </summary>
public sealed record AssistantProviderPreset(
    string Code,
    string DisplayName,
    string BaseUrl,
    string SuggestedApiKeyEnvVar,
    int TimeoutSeconds,
    IReadOnlyList<AssistantModelPreset> Models,
    string? Remark = null,
    AssistantModelProtocol Protocol = AssistantModelProtocol.OpenAiCompatible,
    AssistantAuthStyle AuthStyle = AssistantAuthStyle.Bearer,
    AssistantCapability Embedding = AssistantCapability.Unknown,
    AssistantCapability ModelListing = AssistantCapability.Unknown);

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
///
/// <para>
/// **<see cref="AssistantCapability"/> 与 <see cref="AssistantAuthStyle"/> 是核出来的事实**
/// （2026-10-02 逐家对官方文档），不是印象：嵌入端点的有无、能不能列出模型、认证头长什么样，
/// 都决定了"这家配上去能不能用"。核过才写，没核过的写 <c>Unknown</c>。
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
            // 官方文档的模型页列了思考模式 / 工具调用 / Responses API / 视觉等能力，**没有嵌入端点**；
            // 列出模型页则确有（除标识外还返回窗口、最大输出、模态与 effort 等级）
            Embedding: AssistantCapability.Unsupported,
            ModelListing: AssistantCapability.Supported,
            Remark: "推理模型首次响应可能较慢，可在模型行上单独调大超时。"
                + "窗口与最大输出可直接从该家的列出模型接口取（它比多数厂商给得多）。",
            Models:
            [
                new("deepseek-flash", "DeepSeek Flash（通用 + 视觉）", ContextWindow: 1048576, MaxOutputTokens: 393216,
                    SupportsTools: true, Remark: "厂商文档当前的主推型号之一，支持视觉输入。"),
                new("deepseek-v4-pro", "DeepSeek V4 Pro（旗舰）", ContextWindow: 1048576, MaxOutputTokens: 393216,
                    SupportsTools: true, Remark: "厂商文档当前的主推型号之一。"),
            ]),
        new(
            Code: "openai",
            DisplayName: "OpenAI",
            BaseUrl: "https://api.openai.com/v1",
            SuggestedApiKeyEnvVar: "EOS_ASSISTANT_KEY_OPENAI",
            TimeoutSeconds: 300,
            Embedding: AssistantCapability.Supported,
            ModelListing: AssistantCapability.Supported,
            Remark: "对话与嵌入都有；嵌入模型可用 `dimensions` 对齐到现有集合的维度。",
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
            Embedding: AssistantCapability.Supported,
            Remark: "走百炼的 OpenAI 兼容模式端点（新文档改用带业务空间的 host："
                + "{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode/v1，两者都可用，端点可改）。"
                + "嵌入端点确认为 /compatible-mode/v1/embeddings。",
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
            Embedding: AssistantCapability.Supported,
            Remark: "嵌入端点确认为 /api/paas/v4/embeddings（embedding-3 默认 2048 维，可指定 1024）。",
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
            // 官方文档里有"列出模型"页，但没有嵌入端点。
            // 注意：第三方 API 目录站点声称它有 embeddings，那类目录是自动生成的，**不可采信**——
            // 以官方文档为准，否则会把一个必定 404 的端点写进预设。
            Embedding: AssistantCapability.Unsupported,
            ModelListing: AssistantCapability.Supported,
            Models:
            [
                new("kimi-k2-0905-preview", "Kimi K2", ContextWindow: 131072, MaxOutputTokens: 16384, SupportsTools: true),
                new("moonshot-v1-32k", "Moonshot v1 32K", ContextWindow: 32768, MaxOutputTokens: 8192, SupportsTools: true),
            ]),
        new(
            Code: "xiaomi",
            DisplayName: "小米 MiMo 开放平台",
            BaseUrl: "https://api.xiaomimimo.com/v1",
            SuggestedApiKeyEnvVar: "EOS_ASSISTANT_KEY_XIAOMI",
            TimeoutSeconds: 300,
            // 这家最能说明"认证头也是数据"：curl 示例用的是 `api-key` 头，不是 Authorization: Bearer。
            AuthStyle: AssistantAuthStyle.ApiKeyHeader,
            // 官方模型清单里只有文本生成 / 语音识别 / 语音合成，**没有嵌入模型**
            Embedding: AssistantCapability.Unsupported,
            Remark: "认证头是 `api-key`（不是 Bearer）；另有批量推理与套餐专用 host，端点按需改。"
                + "同平台还有语音模型，本系统只接文本生成。",
            Models:
            [
                new("mimo-v2.6-pro", "MiMo V2.6 Pro", ContextWindow: 1048576, MaxOutputTokens: 131072,
                    SupportsTools: true, Remark: "旗舰；支持全模态理解、函数调用、结构化输出。"),
                new("mimo-v2.6-flash", "MiMo V2.6 Flash", ContextWindow: 1048576, MaxOutputTokens: 131072,
                    SupportsTools: true, Remark: "高频调用档。"),
            ]),
        new(
            Code: "custom",
            DisplayName: "自定义（其它 OpenAI 兼容厂商）",
            // **刻意留空**：自定义厂商没有已知端点。填一个像模像样的假地址比留空更危险——
            // 留空会被控制器的"端点不能为空"当场拦下，假地址则有可能被人一路保存下去。
            BaseUrl: string.Empty,
            SuggestedApiKeyEnvVar: "EOS_ASSISTANT_KEY_CUSTOM",
            TimeoutSeconds: 300,
            // 自定义端点按最主流的形态假设：Bearer + 有对话 + 有没有嵌入不知道。
            // 假设成"没有嵌入"会挡住正当用法；假设成"有"会让界面给出不存在的承诺——所以是 Unknown。
            AuthStyle: AssistantAuthStyle.Bearer,
            Embedding: AssistantCapability.Unknown,
            Remark: "端点、模型名与参数都由你自己填；有嵌入端点的自建服务也走这一项（认证头样式固定为 Bearer，"
                + "密钥环境变量名留空即不带凭据）。",
            Models: []),
    ];

    /// <summary>
    /// 能提供嵌入模型的供应商（实测支持或未核实）——界面据此给出候选，
    /// 而不是让管理员在**明知没有嵌入端点**的那几家上反复试。
    /// </summary>
    public static IReadOnlyList<AssistantProviderPreset> EmbeddingCandidates { get; } =
        [.. All.Where(item => item.Embedding != AssistantCapability.Unsupported)];

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

    /// <summary>
    /// 某个 CODE 用哪种协议。目录外的 CODE 按**默认协议**处理：能落库的 CODE 一定在目录内
    /// （<see cref="IsSupported"/> 在写入侧拦着），走到这里的未知值只可能来自更早的数据，
    /// 这时按默认协议跑比直接拒绝更有用——拒绝会让一个正在工作的模型突然不可用。
    /// </summary>
    public static AssistantModelProtocol ProtocolOf(string? code) =>
        Find(code)?.Protocol ?? AssistantModelProtocol.OpenAiCompatible;

    /// <summary>目录外的 CODE 按主流形态（Bearer）处理，理由同 <see cref="ProtocolOf"/>。</summary>
    public static AssistantAuthStyle AuthStyleOf(string? code) =>
        Find(code)?.AuthStyle ?? AssistantAuthStyle.Bearer;

    /// <summary>允许的 CODE 清单（用于校验失败时给出可选项）。</summary>
    public static IReadOnlyList<string> SupportedCodes { get; } = [.. All.Select(item => item.Code)];
}
