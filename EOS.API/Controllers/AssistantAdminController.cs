using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.ModelAccess;
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
    IAssistantSecretStore modelSecrets,
    AssistantModelRegistry modelRegistry,
    IAssistantUsageRepository usageRepository,
    IOptions<AssistantSettings> assistantSettings) : ControllerBase
{
    /// <summary>受支持的供应商。它们都走 OpenAI 兼容的 <c>/chat/completions</c>，因此共用一个客户端实现；
    /// 将来接入线协议不同的厂商，是"新增一个 <see cref="IChatModel"/> 实现 + 在这里登记"。</summary>
    private static readonly HashSet<string> SupportedProviders =
        new(StringComparer.OrdinalIgnoreCase) { "deepseek", "openai-compatible" };

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
    /// <remarks>
    /// 超时/启用/排序都写成可空：客户端少传一个字段时，应当落到**默认值**（300 秒 / 启用 / 0），
    /// 而不是悄悄变成"0 秒超时"或"新建出来就是停用的"。校验只对**传了**的值生效。
    /// </remarks>
    public sealed record AssistantModelRequest(
        string? DisplayName,
        string? Provider,
        string? ModelName,
        string? BaseUrl,
        string? ApiKeyEnvVar,
        int? TimeoutSeconds,
        decimal? Temperature,
        int? MaxTokens,
        bool? Enabled,
        int? SortIdx,
        string? Remark);

    /// <summary>写入密钥的入参。只进不出——任何端点都不会把它读回来。</summary>
    public sealed record AssistantModelKeyRequest(string? ApiKey);

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
    // 3102 模型与用量（见 ADR-030 §3）：管理员在这里增减 / 切换模型。
    // 密钥只在 PUT models/{id}/key 那一条路上出现过，且**只写不读**。
    // ==================================================================

    /// <summary>
    /// 模型清单。**不含任何密钥本体**，只给"环境变量名 + 是否已配置 + 掩码末四位"。
    /// 同时给出"现在到底在用哪个"——表里没有启用的当前模型时，助手用的是配置文件那套。
    /// </summary>
    [HttpGet("models")]
    public async Task<IActionResult> ListModels(CancellationToken token)
    {
        if (!await CanBrowseModels(token)) return Forbid();
        var rows = await modelCatalog.ListAsync(token);
        var config = assistantSettings.Value;
        var active = modelRegistry.Active;
        return Ok(new
        {
            items = rows.Select(row => new
            {
                modelId = row.ModelId,
                displayName = row.DisplayName,
                provider = row.Provider,
                modelName = row.ModelName,
                baseUrl = row.BaseUrl,
                apiKeyEnvVar = row.ApiKeyEnvVar,
                apiKeyConfigured = modelSecrets.IsConfigured(row.ApiKeyEnvVar),
                // 只露末四位：够确认"配的是哪一个"，不足以还原密钥
                apiKeyMaskedTail = modelSecrets.MaskedTail(row.ApiKeyEnvVar),
                timeoutSeconds = row.TimeoutSeconds,
                temperature = row.Temperature,
                maxTokens = row.MaxTokens,
                isActive = row.IsActive,
                enabled = row.Enabled,
                sortIdx = row.SortIdx,
                remark = row.Remark,
                createdAt = row.CreatedAt,
                updatedAt = row.UpdatedAt,
            }),
            current = new
            {
                // "现在到底在用哪个"必须能回答：否则管理员改完表里那行，界面上看不出有没有生效
                source = active is null ? "appsettings" : "database",
                modelId = active?.ModelId,
                displayName = active?.DisplayName,
                model = active?.Settings.Model ?? config.Model,
                baseUrl = active?.Settings.BaseUrl ?? config.BaseUrl,
                timeoutSeconds = active?.Settings.TimeoutSeconds ?? config.TimeoutSeconds,
                temperature = active?.Settings.Temperature ?? config.Temperature,
                maxTokens = active?.Settings.MaxTokens ?? config.MaxTokens,
                apiKeyConfigured = active is not null
                    ? !string.IsNullOrWhiteSpace(active.Settings.ApiKey)
                    : !string.IsNullOrWhiteSpace(config.ApiKey),
            },
        });
    }

    /// <summary>新增模型。密钥不在这个 body 里，要另外用 <c>PUT models/{id}/key</c> 写一次。</summary>
    [HttpPost("models")]
    public async Task<IActionResult> CreateModel([FromBody] AssistantModelRequest? request, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        if (request is null) return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "缺少请求体。"));
        var error = ValidateModel(request);
        if (error is not null) return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", error));

        var modelId = await modelCatalog.CreateAsync(ToWrite(request), userContext.UserId, token);
        return Ok(new { modelId });
    }

    /// <summary>修改模型（不含密钥，也不改"是否当前"——那是单独的动作）。</summary>
    [HttpPut("models/{modelId:int}")]
    public async Task<IActionResult> UpdateModel(
        int modelId, [FromBody] AssistantModelRequest? request, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        if (request is null) return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "缺少请求体。"));
        var error = ValidateModel(request);
        if (error is not null) return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", error));

        var updated = await modelCatalog.UpdateAsync(modelId, ToWrite(request), userContext.UserId, token);
        if (!updated) return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "模型不存在。"));
        // 改的若是当前这一行（端点/模型名/超时/温度…），必须让快照跟着变，否则界面显示改了、实际还在用旧的
        await modelRegistry.RefreshAsync(token);
        return NoContent();
    }

    /// <summary>
    /// 写入密钥：写进**环境变量**（进程级立即生效 + 用户级持久化），**数据库里只留变量名**。
    /// 成功后返回掩码末四位，供界面确认"配上了"。
    /// </summary>
    [HttpPut("models/{modelId:int}/key")]
    public async Task<IActionResult> SetModelKey(
        int modelId, [FromBody] AssistantModelKeyRequest? request, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        var row = await modelCatalog.GetAsync(modelId, token);
        if (row is null) return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "模型不存在。"));
        if (string.IsNullOrWhiteSpace(request?.ApiKey))
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "密钥不能为空。"));
        }

        var (processUpdated, persisted) = modelSecrets.Write(row.ApiKeyEnvVar, request.ApiKey.Trim());
        // 换密钥必须重建快照：否则当进程内还拿着旧密钥，表现是"界面说配好了，聊天却 401"
        await modelRegistry.RefreshAsync(token);
        return Ok(new
        {
            envVar = row.ApiKeyEnvVar,
            configured = modelSecrets.IsConfigured(row.ApiKeyEnvVar),
            maskedTail = modelSecrets.MaskedTail(row.ApiKeyEnvVar),
            processUpdated,
            // 用户级写入在部分环境会失败（受限账户 / 平台不支持）。那时只有当前进程生效，
            // 不能假装持久化成功——如实告诉调用方，界面才能提示"重启后需重设"。
            persisted,
        });
    }

    /// <summary>
    /// 设为当前模型。**要求密钥已配置**：把一个没有密钥的模型设成当前，会让所有人的助手立刻不可用，
    /// 而界面上看不出原因——所以在这里挡住，并说清先做哪一步。
    /// </summary>
    [HttpPost("models/{modelId:int}/activate")]
    public async Task<IActionResult> ActivateModel(int modelId, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        var row = await modelCatalog.GetAsync(modelId, token);
        if (row is null) return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "模型不存在。"));
        if (!row.Enabled)
        {
            return Conflict(ApiProblem.Create(StatusCodes.Status409Conflict, "MODEL_DISABLED", "已停用的模型不能设为当前，请先启用它。"));
        }

        if (!modelSecrets.IsConfigured(row.ApiKeyEnvVar))
        {
            return Conflict(ApiProblem.Create(
                StatusCodes.Status409Conflict, "MODEL_KEY_NOT_CONFIGURED",
                $"环境变量 {row.ApiKeyEnvVar} 还没有值。请先用「设置密钥」填一次，再设为当前——"
                + "否则整个助手的模型调用会立刻失败。"));
        }

        if (!await modelCatalog.ActivateAsync(modelId, userContext.UserId, token))
        {
            return Conflict(ApiProblem.Create(StatusCodes.Status409Conflict, "ACTIVATE_FAILED", "切换失败，请刷新后重试。"));
        }

        await modelRegistry.RefreshAsync(token);
        return NoContent();
    }

    /// <summary>取消当前模型：助手回到用配置文件里的那套（表里的行都留着）。</summary>
    [HttpPost("models/active/clear")]
    public async Task<IActionResult> ClearActiveModel(CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        await modelCatalog.ClearActiveAsync(token);
        await modelRegistry.RefreshAsync(token);
        return NoContent();
    }

    /// <summary>删除模型。**当前模型删不掉**：删掉它会让助手在无人察觉的情况下退回配置文件那套。</summary>
    [HttpDelete("models/{modelId:int}")]
    public async Task<IActionResult> DeleteModel(int modelId, CancellationToken token)
    {
        if (!await CanSetupModels(token)) return Forbid();
        var row = await modelCatalog.GetAsync(modelId, token);
        if (row is null) return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "模型不存在。"));
        if (row.IsActive)
        {
            return Conflict(ApiProblem.Create(
                StatusCodes.Status409Conflict, "MODEL_IN_USE", "正在使用这个模型，不能删除；请先切换到别的模型或取消当前。"));
        }

        await modelCatalog.DeleteAsync(modelId, token);
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
        var cost = assistantSettings.Value.Cost;

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
            models = models.Select(entry => new
            {
                modelName = entry.ModelName,
                requests = entry.Usage.Requests,
                promptTokens = entry.Usage.PromptTokens,
                completionTokens = entry.Usage.CompletionTokens,
                estimatedCostYuan = Math.Round(AssistantCost.Calculate(entry.Usage.PromptTokens, entry.Usage.CompletionTokens, cost), 4),
                lastUsedAt = entry.LastUsedAt,
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

    /// <summary>校验模型入参；返回 null 表示通过。宁可在这里把话说明白，也不要让一次必然失败的保存悄悄成功。</summary>
    private static string? ValidateModel(AssistantModelRequest request)
    {
        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length == 0) return "显示名不能为空。";
        if (displayName.Length > 100) return "显示名不能超过 100 个字符。";

        var provider = request.Provider?.Trim() ?? string.Empty;
        if (provider.Length == 0 || !SupportedProviders.Contains(provider))
        {
            return $"暂不支持的供应商；目前支持：{string.Join("、", SupportedProviders)}。";
        }

        var modelName = request.ModelName?.Trim() ?? string.Empty;
        if (modelName.Length == 0) return "模型名不能为空（厂商侧的模型标识，例如 deepseek-chat）。";
        if (modelName.Length > 100) return "模型名不能超过 100 个字符。";

        var baseUrl = request.BaseUrl?.Trim() ?? string.Empty;
        if (baseUrl.Length == 0) return "端点不能为空。";
        if (baseUrl.Length > 300) return "端点不能超过 300 个字符。";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return "端点必须是完整 URL（含 http/https）。";
        }

        var envVar = request.ApiKeyEnvVar?.Trim() ?? string.Empty;
        if (envVar.Length == 0) return "密钥环境变量名不能为空——库里只存变量名，不存密钥。";
        if (envVar.Length > 100) return "环境变量名不能超过 100 个字符。";
        if (!IsValidEnvVarName(envVar)) return "环境变量名只能由字母、数字与下划线组成，且不能以数字开头。";

        if (request.TimeoutSeconds is < 10 or > 3600) return "超时需要在 10–3600 秒之间。";
        if (request.Temperature is { } temperature && (temperature < 0 || temperature > 2))
        {
            return "温度需要在 0–2 之间（留空表示用厂商默认）。";
        }

        if (request.MaxTokens is { } maxTokens && (maxTokens is < 1 or > 200000))
        {
            return "最大 token 需要在 1–200000 之间（留空表示用厂商默认）。";
        }

        if (request.SortIdx is < 0 or > 9999) return "排序号需要在 0–9999 之间。";
        if (request.Remark is { Length: > 200 }) return "备注不能超过 200 个字符。";
        return null;
    }

    /// <summary>环境变量名的形状检查（不用 <c>char.IsLetterOrDigit</c>：那个会放进 Unicode 字母）。</summary>
    private static bool IsValidEnvVarName(string name)
    {
        if (char.IsDigit(name[0])) return false;
        return name.All(ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_');
    }

    private static AssistantModelWrite ToWrite(AssistantModelRequest request) => new(
        request.DisplayName!.Trim(),
        request.Provider!.Trim(),
        request.ModelName!.Trim(),
        request.BaseUrl!.Trim(),
        request.ApiKeyEnvVar!.Trim(),
        request.TimeoutSeconds ?? 300,
        request.Temperature,
        request.MaxTokens,
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
