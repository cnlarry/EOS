using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.Admin;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.Memory;
using EOS.API.Features.Assistant.Situation;
using EOS.API.Security;
using EOS.API.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EOS.API.Controllers;

/// <summary>
/// 工作助手端点：会话 CRUD + SSE 流式对话。
/// 全部登录可用；数据按 USER_ID 服务端强制隔离。SSE 事件：delta（文本增量）、
/// done（回复已落库，含用量）、error（流中失败）。客户端断开即中止模型调用，
/// 半截回复不落库，历史经 GET messages 恢复。
/// </summary>
[ApiController, Authorize, Route("api/v1/assistant")]
public sealed class AssistantController(
    IAssistantRepository repository,
    ChatService chat,
    CurrentUserContext userContext,
    IAssistantMemoryStore memoryStore,
    WorkbenchAuditWriter auditWriter,
    ModuleRightsRepository rightsRepository,
    ChangeSetService changeSets,
    IAssistantUsageRepository usageRepository,
    SituationContextSanitizer situationSanitizer,
    AssistantSituationService situationService,
    SituationDigestService situationDigest,
    IOptions<Features.Assistant.ModelAccess.AssistantSettings> assistantOptions) : ControllerBase
{
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 处境上报契约：模块/页面/单据 + 列表筛选 + 选中行 + 表单脏字段 + 最近一次服务端拒绝 + 配置页目标。
    /// 上限与白名单一律由服务端强制（见 <see cref="SituationContextSanitizer"/>）。
    /// </summary>
    public sealed record ChatContext(
        int? ModuleId,
        string? ModuleTitle,
        string? PageType,
        string? DocNo,
        IReadOnlyList<SituationFilter>? Filters = null,
        IReadOnlyList<string>? Selection = null,
        IReadOnlyList<SituationDirtyField>? FormDirty = null,
        SituationNotice? LastNotice = null,
        SituationConfigTarget? ConfigTarget = null)
    {
        public SituationReport ToReport() => new(
            ModuleId, ModuleTitle, PageType, DocNo, Filters, Selection, FormDirty, LastNotice, ConfigTarget);
    }

    public sealed record ChatRequest(string Content, ChatContext? Context);

    /// <summary>重命名会话。标题非空、超长按库内 <c>TITLE</c> 宽度（200）截断。</summary>
    public sealed record RenameSessionRequest(string Title);

    /// <summary>归档 / 取消归档。<c>Archived</c> 缺省视为 <c>true</c>——空 body 不该把会话"取消归档"。</summary>
    public sealed record ArchiveSessionRequest(bool? Archived);

    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions(
        [FromQuery] int limit = 50, [FromQuery] bool archived = false, CancellationToken token = default)
        => Ok(await repository.ListSessionsAsync(userContext.UserId, limit, archived, token));

    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession(CancellationToken token)
        => Ok(await repository.CreateSessionAsync(userContext.UserId, token));

    /// <summary>
    /// 删除会话（连消息一并删）。
    /// <para>
    /// **界面已不提供这个动作**：误删即永久丢历史，产品决定改用「归档」（会话不出现在列表里、
    /// 数据完整保留、可随时取消归档）。端点保留给运维与测试清理，不作为用户界面能力。
    /// </para>
    /// </summary>
    [HttpDelete("sessions/{sessionId:long}")]
    public async Task<IActionResult> DeleteSession(long sessionId, CancellationToken token)
    {
        var deleted = await repository.DeleteSessionAsync(userContext.UserId, sessionId, token);
        return deleted > 0 ? NoContent() : NotFound();
    }

    [HttpPut("sessions/{sessionId:long}/rename")]
    public async Task<IActionResult> RenameSession(long sessionId, [FromBody] RenameSessionRequest request, CancellationToken token)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "会话标题不能为空。"));
        }
        var updated = await repository.RenameSessionAsync(userContext.UserId, sessionId, title[..Math.Min(title.Length, 200)], token);
        return updated > 0 ? NoContent() : NotFound();
    }

    [HttpPut("sessions/{sessionId:long}/archive")]
    public async Task<IActionResult> ArchiveSession(long sessionId, [FromBody] ArchiveSessionRequest? request, CancellationToken token)
    {
        var updated = await repository.ArchiveSessionAsync(userContext.UserId, sessionId, request?.Archived ?? true, token);
        return updated > 0 ? NoContent() : NotFound();
    }

    [HttpGet("sessions/{sessionId:long}/messages")]
    public async Task<IActionResult> ListMessages(long sessionId, CancellationToken token)
    {
        var session = await repository.GetSessionAsync(userContext.UserId, sessionId, token);
        if (session is null) return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "会话不存在或不属于当前用户。"));
        var messages = await repository.ListMessagesAsync(userContext.UserId, sessionId, token);
        // 历史消息按与 done 事件**同形**下发工具摘要（{name, digest}）：切会话/刷新后工具卡不该消失，
        // 同时不把库内 JSON 的形状（含工具参数）暴露给前端——形状只有一处定义。
        return Ok(messages.Select(message => new
        {
            message.Id,
            message.SessionId,
            message.Role,
            message.Content,
            message.ModelName,
            message.PromptTokens,
            message.CompletionTokens,
            message.ElapsedMs,
            message.CorrelationId,
            message.CreatedAt,
            ToolCalls = ParseToolCallDigests(message.ToolCallsJson),
        }));
    }

    /// <summary>读库内工具摘要 JSON 时的选项：命名大小写不敏感——"库里当初怎么写"不该决定"现在还读不读得出来"。</summary>
    private static readonly JsonSerializerOptions DigestJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 库内工具摘要 JSON → 前端要的 <c>{name, digest}</c> 数组；解析不了就当作没有（历史照常显示，
    /// 一条坏数据不该让整个会话打不开）。没有工具调用时返回 null，与 SSE 的 done 事件一致。
    /// </summary>
    internal static IReadOnlyList<object>? ParseToolCallDigests(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var summaries = JsonSerializer.Deserialize<List<ToolCallSummary>>(json, DigestJson);
            if (summaries is null || summaries.Count == 0) return null;
            return [.. summaries.Select(item => (object)new { name = item.Name, digest = item.ResultDigest })];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>发送一条用户消息并流式返回助手回复。响应恒为 text/event-stream。</summary>
    [HttpPost("sessions/{sessionId:long}/chat")]
    public async Task Chat(long sessionId, [FromBody] ChatRequest request, CancellationToken token)
    {
        // 前置归属校验：非本会话直接 404，不进入 SSE。
        var session = await repository.GetSessionAsync(userContext.UserId, sessionId, token);
        if (session is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // 与请求日志/审计同一关联键：会话消息与排障记录要能按同一个值串起来。
        var correlationId = RequestContext.GetCorrelationId(HttpContext);
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var situation = await situationSanitizer.SanitizeAsync(
            userContext.UserId, request.Context?.ToReport(), token);
        var pageContext = Features.Assistant.PageContext.FromSituation(situation);
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(false), leaveOpen: true);
        await foreach (var evt in chat.StreamReplyAsync(
            userContext.UserId, sessionId, request.Content ?? string.Empty,
            pageContext.IsEmpty ? null : pageContext,
            correlationId, token))
        {
            switch (evt)
            {
                case ChatStreamEvent.Delta d:
                    await WriteEventAsync(writer, "delta", new { text = d.Text }, token);
                    break;
                case ChatStreamEvent.Completed done:
                    await WriteEventAsync(writer, "done", new
                    {
                        message = done.Message,
                        toolCalls = done.ToolCalls?.Select(t => new { name = t.Name, digest = t.ResultDigest }),
                        drafts = done.Drafts,
                    }, token);
                    break;
                case ChatStreamEvent.Failed fail:
                    await WriteEventAsync(writer, "error", new { code = fail.Code, message = fail.Message }, token);
                    break;
            }
        }
    }

    /// <summary>
    /// 打开即见：结构化处境快照（身份 / 在哪 / 待办 / 最近被拒 / 摘要）。
    /// **零模型调用**——摘要由服务端规则引擎产出，前端渲染为卡片；追问才走对话。
    /// </summary>
    [HttpGet("situation")]
    public async Task<IActionResult> GetSituation(
        [FromQuery] int? moduleId, [FromQuery] string? pageType, [FromQuery] string? docNo,
        CancellationToken token)
    {
        var userId = userContext.UserId;
        var situation = await situationSanitizer.SanitizeAsync(
            userId, new SituationReport(moduleId, null, pageType, docNo, null, null, null, null, null), token);
        var digest = await situationDigest.BuildAsync(userId, situation, token);
        return Ok(await situationService.BuildSnapshotAsync(userId, situation, digest, token));
    }

    public sealed record SaveMemoryRequest(
        string MemoryType, string MemoryKey, string MemoryValue,
        long? SourceMessageId = null, bool ConfirmedRisk = false);

    public sealed record SavePreferencesRequest(string? PreferencesJson);

    /// <summary>本人记忆：偏好 + 显式记忆列表（仅 active）。</summary>
    [HttpGet("memory")]
    public async Task<IActionResult> ListMemory(CancellationToken token)
    {
        var userId = userContext.UserId;
        var memories = await memoryStore.ListMemoriesAsync(userId, token);
        return Ok(new
        {
            preferences = await memoryStore.GetPreferencesAsync(userId, token),
            memories = memories.Select(m => new
            {
                id = m.Id.ToString(),
                type = m.MemoryType,
                key = m.MemoryKey,
                value = m.MemoryValue,
                source = m.Source,
                updatedAt = m.UpdatedAt,
            }),
        });
    }

    /// <summary>记住一条显式记忆（本人可见；含敏感模式时需确认风险）。</summary>
    [HttpPost("memory")]
    public async Task<IActionResult> SaveMemory([FromBody] SaveMemoryRequest request, CancellationToken token)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.MemoryKey) || string.IsNullOrWhiteSpace(request.MemoryValue))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "记忆标题与内容不能为空。"));
        if (AssistantMemoryStore.ContainsSensitivePattern(request.MemoryKey + "\n" + request.MemoryValue)
            && !request.ConfirmedRisk)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "MEMORY_PII_RISK",
                "记忆疑似包含手机号/证件号/银行卡号等敏感信息，请删除敏感内容或确认风险后重试（confirmedRisk=true）。"));
        try
        {
            var item = await memoryStore.AddMemoryAsync(
                userContext.UserId, request.MemoryType ?? "fact",
                request.MemoryKey, request.MemoryValue, request.SourceMessageId, token);
            await auditWriter.WriteBestEffortAsync(null, $"memory:{item.Id}", "MEMORY_SAVE",
                $"助手显式记忆新增（{item.MemoryType}/{item.MemoryKey}）", userContext.UserId,
                "ASSISTANT_MEMORY", result: 1, null, token);
            return Ok(new { id = item.Id.ToString() });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "MEMORY_LIMIT", ex.Message));
        }
    }

    /// <summary>删除一条本人记忆（软删除，审计保留痕迹）。</summary>
    [HttpDelete("memory/{memoryId:long}")]
    public async Task<IActionResult> DeleteMemory(long memoryId, CancellationToken token)
    {
        var deleted = await memoryStore.DeleteMemoryAsync(userContext.UserId, memoryId, token);
        if (deleted)
        {
            await auditWriter.WriteBestEffortAsync(null, $"memory:{memoryId}", "MEMORY_DELETE",
                "助手显式记忆删除", userContext.UserId, "ASSISTANT_MEMORY", result: 1, null, token);
            return NoContent();
        }

        return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "记忆不存在或不属于当前用户。"));
    }

    /// <summary>本人待确认记忆（AI 自动提炼，pending；确认前不注入对话）。</summary>
    [HttpGet("memory/pending")]
    public async Task<IActionResult> ListPendingMemory(CancellationToken token)
    {
        var memories = await memoryStore.ListPendingAsync(userContext.UserId, token);
        return Ok(new
        {
            memories = memories.Select(m => new
            {
                id = m.Id.ToString(),
                type = m.MemoryType,
                key = m.MemoryKey,
                value = m.MemoryValue,
                confidence = m.Confidence,
            }),
        });
    }

    public sealed record ResolvePendingRequest(bool Confirm);

    /// <summary>确认/拒绝一条待确认记忆（确认转 active 并覆盖同名 active，拒绝归档）。</summary>
    [HttpPost("memory/pending/{memoryId:long}/resolve")]
    public async Task<IActionResult> ResolvePendingMemory(
        long memoryId, [FromBody] ResolvePendingRequest request, CancellationToken token)
    {
        var outcome = await memoryStore.ResolvePendingAsync(userContext.UserId, memoryId, request?.Confirm == true, token);
        if (outcome == "not_found")
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "待确认记忆不存在或不属于当前用户。"));
        await auditWriter.WriteBestEffortAsync(null, $"memory:{memoryId}",
            outcome == "confirmed" ? "MEMORY_CONFIRM" : "MEMORY_REJECT",
            $"助手待确认记忆{ (outcome == "confirmed" ? "确认" : "拒绝") }", userContext.UserId,
            "ASSISTANT_MEMORY", result: 1, null, token);
        return Ok(new { resolved = outcome });
    }

    /// <summary>「忘记我」：硬删除本人的画像与全部记忆（审计仅留操作痕迹，不存内容）。</summary>
    [HttpDelete("memory/all")]
    public async Task<IActionResult> ForgetMe(CancellationToken token)
    {
        await memoryStore.ForgetMeAsync(userContext.UserId, token);
        await auditWriter.WriteBestEffortAsync(null, "memory:all", "MEMORY_FORGET_ALL",
            "用户清空全部助手记忆", userContext.UserId, "ASSISTANT_MEMORY", result: 1, null, token);
        return NoContent();
    }

    /// <summary>保存本人偏好 JSON（空 = 清空）。</summary>
    [HttpPut("memory/preferences")]
    public async Task<IActionResult> SavePreferences([FromBody] SavePreferencesRequest request, CancellationToken token)
    {
        try
        {
            await memoryStore.SetPreferencesAsync(userContext.UserId, request?.PreferencesJson, token);
            await auditWriter.WriteBestEffortAsync(null, "profile", "MEMORY_PREF",
                "助手偏好设置保存", userContext.UserId, "ASSISTANT_MEMORY", result: 1, null, token);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", ex.Message));
        }
    }

    public sealed record ApplyChangeSetRequest(JsonElement Changeset, bool Confirmed);

    /// <summary>
    /// 变更集确认执行（admin-write）：仅结构化确认卡可调用。自然语言确认无效
    /// （confirmed 必须为 true）；执行前重跑试算，拦截即拒绝，零部分写入由仓储事务保证。
    /// </summary>
    [HttpPost("apply-changeset")]
    public async Task<IActionResult> ApplyChangeSet([FromBody] ApplyChangeSetRequest request, CancellationToken token)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, 2302, token)).CanSetup) return Forbid();
        if (request is null || !request.Confirmed)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "CONFIRM_REQUIRED",
                "变更集执行必须经结构化确认卡确认（confirmed=true），自然语言确认无效。"));
        try
        {
            var result = await changeSets.ExecuteAsync(
                request.Changeset, userContext.EmployeeName ?? userContext.UserId, token);
            var failedModules = result.Validation.Where(item => !item.Passed).Select(item => item.ModuleId).ToArray();
            await auditWriter.WriteBestEffortAsync(null, "admin-changeset", "CHANGESET_APPLY",
                $"助手变更集执行（{result.Goal}）：登记表 {result.TablesRegistered} 个，新增字段 {result.FieldsCreated} 个，跳过 {result.FieldsSkipped} 个；" +
                (failedModules.Length == 0 ? "受影响模块发布前校验全部通过" : $"校验未通过模块：{string.Join("、", failedModules)}（已标脏待管理端发布）"),
                userContext.UserId, "ASSISTANT_ADMIN", result: 1, null, token);
            return Ok(new
            {
                goal = result.Goal,
                tablesRegistered = result.TablesRegistered,
                fieldsCreated = result.FieldsCreated,
                fieldsSkipped = result.FieldsSkipped,
                notes = result.Notes,
                affectedModules = result.AffectedModuleIds,
                validation = result.Validation.Select(item => new
                {
                    moduleId = item.ModuleId,
                    passed = item.Passed,
                    failedCodes = item.FailedCodes,
                }),
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "CHANGESET_BLOCKED", ex.Message));
        }
    }

    /// <summary>本人今日用量（请求数/token/估算成本 + 限额）。</summary>
    [HttpGet("usage")]
    public async Task<IActionResult> MyUsage(CancellationToken token)
    {
        var cost = assistantOptions.Value.Cost;
        var usage = await usageRepository.GetUserDailyUsageAsync(
            userContext.UserId, DateTimeOffset.UtcNow.Date, token);
        return Ok(new
        {
            day = DateTimeOffset.UtcNow.Date,
            requests = usage.Requests,
            promptTokens = usage.PromptTokens,
            completionTokens = usage.CompletionTokens,
            estimatedCostYuan = Math.Round(AssistantCost.Calculate(
                usage.PromptTokens, usage.CompletionTokens, cost), 4),
            userDailyCapYuan = cost.UserDailyCapYuan,
            globalDailyCapYuan = cost.GlobalDailyCapYuan,
        });
    }

    /// <summary>用量看板（角色③治理可见，2306 CanSetup 门）：全局 + 分用户聚合。</summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> Metrics(CancellationToken token)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, 2306, token)).CanSetup) return Forbid();
        var cost = assistantOptions.Value.Cost;
        var dayStart = DateTimeOffset.UtcNow.Date;
        var global = await usageRepository.GetGlobalDailyUsageAsync(dayStart, token);
        var perUser = await usageRepository.GetPerUserDailyUsageAsync(dayStart, 20, token);
        var latency = await usageRepository.GetGlobalLatencyAsync(dayStart, token);
        return Ok(new
        {
            day = dayStart,
            global = new
            {
                requests = global.Requests,
                promptTokens = global.PromptTokens,
                completionTokens = global.CompletionTokens,
                estimatedCostYuan = Math.Round(AssistantCost.Calculate(
                    global.PromptTokens, global.CompletionTokens, cost), 4),
            },
            latency = new
            {
                samples = latency.Samples,
                avgMs = Math.Round(latency.AvgMs, 0),
                p95Ms = Math.Round(latency.P95Ms, 0),
            },
            users = perUser.Select(entry => new
            {
                userId = entry.UserId,
                requests = entry.Usage.Requests,
                estimatedCostYuan = Math.Round(AssistantCost.Calculate(
                    entry.Usage.PromptTokens, entry.Usage.CompletionTokens, cost), 4),
            }),
            caps = new
            {
                userDailyCapYuan = cost.UserDailyCapYuan,
                globalDailyCapYuan = cost.GlobalDailyCapYuan,
                maxConsecutiveFailures = cost.MaxConsecutiveFailures,
                cooldownSeconds = cost.CooldownSeconds,
            },
        });
    }

    private static async Task WriteEventAsync(StreamWriter writer, string eventName, object payload, CancellationToken token)
    {
        await writer.WriteAsync(new StringBuilder()
            .Append("event: ").Append(eventName).Append('\n')
            .Append("data: ").Append(JsonSerializer.Serialize(payload, SseJson))
            .Append("\n\n").ToString().AsMemory(), token);
        await writer.FlushAsync(token);
    }
}
