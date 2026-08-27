using System.Data;
using Microsoft.Data.SqlClient;

using EOS.API.Telemetry;
namespace EOS.API.Data;

public sealed record NavigationGroup(int Index, string? Description, string? Expression, bool Enabled);

public sealed record LegacyNavigationModule(
    int Id,
    string Label,
    string? Alias,
    int ParentId,
    int RootId,
    int SortIndex,
    bool Enabled,
    string? LegacyUrl,
    string? MasterTable,
    string? Filter,
    string? Icon,
    IReadOnlyList<NavigationGroup> Groups);

public sealed class NavigationRepository(DbConnectionFactory connections, ILogger<NavigationRepository> logger)
{
    public async Task<IReadOnlyList<LegacyNavigationModule>> GetForUserAsync(string userId, CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
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
            SELECT DISTINCT m.M_IDX,m.M_DESC,m.M_ALIAS,ISNULL(m.M_P_IDX,0) M_P_IDX,ISNULL(m.M_ROOT_IDX,m.M_IDX) M_ROOT_IDX,
                   ISNULL(m.SORT_IDX,0) SORT_IDX,ISNULL(m.M_TAG,1) M_TAG,m.M_URL,m.MASTER_TABLE,m.FILTER,
                   LTRIM(RTRIM(ISNULL(m.M_ICON,''))),
                   ISNULL(m.GROUP1,0),m.GROUP_EXP1,m.GROUP_DESC1,
                   ISNULL(m.GROUP2,0),m.GROUP_EXP2,m.GROUP_DESC2,
                   ISNULL(m.GROUP3,0),m.GROUP_EXP3,m.GROUP_DESC3,
                   ISNULL(m.GROUP4,0),m.GROUP_EXP4,m.GROUP_DESC4,
                   ISNULL(m.GROUP5,0),m.GROUP_EXP5,m.GROUP_DESC5
            FROM dbo.MODULES m WITH (NOLOCK) INNER JOIN Included i ON i.M_IDX=m.M_IDX
            WHERE NULLIF(LTRIM(RTRIM(m.M_DESC)),'') IS NOT NULL AND ISNULL(m.M_TAG,1)=1
            ORDER BY M_ROOT_IDX,M_P_IDX,SORT_IDX,m.M_IDX;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<LegacyNavigationModule>();
        while (await reader.ReadAsync(token))
        {
            var groups = new List<NavigationGroup>();
            for (var i = 0; i < 5; i++)
            {
                // 列布局：0=M_IDX 1=M_DESC 2=M_ALIAS 3=M_P_IDX 4=M_ROOT_IDX 5=SORT_IDX
                // 6=M_TAG 7=M_URL 8=MASTER_TABLE 9=FILTER 10=M_ICON 11..25=GROUP1..5(EXPor/DESC)
                var offset = 11 + i * 3;
                var enabled = !reader.IsDBNull(offset) && reader.GetBoolean(offset);
                var expression = reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1).Trim();
                var description = reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2).Trim();
                if (enabled || !string.IsNullOrWhiteSpace(expression))
                    groups.Add(new NavigationGroup(i + 1, string.IsNullOrWhiteSpace(description) ? null : description, expression, enabled));
            }
            result.Add(new(
                reader.GetInt32(0),
                reader.GetString(1).Trim(),
                reader.IsDBNull(2) ? null : reader.GetString(2).Trim(),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetString(7).Trim(),
                reader.IsDBNull(8) ? null : reader.GetString(8).Trim(),
                reader.IsDBNull(9) ? null : reader.GetString(9).Trim(),
                reader.IsDBNull(10) ? null : reader.GetString(10).Trim(),
                groups));
        }
        logger.LogDebug("用户导航 userId={UserId} modules={ModuleCount}", userId.Trim(), result.Count);
        return result;
    }
}
