using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed class FieldAdminRepository(IConfiguration configuration)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvarchar", "varchar", "nchar", "char", "int", "bigint", "smallint", "tinyint", "decimal", "numeric",
        "float", "real", "money", "smallmoney", "date", "datetime", "datetime2", "smalldatetime", "time", "bit",
        "IDCard", "URL", "Email", "PhoneNo", "ZipCode"
    };
    private static readonly HashSet<string> AllowedAlign = new(StringComparer.OrdinalIgnoreCase)
    {
        "left", "center", "right"
    };

    public async Task<IReadOnlyList<FieldAdminTable>> GetTablesAsync(CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(T_ID)),LTRIM(RTRIM(T_DESC)),LTRIM(RTRIM(ISNULL(T_KIND,''))),LTRIM(RTRIM(ISNULL(T_TYPE,'')))
            FROM dbo.TABLES WITH (NOLOCK)
            ORDER BY T_DESC,T_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<FieldAdminTable>();
        while (await reader.ReadAsync(token))
        {
            var table = reader.GetString(0);
            if (Identifier.IsMatch(table)) result.Add(new(table, reader.GetString(1), NullIfEmpty(reader.GetString(2)), NullIfEmpty(reader.GetString(3))));
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
                       CAST(COALESCE(IS_SECRECY,0) AS bit) IS_SECRECY
                FROM dbo.FIELDS WITH (NOLOCK)
                WHERE T_ID=@TableId
            )
            SELECT F_ID,F_DESC,F_TYPE,IS_VIRTUAL,IS_VISIBLE,IS_DEFAULT_FIELDS,IS_QUERY,IS_READONLY,IS_COST,IS_SECRECY,
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
            if (total == 0) total = Convert.ToInt32(reader.GetValue(10));
            var field = reader.GetString(0).Trim();
            if (!Identifier.IsMatch(field)) continue;
            items.Add(new(tableId, field, reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6),
                reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9)));
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
                   CONVERT_FUNCTION,DATASOURCE_SQL,LAST_UPDATE_BY,LAST_UPDATE_DATE
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
        return new(tableId, field, new FieldAdminInput(
            reader.GetString(1), reader.GetString(2), Math.Clamp(reader.GetInt32(3), 40, 300),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10), reader.GetBoolean(11),
            reader.GetBoolean(12), reader.GetBoolean(13), reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetInt32(15), reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetInt32(19), reader.GetBoolean(20), reader.GetBoolean(21),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            [ReadChooser(reader, 23), ReadChooser(reader, 29), ReadChooser(reader, 35), ReadChooser(reader, 41)],
            reader.GetBoolean(49)),
            reader.GetBoolean(47), reader.IsDBNull(48) ? null : reader.GetString(48), reader.GetBoolean(50),
            reader.IsDBNull(51) ? null : reader.GetString(51), reader.IsDBNull(52) ? null : reader.GetString(52),
            reader.IsDBNull(53) ? null : reader.GetString(53), reader.IsDBNull(54) ? null : reader.GetDateTime(54));
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
                throw new ArgumentException("字段内容已被他人修改，请刷新后重试！", nameof(field));
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
                LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE()
            WHERE T_ID=@TableId AND F_ID=@FieldId;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        AddIdentity(command, tableId, fieldId);
        AddInput(command, field, updatedBy);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new KeyNotFoundException("字段不存在。");
        await transaction.CommitAsync(token);
    }

    public async Task DeleteAsync(string tableId, string fieldId, CancellationToken token)
    {
        EnsureIdentifier(tableId, fieldId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await using var delete = new SqlCommand("DELETE FROM dbo.FIELDS WHERE T_ID=@TableId AND F_ID=@FieldId", connection, transaction);
        delete.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        delete.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
        if (await delete.ExecuteNonQueryAsync(token) != 1)
        {
            await transaction.RollbackAsync(token);
            throw new KeyNotFoundException("字段不存在。");
        }
        const string cleanSql = """
            DELETE FROM dbo.SYSQL_FIELDS WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            DELETE FROM dbo.SYSQL_DEFAULT WHERE F_ID=@FieldId AND (T_ID=@TableId OR T_ID_R=@TableId);
            """;
        await using var clean = new SqlCommand(cleanSql, connection, transaction);
        clean.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
        clean.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        await clean.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
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
                   COALESCE(NULLIF(ITEM_ALIGN,''),'left'),COALESCE(NULLIF(HEADER_ALIGN,''),'center'),DISPLAY_FORMAT,
                   CAST(COALESCE(IS_VISIBLE,1) AS bit),CAST(COALESCE(IS_DEFAULT_FIELDS,0) AS bit),CAST(COALESCE(IS_QUERY,1) AS bit),
                   CAST(COALESCE(IS_READONLY,0) AS bit),CAST(COALESCE(IS_VERIFY,0) AS bit),CAST(COALESCE(IS_COST,0) AS bit),
                   CAST(COALESCE(IS_SECRECY,0) AS bit),DFT_VALUE,VERIFY_INDEX,REGEX,F_REMARK,BROWSE_URL,BROWSE_M_IDX,
                   CAST(COALESCE(ONLY_CHOOSE,0) AS bit),CAST(COALESCE(CHOOSE_MULTI,0) AS bit),CHOOSE_PAGE,
                   CAST(COALESCE(CAN_COPY,1) AS bit),
                   CAST(COALESCE(CHOOSE_ACTIVE1,0) AS bit),CHOOSE_T_ID1,CHOOSE_T_DESC1,CHOOSE_M_IDX1,CHOOSE_FILTER1,CHOOSE_RETURNVAL1,
                   CAST(COALESCE(CHOOSE_ACTIVE2,0) AS bit),CHOOSE_T_ID2,CHOOSE_T_DESC2,CHOOSE_M_IDX2,CHOOSE_FILTER2,CHOOSE_RETURNVAL2,
                   CAST(COALESCE(CHOOSE_ACTIVE3,0) AS bit),CHOOSE_T_ID3,CHOOSE_T_DESC3,CHOOSE_M_IDX3,CHOOSE_FILTER3,CHOOSE_RETURNVAL3,
                   CAST(COALESCE(CHOOSE_ACTIVE4,0) AS bit),CHOOSE_T_ID4,CHOOSE_T_DESC4,CHOOSE_M_IDX4,CHOOSE_FILTER4,CHOOSE_RETURNVAL4
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
            reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10),
            reader.GetBoolean(11), reader.GetBoolean(12), reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetInt32(14), reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetInt32(18), reader.GetBoolean(19), reader.GetBoolean(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            [ReadInputChooser(reader, 23), ReadInputChooser(reader, 29), ReadInputChooser(reader, 35), ReadInputChooser(reader, 41)],
            reader.GetBoolean(22));
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
        command.Parameters.Add("@Align", SqlDbType.NVarChar, 50).Value = input.Align;
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
        && a.Align.Equals(b.Align, StringComparison.OrdinalIgnoreCase)
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
        if (!AllowedAlign.Contains(input.Align)) throw new ArgumentException("对齐方式无效。");
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

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private SqlConnection CreateConnection() => new(configuration.GetConnectionString("ErpDatabase")
        ?? throw new InvalidOperationException("ConnectionStrings:ErpDatabase 未配置。"));
}
