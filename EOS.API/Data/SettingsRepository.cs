using System.Data;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Single-row parameter table store (SYSSS / HR_SETUP / HRM_SETUP).
/// Reads column metadata plus the single settings row, and upserts the first row inside a
/// transaction. SYSSS saves carry a side effect: when the "quick search all fields"
/// option is disabled, the all-fields quick-search condition is removed. Table names come
/// from the registered map and values are parameterized.
/// </summary>
public sealed class SettingsRepository(DbConnectionFactory connections)
{
    private static readonly Dictionary<string, int> SettingTables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SYSSS"] = ModuleIds.SystemSettings,
        ["HR_SETUP"] = ModuleIds.HrSetup,
        ["HRM_SETUP"] = ModuleIds.HrmSetup,
    };

    /// <summary>Returns the module used as the permission gate for a table, if registered.</summary>
    public static int? PermissionModuleId(string table) =>
        SettingTables.TryGetValue(table, out var moduleId) ? moduleId : null;

    /// <summary>Reads field metadata and the single settings row (empty values when no row exists).</summary>
    public async Task<(IReadOnlyList<object> Fields, IReadOnlyDictionary<string, object?> Values)> GetSettingsAsync(
        string table, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var fields = await GetFieldDefinitionsAsync(connection, table, token);
        await using var command = new SqlCommand($"SELECT TOP 1 * FROM dbo.[{table}] WITH (NOLOCK);", connection);
        var rows = await WorkbenchSql.ReadRowsAsync(command, token);
        var values = rows.Count > 0
            ? rows[0]
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        return (fields, values);
    }

    /// <summary>
    /// Upserts the first row with the submitted values (only registered columns) and applies
    /// table side effects. Returns the count of valid entries; callers reject an empty result.
    /// </summary>
    public async Task<int> UpdateSettingsAsync(
        string table, IReadOnlyDictionary<string, string?> values, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var columns = await GetColumnsAsync(connection, table, token);
        var entries = values
            .Where(entry => columns.TryGetValue(entry.Key, out _))
            .ToList();
        if (entries.Count == 0) return 0;

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var exists = await RowExistsAsync(connection, transaction, table, token);
            var names = new List<string>();
            var sets = new List<string>();
            var parameters = new List<string>();
            await using var command = new SqlCommand { Connection = connection, Transaction = transaction };
            foreach (var (key, raw) in entries)
            {
                var parameter = $"@s{command.Parameters.Count}";
                names.Add($"[{key}]");
                sets.Add($"[{key}]={parameter}");
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, Normalize(columns[key], raw));
            }
            command.CommandText = exists
                ? $"UPDATE TOP (1) dbo.[{table}] SET {string.Join(',', sets)};"
                : $"INSERT INTO dbo.[{table}] ({string.Join(',', names)}) VALUES ({string.Join(',', parameters)});";
            await command.ExecuteNonQueryAsync(token);

            await ApplyTableSideEffectsAsync(connection, transaction, table, values, token);
            await transaction.CommitAsync(token);
            return entries.Count;
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>
    /// When the all-fields quick-search option is disabled, removes the stored
    /// all-fields quick-search condition so it no longer applies.
    /// </summary>
    private static async Task ApplyTableSideEffectsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken token)
    {
        if (!string.Equals(table, "SYSSS", StringComparison.OrdinalIgnoreCase)) return;
        if (!values.TryGetValue("QUICK_SEARCH_ALL", out var raw) || IsTruthy(raw)) return;
        await using var command = new SqlCommand("DELETE FROM dbo.SYSQQ WHERE F_ID=@FieldId;", connection, transaction);
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = "all";
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<bool> RowExistsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        CancellationToken token)
    {
        await using var command = new SqlCommand($"SELECT TOP 1 1 FROM dbo.[{table}] WITH (UPDLOCK, HOLDLOCK);", connection, transaction);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<List<object>> GetFieldDefinitionsAsync(SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT c.name AS COLUMN_NAME, TYPE_NAME(c.user_type_id) AS DATA_TYPE,
                   CASE WHEN c.system_type_id IN (167,175) AND c.max_length=-1 THEN NULL
                        WHEN c.system_type_id IN (167,175) THEN c.max_length
                        WHEN c.system_type_id IN (231,239) AND c.max_length=-1 THEN NULL
                        WHEN c.system_type_id IN (231,239) THEN c.max_length/2
                        ELSE NULL END AS CHARACTER_MAXIMUM_LENGTH,
                   CASE WHEN c.is_nullable=1 THEN 'YES' ELSE 'NO' END AS IS_NULLABLE,
                   f.F_DESC, c.column_id AS ORDINAL_POSITION
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            LEFT JOIN dbo.FIELDS f WITH (NOLOCK) ON f.T_ID=@Table AND f.F_ID=c.name
            WHERE s.name=N'dbo' AND o.name=@Table
            ORDER BY c.column_id;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var fields = new List<object>();
        while (await reader.ReadAsync(token))
        {
            fields.Add(new
            {
                key = reader.GetString(0),
                type = reader.GetString(1),
                maxLength = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
                nullable = string.Equals(reader.GetString(3), "YES", StringComparison.OrdinalIgnoreCase),
                label = reader.IsDBNull(4) ? null : reader.GetString(4),
                order = reader.GetInt32(5),
            });
        }
        return fields;
    }

    private static async Task<Dictionary<string, string>> GetColumnsAsync(SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT c.name AS COLUMN_NAME, TYPE_NAME(c.user_type_id) AS DATA_TYPE
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@Table;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token)) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    private static bool IsTruthy(string? raw) =>
        raw is not null
        && (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1" || raw == "是");

    private static object Normalize(string dataType, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DBNull.Value;
        var type = dataType.ToLowerInvariant();
        try
        {
            if (type.Contains("bit", StringComparison.Ordinal))
                return IsTruthy(raw);
            if (type.Contains("int", StringComparison.Ordinal))
                return int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (type.Contains("float", StringComparison.Ordinal) || type.Contains("real", StringComparison.Ordinal)
               || type.Contains("decimal", StringComparison.Ordinal) || type.Contains("numeric", StringComparison.Ordinal))
                return decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (type.Contains("datetime", StringComparison.Ordinal) || type.Contains("date", StringComparison.Ordinal))
                return DateTime.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            return raw;
        }
        catch
        {
            return raw;
        }
    }
}
