using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EOS.API.Controllers;

/// <summary>
/// 工作助手**管理侧**端点（菜单组 31 / 模块 3101，见 ADR-030）。
///
/// <para>
/// 与 <see cref="AssistantController"/>（个人侧）的分工：那一侧所有数据按 <c>USER_ID</c> 强制隔离，
/// 这一侧面向管理员、能看全系统会话，因此**单独一套端点与仓储**——把跨用户能力塞进个人侧会
/// 破坏它"越权一律看不到"的契约。
/// </para>
///
/// <para>
/// **只看元数据，不看正文**（ADR-030 §2）：这里没有"取某会话的消息"这种端点。要看正文是另一个
/// 需要独立权限位与审计留痕的决定。
/// </para>
///
/// <para>
/// 权限门：读 = <c>CanBrowse(3101)</c>，归档/删除 = <c>CanEdit(3101)</c>。
/// </para>
/// </summary>
[ApiController, Authorize, Route("api/v1/admin/assistant")]
public sealed class AssistantAdminController(
    IAssistantAdminRepository repository,
    CurrentUserContext userContext,
    ModuleRightsRepository rightsRepository,
    IEnumerable<Features.Assistant.Tools.IAssistantTool> assistantTools,
    IKnowledgeRepository knowledge,
    IAssistantModelCatalog modelCatalog,
    SystemParameterService parameters,
    AssistantParameterScopeStore scopeStore,
    IAssistantSecretStore modelSecrets,
    AssistantRuntimeRegistry registry,
    IAssistantUsageRepository usageRepository) : ControllerBase
{
    /// <summary>
    /// 受支持的供应商 CODE。**真源是代码里的预设目录**（<see cref="AssistantProviderCatalog"/>），
    /// 不是这里的白名单：库里存的是 CODE，而 CODE 决定了用哪个客户端实现，所以"目录里没有的 CODE"
    /// 存下来只会是一个没人认得的字符串。
    ///
    /// <para>
    /// 它们都走 OpenAI 兼容的 <c>/chat/completions</c>，因此共用一个客户端实现；将来接入线协议不同的
    /// 厂商 = 新增一个 <see cref="IChatModel"/> 实现 + 在目录里登记一行。
    /// </para>
    /// </summary>
    private static bool IsSupportedProvider(string? code) => AssistantProviderCatalog.IsSupported(code);

    /// <summary>
    /// 能力面的边界。写在服务端而不是让前端硬编码：这些是"能力面上根本表达不出来"的东西，
    /// 摊在总览页上是为了让"助手不能做什么"与"能做什么"同样可见。
    /// </summary>
    private static readonly object[] MechanismBoundaries =
    [
        new
        {
            title = "权限授予类配置不可代劳",
            detail = "分权与授权（用户、用户组、按钮权限）在能力面上不存在：动作枚举里没有对应成员，"
                + "写入口也不在可触达的仓储方法集合里（结构断言见 NoPrivilegeConfigCallTests）。",
        },
        new
        {
            title = "批核族不可代理",
            detail = "批核 / 解批 / 结案 / 取消结案不在可代理动作里：助手只准备「操作请求卡」，"
                + "真正执行由界面直接调既有端点。",
        },
        new
        {
            title = "会话正文不开放给管理面",
            detail = "菜单组 31 的会话管理只给元数据，不提供对话正文（ADR-030 §2）；"
                + "真要开放，应当是「独立权限位 ＋ 审计留痕」的另一个决定。",
        },
        new
        {
            title = "密钥不入库",
            detail = "模型密钥只以环境变量名入库，密钥本身不落库、不下发前端、不进诊断包（ADR-030 §3）。",
        },
    ];

    /// <summary>归档 / 取消归档。<c>Archived</c> 缺省视为 <c>true</c>——空 body 不该把会话"取消归档"。</summary>
    public sealed record ArchiveSessionRequest(bool? Archived);

    /// <summary>
    /// 新增 / 修改模型的入参。
    ///
    /// <para>
    /// **这里刻意没有密钥字段**：密钥只能走单独那条 <c>PUT models/{id}/key</c>，而那条路不进库。
    /// 把密钥混进"新增模型"的 body 里，早晚会有人顺手把它存下来——ADR-030 §3 的"密钥不入库"
    /// 就会从一条规则退化成一个愿望。
    /// </para>
    /// </summary>
    /// <summary>
    /// 新增 / 修改**供应商**（端点 + 密钥环境变量名 + 默认超时）。
    ///
    /// <para>
    /// <c>Models</c> 是可选的"顺带添加"：界面上"选供应商 → 勾选可用模型"是**一次**提交，走这一个
    /// 请求体。否则要先建供应商、再发 N 个建模型的请求，中间失败会留下半个供应商。
    /// </para>
    ///
    /// <remarks>
    /// 超时/启用/排序写成可空：客户端少传一个字段时应当落到默认值（300 秒 / 启用 / 0），
    /// 而不是悄悄变成"0 秒超时"。校验只对**传了**的值生效。
    /// </remarks>
    /// </summary>
    public sealed record AssistantProviderRequest(
        string? Code,
        string? DisplayName,
        string? BaseUrl,
        string? ApiKeyEnvVar,
        int? TimeoutSeconds,
        bool? Enabled,
        int? SortIdx,
        string? Remark,
        IReadOnlyList<AssistantModelRequest>? Models);

    /// <summary>
    /// 新增 / 修改**模型**。
    ///
    /// <para>
    /// **这里刻意没有密钥字段**：密钥挂在供应商上，且只能走单独那条 <c>PUT providers/{id}/key</c>，
    /// 而那条路不进库。把密钥混进"新增模型"的 body 里，早晚会有人顺手把它存下来——ADR-030 §3 的
    /// "密钥不入库"就会从一条规则退化成一个愿望。
    /// </para>
    /// </summary>
    public sealed record AssistantModelRequest(
        int? ProviderId,
        string? ModelCode,
        string? DisplayName,
        int? ContextWindow,
        int? MaxOutputTokens,
        decimal? DefaultTemperature,
        int? TimeoutSeconds,
        decimal? InputPerMillionYuan,
        decimal? OutputPerMillionYuan,
        bool? SupportsTools,
        bool? Enabled,
        int? SortIdx,
        string? Remark);

    /// <summary>写入密钥的入参。只进不出——任何端点都不会把它读回来。</summary>
    public sealed record AssistantModelKeyRequest(string? ApiKey);

    /// <summary>写回一个设置项的入参。**空值 = 恢复默认**（删掉覆盖行）。</summary>
    public sealed record AssistantSettingRequest(string? Value);

    /// <summary>一层作用域覆盖的写入请求（3015 页面的「按用户/模块覆盖」）。</summary>
    public sealed record AssistantScopeRequest(string? ScopeType, string? ScopeKey, string? ParamKey, string? Value);

    /// <summary>
    /// 跨用户分页列会话（元数据）。<paramref name="state"/> 三态、<paramref name="owner"/> 按归属用户筛、
    /// <paramref name="sortBy"/><paramref name="sortDir"/> 排序。
    ///
    /// <para>
    /// **排序必须由服务端做**：列表是服务端分页的，在前端排只会排到当前这一页（看着"排了"其实是错的）。
    /// </para>
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions(
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? state = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? owner = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        CancellationToken token = default)
    {
        if (!await CanBrowse(token)) return Forbid();
        var (items, total) = await repository.ListSessionsAsync(
            offset, limit, ParseState(state), keyword, owner,
            ParseSort(sortBy), string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase), token);
        return Ok(new { items, total });
    }

    /// <summary>出现过的归属用户（管理页"按用户筛选"的下拉）。</summary>
    [HttpGet("sessions/owners")]
    public async Task<IActionResult> ListOwners(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.ListOwnersAsync(token));
    }

    [HttpPut("sessions/{sessionId:long}/archive")]
    public async Task<IActionResult> ArchiveSession(long sessionId, [FromBody] ArchiveSessionRequest? request, CancellationToken token)
    {
        if (!await CanEdit(token)) return Forbid();
        var updated = await repository.ArchiveSessionAsync(sessionId, request?.Archived ?? true, token);
        return updated > 0 ? NoContent() : NotFound();
    }

    /// <summary>
    /// 永久删除会话（连消息，不可恢复）。**只对已归档会话生效**（与个人侧同一口径）：
    /// 仓储 SQL 里带 <c>ARCHIVED_AT IS NOT NULL</c>，所以"还在在列的会话"删不掉。
    /// </summary>
    [HttpDelete("sessions/{sessionId:long}")]
    public async Task<IActionResult> DeleteSession(long sessionId, CancellationToken token)
    {
        if (!await CanEdit(token)) return Forbid();
        var deleted = await repository.DeleteSessionAsync(sessionId, token);
        if (deleted > 0) return NoContent();
        // 没删掉有两种可能，这里分不出来（受影响行数为 0），所以如实把两种都告诉调用方
        return Conflict(ApiProblem.Create(
            StatusCodes.Status409Conflict, "NOT_DELETABLE",
            "会话不存在，或它还在「在列」——只有已归档的会话可以永久删除。"));
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.SessionAdmin, token)).CanBrowse;

    private async Task<bool> CanEdit(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.SessionAdmin, token)).CanEdit;

    /// <summary>
    /// 机制与工具总览（只读）：当前挂着的工具、可选动作清单与能力面边界。权限门 3104 的 CanBrowse。
    ///
    /// <para>
    /// 数据全部**现算**（工具来自 DI 注册表、动作来自静态目录），不落库也不缓存：
    /// 这份清单的意义就是"代码里现在到底是什么"，缓存反而会让它说谎。
    /// </para>
    /// </summary>
    [HttpGet("mechanism")]
    public async Task<IActionResult> GetMechanism(CancellationToken token)
    {
        if (!await CanBrowseMechanism(token)) return Forbid();
        return Ok(new
        {
            tools = assistantTools
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => new
                {
                    name = item.Name,
                    risk = item.Risk.ToString(),
                    description = item.Description,
                    parametersJson = item.ParametersJson,
                }),
            actions = Features.Assistant.Actions.AssistantActionRegistry.All,
            boundaries = MechanismBoundaries,
        });
    }

    /// <summary>知识库集合清单（管理面只读）。权限门 3103 的 CanBrowse。</summary>
    [HttpGet("kb/collections")]
    public async Task<IActionResult> ListKbCollections(CancellationToken token)
    {
        if (!await CanBrowseKb(token)) return Forbid();
        return Ok(await knowledge.ListCollectionsAsync(token));
    }

    /// <summary>
    /// 某集合下的文档清单。<paramref name="includeDeleted"/> 为真时连墓碑一起给
    /// —— 知识库现在基本是空的，"看到墓碑"恰恰说明这里曾经有过什么。
    /// </summary>
    [HttpGet("kb/documents")]
    public async Task<IActionResult> ListKbDocuments(
        [FromQuery] string? collectionId,
        [FromQuery] bool includeDeleted = false,
        CancellationToken token = default)
    {
        if (!await CanBrowseKb(token)) return Forbid();
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "缺少集合编号。"));
        }
        return Ok(await knowledge.ListDocumentsAsync(collectionId.Trim(), includeDeleted, token));
    }

    /// <summary>
    /// 删除知识库文档（软删墓碑 + 物理移除向量）。权限门 3103 的 CanEdit。
    ///
    /// <para>
    /// **管理面只开放删除，不开放入库**：入库要过敏感扫描与业务引用复核（见 <c>KbController</c>），
    /// 那套门锚在 2302。在这里另开一个入库入口会绕过那条链路，所以不做——要入库仍走原先的通道。
    /// </para>
    /// </summary>
    [HttpDelete("kb/documents/{docId:long}")]
    public async Task<IActionResult> DeleteKbDocument(long docId, CancellationToken token)
    {
        if (!await CanEditKb(token)) return Forbid();
        var deleted = await knowledge.DeleteDocumentAsync(docId, token);
        return deleted
            ? NoContent()
            : NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "文档不存在。"));
    }

    // ==================================================================
    // 3102 模型与用量（见 ADR-030 §3）：两级 —— **供应商**（端点 + 密钥变量名 + 默认超时）
    // 与**模型**（模型标识 + 上下文窗口 + 单价 + 工具能力）。
    // 密钥只在 PUT providers/{id}/key 那一条路上出现过，且**只写不读**。
    // ==================================================================

    /// <summary>
    /// 预设目录：系统"知道"的主流供应商及其可用模型（端点、上下文窗口、最大输出、是否支持工具）。
    ///
    /// <para>
    /// 放代码里而不是写种子数据：表的语义是"用户配了什么"，目录是"系统知道什么"。界面上
    /// "选供应商 → 自动罗列可用模型 → 勾选落库"靠的就是它，所以它**必须能被前端读到**。
    /// </para>
    /// </summary>
    [HttpGet("presets")]
    public async Task<IActionResult> ListPresets(CancellationToken token)
    {
        if (!await CanBrowseModels(token)) return Forbid();
        return Ok(AssistantProviderCatalog.All.Select(provider => new
        {
            code = provider.Code,
            displayName = provider.DisplayName,
            baseUrl = provider.BaseUrl,
            suggestedApiKeyEnvVar = provider.SuggestedApiKeyEnvVar,
            timeoutSeconds = provider.TimeoutSeconds,
            remark = provider.Remark,
            models = provider.Models.Select(model => new
            {
                modelCode = model.ModelCode,
                displayName = model.DisplayName,
                contextWindow = model.ContextWindow,
                maxOutputTokens = model.MaxOutputTokens,
                supportsTools = model.SupportsTools,
                inputPerMillionYuan = model.InputPerMillionYuan,
                outputPerMillionYuan = model.OutputPerMillionYuan,
                defaultTemperature = model.DefaultTemperature,
                remark = model.Remark,
            }),
        }));
    }

    /// <summary>
    /// 供应商 + 其下模型（一次给全，界面两级渲染，避免展开一行就发一次请求）。
    ///
    /// <para>
    /// **不含任何密钥本体**，只给"环境变量名 + 是否已配置 + 掩码末四位"。另外给出"现在到底在用哪个"
    /// ——<c>current</c> 为 <c>null</c> 就是**尚未配置**：界面上必须能一眼看出这件事，
    /// 而不是等用户打完字发送才报错。
    /// </para>
    /// </summary>
    [HttpGet("providers")]
    public async Task<IActionResult> ListProviders(CancellationToken token)
    {
        if (!await CanBrowseModels(token)) return Forbid();
        var providers = await modelCatalog.ListProvidersAsync(token);
        var models = await modelCatalog.ListModelsAsync(token);
        var snapshot = registry.Current;
        var active = snapshot.Model;

        return Ok(new
        {
            providers = providers.Select(provider => new
            {
                providerId = provider.ProviderId,
                code = provider.Code,
                displayName = provider.DisplayName,
                baseUrl = provider.BaseUrl,
                apiKeyEnvVar = provider.ApiKeyEnvVar,
                apiKeyConfigured = modelSecrets.IsConfigured(provider.ApiKeyEnvVar),
                // 只露末四位：够确认"配的是哪一个"，不足以还原密钥
                apiKeyMaskedTail = modelSecrets.MaskedTail(provider.ApiKeyEnvVar),
                timeoutSeconds = provider.TimeoutSeconds,
                enabled = provider.Enabled,
                sortIdx = provider.SortIdx,
                remark = provider.Remark,
                models = models.Where(row => row.ProviderId == provider.ProviderId).Select(row => new
                {
                    modelId = row.ModelId,
                    providerId = row.ProviderId,
                    modelCode = row.ModelCode,
                    displayName = row.DisplayName,
                    contextWindow = row.ContextWindow,
                    maxOutputTokens = row.MaxOutputTokens,
                    defaultTemperature = row.DefaultTemperature,
                    timeoutSeconds = row.TimeoutSeconds,
                    inputPerMillionYuan = row.InputPerMillionYuan,
                    outputPerMillionYuan = row.OutputPerMillionYuan,
                    supportsTools = row.SupportsTools,
                    isActive = row.IsActive,
                    enabled = row.Enabled,
                    sortIdx = row.SortIdx,
                    remark = row.Remark,
                }),
            }),
            current = active is null ? null : new
            {
                modelId = active.ModelId,
                displayName = active.DisplayName,
                modelCode = active.ModelCode,
                providerId = active.ProviderId,
                providerCode = active.ProviderCode,
                providerDisplayName = active.ProviderDisplayName,
                contextWindow = snapshot.Settings.ContextWindow,
                timeoutSeconds = snapshot.Settings.TimeoutSeconds,
                supportsTools = snapshot.Settings.SupportsTools,
                apiKeyConfigured = snapshot.IsConfigured,
            },
        });
    }

    /// <summary>
    /// 新增供应商（可顺带批量添加它的模型）。密钥不在这个 body 里，要另外用
    /// <c>PUT providers/{id}/key</c> 写一次。
    /// </summary>
    [HttpPost("providers")]
    public async Task<IActionResult> CreateProvider(
        [FromBody] AssistantProviderRequest? request, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        if (request is null)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "缺少请求体。"));
        }

        var error = ValidateProvider(request);
        if (error is not null)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", error));
        }

        // 先把要顺带添加的模型**全部校验一遍**再落库：否则第 3 个模型参数不对时，
        // 供应商已经建好了，留下一个半截的配置要人手工收拾
        var modelWrites = new List<AssistantModelWrite>();
        foreach (var model in request.Models ?? [])
        {
            var modelError = ValidateModel(model);
            if (modelError is not null)
            {
                return BadRequest(ApiProblem.Create(
                    StatusCodes.Status400BadRequest, "INVALID_ARGUMENT",
                    $"模型「{model.ModelCode ?? "未命名"}」：{modelError}"));
            }

            modelWrites.Add(ToModelWrite(model, providerId: 0));
        }

        var write = ToProviderWrite(request);
        // CODE 上有唯一约束（它决定用哪个客户端实现）。撞上去原本会抛 SqlException → 500 与一句
        // 看不懂的英文，所以在这里先挡一次，把"为什么不能加"说清楚。
        var existing = await modelCatalog.ListProvidersAsync(token);
        if (existing.Any(item => string.Equals(item.Code, write.Code, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(ApiProblem.Create(
                StatusCodes.Status409Conflict, "PROVIDER_CODE_EXISTS",
                $"已经有一个「{write.Code}」供应商了。CODE 决定用哪个客户端实现，所以同类型只能有一个接入点；"
                + "要用另一把密钥或另一个端点，请先删掉已有的那一个，或改用「自定义」。"));
        }

        var providerId = await modelCatalog.CreateProviderAsync(write, userContext.UserId, token);
        var created = 0;
        if (modelWrites.Count > 0)
        {
            // 供应商主键要等落库之后才有，所以这里补上再批量插
            created = await modelCatalog.CreateModelsAsync(
                [.. modelWrites.Select(write => write with { ProviderId = providerId })],
                userContext.UserId, token);
        }

        return Ok(new { providerId, modelsCreated = created });
    }

    /// <summary>新增单个模型（挂到某个已有供应商下）。</summary>
    [HttpPost("models")]
    public async Task<IActionResult> CreateModel([FromBody] AssistantModelRequest? request, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        if (request is null)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "缺少请求体。"));
        }

        if (request.ProviderId is not { } providerId)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "缺少供应商。"));
        }

        if (await modelCatalog.GetProviderAsync(providerId, token) is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "供应商不存在。"));
        }

        var error = ValidateModel(request);
        if (error is not null)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", error));
        }

        var modelId = await modelCatalog.CreateModelAsync(
            ToModelWrite(request, providerId), userContext.UserId, token);
        return Ok(new { modelId });
    }

    /// <summary>修改供应商（端点 / 密钥变量名 / 默认超时 / 启用）。改完要让快照跟着变。</summary>
    [HttpPut("providers/{providerId:int}")]
    public async Task<IActionResult> UpdateProvider(
        int providerId, [FromBody] AssistantProviderRequest? request, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        if (request is null)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "缺少请求体。"));
        }

        var error = ValidateProvider(request);
        if (error is not null)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", error));
        }

        var write = ToProviderWrite(request);
        // 改 CODE 等同于换客户端实现，同样要防撞唯一约束（否则又是一个 500 而不是一句说明）
        var others = await modelCatalog.ListProvidersAsync(token);
        if (others.Any(item => item.ProviderId != providerId
            && string.Equals(item.Code, write.Code, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(ApiProblem.Create(
                StatusCodes.Status409Conflict, "PROVIDER_CODE_EXISTS",
                $"已经有一个「{write.Code}」供应商了，不能把这一条也改成它。"));
        }

        var updated = await modelCatalog.UpdateProviderAsync(
            providerId, write, userContext.UserId, token);
        if (!updated)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "供应商不存在。"));
        }

        // 端点/超时/启用都可能被改到，而它们直接决定正在用的那个模型怎么发请求：
        // 不刷新的话，界面显示"改好了"，实际还在用旧的值发。
        await registry.RefreshAsync(token);
        return NoContent();
    }

    /// <summary>
    /// 写入供应商密钥：写进**环境变量**（进程级立即生效 + 用户级持久化），**数据库里只留变量名**。
    /// 成功后返回掩码末四位，供界面确认"配上了"。
    ///
    /// <para>
    /// 密钥挂在**供应商**上：一次配置，该供应商名下所有模型共用——这也是一把密钥的真实用法。
    /// </para>
    /// </summary>
    [HttpPut("providers/{providerId:int}/key")]
    public async Task<IActionResult> SetProviderKey(
        int providerId, [FromBody] AssistantModelKeyRequest? request, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        var provider = await modelCatalog.GetProviderAsync(providerId, token);
        if (provider is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "供应商不存在。"));
        }

        if (string.IsNullOrWhiteSpace(request?.ApiKey))
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "密钥不能为空。"));
        }

        var (processUpdated, persisted) = modelSecrets.Write(provider.ApiKeyEnvVar, request.ApiKey.Trim());
        // 换密钥必须重建快照：否则进程内还拿着旧密钥，表现是"界面说配好了，聊天却 401"
        await registry.RefreshAsync(token);
        return Ok(new
        {
            envVar = provider.ApiKeyEnvVar,
            configured = modelSecrets.IsConfigured(provider.ApiKeyEnvVar),
            maskedTail = modelSecrets.MaskedTail(provider.ApiKeyEnvVar),
            processUpdated,
            // 用户级写入在部分环境会失败（受限账户 / 平台不支持）。那时只有当前进程生效，
            // 不能假装持久化成功——如实告诉调用方，界面才能提示"重启后需重设"。
            persisted,
        });
    }

    /// <summary>
    /// 删除供应商。**名下还有模型就删不掉**：级联删会一次带走整家供应商的配置，手滑代价太大。
    /// </summary>
    [HttpDelete("providers/{providerId:int}")]
    public async Task<IActionResult> DeleteProvider(int providerId, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        var provider = await modelCatalog.GetProviderAsync(providerId, token);
        if (provider is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "供应商不存在。"));
        }

        var modelCount = await modelCatalog.CountModelsAsync(providerId, token);
        if (modelCount > 0)
        {
            return Conflict(ApiProblem.Create(
                StatusCodes.Status409Conflict, "PROVIDER_HAS_MODELS",
                $"该供应商下还有 {modelCount} 个模型，请先删除它们再删供应商。"));
        }

        await modelCatalog.DeleteProviderAsync(providerId, token);
        return NoContent();
    }

    /// <summary>修改模型（不含密钥，也不改"是否当前"——那是单独的动作）。</summary>
    [HttpPut("models/{modelId:int}")]
    public async Task<IActionResult> UpdateModel(
        int modelId, [FromBody] AssistantModelRequest? request, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        if (request is null)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "缺少请求体。"));
        }

        var row = await modelCatalog.GetModelAsync(modelId, token);
        if (row is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "模型不存在。"));
        }

        // 没传 ProviderId 就沿用原供应商：编辑模型时前端不必重复回传它
        var providerId = request.ProviderId ?? row.ProviderId;
        if (await modelCatalog.GetProviderAsync(providerId, token) is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "供应商不存在。"));
        }

        var error = ValidateModel(request);
        if (error is not null)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", error));
        }

        var updated = await modelCatalog.UpdateModelAsync(
            modelId, ToModelWrite(request, providerId), userContext.UserId, token);
        if (!updated)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "模型不存在。"));
        }

        // 改的若是当前这一行（模型标识/窗口/输出/温度/超时/单价…），必须让快照跟着变，
        // 否则界面显示改了、实际还在用旧的
        await registry.RefreshAsync(token);
        return NoContent();
    }

    /// <summary>
    /// 设为当前模型。**要求该供应商的密钥已配置**：把一个没有密钥的模型设成当前，会让所有人的助手
    /// 立刻不可用，而界面上看不出原因——所以在这里挡住，并说清先做哪一步。
    /// </summary>
    [HttpPost("models/{modelId:int}/activate")]
    public async Task<IActionResult> ActivateModel(int modelId, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        var row = await modelCatalog.GetModelAsync(modelId, token);
        if (row is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "模型不存在。"));
        }

        var provider = await modelCatalog.GetProviderAsync(row.ProviderId, token);
        if (provider is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "模型所属的供应商不存在。"));
        }

        if (!row.Enabled || !provider.Enabled)
        {
            return Conflict(ApiProblem.Create(
                StatusCodes.Status409Conflict, "MODEL_DISABLED",
                row.Enabled ? "该模型所属的供应商已停用，请先启用供应商。" : "已停用的模型不能设为当前，请先启用它。"));
        }

        if (!modelSecrets.IsConfigured(provider.ApiKeyEnvVar))
        {
            return Conflict(ApiProblem.Create(
                StatusCodes.Status409Conflict, "MODEL_KEY_NOT_CONFIGURED",
                $"环境变量 {provider.ApiKeyEnvVar}（供应商「{provider.DisplayName}」的密钥）还没有值。"
                + "请先用「设置密钥」填一次，再设为当前——否则整个助手的模型调用会立刻失败。"));
        }

        if (!await modelCatalog.ActivateModelAsync(modelId, userContext.UserId, token))
        {
            return Conflict(ApiProblem.Create(StatusCodes.Status409Conflict, "ACTIVATE_FAILED", "切换失败，请刷新后重试。"));
        }

        await registry.RefreshAsync(token);
        return NoContent();
    }

    /// <summary>取消当前模型：助手回到**未配置**状态（供应商与模型都留着，只是没有"当前"）。</summary>
    [HttpPost("models/active/clear")]
    public async Task<IActionResult> ClearActiveModel(CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        await modelCatalog.ClearActiveModelAsync(token);
        await registry.RefreshAsync(token);
        return NoContent();
    }

    /// <summary>删除模型。**当前模型删不掉**：删掉它会让助手在无人察觉的情况下变成"未配置"。</summary>
    [HttpDelete("models/{modelId:int}")]
    public async Task<IActionResult> DeleteModel(int modelId, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        var row = await modelCatalog.GetModelAsync(modelId, token);
        if (row is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "模型不存在。"));
        }

        if (row.IsActive)
        {
            return Conflict(ApiProblem.Create(
                StatusCodes.Status409Conflict, "MODEL_IN_USE", "正在使用这个模型，不能删除；请先切换到别的模型或取消当前。"));
        }

        await modelCatalog.DeleteModelAsync(modelId, token);
        return NoContent();
    }

    /// <summary>
    /// 用量：近 N 天**按模型**与**按天**的聚合 + 当日总览。权限门 3102 的 CanBrowse。
    ///
    /// <para>
    /// 按模型这一档此前根本没有：<c>ASSISTANT_MESSAGE.MODEL_NAME</c> 一直是只写不聚合，
    /// 于是"换了模型之后用量怎么变的"无从回答。
    /// </para>
    /// </summary>
    [HttpGet("usage")]
    public async Task<IActionResult> GetModelUsage([FromQuery] int days = 30, CancellationToken token = default)
    {
        if (!await CanBrowseModels(token)) return Forbid();
        var window = Math.Clamp(days, 1, 180);
        var todayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var since = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-(window - 1)), TimeSpan.Zero);
        var cost = registry.Current.Settings.Cost;
        // 按**模型单价**计价：看板上的成本要和台账里实际扣掉的钱用同一套口径。两边对不上，
        // 正是"配了单价却不消费"最容易露出来的样子。
        var priceByCode = (await modelCatalog.ListModelsAsync(token)).ToDictionary(
            row => row.ModelCode, StringComparer.OrdinalIgnoreCase);

        var models = await usageRepository.GetPerModelUsageAsync(since, token);
        var trend = await usageRepository.GetDailyTrendAsync(since, token);
        var today = await usageRepository.GetGlobalDailyUsageAsync(todayStart, token);

        return Ok(new
        {
            days = window,
            since,
            today = new
            {
                requests = today.Requests,
                promptTokens = today.PromptTokens,
                completionTokens = today.CompletionTokens,
                estimatedCostYuan = Math.Round(AssistantCost.Calculate(today.PromptTokens, today.CompletionTokens, cost), 4),
            },
            models = models.Select(entry =>
            {
                // 落库的 MODEL_NAME 就是模型标识，所以能按它找回那一行的单价；
                // 找不回（模型已被删/换过名）就用全局兜底价，不能让成本显示成 0。
                priceByCode.TryGetValue(entry.ModelName, out var price);
                return new
                {
                    modelName = entry.ModelName,
                    requests = entry.Usage.Requests,
                    promptTokens = entry.Usage.PromptTokens,
                    completionTokens = entry.Usage.CompletionTokens,
                    estimatedCostYuan = Math.Round(AssistantCost.Calculate(
                        entry.Usage.PromptTokens, entry.Usage.CompletionTokens, cost,
                        price?.InputPerMillionYuan, price?.OutputPerMillionYuan), 4),
                    lastUsedAt = entry.LastUsedAt,
                };
            }),
            trend = trend.Select(entry => new
            {
                day = entry.Day,
                requests = entry.Usage.Requests,
                promptTokens = entry.Usage.PromptTokens,
                completionTokens = entry.Usage.CompletionTokens,
                estimatedCostYuan = Math.Round(AssistantCost.Calculate(entry.Usage.PromptTokens, entry.Usage.CompletionTokens, cost), 4),
            }),
            caps = new
            {
                userDailyCapYuan = cost.UserDailyCapYuan,
                globalDailyCapYuan = cost.GlobalDailyCapYuan,
            },
        });
    }

    // ==================================================================
    // 3105 助手设置（见 ADR-030 §8）：全局策略参数。
    // 参数的**声明**在代码（AssistantParameterCatalog），**取值**在 dbo.SYSSS 的 OWNER_MODULE = 3105；
    // 两者按批同步生长。读写都经 SystemParameterService —— 参数只有一个存储、一套类型校验与审计路径
    // （§4.3：端点仍挂在 3105 自己这里，写门是 CanSetup，不走通用设置端点那条 CanEdit 的通道）。
    // ==================================================================

    /// <summary>
    /// 设置项清单，**按域分组**。每一项都带上代码里的默认值、单位、取值范围与读取方，
    /// 界面因此能显示"改过没有、默认是多少、谁在读它、填错了会怎样"。
    ///
    /// <para>
    /// 另外把 <c>problems</c> 一并给出：库里的值解析不了时（有人手改过库、或升级后格式变了），
    /// 界面要能**当场看见**它被忽略了——而不是显示着 5 元、实际按默认值跑，谁也不知道为什么。
    /// </para>
    /// </summary>
    [HttpGet("settings")]
    public async Task<IActionResult> ListSettings(CancellationToken token)
    {
        if (!await CanBrowseSettings(token)) return Forbid();

        var list = await parameters.ListAsync(AssistantParameterCatalog.OwnerModule, token);
        var rows = list.Groups.SelectMany(group => group.Parameters).ToList();
        var byKey = rows.ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);

        return Ok(new
        {
            groups = AssistantParameterCatalog.Groups.Select(group => new
            {
                code = group.Code,
                label = group.Label,
                seq = group.Seq,
            }),
            items = AssistantParameterCatalog.All.Select(descriptor =>
            {
                byKey.TryGetValue(descriptor.Key, out var row);
                return new
                {
                    key = descriptor.Key,
                    displayName = descriptor.DisplayName,
                    groupCode = descriptor.Group,
                    groupLabel = AssistantParameterCatalog.FindGroup(descriptor.Group)?.Label,
                    seqNo = AssistantParameterCatalog.SeqNoOf(descriptor),
                    valueType = descriptor.ValueType,
                    unit = descriptor.Unit,
                    description = descriptor.Description,
                    rangeHint = AssistantParameterCatalog.DescribeRange(descriptor),
                    defaultValue = descriptor.DefaultValue,
                    consumers = descriptor.Consumers,
                    // value 为 null = 没覆盖过，用的就是默认值
                    value = row?.Value,
                    isOverridden = row?.Value is not null,
                    updatedAt = row?.UpdatedAt,
                    updatedBy = row?.UpdatedBy,
                };
            }),
            problems = AssistantParameterResolver.Interpret(rows).Problems,
        });
    }

    /// <summary>
    /// 写回一个设置项。**空值 = 恢复默认**：把取值列清空（而不是存一个空串）——
    /// 存空串会让默认值永远拿不回来。
    /// </summary>
    [HttpPut("settings/{key}")]
    public async Task<IActionResult> UpdateSetting(
        string key, [FromBody] AssistantSettingRequest? request, CancellationToken token)
    {
        if (!await CanSetupSettings(token)) return Forbid();
        return await SaveSettingAsync(key, request?.Value, token);
    }

    /// <summary>恢复默认：清空取值，回到代码默认值（标量参数则回到 <c>DEFAULT_VALUE</c>）。</summary>
    [HttpDelete("settings/{key}")]
    public async Task<IActionResult> ResetSetting(string key, CancellationToken token)
    {
        if (!await CanSetupSettings(token)) return Forbid();
        return await SaveSettingAsync(key, null, token);
    }

    /// <summary>
    /// 保存与恢复默认的公共路径。两步：
    /// ① 用**参数目录的解析器**校验取值（与生效时同一份实现，所以"保存时通过、生效时被忽略"不可能发生）；
    /// ② 经 <see cref="SystemParameterService.SaveAsync"/> 落库并留痕——类型与归属由它再挡一次。
    /// </summary>
    private async Task<IActionResult> SaveSettingAsync(string key, string? value, CancellationToken token)
    {
        var descriptor = AssistantParameterCatalog.Find(key);
        if (descriptor is null)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "未知的设置项。"));
        }

        if (!AssistantParameterCatalog.TryNormalize(descriptor, value, out var normalized, out var problem))
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", $"{descriptor.DisplayName}：{problem}。"));
        }

        // 目录不许出现的键（红线 / 凭据）在这里也拦一次：正常路径到不了，但"到不了"要靠代码而不是靠界面
        if (AssistantParameterCatalog.RedLineKeys.Contains(descriptor.Key)
            || AssistantParameterCatalog.CredentialKeys.Contains(descriptor.Key))
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "该键不是可写的助手参数。"));
        }

        var result = await parameters.SaveAsync(
            AssistantParameterCatalog.OwnerModule,
            new Dictionary<string, string?> { [descriptor.Key] = normalized },
            userContext.UserId,
            token);

        if (result.Errors.Count > 0)
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", string.Join("；", result.Errors)));
        }

        // 改完立刻重建快照：否则就成了"保存了但要等重启"——那正是 §8 要消掉的体验
        await registry.RefreshAsync(token);
        return NoContent();
    }

    // ==================================================================
    // 3105 助手设置 → **作用域覆盖**（ADR-030 §6.2）：把某一层的值压到全局之上。
    // 优先级 用户 > 模块 > 全局；"哪条参数可被覆盖、出现在哪些层、能否往那个方向走"
    // 全部由参数目录与 AssistantParameterScopeRules 判定——**保存与生效共用那一份判断**。
    // ==================================================================

    /// <summary>
    /// 当前的覆盖清单 + **哪些参数允许被覆盖、允许出现在哪些层**。
    /// 可选项由服务端给出，界面不自己判断"这条能不能覆盖"。
    /// </summary>
    [HttpGet("settings/scopes")]
    public async Task<IActionResult> ListScopes(CancellationToken token)
    {
        if (!await CanBrowseSettings(token)) return Forbid();

        var rows = await scopeStore.ListAllAsync(token);
        return Ok(new
        {
            items = rows.Select(row => new
            {
                scopeType = row.ScopeType,
                scopeKey = row.ScopeKey,
                paramKey = row.ParamKey,
                value = row.Value,
                updatedBy = row.UpdatedBy,
                updatedAt = row.UpdatedAt,
            }),
            scopable = AssistantParameterCatalog.All
                .Where(item => item.Layers != AssistantParameterScopeLayers.None)
                .Select(item => new
                {
                    key = item.Key,
                    displayName = item.DisplayName,
                    valueType = item.ValueType,
                    unit = item.Unit,
                    displayNameOfPolicy = item.ScopePolicy == AssistantParameterScopePolicy.Tighten
                        ? "只能收紧（关得掉、放不开）"
                        : "可放宽",
                    layers = LayersOf(item),
                    rangeHint = AssistantParameterCatalog.DescribeRange(item),
                }),
        });
    }

    /// <summary>
    /// 写入一层覆盖。**空值 = 清掉这一项在这一层的覆盖**（回到上层取值），
    /// 与 3105 主页面"恢复默认"同一口径。
    /// </summary>
    [HttpPut("settings/scopes")]
    public async Task<IActionResult> UpsertScope(
        [FromBody] AssistantScopeRequest? request, CancellationToken token)
    {
        if (!await CanSetupSettings(token)) return Forbid();
        if (!AssistantParameterScopeRules.TryParseType(request?.ScopeType, out var scopeType))
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "未知的作用域类型（只支持 MODULE / USER）。"));
        }

        var scopeKey = request!.ScopeKey?.Trim() ?? string.Empty;
        if (scopeKey.Length == 0 || scopeKey.Length > 50)
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "作用域对象不能为空，且不超过 50 个字符。"));
        }

        var descriptor = AssistantParameterCatalog.Find(request.ParamKey);
        if (descriptor is null)
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "未知的设置项。"));
        }

        if (descriptor.ScopePolicy == AssistantParameterScopePolicy.None)
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT",
                $"{descriptor.DisplayName} 不可作用域化——这类参数只允许全库一个值。"));
        }

        var isUserLayer = scopeType == AssistantParameterScopeRules.User;
        if (!AssistantParameterCatalog.AllowsLayer(descriptor, scopeType))
        {
            var allowed = string.Join('、', LayersOf(descriptor).Select(item => item == "USER" ? "按用户" : "按模块"));
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT",
                $"{descriptor.DisplayName} 没有声明可被{(isUserLayer ? "用户" : "模块")}覆盖（允许：{allowed}）。"));
        }

        if (!await scopeStore.ScopeKeyExistsAsync(scopeType, scopeKey, token))
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT",
                $"{(isUserLayer ? "用户" : "模块")}「{scopeKey}」不存在——"
                + "写进来也永远匹配不上任何请求，所以这里直接拒绝。"));
        }

        // 空值 = 清掉这一层，不必再校验取值
        if (string.IsNullOrWhiteSpace(request.Value))
        {
            await scopeStore.UpsertAsync(scopeType, scopeKey, descriptor.Key, null, userContext.UserId, token);
            return NoContent();
        }

        if (!AssistantParameterCatalog.TryNormalize(descriptor, request.Value, out var normalized, out var problem))
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", $"{descriptor.DisplayName}：{problem}。"));
        }

        // 松紧方向：收紧型的"上层"就是全局值（收紧型只允许声明一层，见目录里的 ValidateCatalog）
        var upperValue = registry.Current.ParameterRows
            .FirstOrDefault(row => string.Equals(row.Key, descriptor.Key, StringComparison.OrdinalIgnoreCase))
            ?.EffectiveValue;
        if (!AssistantParameterScopeRules.IsTightenAllowed(descriptor, upperValue, normalized, out var tightenProblem))
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", $"{descriptor.DisplayName}：{tightenProblem}。"));
        }

        await scopeStore.UpsertAsync(scopeType, scopeKey, descriptor.Key, normalized, userContext.UserId, token);
        return NoContent();
    }

    /// <summary>清掉某一层的全部覆盖（用户在界面上把这一层整个撤掉）。</summary>
    [HttpDelete("settings/scopes/{scopeType}/{scopeKey}")]
    public async Task<IActionResult> DeleteScopeLayer(
        string scopeType, string scopeKey, CancellationToken token)
    {
        if (!await CanSetupSettings(token)) return Forbid();
        if (!AssistantParameterScopeRules.TryParseType(scopeType, out var type))
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "未知的作用域类型。"));
        }

        await scopeStore.DeleteLayerAsync(type, scopeKey.Trim(), token);
        return NoContent();
    }

    /// <summary>该参数声明的层（供界面显示"允许按模块 / 按用户覆盖"）。</summary>
    private static IReadOnlyList<string> LayersOf(AssistantParameterDescriptor descriptor)
    {
        var layers = new List<string>();
        if (descriptor.Layers.HasFlag(AssistantParameterScopeLayers.Module)) layers.Add("MODULE");
        if (descriptor.Layers.HasFlag(AssistantParameterScopeLayers.User)) layers.Add("USER");
        return layers;
    }

    private async Task<bool> CanBrowseSettings(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.Settings, token)).CanBrowse;

    private async Task<bool> CanSetupSettings(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.Settings, token)).CanSetup;

    /// <summary>校验供应商入参；返回 null 表示通过。宁可在这里把话说明白，也不要让一次必然失败的保存悄悄成功。</summary>
    private static string? ValidateProvider(AssistantProviderRequest request)
    {
        if (!IsSupportedProvider(request.Code))
        {
            return $"未知的供应商；可选：{string.Join("、", AssistantProviderCatalog.SupportedCodes)}。";
        }

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length == 0) return "显示名不能为空。";
        if (displayName.Length > 100) return "显示名不能超过 100 个字符。";

        var baseUrl = request.BaseUrl?.Trim() ?? string.Empty;
        if (baseUrl.Length == 0) return "端点不能为空。";
        if (baseUrl.Length > 300) return "端点不能超过 300 个字符。";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return "端点必须是完整 URL（含 http/https）。";
        }

        var envVar = request.ApiKeyEnvVar?.Trim() ?? string.Empty;
        if (envVar.Length == 0) return "密钥环境变量名不能为空——库里只存变量名，不存密钥。";
        if (envVar.Length > 100) return "环境变量名不能超过 100 个字符。";
        if (!IsValidEnvVarName(envVar)) return "环境变量名只能由字母、数字与下划线组成，且不能以数字开头。";

        if (request.TimeoutSeconds is { } timeout && timeout is < 10 or > 3600)
        {
            return "超时需要在 10–3600 秒之间。";
        }

        if (request.SortIdx is { } sortIdx && sortIdx is < 0 or > 9999) return "排序号需要在 0–9999 之间。";
        if (request.Remark is { Length: > 200 }) return "备注不能超过 200 个字符。";
        return null;
    }

    /// <summary>
    /// 校验模型入参；返回 null 表示通过。
    ///
    /// <para>
    /// 窗口/输出/温度/单价/超时**都允许留空**——留空的语义各不相同（未知 / 用厂商默认 / 用供应商默认 /
    /// 用全局兜底价），所以这里只校验"传了的那个值"，不把留空当成 0。
    /// </para>
    /// </summary>
    private static string? ValidateModel(AssistantModelRequest request)
    {
        var modelCode = request.ModelCode?.Trim() ?? string.Empty;
        if (modelCode.Length == 0) return "模型名不能为空（厂商侧的标识，例如 deepseek-reasoner）。";
        if (modelCode.Length > 100) return "模型名不能超过 100 个字符。";

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length == 0) return "显示名不能为空。";
        if (displayName.Length > 100) return "显示名不能超过 100 个字符。";

        if (request.ContextWindow is { } window && window is < 1000 or > 20000000)
        {
            return "上下文窗口需要在 1000–20000000 之间（留空表示未知，按保守默认处理）。";
        }

        if (request.MaxOutputTokens is { } maxOutput && maxOutput is < 1 or > 200000)
        {
            return "最大输出 token 需要在 1–200000 之间（留空表示用厂商默认）。";
        }

        if (request.DefaultTemperature is { } temperature && (temperature < 0 || temperature > 2))
        {
            return "默认温度需要在 0–2 之间（留空表示不传该参数）。";
        }

        if (request.TimeoutSeconds is { } timeout && timeout is < 10 or > 3600)
        {
            return "超时覆盖需要在 10–3600 秒之间（留空表示用供应商的默认超时）。";
        }

        if (request.InputPerMillionYuan is { } inputPrice && inputPrice < 0) return "输入单价不能为负。";
        if (request.OutputPerMillionYuan is { } outputPrice && outputPrice < 0) return "输出单价不能为负。";

        if (request.SortIdx is { } sortIdx && sortIdx is < 0 or > 9999) return "排序号需要在 0–9999 之间。";
        if (request.Remark is { Length: > 200 }) return "备注不能超过 200 个字符。";
        return null;
    }

    /// <summary>环境变量名的形状检查（不用 <c>char.IsLetterOrDigit</c>：那个会放进 Unicode 字母）。</summary>
    private static bool IsValidEnvVarName(string name)
    {
        if (char.IsDigit(name[0])) return false;
        return name.All(ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_');
    }

    private static AssistantProviderWrite ToProviderWrite(AssistantProviderRequest request) => new(
        // CODE 统一小写：它要和代码里的预设目录对上，大小写不一致会让"目录里有、库里对不上"
        request.Code!.Trim().ToLowerInvariant(),
        request.DisplayName!.Trim(),
        request.BaseUrl!.Trim(),
        request.ApiKeyEnvVar!.Trim(),
        request.TimeoutSeconds ?? 300,
        request.Enabled ?? true,
        request.SortIdx ?? 0,
        string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim());

    private static AssistantModelWrite ToModelWrite(AssistantModelRequest request, int providerId) => new(
        providerId,
        request.ModelCode!.Trim(),
        request.DisplayName!.Trim(),
        request.ContextWindow,
        request.MaxOutputTokens,
        request.DefaultTemperature,
        request.TimeoutSeconds,
        request.InputPerMillionYuan,
        request.OutputPerMillionYuan,
        request.SupportsTools ?? true,
        request.Enabled ?? true,
        request.SortIdx ?? 0,
        string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim());

    private async Task<bool> CanBrowseModels(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.ModelUsage, token)).CanBrowse;

    private async Task<bool> CanSetupModels(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.ModelUsage, token)).CanSetup;

    private async Task<bool> CanBrowseKb(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.Kb, token)).CanBrowse;

    private async Task<bool> CanEditKb(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.Kb, token)).CanEdit;

    private async Task<bool> CanBrowseMechanism(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.Mechanism, token)).CanBrowse;

    /// <summary>未知取值一律按"在列"处理——列表查不出东西比抛 400 更难排查（与个人侧同口径）。</summary>
    internal static AssistantSessionListState ParseState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "archived" or "only" or "onlyarchived" => AssistantSessionListState.Archived,
        "all" or "true" => AssistantSessionListState.All,
        _ => AssistantSessionListState.Active,
    };

    /// <summary>
    /// 排序列的解析：只认白名单里的名字，未知一律回落"最近活跃"。
    /// 传出去的是**枚举**，列名由仓储自己映射——调用方无法把列名拼进 SQL。
    /// </summary>
    internal static AssistantSessionSort ParseSort(string? sortBy) => sortBy?.Trim().ToLowerInvariant() switch
    {
        "title" => AssistantSessionSort.Title,
        "created" or "createdat" => AssistantSessionSort.Created,
        "messages" or "messagecount" => AssistantSessionSort.Messages,
        "tokens" or "messagetokens" => AssistantSessionSort.Tokens,
        _ => AssistantSessionSort.LastActive,
    };
}
