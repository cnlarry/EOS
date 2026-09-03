using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 统一选择器数据源集成测试：直连 EOS.ERP 开发库验证 menu-admin.tables / menu-admin.fields
/// 的关键字过滤、排序列白名单与分页。连接串来自 env EOS_ERP_TEST_CONNECTION 或本机 本机配置文件；
/// 拿不到连接串时跳过。只读查询，不产生测试数据。
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChooserRepositoryIntegrationTests
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly ChooserRepository _repository;

    public ChooserRepositoryIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        _repository = new ChooserRepository(new DbConnectionFactory(config), NullLogger<ChooserRepository>.Instance);
    }

    [Fact]
    public async Task QueryTables_ReturnsMetadataRowsWithColumns()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var result = await _repository.QueryAsync(new UnifiedChooserQueryRequest("menu-admin.tables", Page: 1, PageSize: 50), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(["T_ID", "T_DESC", "T_KIND", "T_TYPE"], result!.Columns.Select(column => column.Key));
        Assert.True(result.Total > 0);
        Assert.NotEmpty(result.Rows);
        Assert.All(result.Rows, row => Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", Convert.ToString(row["T_ID"])));
    }

    [Fact]
    public async Task QueryTables_KeywordFiltersAndSorts()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var result = await _repository.QueryAsync(
            new UnifiedChooserQueryRequest("menu-admin.tables", Keyword: "公司", SortField: "T_ID", SortDirection: "asc", Page: 1, PageSize: 50),
            CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result!.Total > 0);
        Assert.All(result.Rows, row => Assert.True(
            Convert.ToString(row["T_ID"])?.Contains("公司", StringComparison.OrdinalIgnoreCase) == true
            || Convert.ToString(row["T_DESC"])?.Contains("公司", StringComparison.OrdinalIgnoreCase) == true
            || Convert.ToString(row["T_KIND"])?.Contains("公司", StringComparison.OrdinalIgnoreCase) == true
            || Convert.ToString(row["T_TYPE"])?.Contains("公司", StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public async Task QueryTables_AppliesAdvancedConditions()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var result = await _repository.QueryAsync(
            new UnifiedChooserQueryRequest(
                "menu-admin.tables",
                Conditions: [new UnifiedChooserCondition("T_DESC", "contains", "公司")],
                Page: 1,
                PageSize: 100),
            CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result!.Total > 0);
        Assert.All(result.Rows, row =>
            Assert.True(Convert.ToString(row["T_DESC"])?.Contains("公司", StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public async Task QueryTables_RejectsConditionOnUnknownField()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        await Assert.ThrowsAsync<ArgumentException>(() => _repository.QueryAsync(
            new UnifiedChooserQueryRequest(
                "menu-admin.tables",
                Conditions: [new UnifiedChooserCondition("NOT_A_COLUMN", "eq", "x")],
                Page: 1,
                PageSize: 50),
            CancellationToken.None));
    }

    [Fact]
    public async Task QueryFields_ReturnsPhysicalNonVirtualFieldsForKnownTable()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var tableId = await PickTableWithFieldsAsync();
        Assert.NotNull(tableId);

        var result = await _repository.QueryAsync(
            new UnifiedChooserQueryRequest(
                "menu-admin.fields",
                Args: new Dictionary<string, string> { ["tableId"] = tableId! },
                Page: 1,
                PageSize: 100),
            CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(["F_ID", "F_DESC", "F_TYPE"], result!.Columns.Select(column => column.Key));
        Assert.True(result.Total > 0);
        Assert.All(result.Rows, row => Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", Convert.ToString(row["F_ID"])));
    }

    [Fact]
    public async Task QueryFields_KeywordFiltersOnFieldNameOrDescription()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var tableId = await PickTableWithFieldsAsync();
        Assert.NotNull(tableId);

        var result = await _repository.QueryAsync(
            new UnifiedChooserQueryRequest(
                "menu-admin.fields",
                Args: new Dictionary<string, string> { ["tableId"] = tableId! },
                Keyword: "ID",
                Page: 1,
                PageSize: 100),
            CancellationToken.None);
        Assert.NotNull(result);
        Assert.All(result!.Rows, row => Assert.True(
            Convert.ToString(row["F_ID"])?.Contains("ID", StringComparison.OrdinalIgnoreCase) == true
            || Convert.ToString(row["F_DESC"])?.Contains("ID", StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public async Task QueryFields_RejectsInvalidTableId()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var result = await _repository.QueryAsync(
            new UnifiedChooserQueryRequest(
                "menu-admin.fields",
                Args: new Dictionary<string, string> { ["tableId"] = "NOT_A_TABLE; DROP TABLE dbo.FIELDS--" },
                Page: 1,
                PageSize: 50),
            CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task QueryReportFields_ReturnsModuleFieldsWithTableTokens()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var moduleId = await PickModuleWithFieldsAsync();
        Assert.NotNull(moduleId);

        var result = await _repository.QueryAsync(
            new UnifiedChooserQueryRequest(
                "report-admin.fields",
                Args: new Dictionary<string, string> { ["moduleId"] = moduleId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                Page: 1,
                PageSize: 100),
            CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(["T_ID", "F_ID", "F_DESC", "F_TYPE"], result!.Columns.Select(column => column.Key));
        Assert.True(result.Total > 0);
        Assert.All(result.Rows, row =>
            Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*\\.[A-Za-z_][A-Za-z0-9_]*$",
                $"{Convert.ToString(row["T_ID"])}.{Convert.ToString(row["F_ID"])}"));
    }

    [Fact]
    public async Task QueryReportFields_RejectsInvalidModuleId()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var result = await _repository.QueryAsync(
            new UnifiedChooserQueryRequest(
                "report-admin.fields",
                Args: new Dictionary<string, string> { ["moduleId"] = "0" },
                Page: 1,
                PageSize: 50),
            CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task QueryEmployees_ReturnsUnopenedEmployeesWithMetadataColumns()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var result = await _repository.QueryAsync(
            new UnifiedChooserQueryRequest("user-admin.employees", Page: 1, PageSize: 50),
            CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotEmpty(result!.Columns);
        Assert.Contains(result.Columns, column => column.Key.Equals("EMP_ID", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Columns, column => column.Key.Equals("EMP_NAME", StringComparison.OrdinalIgnoreCase));
        // 默认列串联 110104 SYSQL_DEFAULT
        Assert.NotNull(result.DefaultKeys);
        Assert.NotEmpty(result.DefaultKeys);
        Assert.Contains(result.DefaultKeys!, key => key.Equals("EMP_ID", StringComparison.OrdinalIgnoreCase));
        if (result.Total > 0)
        {
            Assert.NotEmpty(result.Rows);
            Assert.All(result.Rows, row => Assert.True(
                row.TryGetValue("EMP_ID", out var id) && Convert.ToString(id) is { Length: > 0 }));
        }
    }

    [Fact]
    public async Task QueryEmployees_KeywordFiltersOnMetadataColumns()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var result = await _repository.QueryAsync(
            new UnifiedChooserQueryRequest(
                "user-admin.employees",
                FilterField: "EMP_NAME",
                Keyword: "张",
                Page: 1,
                PageSize: 50),
            CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotNull(result!.Columns);
        // filterField 命中元数据列白名单；命中行按员工姓名过滤（无命中时 total=0 合法）
        if (result.Total > 0)
        {
            Assert.All(result.Rows, row =>
                Assert.True(Convert.ToString(row["EMP_NAME"])?.Contains("张", StringComparison.OrdinalIgnoreCase) == true));
        }
    }

    [Fact]
    public async Task QueryEmployees_AppliesAdvancedConditions()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var result = await _repository.QueryAsync(
            new UnifiedChooserQueryRequest(
                "user-admin.employees",
                Conditions: [new UnifiedChooserCondition("EMP_NAME", "contains", "张")],
                Page: 1,
                PageSize: 50),
            CancellationToken.None);
        Assert.NotNull(result);
        if (result!.Total > 0)
        {
            Assert.All(result.Rows, row =>
                Assert.True(Convert.ToString(row["EMP_NAME"])?.Contains("张", StringComparison.OrdinalIgnoreCase) == true));
        }
    }

    [Fact]
    public async Task Query_RejectsUnknownSourceKey()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var result = await _repository.QueryAsync(new UnifiedChooserQueryRequest("unknown.source"), CancellationToken.None);
        Assert.Null(result);
    }

    private static async Task<string?> PickTableWithFieldsAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT TOP 1 LTRIM(RTRIM(f.T_ID))
            FROM dbo.FIELDS f WITH (NOLOCK)
            INNER JOIN dbo.TABLES t WITH (NOLOCK) ON LTRIM(RTRIM(t.T_ID))=LTRIM(RTRIM(f.T_ID))
            WHERE COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=f.T_ID AND c.name=f.F_ID)
            GROUP BY LTRIM(RTRIM(f.T_ID));
            """, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null || value == DBNull.Value ? null : Convert.ToString(value);
    }

    private static async Task<int?> PickModuleWithFieldsAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT TOP 1 m.M_IDX
            FROM dbo.MODULES m WITH (NOLOCK)
            INNER JOIN dbo.FIELDS f WITH (NOLOCK)
              ON f.T_ID=LTRIM(RTRIM(m.MASTER_TABLE)) AND COALESCE(f.IS_VIRTUAL,0)=0
            WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,'')))<>''
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=f.T_ID AND c.name=f.F_ID)
            GROUP BY m.M_IDX;
            """, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null || value == DBNull.Value ? null : Convert.ToInt32(value);
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
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
}
