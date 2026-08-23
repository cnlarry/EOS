using EOS.API.Data;

namespace EOS.API.Security;

/// <summary>
/// IPermissionService 实现（ADR-005 §6）：包装 LegacyRightsRepository 的模块权限读取。
/// 阶段 1 不加缓存（ADR：权限缓存保持挂起，出现真实外部调用方或指标触发时再启用）。
/// </summary>
public sealed class PermissionService(LegacyRightsRepository rightsRepository) : IPermissionService
{
    public async Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
        => new(await rightsRepository.GetAsync(userId, moduleId, cancellationToken));

    public async Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken)
    {
        var permission = await GetAsync(userId, moduleId, cancellationToken);
        if (!permission.Can(action))
        {
            throw new PermissionDeniedException(userId, moduleId, action);
        }
        return permission;
    }
}
