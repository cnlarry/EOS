using System.Reflection;
using DbUp;

namespace EOS.API.Data;

/// <summary>
/// EOS.Mail 邮件任务库迁移入口：启动时用 DbUp 执行 Data/MailMigrations 下的版本化脚本。
/// 未配置 ConnectionStrings:MailDatabase 时静默跳过（本地无邮件任务时不影响其他功能）。
/// 迁移脚本与 EOS.IM 完全独立（不同目录、不同日志表），互不干扰。
/// </summary>
public static class MailDatabaseInitializer
{
    public static void RunIfConfigured(IConfiguration configuration, ILogger logger)
    {
        var connectionString = configuration.GetConnectionString("MailDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogInformation("未配置 ConnectionStrings:MailDatabase，跳过 EOS.Mail 迁移");
            return;
        }

        EnsureDatabase.For.SqlDatabase(connectionString);
        var result = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.Contains(".Data.MailMigrations.", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .WithTransactionPerScript()
            .JournalToSqlTable("dbo", "mail_schema_journal")
            .LogToConsole()
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException("EOS.Mail 数据库迁移失败", result.Error);
        }

        logger.LogInformation("EOS.Mail 数据库迁移完成（{Scripts} 个脚本）", result.Scripts.Count());
    }
}
