using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace EOS.API.Telemetry;

/// <summary>
/// 请求上下文契约（ADR-005 §1/§5.1）：correlationId / clientId / traceId / spanId /
/// moduleId / action / error.code 的统一存取入口，供中间件、异常出口与日志使用。
/// correlationId 由调用方经 X-Correlation-Id 传入（≤128 字符），未携带时回退
/// HttpContext.TraceIdentifier；clientId 经 X-Client-Id 传入并由 ClientIds.Normalize 归一。
/// </summary>
public static class RequestContext
{
    public const string CorrelationIdKey = "EOS.CorrelationId";
    public const string ClientIdKey = "EOS.ClientId";
    public const string ErrorCodeKey = "EOS.ErrorCode";

    public static string GetCorrelationId(HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdKey, out var value) && value is string correlation
            ? correlation
            : context.TraceIdentifier;

    public static string GetClientId(HttpContext context) =>
        context.Items.TryGetValue(ClientIdKey, out var value) && value is string clientId
            ? clientId
            : ClientIds.Unknown;

    public static void SetErrorCode(HttpContext context, string code) =>
        context.Items[ErrorCodeKey] = code;

    public static string? GetErrorCode(HttpContext context) =>
        context.Items.TryGetValue(ErrorCodeKey, out var value) ? value as string : null;

    /// <summary>路由中的 moduleId（如 /api/document-workbench/1405/records）。</summary>
    public static int? GetModuleId(HttpContext context) =>
        context.Request.RouteValues.TryGetValue("moduleId", out var value)
        && int.TryParse(value?.ToString(), out var moduleId)
            ? moduleId
            : null;

    /// <summary>action = controller.action（小写），用于日志/审计/错误响应关联。</summary>
    public static string? GetAction(HttpContext context)
    {
        var descriptor = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        return descriptor is null
            ? null
            : $"{descriptor.ControllerName}.{descriptor.ActionName}".ToLowerInvariant();
    }

    /// <summary>
    /// W3C trace/span 标识：有 Activity 时取 Activity.TraceId/SpanId，
    /// 否则回退 HttpContext.TraceIdentifier（格式 trace:span）。
    /// </summary>
    public static (string TraceId, string SpanId) GetTraceIds(HttpContext context)
    {
        var activity = Activity.Current;
        if (activity is { IdFormat: ActivityIdFormat.W3C })
        {
            return (activity.TraceId.ToString(), activity.SpanId.ToString());
        }

        var parts = context.TraceIdentifier.Split(':');
        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }
}
