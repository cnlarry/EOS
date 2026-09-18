using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 发布链路重派生回归：发布/校验所用定义必须从「当前代码 + 元数据」重建模块级字段
/// （BusinessRule、DetailNoSave、系统列等），不能继承已发布快照基线——否则元数据改了却
/// 没重发布，快照会继续指向已经不存在的过程/配置。
///
/// 核心断言：
///  1. 发布路径（forPublish=true）忽略 provider 基线，按代码 + 元数据重建业务规则；
///  2. provider 单模块刷新在「库里已无当前快照」时把该模块基线从缓存移除。
///
/// 需要 EOS_ERP_TEST_CONNECTION（与本仓库其它真库测试一致的约定）。
/// </summary>
[Collection("live-database")]
public sealed class WorkbenchDefinitionPublishRebuildTests
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_CONNECTION_STRING\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static DbConnectionFactory Connections()
    {
        if (ConnectionString.Value is null)
        {
            throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        return new DbConnectionFactory(config);
    }

    private static WorkbenchDefinitionBuilder Builder(DbConnectionFactory connections, WorkbenchDefinitionProvider provider)
        => new(connections, provider,
            Options.Create(new UnifiedFormEditorSettings()),
            NullLogger<WorkbenchDefinitionBuilder>.Instance);

    /// <summary>
    /// 发布路径按代码 + 元数据重建业务规则，且不因 provider 缓存里的旧基线而改变：
    /// 构造一个内存基线（BusinessRule 声明了已不存在的保存后过程），发布路径必须
    /// 产出与元数据一致的结果——180206 的保存期行为已在校验目录里（`CatalogAfterSaveMap`），
    /// 故发布产出 `AfterSaveSproc=null`、`SprocPendingPorting=false`，而运行时路径仍读基线。
    /// 该用例证明的是"发布忽略基线、按元数据重建"，与具体字段无关。
    /// </summary>
    [Fact]
    public async Task PublishBuild_IgnoresBaselineAndRebuildsBusinessRule()
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var builder = Builder(connections, provider);
        var emptyDenied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 造一个「快照声明了幽灵保存后过程」的旧基线，塞进 provider 缓存（模拟删码后未重发布的脏快照）。
        var staleBaseline = new WorkbenchDefinition(
            ModuleId: 180206, Title: "员工申请单", MasterTable: "HR_APPLY_M", DetailTable: "HR_APPLY_D",
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true,
            DetailNoSave: false, MasterPkOrder: ["APPLY_TYPE", "APPLY_NO"], DetailNoFields: "", HasWorkflow: false,
            UserId: "", ExecTag: "Z",
            BusinessRule: new ModuleBusinessRule(180206, "P_GHOST_After_Save", null, false, null, null,
                SprocPendingPorting: true));
        provider.SeedBaselineForTest(180206, staleBaseline, "module-180206-v999");

        // 发布路径：忽略基线，业务规则按当前元数据 + 目录承接重建。
        var publish = await builder.GetDefinitionAsync(180206, "admin", "Z", true, true, emptyDenied, emptyDenied, CancellationToken.None, forPublish: true);
        Assert.NotNull(publish);
        Assert.Null(publish!.BusinessRule?.AfterSaveSproc);
        Assert.False(publish.BusinessRule?.SprocPendingPorting ?? true);

        // 运行时路径（forPublish=false）：有基线时仍走基线（已发布快照为运行时事实源）。
        var runtime = await builder.GetDefinitionAsync(180206, "admin", "Z", true, true, emptyDenied, emptyDenied, CancellationToken.None);
        Assert.NotNull(runtime);
        Assert.Equal("P_GHOST_After_Save", runtime!.BusinessRule?.AfterSaveSproc);
    }

    /// <summary>
    /// provider 单模块刷新：库里已无当前快照时把该模块基线从缓存移除（发布后重派生的边界）。
    /// 构造一个临时快照（不存在的模块号，避免触碰真实快照）→ RefreshModuleAsync 加载 →
    /// 删除当前快照 → RefreshModuleAsync 移除。
    /// </summary>
    [Fact]
    public async Task RefreshModuleAsync_RemovesBaseline_WhenNoCurrentSnapshotInDb()
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);

        const int moduleId = 399999;
        const string json = """
            {"ModuleId":399999,"Title":"测试模块","MasterTable":"CURR","MasterFields":[],"DetailFields":[],
             "DetailNoSave":false,"MasterPkOrder":["CURR_ID"],"DetailNoFields":"","HasWorkflow":false,
             "UserId":"","ExecTag":"Z"}
            """;
        await using (var connection = new SqlConnection(ConnectionString.Value))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                INSERT INTO dbo.WORKBENCH_DEFINITION_SNAPSHOT
                    (MODULE_ID, VERSION, DEFINITION_JSON, SOURCE_METADATA_VERSION, VALIDATION_STATUS,
                     VALIDATION_REPORT_JSON, PUBLISHED_BY, PUBLISHED_AT, IS_CURRENT)
                VALUES (@Id, 1, @Json, NULL, N'PASS', N'[]', N'publish-rebuild-test', SYSDATETIME(), 1);
                """, connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
            command.Parameters.Add("@Json", SqlDbType.NVarChar, -1).Value = json;
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await provider.RefreshModuleAsync(moduleId, CancellationToken.None);
            Assert.True(provider.TryGetBaseline(moduleId, out _, out _), "存在当前快照时应加载基线");

            await using (var connection = new SqlConnection(ConnectionString.Value))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    "UPDATE dbo.WORKBENCH_DEFINITION_SNAPSHOT SET IS_CURRENT=0 WHERE MODULE_ID=@Id AND IS_CURRENT=1;",
                    connection);
                command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
                await command.ExecuteNonQueryAsync();
            }

            await provider.RefreshModuleAsync(moduleId, CancellationToken.None);
            Assert.False(provider.TryGetBaseline(moduleId, out _, out _), "库里已无当前快照时应移除基线缓存");
        }
        finally
        {
            await using var connection = new SqlConnection(ConnectionString.Value);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID=@Id AND PUBLISHED_BY=N'publish-rebuild-test';",
                connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
            await command.ExecuteNonQueryAsync();
        }
    }
}
