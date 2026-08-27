using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

using EOS.API.Telemetry;
namespace EOS.API.Data;

/// <summary>
/// 用户管理（ADR-004 随安全升级建设）：列用户、管理员设置密码、启用/停用。
/// 只允许修改认证/安全相关列（USER_PWD、ACTIVE_TAG、审计列），不触碰业务资料。
/// </summary>
public sealed class UserAdminRepository(DbConnectionFactory connections, ILogger<UserAdminRepository> logger)
{
    private static readonly Regex UserIdPattern = new("^[A-Za-z0-9_-]{1,10}$", RegexOptions.Compiled);

    public async Task<UserAdminPageResult> GetUsersAsync(
        string? keyword,
        int page,
        int pageSize,
        CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var pattern = $"%{keyword?.Trim() ?? ""}%";
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            ;WITH base AS (
                SELECT LTRIM(RTRIM(l.USER_ID)) USER_ID,
                       LTRIM(RTRIM(l.EMP_ID)) EMP_ID,
                       COALESCE(NULLIF(LTRIM(RTRIM(n.EMP_NAME)),''), LTRIM(RTRIM(l.EMP_ID))) EMP_NAME,
                       LTRIM(RTRIM(ISNULL(n.DEPT_ID,''))) DEPT_ID,
                       COALESCE(NULLIF(LTRIM(RTRIM(dbo.f_get_dept_desc(n.DEPT_ID))),''),'') DEPT_DESC,
                       LTRIM(RTRIM(ISNULL(n.COMPANY_ID,''))) COMPANY_ID,
                       LTRIM(RTRIM(ISNULL(l.G_IDX,''))) G_IDX,
                       CAST(ISNULL(l.ACTIVE_TAG,0) AS bit) ACTIVE_TAG,
                       CAST(CASE WHEN LEN(ISNULL(l.USER_PWD,'')) > 0 THEN 1 ELSE 0 END AS bit) HAS_PASSWORD,
                       LTRIM(RTRIM(ISNULL(l.LAST_UPDATE_BY,''))) LAST_UPDATE_BY,
                       l.LAST_UPDATE_DATE
                FROM dbo.SYSDL l WITH (NOLOCK)
                LEFT JOIN dbo.SYSDN n WITH (NOLOCK) ON l.EMP_ID = n.EMP_ID
            )
            SELECT USER_ID,EMP_ID,EMP_NAME,DEPT_ID,DEPT_DESC,COMPANY_ID,G_IDX,ACTIVE_TAG,HAS_PASSWORD,
                   LAST_UPDATE_BY,LAST_UPDATE_DATE,
                   OUTER_APPLY_GROUPS.GROUPS,
                   COUNT(*) OVER() AS Total
            FROM base
            OUTER APPLY (
                SELECT STUFF((
                    SELECT N'、' + LTRIM(RTRIM(ISNULL(g.G_DESC,'')))
                    FROM dbo.SYSDG_USER ug
                    INNER JOIN dbo.SYSDG g ON ug.G_IDX=g.G_IDX
                    WHERE LTRIM(RTRIM(ug.USER_ID))=LTRIM(RTRIM(base.USER_ID))
                    ORDER BY g.G_IDX
                    FOR XML PATH('')), 1, 1, N'') AS GROUPS
            ) AS OUTER_APPLY_GROUPS
            WHERE @Keyword = '' OR USER_ID LIKE @Pattern OR EMP_ID LIKE @Pattern OR EMP_NAME LIKE @Pattern
            ORDER BY USER_ID
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 200).Value = keyword?.Trim() ?? "";
        command.Parameters.Add("@Pattern", SqlDbType.NVarChar, 200).Value = pattern;
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = (page - 1) * pageSize;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;
        await using var reader = await command.ExecuteReaderAsync(token);
        var items = new List<UserAdminSummary>();
        var total = 0;
        while (await reader.ReadAsync(token))
        {
            if (total == 0) total = Convert.ToInt32(reader.GetValue(12));
            items.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                reader.GetBoolean(7), reader.GetBoolean(8), NullIfEmpty(reader, 9),
                reader.IsDBNull(10) ? null : reader.GetDateTime(10)));
        }
        return new(items, total, page, pageSize);
    }

    public async Task SetPasswordAsync(string userId, string newPassword, string updatedBy, CancellationToken token)
    {
        ValidateUserId(userId);
        PasswordPolicy.Validate(newPassword);
        var hash = PasswordHasher.Hash(newPassword);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            UPDATE dbo.SYSDL
            SET USER_PWD=@Hash, LAST_UPDATE_BY=@UpdatedBy, LAST_UPDATE_DATE=GETDATE()
            WHERE LTRIM(RTRIM(USER_ID))=@UserId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        command.Parameters.Add("@Hash", SqlDbType.NVarChar, 50).Value = hash;
        command.Parameters.Add("@UpdatedBy", SqlDbType.NChar, 20).Value = updatedBy;
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new KeyNotFoundException("用户不存在。");
        logger.LogInformation("管理员设置密码 userId={UserId} by={UpdatedBy}", userId.Trim(), updatedBy);
    }

    public async Task SetActiveAsync(string userId, bool isActive, string updatedBy, CancellationToken token)
    {
        ValidateUserId(userId);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            UPDATE dbo.SYSDL
            SET ACTIVE_TAG=@Active, LAST_UPDATE_BY=@UpdatedBy, LAST_UPDATE_DATE=GETDATE()
            WHERE LTRIM(RTRIM(USER_ID))=@UserId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        command.Parameters.Add("@Active", SqlDbType.Bit).Value = isActive;
        command.Parameters.Add("@UpdatedBy", SqlDbType.NChar, 20).Value = updatedBy;
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new KeyNotFoundException("用户不存在。");
        logger.LogInformation("管理员变更启用状态 userId={UserId} active={IsActive} by={UpdatedBy}", userId.Trim(), isActive, updatedBy);
    }

    private static void ValidateUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Trim().Length > 10 || !UserIdPattern.IsMatch(userId.Trim()))
            throw new ArgumentException("用户 ID 无效。", nameof(userId));
    }

    private static string? NullIfEmpty(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal).Trim();
}
