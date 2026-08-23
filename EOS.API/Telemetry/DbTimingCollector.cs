using System.Diagnostics;

namespace EOS.API.Telemetry;

/// <summary>
/// 每请求数据库耗时累加器（ADR-005 §5.1 固定字段 dbElapsedMs）。
/// 通过 AsyncLocal 在请求上下文内累加各 Measure() 区间耗时，供请求日志输出；
/// 阶段 1 已接入工作台列表/详情/导出/记录读取与打印数据五条路径，其余路径为 0。
/// </summary>
public sealed class DbTimingCollector
{
    private static readonly AsyncLocal<State?> Current = new();

    public IDisposable BeginRequest()
    {
        Current.Value = new State();
        return new RequestScope();
    }

    public IDisposable Measure()
    {
        var state = Current.Value ??= new State();
        if (state.Active)
        {
            throw new InvalidOperationException("DbTimingCollector 不支持嵌套计时。");
        }
        state.Active = true;
        state.Timestamp = Stopwatch.GetTimestamp();
        return new MeasureScope(state);
    }

    public double TotalMilliseconds => Current.Value?.TotalMs ?? 0;

    private sealed class State
    {
        public long Timestamp;
        public double TotalMs;
        public bool Active;
    }

    private sealed class MeasureScope(State state) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            state.TotalMs += Stopwatch.GetElapsedTime(state.Timestamp).TotalMilliseconds;
            state.Active = false;
        }
    }

    private sealed class RequestScope : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }
}
