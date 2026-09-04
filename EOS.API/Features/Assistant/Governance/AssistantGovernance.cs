namespace EOS.API.Features.Assistant.Governance;

/// <summary>M7 成本与熔断配置（决策 8 默认值，可按真实数据校准，校准走评估集回归）。</summary>
public sealed class AssistantCostOptions
{
    public double GlobalDailyCapYuan { get; set; } = 50;
    public double UserDailyCapYuan { get; set; } = 5;
    public int MaxConsecutiveFailures { get; set; } = 3;
    public int CooldownSeconds { get; set; } = 60;

    /// <summary>每百万 token 输入单价（元）。默认按 deepseek-chat 公示价，可校准。</summary>
    public double InputPerMillionYuan { get; set; } = 1;

    /// <summary>每百万 token 输出单价（元）。</summary>
    public double OutputPerMillionYuan { get; set; } = 2;
}

public static class AssistantCost
{
    public static double Calculate(long promptTokens, long completionTokens, AssistantCostOptions options) =>
        promptTokens / 1_000_000.0 * options.InputPerMillionYuan
        + completionTokens / 1_000_000.0 * options.OutputPerMillionYuan;
}

/// <summary>
/// Consecutive-failure breaker (per user, in-memory; process restart resets).
/// Only technical failures count: model errors/timeouts, empty replies.
/// Permission denials, user cancels and limit trips never count.
/// </summary>
public sealed class FailureBreaker(
    Func<DateTimeOffset> clock, int maxFailures, int cooldownSeconds)
{
    private readonly Dictionary<string, (int Failures, DateTimeOffset BlockedUntil)> _state = new();
    private readonly object _lock = new();

    public bool IsBlocked(string userId)
    {
        lock (_lock)
        {
            return _state.TryGetValue(userId, out var entry)
                && entry.Failures >= maxFailures
                && clock() < entry.BlockedUntil;
        }
    }

    public void RecordSuccess(string userId)
    {
        lock (_lock)
        {
            _state.Remove(userId);
        }
    }

    public void RecordFailure(string userId)
    {
        lock (_lock)
        {
            var failures = _state.TryGetValue(userId, out var entry) ? entry.Failures + 1 : 1;
            _state[userId] = (failures, failures >= maxFailures
                ? clock().AddSeconds(cooldownSeconds)
                : DateTimeOffset.MinValue);
        }
    }
}
