using EOS.API.Data;
using EOS.API.Features.Assistant.Parameters;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>当前生效的模型（来自数据库里那一条 <c>IS_ACTIVE = 1</c> 的记录）及其所属供应商。</summary>
public sealed record ResolvedAssistantModel(
    int ModelId,
    string DisplayName,
    string ModelCode,
    int ProviderId,
    string ProviderCode,
    string ProviderDisplayName);

/// <summary>
/// 助手运行期配置的**一份快照**：当前模型 + 生效的全局策略。
///
/// <para>
/// 两者合成一个对象，是因为它们本来就是一起生效的：模型行提供传输层（端点 / 模型标识 / 超时 /
/// 温度 / 最大输出 / 窗口 / 工具能力 / 单价），策略提供全局项（系统提示词 / 日上限 / 单价兜底 / 熔断）。
/// 一次请求只取一次快照，于是"这一轮回答到底按哪套配置跑的"是可回答的。
/// </para>
/// </summary>
public sealed record AssistantRuntimeSnapshot(ResolvedAssistantModel? Model, AssistantSettings Settings)
{
    /// <summary>
    /// 全局参数行（<c>dbo.SYSSS</c> 的 <c>OWNER_MODULE = 3105</c>）。
    ///
    /// <para>
    /// 快照里连**原始行**一起留着，是为了让作用域覆盖能在它之上叠加
    /// （<c>AssistantParameterScopeRules.Layer</c>），叠加完仍交给同一个解释器——
    /// 于是"全局值怎么解释"只有一处实现，作用域不引入第二套口径。
    /// </para>
    /// </summary>
    public IReadOnlyList<Data.SystemParameterItem> ParameterRows { get; init; } = [];

    /// <summary>
    /// 全局参数的解释结果（快照的一部分）。作用域叠加（模块 / 用户）以它为起点，
    /// 没有覆盖行时直接用它——所以"大多数人"这个请求里一次额外的库都不用查。
    /// </summary>
    public AssistantPolicyValues Policy { get; init; } = AssistantPolicyValues.Default;

    /// <summary>
    /// 能不能真正发起模型调用：**有当前模型，且那把密钥已配置**。
    ///
    /// <para>
    /// 注意这里**没有**任何"退回配置文件"的退路（ADR-030 §8）：管理面没配好就是未配置，
    /// 助手直接以 <c>AI_MODEL_NOT_CONFIGURED</c> 失败并把话说清楚。此前那种"表为空就用 appsettings
    /// 里那套"的兜底，看起来贴心，实际会让"界面没配好"表现成"助手在用另一个谁也说不清的模型"。
    /// </para>
    /// </summary>
    public bool IsConfigured => Model is not null && !string.IsNullOrWhiteSpace(Settings.ApiKey);
}

/// <summary>
/// 运行期配置的只读来源。消费方（<c>ChatService</c>、控制器、<c>ResolvingChatModel</c>）依赖这个接口
/// 而不是具体注册表，测试就能喂一个固定快照，不必去伪造数据库与密钥存储。
/// </summary>
public interface IAssistantRuntimeConfig
{
    AssistantRuntimeSnapshot Current { get; }
}

