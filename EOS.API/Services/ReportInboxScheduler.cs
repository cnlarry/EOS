using System.Diagnostics;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Extensions.Options;

namespace EOS.API.Services;

/// <summary>
/// 报表中心调度订阅后台服务：周期扫描 REPORT_SUBSCRIPTION，
/// 到期订阅以订阅者身份生成报表 PDF 入 REPORT_INBOX。
/// 调度为进程内定时轮询（无 Hangfire/Quartz 依赖），随 EOS.API 启动运行。
/// 节流：每次最多处理 10 个到期订阅，间隔由 ReportInboxSettings.ScanIntervalSeconds 控制。
/// </summary>
public sealed class ReportInboxScheduler(
    IServiceProvider serviceProvider,
    IOptions<ReportInboxSettings> settings,
    ILogger<ReportInboxScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("报表中心调度服务已启动，扫描间隔={Interval}秒", settings.Value.ScanIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueSubscriptionsAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "报表调度扫描异常，下次重试。");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, settings.Value.ScanIntervalSeconds)), stoppingToken);
        }
    }

    private async Task ProcessDueSubscriptionsAsync(CancellationToken token)
    {
        using var scope = serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ReportInboxRepository>();
        var reportRepository = scope.ServiceProvider.GetRequiredService<ReportRepository>();
        var printSettingsRepository = scope.ServiceProvider.GetRequiredService<PrintSettingsRepository>();
        var rightsRepository = scope.ServiceProvider.GetRequiredService<ModuleRightsRepository>();
        var reportPdfService = scope.ServiceProvider.GetRequiredService<ReportPdfService>();

        var now = DateTime.Now;
        var due = await repository.FindDueSubscriptionsAsync(now, token);
        if (due.Count == 0) return;

        var processed = 0;
        foreach (var sub in due)
        {
            if (token.IsCancellationRequested) break;
            if (processed >= 10) break; // 每次最多处理 10 个

            try
            {
                // 1. 获取报表定义（以订阅者身份）
                var definition = await reportRepository.GetDefinitionAsync(
                    sub.ModuleId, sub.UserId, false, false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), null, token);
                if (definition is null) continue;

                // 2. 获取报表元数据（页头/表尾/排序）
                var meta = await printSettingsRepository.GetPdfMetaAsync(sub.ModuleId, sub.ReportId, token);
                if (meta is null) continue;

                // 3. 获取报表权限（订阅者身份）
                var reportRights = await rightsRepository.GetReportAsync(sub.UserId, sub.ModuleId, sub.ReportId, token);
                if (!reportRights.CanPrint) continue;

                // 4. 查询数据（带权限 DATA_FILTER）
                var query = await reportRepository.QueryPdfAsync(
                    definition,
                    new ReportQueryRequest(new Dictionary<int, string?>(), new Dictionary<int, string?>()),
                    definition.ModuleFilter,
                    meta.ReportFilter,
                    reportRights.DataFilter,
                    definition.SortFields,
                    new List<string>(),
                    token);

                // 5. 生成 PDF：与手动打印共用同一个命令式实现。
                // 若日后改用版式解释层，此处必须同步切换——两处同进同退，否则
                // "订阅收到的 PDF"与"手点打印的 PDF"会是两张不同的报表。
                var header = meta.Header;
                var pdf = reportPdfService.Generate(new ReportPdfRenderInput(
                    meta, definition, query, string.Empty, "EOS-BATCH",
                    new List<string>(), false, false, header, meta.TailText));

                // 6. 落盘
                var relativeDir = $"{sub.UserId}";
                var fileName = $"{sub.ModuleId}_{sub.ReportId}_{now:yyyyMMddHHmmss}.pdf";
                var relativePath = $"{relativeDir}/{fileName}";
                var absoluteDir = repository.ResolveInboxPath(relativeDir);
                Directory.CreateDirectory(absoluteDir);
                await System.IO.File.WriteAllBytesAsync(Path.Combine(absoluteDir, fileName), pdf, token);

                // 7. 记录 Inbox
                await repository.RecordInboxAsync(sub.UserId, sub.ModuleId, sub.ReportId, $"{meta.ReportName}（{now:yyyy-MM-dd HH:mm}）", relativePath, token);

                // 8. 更新订阅上次执行时间
                await repository.TouchLastRunAsync(sub.Id, now, token);

                logger.LogInformation("订阅产出：userId={User} module={Module} report={Report} rows={Rows}",
                    sub.UserId, sub.ModuleId, sub.ReportId, query.Total);
                processed++;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "订阅产出失败：userId={User} module={Module} report={Report}",
                    sub.UserId, sub.ModuleId, sub.ReportId);
            }
        }
    }
}