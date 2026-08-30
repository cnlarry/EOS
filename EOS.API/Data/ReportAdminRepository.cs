using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 报表定义维护（旧 2201/229801 的受控等价）：
/// REPORT 主子表 + REPORT_SORT 方案，固定列白名单、标识符/字段串校验、值参数化。
/// 字段选择器选项来自 MODULES + FIELDS 元数据（物理列存在校验）。
/// </summary>
public sealed class ReportAdminRepository(DbConnectionFactory connections)
{
    public async Task<List<ReportAdminModuleOption>> ListModulesAsync(CancellationToken token)
    {
        const string sql = """
            SELECT DISTINCT m.M_IDX,LTRIM(RTRIM(ISNULL(m.M_DESC,'')))
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE ISNULL(m.M_TAG,1)=1
              AND (LTRIM(RTRIM(ISNULL(m.M_URL,''))) LIKE 'RPT/%'
                   OR LTRIM(RTRIM(ISNULL(m.M_URL,''))) LIKE '~/RPT/%'
                   OR EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK) WHERE r.R_M_IDX=m.M_IDX))
            ORDER BY m.M_IDX;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportAdminModuleOption>();
        while (await reader.ReadAsync(token))
            result.Add(new ReportAdminModuleOption(reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    public async Task<List<ReportAdminDraft>> ListReportsAsync(int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(REPORT_ID)),LTRIM(RTRIM(ISNULL(REPORT_NAME,''))),R_M_IDX,
                   LTRIM(RTRIM(ISNULL(ISO_NO,''))),LTRIM(RTRIM(ISNULL(HEADER_ID,''))),LTRIM(RTRIM(ISNULL(TAIL_ID,''))),
                   LTRIM(RTRIM(ISNULL(FOOTER_TEXT,''))),ISNULL(IS_DEFAULT,0),
                   LTRIM(RTRIM(ISNULL(REPORT_FILTER,''))),LTRIM(RTRIM(ISNULL(REMARK,'')))
            FROM dbo.REPORT WITH (NOLOCK) WHERE R_M_IDX=@ModuleId ORDER BY IS_DEFAULT DESC,REPORT_ID;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportAdminDraft>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new ReportAdminDraft(
                reader.GetString(0).Trim(), EmptyToNull(reader.GetString(1)), reader.GetInt32(2),
                EmptyToNull(reader.GetString(3)), EmptyToNull(reader.GetString(4)), EmptyToNull(reader.GetString(5)),
                EmptyToNull(reader.GetString(6)), reader.GetBoolean(7),
                EmptyToNull(reader.GetString(8)), EmptyToNull(reader.GetString(9))));
        }
        return result;
    }

    /// <summary>全部报表定义（不分模块）：2201 定制页以单一列表呈现全部模块报表。</summary>
    public async Task<List<ReportAdminDraft>> ListAllReportsAsync(CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(REPORT_ID)),LTRIM(RTRIM(ISNULL(REPORT_NAME,''))),R_M_IDX,
                   LTRIM(RTRIM(ISNULL(ISO_NO,''))),LTRIM(RTRIM(ISNULL(HEADER_ID,''))),LTRIM(RTRIM(ISNULL(TAIL_ID,''))),
                   LTRIM(RTRIM(ISNULL(FOOTER_TEXT,''))),ISNULL(IS_DEFAULT,0),
                   LTRIM(RTRIM(ISNULL(REPORT_FILTER,''))),LTRIM(RTRIM(ISNULL(REMARK,'')))
            FROM dbo.REPORT WITH (NOLOCK) ORDER BY R_M_IDX,IS_DEFAULT DESC,REPORT_ID;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportAdminDraft>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new ReportAdminDraft(
                reader.GetString(0).Trim(), EmptyToNull(reader.GetString(1)), reader.GetInt32(2),
                EmptyToNull(reader.GetString(3)), EmptyToNull(reader.GetString(4)), EmptyToNull(reader.GetString(5)),
                EmptyToNull(reader.GetString(6)), reader.GetBoolean(7),
                EmptyToNull(reader.GetString(8)), EmptyToNull(reader.GetString(9))));
        }
        return result;
    }

    public async Task CreateReportAsync(ReportAdminDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.REPORT
                (REPORT_ID,REPORT_NAME,R_M_IDX,Q_M_IDX,ISO_NO,HEADER_ID,FOOTER_TEXT,TAIL_ID,
                 IS_DEFAULT,REMARK,REPORT_FILTER,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES
                (@ReportId,@ReportName,@ModuleId,@ModuleId,@IsoNo,@HeaderId,@FooterText,@TailId,
                 @IsDefault,@Remark,@ReportFilter,@User,GETDATE(),@User,GETDATE());
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        AddReportParameters(command, draft, user);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<bool> UpdateReportAsync(string reportId, ReportAdminDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.REPORT
            SET REPORT_NAME=@ReportName,ISO_NO=@IsoNo,HEADER_ID=@HeaderId,FOOTER_TEXT=@FooterText,TAIL_ID=@TailId,
                IS_DEFAULT=@IsDefault,REMARK=@Remark,REPORT_FILTER=@ReportFilter,
                LAST_UPDATE_BY=@User,LAST_UPDATE_DATE=GETDATE()
            WHERE REPORT_ID=@ReportId;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 100).Value = reportId.Trim();
        AddReportParameters(command, draft, user, includeModule: false);
        return await command.ExecuteNonQueryAsync(token) > 0;
    }

    public async Task DeleteReportAsync(string reportId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using (var sortCommand = new SqlCommand("DELETE FROM dbo.REPORT_SORT WHERE REPORT_ID=@ReportId;", connection))
        {
            sortCommand.Parameters.Add("@ReportId", SqlDbType.NChar, 100).Value = reportId.Trim();
            await sortCommand.ExecuteNonQueryAsync(token);
        }
        await using var command = new SqlCommand("DELETE FROM dbo.REPORT WHERE REPORT_ID=@ReportId;", connection);
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 100).Value = reportId.Trim();
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<int?> GetReportModuleAsync(string reportId, CancellationToken token)
    {
        const string sql = "SELECT R_M_IDX FROM dbo.REPORT WITH (NOLOCK) WHERE REPORT_ID=@ReportId;";
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 100).Value = reportId.Trim();
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    public async Task<List<ReportSortDraft>> ListSortsAsync(string reportId, CancellationToken token)
    {
        const string sql = """
            SELECT SERIAL_NO,LTRIM(RTRIM(ISNULL(SORT_NAME,''))),LTRIM(RTRIM(ISNULL(SORT_FIELDS,''))),
                   LTRIM(RTRIM(ISNULL(SORT_DESC,''))),LTRIM(RTRIM(ISNULL(GROUP_NAME,''))),
                   LTRIM(RTRIM(ISNULL(GROUP_FIELDS,''))),LTRIM(RTRIM(ISNULL(GROUP_DESC,'')))
            FROM dbo.REPORT_SORT WITH (NOLOCK) WHERE REPORT_ID=@ReportId ORDER BY SERIAL_NO;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 100).Value = reportId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportSortDraft>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new ReportSortDraft(
                Convert.ToInt32(reader.GetValue(0)), EmptyToNull(reader.GetString(1)), EmptyToNull(reader.GetString(2)),
                EmptyToNull(reader.GetString(3)), EmptyToNull(reader.GetString(4)), EmptyToNull(reader.GetString(5)),
                EmptyToNull(reader.GetString(6))));
        }
        return result;
    }

    public async Task CreateSortAsync(string reportId, ReportSortDraft draft, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.REPORT_SORT (REPORT_ID,SERIAL_NO,SORT_NAME,SORT_FIELDS,SORT_DESC,GROUP_NAME,GROUP_FIELDS,GROUP_DESC)
            VALUES (@ReportId,@SerialNo,@SortName,@SortFields,@SortDesc,@GroupName,@GroupFields,@GroupDesc);
            """;
        await ExecuteSortAsync(sql, reportId, draft, token);
    }

    public async Task<bool> UpdateSortAsync(string reportId, int serialNo, ReportSortDraft draft, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.REPORT_SORT
            SET SORT_NAME=@SortName,SORT_FIELDS=@SortFields,SORT_DESC=@SortDesc,
                GROUP_NAME=@GroupName,GROUP_FIELDS=@GroupFields,GROUP_DESC=@GroupDesc
            WHERE REPORT_ID=@ReportId AND SERIAL_NO=@SerialNo;
            """;
        return await ExecuteSortAsync(sql, reportId, draft, token, serialNo) > 0;
    }

    public async Task DeleteSortAsync(string reportId, int serialNo, CancellationToken token)
    {
        const string sql = "DELETE FROM dbo.REPORT_SORT WHERE REPORT_ID=@ReportId AND SERIAL_NO=@SerialNo;";
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 100).Value = reportId.Trim();
        command.Parameters.Add("@SerialNo", SqlDbType.SmallInt).Value = (short)serialNo;
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<List<ReportAdminFieldOption>> FieldOptionsAsync(int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT t.T_ID,c.F_ID,COALESCE(NULLIF(LTRIM(RTRIM(c.F_DESC)),''),LTRIM(RTRIM(c.F_ID)))
            FROM (
                SELECT LTRIM(RTRIM(MASTER_TABLE)) T_ID FROM dbo.MODULES WHERE M_IDX=@ModuleId
                UNION ALL
                SELECT LTRIM(RTRIM(DETAIL_TABLE)) FROM dbo.MODULES
                WHERE M_IDX=@ModuleId AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE,'')))<>''
            ) t
            INNER JOIN dbo.FIELDS c WITH (NOLOCK) ON c.T_ID=t.T_ID AND COALESCE(c.IS_VIRTUAL,0)=0
            WHERE EXISTS (SELECT 1 FROM sys.columns col
                          JOIN sys.objects o ON col.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=t.T_ID AND col.name=c.F_ID)
            ORDER BY t.T_ID,c.F_ID;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportAdminFieldOption>();
        while (await reader.ReadAsync(token))
            result.Add(new ReportAdminFieldOption(reader.GetString(0).Trim(), reader.GetString(1).Trim(), reader.GetString(2)));
        return result;
    }

    public async Task<List<ReportAdminHeaderTailOption>> HeaderOptionsAsync(CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(LAYOUT_ID)),LTRIM(RTRIM(ISNULL(LAYOUT_DESC,LAYOUT_ID)))
            FROM dbo.REPORT_LAYOUT WITH (NOLOCK)
            WHERE KIND=N'HEADER' ORDER BY LAYOUT_ID;
            """;
        return await ReadIdNameAsync(sql, token);
    }

    public async Task<List<ReportAdminHeaderTailOption>> TailOptionsAsync(CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(LAYOUT_ID)),LTRIM(RTRIM(ISNULL(LAYOUT_DESC,LAYOUT_ID)))
            FROM dbo.REPORT_LAYOUT WITH (NOLOCK)
            WHERE KIND=N'TAIL' ORDER BY LAYOUT_ID;
            """;
        return await ReadIdNameAsync(sql, token);
    }

    private async Task<List<ReportAdminHeaderTailOption>> ReadIdNameAsync(string sql, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportAdminHeaderTailOption>();
        while (await reader.ReadAsync(token))
            result.Add(new ReportAdminHeaderTailOption(reader.GetString(0).Trim(), reader.GetString(1)));
        return result;
    }

    private async Task<int> ExecuteSortAsync(
        string sql, string reportId, ReportSortDraft draft, CancellationToken token, int? serialNo = null)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 100).Value = reportId.Trim();
        command.Parameters.Add("@SerialNo", SqlDbType.SmallInt).Value = (short)(serialNo ?? draft.SerialNo);
        command.Parameters.AddWithNullable("SortName", SqlDbType.NVarChar, 100, draft.SortName);
        command.Parameters.AddWithNullable("SortFields", SqlDbType.NVarChar, 1000, draft.SortFields);
        command.Parameters.AddWithNullable("SortDesc", SqlDbType.NVarChar, 2000, draft.SortDesc);
        command.Parameters.AddWithNullable("GroupName", SqlDbType.NVarChar, 100, draft.GroupName);
        command.Parameters.AddWithNullable("GroupFields", SqlDbType.NVarChar, 1000, draft.GroupFields);
        command.Parameters.AddWithNullable("GroupDesc", SqlDbType.NVarChar, 2000, draft.GroupDesc);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static void AddReportParameters(SqlCommand command, ReportAdminDraft draft, string user, bool includeModule = true)
    {
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 100).Value = draft.ReportId.Trim();
        command.Parameters.AddWithNullable("ReportName", SqlDbType.NVarChar, 200, draft.ReportName);
        if (includeModule) command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = draft.ModuleId ?? 0;
        command.Parameters.AddWithNullable("IsoNo", SqlDbType.NVarChar, 100, draft.IsoNo);
        command.Parameters.AddWithNullable("HeaderId", SqlDbType.NChar, 20, draft.HeaderId);
        command.Parameters.AddWithNullable("FooterText", SqlDbType.NVarChar, 4000, draft.FooterText);
        command.Parameters.AddWithNullable("TailId", SqlDbType.NChar, 20, draft.TailId);
        command.Parameters.Add("@IsDefault", SqlDbType.Bit).Value = draft.IsDefault;
        command.Parameters.AddWithNullable("Remark", SqlDbType.NVarChar, 1000, draft.Remark);
        command.Parameters.AddWithNullable("ReportFilter", SqlDbType.NVarChar, 1000, draft.ReportFilter);
        command.Parameters.Add("@User", SqlDbType.NChar, 40).Value = user.Trim();
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
