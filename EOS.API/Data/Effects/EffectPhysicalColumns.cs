using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>Loads the dbo schema physical column whitelist (table.column, case-insensitive).</summary>
public sealed class EffectPhysicalColumns
{
    public async Task<ISet<string>> LoadAsync(SqlConnection connection, CancellationToken token)
    {
        const string sql = """
            SELECT o.name,c.name
            FROM sys.objects o
            JOIN sys.columns c ON c.object_id=o.object_id
            WHERE o.type IN ('U','V') AND SCHEMA_NAME(o.schema_id)=N'dbo';
            """;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
            await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(reader.GetString(0) + "." + reader.GetString(1));
        if (!wasOpen)
            await connection.CloseAsync();
        return result;
    }
}
