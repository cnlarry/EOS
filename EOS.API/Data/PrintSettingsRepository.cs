using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

using EOS.API.Telemetry;
namespace EOS.API.Data;

/// <summary>
/// 报表打印设置（旧 RptParent.master 面板的受控等价）：
/// - 报表清单按报表级预览权限过滤（SYSDD_REPORT 个人覆盖 SYSDH_REPORT 组，组标签取 OR）；
/// - 页头/表尾来自 REPORT_LAYOUT；
/// - 排序/分组方案来自 REPORT_SORT（字段串仅服务端消费，白名单校验后进入查询）；
/// - 用户最近设置读写 SYSQR（IS_LAST=1），字段值全部参数化。
/// </summary>
public sealed class PrintSettingsRepository(DbConnectionFactory connections, ILogger<PrintSettingsRepository> logger)
{
    public async Task<ReportPrintSettings> GetAsync(int moduleId, string userId, CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var reports = await ReadReportsAsync(connection, moduleId, userId, token);
        var headers = await ReadHeadersAsync(connection, token);
        var tails = await ReadTailsAsync(connection, token);
        var sortSchemes = await ReadSortSchemesAsync(connection, token);
        var userSettings = await ReadUserSettingsAsync(connection, moduleId, userId, token);
        logger.LogDebug("打印设置 module={ModuleId} reports={ReportCount} headers={HeaderCount} tails={TailCount}",
            moduleId, reports.Count, headers.Count, tails.Count);
        return new ReportPrintSettings(moduleId, reports, headers, tails, sortSchemes, userSettings);
    }

