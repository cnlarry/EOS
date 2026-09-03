using System.Globalization;
using System.Text.Json;
using EOS.API.Data;
using ModelContextProtocol.Server;

namespace EOS.API.Logging;

/// <summary>
/// 日志分析 MCP Server 工具：search_logs / get_request_trace /
/// summarize_errors / explain_slow_request / tail_logs / redact。只读日志、输出统一脱敏、
/// 限行数/字节；每次查询经 AUDIT_EVENT（LOG_QUERY）留痕。
/// </summary>
[McpServerToolType]
public sealed class LogMcpTools(LogQueryService logs, WorkbenchAuditWriter auditWriter)
{
    [McpServerTool(Name = "search_logs", Title = "按时间/级别/traceId/correlationId/userId/moduleId/action/error code/关键字检索结构化日志（只读，已脱敏，最多 500 行）。")]
    public async Task<string> SearchLogsAsync(
        string? from, string? to, string? level, string? traceId, string? correlationId,
        string? userId, int? moduleId, string? action, string? errorCode, string? keyword, int? limit, CancellationToken ct)
    {
        var request = new LogSearchRequest(ParseDate(from), ParseDate(to), level, traceId, correlationId, userId,
            moduleId, action, errorCode, keyword, Math.Clamp(limit ?? 200, 1, LogQueryService.MaxLines));
        var rows = await logs.SearchAsync(request, ct);
        await AuditAsync($"search_logs module={moduleId} action={action} correlation={correlationId} rows={rows.Count}", ct);
        return JsonSerializer.Serialize(rows);
    }

    [McpServerTool(Name = "get_request_trace", Title = "按 correlationId 聚合 HTTP/结构化日志事件（含慢请求阶段）。")]
    public async Task<string> GetRequestTraceAsync(string correlationId, CancellationToken ct)
    {
        var rows = await logs.GetTraceAsync(correlationId, ct);
        await AuditAsync($"get_request_trace {correlationId} rows={rows.Count}", ct);
        return JsonSerializer.Serialize(rows);
    }

    [McpServerTool(Name = "summarize_errors", Title = "按错误码/模块/action 聚合时间窗内错误与告警。")]
    public async Task<string> SummarizeErrorsAsync(string? from, string? to, CancellationToken ct)
    {
        var rows = await logs.SummarizeErrorsAsync(ParseDate(from), ParseDate(to), ct);
        await AuditAsync($"summarize_errors rows={rows.Count}", ct);
        return JsonSerializer.Serialize(rows);
    }

    [McpServerTool(Name = "explain_slow_request", Title = "定位慢请求：返回 HTTP 事件耗时/dbElapsedMs/状态/action/路径与完整 trace。")]
    public async Task<string> ExplainSlowRequestAsync(string correlationId, CancellationToken ct)
    {
        var report = await logs.ExplainSlowAsync(correlationId, ct);
        await AuditAsync($"explain_slow_request {correlationId}", ct);
        return report is null ? "{}" : JsonSerializer.Serialize(report);
    }

    [McpServerTool(Name = "tail_logs", Title = "跟踪最新日志（按级别过滤，默认 50 条，最多 500 条，已脱敏）。")]
    public async Task<string> TailLogsAsync(string? level, int? limit, CancellationToken ct)
    {
        var rows = await logs.TailAsync(level, limit ?? 50, ct);
        await AuditAsync($"tail_logs level={level} rows={rows.Count}", ct);
        return JsonSerializer.Serialize(rows);
    }

    [McpServerTool(Name = "redact", Title = "对任意文本统一脱敏（密码/Token/连接串/Cookie/JWT 模式掩码）。")]
    public async Task<string> RedactAsync(string text, CancellationToken ct)
    {
        var redacted = logs.RedactText(text);
        await AuditAsync($"redact chars={redacted.Length}", ct);
        return redacted;
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    private async Task AuditAsync(string summary, CancellationToken ct)
        => await auditWriter.WriteBestEffortAsync(null, summary, "LOG_QUERY", summary, "mcp", "LOG_QUERY", result: 1, null, ct);
}
