using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Metrics;

public sealed record FieldRelationRow(
    string FromTable,
    string FromColumn,
    string ToTable,
    string ToColumn,
    string? Description);

public interface IFieldRelationRepository
{
    /// <summary>Lists registered field relations, optionally filtered by a
    /// keyword over table/column names and description. Developer-owned data,
    /// read-only for the assistant.</summary>
    Task<IReadOnlyList<FieldRelationRow>> ListAsync(string? keyword, CancellationToken token);
}

public sealed class FieldRelationRepository(DbConnectionFactory connections) : IFieldRelationRepository
{
    public async Task<IReadOnlyList<FieldRelationRow>> ListAsync(string? keyword, CancellationToken token)
    {
        const string sql = """
            SELECT FROM_TABLE, FROM_COLUMN, TO_TABLE, TO_COLUMN,
                   LTRIM(RTRIM(ISNULL(DESCRIPTION, ''))) AS DESCRIPTION
            FROM dbo.FIELD_RELATION WITH (NOLOCK)
            WHERE ISNULL(RELATION_KIND, N'READ') = N'READ'
              AND (@Keyword = ''
                   OR FROM_TABLE LIKE '%' + @Keyword + '%' OR FROM_COLUMN LIKE '%' + @Keyword + '%'
                   OR TO_TABLE LIKE '%' + @Keyword + '%' OR TO_COLUMN LIKE '%' + @Keyword + '%'
                   OR DESCRIPTION LIKE '%' + @Keyword + '%')
            ORDER BY FROM_TABLE, FROM_COLUMN, TO_TABLE;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 128).Value = keyword ?? string.Empty;
        var relations = new List<FieldRelationRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            relations.Add(new FieldRelationRow(
                reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return relations;
    }
}
