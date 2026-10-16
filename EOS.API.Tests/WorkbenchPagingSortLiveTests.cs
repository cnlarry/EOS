using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 工作台列表的分页与排序入参有三种构造性输入会让整页 500：
/// ① 页号取到 <see cref="int"/> 上界时 <c>(page-1)*pageSize</c> 溢出成负数，SQL Server 拒绝负 OFFSET；
/// ② 同一列在排序依据里出现两次，SQL Server 报「在排序依据列表中多次指定了某一列」；
/// ③ 排序参数只有分隔符（如 ",,"）时解析结果为空，拼出空的 ORDER BY。
///
/// 三者都只在构造出来的请求下出现（常规交互到不了这些取值），单测与端点冒烟都不覆盖，
/// 因此用真库固定住：任一条单独写都能因对应的修复被撤掉而失败。
/// 需要 MSSQL_ERP_CONN（与本仓库其它真库测试一致）。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class WorkbenchPagingSortLiveTests
{
    private const string TestUserId = "ADMIN";
    private const int ModuleId = 1201;   // 产品/料件基本资料（单表模块，主表 PRODUCT，主键 PRO_NO）
    private const string SortableField = "PRO_NO";

    // 明细行数超过旧上限（100）的单据：客户计价资料的主键 —— 用来卡住"子表详情被静默截断"。
    private const int DetailModuleId = 1402;
    private const string DetailKeyField = "CLIENT_ID";
    private const string LongDetailKeyValue = "TTI769";

    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_ERP_CONN\\s*=\\s*\"([^\"]+)\"");
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
            throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        return new DbConnectionFactory(config);
    }

    /// <summary>
    /// 页号取到 int 上界：溢出会让 Offset 变负并被 SQL Server 拒绝（500）。
    /// 修复后应当表现为"翻过了最后一页"——空结果集、不抛异常。
    /// </summary>
    [Fact]
    public async Task PageAtIntUpperBound_ReturnsEmptyInsteadOfFailing()
    {
        var composer = await CreateComposerAsync();

        var data = await composer.GetRowsAsync(
            definition: await LoadDefinitionAsync(Connections()),
            detail: false,
            keys: new Dictionary<string, string>(),
            page: int.MaxValue,
            pageSize: 100,
            token: CancellationToken.None);

        Assert.Empty(data.Rows);
    }

    /// <summary>
    /// 同一列被排序两次：SQL Server 直接报错（→ 500）。去重后按首次出现生效，正常出结果。
    /// </summary>
    [Fact]
    public async Task DuplicateSortField_IsDeduplicatedInsteadOfFailing()
    {
        var composer = await CreateComposerAsync();

        var data = await composer.GetRowsAsync(
            definition: await LoadDefinitionAsync(Connections()),
            detail: false,
            keys: new Dictionary<string, string>(),
            page: 1,
            pageSize: 20,
            token: CancellationToken.None,
            sortField: $"{SortableField},{SortableField}",
            sortDirection: "asc,asc");

        Assert.NotEmpty(data.Rows);
    }

    /// <summary>
    /// 排序参数只剩分隔符：解析结果为空会拼出空的 ORDER BY（→ 500），应当回落到默认排序。
    /// </summary>
    [Fact]
    public async Task EmptyishSortField_FallsBackToDefaultSort()
    {
        var composer = await CreateComposerAsync();

        var data = await composer.GetRowsAsync(
            definition: await LoadDefinitionAsync(Connections()),
            detail: false,
            keys: new Dictionary<string, string>(),
            page: 1,
            pageSize: 20,
            token: CancellationToken.None,
            sortField: ",,",
            sortDirection: string.Empty);

        Assert.NotEmpty(data.Rows);
    }

    /// <summary>
    /// 子表详情曾经硬编码 100 行：明细超过 100 行时被静默截断——<c>total</c> 是全量行数（一屏上还显示着
    /// "共 112 条"），<c>rows</c> 却只有 100 条，响应里也没有任何"还有更多"的信号，端点还没有翻页参数。
    /// 这里用库里一张明细确实超过旧上限的单据把它卡住，防止再退回去。
    /// </summary>
    [Fact]
    public async Task DetailRowsBeyondOldLimit_AreNotSilentlyTruncated()
    {
        const int oldLimit = 100;
        var definition = await LoadDefinitionAsync(Connections(), DetailModuleId);
        var composer = await CreateComposerAsync();

        var data = await composer.GetRowsAsync(
            definition: definition,
            detail: true,
            keys: new Dictionary<string, string> { [DetailKeyField] = LongDetailKeyValue },
            page: 1,
            pageSize: 500,
            token: CancellationToken.None);

        // 前置：场景必须真的超过旧上限，否则"rows 等于 total"会退化成恒真的假绿
        Assert.True(data.Total > oldLimit,
            $"本用例依赖一张明细超过旧上限 {oldLimit} 的单据；实测 total={data.Total}，请换一张更长的单据");
        Assert.Equal(500, data.PageSize);
        Assert.Equal(data.Total, data.Rows.Count);
    }

    private static async Task<WorkbenchQueryComposer> CreateComposerAsync()
    {
        var connections = Connections();
        var composer = new WorkbenchQueryComposer(
            connections,
            new WorkbenchScopeFilter(new ApiMetrics()),
            new ApiMetrics(),
            new WorkbenchVirtualColumnResolver(),
            NullLogger<WorkbenchQueryComposer>.Instance);
        await Task.CompletedTask;
        return composer;
    }

    private static async Task<WorkbenchDefinition> LoadDefinitionAsync(DbConnectionFactory connections, int moduleId = ModuleId)
    {
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var builder = new WorkbenchDefinitionBuilder(connections, provider,
            TestFormCapability.None, NullLogger<WorkbenchDefinitionBuilder>.Instance);
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definition = await builder.GetDefinitionAsync(moduleId, TestUserId, "Z", true, true,
            denied, denied, CancellationToken.None);
        Assert.NotNull(definition);
        return definition!;
    }
}
