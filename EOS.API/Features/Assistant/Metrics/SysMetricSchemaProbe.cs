using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Metrics;

/// <summary>
/// sys.* backed schema probe (sys.objects for U/V objects, sys.columns for columns).
/// Queries run per validation call with fully parameterized identifiers.
/// </summary>
public sealed class SysMetricSchemaProbe(DbConnectionFactory connections) : IMetricSchemaProbe
{
    public async Task<bool> TableExistsAsync(string table, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            """
            SELECT 1 FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U','V') AND s.name = N'dbo' AND o.name = @Table;
            """, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = table;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    public async Task<IReadOnlySet<string>> GetColumnsAsync(string table, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            """
            SELECT c.name FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U','V') AND s.name = N'dbo' AND o.name = @Table;
            """, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = table;
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            columns.Add(reader.GetString(0));
        }
        return columns;
    }
}
