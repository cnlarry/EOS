using System.Data;
using Microsoft.Data.SqlClient;

using EOS.API.Telemetry;
namespace EOS.API.Data;

public sealed record NavigationGroup(int GroupId, string? Description, string? Expression);

public sealed record NavigationModule(
    int Id,
    string Label,
    string? Alias,
    int ParentId,
    int RootId,
    int SortIndex,
    bool Enabled,
    string? SourceUrl,
    string? MasterTable,
    string? Filter,
    string? Icon,
    IReadOnlyList<NavigationGroup> Groups);

public sealed class NavigationRepository(DbConnectionFactory connections, ILogger<NavigationRepository> logger)
{
    public async Task<IReadOnlyList<NavigationModule>> GetForUserAsync(string userId, CancellationToken token)
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
                   LTRIM(RTRIM(ISNULL(m.M_ICON,'')))
            FROM dbo.MODULES m WITH (NOLOCK) INNER JOIN Included i ON i.M_IDX=m.M_IDX
            WHERE NULLIF(LTRIM(RTRIM(m.M_DESC)),'') IS NOT NULL AND ISNULL(m.M_TAG,1)=1
            ORDER BY M_ROOT_IDX,M_P_IDX,SORT_IDX,m.M_IDX;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        await connection.OpenAsync(token);
        var rows = new List<(int Id, string Label, string? Alias, int ParentId, int RootId, int SortIndex, bool Enabled, string? Url, string? Master, string? Filter, string? Icon)>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                rows.Add((
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
                    reader.IsDBNull(10) ? null : reader.GetString(10).Trim()));
            }
        }
        // 分组在 MODULE_GROUPS（一个模块任意多组），与模块行分两次读：一行的列数不再随分组数量增长
        var groups = await ReadGroupsAsync(connection, token);
        var result = new List<NavigationModule>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(new(
                row.Id, row.Label, row.Alias, row.ParentId, row.RootId, row.SortIndex, row.Enabled,
                row.Url, row.Master, row.Filter, row.Icon,
                groups.TryGetValue(row.Id, out var moduleGroups) ? moduleGroups : []));
        }
        logger.LogDebug("用户导航 userId={UserId} modules={ModuleCount}", userId.Trim(), result.Count);
        return result;
    }

    /// <summary>模块号 → 分组清单（按 SORT_IDX、GROUP_ID 排序；只收名称非空的行）。</summary>
    private static async Task<Dictionary<int, IReadOnlyList<NavigationGroup>>> ReadGroupsAsync(
        SqlConnection connection,
        CancellationToken token)
    {
        const string sql = """
            SELECT M_IDX,GROUP_ID,GROUP_DESC,GROUP_EXP FROM dbo.MODULE_GROUPS WITH (NOLOCK)
            ORDER BY M_IDX,SORT_IDX,GROUP_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<int, List<NavigationGroup>>();
        while (await reader.ReadAsync(token))
        {
            var description = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
            if (description.Length == 0) continue;
            if (!result.TryGetValue(reader.GetInt32(0), out var list))
            {
                list = [];
                result[reader.GetInt32(0)] = list;
            }
            list.Add(new NavigationGroup(
                reader.GetInt32(1),
                description,
                reader.IsDBNull(3) ? null : reader.GetString(3).Trim()));
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<NavigationGroup>)pair.Value);
    }
}
