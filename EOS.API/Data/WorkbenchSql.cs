using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// Workbench 共享 SQL 原语：
/// 查询/命令/审批组件与仓储共用的底层数据访问工具，从 DocumentWorkbenchRepository 迁出，
/// 避免仓储继续充当工具箱。只包含确定性原语（标识符白名单、元数据读取、行读写、范围判定），
/// 不含业务逻辑；动态标识符全部来自服务端白名单，值参数化。
/// </summary>
internal static class WorkbenchSql
{
    internal static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    internal static async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(SqlConnection connection, SqlTransaction? transaction, string table, CancellationToken token)
    {
        const string sql = """
            SELECT c.name AS COLUMN_NAME
            FROM sys.indexes i
            JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            JOIN sys.tables t ON i.object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = N'dbo' AND t.name = @Table AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<string>();
        while (await reader.ReadAsync(token))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    internal static async Task<IReadOnlyList<string>> GetIdentityColumnsAsync(SqlConnection connection, SqlTransaction transaction, string table, CancellationToken token)
    {
        const string sql = "SELECT c.name FROM sys.tables t INNER JOIN sys.columns c ON c.object_id=t.object_id WHERE t.name=@Table AND t.schema_id=SCHEMA_ID('dbo') AND c.is_identity=1;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<string>();
        while (await reader.ReadAsync(token))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    /// <summary>取用户主组（SYSDG_USER 首组，G_IDX 最小），用于 OWNER_G 回填。</summary>
    internal static async Task<int?> GetPrimaryGroupAsync(SqlConnection connection, SqlTransaction transaction, string userId, CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT TOP 1 G_IDX FROM dbo.SYSDG_USER WITH (NOLOCK) WHERE USER_ID=@UserId ORDER BY G_IDX;", connection, transaction);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        return await command.ExecuteScalarAsync(token) as int?;
    }

    internal static async Task<bool> ColumnExistsAsync(SqlConnection connection, SqlTransaction? transaction, string table, string column, CancellationToken token)
    {
        const string sql = "SELECT 1 FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table AND c.name=@Column;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        command.Parameters.Add("@Column", SqlDbType.NVarChar, 100).Value = column;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    internal static async Task<string?> GetDfVerifyAsync(SqlConnection connection, SqlTransaction? transaction, string table, CancellationToken token)
    {
        const string sql = "SELECT LTRIM(RTRIM(ISNULL(DF_VERIFY,''))) FROM dbo.TABLES WITH (NOLOCK) WHERE T_ID=@Table;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        var result = await command.ExecuteScalarAsync(token);
        return result is null || string.IsNullOrWhiteSpace(result.ToString()) ? null : result.ToString();
    }

    internal static IReadOnlySet<string> ScopeFields(IReadOnlyList<FormFieldDefinition> fields, IReadOnlyList<string> pkColumns) =>
        fields.Where(field => !field.DisplayOnly).Select(field => field.Key).Concat(pkColumns).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static async Task<bool> TableExistsAsync(SqlConnection connection, string table, CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT 1 FROM sys.objects o JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table AND o.type IN ('U','V');", connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = table;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    internal static async Task<bool> ColumnsExistAsync(SqlConnection connection, string table, IReadOnlyList<string> columns, CancellationToken token)
    {
        var placeholders = string.Join(",", columns.Select((_, i) => $"@C{i}"));
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table AND c.name IN ({placeholders});", connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = table;
        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters.Add($"@C{i}", SqlDbType.NVarChar, 128).Value = columns[i];
        }
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        return count == columns.Count;
    }

    /// <summary>
    /// 选择器回填映射来源列存在性：物理列 或 来源表内受控虚拟列
    /// （FIELDS.IS_VIRTUAL=1 且 VIRTUAL_EXP 非空，运行时由 VirtualColumnResolver 经受控 JOIN 解析）。
    /// 来源列取自「来源查询结果集列」（物理 + VIRTUAL_EXP 派生），虚拟来源列合法。
    /// </summary>
    internal static async Task<bool> ReturnColumnsExistAsync(SqlConnection connection, string table, IReadOnlyList<string> columns, CancellationToken token)
    {
        if (columns.Count == 0) return true;
        var values = string.Join(",", columns.Select((_, i) => $"(CAST(@C{i} AS nvarchar(128)))"));
        var sql = """
            SELECT COUNT(*) FROM (VALUES {values}) AS v(col)
            WHERE NOT (
              EXISTS (SELECT 1 FROM sys.columns c
                      JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                      JOIN sys.schemas s ON o.schema_id=s.schema_id
                      WHERE s.name=N'dbo' AND o.name=@Table AND c.name=v.col)
              OR EXISTS (SELECT 1 FROM dbo.FIELDS f WITH (NOLOCK)
                         WHERE f.T_ID=@Table AND LTRIM(RTRIM(f.F_ID))=v.col
                           AND COALESCE(f.IS_VIRTUAL,0)=1
                           AND LTRIM(RTRIM(ISNULL(f.VIRTUAL_EXP,'')))<>'')
            );
            """.Replace("{values}", values);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = table;
        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters.Add($"@C{i}", SqlDbType.NVarChar, 128).Value = columns[i];
        }
        var missing = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        return missing == 0;
    }

    internal static async Task<Dictionary<string, object?>?> ReadRowAsync(
        SqlConnection connection, SqlTransaction? transaction, string table, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, IReadOnlyList<string> fields, CancellationToken token)
    {
        var rows = await ReadRowsAsync(connection, transaction, table, pkColumns, keyValues, fields, token);
        return rows.Count == 0 ? null : rows[0];
    }

    internal static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadRowsAsync(
        SqlConnection connection, SqlTransaction? transaction, string table, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, IReadOnlyList<string> fields, CancellationToken token)
    {
        var select = string.Join(',', fields.Select(field => $"[{field}]"));
        var where = string.Join(" AND ", pkColumns.Select((column, index) => $"[{column}]=@k{index}"));
        await using var command = new SqlCommand($"SELECT {select} FROM dbo.[{table}] WHERE {where};", connection, transaction);
        AddKeyParameters(command, pkColumns, keyValues);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(token))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                row[reader.GetName(i)] = value is string text ? text.Trim() : value;
            }
            result.Add(row);
        }
        return result;
    }

    /// <summary>执行命令并读取全部行 → 字典（通用 Reader→Dictionary 收敛，C4；字符串值 Trim）。</summary>
    internal static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadRowsAsync(
        SqlCommand command, CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token);
        return await ReadRowsAsync(reader, token);
    }

    /// <summary>从已执行 reader 的当前结果集读取全部行 → 字典（字符串值 Trim）。</summary>
    internal static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadRowsAsync(
        SqlDataReader reader, CancellationToken token)
    {
        var result = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(token))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                row[reader.GetName(i)] = value is string text ? text.Trim() : value;
            }
            result.Add(row);
        }
        return result;
    }

    internal static async Task<bool> RowExistsAsync(SqlConnection connection, SqlTransaction transaction, string table, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var where = string.Join(" AND ", pkColumns.Select((column, index) => $"[{column}]=@k{index}"));
        await using var command = new SqlCommand($"SELECT 1 FROM dbo.[{table}] WHERE {where};", connection, transaction);
        AddKeyParameters(command, pkColumns, keyValues);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    internal static async Task<bool> RecordInScopeAsync(
        SqlConnection connection, SqlTransaction? transaction, string table, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, string predicate, IReadOnlyList<object> parameters, CancellationToken token)
    {
        var where = string.Join(" AND ", pkColumns.Select((column, index) => $"[{column}]=@k{index}"));
        await using var command = new SqlCommand($"SELECT 1 FROM dbo.[{table}] WHERE {where} AND ({predicate});", connection, transaction);
        AddKeyParameters(command, pkColumns, keyValues);
        for (var i = 0; i < parameters.Count; i++)
        {
            command.Parameters.AddWithValue($"@df{i}", parameters[i] ?? DBNull.Value);
        }
        return await command.ExecuteScalarAsync(token) is not null;
    }

    internal static async Task DeleteDetailRowsAsync(SqlConnection connection, SqlTransaction transaction, string detailTable, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var where = string.Join(" AND ", pkColumns.Select((column, index) => $"[{column}]=@k{index}"));
        await using var command = new SqlCommand($"DELETE FROM dbo.[{detailTable}] WHERE {where};", connection, transaction);
        AddKeyParameters(command, pkColumns, keyValues);
        await command.ExecuteNonQueryAsync(token);
    }

    internal static void AddKeyParameters(SqlCommand command, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues)
    {
        for (var i = 0; i < pkColumns.Count; i++)
        {
            command.Parameters.AddWithValue($"@k{i}", keyValues[i] ?? string.Empty);
        }
    }

    internal static object NormalizeDbValue(object? value) => value is null ? DBNull.Value : value;

    internal static string ValueToString(object? value) => value switch
    {
        null => string.Empty,
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    internal static bool ValuesEqual(object? left, object? right)
    {
        if (left is null && right is null)
        {
            return true;
        }
        if (left is null || right is null)
        {
            return false;
        }
        if (left is string leftText && right is string rightText)
        {
            return string.Equals(leftText.Trim(), rightText.Trim(), StringComparison.Ordinal);
        }
        if (left is DateTime leftDate && right is DateTime rightDate)
        {
            return leftDate == rightDate;
        }
        if (left is double leftDouble && right is double rightDouble)
        {
            return Math.Abs(leftDouble - rightDouble) < 0.0001;
        }
        // 数值跨类型比较：TryConvert('float') 产生 double、DB int 读出 Int64 等场景
        if (IsNumeric(left) && IsNumeric(right))
        {
            var leftNumber = Convert.ToDecimal(left, System.Globalization.CultureInfo.InvariantCulture);
            var rightNumber = Convert.ToDecimal(right, System.Globalization.CultureInfo.InvariantCulture);
            return leftNumber == rightNumber;
        }
        // F_TYPE 与物理类型不一致时一侧为字符串一侧为数值，统一按数值解析比较
        if (left is string || right is string)
        {
            var leftTextValue = ValueToString(left).Trim();
            var rightTextValue = ValueToString(right).Trim();
            if (decimal.TryParse(leftTextValue, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var leftNumber) &&
                decimal.TryParse(rightTextValue, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var rightNumber))
            {
                return leftNumber == rightNumber;
            }
            return string.Equals(leftTextValue, rightTextValue, StringComparison.OrdinalIgnoreCase);
        }
        return left.Equals(right);
    }

    internal static bool IsNumeric(object value) => value is sbyte or byte or short or ushort or int or uint
        or long or ulong or float or double or decimal;
}
