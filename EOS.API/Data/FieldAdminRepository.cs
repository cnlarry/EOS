using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed class FieldAdminRepository(DbConnectionFactory connections, ILogger<FieldAdminRepository> logger)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvarchar", "varchar", "nchar", "char", "int", "bigint", "smallint", "tinyint", "decimal", "numeric",
        "float", "real", "money", "smallmoney", "date", "datetime", "datetime2", "smalldatetime", "time", "bit",
        "uniqueidentifier", "text", "ntext", "image", "varbinary", "binary", "xml", "timestamp", "sql_variant",
        "geometry", "geography", "hierarchyid",
        // 旧系统伪类型与历史遗留写法（保留可编辑，避免已有行保存失败）
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
        var kindFilter = kind?.Trim() ?? "";
        if (kindFilter.Length > 0 && !AllowedTableKinds.Contains(kindFilter))
            throw new ArgumentException("表性质筛选无效。", nameof(kind));
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(t.T_ID)),LTRIM(RTRIM(t.T_DESC)),LTRIM(RTRIM(ISNULL(t.T_KIND,''))),LTRIM(RTRIM(ISNULL(t.T_TYPE,''))),
                   (SELECT COUNT(*) FROM dbo.FIELDS f WITH (NOLOCK) WHERE f.T_ID=t.T_ID) AS FieldCount,
                   (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c
                     WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=t.T_ID
                       AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f2 WITH (NOLOCK)
                                        WHERE f2.T_ID=t.T_ID AND LTRIM(RTRIM(f2.F_ID))=c.COLUMN_NAME)) AS UnmanagedCount,
                   (SELECT COUNT(*) FROM dbo.FIELDS f3 WITH (NOLOCK)
                     WHERE f3.T_ID=t.T_ID
                       AND NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c2
                                        WHERE c2.TABLE_SCHEMA='dbo' AND c2.TABLE_NAME=t.T_ID
                                          AND c2.COLUMN_NAME=LTRIM(RTRIM(f3.F_ID)))) AS OrphanCount
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
            if (Identifier.IsMatch(table))
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
            SELECT 1 FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@TableId;
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

        if (original is not null)
        {
            var current = await ReadCurrentTableInputAsync(connection, transaction, tableId, token)
                ?? throw new KeyNotFoundException("数据表不存在。");
            if (!SameTableInput(original, current))
            {
                logger.LogWarning("数据表乐观锁冲突 table={Table} by={UpdatedBy}", tableId, updatedBy);
                throw new ArgumentException("数据表信息已被他人修改，请刷新后重试！", nameof(input));
            }
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
        await transaction.CommitAsync(token);
        logger.LogInformation("更新数据表元数据 table={Table} by={UpdatedBy}", tableId, updatedBy);
    }

    public async Task DeleteTableAsync(string tableId, CancellationToken token)
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
              (SELECT COUNT(*) FROM dbo.LISTREPORT_CONDITION WITH (NOLOCK) WHERE T_ID=@TableId OR T_ID_R=@TableId) AS ListReport,
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
            ("查询条件记忆", refsReader.GetInt32(5) + refsReader.GetInt32(6) + refsReader.GetInt32(7) + refsReader.GetInt32(8)),
            ("报表条件引用", refsReader.GetInt32(9) + refsReader.GetInt32(10)),
        };
        await refsReader.CloseAsync();
        var blocked = counts.Where(item => item.Count > 0).ToList();
        if (blocked.Count > 0)
            throw new ArgumentException(
                $"数据表存在引用，无法删除：{string.Join("；", blocked.Select(item => $"{item.Name} {item.Count} 条"))}。仅删除无引用的表元数据，物理表不受影响。",
                nameof(tableId));

        await using var delete = new SqlCommand("DELETE FROM dbo.TABLES WHERE T_ID=@TableId", connection, transaction);
        delete.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        if (await delete.ExecuteNonQueryAsync(token) != 1)
            throw new KeyNotFoundException("数据表不存在。");
        await transaction.CommitAsync(token);
        logger.LogInformation("删除数据表元数据 table={Table}", tableId);
    }

    public async Task<IReadOnlyList<FieldAdminUnmanagedField>> GetUnmanagedFieldsAsync(string tableId, CancellationToken token)
    {
        EnsureIdentifier(tableId, null);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT c.COLUMN_NAME, c.DATA_TYPE
            FROM INFORMATION_SCHEMA.COLUMNS c
            WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@TableId
              AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WITH (NOLOCK)
                               WHERE f.T_ID=@TableId AND LTRIM(RTRIM(f.F_ID))=c.COLUMN_NAME)
            ORDER BY c.ORDINAL_POSITION;
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
            SELECT 1 FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@TableId;
            """, connection, transaction))
        {
            physical.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = request.TableId;
            if (await physical.ExecuteScalarAsync(token) is null)
                throw new ArgumentException("物理表或视图不存在，无法生成字段元数据。", nameof(request));
        }

        var created = 0;
        var skipped = 0;
        var reasons = new List<string>();
        foreach (var fieldId in requested)
        {
            if (await FieldExistsAsync(connection, transaction, request.TableId, fieldId, token))
            {
                skipped++;
                reasons.Add($"{fieldId}：已存在元数据，跳过");
                continue;
            }
            var column = await ReadPhysicalColumnAsync(connection, transaction, request.TableId, fieldId, token);
            if (column is null)
            {
                skipped++;
                reasons.Add($"{fieldId}：物理列不存在，跳过");
                continue;
            }
            if (!AllowedTypes.Contains(column.Value.Type))
            {
                skipped++;
                reasons.Add($"{fieldId}：物理类型 {column.Value.Type} 不受支持，跳过");
                continue;
            }

            const string sql = """
                INSERT INTO dbo.FIELDS
                    (T_ID,F_ID,F_DESC,F_TYPE,IS_QUERY,IS_DEFAULT_FIELDS,IS_VISIBLE,IS_VIRTUAL,IS_COST,IS_SECRECY,
                     IS_READONLY,CAN_COPY,DISPLAY_LENGTH,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES
                    (@TableId,@FieldId,@Description,@DataType,1,1,1,0,0,0,0,1,100,@UpdatedBy,GETDATE());
                """;
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = request.TableId;
            command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
            command.Parameters.Add("@Description", SqlDbType.NVarChar, 500).Value = column.Value.Description;
            command.Parameters.Add("@DataType", SqlDbType.NVarChar, 100).Value = column.Value.Type;
            command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = updatedBy;
            await command.ExecuteNonQueryAsync(token);
            created++;
        }
        await transaction.CommitAsync(token);
        logger.LogInformation("批量生成字段元数据 table={Table} created={Created} skipped={Skipped} by={UpdatedBy}",
            request.TableId, created, skipped, updatedBy);
        return new(created, skipped, reasons);
    }

    public async Task<FieldAdminPageResult> GetFieldsAsync(
        string tableId,
        string? keyword,
        int page,
        int pageSize,
        CancellationToken token)
    {
        EnsureIdentifier(tableId, null);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var pattern = $"%{keyword?.Trim() ?? ""}%";
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            ;WITH base AS (
                SELECT LTRIM(RTRIM(F_ID)) F_ID,
                       COALESCE(NULLIF(LTRIM(RTRIM(F_DESC)),''),LTRIM(RTRIM(F_ID))) F_DESC,
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
                     CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c
                                        WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@TableId
                                          AND c.COLUMN_NAME=LTRIM(RTRIM(F_ID))) THEN 1 ELSE 0 END END AS bit) AS IS_PHYSICAL,
                   COUNT(*) OVER() AS Total
            FROM base
            WHERE (@Keyword='' OR F_ID LIKE @Pattern OR F_DESC LIKE @Pattern)
            ORDER BY F_ID
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
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
            if (!Identifier.IsMatch(field)) continue;
            items.Add(new(tableId, field, reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6),
                reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9),
                reader.GetBoolean(10), reader.GetBoolean(11)));
        }
        return new(items, total, page, pageSize);
    }

    public async Task<FieldAdminMetadata?> GetMetadataAsync(string tableId, string fieldId, CancellationToken token)
    {
        EnsureIdentifier(tableId, fieldId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(F_DESC)),''),LTRIM(RTRIM(F_ID))),COALESCE(F_TYPE,'nvarchar'),
                   COALESCE(DISPLAY_LENGTH,100),COALESCE(NULLIF(ITEM_ALIGN,''),'left'),COALESCE(NULLIF(HEADER_ALIGN,''),'center'),
                   DISPLAY_FORMAT,CAST(COALESCE(IS_VISIBLE,1) AS bit),CAST(COALESCE(IS_DEFAULT_FIELDS,0) AS bit),
                   CAST(COALESCE(IS_QUERY,1) AS bit),CAST(COALESCE(IS_READONLY,0) AS bit),CAST(COALESCE(IS_VERIFY,0) AS bit),
                   CAST(COALESCE(IS_COST,0) AS bit),CAST(COALESCE(IS_SECRECY,0) AS bit),DFT_VALUE,VERIFY_INDEX,REGEX,F_REMARK,
                   BROWSE_URL,BROWSE_M_IDX,CAST(COALESCE(ONLY_CHOOSE,0) AS bit),CAST(COALESCE(CHOOSE_MULTI,0) AS bit),CHOOSE_PAGE,
                   CAST(COALESCE(CHOOSE_ACTIVE1,0) AS bit),CHOOSE_T_ID1,CHOOSE_T_DESC1,CHOOSE_M_IDX1,CHOOSE_FILTER1,CHOOSE_RETURNVAL1,
                   CAST(COALESCE(CHOOSE_ACTIVE2,0) AS bit),CHOOSE_T_ID2,CHOOSE_T_DESC2,CHOOSE_M_IDX2,CHOOSE_FILTER2,CHOOSE_RETURNVAL2,
                   CAST(COALESCE(CHOOSE_ACTIVE3,0) AS bit),CHOOSE_T_ID3,CHOOSE_T_DESC3,CHOOSE_M_IDX3,CHOOSE_FILTER3,CHOOSE_RETURNVAL3,
                   CAST(COALESCE(CHOOSE_ACTIVE4,0) AS bit),CHOOSE_T_ID4,CHOOSE_T_DESC4,CHOOSE_M_IDX4,CHOOSE_FILTER4,CHOOSE_RETURNVAL4,
                   CAST(COALESCE(IS_VIRTUAL,0) AS bit),VIRTUAL_EXP,CAST(COALESCE(CAN_COPY,1) AS bit),CAST(COALESCE(IS_AUTOINC,0) AS bit),
                   CONVERT_FUNCTION,DATASOURCE_SQL,LAST_UPDATE_BY,LAST_UPDATE_DATE,CAST(COALESCE(IS_PK,0) AS bit)
            FROM dbo.FIELDS WITH (NOLOCK)
            WHERE T_ID=@TableId AND LTRIM(RTRIM(F_ID))=@FieldId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var field = reader.GetString(0).Trim();
        if (!Identifier.IsMatch(field)) return null;
        var input = new FieldAdminInput(
            reader.GetString(1), reader.GetString(2), Math.Clamp(reader.GetInt32(3), 40, 300),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10), reader.GetBoolean(11),
            reader.GetBoolean(12), reader.GetBoolean(13), reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetInt32(15), reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetInt32(19), reader.GetBoolean(20), reader.GetBoolean(21),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            [ReadChooser(reader, 23), ReadChooser(reader, 29), ReadChooser(reader, 35), ReadChooser(reader, 41)],
            reader.GetBoolean(49));
        var isVirtual = reader.GetBoolean(47);
        var virtualExpression = reader.IsDBNull(48) ? null : reader.GetString(48);
        var isAutoIncrement = reader.GetBoolean(50);
        var convertFunction = reader.IsDBNull(51) ? null : reader.GetString(51);
        var dataSourceSql = reader.IsDBNull(52) ? null : reader.GetString(52);
        var lastUpdatedBy = reader.IsDBNull(53) ? null : reader.GetString(53);
        DateTime? lastUpdatedAt = reader.IsDBNull(54) ? null : reader.GetDateTime(54);
        var isPrimaryKey = reader.GetBoolean(55);
        await reader.CloseAsync();
        var physicalType = await GetPhysicalTypeAsync(connection, tableId, field, token);
        return new(tableId, field, input,
            isVirtual, virtualExpression, isAutoIncrement, convertFunction, dataSourceSql,
            lastUpdatedBy, lastUpdatedAt, isPrimaryKey,
            physicalType is not null,
            physicalType,
            physicalType is null ? null : string.Equals(physicalType, input.DataType.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> GetPhysicalTypeAsync(
        SqlConnection connection,
        string tableId,
        string fieldId,
        CancellationToken token)
    {
        const string sql = """
            SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@TableId AND COLUMN_NAME=@FieldId;
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
            SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@TableId AND COLUMN_NAME=@FieldId;
            """, connection, transaction))
        {
            physical.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = request.TableId;
            physical.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = request.FieldId.Trim();
            if (await physical.ExecuteScalarAsync(token) is null)
                throw new ArgumentException(
                    $"物理列 {request.TableId}.{request.FieldId.Trim()} 不存在，新增字段元数据仅允许指向真实物理列；虚拟/派生字段需经受控表达式机制另行处理。",
                    nameof(request));
        }

        const string sql = """
            INSERT INTO dbo.FIELDS
                (T_ID,F_ID,F_DESC,F_TYPE,BROWSE_URL,BROWSE_M_IDX,ONLY_CHOOSE,CHOOSE_PAGE,CHOOSE_MULTI,
                 CHOOSE_T_ID1,CHOOSE_T_DESC1,CHOOSE_M_IDX1,CHOOSE_FILTER1,CHOOSE_RETURNVAL1,CHOOSE_ACTIVE1,
                 CHOOSE_T_ID2,CHOOSE_T_DESC2,CHOOSE_M_IDX2,CHOOSE_FILTER2,CHOOSE_RETURNVAL2,CHOOSE_ACTIVE2,
                 CHOOSE_T_ID3,CHOOSE_T_DESC3,CHOOSE_M_IDX3,CHOOSE_FILTER3,CHOOSE_RETURNVAL3,CHOOSE_ACTIVE3,
                 CHOOSE_T_ID4,CHOOSE_T_DESC4,CHOOSE_M_IDX4,CHOOSE_FILTER4,CHOOSE_RETURNVAL4,CHOOSE_ACTIVE4,
                 REGEX,DISPLAY_LENGTH,DISPLAY_FORMAT,HEADER_ALIGN,ITEM_ALIGN,IS_VERIFY,VERIFY_INDEX,
                 IS_READONLY,IS_VISIBLE,IS_VIRTUAL,IS_AUTOINC,IS_QUERY,IS_COST,IS_SECRECY,DFT_VALUE,
                 CAN_COPY,IS_DEFAULT_FIELDS,F_REMARK,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES
                (@TableId,@FieldId,@Label,@DataType,NULL,@BrowseModuleId,@OnlyChoose,@ChoosePage,@ChooseMultiple,
                 @Table1,@Description1,@Module1,@Filter1,@Return1,@Active1,
                 @Table2,@Description2,@Module2,@Filter2,@Return2,@Active2,
                 @Table3,@Description3,@Module3,@Filter3,@Return3,@Active3,
                 @Table4,@Description4,@Module4,@Filter4,@Return4,@Active4,
                 @Regex,@Width,@Format,@HeaderAlign,@Align,@Required,@VerifyIndex,
                 @Readonly,@Visible,0,0,@Queryable,@Cost,@Secrecy,@DefaultValue,
                 @CanCopy,@Default,@Remark,@UpdatedBy,GETDATE());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        AddIdentity(command, request.TableId, request.FieldId);
        AddInput(command, request.Field, updatedBy);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new InvalidOperationException("新增字段失败。");
        await transaction.CommitAsync(token);
        logger.LogInformation("新增字段 table={Table} field={Field} by={UpdatedBy}", request.TableId, request.FieldId, updatedBy);
    }

    public async Task UpdateAsync(
        string tableId,
        string fieldId,
        FieldAdminInput field,
        FieldAdminInput? original,
        string updatedBy,
        CancellationToken token)
    {
        EnsureIdentifier(tableId, fieldId);
        Validate(field);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        if (original is not null)
        {
            var current = await ReadCurrentInputAsync(connection, transaction, tableId, fieldId, token)
                ?? throw new KeyNotFoundException("字段不存在。");
            if (!SameInput(original, current))
            {
                logger.LogWarning("字段乐观锁冲突 table={Table} field={Field} by={UpdatedBy}", tableId, fieldId, updatedBy);
                throw new ArgumentException("字段内容已被他人修改，请刷新后重试！", nameof(field));
            }
        }

        const string sql = """
            UPDATE dbo.FIELDS SET
                F_DESC=@Label,F_TYPE=@DataType,DISPLAY_LENGTH=@Width,ITEM_ALIGN=@Align,HEADER_ALIGN=@HeaderAlign,
                DISPLAY_FORMAT=@Format,IS_VISIBLE=@Visible,IS_DEFAULT_FIELDS=@Default,IS_QUERY=@Queryable,
                IS_READONLY=@Readonly,IS_VERIFY=@Required,IS_COST=@Cost,IS_SECRECY=@Secrecy,DFT_VALUE=@DefaultValue,
                VERIFY_INDEX=@VerifyIndex,REGEX=@Regex,F_REMARK=@Remark,BROWSE_URL=@BrowseUrl,BROWSE_M_IDX=@BrowseModuleId,
                ONLY_CHOOSE=@OnlyChoose,CHOOSE_MULTI=@ChooseMultiple,CHOOSE_PAGE=@ChoosePage,CAN_COPY=@CanCopy,
                CHOOSE_ACTIVE1=@Active1,CHOOSE_T_ID1=@Table1,CHOOSE_T_DESC1=@Description1,CHOOSE_M_IDX1=@Module1,
                CHOOSE_FILTER1=@Filter1,CHOOSE_RETURNVAL1=@Return1,
                CHOOSE_ACTIVE2=@Active2,CHOOSE_T_ID2=@Table2,CHOOSE_T_DESC2=@Description2,CHOOSE_M_IDX2=@Module2,
                CHOOSE_FILTER2=@Filter2,CHOOSE_RETURNVAL2=@Return2,
                CHOOSE_ACTIVE3=@Active3,CHOOSE_T_ID3=@Table3,CHOOSE_T_DESC3=@Description3,CHOOSE_M_IDX3=@Module3,
                CHOOSE_FILTER3=@Filter3,CHOOSE_RETURNVAL3=@Return3,
                CHOOSE_ACTIVE4=@Active4,CHOOSE_T_ID4=@Table4,CHOOSE_T_DESC4=@Description4,CHOOSE_M_IDX4=@Module4,
                CHOOSE_FILTER4=@Filter4,CHOOSE_RETURNVAL4=@Return4,
                FORM_TAB_NO=@FormTabNo,FORM_ORDER=@FormOrder,FORM_SPAN=@FormSpan,FORM_NEW_LINE=@FormNewLine,
                FORM_CELL_GROUP=@FormCellGroup,FORM_CELL_ROLE=@FormCellRole,FORM_OPTIONS=@FormOptions,
                LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE()
            WHERE T_ID=@TableId AND F_ID=@FieldId;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        AddIdentity(command, tableId, fieldId);
        AddInput(command, field, updatedBy);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new KeyNotFoundException("字段不存在。");
        await transaction.CommitAsync(token);
        logger.LogInformation("更新字段 table={Table} field={Field} by={UpdatedBy}", tableId, fieldId, updatedBy);
    }

    public async Task DeleteAsync(string tableId, string fieldId, CancellationToken token)
    {
        EnsureIdentifier(tableId, fieldId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await using var delete = new SqlCommand("DELETE FROM dbo.FIELDS WHERE T_ID=@TableId AND F_ID=@FieldId", connection, transaction);
        delete.Parameters.Add("@TableId", SqlDbType.VarChar, 100).Value = tableId;
        delete.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
        if (await delete.ExecuteNonQueryAsync(token) != 1)
        {
            await transaction.RollbackAsync(token);
            throw new KeyNotFoundException("字段不存在。");
        }
        const string cleanSql = """
            DELETE FROM dbo.SYSQL_FIELDS WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.SYSQL_DEFAULT WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.SYSQL_CONDITION WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.SYSQL_COND_DFT WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.SYSQD_CONDITION WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.LISTREPORT_CONDITION WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.SYSQQ WHERE F_ID=@TableDotField;
            DELETE FROM dbo.SYSQR_DEFAULT WHERE F_ID=@TableDotField;
            """;
        await using var clean = new SqlCommand(cleanSql, connection, transaction);
        clean.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
        clean.Parameters.Add("@TableId", SqlDbType.VarChar, 100).Value = tableId;
        clean.Parameters.Add("@TableDotField", SqlDbType.NVarChar, 220).Value = $"{tableId}.{fieldId.Trim()}";
        await clean.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        logger.LogInformation("删除字段 table={Table} field={Field}", tableId, fieldId);
    }

    private static FieldAdminChooser ReadChooser(SqlDataReader reader, int offset) => new(
        reader.GetBoolean(offset),
        reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1),
        reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2),
        reader.IsDBNull(offset + 3) ? null : reader.GetInt32(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
        reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5));

    private static async Task<FieldAdminInput?> ReadCurrentInputAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableId,
        string fieldId,
        CancellationToken token)
    {
        const string sql = """
            SELECT COALESCE(NULLIF(LTRIM(RTRIM(F_DESC)),''),LTRIM(RTRIM(F_ID))),COALESCE(F_TYPE,''),COALESCE(DISPLAY_LENGTH,100),
                   NULLIF(LTRIM(RTRIM(ITEM_ALIGN)),''),COALESCE(NULLIF(HEADER_ALIGN,''),'center'),DISPLAY_FORMAT,
                   CAST(COALESCE(IS_VISIBLE,1) AS bit),CAST(COALESCE(IS_DEFAULT_FIELDS,0) AS bit),CAST(COALESCE(IS_QUERY,1) AS bit),
                   CAST(COALESCE(IS_READONLY,0) AS bit),CAST(COALESCE(IS_VERIFY,0) AS bit),CAST(COALESCE(IS_COST,0) AS bit),
                   CAST(COALESCE(IS_SECRECY,0) AS bit),DFT_VALUE,VERIFY_INDEX,REGEX,F_REMARK,BROWSE_URL,BROWSE_M_IDX,
                   CAST(COALESCE(ONLY_CHOOSE,0) AS bit),CAST(COALESCE(CHOOSE_MULTI,0) AS bit),CHOOSE_PAGE,
                   CAST(COALESCE(CAN_COPY,1) AS bit),
                   CAST(COALESCE(CHOOSE_ACTIVE1,0) AS bit),CHOOSE_T_ID1,CHOOSE_T_DESC1,CHOOSE_M_IDX1,CHOOSE_FILTER1,CHOOSE_RETURNVAL1,
                   CAST(COALESCE(CHOOSE_ACTIVE2,0) AS bit),CHOOSE_T_ID2,CHOOSE_T_DESC2,CHOOSE_M_IDX2,CHOOSE_FILTER2,CHOOSE_RETURNVAL2,
                   CAST(COALESCE(CHOOSE_ACTIVE3,0) AS bit),CHOOSE_T_ID3,CHOOSE_T_DESC3,CHOOSE_M_IDX3,CHOOSE_FILTER3,CHOOSE_RETURNVAL3,
                   CAST(COALESCE(CHOOSE_ACTIVE4,0) AS bit),CHOOSE_T_ID4,CHOOSE_T_DESC4,CHOOSE_M_IDX4,CHOOSE_FILTER4,CHOOSE_RETURNVAL4
                   ,CAST(COALESCE(FORM_TAB_NO,1) AS int) AS FORM_TAB_NO,FORM_ORDER,
                   CAST(COALESCE(FORM_SPAN,1) AS int) AS FORM_SPAN,CAST(COALESCE(FORM_NEW_LINE,0) AS bit) AS FORM_NEW_LINE,
                   FORM_CELL_GROUP,CAST(COALESCE(FORM_CELL_ROLE,0) AS int) AS FORM_CELL_ROLE,FORM_OPTIONS
            FROM dbo.FIELDS WITH (NOLOCK)
            WHERE T_ID=@TableId AND LTRIM(RTRIM(F_ID))=@FieldId;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new FieldAdminInput(
            reader.GetString(0), reader.GetString(1), Math.Clamp(reader.GetInt32(2), 40, 300),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10),
            reader.GetBoolean(11), reader.GetBoolean(12), reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetInt32(14), reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetInt32(18), reader.GetBoolean(19), reader.GetBoolean(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            [ReadInputChooser(reader, 23), ReadInputChooser(reader, 29), ReadInputChooser(reader, 35), ReadInputChooser(reader, 41)],
            reader.GetBoolean(22),
            reader.GetInt32(reader.GetOrdinal("FORM_TAB_NO")),
            reader.IsDBNull(reader.GetOrdinal("FORM_ORDER")) ? null : reader.GetInt32(reader.GetOrdinal("FORM_ORDER")),
            reader.GetInt32(reader.GetOrdinal("FORM_SPAN")),
            reader.GetBoolean(reader.GetOrdinal("FORM_NEW_LINE")),
            reader.IsDBNull(reader.GetOrdinal("FORM_CELL_GROUP")) ? null : reader.GetString(reader.GetOrdinal("FORM_CELL_GROUP")),
            reader.GetInt32(reader.GetOrdinal("FORM_CELL_ROLE")),
            reader.IsDBNull(reader.GetOrdinal("FORM_OPTIONS")) ? null : reader.GetString(reader.GetOrdinal("FORM_OPTIONS")));
    }

    private static FieldAdminChooser ReadInputChooser(SqlDataReader reader, int offset) => new(
        reader.GetBoolean(offset),
        reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1),
        reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2),
        reader.IsDBNull(offset + 3) ? null : reader.GetInt32(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
        reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5));

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
        command.Parameters.Add("@FormTabNo", SqlDbType.Int).Value = input.TabNo;
        command.Parameters.Add("@FormOrder", SqlDbType.Int).Value = input.FormOrder ?? (object)DBNull.Value;
        command.Parameters.Add("@FormSpan", SqlDbType.TinyInt).Value = (byte)Math.Clamp(input.Span, 1, 2);
        command.Parameters.Add("@FormNewLine", SqlDbType.Bit).Value = input.NewLine;
        command.Parameters.Add("@FormCellGroup", SqlDbType.NVarChar, 50).Value = DbValue(input.CellGroup);
        command.Parameters.Add("@FormCellRole", SqlDbType.TinyInt).Value = (byte)Math.Clamp(input.CellRole, 0, 2);
        command.Parameters.Add("@FormOptions", SqlDbType.NVarChar, 500).Value = DbValue(input.Options);
        for (var i = 0; i < 4; i++)
        {
            var source = input.Choosers[i];
            var n = i + 1;
            command.Parameters.Add($"@Active{n}", SqlDbType.Bit).Value = source.Active;
            command.Parameters.Add($"@Table{n}", SqlDbType.NVarChar, 300).Value = DbValue(source.Table);
            command.Parameters.Add($"@Description{n}", SqlDbType.NVarChar, 50).Value = DbValue(source.Description);
            command.Parameters.Add($"@Module{n}", SqlDbType.Int).Value = source.ModuleId ?? (object)DBNull.Value;
            command.Parameters.Add($"@Filter{n}", SqlDbType.NVarChar, 1000).Value = DbValue(source.Filter);
            command.Parameters.Add($"@Return{n}", SqlDbType.VarChar, 8000).Value = DbValue(source.ReturnMapping);
        }
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = updatedBy;
    }

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
        && a.Choosers.Zip(b.Choosers).All(pair => SameChooser(pair.First, pair.Second));

    private static bool SameChooser(FieldAdminChooser a, FieldAdminChooser b) =>
        a.Active == b.Active && NullableEquals(a.Table, b.Table) && NullableEquals(a.Description, b.Description)
        && a.ModuleId == b.ModuleId && NullableEquals(a.Filter, b.Filter) && NullableEquals(a.ReturnMapping, b.ReturnMapping);

    private static bool NullableEquals(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) ? string.IsNullOrWhiteSpace(b) : string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void Validate(FieldAdminInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Label) || input.Label.Trim().Length > 300)
            throw new ArgumentException("字段名称不能为空且不能超过 300 个字符。");
        if (!AllowedTypes.Contains(input.DataType)) throw new ArgumentException("字段类型无效。");
        if (input.Width is < 40 or > 300) throw new ArgumentException("显示宽度必须在 40-300 之间。");
        if (!AllowedAlign.Contains(input.Align ?? "")) throw new ArgumentException("对齐方式无效。");
        if (!AllowedAlign.Contains(input.HeaderAlign)) throw new ArgumentException("列标题对齐方式无效。");
        if ((input.Format?.Length ?? 0) > 50) throw new ArgumentException("显示格式过长。");
        if ((input.DefaultValue?.Length ?? 0) > 200) throw new ArgumentException("默认值过长。");
        if ((input.Regex?.Length ?? 0) > 300) throw new ArgumentException("正则表达式过长。");
        if ((input.Remark?.Length ?? 0) > 500) throw new ArgumentException("备注过长。");
        if (input.VerifyIndex is < 0 or > 9999) throw new ArgumentException("校验顺序无效。");
        if ((input.BrowseUrl?.Length ?? 0) > 1000) throw new ArgumentException("查看详情 URL 过长。");
        if (!string.IsNullOrWhiteSpace(input.BrowseUrl) &&
            (!Uri.TryCreate(input.BrowseUrl, UriKind.Relative, out _) || input.BrowseUrl.TrimStart().StartsWith("//")))
            throw new ArgumentException("查看详情 URL 仅允许站内相对路径。");
        if ((input.ChoosePage?.Length ?? 0) > 500) throw new ArgumentException("数据选择页面过长。");
        if (input.Choosers.Count != 4)
            throw new ArgumentException("数据选择源必须为 4 组。");
        if (input.Choosers.Any(item => (item.Table?.Length ?? 0) > 300 || (item.Description?.Length ?? 0) > 50
            || (item.Filter?.Length ?? 0) > 1000 || (item.ReturnMapping?.Length ?? 0) > 8000))
            throw new ArgumentException("数据选择源配置无效。");
    }

    private static void EnsureIdentifier(string tableId, string? fieldId)
    {
        if (!Identifier.IsMatch(tableId)) throw new ArgumentException("数据表名无效。");
        if (fieldId is not null && !Identifier.IsMatch(fieldId)) throw new ArgumentException("字段名无效。");
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

    private static async Task<bool> FieldExistsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableId,
        string fieldId,
        CancellationToken token)
    {
        const string sql = "SELECT 1 FROM dbo.FIELDS WITH (NOLOCK) WHERE T_ID=@TableId AND LTRIM(RTRIM(F_ID))=@FieldId;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<(string Description, string Type)?> ReadPhysicalColumnAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableId,
        string fieldId,
        CancellationToken token)
    {
        const string sql = """
            SELECT COALESCE(CONVERT(nvarchar(500), ep.value), c.COLUMN_NAME) AS F_DESC, c.DATA_TYPE
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN sys.extended_properties ep
              ON ep.class=1 AND ep.major_id=OBJECT_ID('dbo.' + QUOTENAME(@TableId))
             AND ep.minor_id=c.ORDINAL_POSITION AND ep.name='MS_Description'
            WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@TableId AND c.COLUMN_NAME=@FieldId;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return (reader.GetString(0), reader.GetString(1));
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private SqlConnection CreateConnection() => connections.Create();
}
