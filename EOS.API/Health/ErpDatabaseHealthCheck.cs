using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EOS.API.Health;

/// <summary>
/// /health/ready 的 EOS.ERP 探活（ADR-005 §5.3）：只做轻量 SELECT 1，
/// 不执行重查询；连接超时收敛到 5 秒，避免数据库不可用时健康检查长时间挂起。
/// </summary>
public sealed class ErpDatabaseHealthCheck(DbConnectionFactory connections, ILogger<ErpDatabaseHealthCheck> logger)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connections.Create().ConnectionString)
            {
                ConnectTimeout = 5,
            };
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1;", connection) { CommandTimeout = 5 };
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("EOS.ERP 连接可用。");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "健康检查失败：EOS.ERP 连接不可用");
            return HealthCheckResult.Unhealthy("EOS.ERP 连接不可用。", ex);
        }
    }
}
