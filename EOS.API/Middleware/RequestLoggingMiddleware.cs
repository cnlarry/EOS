using System.Diagnostics;
using System.Security.Claims;
using EOS.API.Telemetry;

namespace EOS.API.Middleware;

/// <summary>
/// 结构化请求日志：为每个请求解析/透传 X-Correlation-Id 与
/// X-Client-Id，输出统一事件 http_request，固定字段含 traceId/spanId/correlationId/
/// userId/clientId/moduleId/action/status/elapsedMs/dbElapsedMs/error.code；
/// 同时驱动 HTTP 指标（请求数/错误数/延迟直方图）。
/// 仅 /api 请求默认输出到 Information，其余按 Debug；5xx 或带 error.code 按 Error。
/// </summary>
public sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger,
    ApiMetrics metrics,
    DbTimingCollector dbTiming,
    EOS.API.Data.WorkbenchDefinitionProvider definitionProvider)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        var clientId = ClientIds.Normalize(context.Request.Headers["X-Client-Id"].ToString());
        context.Items[RequestContext.CorrelationIdKey] = correlationId;
        context.Items[RequestContext.ClientIdKey] = clientId;
        context.Response.Headers["X-Correlation-Id"] = correlationId;

        var isApi = context.Request.Path.StartsWithSegments("/api");
        var start = Stopwatch.GetTimestamp();

        using (dbTiming.BeginRequest())
        {
            try
            {
                await next(context);
            }
            finally
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var status = context.Response.StatusCode;
                var user = context.User.Identity?.IsAuthenticated == true
                    ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "?"
                    : "anonymous";
                var (traceId, spanId) = RequestContext.GetTraceIds(context);
                var moduleId = RequestContext.GetModuleId(context);
                var action = RequestContext.GetAction(context);
                var errorCode = RequestContext.GetErrorCode(context);
                var definitionVersion = moduleId is int moduleIndex ? definitionProvider.GetVersion(moduleIndex) : null;
                var dbElapsedMs = dbTiming.TotalMilliseconds;
                var route = context.GetEndpoint() is RouteEndpoint routeEndpoint
                    ? routeEndpoint.RoutePattern.RawText ?? context.Request.Path.Value ?? "unknown"
                    : context.Request.Path.Value ?? "unknown";

                metrics.CountHttpRequest(context.Request.Method, route, status);
                metrics.ObserveHttpDuration(elapsedMs / 1000.0);

                const string message =
                    "HTTP 请求结束 {Event} {Method} {Path} -> {Status} 耗时 {ElapsedMs:F0}ms db={DbElapsedMs:F0}ms user={User} client={ClientId} module={ModuleId} action={Action} definitionVersion={DefinitionVersion} correlation={CorrelationId} trace={TraceId} span={SpanId} error={ErrorCode}";
                if (status >= 500 || errorCode is not null)
                {
                    logger.LogError(message, "http_request", context.Request.Method,
                        context.Request.Path.ToString(), status, elapsedMs, dbElapsedMs, user, clientId,
                        moduleId, action, definitionVersion, correlationId, traceId, spanId, errorCode);
                }
                else if (isApi)
                {
                    logger.LogInformation(message, "http_request", context.Request.Method,
                        context.Request.Path.ToString(), status, elapsedMs, dbElapsedMs, user, clientId,
                        moduleId, action, definitionVersion, correlationId, traceId, spanId, errorCode);
                }
                else
                {
                    logger.LogDebug(message, "http_request", context.Request.Method,
                        context.Request.Path.ToString(), status, elapsedMs, dbElapsedMs, user, clientId,
                        moduleId, action, definitionVersion, correlationId, traceId, spanId, errorCode);
                }
            }
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        const string header = "X-Correlation-Id";
        var provided = context.Request.Headers[header].ToString();
        if (!string.IsNullOrWhiteSpace(provided) && provided.Length <= 128)
        {
            return provided;
        }

        return context.TraceIdentifier;
    }
}
