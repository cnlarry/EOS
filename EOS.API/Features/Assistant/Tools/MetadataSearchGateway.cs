using System.Data;
using EOS.API.Data;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>一条元数据命中：模块 / 表 / 字段 / 自定义动作。</summary>
public sealed record MetadataHit(
    string Kind,
    int ModuleId,
    string ModuleTitle,
    string Key,
    string Label,
    string? TableId);

public sealed record MetadataSearchResult(IReadOnlyList<MetadataHit> Hits, bool Truncated);

/// <summary>
/// search_metadata 的窄接口。命中来自 MODULES / TABLES / FIELDS / MODULE_BUSINESS_ACTION，
/// 返回前按**模块浏览权**过滤（用户看不到的模块，其表/字段/动作一并隐藏）。
/// </summary>
public interface IMetadataSearchGateway
{
    Task<MetadataSearchResult> SearchAsync(string userId, string keyword, int max, CancellationToken token);
}

public sealed class MetadataSearchGateway(
    DbConnectionFactory connections,
    IPermissionService permissions) : IMetadataSearchGateway
{
    public async Task<MetadataSearchResult> SearchAsync(
        string userId, string keyword, int max, CancellationToken token)
    {
        var text = keyword?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return new MetadataSearchResult([], false);
        }

        max = Math.Clamp(max, 1, 100);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.Add("@Like", SqlDbType.NVarChar, 120).Value = $"%{text}%";
        command.Parameters.Add("@Prefix", SqlDbType.NVarChar, 120).Value = $"{text}%";
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 100).Value = text;
        // 多取一些再按权限过滤，避免"前 20 条恰好都无权"导致结果为空。
        command.Parameters.Add("@Max", SqlDbType.Int).Value = Math.Min(max * 3, 300);

        var candidates = new List<MetadataHit>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                candidates.Add(new MetadataHit(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        // 权限过滤：同一模块只查一次；同一 (类型, 模块, 键) 只保留一条。
        var allowed = new Dictionary<int, bool>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<MetadataHit>();
        foreach (var hit in candidates)
        {
            if (!seen.Add($"{hit.Kind}|{hit.ModuleId}|{hit.Key}")) continue;
            if (!allowed.TryGetValue(hit.ModuleId, out var canBrowse))
            {
                var permission = await permissions.GetAsync(userId, hit.ModuleId, token);
                canBrowse = permission is not null && permission.CanBrowse;
                allowed[hit.ModuleId] = canBrowse;
            }

            if (!canBrowse) continue;
            hits.Add(hit);
            if (hits.Count >= max) break;
        }

        return new MetadataSearchResult(hits, candidates.Count > hits.Count);
    }

    /// <summary>
    /// 四类命中一条 SQL 查完；排序口径与工具输出一致：完全匹配 → 前缀匹配 → 包含匹配，
    /// 同类内再按模块号与键稳定排序。表名/字段名只来自库内元数据，值全部参数化。
    /// </summary>
    private const string Sql = """
        SELECT TOP (@Max)
            h.KIND, h.MODULE_KEY, h.MODULE_TITLE, h.[KEY], h.LABEL, h.TABLE_ID
        FROM (
            SELECT N'module' AS KIND, m.M_IDX AS MODULE_KEY,
                   LTRIM(RTRIM(m.M_DESC)) AS MODULE_TITLE,
                   CONVERT(NVARCHAR(20), m.M_IDX) AS [KEY],
                   LTRIM(RTRIM(m.M_DESC)) AS LABEL,
                   CONVERT(NVARCHAR(128), NULL) AS TABLE_ID
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE NULLIF(LTRIM(RTRIM(m.M_DESC)), N'') IS NOT NULL
              AND m.M_DESC LIKE @Like
            UNION ALL
            SELECT N'table', m.M_IDX, LTRIM(RTRIM(m.M_DESC)),
                   LTRIM(RTRIM(t.T_ID)), LTRIM(RTRIM(COALESCE(t.T_DESC, t.T_ID))),
                   LTRIM(RTRIM(t.T_ID))
            FROM dbo.TABLES t WITH (NOLOCK)
            INNER JOIN dbo.MODULES m WITH (NOLOCK)
                ON LTRIM(RTRIM(m.MASTER_TABLE)) = LTRIM(RTRIM(t.T_ID))
                OR LTRIM(RTRIM(m.DETAIL_TABLE)) = LTRIM(RTRIM(t.T_ID))
            WHERE t.T_ID LIKE @Like OR t.T_DESC LIKE @Like
            UNION ALL
            SELECT N'field', m.M_IDX, LTRIM(RTRIM(m.M_DESC)),
                   LTRIM(RTRIM(f.F_ID)), LTRIM(RTRIM(COALESCE(f.F_DESC, f.F_ID))),
                   LTRIM(RTRIM(f.T_ID))
            FROM dbo.FIELDS f WITH (NOLOCK)
            INNER JOIN dbo.MODULES m WITH (NOLOCK)
                ON LTRIM(RTRIM(m.MASTER_TABLE)) = LTRIM(RTRIM(f.T_ID))
                OR LTRIM(RTRIM(m.DETAIL_TABLE)) = LTRIM(RTRIM(f.T_ID))
            WHERE f.F_ID LIKE @Like OR f.F_DESC LIKE @Like
            UNION ALL
            SELECT N'action', m.M_IDX, LTRIM(RTRIM(m.M_DESC)),
                   LTRIM(RTRIM(a.EFFECT_KEY)), LTRIM(RTRIM(COALESCE(NULLIF(a.LABEL, N''), a.EFFECT_KEY))),
                   CONVERT(NVARCHAR(128), NULL)
            FROM dbo.MODULE_BUSINESS_ACTION a WITH (NOLOCK)
            INNER JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX = a.M_IDX
            WHERE a.EVENT_CODE = N'MANUAL'
              AND (a.EFFECT_KEY LIKE @Like OR a.LABEL LIKE @Like)
        ) h
        ORDER BY
            CASE WHEN h.[KEY] = @Keyword OR h.LABEL = @Keyword THEN 0
                 WHEN h.[KEY] LIKE @Prefix OR h.LABEL LIKE @Prefix THEN 1
                 ELSE 2 END,
            CASE h.KIND WHEN N'module' THEN 0 WHEN N'table' THEN 1 WHEN N'field' THEN 2 ELSE 3 END,
            h.MODULE_KEY, h.[KEY];
        """;
}
