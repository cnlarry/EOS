using System.Collections.Concurrent;

namespace EOS.API.Security;

/// <summary>
/// 模块权限短 TTL 缓存：
/// 缓存 key = 用户 + 模块；TTL 默认 60 秒（Security:PermissionCache:TtlSeconds）；
/// 权限管理端写入后经 RightsAdminRepository 调 InvalidateAll 主动失效。
/// 个人覆盖组、组 OR、EXEC_TAG 最大值等聚合规则仍由底层 ModuleRightsRepository 保证。
/// </summary>
public sealed class PermissionCache(IConfiguration configuration)
{
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(configuration.GetValue("Security:PermissionCache:TtlSeconds", 60));
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public bool TryGet(string userId, int moduleId, out ModulePermission permission)
    {
        var key = Key(userId, moduleId);
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            permission = entry.Permission;
            return true;
        }
        _cache.TryRemove(key, out _);
        permission = null!;
        return false;
    }

    public void Set(string userId, int moduleId, ModulePermission permission)
        => _cache[Key(userId, moduleId)] = new CacheEntry(permission, DateTimeOffset.UtcNow.Add(_ttl));

    /// <summary>权限写路径（用户/组/报表权限保存）后全量失效；写操作低频，全清最稳。</summary>
    public void InvalidateAll() => _cache.Clear();

    private static string Key(string userId, int moduleId) => $"{userId}|{moduleId}";

    private sealed record CacheEntry(ModulePermission Permission, DateTimeOffset ExpiresAt);
}
