using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

using EOS.API.Telemetry;
namespace EOS.API.Data;

public sealed class FieldAdminRepository(
    DbConnectionFactory connections,
    WorkbenchDirtyMarker dirtyMarker,
    WorkbenchAuditWriter auditWriter,
    RestrictedExpressionService expressionService,
    WorkbenchIdempotency idempotency,
    ILogger<FieldAdminRepository> logger)
{
    /// <summary>
    /// 写入侧拒收的占位字段名。历史元数据补齐脚本把"无说明"写成了字符串 'NULL' 与 HTML 空实体
    /// '&nbsp;'，它们非空，读取侧只判空串的兜底拦不住，会原样显示到表单/列表标签上。
    /// </summary>
    private static readonly HashSet<string> PlaceholderLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "NULL", "&nbsp;",
    };

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvarchar", "varchar", "nchar", "char", "int", "bigint", "smallint", "tinyint", "decimal", "numeric",
        "float", "real", "money", "smallmoney", "date", "datetime", "datetime2", "smalldatetime", "time", "bit",
        "uniqueidentifier", "text", "ntext", "image", "varbinary", "binary", "xml", "timestamp", "sql_variant",
        "geometry", "geography", "hierarchyid",
        // Pseudo-types kept editable so existing rows do not fail on save
        "IDCard", "URL", "Email", "PhoneNo", "ZipCode", "String", "Integer"
    };
    private static readonly HashSet<string> AllowedAlign = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "left", "center", "right"
    };
    private static readonly HashSet<string> AllowedTableKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "P", "S", "O", "V"
    };
    private static readonly HashSet<string> AllowedTableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "TABLE", "VIEW", "UNKNOW"
    };

    public async Task<IReadOnlyList<FieldAdminTable>> GetTablesAsync(string? kind, CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        var kindFilter = kind?.Trim() ?? "";
        if (kindFilter.Length > 0 && !AllowedTableKinds.Contains(kindFilter))
            throw new ArgumentException("表性质筛选无效。", nameof(kind));
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(t.T_ID)),LTRIM(RTRIM(t.T_DESC)),LTRIM(RTRIM(ISNULL(t.T_KIND,''))),LTRIM(RTRIM(ISNULL(t.T_TYPE,''))),
                   (SELECT COUNT(*) FROM dbo.FIELDS f WITH (NOLOCK) WHERE f.T_ID=t.T_ID) AS FieldCount,
                   (SELECT COUNT(*) FROM sys.columns c
                     JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                     JOIN sys.schemas s ON o.schema_id=s.schema_id
                     WHERE s.name=N'dbo' AND o.name=t.T_ID
                       AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f2 WITH (NOLOCK)
                                        WHERE f2.T_ID=t.T_ID AND LTRIM(RTRIM(f2.F_ID))=c.name)) AS UnmanagedCount,
                   (SELECT COUNT(*) FROM dbo.FIELDS f3 WITH (NOLOCK)
                     WHERE f3.T_ID=t.T_ID
                       AND COALESCE(f3.IS_VIRTUAL,0)=0
                       AND NOT EXISTS (SELECT 1 FROM sys.columns c2
                                        JOIN sys.objects o2 ON c2.object_id=o2.object_id AND o2.type IN ('U','V')
                                        JOIN sys.schemas s2 ON o2.schema_id=s2.schema_id
                                        WHERE s2.name=N'dbo' AND o2.name=t.T_ID
                                          AND c2.name=LTRIM(RTRIM(f3.F_ID)))) AS OrphanCount
            FROM dbo.TABLES t WITH (NOLOCK)
            WHERE (@Kind='' OR LTRIM(RTRIM(t.T_KIND))=@Kind)
            ORDER BY T_DESC,T_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Kind", SqlDbType.NVarChar, 20).Value = kindFilter;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<FieldAdminTable>();
        while (await reader.ReadAsync(token))
        {
            var table = reader.GetString(0);
            if (WorkbenchSql.Identifier.IsMatch(table))
                result.Add(new(table, reader.GetString(1), NullIfEmpty(reader.GetString(2)), NullIfEmpty(reader.GetString(3)),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
        }
        await reader.DisposeAsync();
        // P4a：TABLES.T_KIND 若配置受控转换函数（f_get_table_kind_desc），列表显示转换后描述
        string? kindConvertFunction = null;
        await using (var fnCommand = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(CONVERT_FUNCTION,''))) FROM dbo.FIELDS WITH (NOLOCK) WHERE LTRIM(RTRIM(T_ID))='TABLES' AND LTRIM(RTRIM(F_ID))='T_KIND';",
            connection))
        {
            kindConvertFunction = await fnCommand.ExecuteScalarAsync(token) as string;
        }
        if (!string.IsNullOrWhiteSpace(kindConvertFunction))
        {
            var distinctKinds = result.Select(item => item.Kind).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).Distinct(StringComparer.Ordinal).ToList();
            var kindMap = await ConvertFunctionResolver.BuildMapAsync(connection, kindConvertFunction.Trim(), distinctKinds, token);
            if (kindMap.Count > 0)
            {
                result = result.Select(item => item.Kind is null ? item
                    : kindMap.TryGetValue(item.Kind.Trim(), out var converted) ? item with { Kind = Convert.ToString(converted) ?? item.Kind } : item).ToList();
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<FieldAdminModule>> GetModulesAsync(CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT M_IDX,COALESCE(NULLIF(LTRIM(RTRIM(M_DESC)),''),CONVERT(nvarchar(20),M_IDX))
            FROM dbo.MODULES WITH (NOLOCK)
            ORDER BY M_DESC,M_IDX;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<FieldAdminModule>();
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    public async Task<FieldAdminTableDetail?> GetTableAsync(string tableId, CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        EnsureIdentifier(tableId, null);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(T_ID)),LTRIM(RTRIM(T_DESC)),LTRIM(RTRIM(ISNULL(T_KIND,''))),LTRIM(RTRIM(ISNULL(T_TYPE,''))),
                   T_REMARK,FK_T_ID_1,FK_T_ID_2,FK_T_ID_3,FK_T_ID_4,FK_T_ID_5,
                   QUERY_RELATION,DF_CONDITION,DF_VERIFY,CAST(COALESCE(CAN_IMPORT,0) AS bit),
                   LAST_UPDATE_BY,LAST_UPDATE_DATE
            FROM dbo.TABLES WITH (NOLOCK)
            WHERE T_ID=@TableId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new(
            reader.GetString(0), reader.GetString(1), NullIfEmpty(reader.GetString(2)), NullIfEmpty(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            NullIfEmpty(reader.IsDBNull(5) ? "" : reader.GetString(5)),
            NullIfEmpty(reader.IsDBNull(6) ? "" : reader.GetString(6)),
            NullIfEmpty(reader.IsDBNull(7) ? "" : reader.GetString(7)),
            NullIfEmpty(reader.IsDBNull(8) ? "" : reader.GetString(8)),
            NullIfEmpty(reader.IsDBNull(9) ? "" : reader.GetString(9)),
            NullIfEmpty(reader.IsDBNull(10) ? "" : reader.GetString(10)),
            NullIfEmpty(reader.IsDBNull(11) ? "" : reader.GetString(11)),
            NullIfEmpty(reader.IsDBNull(12) ? "" : reader.GetString(12)),
            reader.GetBoolean(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetDateTime(15));
    }

    public async Task CreateTableAsync(CreateFieldAdminTableRequest request, string updatedBy, CancellationToken token)
    {
        EnsureIdentifier(request.TableId, null);
        ValidateTableInput(request.Table);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        await using (var physical = new SqlCommand(
            """
            SELECT 1 FROM sys.objects o
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@TableId AND o.type IN ('U','V');
            """, connection, transaction))
        {
            physical.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = request.TableId;
            if (await physical.ExecuteScalarAsync(token) is null)
                throw new ArgumentException("物理表或视图不存在，无法登记表元数据。", nameof(request));
        }
        await using (var repeat = new SqlCommand("SELECT 1 FROM dbo.TABLES WITH (NOLOCK) WHERE T_ID=@TableId", connection, transaction))
        {
            repeat.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = request.TableId;
            if (await repeat.ExecuteScalarAsync(token) is not null)
                throw new ArgumentException("数据表元数据已存在，不能重复登记。", nameof(request));
        }

        const string sql = """
            INSERT INTO dbo.TABLES (T_ID,T_DESC,T_KIND,T_TYPE,T_REMARK,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (@TableId,@Description,@Kind,@Type,@Remark,@UpdatedBy,GETDATE());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        AddTableParameters(command, request.TableId, request.Table, updatedBy);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new InvalidOperationException("新增数据表元数据失败。");
        await dirtyMarker.MarkDirtyForTableAsync(connection, transaction, request.TableId, updatedBy, token);
        // 审计与业务同事务：写不进去就整单回滚，不留"改了但没有痕迹"的状态。
        await auditWriter.WriteEventAsync(connection, transaction, null, request.TableId, "CREATE", "数据表维护新增",
            updatedBy, "FIELD_ADMIN", result: 1, fieldChanges: null, token);
        await transaction.CommitAsync(token);
        logger.LogInformation("新增数据表元数据 table={Table} by={UpdatedBy}", request.TableId, updatedBy);
    }

    public async Task UpdateTableAsync(
        string tableId,
        FieldAdminTableInput input,
        FieldAdminTableInput? original,
        string updatedBy,
        CancellationToken token)
    {
        EnsureIdentifier(tableId, null);
        ValidateTableInput(input);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        var currentTable = await ReadCurrentTableInputAsync(connection, transaction, tableId, token)
            ?? throw new KeyNotFoundException("数据表不存在。");
        if (original is not null && !SameTableInput(original, currentTable))
        {
            logger.LogWarning("数据表乐观锁冲突 table={Table} by={UpdatedBy}", tableId, updatedBy);
            throw new ArgumentException("数据表信息已被他人修改，请刷新后重试！", nameof(input));
        }

        const string sql = """
            UPDATE dbo.TABLES SET
                T_DESC=@Description,T_KIND=@Kind,T_TYPE=@Type,T_REMARK=@Remark,
                LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE()
            WHERE T_ID=@TableId;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        AddTableParameters(command, tableId, input, updatedBy);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new KeyNotFoundException("数据表不存在。");
        await dirtyMarker.MarkDirtyForTableAsync(connection, transaction, tableId, updatedBy, token);
        await auditWriter.WriteEventAsync(connection, transaction, null, tableId, "UPDATE", "数据表维护更新",
            updatedBy, "FIELD_ADMIN", result: 1, DiffTables(currentTable, input), token);
        await transaction.CommitAsync(token);
        logger.LogInformation("更新数据表元数据 table={Table} by={UpdatedBy}", tableId, updatedBy);
    }

    public async Task DeleteTableAsync(string tableId, string updatedBy, CancellationToken token)
    {
        EnsureIdentifier(tableId, null);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        const string refsSql = """
            SELECT
              (SELECT COUNT(*) FROM dbo.FIELDS f WITH (NOLOCK) WHERE f.T_ID=@TableId) AS Fields,
              (SELECT COUNT(*) FROM dbo.MODULES m WITH (NOLOCK)
                WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))) = @TableId OR LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE,''))) = @TableId) AS Modules,
              (SELECT COUNT(*) FROM dbo.TABLES t2 WITH (NOLOCK)
                WHERE @TableId IN (LTRIM(RTRIM(ISNULL(t2.FK_T_ID_1,''))),LTRIM(RTRIM(ISNULL(t2.FK_T_ID_2,''))),
                                   LTRIM(RTRIM(ISNULL(t2.FK_T_ID_3,''))),LTRIM(RTRIM(ISNULL(t2.FK_T_ID_4,''))),
                                   LTRIM(RTRIM(ISNULL(t2.FK_T_ID_5,''))))) AS FkReferences,
              (SELECT COUNT(*) FROM dbo.SYSQL_DEFAULT WITH (NOLOCK) WHERE T_ID=@TableId OR T_ID_R=@TableId) AS SysqlDefault,
              (SELECT COUNT(*) FROM dbo.SYSQL_FIELDS WITH (NOLOCK) WHERE T_ID=@TableId OR T_ID_R=@TableId) AS SysqlFields,
              (SELECT COUNT(*) FROM dbo.SYSQL_CONDITION WITH (NOLOCK) WHERE T_ID=@TableId OR T_ID_R=@TableId) AS SysqlCondition,
              (SELECT COUNT(*) FROM dbo.SYSQL_COND_DFT WITH (NOLOCK) WHERE T_ID=@TableId OR T_ID_R=@TableId) AS SysqlCondDft,
              (SELECT COUNT(*) FROM dbo.SYSQD_CONDITION WITH (NOLOCK) WHERE T_ID=@TableId OR T_ID_R=@TableId) AS SysqdCondition,
              (SELECT COUNT(*) FROM dbo.SYSQQ WITH (NOLOCK) WHERE T_ID=@TableId) AS Sysqq,
              (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT WITH (NOLOCK) WHERE F_ID=@TableDot OR F_ID LIKE @Pattern) AS SysqrDefault;
            """;
        await using var refs = new SqlCommand(refsSql, connection, transaction);
        refs.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        refs.Parameters.Add("@TableDot", SqlDbType.NVarChar, 120).Value = $"{tableId}.";
        refs.Parameters.Add("@Pattern", SqlDbType.NVarChar, 120).Value = $"{tableId}.%";
        await using var refsReader = await refs.ExecuteReaderAsync(token);
        if (!await refsReader.ReadAsync(token))
            throw new KeyNotFoundException("数据表不存在。");
        var counts = new (string Name, int Count)[]
        {
            ("字段元数据", refsReader.GetInt32(0)),
            ("模块引用", refsReader.GetInt32(1)),
            ("关联表引用", refsReader.GetInt32(2)),
            ("默认列配置", refsReader.GetInt32(3)),
            ("用户列配置", refsReader.GetInt32(4)),
            ("查询条件记忆", refsReader.GetInt32(5) + refsReader.GetInt32(6) + refsReader.GetInt32(7)),
            ("报表条件引用", refsReader.GetInt32(8) + refsReader.GetInt32(9)),
        };
        await refsReader.CloseAsync();
        var blocked = counts.Where(item => item.Count > 0).ToList();
        if (blocked.Count > 0)
            throw new ArgumentException(
                $"数据表存在引用，无法删除：{string.Join("；", blocked.Select(item => $"{item.Name} {item.Count} 条"))}。仅删除无引用的表元数据，物理表不受影响。",
                nameof(tableId));

        var snapshot = await ReadCurrentTableInputAsync(connection, transaction, tableId, token)
            ?? throw new KeyNotFoundException("数据表不存在。");
        await using var delete = new SqlCommand("DELETE FROM dbo.TABLES WHERE T_ID=@TableId", connection, transaction);
        delete.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        if (await delete.ExecuteNonQueryAsync(token) != 1)
            throw new KeyNotFoundException("数据表不存在。");
        await dirtyMarker.MarkDirtyForTableAsync(connection, transaction, tableId, updatedBy, token);
        await auditWriter.WriteEventAsync(connection, transaction, null, tableId, "DELETE", "数据表维护删除",
            updatedBy, "FIELD_ADMIN", result: 1,
            [new AuditFieldChange("(table)", JsonSerializer.Serialize(DescribeTable(snapshot)), null, null)],
            token);
        await transaction.CommitAsync(token);
        logger.LogInformation("删除数据表元数据 table={Table} by={UpdatedBy}", tableId, updatedBy);
    }

    public async Task<IReadOnlyList<FieldAdminUnmanagedField>> GetUnmanagedFieldsAsync(string tableId, CancellationToken token)
    {
        EnsureIdentifier(tableId, null);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT c.name AS COLUMN_NAME, TYPE_NAME(c.user_type_id) AS DATA_TYPE
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@TableId
              AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WITH (NOLOCK)
                               WHERE f.T_ID=@TableId AND LTRIM(RTRIM(f.F_ID))=c.name)
            ORDER BY c.column_id;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<FieldAdminUnmanagedField>();
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public async Task<CreateUnmanagedFieldsResult> CreateUnmanagedFieldsAsync(
        CreateUnmanagedFieldsRequest request,
        string updatedBy,
        CancellationToken token)
    {
        EnsureIdentifier(request.TableId, null);
        if (request.FieldIds.Count == 0)
            throw new ArgumentException("请至少选择一个字段。", nameof(request));
        var requested = request.FieldIds
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var id in requested)
            EnsureIdentifier(request.TableId, id);

        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        await using (var physical = new SqlCommand(
            """
            SELECT 1 FROM sys.objects o
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@TableId AND o.type IN ('U','V');
            """, connection, transaction))
        {
            physical.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = request.TableId;
            if (await physical.ExecuteScalarAsync(token) is null)
                throw new ArgumentException("物理表或视图不存在，无法生成字段元数据。", nameof(request));
        }

        var created = 0;
        var skipped = 0;
        var reasons = new List<string>();
        var createdChanges = new List<AuditFieldChange>();
        var existing = await ReadExistingFieldIdsAsync(connection, transaction, request.TableId, token);
        var physicalColumns = (await ReadGeneratedFieldsAsync(connection, transaction, request.TableId, token))
            .ToDictionary(item => item.FieldId, StringComparer.OrdinalIgnoreCase);
        foreach (var fieldId in requested)
        {
            if (existing.Contains(fieldId))
            {
                skipped++;
                reasons.Add($"{fieldId}：已存在元数据，跳过");
                continue;
            }
            if (!physicalColumns.TryGetValue(fieldId, out var column))
            {
                skipped++;
                reasons.Add($"{fieldId}：物理列不存在，跳过");
                continue;
            }
            if (!AllowedTypes.Contains(column.DataType))
            {
                skipped++;
                reasons.Add($"{fieldId}：物理类型 {column.DataType} 不受支持，跳过");
                continue;
            }
            await InsertGeneratedFieldAsync(connection, transaction, request.TableId, column, updatedBy, token);
            created++;
            createdChanges.Add(new AuditFieldChange(fieldId, null, $"{column.DataType} ({column.Description})", null));
        }
        await dirtyMarker.MarkDirtyForTableAsync(connection, transaction, request.TableId, updatedBy, token);
        await auditWriter.WriteEventAsync(connection, transaction, null, request.TableId, "CREATE",
            $"批量生成字段元数据 created={created} skipped={skipped}", updatedBy, "FIELD_ADMIN",
            result: 1, createdChanges, token);
        await transaction.CommitAsync(token);
        logger.LogInformation("批量生成字段元数据 table={Table} created={Created} skipped={Skipped} by={UpdatedBy}",
            request.TableId, created, skipped, updatedBy);
        return new(created, skipped, reasons);
    }

    /// <summary>
    /// 未登记进 TABLES 的物理表/视图候选（新增数据表元数据走选取而不是手敲表名）。
    /// 描述取扩展属性 MS_Description（本库表描述在 class=1, minor_id=0）；候选是"还没登记的对象"，
    /// 正常环境只有几十个（系统自用表与视图），上限 1000 只为兜底。
    /// </summary>
    public async Task<IReadOnlyList<FieldAdminPhysicalObject>> GetPhysicalObjectsAsync(CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT TOP (1000) o.name AS T_ID,o.type AS OBJECT_TYPE,
                   ISNULL(CONVERT(nvarchar(500), ep.value),N'') AS DESCRIPTION,
                   (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id=o.object_id) AS COLUMN_COUNT
            FROM sys.objects o
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            LEFT JOIN sys.extended_properties ep
              ON ep.class=1 AND ep.major_id=o.object_id AND ep.minor_id=0 AND ep.name=N'MS_Description'
            WHERE s.name=N'dbo' AND o.type IN ('U','V')
              AND NOT EXISTS (SELECT 1 FROM dbo.TABLES t WITH (NOLOCK) WHERE LTRIM(RTRIM(t.T_ID))=o.name)
            ORDER BY o.name;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<FieldAdminPhysicalObject>();
        while (await reader.ReadAsync(token))
        {
            var table = reader.GetString(0).Trim();
            if (!WorkbenchSql.Identifier.IsMatch(table)) continue;
            var objectType = reader.GetString(1).Trim();
            result.Add(new(table, objectType, ResolveLabel(reader.GetString(2), table), reader.GetInt32(3)));
        }
        return result;
    }

    /// <summary>
    /// 从物理表/视图登记表元数据并自动生成字段元数据（同一事务）：
    /// 表描述取表说明（无则用表名）、类型与性质来自物理对象（表=TABLE/P、视图=VIEW/V）；
    /// 字段的类型与说明来自物理列（列说明无则用列名），主键/自增/计算列标志取自物理结构。
    /// 类型不受支持的列逐个跳过并回报原因——不静默漏字段。
    /// </summary>
    public async Task<RegisterPhysicalTableResult> RegisterPhysicalTableAsync(
        RegisterPhysicalTableRequest request,
        string updatedBy,
        CancellationToken token)
    {
        EnsureIdentifier(request.TableId, null);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        var physical = await ReadPhysicalObjectAsync(connection, transaction, request.TableId, token)
            ?? throw new ArgumentException("物理表或视图不存在，无法登记表元数据。", nameof(request));
        await using (var repeat = new SqlCommand("SELECT 1 FROM dbo.TABLES WITH (NOLOCK) WHERE T_ID=@TableId", connection, transaction))
        {
            repeat.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = request.TableId;
            if (await repeat.ExecuteScalarAsync(token) is not null)
                throw new ArgumentException("数据表元数据已存在，不能重复登记。", nameof(request));
        }

        var isView = string.Equals(physical.ObjectType, "V", StringComparison.OrdinalIgnoreCase);
        var tableInput = new FieldAdminTableInput(physical.Description, isView ? "V" : "P", isView ? "VIEW" : "TABLE", null);
        const string insertTableSql = """
            INSERT INTO dbo.TABLES (T_ID,T_DESC,T_KIND,T_TYPE,T_REMARK,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (@TableId,@Description,@Kind,@Type,@Remark,@UpdatedBy,GETDATE());
            """;
        await using (var insert = new SqlCommand(insertTableSql, connection, transaction))
        {
            AddTableParameters(insert, request.TableId, tableInput, updatedBy);
            if (await insert.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("新增数据表元数据失败。");
        }

        var existing = await ReadExistingFieldIdsAsync(connection, transaction, request.TableId, token);
        var created = 0;
        var skipped = 0;
        var reasons = new List<string>();
        var changes = new List<AuditFieldChange>
        {
            new("(table)", null, JsonSerializer.Serialize(DescribeTable(tableInput)), null),
        };
        foreach (var column in await ReadGeneratedFieldsAsync(connection, transaction, request.TableId, token))
        {
            if (existing.Contains(column.FieldId))
            {
                skipped++;
                reasons.Add($"{column.FieldId}：已存在元数据，跳过");
                continue;
            }
            if (!AllowedTypes.Contains(column.DataType))
            {
                skipped++;
                reasons.Add($"{column.FieldId}：物理类型 {column.DataType} 不受支持，跳过");
                continue;
            }
            await InsertGeneratedFieldAsync(connection, transaction, request.TableId, column, updatedBy, token);
            created++;
            changes.Add(new AuditFieldChange(column.FieldId, null, $"{column.DataType} ({column.Description})", null));
        }
        await dirtyMarker.MarkDirtyForTableAsync(connection, transaction, request.TableId, updatedBy, token);
        await auditWriter.WriteEventAsync(connection, transaction, null, request.TableId, "CREATE",
            $"从物理对象登记表元数据 fields={created} skipped={skipped}", updatedBy, "FIELD_ADMIN",
            result: 1, changes, token);
        await transaction.CommitAsync(token);
        logger.LogInformation("从物理对象登记表元数据 table={Table} fields={Created} skipped={Skipped} by={UpdatedBy}",
            request.TableId, created, skipped, updatedBy);
        return new(request.TableId, tableInput.Description, tableInput.Kind ?? "", tableInput.Type ?? "", created, skipped, reasons);
    }

    /// <summary>
    /// 幽灵字段：FIELDS 有元数据、物理表已无同名列。虚拟字段结构上就没有物理列，不是幽灵字段，
    /// 故判定一律排除 IS_VIRTUAL=1 的行（与字段列表的物理列口径一致）。
    /// </summary>
    public async Task<IReadOnlyList<FieldAdminGhostField>> GetGhostFieldsAsync(string tableId, CancellationToken token)
    {
        EnsureIdentifier(tableId, null);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        var statuses = await ReadFieldPhysicalStatusAsync(connection, null, tableId, token);
        return statuses
            .Where(item => !item.IsVirtual && !item.PhysicalExists)
            .Select(item => new FieldAdminGhostField(item.FieldId, item.Description, item.DataType))
            .ToList();
    }

    /// <summary>
    /// 清理幽灵字段：删除元数据已找不到物理列的 FIELDS 行并清理其历史列配置（同一事务内）。
    /// 逐条按**当前**库状态复核，虚拟字段、物理列仍存在、系统列一律跳过——
    /// 前端拿到的清单可能已过期，不能凭它直接删。
    /// </summary>
    public async Task<CleanupGhostFieldsResult> CleanupGhostFieldsAsync(
        CleanupGhostFieldsRequest request,
        string updatedBy,
        CancellationToken token)
    {
        EnsureIdentifier(request.TableId, null);
        var requested = request.FieldIds
            .Select(id => id?.Trim() ?? "")
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requested.Count == 0)
            throw new ArgumentException("请至少选择一个字段。", nameof(request));
        foreach (var id in requested)
            EnsureIdentifier(request.TableId, id);

        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var statuses = (await ReadFieldPhysicalStatusAsync(connection, transaction, request.TableId, token))
            .ToDictionary(item => item.FieldId, StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        var reasons = new List<string>();
        var changes = new List<AuditFieldChange>();
        foreach (var fieldId in requested)
        {
            if (!statuses.TryGetValue(fieldId, out var status))
            {
                reasons.Add($"{fieldId}：元数据不存在，跳过");
                continue;
            }
            if (status.IsVirtual)
            {
                reasons.Add($"{fieldId}：虚拟字段没有物理列，不属幽灵字段，跳过");
                continue;
            }
            if (status.PhysicalExists)
            {
                reasons.Add($"{fieldId}：物理列存在，不是幽灵字段，跳过");
                continue;
            }
            if (WorkflowStates.IsLifecycleColumn(fieldId))
            {
                reasons.Add($"{fieldId}：系统列不允许删除，跳过");
                continue;
            }
            if (await DeleteFieldMetadataAsync(connection, transaction, request.TableId, fieldId, token) != 1)
            {
                reasons.Add($"{fieldId}：元数据已被他人删除，跳过");
                continue;
            }
            removed++;
            changes.Add(new AuditFieldChange(fieldId, $"{status.DataType} ({status.Description})", null, null));
        }
        // 无实际删除时不写审计、不标脏：与字段更新的无操作口径一致，避免留下"清了 0 条"的空事件。
        if (removed > 0)
        {
            await dirtyMarker.MarkDirtyForTableAsync(connection, transaction, request.TableId, updatedBy, token);
            await auditWriter.WriteEventAsync(connection, transaction, null, request.TableId, "DELETE",
                $"清理幽灵字段 removed={removed} skipped={reasons.Count}", updatedBy, "FIELD_ADMIN",
                result: 1, changes, token);
        }
        await transaction.CommitAsync(token);
        logger.LogInformation("清理幽灵字段 table={Table} removed={Removed} skipped={Skipped} by={UpdatedBy}",
            request.TableId, removed, reasons.Count, updatedBy);
        return new(removed, reasons.Count, reasons);
    }

    public async Task<FieldAdminPageResult> GetFieldsAsync(
        string tableId,
        string? keyword,
        int page,
        int pageSize,
        CancellationToken token,
        bool excludeSystemColumns = false)
    {
        EnsureIdentifier(tableId, null);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var pattern = $"%{keyword?.Trim() ?? ""}%";
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        // 系统列排除：闭式代码侧常量（WorkflowStates.LifecycleColumns），非用户输入，不拼接用户值。
        var systemFilter = excludeSystemColumns
            ? "AND F_ID NOT IN (" + string.Join(",", WorkflowStates.LifecycleColumns.Select(column => "N'" + column + "'")) + ")"
            : string.Empty;
        const string sqlTemplate = """
            ;WITH base AS (
                SELECT LTRIM(RTRIM(F_ID)) F_ID,
                       COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(F_ID))) F_DESC,
                       COALESCE(F_TYPE,'') F_TYPE,
                       CAST(COALESCE(IS_VIRTUAL,0) AS bit) IS_VIRTUAL,
                       CAST(COALESCE(IS_VISIBLE,1) AS bit) IS_VISIBLE,
                       CAST(COALESCE(IS_DEFAULT_FIELDS,0) AS bit) IS_DEFAULT_FIELDS,
                       CAST(COALESCE(IS_QUERY,1) AS bit) IS_QUERY,
                       CAST(COALESCE(IS_READONLY,0) AS bit) IS_READONLY,
                       CAST(COALESCE(IS_COST,0) AS bit) IS_COST,
                       CAST(COALESCE(IS_SECRECY,0) AS bit) IS_SECRECY,
                       CAST(COALESCE(IS_PK,0) AS bit) IS_PK
                FROM dbo.FIELDS WITH (NOLOCK)
                WHERE T_ID=@TableId
            )
            SELECT F_ID,F_DESC,F_TYPE,IS_VIRTUAL,IS_VISIBLE,IS_DEFAULT_FIELDS,IS_QUERY,IS_READONLY,IS_COST,IS_SECRECY,IS_PK,
                   CAST(CASE WHEN IS_VIRTUAL=1 THEN 1 ELSE
                     CASE WHEN EXISTS (SELECT 1 FROM sys.columns c
                                        JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                                        JOIN sys.schemas s ON o.schema_id=s.schema_id
                                        WHERE s.name=N'dbo' AND o.name=@TableId
                                          AND c.name=LTRIM(RTRIM(F_ID))) THEN 1 ELSE 0 END END AS bit) AS IS_PHYSICAL,
                   COUNT(*) OVER() AS Total
            FROM base
            WHERE (@Keyword='' OR F_ID LIKE @Pattern OR F_DESC LIKE @Pattern)
            /*SYSTEM_FILTER*/
            ORDER BY F_ID
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        var sql = sqlTemplate.Replace("/*SYSTEM_FILTER*/", systemFilter, StringComparison.Ordinal);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 200).Value = keyword?.Trim() ?? "";
        command.Parameters.Add("@Pattern", SqlDbType.NVarChar, 200).Value = pattern;
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = (page - 1) * pageSize;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;
        await using var reader = await command.ExecuteReaderAsync(token);
        var items = new List<FieldAdminFieldSummary>();
        var total = 0;
        while (await reader.ReadAsync(token))
        {
            if (total == 0) total = Convert.ToInt32(reader.GetValue(12));
            var field = reader.GetString(0).Trim();
            if (!WorkbenchSql.Identifier.IsMatch(field)) continue;
            items.Add(new(tableId, field, reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6),
                reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9),
                reader.GetBoolean(10), reader.GetBoolean(11),
                WorkflowStates.IsLifecycleColumn(field)));
        }
        return new(items, total, page, pageSize);
    }

    public async Task<FieldAdminMetadata?> GetMetadataAsync(string tableId, string fieldId, CancellationToken token)
    {
        EnsureIdentifier(tableId, fieldId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(F_ID)),COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(F_ID))),COALESCE(F_TYPE,'nvarchar'),
                   COALESCE(DISPLAY_LENGTH,100),COALESCE(NULLIF(ITEM_ALIGN,''),'left'),COALESCE(NULLIF(HEADER_ALIGN,''),'center'),
                   DISPLAY_FORMAT,CAST(COALESCE(IS_VISIBLE,1) AS bit),CAST(COALESCE(IS_DEFAULT_FIELDS,0) AS bit),
                   CAST(COALESCE(IS_QUERY,1) AS bit),CAST(COALESCE(IS_READONLY,0) AS bit),CAST(COALESCE(IS_VERIFY,0) AS bit),
                   CAST(COALESCE(IS_COST,0) AS bit),CAST(COALESCE(IS_SECRECY,0) AS bit),DFT_VALUE,VERIFY_INDEX,REGEX,F_REMARK,
                   BROWSE_URL,BROWSE_M_IDX,CAST(COALESCE(ONLY_CHOOSE,0) AS bit),CAST(COALESCE(CHOOSE_MULTI,0) AS bit),CHOOSE_PAGE,
                   CAST(COALESCE(IS_VIRTUAL,0) AS bit),VIRTUAL_EXP,CAST(COALESCE(CAN_COPY,1) AS bit),CAST(COALESCE(IS_AUTOINC,0) AS bit),
                   CONVERT_FUNCTION,LAST_UPDATE_BY,LAST_UPDATE_DATE,CAST(COALESCE(IS_PK,0) AS bit)
            FROM dbo.FIELDS WITH (NOLOCK)
            WHERE T_ID=@TableId AND LTRIM(RTRIM(F_ID))=@FieldId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var field = reader.GetString(0).Trim();
        if (!WorkbenchSql.Identifier.IsMatch(field)) return null;
        var input = new FieldAdminInput(
            reader.GetString(1), reader.GetString(2), Math.Clamp(reader.GetInt32(3), 40, 300),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10), reader.GetBoolean(11),
            reader.GetBoolean(12), reader.GetBoolean(13), reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetInt32(15), reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetInt32(19), reader.GetBoolean(20), reader.GetBoolean(21),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            [],
            reader.GetBoolean(25));
        var isVirtual = reader.GetBoolean(23);
        var virtualExpression = reader.IsDBNull(24) ? null : reader.GetString(24);
        var isAutoIncrement = reader.GetBoolean(26);
        var convertFunction = reader.IsDBNull(27) ? null : reader.GetString(27);
        var lastUpdatedBy = reader.IsDBNull(28) ? null : reader.GetString(28);
        DateTime? lastUpdatedAt = reader.IsDBNull(29) ? null : reader.GetDateTime(29);
        var isPrimaryKey = reader.GetBoolean(30);
        await reader.CloseAsync();
        input = input with { Choosers = await ReadFieldChoosersAsync(connection, null, tableId, field, token) };
        var physicalType = await GetPhysicalTypeAsync(connection, tableId, field, token);
        return new(tableId, field, input,
            isVirtual, virtualExpression, isAutoIncrement, convertFunction,
            lastUpdatedBy, lastUpdatedAt, isPrimaryKey,
            physicalType is not null,
            physicalType,
            physicalType is null ? null : string.Equals(physicalType, input.DataType.Trim(), StringComparison.OrdinalIgnoreCase),
            WorkflowStates.IsLifecycleColumn(field));
    }

    /// <summary>
    /// 字段变更历史（AUDIT_EVENT/FIELD_CHANGE，RESOURCE_TYPE=FIELD_ADMIN，RESOURCE_KEY=表.字段）。
    /// 供全尺寸字段设置页「变更历史」选项卡。
    /// </summary>
    public async Task<IReadOnlyList<FieldHistoryEvent>> GetFieldHistoryAsync(
        string tableId,
        string fieldId,
        CancellationToken token)
    {
        // 一次取回事件与其字段级明细，由调用端按 EVENT_ID 分组。
        // 不能用 JSON_QUERY((SELECT ... FOR JSON PATH))：SQL Server 不允许在子查询里使用 FOR JSON，
        // 该写法在解析阶段即报语法错误（端点必然 500）。事件上限 200 条由内层 TOP 限定。
        const string sql = """
            ;WITH ev AS (
                SELECT TOP 200 e.EVENT_ID, e.OCCURRED_AT, e.ACTOR_USER_ID, e.ACTION, e.SUMMARY
                FROM dbo.AUDIT_EVENT e WITH (NOLOCK)
                WHERE e.RESOURCE_TYPE = N'FIELD_ADMIN'
                  AND e.RESOURCE_KEY = @Key
                ORDER BY e.OCCURRED_AT DESC, e.EVENT_ID DESC
            )
            SELECT ev.EVENT_ID,
                   CONVERT(varchar(19), ev.OCCURRED_AT, 120) AS OCCURRED_AT,
                   ev.ACTOR_USER_ID,
                   ISNULL(NULLIF(LTRIM(RTRIM(dn.EMP_NAME)), N''), ev.ACTOR_USER_ID) AS ACTOR_NAME,
                   ev.ACTION, ev.SUMMARY,
                   fc.FIELD_NAME, fc.OLD_VALUE, fc.NEW_VALUE
            FROM ev
            LEFT JOIN dbo.SYSDN dn WITH (NOLOCK)
              ON LTRIM(RTRIM(dn.EMP_ID)) = LTRIM(RTRIM(ev.ACTOR_USER_ID))
            LEFT JOIN dbo.AUDIT_FIELD_CHANGE fc WITH (NOLOCK)
              ON fc.EVENT_ID = ev.EVENT_ID
            ORDER BY ev.OCCURRED_AT DESC, ev.EVENT_ID DESC, fc.FIELD_NAME;
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 220).Value = $"{tableId}.{fieldId}";
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<FieldHistoryEvent>();
        long currentEventId = -1;
        FieldHistoryEvent? current = null;
        var changes = new List<FieldHistoryChange>();
        while (await reader.ReadAsync(token))
        {
            var eventId = Convert.ToInt64(reader.GetValue(0));
            if (current is null || eventId != currentEventId)
            {
                if (current is not null) result.Add(current with { Changes = changes });
                currentEventId = eventId;
                changes = [];
                current = new FieldHistoryEvent(
                    DateTime.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetString(2),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    [],
                    reader.GetString(3));
            }
            if (!reader.IsDBNull(6))
            {
                changes.Add(new FieldHistoryChange(
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }
        if (current is not null) result.Add(current with { Changes = changes });
        return result;
    }

    /// <summary>
    /// 表列（物理列 sys.columns + 来源表内受控虚拟列 FIELDS.IS_VIRTUAL 且 VIRTUAL_EXP 非空）：
    /// 字段设置回填来源列可选虚拟（对齐既有实现语义）；过滤条件字段仅取物理列（IsVirtual=false 过滤）。
    /// </summary>
    public async Task<IReadOnlyList<FieldAdminColumn>> GetTableColumnsAsync(
        string tableId,
        CancellationToken token)
    {
        const string sql = """
            SELECT c.name, TYPE_NAME(c.user_type_id), LTRIM(RTRIM(COALESCE(f.F_DESC,''))) AS F_DESC, CAST(0 AS bit) AS IS_VIRTUAL
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            LEFT JOIN dbo.FIELDS f WITH (NOLOCK)
              ON f.T_ID=@TableId AND LTRIM(RTRIM(f.F_ID))=c.name
            WHERE s.name=N'dbo' AND o.name=@TableId
            ORDER BY c.column_id;
            """;
        const string virtualSql = """
            SELECT LTRIM(RTRIM(f.F_ID)), COALESCE(NULLIF(LTRIM(RTRIM(f.F_TYPE)),''),'nvarchar'),
                   LTRIM(RTRIM(COALESCE(f.F_DESC,''))), CAST(1 AS bit) AS IS_VIRTUAL
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@TableId AND COALESCE(f.IS_VIRTUAL,0)=1
              AND LTRIM(RTRIM(ISNULL(f.VIRTUAL_EXP,'')))<>''
              AND NOT EXISTS (SELECT 1 FROM sys.columns c
                              JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                              JOIN sys.schemas s ON o.schema_id=s.schema_id
                              WHERE s.name=N'dbo' AND o.name=@TableId AND c.name=f.F_ID)
            ORDER BY f.F_ID;
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        var result = new List<FieldAdminColumn>();
        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId.Trim();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                result.Add(new FieldAdminColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
            }
        }
        await using (var command = new SqlCommand(virtualSql, connection))
        {
            command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId.Trim();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                result.Add(new FieldAdminColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
            }
        }
        return result;
    }

    /// <summary>
    /// 表关联白名单（TABLES.QUERY_RELATION）：虚拟表达式构建器的跨表引用候选（别名 + 关联条件）。
    /// 关系为空或不可解析时不抛错，以 Ok/Error 表达（解析器与运行时共用一份，避免两处口径）。
    /// </summary>
    public async Task<FieldAdminRelations> GetTableRelationsAsync(string tableId, CancellationToken token)
    {
        EnsureIdentifier(tableId, null);
        var table = tableId.Trim();
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        string relation;
        await using (var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(QUERY_RELATION,''))) FROM dbo.TABLES WITH (NOLOCK) WHERE LTRIM(RTRIM(T_ID))=@TableId;",
            connection))
        {
            command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = table;
            relation = await command.ExecuteScalarAsync(token) as string ?? "";
        }
        if (string.IsNullOrWhiteSpace(relation)) return new(table, true, null, []);
        if (!VirtualExpressionParser.TryParseRelation(relation, table, out var joins, out var error))
            return new(table, false, error, []);
        var items = joins.Select(join => new FieldAdminRelation(
            join.Table,
            join.Alias,
            join.Conditions.Select(condition => $"{condition.LeftTable}.{condition.LeftColumn}={condition.RightTable}.{condition.RightColumn}")
                .Concat((join.Constants ?? []).Select(constant =>
                    $"{constant.Table}.{constant.Column}={(constant.IsString ? $"'{constant.Literal}'" : constant.Literal)}"))
                .ToList())).ToList();
        return new(table, true, null, items);
    }

    private static async Task<string?> GetPhysicalTypeAsync(
        SqlConnection connection,
        string tableId,
        string fieldId,
        CancellationToken token)
    {
        const string sql = """
            SELECT TYPE_NAME(c.user_type_id) AS DATA_TYPE
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@TableId AND c.name=@FieldId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
        var value = await command.ExecuteScalarAsync(token);
        return value is null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task CreateAsync(CreateFieldAdminRequest request, string updatedBy, CancellationToken token)
    {
        EnsureIdentifier(request.TableId, request.FieldId);
        Validate(request.Field);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        await using (var tableCheck = new SqlCommand("SELECT 1 FROM dbo.TABLES WITH (NOLOCK) WHERE T_ID=@TableId", connection, transaction))
        {
            tableCheck.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = request.TableId;
            if (await tableCheck.ExecuteScalarAsync(token) is null)
                throw new ArgumentException("数据表不存在，无法新增字段。", nameof(request));
        }
        await using (var repeat = new SqlCommand("SELECT 1 FROM dbo.FIELDS WITH (NOLOCK) WHERE T_ID=@TableId AND LTRIM(RTRIM(F_ID))=@FieldId", connection, transaction))
        {
            repeat.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = request.TableId;
            repeat.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = request.FieldId.Trim();
            if (await repeat.ExecuteScalarAsync(token) is not null)
                throw new ArgumentException("数据库中已经存在此字段，不能保存！", nameof(request));
        }
        await using (var physical = new SqlCommand(
            """
            SELECT 1 FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@TableId AND c.name=@FieldId;
            """, connection, transaction))
        {
            physical.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = request.TableId;
            physical.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = request.FieldId.Trim();
            if (await physical.ExecuteScalarAsync(token) is null)
                throw new ArgumentException(
                    $"物理列 {request.TableId}.{request.FieldId.Trim()} 不存在，新增字段元数据仅允许指向真实物理列；虚拟/派生字段需经受控表达式机制另行处理。",
                    nameof(request));
        }
        // 新增出来的字段是物理列（IS_VIRTUAL=0）：虚拟表达式一律拒绝，转换函数可随新增一起配置
        if (!string.IsNullOrWhiteSpace(request.Field.VirtualExpression))
            throw new ArgumentException("新增字段是物理列，不接受虚拟表达式；虚拟字段需另行登记。", nameof(request));
        if (!string.IsNullOrWhiteSpace(request.Field.ConvertFunction))
        {
            var validation = await expressionService.ValidateAsync(
                RestrictedExpressionKind.ConvertFunction, request.TableId, request.FieldId, request.Field.ConvertFunction, token);
            if (!validation.Ok) throw new ArgumentException(string.Join("；", validation.Errors), nameof(request));
        }

        const string sql = """
            INSERT INTO dbo.FIELDS
                (T_ID,F_ID,F_DESC,F_TYPE,BROWSE_URL,BROWSE_M_IDX,ONLY_CHOOSE,CHOOSE_PAGE,CHOOSE_MULTI,
                 REGEX,DISPLAY_LENGTH,DISPLAY_FORMAT,HEADER_ALIGN,ITEM_ALIGN,IS_VERIFY,VERIFY_INDEX,
                 IS_READONLY,IS_VISIBLE,IS_VIRTUAL,IS_AUTOINC,IS_QUERY,IS_COST,IS_SECRECY,DFT_VALUE,
                 CAN_COPY,IS_DEFAULT_FIELDS,CONVERT_FUNCTION,F_REMARK,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES
                (@TableId,@FieldId,@Label,@DataType,NULL,@BrowseModuleId,@OnlyChoose,@ChoosePage,@ChooseMultiple,
                 @Regex,@Width,@Format,@HeaderAlign,@Align,@Required,@VerifyIndex,
                 @Readonly,@Visible,0,0,@Queryable,@Cost,@Secrecy,@DefaultValue,
                 @CanCopy,@Default,@ConvertFunction,@Remark,@UpdatedBy,GETDATE());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        AddIdentity(command, request.TableId, request.FieldId);
        AddInput(command, request.Field, updatedBy);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new InvalidOperationException("新增字段失败。");
        // Write chooser data sources in the same transaction (ordered by SERIAL_NO)
        await ReplaceChoosersAsync(connection, transaction, request.TableId, request.FieldId.Trim(), request.Field.Choosers, updatedBy, token);
        await dirtyMarker.MarkDirtyForTableAsync(connection, transaction, request.TableId, updatedBy, token);
        await auditWriter.WriteEventAsync(connection, transaction, null, $"{request.TableId}.{request.FieldId}",
            "CREATE", "字段维护新增", updatedBy, "FIELD_ADMIN", result: 1,
            [new AuditFieldChange("(field)", null, JsonSerializer.Serialize(DescribeInput(request.Field)), null)],
            token);
        await transaction.CommitAsync(token);
        logger.LogInformation("新增字段 table={Table} field={Field} by={UpdatedBy}", request.TableId, request.FieldId, updatedBy);
    }

    public Task UpdateAsync(
        string tableId,
        string fieldId,
        FieldAdminInput field,
        FieldAdminInput? original,
        string updatedBy,
        CancellationToken token)
        => UpdateCoreAsync(tableId, fieldId, field, original, updatedBy, idempotencyKey: null, token);

    /// <summary>
    /// 带幂等键的字段更新：同一键重放直接返回上次的结果键，**不再落库**。
    ///
    /// <para>
    /// 幂等判定必须发生在乐观锁之前——重放时提交的快照是上一轮的旧值，库中已是新值，
    /// 先比快照会把"重放"误报成"内容已被他人修改"。
    /// </para>
    /// <para>
    /// 抢占、业务写入与审计在同一个事务内：业务失败或审计写不进去都整单回滚，占位随之释放，
    /// 不会留下"占了键却没有结果"的空档。
    /// </para>
    /// </summary>
    public async Task<string> UpdateIdempotentAsync(
        string tableId,
        string fieldId,
        FieldAdminInput field,
        FieldAdminInput? original,
        string updatedBy,
        string idempotencyKey,
        CancellationToken token)
        => await UpdateCoreAsync(tableId, fieldId, field, original, updatedBy, idempotencyKey, token);

    private async Task<string> UpdateCoreAsync(
        string tableId,
        string fieldId,
        FieldAdminInput field,
        FieldAdminInput? original,
        string updatedBy,
        string? idempotencyKey,
        CancellationToken token)
    {
        EnsureIdentifier(tableId, fieldId);
        Validate(field);
        // 表达式随字段一起保存：先过受控校验（空值表示清空，不解析），不通过则整单拒绝
        await ValidateExpressionsAsync(tableId, fieldId, field, token);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var resultKey = $"{tableId}.{fieldId}".Trim();
        if (idempotencyKey is not null)
        {
            var existing = await idempotency.TryClaimAsync(
                connection, transaction, idempotencyKey, moduleId: 0, action: "CFG_FIELD", token);
            if (existing is { ResultKey: not null })
            {
                await transaction.CommitAsync(token);
                return existing.ResultKey;
            }
        }

        var current = await ReadCurrentInputAsync(connection, transaction, tableId, fieldId, token)
            ?? throw new KeyNotFoundException("字段不存在。");
        // 提交内容与库中已一致（实为无操作）：不写、不审计，也不再比对快照。
        // 客户端保存成功后可能仍带着保存前的快照再次提交（重复保存/离开确认保存），
        // 此时比对快照会把"重复提交同一份内容"误报为"内容已被他人修改"。
        if (IsNoOpUpdate(field, current))
        {
            if (idempotencyKey is not null)
            {
                await idempotency.CompleteAsync(
                    connection, transaction, idempotencyKey, resultKey, flowStarted: false, token);
            }
            await transaction.CommitAsync(token);
            return resultKey;
        }
        if (original is not null && !SameInput(original, current))
        {
            logger.LogWarning("字段乐观锁冲突 table={Table} field={Field} by={UpdatedBy}", tableId, fieldId, updatedBy);
            throw new ArgumentException("字段内容已被他人修改，请刷新后重试！", nameof(field));
        }
        // 系统列组：类型/校验/数据源/权限与分组结构锁定，仅名称显示备注与表单位置可改。
        if (WorkflowStates.IsLifecycleColumn(fieldId) && BuildSystemColumnUpdateError(current, field) is { } locked)
        {
            throw new ArgumentException(locked, nameof(field));
        }

        const string sql = """
            UPDATE dbo.FIELDS SET
                F_DESC=@Label,F_TYPE=@DataType,DISPLAY_LENGTH=@Width,ITEM_ALIGN=@Align,HEADER_ALIGN=@HeaderAlign,
                DISPLAY_FORMAT=@Format,IS_VISIBLE=@Visible,IS_DEFAULT_FIELDS=@Default,IS_QUERY=@Queryable,
                IS_READONLY=@Readonly,IS_VERIFY=@Required,IS_COST=@Cost,IS_SECRECY=@Secrecy,DFT_VALUE=@DefaultValue,
                VERIFY_INDEX=@VerifyIndex,REGEX=@Regex,F_REMARK=@Remark,BROWSE_URL=@BrowseUrl,BROWSE_M_IDX=@BrowseModuleId,
                ONLY_CHOOSE=@OnlyChoose,CHOOSE_MULTI=@ChooseMultiple,CHOOSE_PAGE=@ChoosePage,CAN_COPY=@CanCopy,
                FORM_OPTIONS=@FormOptions,
                VIRTUAL_EXP=CASE WHEN @WriteVirtualExp=1 THEN @VirtualExp ELSE VIRTUAL_EXP END,
                CONVERT_FUNCTION=CASE WHEN @WriteConvertFunction=1 THEN @ConvertFunction ELSE CONVERT_FUNCTION END,
                LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE()
            WHERE T_ID=@TableId AND F_ID=@FieldId;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        AddIdentity(command, tableId, fieldId);
        AddInput(command, field, updatedBy);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new KeyNotFoundException("字段不存在。");
        // Replace chooser data sources in the same transaction (DELETE + INSERT, SERIAL_NO 1..n)
        await ReplaceChoosersAsync(connection, transaction, tableId, fieldId.Trim(), field.Choosers, updatedBy, token);
        await dirtyMarker.MarkDirtyForTableAsync(connection, transaction, tableId, updatedBy, token);
        var changes = DiffInputs(current, field).ToList();
        AddExpressionChange(changes, "VIRTUAL_EXP", current.VirtualExpression, field.VirtualExpression);
        AddExpressionChange(changes, "CONVERT_FUNCTION", current.ConvertFunction, field.ConvertFunction);
        await auditWriter.WriteEventAsync(connection, transaction, null, $"{tableId}.{fieldId}",
            "UPDATE", "字段维护更新", updatedBy, "FIELD_ADMIN", result: 1, changes, token);
        if (idempotencyKey is not null)
        {
            await idempotency.CompleteAsync(
                connection, transaction, idempotencyKey, resultKey, flowStarted: false, token);
        }
        await transaction.CommitAsync(token);
        logger.LogInformation("更新字段 table={Table} field={Field} by={UpdatedBy}", tableId, fieldId, updatedBy);
        return resultKey;
    }

    public async Task DeleteAsync(string tableId, string fieldId, string updatedBy, CancellationToken token)
    {
        EnsureIdentifier(tableId, fieldId);
        // 系统列组：单据生命周期列由管线持有，不可删除（开连接前即拒绝，不触库）。
        if (WorkflowStates.IsLifecycleColumn(fieldId.Trim()))
        {
            throw new ArgumentException("系统列不允许删除（单据生命周期列由管线持有）。", nameof(fieldId));
        }
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var snapshot = await ReadCurrentInputAsync(connection, transaction, tableId, fieldId, token)
            ?? throw new KeyNotFoundException("字段不存在。");
        if (await DeleteFieldMetadataAsync(connection, transaction, tableId, fieldId, token) != 1)
        {
            await transaction.RollbackAsync(token);
            throw new KeyNotFoundException("字段不存在。");
        }
        await dirtyMarker.MarkDirtyForTableAsync(connection, transaction, tableId, updatedBy, token);
        await auditWriter.WriteEventAsync(connection, transaction, null, $"{tableId}.{fieldId}",
            "DELETE", "字段维护删除", updatedBy, "FIELD_ADMIN", result: 1,
            [new AuditFieldChange("(field)", JsonSerializer.Serialize(DescribeInput(snapshot)), null, null)],
            token);
        await transaction.CommitAsync(token);
        logger.LogInformation("删除字段 table={Table} field={Field} by={UpdatedBy}", tableId, fieldId, updatedBy);
    }

    private static async Task<FieldAdminInput?> ReadCurrentInputAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableId,
        string fieldId,
        CancellationToken token)
    {
        // Snapshot defaults must mirror GetMetadataAsync ('left' align, 'nvarchar' type):
        // the optimistic lock compares the client round-tripped snapshot against this read,
        // so any defaulting mismatch reports a phantom concurrent modification on every save.
        const string sql = """
             SELECT COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(F_ID))),COALESCE(F_TYPE,'nvarchar'),COALESCE(DISPLAY_LENGTH,100),
                    COALESCE(NULLIF(LTRIM(RTRIM(ITEM_ALIGN)),''),'left'),COALESCE(NULLIF(HEADER_ALIGN,''),'center'),DISPLAY_FORMAT,
                   CAST(COALESCE(IS_VISIBLE,1) AS bit),CAST(COALESCE(IS_DEFAULT_FIELDS,0) AS bit),CAST(COALESCE(IS_QUERY,1) AS bit),
                   CAST(COALESCE(IS_READONLY,0) AS bit),CAST(COALESCE(IS_VERIFY,0) AS bit),CAST(COALESCE(IS_COST,0) AS bit),
                   CAST(COALESCE(IS_SECRECY,0) AS bit),DFT_VALUE,VERIFY_INDEX,REGEX,F_REMARK,BROWSE_URL,BROWSE_M_IDX,
                   CAST(COALESCE(ONLY_CHOOSE,0) AS bit),CAST(COALESCE(CHOOSE_MULTI,0) AS bit),CHOOSE_PAGE,
                   CAST(COALESCE(CAN_COPY,1) AS bit),FORM_OPTIONS,
                   LTRIM(RTRIM(ISNULL(VIRTUAL_EXP,''))),LTRIM(RTRIM(ISNULL(CONVERT_FUNCTION,'')))
            FROM dbo.FIELDS WITH (NOLOCK)
            WHERE T_ID=@TableId AND LTRIM(RTRIM(F_ID))=@FieldId;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var result = new FieldAdminInput(
            reader.GetString(0), reader.GetString(1), Math.Clamp(reader.GetInt32(2), 40, 300),
            reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10),
            reader.GetBoolean(11), reader.GetBoolean(12), reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetInt32(14), reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetInt32(18), reader.GetBoolean(19), reader.GetBoolean(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            [],
            reader.GetBoolean(22),
            reader.IsDBNull(reader.GetOrdinal("FORM_OPTIONS")) ? null : reader.GetString(reader.GetOrdinal("FORM_OPTIONS")),
            // 表达式列补在 FORM_OPTIONS 之后（按序号取，前面每加一列都要跟着挪）
            reader.GetString(24),
            reader.GetString(25));
        await reader.CloseAsync();
        return result with { Choosers = await ReadFieldChoosersAsync(connection, transaction, tableId, fieldId, token) };
    }

    /// <summary>读取字段的数据源列表（FIELD_DATASOURCE，按 SERIAL_NO 排序）。</summary>
    private static async Task<IReadOnlyList<FieldAdminChooser>> ReadFieldChoosersAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string tableId,
        string fieldId,
        CancellationToken token)
    {
        const string sql = """
            SELECT c.SERIAL_NO,CAST(COALESCE(c.ACTIVE_TAG,0) AS bit) AS ACTIVE_TAG,
                   LTRIM(RTRIM(ISNULL(c.SOURCE_T_ID,''))) AS SOURCE_T_ID,
                   LTRIM(RTRIM(ISNULL(c.SOURCE_DESC,''))) AS SOURCE_DESC,c.SOURCE_M_IDX,
                   c.FILTER_STRUCT,c.RETURN_ITEMS
            FROM dbo.FIELD_DATASOURCE c WITH (NOLOCK)
            WHERE c.T_ID=@TableId AND LTRIM(RTRIM(c.F_ID))=@FieldId
            ORDER BY c.SERIAL_NO;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<FieldAdminChooser>();
        while (await reader.ReadAsync(token))
        {
            var table = reader.GetString(reader.GetOrdinal("SOURCE_T_ID")).Trim();
            if (table.Length == 0) continue;
            result.Add(new FieldAdminChooser(
                reader.GetBoolean(reader.GetOrdinal("ACTIVE_TAG")),
                table,
                reader.GetString(reader.GetOrdinal("SOURCE_DESC")),
                reader.IsDBNull(reader.GetOrdinal("SOURCE_M_IDX")) ? null : reader.GetInt32(reader.GetOrdinal("SOURCE_M_IDX")),
                reader.IsDBNull(reader.GetOrdinal("FILTER_STRUCT")) ? null : reader.GetString(reader.GetOrdinal("FILTER_STRUCT")),
                reader.IsDBNull(reader.GetOrdinal("RETURN_ITEMS")) ? null : reader.GetString(reader.GetOrdinal("RETURN_ITEMS")),
                reader.GetInt32(reader.GetOrdinal("SERIAL_NO"))));
        }
        return result;
    }

    private static void AddIdentity(SqlCommand command, string tableId, string fieldId)
    {
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
    }

    private static void AddInput(SqlCommand command, FieldAdminInput input, string updatedBy)
    {
        command.Parameters.Add("@Label", SqlDbType.NVarChar, 300).Value = input.Label.Trim();
        command.Parameters.Add("@DataType", SqlDbType.NVarChar, 100).Value = input.DataType;
        command.Parameters.Add("@Width", SqlDbType.Int).Value = input.Width;
        command.Parameters.Add("@Align", SqlDbType.NVarChar, 50).Value = input.Align ?? "";
        command.Parameters.Add("@HeaderAlign", SqlDbType.NVarChar, 50).Value = input.HeaderAlign;
        command.Parameters.Add("@Format", SqlDbType.NVarChar, 50).Value = DbValue(input.Format);
        command.Parameters.Add("@Visible", SqlDbType.Bit).Value = input.IsVisible;
        command.Parameters.Add("@Default", SqlDbType.Bit).Value = input.IsDefault;
        command.Parameters.Add("@Queryable", SqlDbType.Bit).Value = input.IsQueryable;
        command.Parameters.Add("@Readonly", SqlDbType.Bit).Value = input.IsReadonly;
        command.Parameters.Add("@Required", SqlDbType.Bit).Value = input.IsRequired;
        command.Parameters.Add("@Cost", SqlDbType.Bit).Value = input.IsCost;
        command.Parameters.Add("@Secrecy", SqlDbType.Bit).Value = input.IsSecrecy;
        command.Parameters.Add("@DefaultValue", SqlDbType.NVarChar, 200).Value = DbValue(input.DefaultValue);
        command.Parameters.Add("@VerifyIndex", SqlDbType.Int).Value = input.VerifyIndex ?? (object)DBNull.Value;
        command.Parameters.Add("@Regex", SqlDbType.NVarChar, 300).Value = DbValue(input.Regex);
        command.Parameters.Add("@Remark", SqlDbType.NVarChar, 500).Value = DbValue(input.Remark);
        command.Parameters.Add("@BrowseUrl", SqlDbType.VarChar, 1000).Value = DbValue(input.BrowseUrl);
        command.Parameters.Add("@BrowseModuleId", SqlDbType.Int).Value = input.BrowseModuleId ?? (object)DBNull.Value;
        command.Parameters.Add("@OnlyChoose", SqlDbType.Bit).Value = input.OnlyChoose;
        command.Parameters.Add("@ChooseMultiple", SqlDbType.Bit).Value = input.ChooseMultiple;
        command.Parameters.Add("@ChoosePage", SqlDbType.NVarChar, 500).Value = DbValue(input.ChoosePage);
        command.Parameters.Add("@CanCopy", SqlDbType.Bit).Value = input.CanCopy;
        command.Parameters.Add("@FormOptions", SqlDbType.NVarChar, 500).Value = DbValue(input.Options);
        // 表达式三态：null = 本次不改（UPDATE 里写回原值），空串 = 清空，非空 = 设为该值
        command.Parameters.Add("@WriteVirtualExp", SqlDbType.Bit).Value = input.VirtualExpression is not null;
        command.Parameters.Add("@VirtualExp", SqlDbType.NVarChar, 2000).Value = DbValue(input.VirtualExpression);
        command.Parameters.Add("@WriteConvertFunction", SqlDbType.Bit).Value = input.ConvertFunction is not null;
        command.Parameters.Add("@ConvertFunction", SqlDbType.NVarChar, 200).Value = DbValue(input.ConvertFunction);
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = updatedBy;
    }

    /// <summary>字段元数据扁平化（键对齐 DB 列名，值按写入口径归一），供审计明细 diff 与删除快照。</summary>
    internal static SortedDictionary<string, string?> DescribeInput(FieldAdminInput input)
    {
        var dict = new SortedDictionary<string, string?>(StringComparer.Ordinal)
        {
            ["F_DESC"] = input.Label.Trim(),
            ["F_TYPE"] = input.DataType,
            ["DISPLAY_LENGTH"] = input.Width.ToString(CultureInfo.InvariantCulture),
            ["ITEM_ALIGN"] = input.Align ?? "",
            ["HEADER_ALIGN"] = input.HeaderAlign,
            ["DISPLAY_FORMAT"] = DbText(input.Format),
            ["IS_VISIBLE"] = Bit(input.IsVisible),
            ["IS_DEFAULT_FIELDS"] = Bit(input.IsDefault),
            ["IS_QUERY"] = Bit(input.IsQueryable),
            ["IS_READONLY"] = Bit(input.IsReadonly),
            ["IS_VERIFY"] = Bit(input.IsRequired),
            ["IS_COST"] = Bit(input.IsCost),
            ["IS_SECRECY"] = Bit(input.IsSecrecy),
            ["DFT_VALUE"] = DbText(input.DefaultValue),
            ["VERIFY_INDEX"] = input.VerifyIndex?.ToString(CultureInfo.InvariantCulture),
            ["REGEX"] = DbText(input.Regex),
            ["F_REMARK"] = DbText(input.Remark),
            ["BROWSE_URL"] = DbText(input.BrowseUrl),
            ["BROWSE_M_IDX"] = input.BrowseModuleId?.ToString(CultureInfo.InvariantCulture),
            ["ONLY_CHOOSE"] = Bit(input.OnlyChoose),
            ["CHOOSE_MULTI"] = Bit(input.ChooseMultiple),
            ["CHOOSE_PAGE"] = DbText(input.ChoosePage),
            ["CAN_COPY"] = Bit(input.CanCopy),
            ["FORM_OPTIONS"] = DbText(input.Options),
        };
        for (var i = 0; i < input.Choosers.Count; i++)
        {
            var source = input.Choosers[i];
            var serial = source.SerialNo ?? i + 1;
            var prefix = $"CHOOSER[{serial}].";
            dict[$"{prefix}ACTIVE"] = Bit(source.Active);
            dict[$"{prefix}SOURCE_T_ID"] = DbText(source.Table);
            dict[$"{prefix}SOURCE_DESC"] = DbText(source.Description);
            dict[$"{prefix}SOURCE_M_IDX"] = source.ModuleId?.ToString(CultureInfo.InvariantCulture);
            dict[$"{prefix}FILTER_STRUCT"] = DbText(source.Filter);
            dict[$"{prefix}RETURN_ITEMS"] = DbText(source.ReturnMapping);
        }
        return dict;
    }

    /// <summary>表元数据扁平化（同 DescribeInput 口径）。</summary>
    internal static SortedDictionary<string, string?> DescribeTable(FieldAdminTableInput input) => new(StringComparer.Ordinal)
    {
        ["T_DESC"] = input.Description.Trim(),
        ["T_KIND"] = (input.Kind ?? "").Trim(),
        ["T_TYPE"] = (input.Type ?? "").Trim(),
        ["T_REMARK"] = DbText(input.Remark),
    };

    /// <summary>按扁平化键集对比前后差异，仅输出变化项（审计字段级明细）。</summary>
    internal static IReadOnlyList<AuditFieldChange> DiffInputs(FieldAdminInput before, FieldAdminInput after) =>
        Diff(DescribeInput(before), DescribeInput(after));

    internal static IReadOnlyList<AuditFieldChange> DiffTables(FieldAdminTableInput before, FieldAdminTableInput after) =>
        Diff(DescribeTable(before), DescribeTable(after));

    private static IReadOnlyList<AuditFieldChange> Diff(
        SortedDictionary<string, string?> before, SortedDictionary<string, string?> after)
    {
        var changes = new List<AuditFieldChange>();
        foreach (var key in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(key, out var oldValue);
            after.TryGetValue(key, out var newValue);
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                changes.Add(new AuditFieldChange(key, oldValue, newValue, null));
        }
        return changes;
    }

    private static string Bit(bool value) => value ? "1" : "0";

    private static string? DbText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool SameInput(FieldAdminInput a, FieldAdminInput b) =>
        a.Label.Trim().Equals(b.Label.Trim(), StringComparison.OrdinalIgnoreCase)
        && a.DataType.Equals(b.DataType, StringComparison.OrdinalIgnoreCase)
        && a.Width == b.Width
        && NullableEquals(a.Align, b.Align)
        && a.HeaderAlign.Equals(b.HeaderAlign, StringComparison.OrdinalIgnoreCase)
        && NullableEquals(a.Format, b.Format)
        && a.IsVisible == b.IsVisible && a.IsDefault == b.IsDefault && a.IsQueryable == b.IsQueryable
        && a.IsReadonly == b.IsReadonly && a.IsRequired == b.IsRequired && a.IsCost == b.IsCost && a.IsSecrecy == b.IsSecrecy
        && NullableEquals(a.DefaultValue, b.DefaultValue)
        && a.VerifyIndex == b.VerifyIndex
        && NullableEquals(a.Regex, b.Regex) && NullableEquals(a.Remark, b.Remark)
        && NullableEquals(a.BrowseUrl, b.BrowseUrl) && a.BrowseModuleId == b.BrowseModuleId
        && a.OnlyChoose == b.OnlyChoose && a.ChooseMultiple == b.ChooseMultiple && NullableEquals(a.ChoosePage, b.ChoosePage)
        && a.CanCopy == b.CanCopy
        && a.Choosers.Count == b.Choosers.Count
        && a.Choosers.Zip(b.Choosers).All(pair => SameChooser(pair.First, pair.Second))
        // 表达式：null = 调用方本次不改（如工作台列宽保存路径），不参与冲突判定
        && (a.VirtualExpression is null || b.VirtualExpression is null || NullableEquals(a.VirtualExpression, b.VirtualExpression))
        && (a.ConvertFunction is null || b.ConvertFunction is null || NullableEquals(a.ConvertFunction, b.ConvertFunction));

    /// <summary>
    /// 提交内容与库中现有一致（本次更新实为无操作）：结构字段、数据源、下拉选项都相同，
    /// 且受控表达式同口径（null = 本次不改）。用于跳过无操作写入，并避免把重复提交误报为并发修改。
    /// </summary>
    internal static bool IsNoOpUpdate(FieldAdminInput next, FieldAdminInput current) =>
        SameInput(next, current)
        && NullableEquals(next.Options, current.Options)
        && (next.VirtualExpression is null || NullableEquals(next.VirtualExpression, current.VirtualExpression))
        && (next.ConvertFunction is null || NullableEquals(next.ConvertFunction, current.ConvertFunction));

    private static bool SameChooser(FieldAdminChooser a, FieldAdminChooser b) =>
        a.Active == b.Active && NullableEquals(a.Table, b.Table) && NullableEquals(a.Description, b.Description)
        && a.ModuleId == b.ModuleId && NullableEquals(a.Filter, b.Filter) && NullableEquals(a.ReturnMapping, b.ReturnMapping)
        && a.SerialNo == b.SerialNo;

    /// <summary>
    /// 系统列更新结构锁：仅名称、显示与备注类（标签/列宽/对齐/格式/
    /// 可见/默认/查询/备注/校验顺序）可改；类型/校验/数据源/权限与分组结构
    /// 锁定。返回 null 表示仅动了可改项，否则返回拒绝原因（不触库，便于单测）。
    /// </summary>
    internal static string? BuildSystemColumnUpdateError(FieldAdminInput current, FieldAdminInput next)
    {
        var editableOnly = next with
        {
            Label = current.Label,
            Width = current.Width,
            Align = current.Align,
            HeaderAlign = current.HeaderAlign,
            Format = current.Format,
            IsVisible = current.IsVisible,
            IsDefault = current.IsDefault,
            IsQueryable = current.IsQueryable,
            Remark = current.Remark,
            VerifyIndex = current.VerifyIndex,
        };
        if (!SameInput(editableOnly, current) || !NullableEquals(current.Options, next.Options))
        {
            return "系统列只允许修改名称、显示与备注类属性，类型、校验、数据源、权限与分组结构锁定。";
        }
        return null;
    }

    private static bool NullableEquals(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) ? string.IsNullOrWhiteSpace(b) : string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 正则语法校验，选项与超时同运行期校验（RecordPayloadValidator 的字段正则）。
    /// 只判长度不判语法时，坏模式能存进元数据，直到保存单据时才暴露。
    /// </summary>
    private static bool IsCompilableRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 表达式随字段保存：把本次要写的表达式过一遍受控校验（null = 本次不改，空串 = 清空不解析）。
    /// 任何一项不通过就整体拒绝，避免落半截配置。
    /// </summary>
    private async Task ValidateExpressionsAsync(string tableId, string fieldId, FieldAdminInput field, CancellationToken token)
    {
        var errors = new List<string>();
        if (field.VirtualExpression is not null)
        {
            var virtualResult = await expressionService.ValidateAsync(
                RestrictedExpressionKind.VirtualExp, tableId, fieldId, field.VirtualExpression, token);
            errors.AddRange(virtualResult.Errors);
        }
        if (field.ConvertFunction is not null)
        {
            var convertResult = await expressionService.ValidateAsync(
                RestrictedExpressionKind.ConvertFunction, tableId, fieldId, field.ConvertFunction, token);
            errors.AddRange(convertResult.Errors);
        }
        if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors), nameof(field));
    }

    /// <summary>表达式变更进审计明细：null = 本次不改（不记），同值不记。</summary>
    private static void AddExpressionChange(List<AuditFieldChange> changes, string column, string? before, string? after)
    {
        if (after is null) return;
        var oldValue = string.IsNullOrWhiteSpace(before) ? null : before.Trim();
        var newValue = string.IsNullOrWhiteSpace(after) ? null : after.Trim();
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) return;
        changes.Add(new AuditFieldChange(column, oldValue, newValue, null));
    }

    private static void Validate(FieldAdminInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Label) || input.Label.Trim().Length > 300)
            throw new ArgumentException("字段名称不能为空且不能超过 300 个字符。");
        if (PlaceholderLabels.Contains(input.Label.Trim()))
            throw new ArgumentException("字段名称不能是占位文本（NULL / &nbsp;），请填写真实的字段中文名。");
        if (!AllowedTypes.Contains(input.DataType)) throw new ArgumentException("字段类型无效。");
        if (input.Width is < 40 or > 300) throw new ArgumentException("显示宽度必须在 40-300 之间。");
        if (!AllowedAlign.Contains(input.Align ?? "")) throw new ArgumentException("对齐方式无效。");
        if (!AllowedAlign.Contains(input.HeaderAlign)) throw new ArgumentException("列标题对齐方式无效。");
        if ((input.Format?.Length ?? 0) > 50) throw new ArgumentException("显示格式过长。");
        if ((input.DefaultValue?.Length ?? 0) > 200) throw new ArgumentException("默认值过长。");
        if ((input.Regex?.Length ?? 0) > 300) throw new ArgumentException("正则表达式过长。");
        if (!string.IsNullOrWhiteSpace(input.Regex) && !IsCompilableRegex(input.Regex))
            throw new ArgumentException("正则表达式无法编译，请检查语法。");
        if ((input.Remark?.Length ?? 0) > 500) throw new ArgumentException("备注过长。");
        if (input.VerifyIndex is < 0 or > 9999) throw new ArgumentException("校验顺序无效。");
        if ((input.BrowseUrl?.Length ?? 0) > 1000) throw new ArgumentException("查看详情 URL 过长。");
        if (!string.IsNullOrWhiteSpace(input.BrowseUrl) &&
            (!Uri.TryCreate(input.BrowseUrl, UriKind.Relative, out _) || input.BrowseUrl.TrimStart().StartsWith("//")))
            throw new ArgumentException("查看详情 URL 仅允许站内相对路径。");
        if ((input.ChoosePage?.Length ?? 0) > 500) throw new ArgumentException("数据选择页面过长。");
        foreach (var source in input.Choosers)
        {
            if ((source.Table?.Length ?? 0) > 300 || (source.Description?.Length ?? 0) > 50)
                throw new ArgumentException("数据选择源配置无效（来源表/说明超长）。");
            if (!string.IsNullOrWhiteSpace(source.Table) && !WorkbenchSql.Identifier.IsMatch(source.Table.Trim()))
                throw new ArgumentException($"数据选择源表名无效：{source.Table}");
            // Filter conditions only accept structured JSON; raw SQL is never accepted here
            if (!string.IsNullOrWhiteSpace(source.Filter) && !ChooserFilterStruct.TryParse(source.Filter, out _))
                throw new ArgumentException("过滤条件必须是结构化 JSON（{\"logic\":\"AND\",\"items\":[...]}）；旧手写 SQL 不再接受。");
            if (!string.IsNullOrWhiteSpace(source.ReturnMapping) && ChooserReturnItems.Parse(source.ReturnMapping) is null)
                throw new ArgumentException("回填映射必须是 JSON 数组（[{\"target\":\"...\",\"column\":\"...\"}]）。");
        }
    }

    /// <summary>
    /// 事务内全量替换字段的数据源（DELETE + INSERT；空来源表不落行；保存即校验）。
    /// 过滤条件空 → FILTER_STRUCT=NULL（fail-closed）；迁移清单内待重建来源禁止静默清空。
    /// </summary>
    private static async Task ReplaceChoosersAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableId,
        string fieldId,
        IReadOnlyList<FieldAdminChooser> choosers,
        string updatedBy,
        CancellationToken token)
    {
        await using (var delete = new SqlCommand("DELETE FROM dbo.FIELD_DATASOURCE WHERE T_ID=@TableId AND F_ID=@FieldId;", connection, transaction))
        {
            delete.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
            delete.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
            await delete.ExecuteNonQueryAsync(token);
        }

        const string insertSql = """
            INSERT INTO dbo.FIELD_DATASOURCE
                (T_ID,F_ID,SERIAL_NO,ACTIVE_TAG,SOURCE_T_ID,SOURCE_DESC,SOURCE_M_IDX,FILTER_STRUCT,RETURN_ITEMS,
                 CREATE_BY,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (@TableId,@FieldId,@SerialNo,@Active,@SourceTable,@SourceDesc,@SourceModule,@FilterStruct,@ReturnItems,
                    @UpdatedBy,GETDATE(),@UpdatedBy,GETDATE());
            """;

        for (var i = 0; i < choosers.Count; i++)
        {
            var source = choosers[i];
            // SERIAL_NO = 列表顺序（1..n），随前端增删/上下移重排；不沿用既有槽位号
            var serial = i + 1;
            if (string.IsNullOrWhiteSpace(source.Table)) continue;
            var sourceTable = source.Table.Trim();
            if (!WorkbenchSql.Identifier.IsMatch(sourceTable))
                throw new ArgumentException($"数据选择源表名无效：{sourceTable}");
            if (!await WorkbenchSql.TableExistsAsync(connection, sourceTable, token, transaction))
                throw new ArgumentException($"数据选择源表 {sourceTable} 不存在。");

            string? filterStructJson = null;
            if (!string.IsNullOrWhiteSpace(source.Filter))
            {
                if (!ChooserFilterStruct.TryParse(source.Filter, out var filterStruct) || filterStruct is null)
                    throw new ArgumentException($"数据来源 {serial} 的过滤条件不是合法的  结构化 JSON。");
                var validation = await ChooserFilterValidator.ValidateAsync(connection, filterStruct, sourceTable, token, transaction);
                if (!validation.Ok)
                    throw new ArgumentException($"数据来源 {serial} 过滤条件校验失败：{string.Join("；", validation.Messages.Take(4))}");
                filterStructJson = filterStruct.ToJson();
            }

            string? returnItemsJson = null;
            if (!string.IsNullOrWhiteSpace(source.ReturnMapping))
            {
                var returnItems = ChooserReturnItems.Parse(source.ReturnMapping)
                    ?? throw new ArgumentException($"数据来源 {serial} 的回填映射不是合法的 JSON 数组。");
                if (returnItems.Any(item => !WorkbenchSql.Identifier.IsMatch(item.Column.Trim())
                    || !WorkbenchSql.Identifier.IsMatch(item.Target.Trim())))
                    throw new ArgumentException($"数据来源 {serial} 回填映射含非法来源列/目标字段名。");
                var columns = returnItems.Select(item => item.Column.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // 来源列允许物理列或来源表内受控虚拟列（来源查询结果集列含 VIRTUAL_EXP 派生值）
                if (columns.Length > 0 && !await WorkbenchSql.ReturnColumnsExistAsync(connection, sourceTable, columns, token, transaction))
                    throw new ArgumentException($"数据来源 {serial} 回填映射引用了源表 {sourceTable} 中既非物理列也非受控虚拟列的来源字段。");
                returnItemsJson = ChooserReturnItems.ToJson(returnItems);
            }

            await using var insert = new SqlCommand(insertSql, connection, transaction);
            insert.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
            insert.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
            insert.Parameters.Add("@SerialNo", SqlDbType.Int).Value = serial;
            insert.Parameters.Add("@Active", SqlDbType.Bit).Value = source.Active;
            insert.Parameters.Add("@SourceTable", SqlDbType.NVarChar, 300).Value = sourceTable;
            insert.Parameters.Add("@SourceDesc", SqlDbType.NVarChar, 50).Value = DbValue(source.Description) ?? (object)DBNull.Value;
            insert.Parameters.Add("@SourceModule", SqlDbType.Int).Value = source.ModuleId ?? (object)DBNull.Value;
            insert.Parameters.Add("@FilterStruct", SqlDbType.NVarChar, -1).Value = (object?)filterStructJson ?? DBNull.Value;
            insert.Parameters.Add("@ReturnItems", SqlDbType.NVarChar, -1).Value = (object?)returnItemsJson ?? DBNull.Value;
            insert.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = updatedBy;
            await insert.ExecuteNonQueryAsync(token);
        }
    }

    private static void EnsureIdentifier(string tableId, string? fieldId)
    {
        if (!WorkbenchSql.Identifier.IsMatch(tableId)) throw new ArgumentException("数据表名无效。");
        if (fieldId is not null && !WorkbenchSql.Identifier.IsMatch(fieldId)) throw new ArgumentException("字段名无效。");
    }

    private static void ValidateTableInput(FieldAdminTableInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Trim().Length > 300)
            throw new ArgumentException("数据表描述不能为空且不能超过 300 个字符。");
        if (!AllowedTableKinds.Contains(input.Kind ?? ""))
            throw new ArgumentException("数据表性质无效（P=主表/S=明细/O=其它/V=视图）。");
        if (!AllowedTableTypes.Contains(input.Type ?? ""))
            throw new ArgumentException("数据表类型无效（TABLE/VIEW/UNKNOW）。");
        if ((input.Remark?.Length ?? 0) > 500)
            throw new ArgumentException("数据表备注过长。");
    }

    private static void AddTableParameters(SqlCommand command, string tableId, FieldAdminTableInput input, string updatedBy)
    {
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 300).Value = input.Description.Trim();
        command.Parameters.Add("@Kind", SqlDbType.NVarChar, 20).Value = (input.Kind ?? "").Trim();
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = (input.Type ?? "").Trim();
        command.Parameters.Add("@Remark", SqlDbType.NVarChar, 500).Value = DbValue(input.Remark);
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = updatedBy;
    }

    private static bool SameTableInput(FieldAdminTableInput a, FieldAdminTableInput b) =>
        a.Description.Trim().Equals(b.Description.Trim(), StringComparison.OrdinalIgnoreCase)
        && NullableEquals(a.Kind, b.Kind)
        && NullableEquals(a.Type, b.Type)
        && NullableEquals(a.Remark, b.Remark);

    private static async Task<FieldAdminTableInput?> ReadCurrentTableInputAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableId,
        CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(T_DESC)),LTRIM(RTRIM(ISNULL(T_KIND,''))),LTRIM(RTRIM(ISNULL(T_TYPE,''))),T_REMARK
            FROM dbo.TABLES WITH (NOLOCK)
            WHERE T_ID=@TableId;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new(reader.GetString(0), NullIfEmpty(reader.GetString(1)), NullIfEmpty(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <summary>
    /// 按物理列生成字段元数据的输入（描述取列说明、类型取物理类型、主键/自增/计算列取物理结构）：
    /// 「未管理字段批量生成」与「从物理对象登记表」共用同一份事实，避免两处口径分叉。
    /// 类型不受支持的列也会返回，由调用方判定后回报原因。
    /// </summary>
    private sealed record GeneratedFieldSource(
        string FieldId,
        string Description,
        string DataType,
        bool IsPrimaryKey,
        bool IsAutoIncrement,
        bool IsReadonly);

    /// <summary>读该表全部物理列的生成输入（一次取回，按 column_id 排序）。</summary>
    private static async Task<IReadOnlyList<GeneratedFieldSource>> ReadGeneratedFieldsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string tableId,
        CancellationToken token)
    {
        const string sql = """
            SELECT c.name AS F_ID,
                   TYPE_NAME(c.user_type_id) AS DATA_TYPE,
                   ISNULL(CONVERT(nvarchar(500), ep.value),N'') AS DESCRIPTION,
                   CAST(c.is_identity AS bit) AS IS_IDENTITY,
                   CAST(c.is_computed AS bit) AS IS_COMPUTED,
                   CAST(CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS bit) AS IS_PK
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            LEFT JOIN sys.extended_properties ep
              ON ep.class=1 AND ep.major_id=c.object_id AND ep.minor_id=c.column_id AND ep.name=N'MS_Description'
            LEFT JOIN (SELECT ic.object_id,ic.column_id FROM sys.indexes i
                       JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                       WHERE i.is_primary_key=1) pk
              ON pk.object_id=c.object_id AND pk.column_id=c.column_id
            WHERE s.name=N'dbo' AND o.name=@TableId
            ORDER BY c.column_id;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<GeneratedFieldSource>();
        while (await reader.ReadAsync(token))
        {
            var field = reader.GetString(0).Trim();
            if (!WorkbenchSql.Identifier.IsMatch(field)) continue;
            var dataType = MapPhysicalType(reader.GetString(1));
            result.Add(new(
                FieldId: field,
                Description: ResolveLabel(reader.GetString(2), field),
                DataType: dataType,
                IsPrimaryKey: reader.GetBoolean(5),
                IsAutoIncrement: reader.GetBoolean(3),
                IsReadonly: reader.GetBoolean(4) || IsRowVersionType(dataType)));
        }
        return result;
    }

    /// <summary>表的物理对象信息（类型 U/V + 表说明，说明为空/占位时回落到表名）。</summary>
    private static async Task<(string ObjectType, string Description)?> ReadPhysicalObjectAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableId,
        CancellationToken token)
    {
        const string sql = """
            SELECT o.type,ISNULL(CONVERT(nvarchar(500), ep.value),N'')
            FROM sys.objects o
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            LEFT JOIN sys.extended_properties ep
              ON ep.class=1 AND ep.major_id=o.object_id AND ep.minor_id=0 AND ep.name=N'MS_Description'
            WHERE s.name=N'dbo' AND o.name=@TableId AND o.type IN ('U','V');
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return (reader.GetString(0).Trim(), ResolveLabel(reader.GetString(1), tableId));
    }

    /// <summary>该表已有元数据的字段名集合（生成字段元数据前判重）。</summary>
    private static async Task<HashSet<string>> ReadExistingFieldIdsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string tableId,
        CancellationToken token)
    {
        var statuses = await ReadFieldPhysicalStatusAsync(connection, transaction, tableId, token);
        return statuses.Select(item => item.FieldId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>插入一条按物理列生成的字段元数据（调用方负责事务、判重、类型支持与审计）。</summary>
    private static async Task InsertGeneratedFieldAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableId,
        GeneratedFieldSource source,
        string updatedBy,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.FIELDS
                (T_ID,F_ID,F_DESC,F_TYPE,IS_QUERY,IS_DEFAULT_FIELDS,IS_VISIBLE,IS_VIRTUAL,IS_COST,IS_SECRECY,
                 IS_READONLY,IS_AUTOINC,IS_PK,CAN_COPY,DISPLAY_LENGTH,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES
                (@TableId,@FieldId,@Description,@DataType,1,1,1,0,0,0,
                 @Readonly,@AutoIncrement,@PrimaryKey,1,100,@UpdatedBy,GETDATE());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = source.FieldId;
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 500).Value = source.Description;
        command.Parameters.Add("@DataType", SqlDbType.NVarChar, 100).Value = source.DataType;
        command.Parameters.Add("@Readonly", SqlDbType.Bit).Value = source.IsReadonly;
        command.Parameters.Add("@AutoIncrement", SqlDbType.Bit).Value = source.IsAutoIncrement;
        command.Parameters.Add("@PrimaryKey", SqlDbType.Bit).Value = source.IsPrimaryKey;
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = updatedBy;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>物理类型名归一：sysname 即 nvarchar、rowversion 即 timestamp，其余原样。</summary>
    private static string MapPhysicalType(string physicalType) => physicalType.Trim().ToLowerInvariant() switch
    {
        "sysname" => "nvarchar",
        "rowversion" => "timestamp",
        var other => other,
    };

    /// <summary>行版本列由数据库维护，表单不得要求录入（标只读）。</summary>
    private static bool IsRowVersionType(string dataType) => dataType is "timestamp";

    /// <summary>说明为空或占位文本（'NULL' / '&nbsp;'）时回落到名字，避免把占位文本写成标签。</summary>
    private static string ResolveLabel(string? value, string fallback)
    {
        var text = value?.Trim() ?? "";
        return text.Length == 0 || PlaceholderLabels.Contains(text) ? fallback : text;
    }

    /// <summary>单个字段的物理列状态（幽灵判定与清理共用的唯一来源）。</summary>
    private sealed record FieldPhysicalStatus(
        string FieldId,
        string Description,
        string DataType,
        bool IsVirtual,
        bool PhysicalExists);

    /// <summary>读该表全部字段的物理列状态（一次取回，判定与清理都在内存里对同一份事实做决定）。</summary>
    private static async Task<IReadOnlyList<FieldPhysicalStatus>> ReadFieldPhysicalStatusAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string tableId,
        CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)) AS F_ID,
                   COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID))) AS F_DESC,
                   COALESCE(NULLIF(LTRIM(RTRIM(f.F_TYPE)),''),'nvarchar') AS F_TYPE,
                   CAST(COALESCE(f.IS_VIRTUAL,0) AS bit) AS IS_VIRTUAL,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.columns c
                                            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                                            JOIN sys.schemas s ON o.schema_id=s.schema_id
                                            WHERE s.name=N'dbo' AND o.name=@TableId
                                              AND c.name=LTRIM(RTRIM(f.F_ID))) THEN 1 ELSE 0 END AS bit) AS IS_PHYSICAL
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@TableId
            ORDER BY f.F_ID;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<FieldPhysicalStatus>();
        while (await reader.ReadAsync(token))
        {
            var field = reader.GetString(0).Trim();
            if (!WorkbenchSql.Identifier.IsMatch(field)) continue;
            result.Add(new(field, reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        }
        return result;
    }

    /// <summary>
    /// 删除单条字段元数据并清理其历史列配置引用（同一事务内；调用方负责系统列拒绝、审计与脏标记）。
    /// 返回受影响行数：0 表示该字段元数据已不存在。
    /// </summary>
    private static async Task<int> DeleteFieldMetadataAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableId,
        string fieldId,
        CancellationToken token)
    {
        await using var delete = new SqlCommand("DELETE FROM dbo.FIELDS WHERE T_ID=@TableId AND F_ID=@FieldId", connection, transaction);
        delete.Parameters.Add("@TableId", SqlDbType.VarChar, 100).Value = tableId;
        delete.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
        if (await delete.ExecuteNonQueryAsync(token) != 1) return 0;
        // 引用清理清单与字段删除同源：用户列配置、默认列、查询条件记忆、报表条件引用一并收口
        const string cleanSql = """
            DELETE FROM dbo.SYSQL_FIELDS WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.SYSQL_DEFAULT WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.SYSQL_CONDITION WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.SYSQL_COND_DFT WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.SYSQD_CONDITION WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.SYSQQ WHERE F_ID=@TableDotField;
            DELETE FROM dbo.SYSQR_DEFAULT WHERE F_ID=@TableDotField;
            """;
        await using var clean = new SqlCommand(cleanSql, connection, transaction);
        clean.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
        clean.Parameters.Add("@TableId", SqlDbType.VarChar, 100).Value = tableId;
        clean.Parameters.Add("@TableDotField", SqlDbType.NVarChar, 220).Value = $"{tableId}.{fieldId.Trim()}";
        await clean.ExecuteNonQueryAsync(token);
        return 1;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private SqlConnection CreateConnection() => connections.Create();
}
