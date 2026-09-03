using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EOS.API.Health;

/// <summary>
/// /health/ready 的必要配置探活：连接串与运行必需配置存在。
/// </summary>
public sealed class ConfigurationHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("ErpDatabase")))
        {
            missing.Add("ConnectionStrings:ErpDatabase");
        }
        if (string.IsNullOrWhiteSpace(configuration["DataProtection:ApplicationName"]))
        {
            missing.Add("DataProtection:ApplicationName");
        }

        return Task.FromResult(missing.Count == 0
            ? HealthCheckResult.Healthy("必要配置存在。")
            : HealthCheckResult.Unhealthy($"缺少必要配置：{string.Join("、", missing)}。"));
    }
}
