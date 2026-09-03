using Microsoft.AspNetCore.Mvc;
using EOS.API.Telemetry;
using EOS.API.Data;

namespace EOS.API.Errors;

/// <summary>
/// 统一错误响应工厂。以 RFC 7807 ProblemDetails 为信封，code/message/fieldErrors
/// 作为顶层扩展属性输出，兼容文档约定 { code, message, fieldErrors } 与前端
/// 现有的 title/detail/message 解析。
/// </summary>
public static class ApiProblem
{
    public static ProblemDetails Create(
        int status,
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = message,
            Type = "about:blank"
        };
        problem.Extensions["code"] = code;
        problem.Extensions["message"] = message;
        if (fieldErrors is { Count: > 0 })
        {
            problem.Extensions["fieldErrors"] = fieldErrors;
        }
        return problem;
    }

    public static ProblemDetails BadRequest(string message, string code = ApiErrorCodes.InvalidArgument) =>
        Create(StatusCodes.Status400BadRequest, code, message);

    public static ProblemDetails NotFound(string message, string code = ApiErrorCodes.NotFound) =>
        Create(StatusCodes.Status404NotFound, code, message);

    public static ProblemDetails Forbidden(string message = "无权执行该操作", string code = ApiErrorCodes.Forbidden) =>
        Create(StatusCodes.Status403Forbidden, code, message);

    /// <summary>
    /// 附加表单字段级错误。
    /// </summary>
    public static ProblemDetails WithFieldErrors(this ProblemDetails problem, IReadOnlyList<FieldError>? fieldErrors)
    {
        if (fieldErrors is { Count: > 0 })
        {
            problem.Extensions["fieldErrors"] = fieldErrors;
        }
        return problem;
    }

    public static void AttachTraceId(ProblemDetails problem, HttpContext context)
    {
        problem.Extensions["traceId"] = context.TraceIdentifier;
    }

    /// <summary>
    /// 统一请求上下文扩展：traceId / correlationId / clientId /
    /// moduleId（路由携带时）。作为排障关联键随错误响应返回给调用方。
    /// </summary>
    public static void AttachRequestContext(ProblemDetails problem, HttpContext context)
    {
        AttachTraceId(problem, context);
        problem.Extensions["correlationId"] = RequestContext.GetCorrelationId(context);
        problem.Extensions["clientId"] = RequestContext.GetClientId(context);
        var moduleId = RequestContext.GetModuleId(context);
        if (moduleId is not null)
        {
            problem.Extensions["moduleId"] = moduleId;
        }
    }
}
