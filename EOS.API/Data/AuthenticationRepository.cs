using System.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

using EOS.API.Telemetry;
namespace EOS.API.Data;

public sealed class AuthenticationRepository(DbConnectionFactory connections, ILogger<AuthenticationRepository> logger)
{
    public async Task<LoginResult> AuthenticateAsync(string userId, string password, CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        var normalizedUserId = userId.Trim();
        logger.LogInformation("登录尝试 userId={UserId}", normalizedUserId);

        const string sql = """
            SELECT l.USER_ID,
                   -- 主组取自用户-组关联表（G_IDX 最小者）。
                   -- 用户与用户组是多对多关系，账号表不承载"那个组"，只有关联表是它的唯一存储。
                   (SELECT TOP 1 LTRIM(RTRIM(gu.G_IDX)) FROM dbo.SYSDG_USER gu WITH (NOLOCK)
                     WHERE LTRIM(RTRIM(gu.USER_ID))=LTRIM(RTRIM(l.USER_ID)) ORDER BY gu.G_IDX) AS G_IDX,
                   l.USER_PWD,l.EMP_ID,n.EMP_NAME,n.DEPT_ID,
                   dbo.f_get_dept_desc(n.DEPT_ID) AS DEPT_DESC,n.CI,l.ACTIVE_TAG
            FROM dbo.SYSDL l WITH (NOLOCK)
            INNER JOIN dbo.SYSDN n WITH (NOLOCK) ON l.EMP_ID=n.EMP_ID
            WHERE l.USER_ID=@UserId;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = normalizedUserId;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            logger.LogWarning("登录失败 userId={UserId} reason=UserNotFound", normalizedUserId);
            return new(LoginFailure.UserNotFound, null);
        }
        var storedHash = reader.IsDBNull(reader.GetOrdinal("USER_PWD")) ? null : reader.GetString("USER_PWD");
        if (!PasswordHasher.Verify(storedHash, password))
        {
            logger.LogWarning("登录失败 userId={UserId} reason=InvalidPassword", normalizedUserId);
            return new(LoginFailure.InvalidPassword, null);
        }
        if (!reader.GetBoolean("ACTIVE_TAG"))
        {
            logger.LogWarning("登录失败 userId={UserId} reason=Disabled", normalizedUserId);
            return new(LoginFailure.Disabled, null);
        }
        var user = new LoginUser(
            reader.GetString("USER_ID").Trim(), reader.GetString("EMP_ID").Trim(),
            reader.GetString("EMP_NAME").Trim(), reader.GetString("DEPT_ID").Trim(),
            reader.GetNullableString("DEPT_DESC") ?? string.Empty,
            reader.GetString("CI").Trim(), reader.GetNullableString("G_IDX") ?? string.Empty);
        logger.LogInformation("登录成功 userId={UserId} employee={EmployeeName}", user.UserId, user.EmployeeName);
        return new(LoginFailure.None, user);
    }

    /// <summary>
    /// 用户自助修改密码：必须验证当前密码，成功后写入现代哈希并记录审计。
    /// </summary>
    public async Task<PasswordChangeResult> ChangePasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        string updatedBy,
        CancellationToken token)
    {
        const string selectSql = """
            SELECT USER_PWD
            FROM dbo.SYSDL WITH (NOLOCK)
            WHERE LTRIM(RTRIM(USER_ID))=@UserId;
            """;
        await using var connection = connections.Create();
        await using var select = new SqlCommand(selectSql, connection);
        select.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        await connection.OpenAsync(token);
        var storedHash = await select.ExecuteScalarAsync(token) as string;
        if (storedHash is null)
            return new(PasswordChangeFailure.UserNotFound);
        if (string.IsNullOrEmpty(storedHash))
            return new(PasswordChangeFailure.NoPasswordSet);
        if (!PasswordHasher.Verify(storedHash, currentPassword))
            return new(PasswordChangeFailure.WrongCurrentPassword);
        try
        {
            PasswordPolicy.Validate(newPassword);
        }
        catch (ArgumentException)
        {
            return new(PasswordChangeFailure.InvalidNewPassword);
        }

        var hash = PasswordHasher.Hash(newPassword);
        const string updateSql = """
            UPDATE dbo.SYSDL
            SET USER_PWD=@Hash, LAST_UPDATE_BY=@UpdatedBy, LAST_UPDATE_DATE=GETDATE()
            WHERE LTRIM(RTRIM(USER_ID))=@UserId;
            """;
        await using var update = new SqlCommand(updateSql, connection);
        update.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        update.Parameters.Add("@Hash", SqlDbType.NVarChar, 50).Value = hash;
        update.Parameters.Add("@UpdatedBy", SqlDbType.NChar, 20).Value = updatedBy;
        if (await update.ExecuteNonQueryAsync(token) != 1)
            return new(PasswordChangeFailure.UserNotFound);
        logger.LogInformation("用户自助修改密码 userId={UserId} by={UpdatedBy}", userId.Trim(), updatedBy);
        return new(PasswordChangeFailure.None);
    }
}