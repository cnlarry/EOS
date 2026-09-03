namespace EOS.API.Security;

/// <summary>
/// EOS.API 内唯一模块权限判断入口：
/// 控制器与仓储不再直接调用 ModuleRightsRepository 做模块级权限决策；
/// 个人覆盖组、组布尔 OR、EXEC_TAG 最大值规则由底层聚合保持不变。
/// </summary>
public interface IPermissionService
{
    /// <summary>取模块权限（不校验动作，用于需要区分「无权限→404」与「拒绝→403」的路径）。</summary>
    Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken);

    /// <summary>取模块权限并要求指定动作；不满足时抛 PermissionDeniedException（403）。</summary>
    Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken);
}
