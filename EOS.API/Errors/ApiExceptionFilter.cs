using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using EOS.API.Telemetry;
using EOS.API.Security;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Features.Assistant.Admin;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Import;

namespace EOS.API.Errors;

/// <summary>
/// 统一异常出口：把业务校验与资源缺失异常转换为统一错误契约，
/// 控制器不再各自 try/catch 映射错误。
///
/// 映射表是**纯函数** <see cref="Map"/>：控制器只抛语义异常，怎么翻成 HTTP 状态与稳定码
/// 只有这一处；它为纯函数是为了能被单测钉住（控制器直接调用时过滤器不参与，用例无法间接验证）。
/// </summary>
public sealed class ApiExceptionFilter(
    ILogger<ApiExceptionFilter> logger,
    WorkbenchAuditWriter auditWriter,
    EOS.API.Data.WorkbenchDefinitionProvider definitionProvider) : IAsyncExceptionFilter
{
    public async Task OnExceptionAsync(ExceptionContext context)
    {
        var (status, code, message) = Map(context.Exception);

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

    /// <summary>
    /// 异常 → (状态码, 稳定错误码, 展示文案)。未登记的类型返回 `(0, "", "")`，由上层放行成 500，
    /// 并照常走全局异常日志——"没登记"不等于"可以静默吞掉"。
    /// </summary>
    internal static (int Status, string Code, string Message) Map(Exception exception) => exception switch
    {
        KeyNotFoundException ex => (StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, ex.Message),
        UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "当前请求尚未登录或会话已过期。"),
        PermissionDeniedException => (StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "无权执行该操作"),
        ArgumentException ex => (StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, ex.Message),
        DataFilterUnsupportedException ex => (StatusCodes.Status403Forbidden, ApiErrorCodes.DataFilterUnsupported, ex.Message),
        GroupExpressionUnsupportedException ex => (StatusCodes.Status403Forbidden, ApiErrorCodes.GroupExpressionUnsupported, ex.Message),
        PdfDataTooLargeException ex => (StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.PdfDataTooLarge, ex.Message),
        // 关账拦截：单据业务日期落在已关账期间 ⇒ 400（是"这单现在不能这么做"，不是服务器故障）
        PeriodClosedException ex => (StatusCodes.Status400BadRequest, ApiErrorCodes.PeriodClosed, ex.Message),
        // 知识库入库：向量模型未接线 / 厂商侧失败要各自带原因码——把"密钥被拒"混进"未配置"里，
        // 管理员会去反复确认模型配好了没有，而问题在密钥。
        EmbeddingNotConfiguredException ex =>
            (StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.KbEmbeddingNotConfigured, ex.Message),
        AssistantModelException ex =>
            (StatusCodes.Status503ServiceUnavailable, ex.Code, ex.UserMessage),
        // 导入：逐行失败的码由导入服务给（文件问题与请求问题分开）
        ImportFileException ex => (StatusCodes.Status400BadRequest, ex.Code, ex.Message),
        ImportRequestException ex => (StatusCodes.Status400BadRequest, ex.Code, ex.Message),
        // 效果预演：该模块不执行效果链（预演会给出与真实路径不符的报告）⇒ 400；预算超时 ⇒ 504
        EffectSimulationService.UnsupportedModuleException ex =>
            (StatusCodes.Status400BadRequest, ex.Code, ex.Message),
        EffectSimulationService.TimeoutException =>
            (StatusCodes.Status504GatewayTimeout, ApiErrorCodes.SimulationTimeout,
                "预演超时，请缩小单据范围后重试（事务已回滚，库内数据无变化）。"),
        // 助手变更集：试算未通过 ⇒ 400（是"这组改动不能执行"，不是服务器故障）
        ChangeSetBlockedException ex =>
            (StatusCodes.Status400BadRequest, ApiErrorCodes.ChangeSetBlocked, ex.Message),
        _ => (0, string.Empty, string.Empty),
    };
}
