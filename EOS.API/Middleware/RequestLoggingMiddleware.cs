using System.Diagnostics;
using System.Security.Claims;
using EOS.API.Telemetry;

namespace EOS.API.Middleware;

/// <summary>
/// Structured request log: resolves/forwards X-Correlation-Id and X-Client-Id,
/// emits a uniform http_request event with traceId/spanId/correlationId/userId/
/// clientId/moduleId/action/status/elapsedMs/dbElapsedMs/error.code, and feeds
/// HTTP metrics. Only /api requests log at Information by default, the rest at
/// Debug; 5xx or error.code log at Error, slow successes (>= 1s) at Warning so
/// the Warning+ file log keeps every troubleshooting-relevant request.
/// </summary>
public sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger,
    ApiMetrics metrics,
    DbTimingCollector dbTiming,
    EOS.API.Data.WorkbenchDefinitionProvider definitionProvider)
{
    /// <summary>Slow request threshold: successes at or above this log at Warning.</summary>
    public const double SlowRequestThresholdMs = 1000;

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
                else if (elapsedMs >= SlowRequestThresholdMs)
                {
                    logger.LogWarning(message, "http_request", context.Request.Method,
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
