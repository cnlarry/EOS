using EOS.API.Errors;
using EOS.API.Telemetry;

namespace EOS.API.Security;

/// <summary>
/// API 写操作的跨站请求（CSRF）纵深防御。
///
/// 对 /api 下的 POST/PUT/PATCH/DELETE：
/// - 请求带 Origin 头时，仅接受同源（与 Host 一致）或在 Security:AllowedOrigins
///   白名单内的来源（如 Vite 开发服务器端口），否则返回 403；
/// - 无 Origin 头的非浏览器客户端不受影响。
/// SameSite=Lax Cookie 已阻断跨站表单提交携带会话，本中间件是第二道防线。
/// </summary>
public sealed class SameOriginGuardMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private static readonly HashSet<string> UnsafeMethods = new(StringComparer.Ordinal)
    {
        HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete,
    };

    private readonly string[] _allowedOrigins =
        configuration.GetSection("Security:AllowedOrigins").Get<string[]>() ?? [];

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/api") && UnsafeMethods.Contains(request.Method))
        {
            var origin = request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) && !IsAllowedOrigin(context, origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/problem+json";
                var problem = ApiProblem.Create(
                    StatusCodes.Status403Forbidden,
                    ApiErrorCodes.Forbidden,
                    "跨站请求被拒绝。");
                ApiProblem.AttachRequestContext(problem, context);
                await context.Response.WriteAsJsonAsync(problem, context.RequestAborted);
                return;
            }
        }
        await next(context);
    }

    private bool IsAllowedOrigin(HttpContext context, string origin)
    {
        if (_allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == context.Request.Host.Port;
    }
}
