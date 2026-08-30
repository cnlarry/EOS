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
/// 数据表/字段维护仓储集成测试：直连 EOS.ERP 开发库验证表统计、表详情、
/// 未管理字段批量生成（生成后即清理）与删除联动清理。
/// 连接串来自 env EOS_ERP_TEST_CONNECTION 或本机 Codex 配置；拿不到连接串时跳过。
/// </summary>
[Trait("Category", "Integration")]
public sealed class FieldAdminRepositoryIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly FieldAdminRepository _repository;
    private readonly List<(string Table, string Field)> _createdFields = [];

    public FieldAdminRepositoryIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        _repository = new FieldAdminRepository(new DbConnectionFactory(config), new WorkbenchDirtyMarker(new DbConnectionFactory(config)),
            new WorkbenchAuditWriter(new DbConnectionFactory(config), new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new WorkbenchDefinitionProvider(new DbConnectionFactory(config), NullLogger<WorkbenchDefinitionProvider>.Instance),
                Microsoft.Extensions.Options.Options.Create(new EOS.API.Models.AuditSettings())),
            NullLogger<FieldAdminRepository>.Instance);
    }

    [Fact]
    public async Task GetTables_ReturnsFieldCounts()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var tables = await _repository.GetTablesAsync(null, CancellationToken.None);
        Assert.NotEmpty(tables);
        var company = tables.FirstOrDefault(item => item.TableId.Equals("COMPANY", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(company);
        Assert.True(company!.FieldCount > 0, "COMPANY 应有字段元数据");
        Assert.True(company.UnmanagedCount >= 0);
        Assert.True(company.OrphanCount >= 0);
        Assert.All(tables, item => Assert.True(item.FieldCount >= 0));
    }

    [Fact]
    public async Task GetTable_ReturnsDetailWithReadonlyColumns()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var detail = await _repository.GetTableAsync("COMPANY", CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal("COMPANY", detail!.TableId, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(detail.Description));
    }

    [Fact]
    public async Task GetFields_ReturnsPrimaryKeyAndPhysicalState()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var result = await _repository.GetFieldsAsync("COMPANY", null, 1, 16, CancellationToken.None);
        Assert.NotEmpty(result.Items);
        Assert.True(result.Total >= result.Items.Count);
        var companyId = result.Items.Single(item => item.FieldId.Equals("COMPANY_ID", StringComparison.OrdinalIgnoreCase));
        Assert.True(companyId.PhysicalExists);
        Assert.True(companyId.IsPrimaryKey);
    }

    [Fact]
    public async Task CreateUnmanagedFields_GeneratesAndIsIdempotent_ThenDeleteCleans()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var candidate = await FindUnmanagedColumnAsync();
        if (candidate is null)
        {
            return;
        }

        try
        {
            var result = await _repository.CreateUnmanagedFieldsAsync(
                new(candidate.Value.Table, [candidate.Value.Field]), "IT", CancellationToken.None);
            Assert.Equal(1, result.Created);
            Assert.Equal(0, result.Skipped);
            _createdFields.Add((candidate.Value.Table, candidate.Value.Field));

            // 幂等：再次生成全部跳过
            var second = await _repository.CreateUnmanagedFieldsAsync(
                new(candidate.Value.Table, [candidate.Value.Field]), "IT", CancellationToken.None);
            Assert.Equal(0, second.Created);
            Assert.Equal(1, second.Skipped);

            // 生成后元数据可见且指向真实物理列
            var meta = await _repository.GetMetadataAsync(candidate.Value.Table, candidate.Value.Field, CancellationToken.None);
            Assert.NotNull(meta);
            Assert.True(meta!.PhysicalExists);
            Assert.Equal(meta.Field.DataType, meta.PhysicalType, ignoreCase: true);
            Assert.True(meta.TypeMatches);
            Assert.Equal(meta.Field.DataType, meta.PhysicalType, ignoreCase: true);
        }
        finally
        {
            await CleanupCreatedFieldsAsync();
        }
    }

    [Fact]
    public async Task DeleteField_CleansUserColumnAndConditionReferences()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var marker = $"ZZ_FA_{Random.Shared.Next(1000, 9999)}";
        var table = "COMPANY";
        var field = $"ZZ_FA_F_{Random.Shared.Next(100000, 999999)}";
        var fIdx = await NextFreeFIdxAsync(table);
        var reportModuleId = Random.Shared.Next(100_000_000, 999_999_999);
        var sql = $"""
            INSERT INTO dbo.FIELDS (T_ID,F_ID,F_DESC,F_TYPE,IS_QUERY,IS_DEFAULT_FIELDS,IS_VISIBLE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (N'{table}',N'{field}',N'联动清理测试','nvarchar',1,1,0,'IT',GETDATE());
            INSERT INTO dbo.SYSQL_DEFAULT (T_ID,T_ID_R,F_IDX,F_ID)
            VALUES (N'{table}',N'{table}',{fIdx},N'{field}');
            INSERT INTO dbo.SYSQL_CONDITION (USER_ID,T_ID,T_ID_R,F_ID,F_DESC,IS_USE)
            VALUES (N'{marker}',N'{table}',N'{table}',N'{field}',N'联动清理测试',1);
            INSERT INTO dbo.SYSQR_DEFAULT (M_IDX,SERIAL_NO,F_ID,F_TYPE,F_DESC,F_VALUE)
            VALUES ({reportModuleId},1,N'{table}.{field}',N'C',N'联动清理测试',N'1');
            """;

        try
        {
            await ExecuteStatementsAsync(sql);
            _createdFields.Add((table, field));

            await _repository.DeleteAsync(table, field, "IT", CancellationToken.None);
            _createdFields.Remove((table, field));

            await using var check = new SqlConnection(ConnectionString.Value);
            await check.OpenAsync();
            await using var command = new SqlCommand(
                """
                SELECT
                  (SELECT COUNT(*) FROM dbo.FIELDS WHERE T_ID=@Table AND LTRIM(RTRIM(F_ID))=@Field),
                  (SELECT COUNT(*) FROM dbo.SYSQL_DEFAULT WHERE F_ID=@Field AND (T_ID=@Table OR T_ID_R=@Table)),
                  (SELECT COUNT(*) FROM dbo.SYSQL_CONDITION WHERE F_ID=@Field AND USER_ID=@Marker),
                  (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT WHERE F_ID=@TableDotField);
                """, check);
            command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
            command.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = field;
            command.Parameters.Add("@Marker", SqlDbType.NChar, 20).Value = marker;
            command.Parameters.Add("@TableDotField", SqlDbType.NVarChar, 220).Value = $"{table}.{field}";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal(0, reader.GetInt32(3));
        }
        finally
        {
            await CleanupCreatedFieldsAsync();
        }
    }

    [Fact]
    public async Task UpdateTable_RejectsOptimisticLockConflict()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var current = await _repository.GetTableAsync("COMPANY", CancellationToken.None);
        Assert.NotNull(current);
        var wrongOriginal = new FieldAdminTableInput(
            current!.Description + "（过期快照）", current.Kind, current.Type, current.Remark);
        var input = new FieldAdminTableInput(current.Description, current.Kind, current.Type, current.Remark);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.UpdateTableAsync("COMPANY", input, wrongOriginal, "IT", CancellationToken.None));

        // 冲突时数据不变
        var after = await _repository.GetTableAsync("COMPANY", CancellationToken.None);
        Assert.Equal(current.Description, after!.Description);
    }

    [Fact]
    public async Task DeleteTable_BlocksWhenReferenced()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.DeleteTableAsync("COMPANY", "IT", CancellationToken.None));
    }

    [Fact]
    public async Task CreateTable_RejectsDuplicateTableMetadata()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.CreateTableAsync(new("COMPANY", new("公司资料", "P", "TABLE", null)), "IT", CancellationToken.None));
    }

    public void Dispose()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        try
        {
            CleanupCreatedFieldsAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 清理失败不影响测试结论；残留仅为开发库测试数据
        }
    }

    private static async Task<(string Table, string Field)?> FindUnmanagedColumnAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT TOP 1 c.TABLE_NAME, c.COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS c
            WHERE c.TABLE_SCHEMA='dbo'
              AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                               WHERE f.T_ID=c.TABLE_NAME AND LTRIM(RTRIM(f.F_ID))=c.COLUMN_NAME)
              AND EXISTS (SELECT 1 FROM dbo.TABLES t WHERE t.T_ID=c.TABLE_NAME)
            ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<int> NextFreeFIdxAsync(string table)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT ISNULL(MAX(F_IDX),0)+1 FROM dbo.SYSQL_DEFAULT WITH (NOLOCK) WHERE T_ID=@Table;", connection);
        command.Parameters.Add("@Table", SqlDbType.VarChar, 50).Value = table;
        var value = await command.ExecuteScalarAsync();
        return value is null ? 1 : Convert.ToInt32(value);
    }

    private async Task ExecuteStatementsAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task CleanupCreatedFieldsAsync()
    {
        if (_createdFields.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        foreach (var (table, field) in _createdFields)
        {
            await using var delete = new SqlCommand(
                """
                DELETE FROM dbo.FIELDS WHERE T_ID=@Table AND LTRIM(RTRIM(F_ID))=@Field;
                DELETE FROM dbo.SYSQL_DEFAULT WHERE F_ID=@Field AND (T_ID=@Table OR T_ID_R=@Table);
                DELETE FROM dbo.SYSQL_FIELDS WHERE F_ID=@Field AND (T_ID=@Table OR T_ID_R=@Table);
                DELETE FROM dbo.SYSQL_CONDITION WHERE F_ID=@Field AND (T_ID=@Table OR T_ID_R=@Table);
                DELETE FROM dbo.SYSQL_COND_DFT WHERE F_ID=@Field AND (T_ID=@Table OR T_ID_R=@Table);
                DELETE FROM dbo.SYSQD_CONDITION WHERE F_ID=@Field AND (T_ID=@Table OR T_ID_R=@Table);
                DELETE FROM dbo.SYSQQ WHERE F_ID=@TableDotField;
                DELETE FROM dbo.SYSQR_DEFAULT WHERE F_ID=@TableDotField;
                """, connection);
            delete.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
            delete.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = field;
            delete.Parameters.Add("@TableDotField", SqlDbType.NVarChar, 220).Value = $"{table}.{field}";
            await delete.ExecuteNonQueryAsync();
        }
        _createdFields.Clear();
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
