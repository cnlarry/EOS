using System.Collections.Concurrent;

namespace EOS.API.Hubs;

/// <summary>
/// 进程内用户 → SignalR 连接映射。用于把新加入/被移除的成员即时挂到会话组，
/// 以及将来对禁用账号做主动踢下线。单机部署足够；多实例扩展时替换为 Redis 订阅。
/// </summary>
public sealed class HubUserTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connections =
        new(StringComparer.OrdinalIgnoreCase);

    public void Add(string userId, string connectionId)
    {
        var set = _connections.GetOrAdd(userId.Trim(), _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        set[connectionId] = 0;
    }

    public void Remove(string userId, string connectionId)
    {
        if (_connections.TryGetValue(userId.Trim(), out var set))
        {
            set.TryRemove(connectionId, out _);
            if (set.IsEmpty)
            {
                _connections.TryRemove(userId.Trim(), out _);
            }
        }
    }

    public IReadOnlyList<string> GetConnectionIds(string userId)
        => _connections.TryGetValue(userId.Trim(), out var set)
            ? set.Keys.ToList()
            : [];
}
