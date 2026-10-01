using EOS.API.Data;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>当前生效的模型（来自数据库里那一条 <c>IS_ACTIVE = 1</c> 的记录）及其所属供应商。</summary>
public sealed record ResolvedAssistantModel(
    int ModelId,
    string DisplayName,
    string ModelCode,
    int ProviderId,
    string ProviderCode,
    string ProviderDisplayName,
    AssistantSettings Settings);

/// <summary>
/// 助手"当前用哪个模型"的唯一权威来源。
///
/// <para>
/// 此前模型是**启动期单例绑配置**（<c>AddSingleton&lt;IChatModel, DeepSeekChatModel&gt;</c> +
/// <c>IOptions&lt;AssistantSettings&gt;</c>），进程跑起来就改不了。管理员要在界面上切换模型，
/// 就必须有一个**运行期可刷新**的快照存放处——这就是它。
/// </para>
///
/// <para>
/// 刷新时机只有两处：**启动时**（迁移之后，照 <c>WorkbenchDefinitionProvider</c> 的既有做法）
/// 与**管理员每次改动模型之后**。于是切换模型不需要重启服务。
/// </para>
///
/// <para>
/// 表里没有启用的当前模型时 <see cref="Active"/> 为 <c>null</c>，助手回到配置文件里那套——
/// 所以这张表空着上线，行为与从前**完全一致**。
/// </para>
/// </summary>
public sealed class AssistantModelRegistry(
    IAssistantModelCatalog catalog,
    IAssistantSecretStore secrets,
    IOptions<AssistantSettings> fallback,
    ILogger<AssistantModelRegistry> logger)
{
    private volatile ResolvedAssistantModel? _active;

    /// <summary>当前生效的模型；<c>null</c> = 用配置文件里的模型。</summary>
    public ResolvedAssistantModel? Active => _active;

    /// <summary>
    /// 重新读取当前模型。启动时一次，管理员改动后一次。
    ///
    /// <para>
    /// **读失败时保持现状**而不是清空：把正在工作的模型撤掉，比"晚一点生效"糟得多
    /// （迁移脚本尚未执行时启动、数据库短暂不可用，都会走到这里）。
    /// </para>
    /// </summary>
    public async Task RefreshAsync(CancellationToken token)
    {
        try
        {
            var active = await catalog.GetActiveAsync(token);
            _active = active is null
                ? null
                : new ResolvedAssistantModel(
                    active.Model.ModelId,
                    active.Model.DisplayName,
                    active.Model.ModelCode,
                    active.Provider.ProviderId,
                    active.Provider.Code,
                    active.Provider.DisplayName,
                    Build(active));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取当前模型失败，继续沿用上一次生效的模型。");
        }
    }

    /// <summary>
    /// 把表行物化成一次调用要用的配置。
    ///
    /// <para>
    /// 只让**传输层**字段来自表行（端点 / 模型名 / 超时 / 温度 / 最大 token / 密钥）；
    /// 系统提示词与成本限额仍来自配置——换个模型不该把提示词和额度口径一起换掉。
    /// </para>
    /// </summary>
    private AssistantSettings Build(AssistantActiveModel active)
    {
        var baseSettings = fallback.Value;
        var model = active.Model;
        return new AssistantSettings
        {
            // 端点与密钥来自**供应商**：一个供应商一个端点、一把密钥，通吃它下面所有模型
            BaseUrl = active.Provider.BaseUrl,
            Model = model.ModelCode,
            // 密钥此刻从环境变量读；库里存的那一位只是变量名
            ApiKey = secrets.Read(active.Provider.ApiKeyEnvVar) ?? string.Empty,
            // 模型行可以覆盖供应商的默认超时（推理模型首次响应慢，往往要单独调大）
            TimeoutSeconds = model.TimeoutSeconds ?? active.Provider.TimeoutSeconds,
            Temperature = model.DefaultTemperature,
            MaxTokens = model.MaxOutputTokens,
            ContextWindow = model.ContextWindow,
            SupportsTools = model.SupportsTools,
            InputPerMillionYuan = model.InputPerMillionYuan,
            OutputPerMillionYuan = model.OutputPerMillionYuan,
            EnableAutoDistill = baseSettings.EnableAutoDistill,
            Cost = baseSettings.Cost,
            SystemPrompt = baseSettings.SystemPrompt,
        };
    }
}

/// <summary>
/// 消费方用的 <see cref="IChatModel"/>：按注册表里"当前模型"构造真正的实现。
///
/// <para>
/// 注册为 **Scoped**（此前是 Singleton），两个原因：
/// ① **一次请求内只解析一次**——一条回答可能跑好几轮工具调用，若中途有人切换模型，同一条回答会
/// 跨两个模型，而落库的 <c>MODEL_NAME</c> 只有一个，审计与用量都会失真；
/// ② **跨请求读得到新快照**——所以切换模型不需要重启。
/// </para>
///
/// <para>
/// 换掉注册生命周期后 <c>ChatService</c> 一行都不用改：它是 Scoped，本来就只按接口注入。
/// </para>
/// </summary>
public sealed class ResolvingChatModel(
    IHttpClientFactory httpClientFactory,
    IOptions<AssistantSettings> fallback,
    AssistantModelRegistry registry) : IChatModel
{
    private IChatModel? _inner;

    private IChatModel Inner => _inner ??= new DeepSeekChatModel(
        httpClientFactory,
        Options.Create(registry.Active?.Settings ?? fallback.Value));

    /// <inheritdoc />
    public string ModelName => Inner.ModelName;

    /// <inheritdoc />
    public bool IsConfigured => Inner.IsConfigured;

    /// <inheritdoc />
    public IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken cancellationToken) => Inner.StreamAsync(messages, tools, cancellationToken);
}
