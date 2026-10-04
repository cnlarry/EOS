using EOS.API.Data;
using EOS.API.Data.Assistant;

namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// 一次嵌入调用要用的全部配置：端点在**供应商**、型号与维度在**模型**、密钥在环境变量
/// （库里只有变量名）、认证头样式与单请求条数在**预设目录**。
///
/// <para>
/// 这些字段**刻意不塞进 <c>AssistantSettings</c>**：那份配置是"对话"的（提示词 / 温度 / 工具能力），
/// 嵌入侧没有温度、没有工具、多的是维度与批量上限。合成一个对象会让"哪些字段对哪条链路有意义"
/// 变成读代码才知道的事。
/// </para>
/// </summary>
public sealed record AssistantEmbeddingConfig(
    string ModelId,
    string ProviderDisplayName,
    string BaseUrl,
    string? ApiKey,
    string ModelCode,
    int Dimension,
    AssistantAuthStyle AuthStyle,
    int TimeoutSeconds,
    int BatchMax);

/// <summary>
/// "当前该用哪个嵌入模型"的唯一入口（ADR-031 §3.1：嵌入模型独立于对话模型，各有一条"当前"）。
///
/// <para>
/// 为什么是"每次调用先解析"而不是把客户端注册进容器：当前嵌入模型是**库里的数据**，
/// 解析要读库（异步），而 <see cref="IEmbeddingModel"/> 的 <c>Dimension</c> 是同步属性——
/// 硬塞进属性里就得在同步上下文里阻塞等库，那是把问题藏起来而不是解决。
/// </para>
/// </summary>
public interface IAssistantEmbeddingResolver
{
    /// <summary>取当前嵌入模型。未配置时抛 <see cref="EmbeddingNotConfiguredException"/>（fail-closed）。</summary>
    Task<IEmbeddingModel> ResolveAsync(CancellationToken token);
}

/// <summary>
/// 解析当前嵌入模型的默认实现。**解析一次即缓存到本作用域**（Scoped）：
/// 一次请求里可能嵌入多个片段（入库一篇文档），不该每个片段都去查一次库。
/// 但**失败不缓存**——配置刚改好时不该因为上一次的失败继续报旧原因。
/// </summary>
public sealed class AssistantEmbeddingResolver(
    IAssistantModelCatalog catalog,
    IAssistantSecretStore secrets,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IAssistantEmbeddingResolver
{
    private readonly ILogger<AssistantEmbeddingResolver> _logger = loggerFactory.CreateLogger<AssistantEmbeddingResolver>();
    private IEmbeddingModel? _resolved;

    /// <inheritdoc />
    public async Task<IEmbeddingModel> ResolveAsync(CancellationToken token)
    {
        if (_resolved is not null)
        {
            return _resolved;
        }

        var active = await catalog.GetActiveAsync(AssistantModelKind.Embedding, token)
            ?? throw NotConfigured();
        // 无凭据端点（本机/内网自建，供应商行上没填变量名）：没有密钥可配，
        // 也不该被判成"没配好"——否则批 A 才放开的这类端点在这里又被挡回去
        var apiKey = RequiresKey(active.Provider) ? secrets.Read(active.Provider.ApiKeyEnvVar) : null;
        var config = Compose(active, apiKey);

        _logger.LogInformation("嵌入模型已就绪 model={Model} provider={Provider} dimension={Dimension} batchMax={Batch}",
            config.ModelId, active.Provider.Code, config.Dimension, config.BatchMax);

        _resolved = new OpenAiCompatibleEmbeddingModel(
            httpClientFactory, config, loggerFactory.CreateLogger<OpenAiCompatibleEmbeddingModel>());
        return _resolved;
    }

    /// <summary>没配嵌入模型时的拒答。抽成函数是为了让**文案**可被离线断言——它是用户唯一的线索。</summary>
    internal static EmbeddingNotConfiguredException NotConfigured() => new(
        "未配置嵌入模型（KB_EMBEDDING_NOT_CONFIGURED）——知识库的检索与入库都不可用。"
        + "请管理员在「工作助手管理 → 模型与用量」里，为某个供应商添加一个用途为「嵌入」的模型"
        + "（填好维度），把它设为当前，并确认该供应商的密钥已配置。");

    /// <summary>
    /// 这家供应商到底要不要密钥。判定同时看**认证样式**与**变量名是否留空**：
    /// 留空表示"这个端点不需要凭据"（迁移 307 放宽的那一格），把这种情况也当成"没配好"，
    /// 无凭据端点就永远用不起来。
    /// </summary>
    internal static bool RequiresKey(AssistantProviderRow provider) =>
        AssistantProviderCatalog.AuthStyleOf(provider.Code) != AssistantAuthStyle.None
        && !string.IsNullOrWhiteSpace(provider.ApiKeyEnvVar);

    /// <summary>
    /// 把"当前嵌入模型行 + 密钥"合成一次调用要用的配置。三种"没配好"都**在这里拒答**，
    /// 而且各说各的话：没配模型、模型没维度、密钥没配——处置完全不同（配模型 / 补维度 / 填密钥）。
    ///
    /// <para>纯函数：不读库、不读环境变量，所以三种拒答都能离线断言。</para>
    /// </summary>
    internal static AssistantEmbeddingConfig Compose(AssistantActiveModel active, string? apiKey)
    {
        var model = active.Model;
        // 维度缺失等于"不知道向量该多长"：写进集合会被列宽拒，检索也没法拼查询向量。
        // 3102 的新增/编辑已挡住新数据，这里兜的是历史行与被人手改过的行
        if (model.Dimension is not { } dimension || dimension <= 0)
        {
            throw new EmbeddingNotConfiguredException(
                $"嵌入模型「{model.DisplayName}」没有填维度，无法使用：维度决定向量占多宽，"
                + "必须与知识库集合登记的维度一致。请在「工作助手管理 → 模型与用量」编辑该模型并补上维度。");
        }

        if (RequiresKey(active.Provider) && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new EmbeddingNotConfiguredException(
                $"嵌入模型「{model.DisplayName}」的密钥还没配：供应商「{active.Provider.DisplayName}」"
                + $"的环境变量 {active.Provider.ApiKeyEnvVar} 是空的。请管理员在「工作助手管理 → 模型与用量」"
                + "里对该供应商点「密钥」填一次（密钥只写不读、不进数据库）。");
        }

        return new AssistantEmbeddingConfig(
            ModelId: model.ModelCode,
            ProviderDisplayName: active.Provider.DisplayName,
            BaseUrl: active.Provider.BaseUrl,
            ApiKey: apiKey,
            ModelCode: model.ModelCode,
            Dimension: dimension,
            AuthStyle: AssistantProviderCatalog.AuthStyleOf(active.Provider.Code),
            // 模型行可覆盖供应商的默认超时（与对话同一条口径）
            TimeoutSeconds: model.TimeoutSeconds ?? active.Provider.TimeoutSeconds,
            BatchMax: AssistantProviderCatalog.EmbeddingBatchMaxOf(active.Provider.Code));
    }
}
