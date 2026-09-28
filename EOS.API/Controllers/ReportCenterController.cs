using System.Data;
using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

/// <summary>
/// 报表中心：列表明细型报表的目录入口。
/// 撤 22 个 XX98 菜单节点后，全部报表经本控制器查询、按业务域分组展示；
/// 单据打印仍走统一表单工具栏（ReportController Pdf / PrintController，机制不动）。
/// 权限语义（P1）：模块级 REPORT_TAG 为可见性真源，SYSDD_REPORT 降级为 override 收紧。
/// 目录只返回「用户对模块有报表可见性（REPORT_TAG）」的报表。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/report-center")]
public sealed class ReportCenterController(
    DbConnectionFactory connections,
    IPermissionService permissions) : ControllerBase
{
    /// <summary>报表目录：按业务域分组返回用户可见的全部报表 + 收藏/最近使用标记。</summary>
    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog(CancellationToken token)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Unauthorized();

        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        // 模块级报表可见性真源：个人 SYSDD.REPORT_TAG 优先，否则组 SYSDH.REPORT_TAG OR
        // （与 ModuleRightsRepository.GetModuleReportTagAsync 同口径）。
        const string catalogSql = """
            SELECT m.M_IDX, LTRIM(RTRIM(ISNULL(m.M_DESC, ''))) AS M_DESC,
                   LTRIM(RTRIM(ISNULL(dom.M_DESC, ISNULL(m.M_DESC, '')))) AS DOMAIN_DESC,
                   LTRIM(RTRIM(ISNULL(r.REPORT_ID, ''))) AS REPORT_ID,
                   LTRIM(RTRIM(ISNULL(r.REPORT_NAME, r.REPORT_ID))) AS REPORT_NAME,
                   ISNULL(r.IS_DEFAULT, 0) AS IS_DEFAULT,
                   ISNULL(p.FAVORITE_TAG, 0) AS FAVORITE_TAG,
                   ISNULL(p.SORT_IDX, 0) AS SORT_IDX,
                   p.LAST_RUN_AT
            FROM dbo.REPORT r WITH (NOLOCK)
            INNER JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX = r.R_M_IDX
            LEFT JOIN dbo.MODULES dom WITH (NOLOCK) ON dom.M_IDX = m.M_P_IDX
                AND RIGHT(CAST(dom.M_IDX AS VARCHAR(20)), 2) = '98'
            LEFT JOIN dbo.SYSDD_REPORT p WITH (NOLOCK) ON p.USER_ID = @UserId AND p.M_IDX = r.R_M_IDX AND p.REPORT_ID = r.REPORT_ID
            WHERE r.R_M_IDX IS NOT NULL
              AND (
                EXISTS (SELECT 1 FROM dbo.SYSDD d WITH (NOLOCK)
                        WHERE d.USER_ID = @UserId AND d.M_IDX = r.R_M_IDX AND ISNULL(d.REPORT_TAG, 0) = 1)
                OR (
                  NOT EXISTS (SELECT 1 FROM dbo.SYSDD d WITH (NOLOCK) WHERE d.USER_ID = @UserId AND d.M_IDX = r.R_M_IDX)
                  AND EXISTS (SELECT 1 FROM dbo.SYSDH h WITH (NOLOCK)
                              INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX = h.G_IDX
                              WHERE gu.USER_ID = @UserId AND h.M_IDX = r.R_M_IDX AND ISNULL(h.REPORT_TAG, 0) = 1)
                )
              )
              AND NOT EXISTS (
                SELECT 1 FROM dbo.SYSDD_REPORT o WITH (NOLOCK)
                WHERE o.USER_ID = @UserId AND o.M_IDX = r.R_M_IDX AND o.REPORT_ID = r.REPORT_ID
                  AND ISNULL(o.PREVIEW_TAG, 0) = 0
              )
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

        return Ok(reports);
    }

    /// <summary>
    /// 收藏开关与收藏排序：写 SYSDD_REPORT（upsert，保持 override 收紧语义）。
    /// 只覆盖本次提交的字段——未提交的（SORT_IDX）保持原值，避免一次收藏开关顺带清空收藏顺序。
    /// </summary>
    [HttpPost("favorite")]
    public async Task<IActionResult> SaveFavorite(
        [FromBody] ReportFavoriteRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.ReportId))
            throw new ArgumentException("报表编号不能为空。", nameof(request));
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Unauthorized();

        // 权限门：目标模块报表可见性（REPORT_TAG）——与目录查询同口径
        var permission = await permissions.GetAsync(userId, request.ModuleId, token);
        if (!permission.Rights.CanBrowse) return Forbid();

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            IF EXISTS (SELECT 1 FROM dbo.SYSDD_REPORT WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId)
                UPDATE dbo.SYSDD_REPORT
                SET FAVORITE_TAG=@FavoriteTag, SORT_IDX=ISNULL(@SortIdx, SORT_IDX)
                WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId;
            ELSE IF @FavoriteTag = 1
                INSERT INTO dbo.SYSDD_REPORT (USER_ID,M_IDX,REPORT_ID,FAVORITE_TAG,SORT_IDX)
                VALUES (@UserId,@ModuleId,@ReportId,@FavoriteTag,@SortIdx);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = request.ModuleId;
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = request.ReportId.Trim();
        command.Parameters.Add("@FavoriteTag", SqlDbType.Bit).Value = request.Favorite;
        command.Parameters.Add("@SortIdx", SqlDbType.Int).Value = request.SortIndex ?? (object)DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
        return NoContent();
    }

    /// <summary>
    /// 记录「最近使用」：只写 LAST_RUN_AT，不触碰收藏开关与收藏顺序。
    /// 时间戳以服务端时钟为准（不受调用方时区/时钟影响）。
    /// </summary>
    [HttpPost("touch")]
    public async Task<IActionResult> Touch(
        [FromBody] ReportTouchRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.ReportId))
            throw new ArgumentException("报表编号不能为空。", nameof(request));
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Unauthorized();

        // 权限门：目标模块报表可见性（REPORT_TAG）——与目录查询同口径
        var permission = await permissions.GetAsync(userId, request.ModuleId, token);
        if (!permission.Rights.CanBrowse) return Forbid();

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            IF EXISTS (SELECT 1 FROM dbo.SYSDD_REPORT WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId)
                UPDATE dbo.SYSDD_REPORT SET LAST_RUN_AT=@LastRunAt
                WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId;
            ELSE
                INSERT INTO dbo.SYSDD_REPORT (USER_ID,M_IDX,REPORT_ID,FAVORITE_TAG,LAST_RUN_AT)
                VALUES (@UserId,@ModuleId,@ReportId,0,@LastRunAt);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = request.ModuleId;
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = request.ReportId.Trim();
        command.Parameters.Add("@LastRunAt", SqlDbType.DateTime2).Value = DateTime.UtcNow;
        await command.ExecuteNonQueryAsync(token);
        return NoContent();
    }

    /// <summary>收藏重排（P5）：一次性写入完整收藏顺序，SORT_IDX=1..N 顺次落库。</summary>
    [HttpPost("reorder")]
    public async Task<IActionResult> Reorder(
        [FromBody] ReportReorderRequest request, CancellationToken token)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Unauthorized();
        if (request.Items is null || request.Items.Count == 0)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ORDER", "收藏顺序不能为空。"));

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            for (var index = 0; index < request.Items.Count; index++)
            {
                var item = request.Items[index];
                if (string.IsNullOrWhiteSpace(item.ReportId)) continue;
                // 权限门：目标模块报表可见性（REPORT_TAG）
                var permission = await permissions.GetAsync(userId, item.ModuleId, token);
                if (!permission.Rights.CanBrowse) return Forbid();

                const string sql = """
                    IF EXISTS (SELECT 1 FROM dbo.SYSDD_REPORT WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId)
                        UPDATE dbo.SYSDD_REPORT SET FAVORITE_TAG=1, SORT_IDX=@SortIdx
                        WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId;
                    ELSE
                        INSERT INTO dbo.SYSDD_REPORT (USER_ID,M_IDX,REPORT_ID,FAVORITE_TAG,SORT_IDX)
                        VALUES (@UserId,@ModuleId,@ReportId,1,@SortIdx);
                    """;
                await using var command = new SqlCommand(sql, connection, transaction);
                command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
                command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = item.ModuleId;
                command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = item.ReportId.Trim();
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
        return NoContent();
    }
}

/// <summary>报表中心收藏重排项。</summary>
public sealed record ReportReorderItem(int ModuleId, string? ReportId);

/// <summary>收藏重排请求。</summary>
public sealed record ReportReorderRequest(IReadOnlyList<ReportReorderItem>? Items);

/// <summary>报表中心目录项。</summary>
public sealed record ReportCatalogItem(
    int ModuleId,
    string ModuleDesc,
    string DomainDesc,
    string ReportId,
    string ReportName,
    bool IsDefault,
    bool Favorite,
    int SortIndex,
    DateTime? LastRunAt);

/// <summary>收藏开关/排序请求。SortIndex 缺省表示本次不改收藏顺序。</summary>
public sealed record ReportFavoriteRequest(
    int ModuleId,
    string? ReportId,
    bool Favorite = false,
    int? SortIndex = null);

/// <summary>最近使用登记请求（时间戳由服务端生成）。</summary>
public sealed record ReportTouchRequest(
    int ModuleId,
    string? ReportId);
