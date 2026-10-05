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
/// 连接串来自 env MSSQL_ERP_CONN 或本机 本机配置文件；拿不到连接串时跳过。
/// </summary>
[Trait("Category", "Integration")]
public sealed class FieldAdminRepositoryIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly FieldAdminRepository _repository;
    private readonly List<(string Table, string Field)> _createdFields = [];
    private readonly List<string> _createdTables = [];
    private readonly List<string> _createdPhysicalTables = [];

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
            new RestrictedExpressionService(new DbConnectionFactory(config)),
            new WorkbenchIdempotency(),
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

    /// <summary>
    /// 幽灵字段判定与清理：只有「非虚拟且物理列已不存在」的字段算幽灵。
    /// 虚拟字段结构上就没有物理列，既不进清单、也不进表的幽灵计数、更不会被清理请求删掉。
    /// </summary>
    [Fact]
    public async Task CleanupGhostFields_RemovesOnlyNonVirtualMissingColumns()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var table = "COMPANY";
        var ghost = $"ZZ_FA_GHOST_{Random.Shared.Next(100000, 999999)}";
        var virtualField = $"ZZ_FA_VIRT_{Random.Shared.Next(100000, 999999)}";
        var sql = $"""
            INSERT INTO dbo.FIELDS (T_ID,F_ID,F_DESC,F_TYPE,IS_QUERY,IS_DEFAULT_FIELDS,IS_VISIBLE,IS_VIRTUAL,VIRTUAL_EXP,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (N'{table}',N'{ghost}',N'幽灵字段测试','nvarchar',0,0,0,0,NULL,'IT',GETDATE());
            INSERT INTO dbo.FIELDS (T_ID,F_ID,F_DESC,F_TYPE,IS_QUERY,IS_DEFAULT_FIELDS,IS_VISIBLE,IS_VIRTUAL,VIRTUAL_EXP,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (N'{table}',N'{virtualField}',N'虚拟字段测试','nvarchar',0,0,0,1,N'COMPANY_ID','IT',GETDATE());
            """;

        var orphansBefore = await OrphanCountAsync(table);
        try
        {
            await ExecuteStatementsAsync(sql);
            _createdFields.Add((table, ghost));
            _createdFields.Add((table, virtualField));

            var ghosts = await _repository.GetGhostFieldsAsync(table, CancellationToken.None);
            Assert.Contains(ghosts, item => item.FieldId.Equals(ghost, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(ghosts, item => item.FieldId.Equals(virtualField, StringComparison.OrdinalIgnoreCase));

            // 两个夹具一起插入后计数只涨 1：虚拟字段那一行不进幽灵计数
            Assert.Equal(orphansBefore + 1, await OrphanCountAsync(table));

            var cleanup = await _repository.CleanupGhostFieldsAsync(
                new(table, [ghost, virtualField]), "IT", CancellationToken.None);
            Assert.Equal(1, cleanup.Removed);
            Assert.Equal(1, cleanup.Skipped);
            Assert.Contains(cleanup.SkippedReasons, reason => reason.Contains("虚拟字段"));
            _createdFields.Remove((table, ghost));

            // 幽灵已删、虚拟字段仍在：计数回到插入前（虚拟字段此刻就是"没有物理列的字段"，若被计入就会多 1）
            Assert.Null(await _repository.GetMetadataAsync(table, ghost, CancellationToken.None));
            Assert.NotNull(await _repository.GetMetadataAsync(table, virtualField, CancellationToken.None));
            Assert.Equal(orphansBefore, await OrphanCountAsync(table));
        }
        finally
        {
            await CleanupCreatedFieldsAsync();
        }
    }

    /// <summary>
    /// 从物理表登记表元数据（选取式新增）：表描述与类型由物理对象推导，字段按物理列生成
    /// （说明取列说明、类型取物理类型、主键/自增/计算列取自物理结构），生成后不产生「幽灵」。
    /// 物理对象由用例自造（含表/列说明）——登记这类用例的写目标就是元数据，
    /// 不借库里现成的表来写；结束先删元数据再 DROP 物理表。
    /// </summary>
    [Fact]
    public async Task RegisterPhysicalTable_CreatesTableAndFieldMetadata()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var table = $"ZZ_FA_PHYS_{Random.Shared.Next(100000, 999999)}";
        var sql = $"""
            CREATE TABLE dbo.{table} (
                CODE nvarchar(50) NOT NULL,
                QTY decimal(18,4) NULL,
                REMARK nvarchar(200) NULL,
                REMARK_LEN AS (len(REMARK)),
                CONSTRAINT PK_{table} PRIMARY KEY (CODE));
            EXEC sp_addextendedproperty N'MS_Description',N'字段维护登记用例表',N'SCHEMA',N'dbo',N'TABLE',N'{table}';
            EXEC sp_addextendedproperty N'MS_Description',N'编号',N'SCHEMA',N'dbo',N'TABLE',N'{table}',N'COLUMN',N'CODE';
            """;
        _createdPhysicalTables.Add(table);

        try
        {
            await ExecuteStatementsAsync(sql);
            var result = await _repository.RegisterPhysicalTableAsync(new(table), "IT", CancellationToken.None);
            _createdTables.Add(result.TableId);

            // 表：描述取表说明，类型与性质按物理对象
            Assert.Equal(table, result.TableId, ignoreCase: true);
            Assert.Equal("字段维护登记用例表", result.Description);
            Assert.Equal("TABLE", result.Type);
            Assert.Equal("P", result.Kind);
            Assert.Equal(4, result.FieldCreated);
            Assert.Equal(0, result.FieldSkipped);

            // 字段：说明取列说明（无则用列名）、类型按物理列、主键与计算列取自物理结构
            var code = await _repository.GetMetadataAsync(table, "CODE", CancellationToken.None);
            Assert.NotNull(code);
            Assert.Equal("编号", code!.Field.Label);
            Assert.Equal("nvarchar", code.Field.DataType, ignoreCase: true);
            Assert.True(code.IsPrimaryKey);
            Assert.True(code.PhysicalExists);
            var computed = await _repository.GetMetadataAsync(table, "REMARK_LEN", CancellationToken.None);
            Assert.NotNull(computed);
            Assert.Equal("REMARK_LEN", computed!.Field.Label);
            Assert.True(computed.Field.IsReadonly, "计算列由数据库算，必须标只读");

            // 生成的字段都能对应物理列：该表不该留下「幽灵」或「未管理」
            var fields = await _repository.GetFieldsAsync(table, null, 1, 50, CancellationToken.None);
            Assert.Equal(result.FieldCreated, fields.Total);
            Assert.All(fields.Items, item => Assert.True(item.PhysicalExists));
            var row = (await _repository.GetTablesAsync(null, CancellationToken.None))
                .Single(item => item.TableId.Equals(table, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, row.OrphanCount);
            Assert.Equal(0, row.UnmanagedCount);

            await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.RegisterPhysicalTableAsync(new(table), "IT", CancellationToken.None));
        }
        finally
        {
            await CleanupCreatedTablesAsync();
            await CleanupPhysicalTablesAsync();
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

    /// <summary>
    /// 表列清单的物理列分支：返回的行数与库里真实列一致，且物理列一律 IsVirtual=false。
    /// 该断言同时覆盖 IS_VIRTUAL 列的读取类型——若 SQL 里该列不是可被 GetBoolean 读取的类型，
    /// 这里会先抛 InvalidCastException（而不是静默返回错误的标志位）。
    /// </summary>
    [Fact]
    public async Task GetTableColumns_PhysicalColumnsReportNotVirtual()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var physical = await PhysicalColumnsAsync("COMPANY");
        Assert.NotEmpty(physical);

        var columns = await _repository.GetTableColumnsAsync("COMPANY", CancellationToken.None);

        Assert.NotEmpty(columns);
        var physicalRows = columns.Where(item => physical.Contains(item.Name)).ToList();
        Assert.Equal(physical.Count, physicalRows.Count);
        Assert.All(physicalRows, item => Assert.False(item.IsVirtual));
        Assert.All(columns, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.DataType));
        });
    }

    /// <summary>
    /// 表列清单的虚拟列分支：来源表内配了 VIRTUAL_EXP 且无同名物理列的字段应带 IsVirtual=true 返回。
    /// 开发库若没有这种字段则跳过（不构造数据，避免测试污染元数据）。
    /// </summary>
    [Fact]
    public async Task GetTableColumns_IncludesVirtualColumnsWithFlag()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var candidate = await FindVirtualColumnTableAsync();
        if (candidate is null)
        {
            return;
        }

        var physical = await PhysicalColumnsAsync(candidate.Value.Table);
        var columns = await _repository.GetTableColumnsAsync(candidate.Value.Table, CancellationToken.None);
        var virtualColumns = columns.Where(item => item.IsVirtual).ToList();

        Assert.Contains(virtualColumns, item => item.Name.Equals(candidate.Value.Field, StringComparison.OrdinalIgnoreCase));
        Assert.All(virtualColumns, item => Assert.DoesNotContain(item.Name, physical));
    }

    private static async Task<HashSet<string>> PhysicalColumnsAsync(string table)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT c.name
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@Table;
            """, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static async Task<(string Table, string Field)?> FindVirtualColumnTableAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT TOP 1 LTRIM(RTRIM(f.T_ID)), LTRIM(RTRIM(f.F_ID))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE COALESCE(f.IS_VIRTUAL,0)=1
              AND LTRIM(RTRIM(ISNULL(f.VIRTUAL_EXP,'')))<>''
              AND NOT EXISTS (SELECT 1 FROM sys.columns c
                              JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                              JOIN sys.schemas s ON o.schema_id=s.schema_id
                              WHERE s.name=N'dbo' AND o.name=LTRIM(RTRIM(f.T_ID))
                                AND c.name=LTRIM(RTRIM(f.F_ID)))
              AND EXISTS (SELECT 1 FROM sys.objects o
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=LTRIM(RTRIM(f.T_ID)) AND o.type IN ('U','V'))
            ORDER BY f.T_ID, f.F_ID;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }
        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>取表列表口径下的幽灵字段计数（与界面「幽灵」列同源）。</summary>
    private async Task<int> OrphanCountAsync(string table)
    {
        var tables = await _repository.GetTablesAsync(null, CancellationToken.None);
        return tables.Single(item => item.TableId.Equals(table, StringComparison.OrdinalIgnoreCase)).OrphanCount;
    }

    /// <summary>清理本用例自造的物理表（登记用例的物理对象由用例自己建）。</summary>
    private async Task CleanupPhysicalTablesAsync()
    {
        if (_createdPhysicalTables.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        foreach (var table in _createdPhysicalTables)
        {
            if (!WorkbenchSql.Identifier.IsMatch(table))
            {
                continue;
            }
            await using var drop = new SqlCommand($"DROP TABLE IF EXISTS dbo.{table};", connection);
            await drop.ExecuteNonQueryAsync();
        }
        _createdPhysicalTables.Clear();
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
            CleanupCreatedTablesAsync().GetAwaiter().GetResult();
            CleanupPhysicalTablesAsync().GetAwaiter().GetResult();
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
            SELECT TOP 1 o.name AS TABLE_NAME, c.name AS COLUMN_NAME
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo'
              AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                               WHERE f.T_ID=o.name AND LTRIM(RTRIM(f.F_ID))=c.name)
              AND EXISTS (SELECT 1 FROM dbo.TABLES t WHERE t.T_ID=o.name)
            ORDER BY o.name, c.column_id;
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

    /// <summary>清理本用例登记的表元数据、其字段元数据与历史列配置引用。</summary>
    private async Task CleanupCreatedTablesAsync()
    {
        if (_createdTables.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        foreach (var table in _createdTables)
        {
            await using var delete = new SqlCommand(
                """
                DELETE FROM dbo.SYSQL_DEFAULT WHERE T_ID=@Table OR T_ID_R=@Table;
                DELETE FROM dbo.SYSQL_FIELDS WHERE T_ID=@Table OR T_ID_R=@Table;
                DELETE FROM dbo.SYSQL_CONDITION WHERE T_ID=@Table OR T_ID_R=@Table;
                DELETE FROM dbo.SYSQL_COND_DFT WHERE T_ID=@Table OR T_ID_R=@Table;
                DELETE FROM dbo.SYSQD_CONDITION WHERE T_ID=@Table OR T_ID_R=@Table;
                DELETE FROM dbo.FIELDS WHERE T_ID=@Table;
                DELETE FROM dbo.TABLES WHERE T_ID=@Table;
                """, connection);
            delete.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
            await delete.ExecuteNonQueryAsync();
        }
        _createdTables.Clear();
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
        catch
        {
            return null;
        }
    }
}
