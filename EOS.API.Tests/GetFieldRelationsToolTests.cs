using System.Text.Json;
using EOS.API.Features.Assistant.Metrics;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

public sealed class GetFieldRelationsToolTests
{
    private sealed record Row(string FromTable, string FromColumn, string ToTable, string ToColumn, string? Desc);

    private sealed class FakeRelations(Row[] rows) : IFieldRelationRepository
    {
        public Task<IReadOnlyList<FieldRelationRow>> ListAsync(string? keyword, CancellationToken token)
        {
            IEnumerable<Row> query = rows;
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                query = query.Where(r =>
                    r.FromTable.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || r.FromColumn.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || r.ToTable.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || r.ToColumn.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || (r.Desc?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
            }
            IReadOnlyList<FieldRelationRow> result = query
                .Select(r => new FieldRelationRow(r.FromTable, r.FromColumn, r.ToTable, r.ToColumn, r.Desc))
                .ToList();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeModuleLocator(IReadOnlyDictionary<string, int[]> moduleIdsByTable) : IMetricRepository
    {
        public Task<MetricDefinitionRow?> GetAsync(string metricId, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<int>> FindModuleIdsByTableAsync(string table, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<int>>(
                moduleIdsByTable.TryGetValue(table, out var ids) ? ids : []);
    }

    private sealed class PerModulePermissions(IReadOnlyDictionary<int, bool> browse) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(new ModulePermission(new ModuleRights(
                CanBrowse: !browse.TryGetValue(moduleId, out var denied) || !denied,
                CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
                DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
                CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
                CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
                CanFileEdit: false, CanFileDele: false,
                DenyNewMasterFields: new HashSet<string>(), DenyNewDetailFields: new HashSet<string>(),
                DenyModiMasterFields: new HashSet<string>(), DenyModiDetailFields: new HashSet<string>(),
                DataFilter: string.Empty, ExecuteTag: "A")));

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static GetFieldRelationsTool CreateTool(
        Row[] rows, IReadOnlyDictionary<string, int[]> moduleIdsByTable,
        IReadOnlyDictionary<int, bool>? browse = null) => new(
        new FakeRelations(rows),
        new FakeModuleLocator(moduleIdsByTable),
        new PerModulePermissions(browse ?? new Dictionary<int, bool>()));

    private static readonly Row[] SeedRows =
    [
        new("COP_ORDER_D", "PRO_NO", "PRODUCT", "PRO_NO", "订单明细的产品"),
        new("COP_ORDER_M", "CLIENT_ID", "CLIENT", "CLIENT_ID", "订单的客户"),
        new("COP_ORDER_M", "SUPPLIER_ID", "SUPPLIER", "SUPPLIER_ID", null),
    ];

    private static readonly IReadOnlyDictionary<string, int[]> Modules = new Dictionary<string, int[]>
    {
        ["COP_ORDER_D"] = [1405],
        ["COP_ORDER_M"] = [1405],
        ["PRODUCT"] = [1401],
        ["CLIENT"] = [1101],
        ["SUPPLIER"] = [1102],
    };

    [Fact]
    public async Task Lists_Relations_For_User_With_Browse_On_Both_Sides()
    {
        var tool = CreateTool(SeedRows, Modules);
        var result = await tool.ExecuteAsync("u1", Args("{}"), CancellationToken.None);
        Assert.True(result.Ok, result.ContentForModel);
        Assert.Contains("COP_ORDER_D.PRO_NO = PRODUCT.PRO_NO（订单明细的产品）", result.ContentForModel);
        Assert.Contains("COP_ORDER_M.CLIENT_ID = CLIENT.CLIENT_ID（订单的客户）", result.ContentForModel);
        Assert.Contains("COP_ORDER_M.SUPPLIER_ID = SUPPLIER.SUPPLIER_ID", result.ContentForModel);
    }

    [Fact]
    public async Task Hides_Relations_When_One_Side_Is_Not_Browsable()
    {
        var tool = CreateTool(SeedRows, Modules, new Dictionary<int, bool> { [1102] = true });
        var result = await tool.ExecuteAsync("u1", Args("{}"), CancellationToken.None);
        Assert.True(result.Ok, result.ContentForModel);
        Assert.DoesNotContain("SUPPLIER", result.ContentForModel);
        Assert.Contains("1 条", result.ContentForModel);
        Assert.Contains("未展示", result.ContentForModel);
    }

    [Fact]
    public async Task Hides_Relations_When_A_Side_Has_No_Module()
    {
        var noProductModules = new Dictionary<string, int[]>(Modules)
        {
            ["PRODUCT"] = [],
        };
        var tool = CreateTool(SeedRows, noProductModules);
        var result = await tool.ExecuteAsync("u1", Args("{}"), CancellationToken.None);
        Assert.True(result.Ok, result.ContentForModel);
        Assert.DoesNotContain("COP_ORDER_D.PRO_NO", result.ContentForModel);
        Assert.Contains("2 条", result.ContentForModel);
    }

    [Fact]
    public async Task Keyword_Filters_Relations()
    {
        var tool = CreateTool(SeedRows, Modules);
        var result = await tool.ExecuteAsync("u1", Args("""{"keyword":"产品"}"""),
            CancellationToken.None);
        Assert.True(result.Ok, result.ContentForModel);
        Assert.Contains("PRO_NO", result.ContentForModel);
        Assert.DoesNotContain("CLIENT_ID", result.ContentForModel);
    }

    [Fact]
    public async Task Empty_Registry_Returns_Friendly_Message()
    {
        var tool = CreateTool([], new Dictionary<string, int[]>());
        var result = await tool.ExecuteAsync("u1", Args("{}"), CancellationToken.None);
        Assert.True(result.Ok, result.ContentForModel);
        Assert.Contains("没有当前用户可见", result.ContentForModel);
    }

    private static JsonElement Args(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<JsonElement>(json);
}

/// <summary>
/// Data-integrity gate on the FIELD_RELATION registry: every registered relation
/// must reference tables and columns that physically exist (sys.objects/sys.columns).
/// A ghost relation would poison cross-table planning, so it fails loudly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FieldRelationRegistryTests
{
    private static string? TestConnection() =>
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");

    [Fact]
    public async Task Every_Registered_Relation_References_Existing_Tables_And_Columns()
    {
        var connectionString = TestConnection()
            ?? throw new InvalidOperationException(
                "真库完整性测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        const string sql = """
            SELECT r.FROM_TABLE, r.FROM_COLUMN, r.TO_TABLE, r.TO_COLUMN,
               CASE WHEN EXISTS (SELECT 1 FROM sys.objects o WHERE o.name = r.FROM_TABLE AND o.type IN ('U','V')) THEN 1 ELSE 0 END AS FT,
               CASE WHEN EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID('dbo.' + r.FROM_TABLE) AND c.name = r.FROM_COLUMN) THEN 1 ELSE 0 END AS FC,
               CASE WHEN EXISTS (SELECT 1 FROM sys.objects o WHERE o.name = r.TO_TABLE AND o.type IN ('U','V')) THEN 1 ELSE 0 END AS TT,
               CASE WHEN EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID('dbo.' + r.TO_TABLE) AND c.name = r.TO_COLUMN) THEN 1 ELSE 0 END AS TC
            FROM dbo.FIELD_RELATION r;
            """;
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        var ghosts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetInt32(4) == 0 || reader.GetInt32(5) == 0
                || reader.GetInt32(6) == 0 || reader.GetInt32(7) == 0)
            {
                ghosts.Add($"{reader.GetString(0)}.{reader.GetString(1)} -> {reader.GetString(2)}.{reader.GetString(3)}");
            }
        }
        Assert.True(ghosts.Count == 0, "存在幽灵关联（表或列不存在）：" + string.Join("; ", ghosts));
    }
}
