using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed class FieldConfigurationRepository(IConfiguration configuration)
{
    private const string MasterTable = "BOM_STRU_M";
    private const string DetailTable = "BOM_STRU_D";

    public async Task<FieldConfigurationResult> GetAsync(
        string userId,
        string scope,
        bool allowCost,
        bool allowSecrecy,
        IReadOnlySet<string> deniedFields,
        CancellationToken cancellationToken)
    {
        var relatedTable = GetRelatedTable(scope);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var usesUserConfiguration = await HasUserConfigurationAsync(
            connection, userId, relatedTable, cancellationToken);
        var fields = (await ReadFieldsAsync(
            connection, userId, relatedTable, usesUserConfiguration, cancellationToken))
            .Where(field => (allowCost || !field.IsCost)
                && (allowSecrecy || !field.IsSecrecy)
                && !deniedFields.Contains(field.SourceField))
            .ToArray();

        return new FieldConfigurationResult(
            1204,
            scope,
            userId,
            usesUserConfiguration,
            fields.Where(field => !field.Visible).ToArray(),
            fields.Where(field => field.Visible).OrderBy(field => field.Position).ToArray());
    }

    public async Task<IReadOnlyList<FieldDefinition>> GetSelectedAsync(
        string userId,
        string scope,
        bool allowCost,
        bool allowSecrecy,
        IReadOnlySet<string> deniedFields,
        CancellationToken cancellationToken)
    {
        var result = await GetAsync(
            userId, scope, allowCost, allowSecrecy, deniedFields, cancellationToken);
        return result.SelectedFields;
    }

    public async Task SaveAsync(
        string userId,
        string scope,
        IReadOnlyList<string> fieldIds,
        bool allowCost,
        bool allowSecrecy,
        IReadOnlySet<string> deniedFields,
        CancellationToken cancellationToken)
    {
        var relatedTable = GetRelatedTable(scope);
        var normalized = fieldIds
            .Where(fieldId => !string.IsNullOrWhiteSpace(fieldId))
            .Select(ParseFieldId)
            .Distinct()
            .ToArray();
        var allowed = await GetAsync(
            userId, scope, allowCost, allowSecrecy, deniedFields, cancellationToken);
        var allowedIds = allowed.AvailableFields.Concat(allowed.SelectedFields)
            .Select(field => field.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalized.Any(field => !allowedIds.Contains($"{field.SourceTable}^{field.SourceField}")))
            throw new ArgumentException("字段列表包含当前用户无权查看的字段。", nameof(fieldIds));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        const string deleteSql = """
            DELETE FROM dbo.SYSQL_FIELDS
            WHERE USER_ID = @UserId
              AND T_ID = @MasterTable
              AND T_ID_R = @RelatedTable;
            """;
        await using (var delete = new SqlCommand(deleteSql, connection, transaction))
        {
            AddIdentityParameters(delete, userId, relatedTable);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertSql = """
            INSERT INTO dbo.SYSQL_FIELDS (USER_ID, T_ID, T_ID_R, F_ID, F_IDX)
            SELECT @UserId, @MasterTable, @RelatedTable, @FieldId, @Position
            WHERE EXISTS (
                SELECT 1
                FROM dbo.FIELDS
                WHERE T_ID = @SourceTable
                  AND F_ID = @FieldId
                  AND ISNULL(IS_VISIBLE, 0) = 1
            );
            """;
        for (var index = 0; index < normalized.Length; index++)
        {
            await using var insert = new SqlCommand(insertSql, connection, transaction);
            AddIdentityParameters(insert, userId, relatedTable);
            insert.Parameters.Add("@SourceTable", SqlDbType.NVarChar, 100).Value = normalized[index].SourceTable;
            insert.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = normalized[index].SourceField;
            insert.Parameters.Add("@Position", SqlDbType.Int).Value = index + 1;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RestoreDefaultAsync(
        string userId,
        string scope,
        CancellationToken cancellationToken)
    {
        var relatedTable = GetRelatedTable(scope);
        const string sql = """
            DELETE FROM dbo.SYSQL_FIELDS
            WHERE USER_ID = @UserId
              AND T_ID = @MasterTable
              AND T_ID_R = @RelatedTable;
            """;
        await using var connection = CreateConnection();
        await using var command = new SqlCommand(sql, connection);
        AddIdentityParameters(command, userId, relatedTable);
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasUserConfigurationAsync(
        SqlConnection connection,
        string userId,
        string relatedTable,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM dbo.SYSQL_FIELDS
            WHERE USER_ID = @UserId
              AND T_ID = @MasterTable
              AND T_ID_R = @RelatedTable;
            """;
        await using var command = new SqlCommand(sql, connection);
        AddIdentityParameters(command, userId, relatedTable);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<IReadOnlyList<FieldDefinition>> ReadFieldsAsync(
        SqlConnection connection,
        string userId,
        string relatedTable,
        bool usesUserConfiguration,
        CancellationToken cancellationToken)
    {
        var configurationTable = usesUserConfiguration ? "dbo.SYSQL_FIELDS" : "dbo.SYSQL_DEFAULT";
        var userPredicate = usesUserConfiguration ? "AND c.USER_ID = @UserId" : string.Empty;
        var sql = $"""
            SELECT f.T_ID,
                   f.F_ID,
                   f.F_DESC,
                   f.F_TYPE,
                   ISNULL(f.IS_VIRTUAL, 0) AS IS_VIRTUAL,
                   f.VIRTUAL_EXP,
                   f.DISPLAY_LENGTH,
                   f.DISPLAY_FORMAT,
                   f.HEADER_ALIGN,
                   f.ITEM_ALIGN,
                   ISNULL(f.IS_COST, 0) AS IS_COST,
                   ISNULL(f.IS_SECRECY, 0) AS IS_SECRECY,
                   c.F_IDX
            FROM dbo.FIELDS AS f
            LEFT JOIN {configurationTable} AS c
              ON c.T_ID = @MasterTable
             AND c.T_ID_R = @RelatedTable
             AND c.F_ID = f.F_ID
             {userPredicate}
            WHERE f.T_ID = @RelatedTable
              AND ISNULL(f.IS_VISIBLE, 0) = 1
            ORDER BY CASE WHEN c.F_IDX IS NULL THEN 1 ELSE 0 END,
                     c.F_IDX,
                     f.F_DESC,
                     f.F_ID;
            """;

        await using var command = new SqlCommand(sql, connection);
        AddIdentityParameters(command, userId, relatedTable);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var fields = new List<FieldDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString("T_ID").Trim();
            var field = reader.GetString("F_ID").Trim();
            var positionOrdinal = reader.GetOrdinal("F_IDX");
            fields.Add(new FieldDefinition(
                $"{table}^{field}",
                table,
                field,
                reader.GetString("F_DESC").Trim(),
                reader.GetString("F_TYPE").Trim(),
                reader.GetBoolean("IS_VIRTUAL"),
                reader.GetNullableString("VIRTUAL_EXP"),
                !reader.IsDBNull(positionOrdinal),
                reader.IsDBNull(positionOrdinal) ? int.MaxValue : reader.GetInt32(positionOrdinal),
                reader.GetNullableInt32("DISPLAY_LENGTH"),
                reader.GetNullableString("DISPLAY_FORMAT"),
                reader.GetNullableString("HEADER_ALIGN"),
                reader.GetNullableString("ITEM_ALIGN"),
                reader.GetBoolean("IS_COST"),
                reader.GetBoolean("IS_SECRECY")));
        }
        return fields;
    }

    private static (string SourceTable, string SourceField) ParseFieldId(string fieldId)
    {
        var parts = fieldId.Split('^', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0] is not (MasterTable or DetailTable) || parts[1].Length == 0)
        {
            throw new ArgumentException($"字段标识无效：{fieldId}");
        }
        return (parts[0], parts[1]);
    }

    private static string GetRelatedTable(string scope) => scope switch
    {
        "master" => MasterTable,
        "detail" => DetailTable,
        _ => throw new ArgumentException("字段范围必须是 master 或 detail。", nameof(scope))
    };

    private static void AddIdentityParameters(
        SqlCommand command,
        string userId,
        string relatedTable)
    {
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId;
        command.Parameters.Add("@MasterTable", SqlDbType.VarChar, 100).Value = MasterTable;
        command.Parameters.Add("@RelatedTable", SqlDbType.VarChar, 100).Value = relatedTable;
    }

    private SqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("ErpDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:ErpDatabase 未配置。");
        }
        return new SqlConnection(connectionString);
    }
}

internal static class FieldSqlDataReaderExtensions
{
    public static string? GetNullableString(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal).Trim();
    }

    public static int? GetNullableInt32(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }
}
