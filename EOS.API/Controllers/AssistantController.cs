using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Features.Assistant;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 工作助手端点（ADR-007 M2 骨架）：会话 CRUD + SSE 流式对话。
/// 全部登录可用；数据按 USER_ID 服务端强制隔离。SSE 事件：delta（文本增量）、
/// done（回复已落库，含用量）、error（流中失败）。客户端断开即中止模型调用，
/// 半截回复不落库，历史经 GET messages 恢复。
/// </summary>
[ApiController, Authorize, Route("api/v1/assistant")]
public sealed class AssistantController(
    IAssistantRepository repository,
    ChatService chat,
    CurrentUserContext userContext) : ControllerBase
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

    private static async Task WriteEventAsync(StreamWriter writer, string eventName, object payload, CancellationToken token)
    {
        await writer.WriteAsync(new StringBuilder()
            .Append("event: ").Append(eventName).Append('\n')
            .Append("data: ").Append(JsonSerializer.Serialize(payload, SseJson))
            .Append("\n\n").ToString().AsMemory(), token);
        await writer.FlushAsync(token);
    }
}
