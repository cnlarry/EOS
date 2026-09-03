using EOS.API.Data;

namespace EOS.API.Security;

/// <summary>
/// IPermissionService 实现：包装 ModuleRightsRepository 的模块权限读取。
/// 当前不加缓存。
/// </summary>
public sealed class PermissionService(ModuleRightsRepository rightsRepository, PermissionCache cache) : IPermissionService
{
    public async Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
    {
        if (cache.TryGet(userId, moduleId, out var cached))
        {
            return cached;
        }
        var permission = new ModulePermission(await rightsRepository.GetAsync(userId, moduleId, cancellationToken));
        cache.Set(userId, moduleId, permission);
        return permission;
    }

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
