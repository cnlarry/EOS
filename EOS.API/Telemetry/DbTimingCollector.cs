using System.Diagnostics;

namespace EOS.API.Telemetry;

/// <summary>
/// 每请求数据库耗时累加器。
/// 通过 AsyncLocal 在请求上下文内累加各 Measure() 区间耗时，供请求日志输出；
/// 已接入工作台列表/详情/导出/记录读取与打印数据五条路径，其余路径为 0。
/// </summary>
public sealed class DbTimingCollector
{
    /// <summary>进程内共享实例：AsyncLocal 状态为静态，任意实例操作同一请求上下文。</summary>
    public static DbTimingCollector Instance { get; } = new();

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
            // 嵌套计时：外层已覆盖本段耗时，内层为 no-op，避免重复累计与抛异常
            return NoopScope.Instance;
        }
        state.Active = true;
        state.Timestamp = Stopwatch.GetTimestamp();
        return new MeasureScope(state);
    }

    /// <summary>连接层统计（SqlConnection.RetrieveStatistics ExecutionTime）累计入口。</summary>
    public void AddMilliseconds(double milliseconds)
    {
        if (milliseconds <= 0)
        {
            return;
        }
        var state = Current.Value ??= new State();
        state.TotalMs += milliseconds;
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

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose()
        {
        }
    }

    private sealed class RequestScope : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }
}
