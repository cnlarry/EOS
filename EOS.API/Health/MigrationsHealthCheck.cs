using EOS.API.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EOS.API.Health;

/// <summary>
/// /health/startup 与 /health/ready 的迁移状态：
/// EOS.ERP 的 DbUp 启动迁移必须成功，失败即服务不应进入就绪。
/// </summary>
public sealed class MigrationsHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(ErpDatabaseInitializer.LastRunSucceeded
            ? HealthCheckResult.Healthy("EOS.ERP DbUp 迁移已完成。")
            : HealthCheckResult.Unhealthy("EOS.ERP DbUp 迁移未完成或启动时失败。"));
    }
}
