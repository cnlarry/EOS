using System.Security.Claims;
using EOS.API.Errors;
using Microsoft.AspNetCore.Diagnostics;

namespace EOS.API.Middleware;

/// <summary>
/// 全局未处理异常出口：记录异常上下文（路径、用户、关联 ID），
/// 返回稳定的 ProblemDetails；开发环境额外附带完整异常信息便于调试。
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment environment)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            return false;
        }

        var user = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "?"
            : "anonymous";

        logger.LogError(
            exception,
            "未处理异常 {ExceptionType} path={Path} user={User} correlation={CorrelationId}",
            exception.GetType().Name, context.Request.Path, user, context.TraceIdentifier);

        var problem = ApiProblem.Create(
            StatusCodes.Status500InternalServerError,
            ApiErrorCodes.InternalError,
            "服务器内部错误");
        ApiProblem.AttachTraceId(problem, context);
        if (environment.IsDevelopment())
        {
            problem.Extensions["detail"] = exception.ToString();
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
