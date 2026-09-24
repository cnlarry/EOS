using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed class ModuleRightsRepository(DbConnectionFactory connections, ILogger<ModuleRightsRepository> logger)
{
    public async Task<ModuleRights> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var personal = await ReadRowsAsync(connection, "SYSDD", "USER_ID", userId, moduleId, cancellationToken);
        if (personal.Count > 0)
        {
            var personalRights = RightsAggregator.FromPersonal(personal[0]);
            LogRights(userId, moduleId, "personal", personalRights);
            return personalRights;
        }

        const string groupSql = """
            SELECT h.EXEC_TAG,h.ADDNEW_TAG,h.DELETE_TAG,h.EDIT_TAG,h.APPROVE_TAG,h.DEAPPROVE_TAG,h.ENDCASE_TAG,h.UNENDCASE_TAG,
                   h.FILE_VIEW_TAG,h.FILE_UPDA_TAG,h.FILE_EDIT_TAG,h.FILE_DELE_TAG,
                   h.COST_TAG,h.SECRECY_TAG,h.SETUP_TAG,h.MODULE_CONFIG_TAG,
                   h.DENY_VIEW_FIELD_MASTER,h.DENY_VIEW_FIELD_DETAIL,h.DENY_NEW_FIELD_MASTER,h.DENY_NEW_FIELD_DETAIL,
                   h.DENY_MODI_FIELD_MASTER,h.DENY_MODI_FIELD_DETAIL,h.DATA_FILTER
            FROM dbo.SYSDH h WITH (NOLOCK)
            WHERE h.M_IDX=@ModuleId AND h.G_IDX IN
              (SELECT G_IDX FROM dbo.SYSDG_USER WITH (NOLOCK) WHERE USER_ID=@UserId);
            """;
        await using var command = new SqlCommand(groupSql, connection);
        AddParameters(command, userId, moduleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var groups = new List<RightRow>();
        while (await reader.ReadAsync(cancellationToken)) groups.Add(ReadRow(reader));
        var rights = RightsAggregator.FromGroups(groups);
        LogRights(userId, moduleId, groups.Count == 0 ? "none" : $"group({groups.Count})", rights);
        return rights;
    }

    /// <summary>
    /// 报表级权限：
    /// 1. 先判断模块级报表可见性（SYSDD.REPORT_TAG 个人优先，否则 SYSDH.REPORT_TAG 组 OR）；
    /// 2. 若模块 REPORT_TAG 未授予 → 全禁（CanPreview/CanPrint/CanExport all false）；
    /// 3. 若模块 REPORT_TAG 已授予 → 查询 override 行（SYSDD_REPORT 个人优先，否则 SYSDH_REPORT 组 OR）；
    /// 4. 无 override 行 → 默认全开（PREVIEW/PRINT/EXPORT = true, DATA_FILTER = ''）；
    /// 5. 有 override 行 → 按 override 值覆盖（通常收紧）。
    /// </summary>
    public async Task<ReportRights> GetReportAsync(string userId, int moduleId, string reportId, CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);

        // 1. 判断模块级 REPORT_TAG（个人覆盖组）
        var moduleReportTag = await GetModuleReportTagAsync(connection, userId, moduleId, token);
        if (!moduleReportTag)
        {
            logger.LogDebug("报表权限 userId={UserId} module={ModuleId} report={ReportId} source=module_no_report_tag",
                userId, moduleId, reportId);
            return new ReportRights(false, false, false, string.Empty);
        }

        // 2. 查询 override 行（个人 SYSDD_REPORT 优先，否则组 SYSDH_REPORT OR）
        const string personalOverrideSql = """
            SELECT ISNULL(PREVIEW_TAG,0) AS PREVIEW_TAG,ISNULL(PRINT_TAG,0) AS PRINT_TAG,
                   ISNULL(EXPORT_TAG,0) AS EXPORT_TAG,ISNULL(DATA_FILTER,'') AS DATA_FILTER
            FROM dbo.SYSDD_REPORT WITH (NOLOCK)
            WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId;
            """;
        await using (var personalCommand = new SqlCommand(personalOverrideSql, connection))
        {
            AddReportParameters(personalCommand, userId, moduleId, reportId);
            await using var reader = await personalCommand.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                var row = ReadReportRow(reader);
                var rights = new ReportRights(row.Preview, row.Print, row.Export, row.DataFilter.Trim());
                logger.LogDebug("报表权限 userId={UserId} module={ModuleId} report={ReportId} source=personal_override",
                    userId, moduleId, reportId);
                return rights;
            }
        }

        const string groupOverrideSql = """
            SELECT ISNULL(g.PREVIEW_TAG,0) AS PREVIEW_TAG,ISNULL(g.PRINT_TAG,0) AS PRINT_TAG,
                   ISNULL(g.EXPORT_TAG,0) AS EXPORT_TAG,ISNULL(g.DATA_FILTER,'') AS DATA_FILTER
            FROM dbo.SYSDH_REPORT g WITH (NOLOCK)
            INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX=g.G_IDX
            WHERE gu.USER_ID=@UserId AND g.M_IDX=@ModuleId AND g.REPORT_ID=@ReportId;
            """;
        await using var groupCommand = new SqlCommand(groupOverrideSql, connection);
        AddReportParameters(groupCommand, userId, moduleId, reportId);
        await using var groupReader = await groupCommand.ExecuteReaderAsync(token);
        var rows = new List<ReportRightRow>();
        while (await groupReader.ReadAsync(token)) rows.Add(ReadReportRow(groupReader));
        if (rows.Count > 0)
        {
            var result = ReportRightsAggregator.FromGroups(rows);
            logger.LogDebug("报表权限 userId={UserId} module={ModuleId} report={ReportId} source=group_override({Count})",
                userId, moduleId, reportId, rows.Count);
            return result;
        }

        // 3. 无 override 行 → 默认全开（跟随模块 REPORT_TAG）
        logger.LogDebug("报表权限 userId={UserId} module={ModuleId} report={ReportId} source=default_open",
            userId, moduleId, reportId);
        return new ReportRights(true, true, true, string.Empty);
    }

    private void LogRights(string userId, int moduleId, string source, ModuleRights rights) =>
        logger.LogDebug(
            "模块权限 userId={UserId} module={ModuleId} source={Source} browse={CanBrowse} cost={CanViewCost} secrecy={CanViewSecrecy} setup={CanSetup} addNew={CanAddNew} edit={CanEdit} delete={CanDelete} deniedMaster={DeniedMasterCount} deniedDetail={DeniedDetailCount} denyNewMaster={DenyNewMasterCount} denyModiMaster={DenyModiMasterCount}",
            userId, moduleId, source, rights.CanBrowse, rights.CanViewCost, rights.CanViewSecrecy, rights.CanSetup,
            rights.CanAddNew, rights.CanEdit, rights.CanDelete,
            rights.DeniedMasterFields.Count, rights.DeniedDetailFields.Count,
            rights.DenyNewMasterFields.Count, rights.DenyModiMasterFields.Count);

    private static async Task<List<RightRow>> ReadRowsAsync(SqlConnection connection, string table, string idColumn, string userId, int moduleId, CancellationToken token)
    {
        var sql = $"SELECT EXEC_TAG,ADDNEW_TAG,DELETE_TAG,EDIT_TAG,APPROVE_TAG,DEAPPROVE_TAG,ENDCASE_TAG,UNENDCASE_TAG,FILE_VIEW_TAG,FILE_UPDA_TAG,FILE_EDIT_TAG,FILE_DELE_TAG,COST_TAG,SECRECY_TAG,SETUP_TAG,MODULE_CONFIG_TAG,DENY_VIEW_FIELD_MASTER,DENY_VIEW_FIELD_DETAIL,DENY_NEW_FIELD_MASTER,DENY_NEW_FIELD_DETAIL,DENY_MODI_FIELD_MASTER,DENY_MODI_FIELD_DETAIL,DATA_FILTER FROM dbo.{table} WITH (NOLOCK) WHERE {idColumn}=@UserId AND M_IDX=@ModuleId";
        await using var command = new SqlCommand(sql, connection);
        AddParameters(command, userId, moduleId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<RightRow>();
        while (await reader.ReadAsync(token)) rows.Add(ReadRow(reader));
        return rows;
    }

    private static RightRow ReadRow(SqlDataReader reader) => new(
        reader.GetNullableString("EXEC_TAG") ?? "A",
        reader.GetNullableBoolean("ADDNEW_TAG"),
        reader.GetNullableBoolean("DELETE_TAG"),
        reader.GetNullableBoolean("EDIT_TAG"),
        reader.GetNullableBoolean("APPROVE_TAG"),
        reader.GetNullableBoolean("DEAPPROVE_TAG"),
        reader.GetNullableBoolean("ENDCASE_TAG"),
        reader.GetNullableBoolean("UNENDCASE_TAG"),
        reader.GetNullableBoolean("FILE_VIEW_TAG"),
        reader.GetNullableBoolean("FILE_UPDA_TAG"),
        reader.GetNullableBoolean("FILE_EDIT_TAG"),
        reader.GetNullableBoolean("FILE_DELE_TAG"),
        reader.GetNullableBoolean("COST_TAG"),
        reader.GetNullableBoolean("SECRECY_TAG"),
        reader.GetNullableBoolean("SETUP_TAG"),
        reader.GetNullableString("DENY_VIEW_FIELD_MASTER") ?? string.Empty,
        reader.GetNullableString("DENY_VIEW_FIELD_DETAIL") ?? string.Empty,
        reader.GetNullableString("DENY_NEW_FIELD_MASTER") ?? string.Empty,
        reader.GetNullableString("DENY_NEW_FIELD_DETAIL") ?? string.Empty,
        reader.GetNullableString("DENY_MODI_FIELD_MASTER") ?? string.Empty,
        reader.GetNullableString("DENY_MODI_FIELD_DETAIL") ?? string.Empty,
        reader.GetNullableString("DATA_FILTER") ?? string.Empty,
        reader.GetNullableBoolean("MODULE_CONFIG_TAG"));

    private static void AddParameters(SqlCommand command, string userId, int moduleId)
    {
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
    }

    private static void AddReportParameters(SqlCommand command, string userId, int moduleId, string reportId)
    {
        AddParameters(command, userId, moduleId);
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = reportId.Trim();
    }

    /// <summary>模块级报表可见性唯一真源：SYSDD.REPORT_TAG 个人优先，否则 SYSDH.REPORT_TAG 组 OR。</summary>
    private static async Task<bool> GetModuleReportTagAsync(
        SqlConnection connection, string userId, int moduleId, CancellationToken token)
    {
        const string personalSql = """
            SELECT ISNULL(REPORT_TAG,0) FROM dbo.SYSDD WITH (NOLOCK)
            WHERE USER_ID=@UserId AND M_IDX=@ModuleId;
            """;
        await using (var personalCommand = new SqlCommand(personalSql, connection))
        {
            AddParameters(personalCommand, userId, moduleId);
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
        AddParameters(groupCommand, userId, moduleId);
        return await groupCommand.ExecuteScalarAsync(token) is not null;
    }

    private static ReportRightRow ReadReportRow(SqlDataReader reader) => new(
        reader.GetNullableBoolean("PREVIEW_TAG"),
        reader.GetNullableBoolean("PRINT_TAG"),
        reader.GetNullableBoolean("EXPORT_TAG"),
        reader.GetNullableString("DATA_FILTER") ?? string.Empty);

    private SqlConnection CreateConnection() => connections.Create();
}

internal sealed record RightRow(
    string Execute,
    bool AddNew,
    bool Delete,
    bool Edit,
    bool Approve,
    bool Deapprove,
    bool EndCase,
    bool UnEndCase,
    bool FileView,
    bool FileUpda,
    bool FileEdit,
    bool FileDele,
    bool Cost,
    bool Secrecy,
    bool Setup,
    string DenyViewMaster,
    string DenyViewDetail,
    string DenyNewMaster,
    string DenyNewDetail,
    string DenyModiMaster,
    string DenyModiDetail,
    string DataFilter,
    /// <summary>模块配置权（行为动作/校验规则/自定义按钮）；默认关闭。</summary>
    bool ModuleConfig = false);

internal sealed record ReportRightRow(bool Preview, bool Print, bool Export, string DataFilter);

/// <summary>
/// 报表权限聚合（纯逻辑，与数据库解耦，便于单元测试）。
/// OR；
/// DATA_FILTER 非空项以 OR 拼接；无记录全禁。
/// </summary>
internal static class ReportRightsAggregator
{
    public static ReportRights FromPersonal(ReportRightRow row) =>
        new(row.Preview, row.Print, row.Export, row.DataFilter.Trim());

    public static ReportRights FromGroups(IReadOnlyList<ReportRightRow> rows)
    {
        if (rows.Count == 0) return new ReportRights(false, false, false, string.Empty);
        var filters = rows
            .Select(row => row.DataFilter.Trim())
            .Where(filter => filter.Length > 0)
            .Select(filter => $"({filter})")
            .ToList();
        return new ReportRights(
            rows.Any(row => row.Preview),
            rows.Any(row => row.Print),
            rows.Any(row => row.Export),
            string.Join(" OR ", filters));
    }
}

/// <summary>
/// 纯权限聚合逻辑（与数据库解耦，便于单元测试）。
/// 语义系统 Right.cs / Admin.GetUserRightDetail：
/// - 个人权限覆盖组权限；组权限布尔位取 OR；禁止字段取各组交集（不是并集）；
/// - EXEC_TAG 从 A 起按字符串比较取最大；无记录时无权；
/// - DATA_FILTER 仅登记、不执行：以显式 AND 组合登记，
/// 具体运算符留待白名单化解析（受控解析）时定稿。
/// </summary>
internal static class RightsAggregator
{
    public static ModuleRights FromPersonal(RightRow row) => new(
        CanBrowse: !string.Equals(row.Execute, "A", StringComparison.OrdinalIgnoreCase),
        CanViewCost: row.Cost,
        CanViewSecrecy: row.Secrecy,
        CanSetup: row.Setup,
        CanModuleConfig: row.ModuleConfig,
        DeniedMasterFields: ParseDenied(row.DenyViewMaster),
        DeniedDetailFields: ParseDenied(row.DenyViewDetail),
        CanAddNew: row.AddNew,
        CanEdit: row.Edit,
        CanDelete: row.Delete,
        CanApprove: row.Approve,
        CanDeapprove: row.Deapprove,
        CanEndCase: row.EndCase,
        CanUnEndCase: row.UnEndCase,
        CanFileView: row.FileView,
        CanFileUpda: row.FileUpda,
        CanFileEdit: row.FileEdit,
        CanFileDele: row.FileDele,
        DenyNewMasterFields: ParseDenied(row.DenyNewMaster),
        DenyNewDetailFields: ParseDenied(row.DenyNewDetail),
        DenyModiMasterFields: ParseDenied(row.DenyModiMaster),
        DenyModiDetailFields: ParseDenied(row.DenyModiDetail),
        DataFilter: row.DataFilter.Trim(),
        ExecuteTag: string.IsNullOrWhiteSpace(row.Execute) ? "A" : row.Execute.Trim());

    public static ModuleRights FromGroups(IReadOnlyList<RightRow> rows)
    {
        if (rows.Count == 0)
        {
            return new ModuleRights(false, false, false, false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                false, false, false,
                false, false, false, false, false, false, false, false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                string.Empty,
                "A");
        }

        var execute = rows.Select(row => row.Execute).OrderByDescending(value => value, StringComparer.Ordinal).First();
        return new ModuleRights(
            !string.Equals(execute, "A", StringComparison.OrdinalIgnoreCase),
            rows.Any(row => row.Cost),
            rows.Any(row => row.Secrecy),
            rows.Any(row => row.Setup),
            IntersectDenied(rows.Select(row => row.DenyViewMaster)),
            IntersectDenied(rows.Select(row => row.DenyViewDetail)),
            rows.Any(row => row.AddNew),
            rows.Any(row => row.Edit),
            rows.Any(row => row.Delete),
            rows.Any(row => row.Approve),
            rows.Any(row => row.Deapprove),
            rows.Any(row => row.EndCase),
            rows.Any(row => row.UnEndCase),
            rows.Any(row => row.FileView),
            rows.Any(row => row.FileUpda),
            rows.Any(row => row.FileEdit),
            rows.Any(row => row.FileDele),
            IntersectDenied(rows.Select(row => row.DenyNewMaster)),
            IntersectDenied(rows.Select(row => row.DenyNewDetail)),
            IntersectDenied(rows.Select(row => row.DenyModiMaster)),
            IntersectDenied(rows.Select(row => row.DenyModiDetail)),
            CombineDataFilters(rows),
            execute,
            rows.Any(row => row.ModuleConfig));
    }

    private static string CombineDataFilters(IReadOnlyList<RightRow> rows)
    {
        var filters = rows
            .Select(row => row.DataFilter.Trim())
            .Where(filter => filter.Length > 0)
            .Select(filter => $"({filter})")
            .ToList();
        return filters.Count == 0 ? string.Empty : string.Join(" AND ", filters);
    }

    private static IReadOnlySet<string> IntersectDenied(IEnumerable<string> values)
    {
        HashSet<string>? result = null;
        foreach (var value in values)
        {
            var current = ParseDenied(value);
            if (result is null) result = current;
            else result.IntersectWith(current);
        }
        return result ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ParseDenied(string? value) =>
        (value ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

internal static class ModuleRightsReaderExtensions
{
    public static bool GetNullableBoolean(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal);
    }
}
