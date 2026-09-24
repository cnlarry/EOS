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
/// 引擎可能要求的位置 / 批次列必须进明细表单：单据明细的位置与批次是否出现，原先完全取决于用户的
/// 「选择列」配置；而"位置档 3 必须指明库位""管批次的料号必须给批号"是**过账引擎的判据**——
/// 用户没勾这两列时表单里就没有格子，单据必然过账失败。本用例把"配置里没勾也要出现在表单里"钉住。
/// 需要 EOS_ERP_TEST_CONNECTION（与本仓库其它真库测试一致）。
/// </summary>
[Collection("live-database")]
public sealed class RuntimeRequiredDetailColumnsLiveTests
{
    private const string TestUserId = "zztst01";
    private const int ModuleId = 130101;                 // 库存盘点单
    private const string MasterTable = "INV_CHECK_STOCK_M";
    private const string DetailTable = "INV_CHECK_STOCK_D";

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

    [Fact]
    public async Task FormDefinition_KeepsLocationAndBatchColumns_EvenWhenColumnConfigOmitsThem()
    {
        var connections = Connections();
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        var seeded = await SeedColumnConfigWithoutLocationAndBatchAsync(connection);
        try
        {
            var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
            var builder = new WorkbenchDefinitionBuilder(connections, provider,
                Options.Create(new UnifiedFormEditorSettings()), NullLogger<WorkbenchDefinitionBuilder>.Instance);
            var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var definition = await builder.GetDefinitionAsync(ModuleId, TestUserId, "Z", true, true,
                empty, empty, CancellationToken.None);
            Assert.NotNull(definition);
            Assert.Equal(DetailTable, definition!.DetailTable);

            var form = await builder.GetFormDefinitionAsync(definition, TestUserId, "new", true, true,
                empty, empty, empty, empty, empty, empty, CancellationToken.None);
            Assert.NotNull(form);

            var keys = form!.DetailFields.Select(field => field.Key).ToArray();
            // 先证明配置确实没勾这两列（否则本用例证明不了"配置漏勾也照样进表单"）
            Assert.DoesNotContain("LOCATION_NO", seeded, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("BATCH_NO", seeded, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("LOCATION_NO", keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("BATCH_NO", keys, StringComparer.OrdinalIgnoreCase);
            // 可填（不是只读回显）：引擎要求用户给出这两个值
            Assert.False(form.DetailFields.First(field => field.Key.Equals("LOCATION_NO", StringComparison.OrdinalIgnoreCase)).IsReadonly);
            Assert.False(form.DetailFields.First(field => field.Key.Equals("BATCH_NO", StringComparison.OrdinalIgnoreCase)).IsReadonly);
        }
        finally
        {
            await ClearColumnConfigAsync(connection);
        }
    }

    private static async Task<string[]> SeedColumnConfigWithoutLocationAndBatchAsync(SqlConnection connection)
    {
        var candidates = new List<string>();
        await using (var command = new SqlCommand("""
            SELECT TOP 2 LTRIM(RTRIM(f.F_ID))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@DetailTable AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0
              AND LTRIM(RTRIM(f.F_ID)) NOT IN (N'LOCATION_NO', N'BATCH_NO')
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@DetailTable AND c.name=f.F_ID)
            ORDER BY COALESCE(f.VERIFY_INDEX,999),f.F_ID;
            """, connection))
        {
            command.Parameters.Add("@DetailTable", SqlDbType.NVarChar, 100).Value = DetailTable;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                candidates.Add(reader.GetString(0));
            }
        }
        Assert.Equal(2, candidates.Count);

        await ClearColumnConfigAsync(connection);
        for (var index = 0; index < candidates.Count; index++)
        {
            await using var insert = new SqlCommand(
                "INSERT INTO dbo.SYSQL_FIELDS (USER_ID,T_ID,T_ID_R,F_ID,F_IDX) VALUES (@UserId,@Master,@Detail,@Field,@Index);",
                connection);
            insert.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = TestUserId;
            insert.Parameters.Add("@Master", SqlDbType.VarChar, 100).Value = MasterTable;
            insert.Parameters.Add("@Detail", SqlDbType.VarChar, 100).Value = DetailTable;
            insert.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = candidates[index];
            insert.Parameters.Add("@Index", SqlDbType.Int).Value = index + 1;
            await insert.ExecuteNonQueryAsync();
        }
        return [.. candidates];
    }

    private static async Task ClearColumnConfigAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand(
            "DELETE FROM dbo.SYSQL_FIELDS WHERE USER_ID=@UserId AND T_ID=@Master AND T_ID_R=@Detail;", connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = TestUserId;
        command.Parameters.Add("@Master", SqlDbType.VarChar, 100).Value = MasterTable;
        command.Parameters.Add("@Detail", SqlDbType.VarChar, 100).Value = DetailTable;
        await command.ExecuteNonQueryAsync();
    }
}
