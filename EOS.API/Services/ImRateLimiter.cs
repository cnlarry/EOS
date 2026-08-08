using System.Collections.Concurrent;

namespace EOS.API.Services;

/// <summary>
/// IM 发送限流：每用户每分钟允许的消息条数（固定窗口）。
/// 配置项 Im:RateLimit:MaxMessagesPerMinute，默认 60。
/// 防刷屏与滥用；限流只作用于发送路径，不阻塞历史/搜索等读操作。
/// </summary>
public sealed class ImRateLimiter(IConfiguration configuration)
{
    private readonly int _maxPerMinute =
        Math.Max(1, configuration.GetValue("Im:RateLimit:MaxMessagesPerMinute", 60));

    private readonly ConcurrentDictionary<string, (DateTimeOffset WindowStart, int Count)> _counters = new();

    /// <summary>尝试消耗一次发送配额；超限返回 false。</summary>
    public bool TryConsume(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        var key = userId.Trim();
        var now = DateTimeOffset.UtcNow;
        var entry = _counters.AddOrUpdate(
            key,
            _ => (now, 1),
            (_, existing) =>
                now - existing.WindowStart >= TimeSpan.FromMinutes(1)
                    ? (now, 1)
                    : (existing.WindowStart, existing.Count + 1));
        return entry.Count <= _maxPerMinute;
    }

    /// <summary>清除某用户计数（测试与限流解除用）。</summary>
    public void Reset(string userId)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            _counters.TryRemove(userId.Trim(), out _);
        }
    }
}
