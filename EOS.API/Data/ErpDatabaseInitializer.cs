using System.Reflection;
using DbUp;

namespace EOS.API.Data;

/// <summary>
/// EOS.ERP 数据库迁移入口：启动时用 DbUp 执行 Data/Migrations 下的版本化脚本。
///
/// EOS.ERP 是全新系统的唯一业务数据库（业务逻辑与框架承袭旧 ERP，但按全新系统对待）：
/// 新系统新增的库对象（如表、列、索引、约束）一律经本通道版本化、全大写命名，
/// 不再沿用 update.sql 追加段落的方式（update.sql 冻结为历史升级记录保留）。
/// 本迁移必须成功——EOS.ERP 是唯一库，迁移失败即启动失败。
/// </summary>
public static class ErpDatabaseInitializer
{
    /// <summary>最近一次启动迁移是否成功（/health/startup 与 /health/ready 依据）。</summary>
    public static bool LastRunSucceeded { get; private set; }

    public static void Run(IConfiguration configuration, ILogger logger)
    {
        var connectionString = configuration.GetConnectionString("ErpDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:ErpDatabase 未配置。");

        var result = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.Contains(".Data.Migrations.", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .WithTransactionPerScript()
            .JournalToSqlTable("dbo", "ERP_SCHEMA_JOURNAL")
            .LogToConsole()
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
        {
            LastRunSucceeded = false;
            throw new InvalidOperationException("EOS.ERP 数据库迁移失败", result.Error);
        }

        LastRunSucceeded = true;
        logger.LogInformation("EOS.ERP 数据库迁移完成（{Scripts} 个脚本）", result.Scripts.Count());
    }
}