    /// <summary>保存最近一次打印设置（SYSQR upsert，IS_LAST=1 语义）。</summary>
    public async Task SaveAsync(int moduleId, string userId, ReportPrintSettingsRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.ReportId))
            throw new ArgumentException("未选择报表。", nameof(request.ReportId));
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            UPDATE dbo.SYSQR SET IS_LAST=0 WHERE USER_ID=@UserId AND R_M_IDX=@ModuleId;
            IF EXISTS (SELECT 1 FROM dbo.SYSQR WHERE USER_ID=@UserId AND R_M_IDX=@ModuleId AND REPORT_ID=@ReportId)
                UPDATE dbo.SYSQR
                SET HEADER_ID=@HeaderId,TAIL_ID=@TailId,LAST_SORT=@LastSort,SORT_ASC=@SortAsc,
                    SHOW_GROUP=@ShowGroup,SHOW_DETAIL=@ShowDetail,IS_LAST=1
                WHERE USER_ID=@UserId AND R_M_IDX=@ModuleId AND REPORT_ID=@ReportId;
            ELSE
                INSERT INTO dbo.SYSQR
                    (USER_ID,R_M_IDX,REPORT_ID,HEADER_ID,TAIL_ID,LAST_SORT,SORT_ASC,SHOW_GROUP,SHOW_DETAIL,IS_LAST,PRINTER_NAME,PAPER_SIZE)
                VALUES
                    (@UserId,@ModuleId,@ReportId,@HeaderId,@TailId,@LastSort,@SortAsc,@ShowGroup,@ShowDetail,1,'','');
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = request.ReportId.Trim();
        command.Parameters.AddWithNullable("@HeaderId", SqlDbType.NChar, 10, request.HeaderId);
        command.Parameters.AddWithNullable("@TailId", SqlDbType.NChar, 10, request.TailId);
        command.Parameters.AddWithNullable("@LastSort", SqlDbType.NVarChar, 50, request.SortSerialNo?.ToString());
        command.Parameters.Add("@SortAsc", SqlDbType.Bit).Value = request.SortAsc;
        command.Parameters.Add("@ShowGroup", SqlDbType.Bit).Value = request.ShowGroup;
        command.Parameters.Add("@ShowDetail", SqlDbType.Bit).Value = request.ShowDetail;
        await command.ExecuteNonQueryAsync(token);

        // 用户条件记忆（SYSQR_USER）：范围条件存 "from☆to"，其余存单值；F_TAG=1
        await using (var deleteConditions = new SqlCommand(
            "DELETE FROM dbo.SYSQR_USER WHERE USER_ID=@UserId AND M_IDX=@ModuleId;", connection))
        {
            deleteConditions.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
            deleteConditions.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await deleteConditions.ExecuteNonQueryAsync(token);
        }
        var values = request.Values ?? new Dictionary<int, string?>();
        var valuesTo = request.ValuesTo ?? new Dictionary<int, string?>();
        foreach (var (serial, raw) in values)
        {
            var fromValue = (raw ?? string.Empty).Trim();
            var toValue = (valuesTo.GetValueOrDefault(serial) ?? string.Empty).Trim();
            if (fromValue.Length == 0 && toValue.Length == 0) continue;
            var storedValue = toValue.Length > 0 ? $"{fromValue}☆{toValue}" : fromValue;
            await using var insertCondition = new SqlCommand(
                "INSERT INTO dbo.SYSQR_USER (USER_ID,M_IDX,SERIAL_NO,F_VALUE,F_TAG) VALUES (@UserId,@ModuleId,@SerialNo,@FValue,1);", connection);
            insertCondition.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
            insertCondition.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            insertCondition.Parameters.Add("@SerialNo", SqlDbType.Int).Value = serial;
            insertCondition.Parameters.Add("@FValue", SqlDbType.NVarChar, 1000).Value = storedValue;
            await insertCondition.ExecuteNonQueryAsync(token);
        }
    }

    /// <summary>PDF 渲染元数据：报表定义 + 页头/表尾 + 排序方案（均来自服务端白名单表）。</summary>
    public async Task<ReportPdfMeta?> GetPdfMetaAsync(int moduleId, string reportId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string reportSql = """
            SELECT LTRIM(RTRIM(r.REPORT_ID)),LTRIM(RTRIM(ISNULL(r.REPORT_NAME,r.REPORT_ID))),
                   LTRIM(RTRIM(ISNULL(r.HEADER_ID,''))),LTRIM(RTRIM(ISNULL(r.TAIL_ID,''))),
                   LTRIM(RTRIM(ISNULL(r.FOOTER_TEXT,''))),LTRIM(RTRIM(ISNULL(r.ISO_NO,''))),
                   LTRIM(RTRIM(ISNULL(r.DEFAULT_PAPER,''))),LTRIM(RTRIM(ISNULL(r.REPORT_FILTER,'')))
            FROM dbo.REPORT r WITH (NOLOCK)
            WHERE r.R_M_IDX=@ModuleId AND r.REPORT_ID=@ReportId;
            """;
        await using var reportCommand = new SqlCommand(reportSql, connection);
        reportCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        reportCommand.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = reportId.Trim();
        await using var reportReader = await reportCommand.ExecuteReaderAsync(token);
        if (!await reportReader.ReadAsync(token)) return null;
        var metaReportId = reportReader.GetString(0).Trim();
        var reportName = reportReader.GetString(1);
        var defaultHeaderId = reportReader.GetString(2);
        var defaultTailId = reportReader.GetString(3);
        var footerText = reportReader.GetString(4);
        var isoNo = reportReader.GetString(5);
        var defaultPaper = reportReader.GetString(6);
        var reportFilter = reportReader.GetString(7);
        await reportReader.DisposeAsync();

        ReportHeaderOption? header = null;
        var headerId = string.IsNullOrWhiteSpace(defaultHeaderId) ? "DEFAULT" : defaultHeaderId;
        const string headerSql = """
            SELECT LTRIM(RTRIM(LAYOUT_ID)),LTRIM(RTRIM(ISNULL(LAYOUT_DESC,LAYOUT_ID))),
                   LTRIM(RTRIM(ISNULL(CONTENT,''))),LTRIM(RTRIM(ISNULL(IMAGE_PATH,'')))
            FROM dbo.REPORT_LAYOUT WITH (NOLOCK)
            WHERE KIND=N'HEADER' AND LAYOUT_ID=@HeaderId;
            """;
        await using (var headerCommand = new SqlCommand(headerSql, connection))
        {
            headerCommand.Parameters.Add("@HeaderId", SqlDbType.NChar, 50).Value = headerId;
            await using var headerReader = await headerCommand.ExecuteReaderAsync(token);
            if (await headerReader.ReadAsync(token))
            {
                var id = headerReader.GetString(0).Trim();
                var name = headerReader.GetString(1);
                var contentJson = headerReader.GetString(2);
                var logoPath = headerReader.IsDBNull(3) ? null : headerReader.GetString(3).Trim();
                var (company, companyEn, headerText) = ParseHeaderJson(contentJson);
                header = new ReportHeaderOption(id, name, company ?? string.Empty, companyEn, headerText, logoPath, LogoUrl(logoPath ?? string.Empty));
            }
        }

        string? tailText = null;
        if (!string.IsNullOrWhiteSpace(defaultTailId))
        {
            const string tailSql = """
                SELECT LTRIM(RTRIM(ISNULL(CONTENT,'')))
                FROM dbo.REPORT_LAYOUT WITH (NOLOCK) WHERE KIND=N'TAIL' AND LAYOUT_ID=@TailId;
                """;
            await using var tailCommand = new SqlCommand(tailSql, connection);
            tailCommand.Parameters.Add("@TailId", SqlDbType.NChar, 10).Value = defaultTailId;
            tailText = await tailCommand.ExecuteScalarAsync(token) as string;
            tailText = string.IsNullOrWhiteSpace(tailText) ? null : tailText.Trim();
        }

        var schemes = await ReadSortSchemesForReportAsync(connection, metaReportId, token);
        return new ReportPdfMeta(
            metaReportId, reportName, defaultHeaderId.Length == 0 ? null : defaultHeaderId,
            defaultTailId.Length == 0 ? null : defaultTailId,
            footerText.Length == 0 ? null : footerText,
            isoNo.Length == 0 ? null : isoNo,
            defaultPaper.Length == 0 ? null : defaultPaper,
            reportFilter.Length == 0 ? null : reportFilter,
            header, tailText, schemes);
    }

    /// <summary>
    /// 报表清单（ADR-009 §2 语义：模块 REPORT_TAG 唯一真源，SYSDD_REPORT/SYSDH_REPORT 降级为 override 收紧）。
    /// 模块级 REPORT_TAG=1 → 默认全部可见，除非存在 override 收紧（个人 SYSDD_REPORT 或组 SYSDH_REPORT 的 PREVIEW_TAG=0）。
    /// 个人 override 覆盖组 override（有个人行即按个人收紧，不再看组）。
    /// </summary>
    private static async Task<List<ReportPrintOption>> ReadReportsAsync(
        SqlConnection connection, int moduleId, string userId, CancellationToken token)
    {
        if (!await GetModuleReportTagAsync(connection, userId, moduleId, token)) return [];
        const string sql = """
            SELECT r.REPORT_ID,LTRIM(RTRIM(ISNULL(r.REPORT_NAME,r.REPORT_ID))),
                   LTRIM(RTRIM(ISNULL(r.HEADER_ID,''))),LTRIM(RTRIM(ISNULL(r.TAIL_ID,''))),
                   LTRIM(RTRIM(ISNULL(r.FOOTER_TEXT,''))),LTRIM(RTRIM(ISNULL(r.ISO_NO,''))),
                   LTRIM(RTRIM(ISNULL(r.DEFAULT_PAPER,''))),ISNULL(r.IS_DEFAULT,0)
            FROM dbo.REPORT r WITH (NOLOCK)
            WHERE r.R_M_IDX=@ModuleId
              -- 个人 override 收紧（PREVIEW_TAG=0 → 隐藏）
              AND NOT EXISTS (
                SELECT 1 FROM dbo.SYSDD_REPORT p WITH (NOLOCK)
                WHERE p.USER_ID=@UserId AND p.M_IDX=@ModuleId AND p.REPORT_ID=r.REPORT_ID
                  AND ISNULL(p.PREVIEW_TAG,0)=0)
              -- 无个人 override 时：组 OR（任一组 PREVIEW=1 → 可见）；
              -- 无任何 override 行 → 默认可见（跟随模块 REPORT_TAG）
              AND (
                EXISTS (SELECT 1 FROM dbo.SYSDD_REPORT p WITH (NOLOCK)
                        WHERE p.USER_ID=@UserId AND p.M_IDX=@ModuleId AND p.REPORT_ID=r.REPORT_ID)
                OR NOT EXISTS (SELECT 1 FROM dbo.SYSDH_REPORT g WITH (NOLOCK)
                               INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX=g.G_IDX
                               WHERE gu.USER_ID=@UserId AND g.M_IDX=@ModuleId AND g.REPORT_ID=r.REPORT_ID)
                OR EXISTS (SELECT 1 FROM dbo.SYSDH_REPORT g WITH (NOLOCK)
                           INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX=g.G_IDX
                           WHERE gu.USER_ID=@UserId AND g.M_IDX=@ModuleId AND g.REPORT_ID=r.REPORT_ID
                             AND ISNULL(g.PREVIEW_TAG,0)=1)
              )
            ORDER BY ISNULL(r.IS_DEFAULT,0) DESC,r.REPORT_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportPrintOption>();
        while (await reader.ReadAsync(token))
        {
            var reportId = reader.GetString(0).Trim();
            result.Add(new ReportPrintOption(
                reportId,
                reader.GetString(1),
                EmptyToNull(reader.GetString(2)),
                EmptyToNull(reader.GetString(3)),
                EmptyToNull(reader.GetString(4)),
                EmptyToNull(reader.GetString(5)),
                EmptyToNull(reader.GetString(6)),
                reader.GetBoolean(7)));
        }
        return result;
    }

    /// <summary>模块级报表可见性唯一真源（ADR-009 §2）：SYSDD.REPORT_TAG 个人优先，否则 SYSDH.REPORT_TAG 组 OR。</summary>
    private static async Task<bool> GetModuleReportTagAsync(
        SqlConnection connection, string userId, int moduleId, CancellationToken token)
    {
        const string personalSql = """
            SELECT ISNULL(REPORT_TAG,0) FROM dbo.SYSDD WITH (NOLOCK)
            WHERE USER_ID=@UserId AND M_IDX=@ModuleId;
            """;
        await using (var personalCommand = new SqlCommand(personalSql, connection))
        {
            personalCommand.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
            personalCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            var personal = await personalCommand.ExecuteScalarAsync(token);
            if (personal is not null && personal is not DBNull)
            {
                return Convert.ToBoolean(personal);
            }
        }

        const string groupSql = """
            SELECT TOP 1 1 FROM dbo.SYSDH h WITH (NOLOCK)
            INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX=h.G_IDX
            WHERE gu.USER_ID=@UserId AND h.M_IDX=@ModuleId AND ISNULL(h.REPORT_TAG,0)=1;
            """;
        await using var groupCommand = new SqlCommand(groupSql, connection);
        groupCommand.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        groupCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return await groupCommand.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<List<ReportHeaderOption>> ReadHeadersAsync(SqlConnection connection, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(LAYOUT_ID)),LTRIM(RTRIM(ISNULL(LAYOUT_DESC,LAYOUT_ID))),
                   LTRIM(RTRIM(ISNULL(CONTENT,''))),LTRIM(RTRIM(ISNULL(IMAGE_PATH,'')))
            FROM dbo.REPORT_LAYOUT WITH (NOLOCK)
            WHERE KIND=N'HEADER' ORDER BY LAYOUT_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportHeaderOption>();
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetString(0).Trim();
            var name = reader.GetString(1);
            var contentJson = reader.GetString(2);
            var logoPath = reader.IsDBNull(3) ? null : reader.GetString(3).Trim();
            // 解析 CONTENT JSON → companyName/companyNameEn/headerText
            var (company, companyEn, headerText) = ParseHeaderJson(contentJson);
            result.Add(new ReportHeaderOption(id, name, company ?? string.Empty, companyEn, headerText, logoPath, LogoUrl(logoPath ?? string.Empty)));
        }
        return result;
    }

    private static (string? Company, string? CompanyEn, string? HeaderText) ParseHeaderJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var company = root.TryGetProperty("companyName", out var c) ? c.GetString() : null;
            var companyEn = root.TryGetProperty("companyNameEn", out var ce) ? ce.GetString() : null;
            var headerText = root.TryGetProperty("headerText", out var h) ? h.GetString() : null;
            return (company, companyEn, headerText);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static async Task<List<ReportTailOption>> ReadTailsAsync(SqlConnection connection, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(LAYOUT_ID)),LTRIM(RTRIM(ISNULL(LAYOUT_DESC,LAYOUT_ID))),
                   LTRIM(RTRIM(ISNULL(CONTENT,'')))
            FROM dbo.REPORT_LAYOUT WITH (NOLOCK)
            WHERE KIND=N'TAIL' ORDER BY LAYOUT_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportTailOption>();
        while (await reader.ReadAsync(token))
            result.Add(new ReportTailOption(reader.GetString(0).Trim(), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<ReportSortScheme>>> ReadSortSchemesAsync(
        SqlConnection connection, CancellationToken token)
    {
        await using var reader = await ReadSortSchemesCoreAsync(connection, null, token);
        var result = new Dictionary<string, List<ReportSortScheme>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            var reportId = reader.GetString(0).Trim();
            if (!result.TryGetValue(reportId, out var list)) result[reportId] = list = [];
            list.Add(ReadSortScheme(reader));
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ReportSortScheme>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<List<ReportSortScheme>> ReadSortSchemesForReportAsync(
        SqlConnection connection, string reportId, CancellationToken token)
    {
        await using var reader = await ReadSortSchemesCoreAsync(connection, reportId, token);
        var result = new List<ReportSortScheme>();
        while (await reader.ReadAsync(token)) result.Add(ReadSortScheme(reader));
        return result;
    }

    private static async Task<SqlDataReader> ReadSortSchemesCoreAsync(
        SqlConnection connection, string? reportId, CancellationToken token)
    {
        const string sql = """
            SELECT REPORT_ID,SERIAL_NO,LTRIM(RTRIM(ISNULL(SORT_NAME,''))),
                   LTRIM(RTRIM(ISNULL(SORT_FIELDS,''))),LTRIM(RTRIM(ISNULL(GROUP_NAME,''))),
                   LTRIM(RTRIM(ISNULL(GROUP_FIELDS,'')))
            FROM dbo.REPORT_SORT WITH (NOLOCK)
            {0}
            ORDER BY REPORT_ID,SERIAL_NO;
            """;
        var command = new SqlCommand(
            string.Format(sql, reportId is null ? "" : "WHERE REPORT_ID=@ReportId"), connection);
        if (reportId is not null)
            command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = reportId.Trim();
        return await command.ExecuteReaderAsync(token);
    }

    private static ReportSortScheme ReadSortScheme(SqlDataReader reader) => new(
        Convert.ToInt32(reader.GetValue(1)),
        reader.GetString(2),
        EmptyToNull(reader.GetString(3)),
        EmptyToNull(reader.GetString(4)),
        EmptyToNull(reader.GetString(5)));

    private static async Task<ReportUserPrintSettings?> ReadUserSettingsAsync(
        SqlConnection connection, int moduleId, string userId, CancellationToken token)
    {
        const string sql = """
            SELECT TOP 1 REPORT_ID,HEADER_ID,TAIL_ID,LAST_SORT,ISNULL(SORT_ASC,1),ISNULL(SHOW_GROUP,1),ISNULL(SHOW_DETAIL,1)
            FROM dbo.SYSQR WITH (NOLOCK)
            WHERE USER_ID=@UserId AND R_M_IDX=@ModuleId AND IS_LAST=1;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        int? sortSerialNo = null;
        if (!reader.IsDBNull(3) && int.TryParse(reader.GetString(3).Trim(), out var parsed)) sortSerialNo = parsed;
        return new ReportUserPrintSettings(
            reader.IsDBNull(0) ? null : reader.GetString(0).Trim(),
            reader.IsDBNull(1) ? null : reader.GetString(1).Trim(),
            reader.IsDBNull(2) ? null : reader.GetString(2).Trim(),
            sortSerialNo,
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6));
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? LogoUrl(string logoPath)
    {
        var path = logoPath.Trim();
        if (path.Length == 0) return null;
        return "/" + path.Replace('\\', '/').TrimStart('~', '/');
    }
}

internal static class PrintSettingsSqlExtensions
{
    public static void AddWithNullable(
        this SqlParameterCollection parameters, string name, SqlDbType type, int size, string? value)
    {
        var parameter = new SqlParameter(name, type, size) { Value = value ?? (object)DBNull.Value };
        parameters.Add(parameter);
    }
}