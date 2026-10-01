namespace EOS.API.Features.Assistant.Governance;

/// <summary> 成本与熔断配置（默认值，可按真实数据校准，校准走评估集回归）。</summary>
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

    /// <summary>
    /// 每轮模型调用预留额（微元，默认 0.05 元/轮 = 单轮平均成本上限）。
    /// 单请求实际预留 = 本值 ×（MaxToolRounds + 1），覆盖工具多轮调用的最坏调用次数。
    /// </summary>
    public long ReserveMicroYuanPerRequest { get; set; } = 50_000;
}

public static class AssistantCost
{
    /// <summary>
    /// 计价。<paramref name="inputPerMillion"/> / <paramref name="outputPerMillion"/> 是**当前模型的单价**
    /// （模型行上配的），留空则退回 <paramref name="options"/> 里的全局兜底价。
    ///
    /// <para>
    /// 之所以要按模型算：不同模型单价可以差十倍，而限额熔断（日上限、预留/结算）判定的就是"钱"。
    /// 用一套全局单价去判所有模型，等于把限额变成了一个跟实际花费无关的数字。
    /// </para>
    ///
    /// <para>
    /// 留空退回兜底、而不是按 0 元算，是刻意的：0 元会让 <c>SPENT + RESERVED &lt;= CAP</c> 永远成立，
    /// 日上限就废了。
    /// </para>
    /// </summary>
    public static double Calculate(
        long promptTokens, long completionTokens, AssistantCostOptions options,
        decimal? inputPerMillion = null, decimal? outputPerMillion = null) =>
        promptTokens / 1_000_000.0 * (double)(inputPerMillion ?? (decimal)options.InputPerMillionYuan)
        + completionTokens / 1_000_000.0 * (double)(outputPerMillion ?? (decimal)options.OutputPerMillionYuan);
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
