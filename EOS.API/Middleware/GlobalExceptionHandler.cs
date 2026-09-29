using System.Security.Claims;
using EOS.API.Errors;
using EOS.API.Telemetry;
using Microsoft.AspNetCore.Diagnostics;

namespace EOS.API.Middleware;

/// <summary>
/// 全局未处理异常出口：记录异常上下文（路径、用户、关联 ID），
/// 返回稳定的 ProblemDetails；开发环境额外附带完整异常信息便于调试。
/// </summary>
public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IHostEnvironment environment,
    EOS.API.Data.WorkbenchDefinitionProvider definitionProvider)
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

        // 排障关联键以 RequestContext 为唯一真源：请求可能由调用方经 X-Correlation-Id 传入，
        // 直接取 TraceIdentifier 会让"响应里给出的键"在文件日志里查不到这条异常。
        var correlationId = RequestContext.GetCorrelationId(context);
        var moduleId = RequestContext.GetModuleId(context);
        var definitionVersion = moduleId is { } index ? definitionProvider.GetVersion(index) : null;
        logger.LogError(
            exception,
            "未处理异常 {ExceptionType} path={Path} user={User} client={ClientId} module={ModuleId} definitionVersion={DefinitionVersion} correlation={CorrelationId}",
            exception.GetType().Name,
            context.Request.Path,
            user,
            RequestContext.GetClientId(context),
            moduleId,
            definitionVersion,
            correlationId);

        var problem = ApiProblem.Create(
            StatusCodes.Status500InternalServerError,
            ApiErrorCodes.InternalError,
            "服务器内部错误");
        RequestContext.SetErrorCode(context, ApiErrorCodes.InternalError);
        ApiProblem.AttachRequestContext(problem, context);
        if (definitionVersion is not null)
        {
            problem.Extensions["definitionVersion"] = definitionVersion;
        }
        if (environment.IsDevelopment())
        {
            problem.Extensions["detail"] = exception.ToString();
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
