using System.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed class AuthenticationRepository(IConfiguration configuration)
{
    public async Task<LoginResult> AuthenticateAsync(string userId, string password, CancellationToken token)
    {
        const string sql = """
            SELECT l.USER_ID,l.G_IDX,l.USER_PWD,l.EMP_ID,n.EMP_NAME,n.DEPT_ID,
                   dbo.f_get_dept_desc(n.DEPT_ID) AS DEPT_DESC,n.COMPANY_ID,l.ACTIVE_TAG
            FROM dbo.SYSDL l WITH (NOLOCK)
            INNER JOIN dbo.SYSDN n WITH (NOLOCK) ON l.EMP_ID=n.EMP_ID
            WHERE l.USER_ID=@UserId;
            """;
        await using var connection = new SqlConnection(configuration.GetConnectionString("ErpDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:ErpDatabase 未配置。"));
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return new(LoginFailure.UserNotFound, null);
        if (!LegacyPassword.Verify(reader.GetString("USER_PWD"), password)) return new(LoginFailure.InvalidPassword, null);
        if (!reader.GetBoolean("ACTIVE_TAG")) return new(LoginFailure.Disabled, null);
        return new(LoginFailure.None, new LoginUser(
            reader.GetString("USER_ID").Trim(), reader.GetString("EMP_ID").Trim(),
            reader.GetString("EMP_NAME").Trim(), reader.GetString("DEPT_ID").Trim(),
            reader.GetNullableString("DEPT_DESC") ?? string.Empty,
            reader.GetString("COMPANY_ID").Trim(), reader.GetNullableString("G_IDX") ?? string.Empty));
    }
}
