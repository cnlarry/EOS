using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.Admin;
using EOS.API.Features.Assistant.Memory;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    ChangeSetService changeSets) : ControllerBase
{
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    public sealed record ChatContext(int? ModuleId, string? ModuleTitle, string? PageType, string? DocNo);
    public sealed record ChatRequest(string Content, ChatContext? Context);

    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions([FromQuery] int limit = 50, CancellationToken token = default)
        => Ok(await repository.ListSessionsAsync(userContext.UserId, limit, token));

    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession(CancellationToken token)
        => Ok(await repository.CreateSessionAsync(userContext.UserId, token));

    [HttpDelete("sessions/{sessionId:long}")]
    public async Task<IActionResult> DeleteSession(long sessionId, CancellationToken token)
    {
        var deleted = await repository.DeleteSessionAsync(userContext.UserId, sessionId, token);
        return deleted > 0 ? NoContent() : NotFound();
    }

    [HttpGet("sessions/{sessionId:long}/messages")]
    public async Task<IActionResult> ListMessages(long sessionId, CancellationToken token)
    {
        var session = await repository.GetSessionAsync(userContext.UserId, sessionId, token);
        if (session is null) return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "会话不存在或不属于当前用户。"));
        return Ok(await repository.ListMessagesAsync(userContext.UserId, sessionId, token));
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

        var correlationId = HttpContext.TraceIdentifier;
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(false), leaveOpen: true);
        await foreach (var evt in chat.StreamReplyAsync(
            userContext.UserId, sessionId, request.Content ?? string.Empty,
            request.Context is null ? null : new Features.Assistant.PageContext(
                request.Context.ModuleId, request.Context.ModuleTitle,
                request.Context.PageType, request.Context.DocNo),
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
    /// 变更集确认执行（M8 admin-write）：仅结构化确认卡可调用。自然语言确认无效
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
            await auditWriter.WriteBestEffortAsync(null, "admin-changeset", "CHANGESET_APPLY",
                $"助手变更集执行（{result.Goal}）：登记表 {result.TablesRegistered} 个，新增字段 {result.FieldsCreated} 个，跳过 {result.FieldsSkipped} 个",
                userContext.UserId, "ASSISTANT_ADMIN", result: 1, null, token);
            return Ok(new
            {
                goal = result.Goal,
                tablesRegistered = result.TablesRegistered,
                fieldsCreated = result.FieldsCreated,
                fieldsSkipped = result.FieldsSkipped,
                notes = result.Notes,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "CHANGESET_BLOCKED", ex.Message));
        }
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
