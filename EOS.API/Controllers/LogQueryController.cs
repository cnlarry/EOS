using EOS.API.Data;
using EOS.API.Logging;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 结构化日志查询（ADR-005 §5.4）：search/trace/errors-summary/slow/tail/redact，
/// 只读、统一脱敏、限行数/字节、查询留痕；权限门：模块 2306（系统管理）CanSetup。
/// 默认面向开发/测试环境；生产访问需显式授权（具备 2306 Setup 的用户）。
/// </summary>
[ApiController, Authorize, Route("api/logs")]
public sealed class LogQueryController(
    LogQueryService logs,
    WorkbenchAuditWriter auditWriter,
    IPermissionService permissions,
    CurrentUserContext userContext) : ControllerBase
{
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? level,
        [FromQuery] string? traceId,
        [FromQuery] string? correlationId,
        [FromQuery] string? userId,
        [FromQuery] int? moduleId,
        [FromQuery] string? action,
        [FromQuery] string? errorCode,
        [FromQuery] string? keyword,
        [FromQuery] int? limit,
        CancellationToken token)
    {
        await RequireSetupAsync(token);
        var result = await logs.SearchAsync(
            new LogSearchRequest(from, to, level, traceId, correlationId, userId, moduleId, action, errorCode, keyword, limit ?? 200), token);
        await AuditQueryAsync($"search module={moduleId} action={action} correlation={correlationId} rows={result.Count}", token);
        return Ok(result);
    }

    [HttpGet("trace/{correlationId}")]
    public async Task<IActionResult> Trace(string correlationId, CancellationToken token)
    {
        await RequireSetupAsync(token);
        var result = await logs.GetTraceAsync(correlationId, token);
        await AuditQueryAsync($"trace {correlationId} rows={result.Count}", token);
        return Ok(result);
    }

    [HttpGet("errors/summary")]
    public async Task<IActionResult> ErrorSummary([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken token)
    {
        await RequireSetupAsync(token);
        var result = await logs.SummarizeErrorsAsync(from, to, token);
        await AuditQueryAsync($"errors-summary rows={result.Count}", token);
        return Ok(result);
    }

    [HttpGet("slow/{correlationId}")]
    public async Task<IActionResult> Slow(string correlationId, CancellationToken token)
    {
        await RequireSetupAsync(token);
        var result = await logs.ExplainSlowAsync(correlationId, token);
        await AuditQueryAsync($"slow {correlationId}", token);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("tail")]
    public async Task<IActionResult> Tail([FromQuery] string? level, [FromQuery] int? limit, CancellationToken token)
    {
        await RequireSetupAsync(token);
        var result = await logs.TailAsync(level, limit ?? 50, token);
        await AuditQueryAsync($"tail level={level} rows={result.Count}", token);
        return Ok(result);
    }

    [HttpPost("redact")]
    public async Task<IActionResult> Redact([FromBody] LogRedactRequest request, CancellationToken token)
    {
        await RequireSetupAsync(token);
        var redacted = logs.RedactText(request.Text ?? string.Empty);
        await AuditQueryAsync($"redact chars={redacted.Length}", token);
        return Ok(new { redacted });
    }

    private async Task RequireSetupAsync(CancellationToken token)
        => await permissions.RequireAsync(userContext.UserId, PermissionModules.SystemManagement, PermissionAction.Setup, token);

    private async Task AuditQueryAsync(string summary, CancellationToken token)
        => await auditWriter.WriteBestEffortAsync(null, summary, "LOG_QUERY", summary, userContext.UserId, "LOG_QUERY", result: 1, null, token);
}

public sealed record LogRedactRequest(string? Text);
