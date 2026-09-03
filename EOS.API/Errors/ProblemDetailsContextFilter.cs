using EOS.API.Data;
using EOS.API.Telemetry;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EOS.API.Errors;

/// <summary>
/// 控制器直接返回的 ProblemDetails 统一附加请求上下文（traceId/correlationId/clientId/moduleId，
/// 路由携带时）并登记错误码；异常路径已由 ApiExceptionFilter / GlobalExceptionHandler 自行附加，
/// 此处重复执行为幂等覆盖。
/// </summary>
public sealed class ProblemDetailsContextFilter(EOS.API.Data.WorkbenchDefinitionProvider definitionProvider) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: ProblemDetails problem })
        {
            ApiProblem.AttachRequestContext(problem, context.HttpContext);
            if (problem.Extensions.TryGetValue("code", out var value) && value is string code)
            {
                RequestContext.SetErrorCode(context.HttpContext, code);
            }
            if (RequestContext.GetModuleId(context.HttpContext) is { } moduleId
                && definitionProvider.GetVersion(moduleId) is { } definitionVersion)
            {
                problem.Extensions["definitionVersion"] = definitionVersion;
            }
        }
        await next();
    }
}
