using EOS.API.Telemetry;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EOS.API.Errors;

/// <summary>
/// 把控制器里裸写的 <c>Forbid()</c>（ForbidResult）统一换成带错误码与人话说明的 ProblemDetails。
/// </summary>
/// <remarks>
/// 裸 <c>Forbid()</c> 的响应体为空，带来两个后果：前端 httpTransport 取不到
/// message/detail/title，只能显示兜底文案「请求失败。」；同时它不携带 error.code，
/// 按 RequestLoggingMiddleware 的分级规则只记 Information（文件日志仅保留 Warning+），
/// 于是表现为「界面在报错、文件日志里查不到」，排障时只能靠猜。
/// 换成 ProblemDetails 后由 ProblemDetailsContextFilter 附加请求上下文（correlationId 等）
/// 并登记错误码，拒绝原因因此既能在界面上读到，也能在日志里按号检索。
/// </remarks>
public sealed class ForbiddenProblemFilter : IAlwaysRunResultFilter
{
    /// <summary>
    /// 控制器可经 <see cref="ForbidReasonExtensions.ForbidWithReason"/> 放入本次拒绝的具体原因，
    /// 用于替代默认的兜底说明（例如点名缺的是哪个模块的哪个权限位）。
    /// </summary>
    public const string ReasonKey = "EOS.ForbidReason";

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ForbidResult)
        {
            return;
        }

        context.Result = new ObjectResult(ApiProblem.Forbidden(Describe(context.HttpContext)))
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private static string Describe(HttpContext context)
    {
        if (context.Items.TryGetValue(ReasonKey, out var value) && value is string reason && reason.Length > 0)
        {
            return reason;
        }

        // 权限不足属于「用户自己为什么做不了这件事」，按既有口径必须明说，不笼统带过。
        var where = RequestContext.GetModuleId(context) is { } moduleId
            ? $"（模块 {moduleId}）"
            : RequestContext.GetAction(context) is { } action ? $"（{action}）" : string.Empty;
        return $"无权执行该操作：当前账号缺少该操作所需的权限{where}。请联系管理员开通对应权限位。";
    }
}

/// <summary>
/// 给 <c>Forbid()</c> 补一句具体原因的扩展方法：调用点比过滤器更清楚缺的到底是哪个权限位。
/// </summary>
public static class ForbidReasonExtensions
{
    /// <summary>带具体原因地拒绝，说明文案经 ForbiddenProblemFilter 落到响应体的 message。</summary>
    public static IActionResult ForbidWithReason(this ControllerBase controller, string reason)
    {
        controller.HttpContext.Items[ForbiddenProblemFilter.ReasonKey] = reason;
        return controller.Forbid();
    }
}
