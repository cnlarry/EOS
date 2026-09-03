namespace EOS.API.Security;

/// <summary>
/// 权限拒绝异常：由 ApiExceptionFilter 映射 403 FORBIDDEN。
/// </summary>
public sealed class PermissionDeniedException(string userId, int moduleId, PermissionAction action)
    : Exception($"用户 {userId} 无权执行模块 {moduleId} 的操作 {action}。")
{
    public string UserId { get; } = userId;
    public int ModuleId { get; } = moduleId;
    public PermissionAction Action { get; } = action;
}
