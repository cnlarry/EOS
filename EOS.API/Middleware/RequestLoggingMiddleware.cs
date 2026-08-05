using System.Diagnostics;
using System.Security.Claims;

namespace EOS.API.Middleware;

/// <summary>
/// 轻量请求日志：为每个请求生成/透传 X-Correlation-Id，记录方法、路径、
/// 用户、状态码与耗时。仅 /api 请求默认输出到 Information，其余按 Debug，
/// 5xx 一律按 Error 输出。
/// </summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Response.Headers["X-Correlation-Id"] = correlationId;

        var isApi = context.Request.Path.StartsWithSegments("/api");
        var start = Stopwatch.GetTimestamp();

        if (isApi)
        {
            logger.LogInformation(
                "HTTP 开始 {Method} {Path}{Query} correlation={CorrelationId}",
                context.Request.Method, context.Request.Path, context.Request.QueryString, correlationId);
        }
        else
        {
            logger.LogDebug(
                "HTTP 开始 {Method} {Path}{Query} correlation={CorrelationId}",
                context.Request.Method, context.Request.Path, context.Request.QueryString, correlationId);
        }

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

            if (status >= 500)
            {
                logger.LogError(
                    "HTTP 结束 {Method} {Path} -> {Status} 耗时 {ElapsedMs:F0}ms user={User} correlation={CorrelationId}",
                    context.Request.Method, context.Request.Path, status, elapsedMs, user, correlationId);
            }
            else if (isApi)
            {
                logger.LogInformation(
                    "HTTP 结束 {Method} {Path} -> {Status} 耗时 {ElapsedMs:F0}ms user={User} correlation={CorrelationId}",
                    context.Request.Method, context.Request.Path, status, elapsedMs, user, correlationId);
            }
            else
            {
                logger.LogDebug(
                    "HTTP 结束 {Method} {Path} -> {Status} 耗时 {ElapsedMs:F0}ms user={User} correlation={CorrelationId}",
                    context.Request.Method, context.Request.Path, status, elapsedMs, user, correlationId);
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
