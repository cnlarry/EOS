using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EOS.API.Health;

/// <summary>
/// /health/ready 的附件存储探活：附件根目录可写。
/// 根目录解析与 AttachmentController 一致（Attachment:StorageRoot 或默认 attachments）。
/// </summary>
public sealed class AttachmentStorageHealthCheck(IConfiguration configuration, ILogger<AttachmentStorageHealthCheck> logger)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            var root = ResolveRoot();
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".health-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Task.FromResult(HealthCheckResult.Healthy($"附件根目录可写：{root}"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "健康检查失败：附件根目录不可写");
            return Task.FromResult(HealthCheckResult.Unhealthy("附件根目录不可写。", ex));
        }
    }

    private string ResolveRoot()
    {
        var configured = configuration["Attachment:StorageRoot"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "attachments"))
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
    }
}
