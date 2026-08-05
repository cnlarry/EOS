using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed class AdminFieldRepository(DbConnectionFactory connections, ILogger<AdminFieldRepository> logger)
{
    private static readonly HashSet<string> AllowedTables = new(StringComparer.OrdinalIgnoreCase)
        { "BOM_STRU_M", "BOM_STRU_D" };

    public async Task<IReadOnlyList<AdminFieldDefinition>> GetFieldsAsync(
        string tableId,
        CancellationToken cancellationToken)
    {
        EnsureTable(tableId);
        const string sql = """
            SELECT T_ID,F_ID,F_DESC,IS_VISIBLE,IS_VIRTUAL,VIRTUAL_EXP,IS_COST,IS_SECRECY,
                   F_TYPE,DISPLAY_LENGTH,DISPLAY_FORMAT,HEADER_ALIGN,ITEM_ALIGN
            FROM dbo.FIELDS
            WHERE T_ID=@TableId
            ORDER BY F_DESC,F_ID;
            """;
        await using var connection = CreateConnection();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AdminFieldDefinition>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new AdminFieldDefinition(
                reader.GetString("T_ID").Trim(), reader.GetString("F_ID").Trim(),
                reader.GetString("F_DESC").Trim(), reader.GetNullableBoolean("IS_VISIBLE"),
                reader.GetNullableBoolean("IS_VIRTUAL"), reader.GetNullableString("VIRTUAL_EXP"),
                reader.GetNullableBoolean("IS_COST"), reader.GetNullableBoolean("IS_SECRECY"),
                reader.GetNullableString("F_TYPE"), reader.GetNullableInt32("DISPLAY_LENGTH"),
                reader.GetNullableString("DISPLAY_FORMAT"), reader.GetNullableString("HEADER_ALIGN"),
                reader.GetNullableString("ITEM_ALIGN")));
        return result;
    }

    public async Task UpdateAsync(
        AdminFieldUpdateRequest request,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        EnsureTable(request.TableId);
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 300)
            throw new ArgumentException("字段名称不能为空且不能超过 300 个字符。", nameof(request));
        const string sql = """
            UPDATE dbo.FIELDS SET
                F_DESC=@Description, IS_VISIBLE=@Visible, IS_VIRTUAL=@Virtual,
                VIRTUAL_EXP=@VirtualExpression, IS_COST=@Cost, IS_SECRECY=@Secrecy,
                F_TYPE=@DataType, DISPLAY_LENGTH=@DisplayLength, DISPLAY_FORMAT=@DisplayFormat,
                HEADER_ALIGN=@HeaderAlign, ITEM_ALIGN=@ItemAlign,
                LAST_UPDATE_BY=@UpdatedBy, LAST_UPDATE_DATE=GETDATE()
            WHERE T_ID=@TableId AND F_ID=@FieldId;
            """;
        await using var connection = CreateConnection();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = request.TableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = request.FieldId;
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 300).Value = request.Description.Trim();
        command.Parameters.Add("@Visible", SqlDbType.Bit).Value = request.Visible;
        command.Parameters.Add("@Virtual", SqlDbType.Bit).Value = request.Virtual;
        command.Parameters.Add("@VirtualExpression", SqlDbType.NVarChar, 500).Value = DbValue(request.VirtualExpression);
        command.Parameters.Add("@Cost", SqlDbType.Bit).Value = request.Cost;
        command.Parameters.Add("@Secrecy", SqlDbType.Bit).Value = request.Secrecy;
        command.Parameters.Add("@DataType", SqlDbType.NVarChar, 100).Value = DbValue(request.DataType);
        command.Parameters.Add("@DisplayLength", SqlDbType.Int).Value = request.DisplayLength ?? (object)DBNull.Value;
        command.Parameters.Add("@DisplayFormat", SqlDbType.NVarChar, 50).Value = DbValue(request.DisplayFormat);
        command.Parameters.Add("@HeaderAlign", SqlDbType.NVarChar, 50).Value = DbValue(request.HeaderAlign);
        command.Parameters.Add("@ItemAlign", SqlDbType.VarChar, 50).Value = DbValue(request.ItemAlign);
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = updatedBy;
        await connection.OpenAsync(cancellationToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new KeyNotFoundException("指定字段不存在。" );
        logger.LogInformation("更新 BOM 字段 table={Table} field={Field} by={UpdatedBy}", request.TableId, request.FieldId, updatedBy);
    }

    public async Task SaveDefaultsAsync(DefaultColumnRequest request, CancellationToken cancellationToken)
    {
        EnsureTable(request.ModuleTable);
        EnsureTable(request.RelatedTable);
        if (!request.ModuleTable.Equals("BOM_STRU_M", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("模块主表必须是 BOM_STRU_M。", nameof(request));
        var fields = request.FieldIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = new SqlCommand("DELETE dbo.SYSQL_DEFAULT WHERE T_ID=@ModuleTable AND T_ID_R=@RelatedTable", connection, transaction))
        {
            AddDefaultIdentity(delete, request);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        for (var i = 0; i < fields.Length; i++)
        {
            const string insertSql = """
                INSERT dbo.SYSQL_DEFAULT(T_ID,T_ID_R,F_ID,F_IDX)
                SELECT @ModuleTable,@RelatedTable,@FieldId,@Position
                WHERE EXISTS(SELECT 1 FROM dbo.FIELDS WHERE T_ID=@RelatedTable AND F_ID=@FieldId);
                """;
            await using var insert = new SqlCommand(insertSql, connection, transaction);
            AddDefaultIdentity(insert, request);
            insert.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fields[i];
            insert.Parameters.Add("@Position", SqlDbType.Int).Value = i + 1;
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new ArgumentException($"字段不存在：{fields[i]}", nameof(request));
        }
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("保存 BOM 默认列 moduleTable={ModuleTable} relatedTable={RelatedTable} fields={FieldCount}",
            request.ModuleTable, request.RelatedTable, fields.Length);
    }

    private static void AddDefaultIdentity(SqlCommand command, DefaultColumnRequest request)
    {
        command.Parameters.Add("@ModuleTable", SqlDbType.VarChar, 50).Value = request.ModuleTable;
        command.Parameters.Add("@RelatedTable", SqlDbType.VarChar, 50).Value = request.RelatedTable;
    }
    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static void EnsureTable(string tableId)
    {
        if (!AllowedTables.Contains(tableId)) throw new ArgumentException("不允许维护指定数据表。", nameof(tableId));
    }
    private SqlConnection CreateConnection() => connections.Create();
}
