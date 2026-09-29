using EOS.API.Telemetry;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EOS.API.Health;

/// <summary>
/// /health/ready 的文件日志探活：日志目录可写。
/// 报告的是"排障证据是否还在落盘"。**刻意返回 Degraded 而不是 Unhealthy**：
/// 健康检查的状态是全项合成的，任何一项 Unhealthy 都会把 /health/ready 变成 503，
/// 而"日志写不了"不该让整个服务被摘出负载（业务请求仍然正常）。
/// 需要机器可判的失败信号时，读报告体里 log_file 这一项的状态。
/// </summary>
public sealed class LogFileHealthCheck(JsonFileLoggerProvider provider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        if (provider.IsDisabled)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"文件日志不可写：{provider.Path}（本次运行只向控制台输出）。"));
        }
        return Task.FromResult(HealthCheckResult.Healthy($"文件日志可写：{provider.Path}"));
    }
}
