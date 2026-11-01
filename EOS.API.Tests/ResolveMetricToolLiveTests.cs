using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Features.Assistant.Metrics;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// `resolve_metric` 的端到端真库用例：真实仓储 + 真实定义装配 + 真实权限 + 真实执行器，
/// 只把"定义来源"换成工作台定义构建器的窄包装。
///
/// 钉住的是"口径在真实模块上算得出来，且数与手写 SQL 一致"——`sales_amount` 的指标列在明细
/// `COP_ORDER_D`、维度与行过滤在主表 `COP_ORDER_M`，任何一环被误判（列不可见、主表列不认、
/// 行过滤表归属不符）都会让这条用例红。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class ResolveMetricToolLiveTests
{
    private const string MetricId = "sales_amount";
    private const string AdminUser = "admin";

    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text,
                "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_ERP_CONN\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static (DbConnectionFactory Connections, IConfiguration Config) Services()
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
        return (new DbConnectionFactory(config), config);
    }

    /// <summary>定义来源：真实构建器；其余能力本用例不触达。</summary>
    private sealed class BuilderGateway(WorkbenchDefinitionBuilder builder) : IWorkbenchSearchGateway
    {
        public Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, CancellationToken token) =>
            builder.GetDefinitionAsync(moduleId, userId, execTag, canViewCost, canViewSecrecy,
                deniedMasterFields, deniedDetailFields, token);

        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<WorkbenchData> GetRowsAsync(WorkbenchDefinition definition, bool detail,
            IReadOnlyDictionary<string, string> keys, int page, int pageSize, CancellationToken token,
            WorkbenchQuery? query = null, string? keyword = null, string? sortField = null,
            string? sortDirection = null, int? groupIndex = null, string? groupValue = null,
            string? dataFilter = null) => throw new NotSupportedException();

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupIndex = null, string? groupValue = null, IReadOnlyList<WorkbenchField>? exportFields = null,
            string? dataFilter = null) => throw new NotSupportedException();

        public Task<FormDefinition?> GetFormDefinitionAsync(WorkbenchDefinition definition, string userId, string mode,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, IReadOnlySet<string> deniedNewMasterFields,
            IReadOnlySet<string> deniedNewDetailFields, IReadOnlySet<string> deniedModiMasterFields,
            IReadOnlySet<string> deniedModiDetailFields, CancellationToken token, bool canAddNew = false,
            bool canEdit = false, bool canDelete = false, bool canApprove = false, bool canDeapprove = false,
            bool canEndCase = false, bool canUnEndCase = false, bool canFileView = false, bool canFileUpda = false,
            bool canFileEdit = false, bool canFileDele = false, bool canSetup = false) =>
            throw new NotSupportedException();
    }

    private static ResolveMetricTool CreateTool(DbConnectionFactory connections, IConfiguration config)
    {
        var builder = new WorkbenchDefinitionBuilder(connections,
            new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
            TestFormCapability.None, TestBusinessConfig.Reader(connections),
            NullLogger<WorkbenchDefinitionBuilder>.Instance);
        var probe = new SysMetricSchemaProbe(connections);
        return new ResolveMetricTool(
            new MetricRepository(connections),
            new MetricExecutor(connections),
            probe,
            new MetricDefinitionValidator(probe),
            new MetricFieldWhitelist(connections),
            new PermissionService(new ModuleRightsRepository(connections, NullLogger<ModuleRightsRepository>.Instance),
                new PermissionCache(config)),
            new BuilderGateway(builder),
            new WorkbenchScopeFilter(new ApiMetrics()));
    }

    private static async Task<decimal?> SumApprovedAsync(SqlConnection connection, string? clientId)
    {
        await using var command = new SqlCommand("""
            SELECT SUM(d.AMOUNT_TAX)
            FROM dbo.COP_ORDER_D d WITH (NOLOCK)
            INNER JOIN dbo.COP_ORDER_M m WITH (NOLOCK)
                    ON m.ORDER_TYPE=d.ORDER_TYPE AND m.ORDER_NO=d.ORDER_NO
            WHERE m.APPROVED=1 AND (@ClientId IS NULL OR m.CLIENT_ID=@ClientId);
            """, connection);
        command.Parameters.Add(new SqlParameter("@ClientId", System.Data.SqlDbType.NVarChar, 32)
        {
            Value = (object?)clientId ?? DBNull.Value,
        });
        var reference = await command.ExecuteScalarAsync();
        return reference is null or DBNull ? null : Convert.ToDecimal(reference);
    }

    [Fact]
    public async Task Sales_Amount_Computes_And_Matches_Reference_Query()
    {
        var (connections, config) = Services();
        var tool = CreateTool(connections, config);

        var result = await tool.ExecuteAsync(AdminUser,
            JsonSerializer.Deserialize<JsonElement>($$"""{"metric_id":"{{MetricId}}"}"""), CancellationToken.None);

        Assert.True(result.Ok, result.ContentForModel);

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        Assert.Equal(await SumApprovedAsync(connection, null), ParseValue(result.ContentForModel));
    }

    /// <summary>
    /// 业务词解析：用户/模型说「营业额」而不是标识时，命中唯一已确认口径就直接算，
    /// 且必须与按标识计算的结果一致（省掉一次 enum_metrics 往返，但不能算成别的口径）。
    /// </summary>
    [Fact]
    public async Task Business_Word_Resolves_To_Same_Metric_And_Value()
    {
        var (connections, config) = Services();
        var tool = CreateTool(connections, config);

        var byId = await tool.ExecuteAsync(AdminUser,
            JsonSerializer.Deserialize<JsonElement>($$"""{"metric_id":"{{MetricId}}"}"""), CancellationToken.None);
        var byWord = await tool.ExecuteAsync(AdminUser,
            JsonSerializer.Deserialize<JsonElement>("""{"metric_id":"营业额"}"""), CancellationToken.None);

        Assert.True(byId.Ok, byId.ContentForModel);
        Assert.True(byWord.Ok, byWord.ContentForModel);
        Assert.Contains("按业务词「营业额」解析", byWord.ContentForModel);
        Assert.Equal(ParseValue(byId.ContentForModel), ParseValue(byWord.ContentForModel));
    }

    /// <summary>
    /// 中文维度名：模型说的是"订单日期"，`FIELDS.F_DESC` 就是它——必须映射回 ORDER_DATE 再算，
    /// 而不是回一句"维度名不符合标识符规则"让它自己猜（实测那样会让它省掉过滤重算，
    /// 答出全时间范围的数字）。结果须与按标识过滤一致，且证据里带可用维度。
    /// </summary>
    [Fact]
    public async Task Chinese_Dimension_Label_Computes_Same_Value_As_Key()
    {
        var (connections, config) = Services();
        var tool = CreateTool(connections, config);

        var byKey = await tool.ExecuteAsync(AdminUser, JsonSerializer.Deserialize<JsonElement>(
            """{"metric_id":"sales_amount","dimensions":{"ORDER_DATE":{"from":"2026-09-01","to":"2026-09-30"}}}"""),
            CancellationToken.None);
        var byLabel = await tool.ExecuteAsync(AdminUser, JsonSerializer.Deserialize<JsonElement>(
            """{"metric_id":"sales_amount","dimensions":{"订单日期":{"from":"2026-09-01","to":"2026-09-30"}}}"""),
            CancellationToken.None);

        Assert.True(byKey.Ok, byKey.ContentForModel);
        Assert.True(byLabel.Ok, byLabel.ContentForModel);
        Assert.Equal(ParseValue(byKey.ContentForModel), ParseValue(byLabel.ContentForModel));
        Assert.Contains("可用维度 ORDER_DATE（订单日期）", byLabel.ContentForModel);
    }

    /// <summary>
    /// 按单据取数（P2）：口径维度里通常没有单号，"这张单多少钱"必须能精确回答。
    /// 用完整主键过滤后，结果等于该单明细的合计。
    /// </summary>
    [Fact]
    public async Task Record_Filter_Matches_Document_Detail_Total()
    {
        var (connections, config) = Services();
        string type, no;
        await using (var connection = new SqlConnection(ConnectionString.Value))
        {
            await connection.OpenAsync();
            await using var sample = new SqlCommand("""
                SELECT TOP (1) LTRIM(RTRIM(m.PURCHASE_TYPE)), LTRIM(RTRIM(m.PURCHASE_NO))
                FROM dbo.PUR_PURCHASE_M m WITH (NOLOCK)
                WHERE EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_D d WITH (NOLOCK)
                              WHERE d.PURCHASE_TYPE=m.PURCHASE_TYPE AND d.PURCHASE_NO=m.PURCHASE_NO)
                ORDER BY m.PURCHASE_NO DESC;
                """, connection);
            await using var reader = await sample.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "采购单里没有带明细的单据，无法取样。");
            type = reader.GetString(0);
            no = reader.GetString(1);
        }

        var tool = CreateTool(connections, config);
        var args = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
        {
            metric_id = "purchase_amount",
            record = new Dictionary<string, string> { ["PURCHASE_TYPE"] = type, ["PURCHASE_NO"] = no },
        }));
        var result = await tool.ExecuteAsync(AdminUser, args, CancellationToken.None);

        Assert.True(result.Ok, result.ContentForModel);
        Assert.Contains("按单据主键精确过滤", result.ContentForModel);

        await using var check = new SqlConnection(ConnectionString.Value);
        await check.OpenAsync();
        await using var sum = new SqlCommand("""
            SELECT SUM(AMOUNT_TAX) FROM dbo.PUR_PURCHASE_D WITH (NOLOCK)
            WHERE PURCHASE_TYPE=@T AND PURCHASE_NO=@N;
            """, check);
        sum.Parameters.Add(new SqlParameter("@T", System.Data.SqlDbType.NVarChar, 8) { Value = type });
        sum.Parameters.Add(new SqlParameter("@N", System.Data.SqlDbType.NVarChar, 64) { Value = no });
        var expected = await sum.ExecuteScalarAsync();
        Assert.Equal(expected is null or DBNull ? null : Convert.ToDecimal(expected),
            ParseValue(result.ContentForModel));
    }

    /// <summary>
    /// 维度在主表（`CLIENT_ID`）、指标在明细：过滤必须落到主表，且数与手写 SQL 一致。
    /// 取样值来自真实数据（已批核订单里第一条客户），数据变化不会让用例失效。
    /// </summary>
    [Fact]
    public async Task Master_Dimension_Filters_And_Matches_Reference_Query()
    {
        var (connections, config) = Services();
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using (var sample = new SqlCommand("""
            SELECT TOP (1) LTRIM(RTRIM(m.CLIENT_ID))
            FROM dbo.COP_ORDER_M m WITH (NOLOCK)
            WHERE m.APPROVED=1 AND NULLIF(LTRIM(RTRIM(m.CLIENT_ID)), N'') IS NOT NULL
            ORDER BY m.CLIENT_ID;
            """, connection))
        {
            var clientId = (string?)await sample.ExecuteScalarAsync();
            Assert.False(string.IsNullOrWhiteSpace(clientId), "已批核订单里没有客户可用作维度取样。");

            var tool = CreateTool(connections, config);
            var args = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(new { metric_id = MetricId, dimensions = new { CLIENT_ID = clientId } }));
            var result = await tool.ExecuteAsync(AdminUser, args, CancellationToken.None);

            Assert.True(result.Ok, result.ContentForModel);
            Assert.Equal(await SumApprovedAsync(connection, clientId), ParseValue(result.ContentForModel));
        }
    }

    /// <summary>工具正文形如「口径 sales_amount（营业额）计算结果：1234.5」。</summary>
    private static decimal? ParseValue(string content)
    {
        var match = Regex.Match(content, @"计算结果：(-?\d+(?:\.\d+)?)");
        if (!match.Success) return null;
        return decimal.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
