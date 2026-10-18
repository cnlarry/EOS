using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.ModuleGroups;
using EOS.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 三个配置面的**系统级总览**的真库契约：总览给出的行必须与库内事实一致（不是缓存、不是抽样），
/// 并且**作用域不串域**——按钮面的总览只含 `MANUAL` 行、动作面只含非 `MANUAL` 行。
///
/// 为什么值得一条真库用例：总览的谓词与保存路径的删除谓词**同源**（`= N'MANUAL'` / `&lt;&gt; N'MANUAL'`）。
/// 两者一旦分叉，界面就会显示一批"保存时不归它管"的行——那是最难被发现的一类错。
/// </summary>
// 只读也要进 `live-database` 集合：它按"总览行数 = 库内行数"断言，
// 与正在增删行的用例并行时会读到中间态（本仓真库用例一律挂这个集合）。
[Collection("live-database")]
[Trait("Category", "Integration")]
public sealed class ConfigOverviewLiveTests
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly ModuleGroupAdminRepository _groups;
    private readonly ModuleBusinessConfigRepository _config;

    public ConfigOverviewLiveTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        var connections = new DbConnectionFactory(config);
        _groups = new ModuleGroupAdminRepository(connections, NullLogger<ModuleGroupAdminRepository>.Instance);
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(
            connections, new HttpContextAccessor(), provider, Options.Create(new AuditSettings()));
        _config = new ModuleBusinessConfigRepository(
            connections, new WorkbenchDirtyMarker(connections), auditWriter,
            new DocumentActionRegistry(Array.Empty<IDocumentUserAction>(), NullLogger<DocumentActionRegistry>.Instance),
            new WorkbenchIdempotency(), LiveFormCapability.FromEnvironment(),
            NullLogger<ModuleBusinessConfigRepository>.Instance);
    }

    [Fact]
    public async Task 分组总览逐行列全库分组且带模块名()
    {
        if (ConnectionString.Value is null) return;

        var overview = await _groups.ReadOverviewAsync(CancellationToken.None);

        await AssertStableAsync(async () => (
            await CountAsync("SELECT COUNT(*) FROM dbo.MODULE_GROUPS;"),
            (await _groups.ReadOverviewAsync(CancellationToken.None)).Groups.Count));
        Assert.All(overview.Groups, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.ModuleDesc));
            Assert.False(string.IsNullOrWhiteSpace(row.Description));
        });
        // 已知事实：170204「其它付款凭证」配了「应付月份」
        Assert.Contains(overview.Groups, row => row.ModuleId == 170204 && row.Description == "应付月份" && row.Available);
        // 左栏模块清单：分组面只对统一工作台（定制页不读 MODULE_GROUPS）；已配条数与库内一致
        await AssertTargetsAsync(
            async () => (await _groups.ReadOverviewAsync(CancellationToken.None)).Targets,
            documentSurface: false);
        Assert.Equal(2, overview.Targets.Single(target => target.ModuleId == 170204).Configured);
    }

    [Fact]
    public async Task 按钮面总览只含按钮行()
    {
        if (ConnectionString.Value is null) return;

        var overview = await _config.ReadOverviewAsync(BusinessConfigScope.Manual, CancellationToken.None);

        await AssertStableAsync(async () => (
            await CountAsync("SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE EVENT_CODE=N'MANUAL';"),
            (await _config.ReadOverviewAsync(BusinessConfigScope.Manual, CancellationToken.None)).Actions.Count));
        Assert.All(overview.Actions, row => Assert.Equal("MANUAL", row.EventCode));
        // 按钮面不碰校验规则（规则归"动作"那一面）
        Assert.Empty(overview.ValidationRules);
        await AssertTargetsAsync(async () => (await _config.ReadOverviewAsync(BusinessConfigScope.Manual, CancellationToken.None)).Targets);
    }

    [Fact]
    public async Task 动作面总览含全部效果链动作与全部校验规则()
    {
        if (ConnectionString.Value is null) return;

        var overview = await _config.ReadOverviewAsync(BusinessConfigScope.Actions, CancellationToken.None);

        await AssertStableAsync(async () =>
        {
            var fresh = await _config.ReadOverviewAsync(BusinessConfigScope.Actions, CancellationToken.None);
            return (
                await CountAsync("SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE EVENT_CODE<>N'MANUAL';"),
                fresh.Actions.Count);
        });
        await AssertStableAsync(async () =>
        {
            var fresh = await _config.ReadOverviewAsync(BusinessConfigScope.Actions, CancellationToken.None);
            return (await CountAsync("SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE;"), fresh.ValidationRules.Count);
        });
        Assert.All(overview.Actions, row => Assert.NotEqual("MANUAL", row.EventCode));
        Assert.All(overview.Actions, row => Assert.False(string.IsNullOrWhiteSpace(row.ModuleDesc)));
        Assert.All(overview.ValidationRules, row => Assert.False(string.IsNullOrWhiteSpace(row.ModuleDesc)));
        await AssertTargetsAsync(async () => (await _config.ReadOverviewAsync(BusinessConfigScope.Actions, CancellationToken.None)).Targets);
        // 已配条数把两侧都算上（动作 + 规则），且与库内该模块的行数一致
        await AssertStableAsync(async () =>
        {
            var fresh = await _config.ReadOverviewAsync(BusinessConfigScope.Actions, CancellationToken.None);
            return (
                await CountAsync(
                    "SELECT (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=170204 AND EVENT_CODE<>N'MANUAL') " +
                    "+ (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX=170204);"),
                fresh.Targets.Single(target => target.ModuleId == 170204).Configured);
        });
    }

    /// <summary>
    /// 左栏模块清单的契约：候选集 = 有主表且**该配置面有消费方**，且逐个带模块名。
    ///
    /// 两处配置面的判据**不同源，这是有意的**：
    ///   · 业务动作/校验规则面：候选集 = **有单据面**（工作台形态，或声明了统一表单能力的定制页）。
    ///     定制页写入仍走同一套管线，效果链与校验目录照常执行（ADR-039 D2）；
    ///   · 模块分组面：候选集 = 工作台形态。分组是列表视图的呈现配置，定制页自己画列表、
    ///     不读 `MODULE_GROUPS`，放进来只会产生"配了不会跑"的行。
    /// </summary>
    private static async Task AssertTargetsAsync(
        Func<Task<IReadOnlyList<ModuleConfigTargetOptionDto>>> readTargets,
        bool documentSurface = true)
    {
        var countSql = documentSurface
            ?
            """
            SELECT COUNT(*)
            FROM dbo.MODULES m
            INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX=m.M_IDX
            WHERE NULLIF(LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))),'') IS NOT NULL
              AND (n.NODE_KIND=N'WORKBENCH' OR ISNULL(m.UNIFIED_FORM_ENABLED,0)=1);
            """
            :
            """
            SELECT COUNT(*)
            FROM dbo.MODULES m
            INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX=m.M_IDX AND n.NODE_KIND=N'WORKBENCH'
            WHERE NULLIF(LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))),'') IS NOT NULL;
            """;
        var expected = await CountAsync(countSql);
        var targets = await readTargets();
        if (expected != targets.Count)
        {
            expected = await CountAsync(countSql);
            targets = await readTargets();
        }
        Assert.Equal(expected, targets.Count);
        Assert.All(targets, target =>
        {
            Assert.False(string.IsNullOrWhiteSpace(target.ModuleDesc));
            Assert.False(string.IsNullOrWhiteSpace(target.MasterTable));
            Assert.True(target.Configured >= 0);
        });
    }

    /// <summary>
    /// "读数 A 应等于读数 B"这类**跨两次查询**的等式：本机其它进程（并行开发、后台任务）随时可能写这些表，
    /// 两次读之间漂移就会偶发不成立。故对不上时把两个读数都重取一次再判——真漏行/多行两次都对不上，
    /// 而外部并发写的漂移会被这一步吸收。
    /// </summary>
    private static async Task AssertStableAsync(Func<Task<(int Expected, int Actual)>> read)
    {
        var (expected, actual) = await read();
        if (expected != actual)
        {
            (expected, actual) = await read();
        }
        Assert.Equal(expected, actual);
    }

    private static async Task<int> CountAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_ERP_CONN\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
