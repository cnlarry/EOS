using System.Data;

using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Reports;

/// <summary>
/// 报表中心的数据访问：目录（按归属模块分组 + 收藏/最近使用标记）与 <c>REPORT_USER_STATE</c> 三处 upsert
/// （收藏开关 / 最近使用 / 收藏重排）。此前这些 SQL 写在 <c>ReportCenterController</c> 里，
/// 控制器因此同时是契约层与数据访问层（ADR-036 批 1 第 5 项）。
///
/// 目录查询的**可见性判据**与报表权、打印面板清单同源，只有一处实现：
/// <see cref="ModuleReportVisibility"/>（归属模块的 <c>REPORT_TAG</c>，个人
/// <c>SYS_USER_MODULE_PERMISSION</c> 优先，否则组授权取或）。三处各写一遍时，
/// "目录里看得见、打印面板里没有"这类不一致只能靠人眼发现。
/// </summary>
public sealed class ReportCenterRepository(DbConnectionFactory connections)
{
    /// <summary>报表目录：按业务域分组返回用户可见的全部报表 + 收藏/最近使用标记。</summary>
    public async Task<IReadOnlyList<ReportCatalogItem>> ListCatalogAsync(string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        // 报表按**归属模块**（业务模块）分组与鉴权：r.M_IDX 指向业务模块，不再是承载页。
        // 可见性判据只在这里拼一次（ModuleReportVisibility），与报表权、打印面板清单同源。
        // 业务域取归属模块的上级菜单分组（历史实现取的是承载页的 XX98 目录，归位后那层已不存在）。
        var catalogSql = $"""
            SELECT m.M_IDX, LTRIM(RTRIM(ISNULL(m.M_DESC, ''))) AS M_DESC,
                   LTRIM(RTRIM(ISNULL(dom.M_DESC, ISNULL(m.M_DESC, '')))) AS DOMAIN_DESC,
                   LTRIM(RTRIM(ISNULL(r.REPORT_ID, ''))) AS REPORT_ID,
                   LTRIM(RTRIM(ISNULL(r.REPORT_NAME, r.REPORT_ID))) AS REPORT_NAME,
                   ISNULL(r.IS_DEFAULT, 0) AS IS_DEFAULT,
                   ISNULL(p.FAVORITE_TAG, 0) AS FAVORITE_TAG,
                   ISNULL(p.SORT_IDX, 0) AS SORT_IDX,
                   p.LAST_RUN_AT
            FROM dbo.REPORT r WITH (NOLOCK)
            INNER JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX = r.M_IDX
            LEFT JOIN dbo.MODULES dom WITH (NOLOCK) ON dom.M_IDX = m.M_P_IDX
            LEFT JOIN dbo.REPORT_USER_STATE p WITH (NOLOCK) ON p.USER_ID = @UserId AND p.M_IDX = r.M_IDX AND p.REPORT_ID = r.REPORT_ID
            WHERE {ModuleReportVisibility.ForUser("r.M_IDX")}
            ORDER BY DOMAIN_DESC, ISNULL(p.FAVORITE_TAG, 0) DESC, ISNULL(p.SORT_IDX, 0), ISNULL(r.IS_DEFAULT, 0) DESC, r.REPORT_ID;
            """;

        await using var command = new SqlCommand(catalogSql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);

        var reports = new List<ReportCatalogItem>();
        while (await reader.ReadAsync(token))
        {
            reports.Add(new ReportCatalogItem(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetDateTime(8)));
        }
        return reports;
    }

    /// <summary>
    /// 收藏开关与收藏排序（upsert）。只覆盖本次提交的字段——未提交的 <c>SORT_IDX</c> 保持原值，
    /// 避免一次收藏开关把收藏顺序清空。
    /// </summary>
    public async Task SaveFavoriteAsync(
        string userId, int moduleId, string reportId, bool favorite, int? sortIndex, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            IF EXISTS (SELECT 1 FROM dbo.REPORT_USER_STATE WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId)
                UPDATE dbo.REPORT_USER_STATE
                SET FAVORITE_TAG=@FavoriteTag, SORT_IDX=ISNULL(@SortIdx, SORT_IDX)
                WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId;
            ELSE IF @FavoriteTag = 1
                INSERT INTO dbo.REPORT_USER_STATE (USER_ID,M_IDX,REPORT_ID,FAVORITE_TAG,SORT_IDX)
                VALUES (@UserId,@ModuleId,@ReportId,@FavoriteTag,@SortIdx);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = reportId.Trim();
        command.Parameters.Add("@FavoriteTag", SqlDbType.Bit).Value = favorite;
        command.Parameters.Add("@SortIdx", SqlDbType.Int).Value = sortIndex ?? (object)DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>记录「最近使用」：只写 <c>LAST_RUN_AT</c>，不触碰收藏开关与收藏顺序。时间戳取服务端时钟。</summary>
    public async Task TouchAsync(string userId, int moduleId, string reportId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            IF EXISTS (SELECT 1 FROM dbo.REPORT_USER_STATE WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId)
                UPDATE dbo.REPORT_USER_STATE SET LAST_RUN_AT=@LastRunAt
                WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId;
            ELSE
                INSERT INTO dbo.REPORT_USER_STATE (USER_ID,M_IDX,REPORT_ID,FAVORITE_TAG,LAST_RUN_AT)
                VALUES (@UserId,@ModuleId,@ReportId,0,@LastRunAt);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = reportId.Trim();
        command.Parameters.Add("@LastRunAt", SqlDbType.DateTime2).Value = DateTime.UtcNow;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// 收藏重排：一次性写入完整收藏顺序，<c>SORT_IDX</c>=1..N 顺次落库，整体一个事务。
    /// 权限判定由调用方**先行逐项完成**（任一不可见即整体拒绝），故这里不会写到一半才发现越权。
    /// </summary>
    public async Task ReorderAsync(
        string userId, IReadOnlyList<(int ModuleId, string ReportId)> items, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var (moduleId, reportId) = items[index];
                const string sql = """
                    IF EXISTS (SELECT 1 FROM dbo.REPORT_USER_STATE WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId)
                        UPDATE dbo.REPORT_USER_STATE SET FAVORITE_TAG=1, SORT_IDX=@SortIdx
                        WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId;
                    ELSE
                        INSERT INTO dbo.REPORT_USER_STATE (USER_ID,M_IDX,REPORT_ID,FAVORITE_TAG,SORT_IDX)
                        VALUES (@UserId,@ModuleId,@ReportId,1,@SortIdx);
                    """;
                await using var command = new SqlCommand(sql, connection, transaction);
                command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
                command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = reportId.Trim();
                command.Parameters.Add("@SortIdx", SqlDbType.Int).Value = index + 1;
                await command.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }
}
