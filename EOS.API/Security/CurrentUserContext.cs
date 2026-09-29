using System.Security.Claims;

namespace EOS.API.Security;

/// <summary>
/// 当前请求的身份：全部取自登录时签发的声明（无新增认证路径）。
/// 声明缺失时为空串而不是抛错——出现"没带工号"的会话不该让整个端点 500。
/// </summary>
public sealed class CurrentUserContext(IHttpContextAccessor accessor)
{
    public string UserId => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException("当前请求尚未登录。" );

    public string EmployeeName => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name) ?? UserId;

    /// <summary>工号（`employee_id` 声明）。</summary>
    public string EmployeeId => Find("employee_id");

    /// <summary>部门号（`department_id` 声明）。</summary>
    public string DepartmentId => Find("department_id");

    /// <summary>部门名（`department_name` 声明）。</summary>
    public string DepartmentName => Find("department_name");

    /// <summary>公司（`company_id` 声明）。</summary>
    public string CompanyId => Find("company_id");

    /// <summary>默认用户组（`default_group_id` 声明）。</summary>
    public string DefaultGroupId => Find("default_group_id");

    private string Find(string claimType)
        => accessor.HttpContext?.User.FindFirstValue(claimType) ?? string.Empty;
}