/// <summary>
/// 助手"当前用什么模型、按哪套策略跑"的唯一权威来源。
///
/// <para>
/// 此前是**启动期单例绑配置**（<c>AddSingleton&lt;IChatModel, DeepSeekChatModel&gt;</c> +
/// <c>IOptions&lt;AssistantSettings&gt;</c>），进程跑起来就改不了。现在两块都来自数据库：
/// 模型来自 <c>dbo.ASSISTANT_MODEL/PROVIDER</c>（3102），全局策略来自 <c>dbo.SYSSS</c> 的
/// <c>OWNER_MODULE = 3105</c>（3105 助手设置，解析见 <see cref="AssistantParameterResolver"/>）。
/// </para>
///
/// <para>
/// 刷新时机只有两处：**启动时**（迁移之后，照 <c>WorkbenchDefinitionProvider</c> 的既有做法）
/// 与**管理员每次改动之后**。所以改配置与切模型都不需要重启服务。
/// </para>
///
/// <para>
/// **一次刷新是全量或不动**：任何一步失败都只沿用旧值，绝不把正在工作的模型撤掉——
/// 数据库短暂不可用、迁移还没跑完，都会走到这条路径上。
/// </para>
/// </summary>
public sealed class AssistantRuntimeRegistry(
    IAssistantModelCatalog catalog,
    AssistantParameterResolver parameterResolver,
    IAssistantSecretStore secrets,
    ILogger<AssistantRuntimeRegistry> logger) : IAssistantRuntimeConfig
{
    private AssistantActiveModel? _active;
    private AssistantParameterSet _parameters = AssistantParameterSet.Empty;
    private volatile AssistantRuntimeSnapshot _current = new(null, new AssistantSettings());

    /// <inheritdoc />
    public AssistantRuntimeSnapshot Current => _current;

    /// <summary>重新读取全局策略与当前模型。启动时一次，管理员改动后各一次。</summary>
    public async Task RefreshAsync(CancellationToken token)
    {
        try
        {
            // 解析失败的值由解析器**逐条记日志**：静默忽略会让"界面显示 5 元、实际按默认值跑"无从追查
            _parameters = await parameterResolver.ResolveAsync(token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取助手参数失败，沿用上一次生效的策略。");
        }

        try
        {
            // 按用途取：这张快照服务的是**对话**（嵌入模型的当前值由知识库那条链路自己取，ADR-031 §3.1）
            _active = await catalog.GetActiveAsync(AssistantModelKind.Chat, token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取当前模型失败，沿用上一次生效的模型。");
        }

        Rebuild();
    }

    /// <summary>用"当前拿得到的最新值"重组快照。<c>Compose</c> 只读内存与环境变量，不会抛。</summary>
    private void Rebuild()
    {
        if (_active is null)
        {
            // 没有当前模型：策略照旧生效（提示词与限额仍要能看、能改），但未配置
            _current = new AssistantRuntimeSnapshot(null, PolicyOnly(_parameters.Values))
            {
                ParameterRows = _parameters.Rows,
                Policy = _parameters.Values,
            };
            return;
        }

        var settings = Compose(_active, _parameters.Values);
        _current = new AssistantRuntimeSnapshot(
            new ResolvedAssistantModel(
                _active.Model.ModelId,
                _active.Model.DisplayName,
                _active.Model.ModelCode,
                _active.Provider.ProviderId,
                _active.Provider.Code,
                _active.Provider.DisplayName),
            settings)
        {
            ParameterRows = _parameters.Rows,
            Policy = _parameters.Values,
        };
    }

    /// <summary>只有全局策略、没有模型行时的配置（传输层字段取代码默认值，密钥为空即"未配置"）。</summary>
    private static AssistantSettings PolicyOnly(AssistantPolicyValues policy) => new()
    {
        EnableAutoDistill = policy.EnableAutoDistill,
        Cost = policy.Cost,
        SystemPrompt = policy.SystemPrompt,
    };

    /// <summary>
    /// 把"模型行 + 供应商行 + 全局策略"合成为一次调用要用的配置。
    ///
    /// <para>
    /// 传输层字段来自表行（端点 / 模型标识 / 超时 / 温度 / 最大输出 / 窗口 / 工具能力 / 单价）；
    /// 策略字段来自 3105 的参数（提示词 / 自动提炼 / 日上限 / 单价兜底 / 熔断）。
    /// 单价留空时由策略里的兜底价接手——这条退路在 <c>AssistantCost.Calculate</c> 里。
    /// </para>
    /// </summary>
    private AssistantSettings Compose(AssistantActiveModel active, AssistantPolicyValues policy)
    {
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
            // 以下来自 3105 的全局策略参数
            EnableAutoDistill = policy.EnableAutoDistill,
            Cost = policy.Cost,
            SystemPrompt = policy.SystemPrompt,
        };
    }
}

/// <summary>
/// 消费方用的 <see cref="IChatModel"/>：按快照里的配置构造真正的实现。
///
/// <para>
/// 注册为 **Scoped**（此前是 Singleton），两个原因：
/// ① **一次请求内只解析一次**——一条回答可能跑好几轮工具调用，若中途有人切换模型，同一条回答会
/// 跨两个模型，而落库的 <c>MODEL_NAME</c> 只有一个，审计与用量都会失真；
/// ② **跨请求读得到新快照**——所以切换模型不需要重启。
/// </para>
/// </summary>
public sealed class ResolvingChatModel(
    IHttpClientFactory httpClientFactory,
    IAssistantRuntimeConfig runtime) : IChatModel
{
    private AssistantSettings? _snapshot;
    private IChatModel? _inner;

    /// <summary>本次请求用的配置：取一次就固定，保证同一请求内的一致性与快照语义。</summary>
    private AssistantSettings Settings => _snapshot ??= runtime.Current.Settings;

    /// <summary>
    /// 按**协议**构造客户端实现：协议来自供应商 CODE 在预设目录里的声明，
    /// 所以"换一家 OpenAI 兼容的供应商"是纯数据操作，不必碰代码。
    /// 新增一种协议时，在这里加一个分支即可——判定入口只有这一处，
    /// 不会出现"某家供应商偷偷走了别的客户端"。
    /// </summary>
    private IChatModel Inner => _inner ??= Create(runtime.Current.Model?.ProviderCode);

    private IChatModel Create(string? providerCode) =>
        AssistantProviderCatalog.ProtocolOf(providerCode) switch
        {
            _ => new OpenAiCompatibleChatModel(httpClientFactory, Options.Create(Settings)),
        };

    /// <inheritdoc />
    public string ModelName => Settings.Model;

    /// <summary>
    /// 密钥非空 ⟺ 已有当前模型且那把密钥配好了：策略快照本身不带密钥，只有 <c>Compose</c> 会写进去。
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Settings.ApiKey);

    /// <inheritdoc />
    public IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken cancellationToken) => Inner.StreamAsync(messages, tools, cancellationToken);
}
