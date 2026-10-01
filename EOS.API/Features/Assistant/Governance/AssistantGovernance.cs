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
    /// 一元等于多少微元。界面上的预留额以**元**为单位（管理员看得懂的单位），记账用微元；
    /// 换算是实现细节，因此它是常量而不是参数——参数里出现微元只会让人把 0.05 填成 50000。
    /// </summary>
    public const decimal MicroYuanPerYuan = 1_000_000m;

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
///
/// <para>
/// 阈值与冷却**按需读取**（<paramref name="policy"/>）而不是构造期固定：它们来自 3105 的参数
/// （<c>dbo.SYSSS</c>，<c>OWNER_MODULE = 3105</c>），管理员改完应当**立即**生效——把阈值钉在构造期，
/// 会让"阈值改小了却还在按旧值熔断"变成一处无从解释的怪现象，而这个单例活得和进程一样长。
/// </para>
/// </summary>
public sealed class FailureBreaker(
    Func<DateTimeOffset> clock, Func<AssistantCostOptions> policy)
{
    /// <summary>
    /// 固定阈值的便捷构造。给"不关心阈值随配置变化"的场景用（主要是测试）——
    /// 生产侧一律走上面那个按需读取的构造，否则改了 3105 的阈值却还要等重启。
    /// </summary>
    public FailureBreaker(Func<DateTimeOffset> clock, int maxFailures, int cooldownSeconds)
        : this(clock, () => new AssistantCostOptions
        {
            MaxConsecutiveFailures = maxFailures,
            CooldownSeconds = cooldownSeconds,
        })
    {
    }

    private readonly Dictionary<string, (int Failures, DateTimeOffset BlockedUntil)> _state = new();
    private readonly object _lock = new();

    public bool IsBlocked(string userId)
    {
        // 在锁外取策略：读快照是内存操作，但没必要占着锁做件与状态无关的事
        var options = policy();
        lock (_lock)
        {
            return _state.TryGetValue(userId, out var entry)
                && entry.Failures >= options.MaxConsecutiveFailures
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
        var options = policy();
        lock (_lock)
        {
            var failures = _state.TryGetValue(userId, out var entry) ? entry.Failures + 1 : 1;
            _state[userId] = (failures, failures >= options.MaxConsecutiveFailures
                ? clock().AddSeconds(options.CooldownSeconds)
                : DateTimeOffset.MinValue);
        }
    }
}
