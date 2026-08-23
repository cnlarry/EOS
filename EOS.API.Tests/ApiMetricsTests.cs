using EOS.API.Telemetry;
using Xunit;

namespace EOS.API.Tests;

public sealed class ApiMetricsTests
{
    [Fact]
    public void CountHttpRequest_TracksRequestsAndErrors()
    {
        var metrics = new ApiMetrics();

        metrics.CountHttpRequest("GET", "api/document-workbench/{moduleId:int}/records", 200);
        metrics.CountHttpRequest("GET", "api/document-workbench/{moduleId:int}/records", 500);

        var text = metrics.RenderPrometheus();
        Assert.Contains("http_requests_total", text);
        Assert.Contains("http_errors_total", text);
        Assert.Contains("status=\"500\"", text);
    }

    [Fact]
    public void ObserveHttpDuration_EmitsHistogramBucketsAndCount()
    {
        var metrics = new ApiMetrics();

        metrics.ObserveHttpDuration(0.01);
        metrics.ObserveHttpDuration(0.01);

        var text = metrics.RenderPrometheus();
        Assert.Contains("http_request_duration_seconds_bucket", text);
        Assert.Contains("http_request_duration_seconds_count 2", text);
        Assert.Contains("http_request_duration_seconds_sum", text);
    }

    [Fact]
    public void IncrementScopeRejected_EmitsReasonLabel()
    {
        var metrics = new ApiMetrics();

        metrics.IncrementScopeRejected("data_filter");

        var text = metrics.RenderPrometheus();
        Assert.Contains("workbench_scope_rejected_total{reason=\"data_filter\"} 1", text);
    }
}
