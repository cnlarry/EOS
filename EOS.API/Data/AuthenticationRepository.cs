using System.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed class AuthenticationRepository(DbConnectionFactory connections, ILogger<AuthenticationRepository> logger)
{
    public async Task<LoginResult> AuthenticateAsync(string userId, string password, CancellationToken token)
    {
        var normalizedUserId = userId.Trim();
        logger.LogInformation("登录尝试 userId={UserId}", normalizedUserId);

        const string sql = """
            SELECT l.USER_ID,l.G_IDX,l.USER_PWD,l.EMP_ID,n.EMP_NAME,n.DEPT_ID,
                   dbo.f_get_dept_desc(n.DEPT_ID) AS DEPT_DESC,n.COMPANY_ID,l.ACTIVE_TAG
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
        if (!LegacyPassword.Verify(reader.GetString("USER_PWD"), password))
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
            reader.GetString("COMPANY_ID").Trim(), reader.GetNullableString("G_IDX") ?? string.Empty);
        logger.LogInformation("登录成功 userId={UserId} employee={EmployeeName}", user.UserId, user.EmployeeName);
        return new(LoginFailure.None, user);
    }
}
