using System.Data;
using EOS.API.Data;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

/// <summary>
/// 单行参数表设置接口（旧系统 MagSysSet.aspx / TxtSetup.aspx 语义）。
/// GET 返回参数值与字段元数据（FIELDS 标签 + INFORMATION_SCHEMA 类型/长度），
/// PUT 在事务内 upsert 第一行，并对 SYSSS 复刻旧页副作用：关闭「快查全部字段」时删除
/// SYSQQ 中 F_ID='all' 的快查条件。权限对齐旧页 DxAuthentication：
/// 浏览要求 EXEC_TAG<>A，保存要求 EDIT_TAG（个人/组权限经 LegacyRightsRepository 聚合）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/settings")]
public sealed class SettingsController(
    DbConnectionFactory connections,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private static readonly Dictionary<string, int> SettingTables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SYSSS"] = 110111,
        ["HR_SETUP"] = 180213,
        ["HRM_SETUP"] = 180662,
    };

    [HttpGet("{table}")]
    public async Task<IActionResult> GetSettings(string table, CancellationToken token)
    {
        if (!SettingTables.TryGetValue(table, out var moduleId)) return NotFound();
        if (!await CanBrowseAsync(moduleId, token)) return Forbid();

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var fields = await GetFieldDefinitionsAsync(connection, table, token);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand($"SELECT TOP 1 * FROM dbo.[{table}] WITH (NOLOCK);", connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (await reader.ReadAsync(token))
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
        }
        return Ok(new { values, fields });
    }

    [HttpPut("{table}")]
    public async Task<IActionResult> UpdateSettings(string table, [FromBody] Dictionary<string, string?> values, CancellationToken token)
    {
        if (!SettingTables.TryGetValue(table, out var moduleId)) return NotFound();
        if (!await CanEditAsync(moduleId, token)) return Forbid();
        if (values.Count > 100) return BadRequest(new { code = "TOO_MANY_FIELDS", message = "参数数量超出限制。" });

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var columns = await GetColumnsAsync(connection, table, token);
        var entries = values
            .Where(entry => columns.TryGetValue(entry.Key, out _))
            .ToList();
        if (entries.Count == 0) return BadRequest(new { code = "NO_VALID_FIELDS", message = "没有可更新的参数。" });

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
            return NoContent();
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private async Task<bool> CanBrowseAsync(int moduleId, CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, moduleId, token)).CanBrowse;

    private async Task<bool> CanEditAsync(int moduleId, CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, moduleId, token)).CanEdit;

    /// <summary>SYSSS 保存时若「使用快查全部字段」未勾选，删除全字段快查条件（旧 MagSysSet.aspx 副作用）。</summary>
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
            SELECT c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.IS_NULLABLE, f.F_DESC, c.ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN dbo.FIELDS f WITH (NOLOCK) ON f.T_ID=@Table AND f.F_ID=c.COLUMN_NAME
            WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@Table
            ORDER BY c.ORDINAL_POSITION;
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
            SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@Table;
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
