using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace EOS.API.Telemetry;

/// <summary>
/// 进程内基础指标（ADR-005 §5.2 最小集，阶段 1 零外部依赖）：
/// HTTP 请求数/错误数/延迟直方图、Workbench 范围过滤拒绝数、慢查询数。
/// 经 GET /metrics 以 Prometheus 文本格式导出（开发环境匿名，生产要求登录）。
/// 指标键即完整 Prometheus 行（metric + labels + 值），渲染时排序保证输出稳定。
/// </summary>
public sealed class ApiMetrics
{
    private static readonly double[] DurationBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _sums = new(StringComparer.Ordinal);

    public void CountHttpRequest(string method, string route, int status)
    {
        _counters.AddOrUpdate($"http_requests_total{{method=\"{Escape(method)}\",route=\"{Escape(route)}\",status=\"{status}\"}}",
            1, (_, current) => current + 1);
        if (status >= 500)
        {
            _counters.AddOrUpdate($"http_errors_total{{method=\"{Escape(method)}\",route=\"{Escape(route)}\"}}",
                1, (_, current) => current + 1);
        }
    }

    public void ObserveHttpDuration(double seconds)
    {
        foreach (var upper in DurationBuckets.Where(bound => seconds <= bound))
        {
            _counters.AddOrUpdate($"http_request_duration_seconds_bucket{{le=\"{Format(upper)}\"}}",
                1, (_, current) => current + 1);
        }
        _counters.AddOrUpdate("http_request_duration_seconds_bucket{le=\"+Inf\"}", 1, (_, current) => current + 1);
        _counters.AddOrUpdate("http_request_duration_seconds_count", 1, (_, current) => current + 1);
        _sums.AddOrUpdate("http_request_duration_seconds_sum", seconds, (_, current) => current + seconds);
    }

    /// <summary>范围过滤拒绝（DATA_FILTER/GROUP_EXP 解析失败或执行范围不可用时 fail-closed）。</summary>
    public void IncrementScopeRejected(string reason)
    {
        _counters.AddOrUpdate($"workbench_scope_rejected_total{{reason=\"{Escape(reason)}\"}}",
            1, (_, current) => current + 1);
    }

    /// <summary>慢查询（>1s）与查询耗时分布（阶段 1 覆盖工作台查询/导出/打印路径）。</summary>
    public void ObserveWorkbenchQuery(double elapsedMs)
    {
        if (elapsedMs > 1000)
        {
            _counters.AddOrUpdate("workbench_slow_query_total", 1, (_, current) => current + 1);
        }
        _counters.AddOrUpdate("workbench_query_elapsed_seconds_count", 1, (_, current) => current + 1);
        _sums.AddOrUpdate("workbench_query_elapsed_seconds_sum", elapsedMs / 1000.0, (_, current) => current + elapsedMs / 1000.0);
    }

    public string RenderPrometheus()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# EOS.API 进程内指标（Prometheus 文本格式，ADR-005 §5.2 最小集）");
        foreach (var pair in _counters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            sb.Append(pair.Key).Append(' ').Append(pair.Value).Append('\n');
        }
        foreach (var pair in _sums.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            sb.Append(pair.Key).Append(' ').Append(pair.Value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
