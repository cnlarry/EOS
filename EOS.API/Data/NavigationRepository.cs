using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed record LegacyNavigationModule(int Id, string Label, int ParentId, int RootId, int SortIndex, bool Enabled, string? LegacyUrl);

public sealed class NavigationRepository(IConfiguration configuration)
{
    public async Task<IReadOnlyList<LegacyNavigationModule>> GetForUserAsync(string userId, CancellationToken token)
    {
        const string sql = """
            WITH UserModules AS (
                SELECT d.M_IDX
                FROM dbo.SYSDD d WITH (NOLOCK)
                WHERE d.USER_ID=@UserId AND ISNULL(d.EXEC_TAG,'A') <> 'A'
                UNION
                SELECT h.M_IDX
                FROM dbo.SYSDH h WITH (NOLOCK)
                INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX=h.G_IDX
                WHERE gu.USER_ID=@UserId AND ISNULL(h.EXEC_TAG,'A') <> 'A'
                  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD d WITH (NOLOCK) WHERE d.USER_ID=@UserId AND d.M_IDX=h.M_IDX)
            ), Included AS (
                SELECT m.M_IDX FROM dbo.MODULES m WITH (NOLOCK) INNER JOIN UserModules u ON u.M_IDX=m.M_IDX
                UNION
                SELECT p.M_IDX FROM dbo.MODULES p WITH (NOLOCK) WHERE EXISTS (
                    SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) INNER JOIN UserModules u ON u.M_IDX=m.M_IDX
                    WHERE m.M_P_IDX=p.M_IDX OR m.M_ROOT_IDX=p.M_IDX)
            )
            SELECT DISTINCT m.M_IDX,m.M_DESC,ISNULL(m.M_P_IDX,0) M_P_IDX,ISNULL(m.M_ROOT_IDX,m.M_IDX) M_ROOT_IDX,
                   ISNULL(m.SORT_IDX,0) SORT_IDX,ISNULL(m.M_TAG,1) M_TAG,m.M_URL
            FROM dbo.MODULES m WITH (NOLOCK) INNER JOIN Included i ON i.M_IDX=m.M_IDX
            WHERE NULLIF(LTRIM(RTRIM(m.M_DESC)),'') IS NOT NULL
            ORDER BY M_ROOT_IDX,M_P_IDX,SORT_IDX,m.M_IDX;
            """;
        await using var connection = new SqlConnection(configuration.GetConnectionString("ErpDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:ErpDatabase 未配置。"));
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<LegacyNavigationModule>();
        while (await reader.ReadAsync(token)) result.Add(new(
            reader.GetInt32(0), reader.GetString(1).Trim(), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetBoolean(5),
            reader.IsDBNull(6) ? null : reader.GetString(6).Trim()));
        return result;
    }
}
