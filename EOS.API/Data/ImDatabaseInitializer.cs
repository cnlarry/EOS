using System.Reflection;
using DbUp;

namespace EOS.API.Data;

/// <summary>
/// EOS.IM 数据库迁移入口：启动时用 DbUp 执行 Data/Migrations 下的版本化脚本。
/// 未配置 ConnectionStrings:ImDatabase 时静默跳过（本地无 IM 时不影响其他功能）。
/// </summary>
public static class ImDatabaseInitializer
{
    public static void RunIfConfigured(IConfiguration configuration, ILogger logger)
    {
        var connectionString = configuration.GetConnectionString("ImDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogInformation("未配置 ConnectionStrings:ImDatabase，跳过 EOS.IM 迁移");
            return;
        }

        EnsureDatabase.For.SqlDatabase(connectionString);
        var result = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.Contains(".Data.Migrations.", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .WithTransactionPerScript()
            .JournalToSqlTable("dbo", "im_schema_journal")
            .LogToConsole()
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException("EOS.IM 数据库迁移失败", result.Error);
        }

        logger.LogInformation("EOS.IM 数据库迁移完成（{Scripts} 个脚本）", result.Scripts.Count());
    }
}
