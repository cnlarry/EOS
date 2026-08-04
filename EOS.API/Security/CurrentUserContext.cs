using System.Security.Claims;

namespace EOS.API.Security;

public sealed class CurrentUserContext(IHttpContextAccessor accessor)
{
    public string UserId => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException("当前请求尚未登录。" );
    public string EmployeeName => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name) ?? UserId;
}
