using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace EOS.API.Security;

public sealed record LoginThrottleOptions
{
    public int MaxFailures { get; init; } = 5;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan Lockout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// 登录失败限流与临时锁定（进程内实现，单实例有效）。
///
/// 以「规范化用户名 + 客户端 IP」为维度，在滑动窗口内失败次数达到阈值后
/// 锁定一段时间。成功登录会清零。多实例部署时应替换为共享存储（如 Redis），
/// 当前规模（单机 ERP）下进程内实现足够，限制已在 README 注明。
/// </summary>
public sealed class LoginThrottleService
{
    private readonly LoginThrottleOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public LoginThrottleService(IOptions<LoginThrottleOptions> options)
        : this(options.Value, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>直接指定选项与时钟（测试注入用；运行时使用默认系统时钟）。</summary>
    public LoginThrottleService(LoginThrottleOptions options, Func<DateTimeOffset> now)
    {
        _options = options;
        _now = now;
    }

    /// <summary>返回剩余锁定时间；未锁定时返回 false。</summary>
    public bool TryGetLockoutRemaining(string key, out TimeSpan remaining)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            remaining = TimeSpan.Zero;
            return false;
        }

        lock (entry)
        {
            if (entry.LockedUntil is not { } lockedUntil || lockedUntil <= _now())
            {
                entry.LockedUntil = null;
                remaining = TimeSpan.Zero;
                return false;
            }
            remaining = lockedUntil - _now();
            return true;
        }
    }

    /// <summary>记录一次失败；达到阈值时开始锁定。返回是否因本次失败进入锁定。</summary>
    public bool RecordFailure(string key)
    {
        Prune();
        var entry = _entries.GetOrAdd(key, static _ => new Entry());
        var now = _now();
        lock (entry)
        {
            entry.Failures.RemoveAll(timestamp => timestamp <= now - _options.Window);
            entry.Failures.Add(now);
            if (entry.LockedUntil is null && entry.Failures.Count >= _options.MaxFailures)
            {
                entry.LockedUntil = now + _options.Lockout;
                return true;
            }
            return false;
        }
    }

    public void Reset(string key)
    {
        _entries.TryRemove(key, out _);
    }

    private void Prune()
    {
        var now = _now();
        foreach (var (key, entry) in _entries)
        {
            lock (entry)
            {
                entry.Failures.RemoveAll(timestamp => timestamp <= now - _options.Window);
                var locked = entry.LockedUntil is { } until && until > now;
                if (!locked && entry.Failures.Count == 0)
                    _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed class Entry
    {
        public List<DateTimeOffset> Failures { get; } = [];
        public DateTimeOffset? LockedUntil { get; set; }
    }
}
