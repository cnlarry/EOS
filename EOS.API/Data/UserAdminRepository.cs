using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

using EOS.API.Telemetry;
namespace EOS.API.Data;

/// <summary>
/// 用户管理：列用户、管理员设置密码、启用/停用。
/// 只允许修改认证/安全相关列（USER_PWD、ACTIVE_TAG、审计列），不触碰业务资料。
/// </summary>
public sealed class UserAdminRepository(
    DbConnectionFactory connections,
    ILogger<UserAdminRepository> logger,
    WorkbenchAuditWriter auditWriter,
    PermissionCache permissionCache)
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
                       LTRIM(RTRIM(ISNULL(n.CI,''))) CI,
                       CAST(ISNULL(l.ACTIVE_TAG,0) AS bit) ACTIVE_TAG,
                       CAST(CASE WHEN LEN(ISNULL(l.USER_PWD,'')) > 0 THEN 1 ELSE 0 END AS bit) HAS_PASSWORD,
                       LTRIM(RTRIM(ISNULL(l.LAST_UPDATE_BY,''))) LAST_UPDATE_BY,
                       l.LAST_UPDATE_DATE
                FROM dbo.SYSDL l WITH (NOLOCK)
                LEFT JOIN dbo.SYSDN n WITH (NOLOCK) ON l.EMP_ID = n.EMP_ID
            )
            SELECT USER_ID,EMP_ID,EMP_NAME,DEPT_ID,DEPT_DESC,CI,ACTIVE_TAG,HAS_PASSWORD,
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
            if (total == 0) total = Convert.ToInt32(reader.GetValue(11));
            items.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                reader.GetBoolean(6), reader.GetBoolean(7), NullIfEmpty(reader, 8),
                reader.IsDBNull(9) ? null : reader.GetDateTime(9)));
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

    /// <summary>
    /// 新增用户（开户，2306）：SYSDL 建号 + 初始密码（现代哈希）+ 可选所属组（可多组）。
    /// 校验：用户名格式（1-10 位字母数字下划线连字符）、初始密码策略、用户名唯一、
    /// 员工必须存在于 SYSDN 且尚未开户、所选用户组存在；写 AUDIT_EVENT 审计。
    /// </summary>
    public async Task CreateUserAsync(
        string userId, string employeeId, string password, IReadOnlyList<string>? groupIds,
        string adminName, CancellationToken token)
    {
        ValidateUserId(userId);
        PasswordPolicy.Validate(password);
        var id = userId.Trim();
        var emp = (employeeId ?? string.Empty).Trim();
        if (emp.Length == 0)
            throw new ArgumentException("请选择员工。", nameof(employeeId));
        var groups = (groupIds ?? [])
            .Select(groupId => (groupId ?? string.Empty).Trim())
            .Where(groupId => groupId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hash = PasswordHasher.Hash(password);

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // 用户名唯一（UPDLOCK/HOLDLOCK 防并发重复开户）
            await using (var exists = new SqlCommand(
                "SELECT COUNT(1) FROM dbo.SYSDL WITH (UPDLOCK,HOLDLOCK) WHERE LTRIM(RTRIM(USER_ID))=@UserId;",
                connection, transaction))
            {
                exists.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = id;
                if (Convert.ToInt32(await exists.ExecuteScalarAsync(token)) > 0)
                    throw new ArgumentException($"用户名 {id} 已存在，不能重复开户。", nameof(userId));
            }
            // 员工存在且未开户（fail-closed：一个员工一个账号）
            await using (var empCheck = new SqlCommand(
                """
                SELECT COUNT(1) FROM dbo.SYSDN WITH (NOLOCK) WHERE LTRIM(RTRIM(EMP_ID))=@Emp;
                """, connection, transaction))
            {
                empCheck.Parameters.Add("@Emp", SqlDbType.NChar, 10).Value = emp;
                if (Convert.ToInt32(await empCheck.ExecuteScalarAsync(token)) == 0)
                    throw new KeyNotFoundException($"员工 {emp} 不存在。");
            }
            await using (var accountCheck = new SqlCommand(
                "SELECT COUNT(1) FROM dbo.SYSDL WITH (UPDLOCK,HOLDLOCK) WHERE LTRIM(RTRIM(EMP_ID))=@Emp;",
                connection, transaction))
            {
                accountCheck.Parameters.Add("@Emp", SqlDbType.NChar, 10).Value = emp;
                if (Convert.ToInt32(await accountCheck.ExecuteScalarAsync(token)) > 0)
                    throw new ArgumentException($"员工 {emp} 已有登录账号，不能重复开户。", nameof(employeeId));
            }
            foreach (var group in groups)
            {
                await using var groupCheck = new SqlCommand(
                    "SELECT COUNT(1) FROM dbo.SYSDG WITH (NOLOCK) WHERE LTRIM(RTRIM(G_IDX))=@GroupId;",
                    connection, transaction);
                groupCheck.Parameters.Add("@GroupId", SqlDbType.NChar, 10).Value = group;
                if (Convert.ToInt32(await groupCheck.ExecuteScalarAsync(token)) == 0)
                    throw new KeyNotFoundException($"用户组 {group} 不存在。");
            }

            await using var insert = new SqlCommand(
                """
                INSERT INTO dbo.SYSDL (USER_ID,EMP_ID,USER_PWD,ACTIVE_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@UserId,@Emp,@Hash,1,@By,GETDATE(),@By,GETDATE());
                """, connection, transaction);
            insert.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = id;
            insert.Parameters.Add("@Emp", SqlDbType.NChar, 10).Value = emp;
            insert.Parameters.Add("@Hash", SqlDbType.NVarChar, 50).Value = hash;
            insert.Parameters.Add("@By", SqlDbType.NChar, 40).Value = adminName;
            await insert.ExecuteNonQueryAsync(token);

            // 用户与用户组是多对多关系，唯一落在 SYSDG_USER；用户表不再保存所属组
            foreach (var group in groups)
            {
                await using var link = new SqlCommand(
                    "INSERT INTO dbo.SYSDG_USER (G_IDX,USER_ID) VALUES (@GroupId,@UserId);",
                    connection, transaction);
                link.Parameters.Add("@GroupId", SqlDbType.NChar, 10).Value = group;
                link.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = id;
                await link.ExecuteNonQueryAsync(token);
            }

            await auditWriter.WriteAsync(
                connection, transaction, 2306, id, "USER_CREATE",
                groups.Count == 0
                    ? $"新增用户 {id}（员工 {emp}）"
                    : $"新增用户 {id}（员工 {emp}，所属组 {string.Join(',', groups)}）",
                adminName, token);

            await transaction.CommitAsync(token);
            permissionCache.InvalidateAll();
            logger.LogInformation("新增用户 userId={UserId} emp={Emp} groups={Groups} by={By}",
                id, emp, string.Join(',', groups), adminName);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private static void ValidateUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Trim().Length > 10 || !UserIdPattern.IsMatch(userId.Trim()))
            throw new ArgumentException("用户 ID 无效。", nameof(userId));
    }

    private static string? NullIfEmpty(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal).Trim();
}
