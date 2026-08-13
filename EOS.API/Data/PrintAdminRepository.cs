using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 页头/表尾/页脚维护（旧 2202/2203/2204 的受控等价）：
/// 固定表名、固定列白名单、值全部参数化；删除前校验是否被 REPORT 引用。
/// </summary>
public sealed class PrintAdminRepository(DbConnectionFactory connections)
{
    public async Task<List<PrintHeaderDraft>> ListHeadersAsync(CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(HEADER_ID)),LTRIM(RTRIM(ISNULL(HEADER_NAME,''))),
                   LTRIM(RTRIM(ISNULL(COMPANY_NAME,''))),LTRIM(RTRIM(ISNULL(COMPANY_NAME_EN,''))),
                   LTRIM(RTRIM(ISNULL(HEADER_TEXT,''))),LTRIM(RTRIM(ISNULL(LOGO_PATH,'')))
            FROM dbo.REPORT_HEADER WITH (NOLOCK) ORDER BY HEADER_ID;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<PrintHeaderDraft>();
        while (await reader.ReadAsync(token))
            result.Add(new PrintHeaderDraft(
                reader.GetString(0).Trim(), EmptyToNull(reader.GetString(1)), EmptyToNull(reader.GetString(2)),
                EmptyToNull(reader.GetString(3)), EmptyToNull(reader.GetString(4)), EmptyToNull(reader.GetString(5))));
        return result;
    }

    public async Task<List<PrintTailDraft>> ListTailsAsync(CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(TAIL_ID)),LTRIM(RTRIM(ISNULL(TAIL_NAME,''))),LTRIM(RTRIM(ISNULL(TAIL_TEXT,'')))
            FROM dbo.REPORT_TAIL WITH (NOLOCK) ORDER BY TAIL_ID;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<PrintTailDraft>();
        while (await reader.ReadAsync(token))
            result.Add(new PrintTailDraft(reader.GetString(0).Trim(), EmptyToNull(reader.GetString(1)), EmptyToNull(reader.GetString(2))));
        return result;
    }

    public async Task<List<PrintFooterDraft>> ListFootersAsync(CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(FOOTER_ID)),LTRIM(RTRIM(ISNULL(FOOTER_NAME,''))),LTRIM(RTRIM(ISNULL(FOOTER_TEXT,'')))
            FROM dbo.REPORT_FOOTER WITH (NOLOCK) ORDER BY FOOTER_ID;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<PrintFooterDraft>();
        while (await reader.ReadAsync(token))
            result.Add(new PrintFooterDraft(reader.GetString(0).Trim(), EmptyToNull(reader.GetString(1)), EmptyToNull(reader.GetString(2))));
        return result;
    }

    public async Task CreateHeaderAsync(PrintHeaderDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.REPORT_HEADER
                (HEADER_ID,HEADER_NAME,COMPANY_NAME,COMPANY_NAME_EN,HEADER_TEXT,LOGO_PATH,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES
                (@Id,@Name,@Company,@CompanyEn,@HeaderText,@LogoPath,@User,GETDATE(),@User,GETDATE());
            """;
        await ExecuteDraftAsync(sql, draft.HeaderId, draft.HeaderName, draft.CompanyName, draft.CompanyNameEn, draft.HeaderText, draft.LogoPath, user, token);
    }

    public async Task<bool> UpdateHeaderAsync(string id, PrintHeaderDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.REPORT_HEADER
            SET HEADER_NAME=@Name,COMPANY_NAME=@Company,COMPANY_NAME_EN=@CompanyEn,
                HEADER_TEXT=@HeaderText,LOGO_PATH=@LogoPath,LAST_UPDATE_BY=@User,LAST_UPDATE_DATE=GETDATE()
            WHERE HEADER_ID=@Id;
            """;
        return await ExecuteDraftAsync(sql, id, draft.HeaderName, draft.CompanyName, draft.CompanyNameEn, draft.HeaderText, draft.LogoPath, user, token) > 0;
    }

    public async Task<int> DeleteHeaderAsync(string id, CancellationToken token)
        => await DeleteAsync("REPORT_HEADER", "HEADER_ID", "REPORT", "HEADER_ID", id, token);

    public async Task CreateTailAsync(PrintTailDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.REPORT_TAIL (TAIL_ID,TAIL_NAME,TAIL_TEXT,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (@Id,@Name,@TailText,@User,GETDATE(),@User,GETDATE());
            """;
        await ExecuteTailAsync(sql, draft.TailId, draft.TailName, draft.TailText, user, token);
    }

    public async Task<bool> UpdateTailAsync(string id, PrintTailDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.REPORT_TAIL SET TAIL_NAME=@Name,TAIL_TEXT=@TailText,LAST_UPDATE_BY=@User,LAST_UPDATE_DATE=GETDATE()
            WHERE TAIL_ID=@Id;
            """;
        return await ExecuteTailAsync(sql, id, draft.TailName, draft.TailText, user, token) > 0;
    }

    public async Task<int> DeleteTailAsync(string id, CancellationToken token)
        => await DeleteAsync("REPORT_TAIL", "TAIL_ID", "REPORT", "TAIL_ID", id, token);

    public async Task CreateFooterAsync(PrintFooterDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.REPORT_FOOTER (FOOTER_ID,FOOTER_NAME,FOOTER_TEXT,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (@Id,@Name,@FooterText,@User,GETDATE(),@User,GETDATE());
            """;
        await ExecuteFooterAsync(sql, draft.FooterId, draft.FooterName, draft.FooterText, user, token);
    }

    public async Task<bool> UpdateFooterAsync(string id, PrintFooterDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.REPORT_FOOTER SET FOOTER_NAME=@Name,FOOTER_TEXT=@FooterText,LAST_UPDATE_BY=@User,LAST_UPDATE_DATE=GETDATE()
            WHERE FOOTER_ID=@Id;
            """;
        return await ExecuteFooterAsync(sql, id, draft.FooterName, draft.FooterText, user, token) > 0;
    }

    public async Task<int> DeleteFooterAsync(string id, CancellationToken token)
        => await DeleteAsync("REPORT_FOOTER", "FOOTER_ID", null, null, id, token);

    private async Task<int> ExecuteDraftAsync(
        string sql, string id, string? name, string? company, string? companyEn, string? headerText, string? logoPath,
        string user, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        AddCommon(command, id, name, user);
        command.Parameters.AddWithNullable("Company", SqlDbType.NVarChar, 200, company);
        command.Parameters.AddWithNullable("CompanyEn", SqlDbType.NVarChar, 200, companyEn);
        command.Parameters.AddWithNullable("HeaderText", SqlDbType.NVarChar, 4000, headerText);
        command.Parameters.AddWithNullable("LogoPath", SqlDbType.NVarChar, 200, logoPath);
        return await command.ExecuteNonQueryAsync(token);
    }

    private async Task<int> ExecuteTailAsync(
        string sql, string id, string? name, string? tailText, string user, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        AddCommon(command, id, name, user);
        command.Parameters.AddWithNullable("TailText", SqlDbType.NVarChar, 4000, tailText);
        return await command.ExecuteNonQueryAsync(token);
    }

    private async Task<int> ExecuteFooterAsync(
        string sql, string id, string? name, string? footerText, string user, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        AddCommon(command, id, name, user);
        command.Parameters.AddWithNullable("FooterText", SqlDbType.NVarChar, 4000, footerText);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static void AddCommon(SqlCommand command, string id, string? name, string user)
    {
        command.Parameters.Add("@Id", SqlDbType.NChar, 20).Value = id.Trim();
        command.Parameters.AddWithNullable("Name", SqlDbType.NVarChar, 200, name);
        command.Parameters.Add("@User", SqlDbType.NChar, 40).Value = user.Trim();
    }

    private async Task<int> DeleteAsync(
        string table, string idColumn, string? refTable, string? refColumn, string id, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        if (refTable is not null)
        {
            var check = new SqlCommand(
                $"SELECT 1 FROM dbo.{refTable} WITH (NOLOCK) WHERE {refColumn}=@Id;", connection);
            check.Parameters.Add("@Id", SqlDbType.NChar, 20).Value = id.Trim();
            if (await check.ExecuteScalarAsync(token) is not null) return 2;
        }
        var command = new SqlCommand($"DELETE FROM dbo.{table} WHERE {idColumn}=@Id;", connection);
        command.Parameters.Add("@Id", SqlDbType.NChar, 20).Value = id.Trim();
        return await command.ExecuteNonQueryAsync(token) > 0 ? 0 : 1;
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>页头/表尾/页脚编号校验（纯逻辑，便于单元测试）。</summary>
internal static class PrintAdminValidator
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_\\-]{1,20}$", RegexOptions.Compiled);

    public static void ValidateId(string? id, string label)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id.Trim()))
            throw new ArgumentException($"{label}编号格式无效（1-20 位字母/数字/下划线/连字符）。", nameof(id));
    }
}
