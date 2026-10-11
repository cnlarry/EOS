using EOS.API.Data.Effects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EOS.API.Tests;

/// <summary>
/// 测试用的选项监视器：效果引擎总闸（ADR-034 起是 <see cref="IOptionsMonitor{T}"/> + 断路器兜底）
/// 在用例里通常只要"一个固定值"，或"一个读就抛的坏监视器"来验证兜底行为。
/// </summary>
internal static class TestOptions
{
    /// <summary>固定值总闸：等价于旧写法里直接塞一个 <see cref="EffectEngineSettings"/>。</summary>
    public static EffectEngineSwitch EngineSwitch(bool enabled) =>
        new(new StaticMonitor<EffectEngineSettings>(new EffectEngineSettings { Enabled = enabled }),
            NullLogger<EffectEngineSwitch>.Instance);

    /// <summary>可切换"读取是否失败"的监视器：验证"坏值沿用上一次有效值"。</summary>
    public sealed class FlakyMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public bool Fail { get; set; }

        public T CurrentValue => Fail ? throw new InvalidOperationException("EffectEngine 段取了坏值") : value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
