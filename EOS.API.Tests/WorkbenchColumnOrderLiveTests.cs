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
/// 「选择列」配置 → 工作台列表列：列集合与顺序必须完全等于用户保存的列配置。
/// 主键列不因行标识需要被插回显示列（键列由查询层单独并入返回行，不参与渲染）。
/// 需要 MSSQL_ERP_CONN（与本仓库其它真库测试一致）。
/// </summary>
[Collection("live-database")]
public sealed class WorkbenchColumnOrderLiveTests
{
    private const string TestUserId = "zztst01";
    private const int ModuleId = 1201;          // 产品/料件基本资料（单表模块，主表 PRODUCT，主键 PRO_NO）
    private const string MasterTable = "PRODUCT";

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
    /// 造一份「不含主键列、且顺序与字段元数据默认顺序不同」的列配置，断言列表按它原样出列：
    /// 少一列（主键被插回显示列）或顺序回落到字段元数据顺序都会失败。
    /// </summary>
    [Fact]
    public async Task ColumnConfig_DrivesListColumnsAndOrder_KeyColumnStaysOutOfView()
    {
        var connections = Connections();
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        var configured = await SeedColumnConfigAsync(connection);
        try
        {
            var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
            var builder = new WorkbenchDefinitionBuilder(connections, provider,
                Options.Create(new UnifiedFormEditorSettings()), NullLogger<WorkbenchDefinitionBuilder>.Instance);
            var emptyDenied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var definition = await builder.GetDefinitionAsync(ModuleId, TestUserId, "Z", true, true,
                emptyDenied, emptyDenied, CancellationToken.None);
            Assert.NotNull(definition);

            var keys = definition!.MasterFields.Select(field => field.Key).ToArray();
            Assert.Equal(configured, keys);
            Assert.DoesNotContain("PRO_NO", keys, StringComparer.OrdinalIgnoreCase);

            // 行标识不因主键列退出显示列而丢失：返回行仍需带主键值（前端据此选中/定位明细）
            var composer = new WorkbenchQueryComposer(connections, new WorkbenchScopeFilter(new ApiMetrics()),
                new ApiMetrics(), new WorkbenchVirtualColumnResolver(), NullLogger<WorkbenchQueryComposer>.Instance);
            var data = await composer.GetRowsAsync(definition, false, new Dictionary<string, string>(), 1, 20, CancellationToken.None);
            Assert.NotEmpty(data.Rows);
            Assert.All(data.Rows, row => Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(row["PRO_NO"]))));
        }
        finally
        {
            await ClearColumnConfigAsync(connection);
        }
    }

    private static async Task<string[]> SeedColumnConfigAsync(SqlConnection connection)
    {
        var candidates = new List<string>();
        await using (var command = new SqlCommand("""
            SELECT TOP 3 LTRIM(RTRIM(f.F_ID))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0
              AND NOT EXISTS (SELECT 1 FROM sys.indexes i
                              JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id
                              JOIN sys.columns c ON ic.object_id=c.object_id AND ic.column_id=c.column_id
                              JOIN sys.tables t ON i.object_id=t.object_id
                              JOIN sys.schemas s ON t.schema_id=s.schema_id
                              WHERE s.name=N'dbo' AND t.name=@Table AND i.is_primary_key=1 AND c.name=f.F_ID)
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@Table AND c.name=f.F_ID)
            ORDER BY COALESCE(f.VERIFY_INDEX,999),f.F_ID;
            """, connection))
        {
            command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = MasterTable;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                candidates.Add(reader.GetString(0));
            }
        }
        Assert.Equal(3, candidates.Count);
        // 第 3 列排首位：按字段元数据顺序渲染的实现会立刻失败
        var configured = new[] { candidates[2], candidates[0], candidates[1] };
        await ClearColumnConfigAsync(connection);
        for (var index = 0; index < configured.Length; index++)
        {
            await using var insert = new SqlCommand(
                "INSERT INTO dbo.SYSQL_FIELDS (USER_ID,T_ID,T_ID_R,F_ID,F_IDX) VALUES (@UserId,@Table,@Table,@Field,@Index);",
                connection);
            insert.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = TestUserId;
            insert.Parameters.Add("@Table", SqlDbType.VarChar, 100).Value = MasterTable;
            insert.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = configured[index];
            insert.Parameters.Add("@Index", SqlDbType.Int).Value = index + 1;
            await insert.ExecuteNonQueryAsync();
        }
        return configured;
    }

    private static async Task ClearColumnConfigAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand(
            "DELETE FROM dbo.SYSQL_FIELDS WHERE USER_ID=@UserId AND T_ID=@Table AND T_ID_R=@Table;", connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = TestUserId;
        command.Parameters.Add("@Table", SqlDbType.VarChar, 100).Value = MasterTable;
        await command.ExecuteNonQueryAsync();
    }
}
