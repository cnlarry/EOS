using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EOS.API.Errors;

/// <summary>
/// 统一异常出口：把业务校验与资源缺失异常转换为统一错误契约，
/// 控制器不再各自 try/catch 映射错误。
/// </summary>
public sealed class ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) : IAsyncExceptionFilter
{
    public Task OnExceptionAsync(ExceptionContext context)
    {
        var (status, code, message) = context.Exception switch
        {
            KeyNotFoundException exception => (StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, exception.Message),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "当前请求尚未登录或会话已过期。"),
            ArgumentException exception => (StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, exception.Message),
            DataFilterUnsupportedException exception => (StatusCodes.Status403Forbidden, "DATA_FILTER_UNSUPPORTED", exception.Message),
            GroupExpressionUnsupportedException exception => (StatusCodes.Status403Forbidden, "GROUP_EXP_UNSUPPORTED", exception.Message),
            _ => (0, string.Empty, string.Empty)
        };

        if (status == 0)
        {
            return Task.CompletedTask;
        }

        logger.LogDebug(
            "请求被拒绝 status={Status} code={Code} message={Message} path={Path} correlation={CorrelationId}",
            status, code, message, context.HttpContext.Request.Path, context.HttpContext.TraceIdentifier);

        var problem = ApiProblem.Create(status, code, message);
        ApiProblem.AttachTraceId(problem, context.HttpContext);
        context.Result = new ObjectResult(problem) { StatusCode = status };
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
}
