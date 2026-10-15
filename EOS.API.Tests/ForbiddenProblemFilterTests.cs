using EOS.API.Errors;
using EOS.API.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 拒绝结构化：控制器里裸写的 Forbid() 响应体为空，界面只剩兜底文案、文件日志也查不到；
/// 本组用例钉住「裸 Forbid() 一定被换成带 code/message 的 ProblemDetails」这一不变量，
/// 错误码非空才会被请求日志记为 Error 并落盘。
/// </summary>
public sealed class ForbiddenProblemFilterTests
{
    [Fact]
    public void ForbidResult_IsReplacedByProblemDetails_WithCodeAndMessage()
    {
        var (context, httpContext) = CreateContext(new ForbidResult());

        new ForbiddenProblemFilter().OnResultExecuting(context);

        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.Forbidden, problem.Extensions["code"]);
        Assert.False(string.IsNullOrWhiteSpace(problem.Extensions["message"] as string));
        // 错误码是 ProblemDetailsContextFilter 登记 error.code 的依据，非空才会落文件日志
        Assert.NotNull(RequestContext.GetCorrelationId(httpContext));
    }

    [Fact]
    public void ReasonProvidedByController_TakesPrecedenceOverFallback()
    {
        var (context, httpContext) = CreateContext(new ForbidResult());
        httpContext.Items[ForbiddenProblemFilter.ReasonKey] = "需要「用户权限设定」(2306) 的设置权(CanSetup)。";

        new ForbiddenProblemFilter().OnResultExecuting(context);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(context.Result).Value);
        Assert.Equal("需要「用户权限设定」(2306) 的设置权(CanSetup)。", problem.Extensions["message"]);
    }

    [Fact]
    public void WithoutReason_MessageNamesTheModule()
    {
        var (context, _) = CreateContext(new ForbidResult());
        context.HttpContext.Request.RouteValues["moduleId"] = 2306;

        new ForbiddenProblemFilter().OnResultExecuting(context);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(context.Result).Value);
        Assert.Contains("2306", problem.Extensions["message"] as string);
    }

    [Fact]
    public void NonForbidResult_IsLeftUntouched()
    {
        var original = new OkObjectResult(new { ok = true });
        var (context, _) = CreateContext(original);

        new ForbiddenProblemFilter().OnResultExecuting(context);

        Assert.Same(original, context.Result);
    }

    private static (ResultExecutingContext Context, HttpContext HttpContext) CreateContext(IActionResult result)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[RequestContext.CorrelationIdKey] = "corr-forbid-test";
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ResultExecutingContext(actionContext, [], result, controller: new object());
        return (context, httpContext);
    }
}
