using EOS.API.Data;

namespace EOS.API.Services;

/// <summary>
/// 暂存消息清理任务：每小时删除已到保留期（默认 30 天）的消息。
/// 方案 B 下服务器不承担权威历史，清理是常态而非异常。
/// </summary>
public sealed class ImCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ImCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var messages = scope.ServiceProvider.GetRequiredService<IImMessageRepository>();
                    var removed = await messages.CleanupExpiredAsync(stoppingToken);
                    if (removed > 0)
                    {
                        logger.LogInformation("EOS.IM 清理过期暂存消息 {Count} 条", removed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "EOS.IM 暂存消息清理失败");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }
}
