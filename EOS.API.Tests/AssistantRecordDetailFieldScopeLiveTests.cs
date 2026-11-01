using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data;
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
/// `get_record_detail` 的列顺序：知识型读取按物理列序，而不是个人列序。
///
/// 背景：明细行每行只渲染前 8 列（`DetailRowMaxColumns`），详情工具原先直接复用"列表定义"，
/// 其列序是该账号的个人列序（实测是字母序）。于是问"采购单 CGD26100010 有哪些明细"，
/// 明细只回了金额/件数/库别，料号与数量被截断在外——模型只能照实说"明细里没有料号、数量"。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class AssistantRecordDetailFieldScopeLiveTests
{
    private const int PurchaseModuleId = 1606;
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

    private static WorkbenchDefinitionBuilder Builder(DbConnectionFactory connections) => new(
        connections,
        new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
        TestFormCapability.None, TestBusinessConfig.Reader(connections),
        NullLogger<WorkbenchDefinitionBuilder>.Instance);

    /// <summary>定义来自构建器（两种口径各一路），取行/取明细接真查询组合器。</summary>
    private sealed class LiveGateway(WorkbenchDefinitionBuilder builder, WorkbenchQueryComposer composer)
        : IWorkbenchSearchGateway
    {
        public Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, CancellationToken token) =>
            builder.GetDefinitionAsync(moduleId, userId, execTag, canViewCost, canViewSecrecy,
                deniedMasterFields, deniedDetailFields, token);

        public Task<WorkbenchDefinition?> GetAssistantDefinitionAsync(int moduleId, string userId, string? execTag,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, CancellationToken token) =>
            builder.GetDefinitionAsync(moduleId, userId, execTag, canViewCost, canViewSecrecy,
                deniedMasterFields, deniedDetailFields, token, orderByPhysicalColumns: true);

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupIndex = null, string? groupValue = null, IReadOnlyList<WorkbenchField>? exportFields = null,
            string? dataFilter = null) =>
            composer.GetExportRowsByKeysAsync(definition, keys, token, groupIndex, groupValue, exportFields, dataFilter);

        public Task<WorkbenchData> GetRowsAsync(WorkbenchDefinition definition, bool detail,
            IReadOnlyDictionary<string, string> keys, int page, int pageSize, CancellationToken token,
            WorkbenchQuery? query = null, string? keyword = null, string? sortField = null,
            string? sortDirection = null, int? groupIndex = null, string? groupValue = null,
            string? dataFilter = null) =>
            composer.GetRowsAsync(definition, detail, keys, page, pageSize, token, query, keyword,
                sortField, sortDirection, groupIndex, groupValue, dataFilter);

        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token) =>
            throw new NotSupportedException();

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

    [Fact]
    public async Task Assistant_Definition_Orders_Columns_By_Physical_Order()
    {
        var (connections, _) = Services();
        var builder = Builder(connections);
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var display = await builder.GetDefinitionAsync(PurchaseModuleId, AdminUser, "A", true, true,
            empty, empty, CancellationToken.None);
        var knowledge = await builder.GetDefinitionAsync(PurchaseModuleId, AdminUser, "A", true, true,
            empty, empty, CancellationToken.None, orderByPhysicalColumns: true);

        Assert.NotNull(display);
        Assert.NotNull(knowledge);
        // 字段集不变（只改顺序）：权限与列选择的口径没有被这条改动绕过。
        Assert.Equal(
            display!.DetailFields.Select(field => field.Key).OrderBy(key => key, StringComparer.OrdinalIgnoreCase),
            knowledge!.DetailFields.Select(field => field.Key).OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        // 物理列序把「料号」「数量」排进每行可见的前 8 列。
        var head = knowledge.DetailFields.Take(8).Select(field => field.Key).ToArray();
        Assert.Contains("PRO_NO", head, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("QTY", head, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detail_Tool_Shows_Item_And_Quantity_Columns()
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

        var composer = new WorkbenchQueryComposer(connections, new WorkbenchScopeFilter(new ApiMetrics()),
            new ApiMetrics(), new WorkbenchVirtualColumnResolver(), NullLogger<WorkbenchQueryComposer>.Instance);
        var tool = new GetRecordDetailTool(
            new LiveGateway(Builder(connections), composer),
            new PermissionService(new ModuleRightsRepository(connections, NullLogger<ModuleRightsRepository>.Instance),
                new PermissionCache(config)));

        var result = await tool.ExecuteAsync(AdminUser, JsonSerializer.Deserialize<JsonElement>(
            $$"""{"module_id":{{PurchaseModuleId}},"_keys":["{{type}}","{{no}}"]}"""), CancellationToken.None);

        Assert.True(result.Ok, result.ContentForModel);
        // 列序修正后，前 8 列里应当出现料号与数量（而非被个人列序截断在外）。
        Assert.True(result.ContentForModel.Contains("料号=", StringComparison.Ordinal), result.ContentForModel);
        Assert.True(result.ContentForModel.Contains("数量=", StringComparison.Ordinal), result.ContentForModel);
    }
}
