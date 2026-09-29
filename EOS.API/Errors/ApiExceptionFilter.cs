using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using EOS.API.Telemetry;
using EOS.API.Security;
using EOS.API.Data;

namespace EOS.API.Errors;

/// <summary>
/// 统一异常出口：把业务校验与资源缺失异常转换为统一错误契约，
/// 控制器不再各自 try/catch 映射错误。
/// </summary>
public sealed class ApiExceptionFilter(
    ILogger<ApiExceptionFilter> logger,
    WorkbenchAuditWriter auditWriter,
    EOS.API.Data.WorkbenchDefinitionProvider definitionProvider) : IAsyncExceptionFilter
{
    public async Task OnExceptionAsync(ExceptionContext context)
    {
        var (status, code, message) = context.Exception switch
        {
            KeyNotFoundException exception => (StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, exception.Message),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "当前请求尚未登录或会话已过期。"),
            PermissionDeniedException => (StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "无权执行该操作"),
            ArgumentException exception => (StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, exception.Message),
            DataFilterUnsupportedException exception => (StatusCodes.Status403Forbidden, "DATA_FILTER_UNSUPPORTED", exception.Message),
            GroupExpressionUnsupportedException exception => (StatusCodes.Status403Forbidden, "GROUP_EXP_UNSUPPORTED", exception.Message),
            PdfDataTooLargeException exception => (StatusCodes.Status422UnprocessableEntity, "PDF_DATA_TOO_LARGE", exception.Message),
            // 关账拦截：单据业务日期落在已关账期间 ⇒ 400（是"这单现在不能这么做"，不是服务器故障）
            PeriodClosedException exception => (StatusCodes.Status400BadRequest, "PERIOD_CLOSED", exception.Message),
            _ => (0, string.Empty, string.Empty)
        };

        if (status == 0)
        {
            return;
        }

        logger.LogDebug(
            "请求被拒绝 status={Status} code={Code} message={Message} path={Path} correlation={CorrelationId}",
            status, code, message, context.HttpContext.Request.Path,
            RequestContext.GetCorrelationId(context.HttpContext));

        RequestContext.SetErrorCode(context.HttpContext, code);
        var problem = ApiProblem.Create(status, code, message);
        ApiProblem.AttachRequestContext(problem, context.HttpContext);
        if (RequestContext.GetModuleId(context.HttpContext) is { } moduleId
            && definitionProvider.GetVersion(moduleId) is { } definitionVersion)
        {
            problem.Extensions["definitionVersion"] = definitionVersion;
        }
        if (context.Exception is PermissionDeniedException pde)
        {
            await auditWriter.WriteBestEffortAsync(
                RequestContext.GetModuleId(context.HttpContext), pde.UserId, "DENY",
                $"权限拒绝 {pde.Action}（模块 {pde.ModuleId}）", pde.UserId, "PERMISSION",
                result: 0, null, context.HttpContext.RequestAborted);
        }
        context.Result = new ObjectResult(problem) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
