using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed class LegacyRightsRepository(DbConnectionFactory connections, ILogger<LegacyRightsRepository> logger)
{
    public async Task<LegacyModuleRights> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
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
            SELECT h.EXEC_TAG,h.ADDNEW_TAG,h.DELETE_TAG,h.EDIT_TAG,h.COST_TAG,h.SECRECY_TAG,h.SETUP_TAG,
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
    /// 报表级权限（对齐旧 Admin.GetUserReportRightDetail）：
    /// 个人 SYSDD_REPORT 记录存在则完全采用（不再合并组）；否则取用户所属组
    /// SYSDH_REPORT 的 PREVIEW/PRINT/EXPORT 标签按 OR 合并、DATA_FILTER 按 OR 拼接；
    /// 无任何记录时三项权限均为 false。
    /// </summary>
    public async Task<ReportRights> GetReportAsync(string userId, int moduleId, string reportId, CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);

        const string personalSql = """
            SELECT ISNULL(PREVIEW_TAG,0) AS PREVIEW_TAG,ISNULL(PRINT_TAG,0) AS PRINT_TAG,
                   ISNULL(EXPORT_TAG,0) AS EXPORT_TAG,ISNULL(DATA_FILTER,'') AS DATA_FILTER
            FROM dbo.SYSDD_REPORT WITH (NOLOCK)
            WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId;
            """;
        await using (var personalCommand = new SqlCommand(personalSql, connection))
        {
            AddReportParameters(personalCommand, userId, moduleId, reportId);
            await using var reader = await personalCommand.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                var rights = ReportRightsAggregator.FromPersonal(ReadReportRow(reader));
                logger.LogDebug("报表权限 userId={UserId} module={ModuleId} report={ReportId} source=personal",
                    userId, moduleId, reportId);
                return rights;
            }
        }

        const string groupSql = """
            SELECT ISNULL(g.PREVIEW_TAG,0) AS PREVIEW_TAG,ISNULL(g.PRINT_TAG,0) AS PRINT_TAG,
                   ISNULL(g.EXPORT_TAG,0) AS EXPORT_TAG,ISNULL(g.DATA_FILTER,'') AS DATA_FILTER
            FROM dbo.SYSDH_REPORT g WITH (NOLOCK)
            INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX=g.G_IDX
            WHERE gu.USER_ID=@UserId AND g.M_IDX=@ModuleId AND g.REPORT_ID=@ReportId;
            """;
        await using var groupCommand = new SqlCommand(groupSql, connection);
        AddReportParameters(groupCommand, userId, moduleId, reportId);
        await using var groupReader = await groupCommand.ExecuteReaderAsync(token);
        var rows = new List<ReportRightRow>();
        while (await groupReader.ReadAsync(token)) rows.Add(ReadReportRow(groupReader));
        var result = ReportRightsAggregator.FromGroups(rows);
        logger.LogDebug("报表权限 userId={UserId} module={ModuleId} report={ReportId} source={Source}",
            userId, moduleId, reportId, rows.Count == 0 ? "none" : $"group({rows.Count})");
        return result;
    }

    private void LogRights(string userId, int moduleId, string source, LegacyModuleRights rights) =>
        logger.LogDebug(
            "模块权限 userId={UserId} module={ModuleId} source={Source} browse={CanBrowse} cost={CanViewCost} secrecy={CanViewSecrecy} setup={CanSetup} addNew={CanAddNew} edit={CanEdit} delete={CanDelete} deniedMaster={DeniedMasterCount} deniedDetail={DeniedDetailCount} denyNewMaster={DenyNewMasterCount} denyModiMaster={DenyModiMasterCount}",
            userId, moduleId, source, rights.CanBrowse, rights.CanViewCost, rights.CanViewSecrecy, rights.CanSetup,
            rights.CanAddNew, rights.CanEdit, rights.CanDelete,
            rights.DeniedMasterFields.Count, rights.DeniedDetailFields.Count,
            rights.DenyNewMasterFields.Count, rights.DenyModiMasterFields.Count);

    private static async Task<List<RightRow>> ReadRowsAsync(SqlConnection connection, string table, string idColumn, string userId, int moduleId, CancellationToken token)
    {
        var sql = $"SELECT EXEC_TAG,ADDNEW_TAG,DELETE_TAG,EDIT_TAG,COST_TAG,SECRECY_TAG,SETUP_TAG,DENY_VIEW_FIELD_MASTER,DENY_VIEW_FIELD_DETAIL,DENY_NEW_FIELD_MASTER,DENY_NEW_FIELD_DETAIL,DENY_MODI_FIELD_MASTER,DENY_MODI_FIELD_DETAIL,DATA_FILTER FROM dbo.{table} WITH (NOLOCK) WHERE {idColumn}=@UserId AND M_IDX=@ModuleId";
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
        reader.GetNullableBoolean("COST_TAG"),
        reader.GetNullableBoolean("SECRECY_TAG"),
        reader.GetNullableBoolean("SETUP_TAG"),
        reader.GetNullableString("DENY_VIEW_FIELD_MASTER") ?? string.Empty,
        reader.GetNullableString("DENY_VIEW_FIELD_DETAIL") ?? string.Empty,
        reader.GetNullableString("DENY_NEW_FIELD_MASTER") ?? string.Empty,
        reader.GetNullableString("DENY_NEW_FIELD_DETAIL") ?? string.Empty,
        reader.GetNullableString("DENY_MODI_FIELD_MASTER") ?? string.Empty,
        reader.GetNullableString("DENY_MODI_FIELD_DETAIL") ?? string.Empty,
        reader.GetNullableString("DATA_FILTER") ?? string.Empty);

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
    bool Cost,
    bool Secrecy,
    bool Setup,
    string DenyViewMaster,
    string DenyViewDetail,
    string DenyNewMaster,
    string DenyNewDetail,
    string DenyModiMaster,
    string DenyModiDetail,
    string DataFilter);

internal sealed record ReportRightRow(bool Preview, bool Print, bool Export, string DataFilter);

/// <summary>
/// 报表权限聚合（纯逻辑，与数据库解耦，便于单元测试）。
/// 对齐旧 Admin.GetUserReportRightDetail：个人覆盖组；组标签取 OR；
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
/// 语义对齐旧系统 Right.cs / Admin.GetUserRightDetail：
/// - 个人权限覆盖组权限；组权限布尔位取 OR；禁止字段取各组交集（不是并集）；
/// - EXEC_TAG 从 A 起按字符串比较取最大；无记录时无权；
/// - DATA_FILTER 仅登记、不执行：旧 Grid.cs 以 " and " 前缀消费，此处以显式 AND 组合登记，
///   具体运算符留待白名单化解析（受控解析）时定稿。
/// </summary>
internal static class RightsAggregator
{
    public static LegacyModuleRights FromPersonal(RightRow row) => new(
        CanBrowse: !string.Equals(row.Execute, "A", StringComparison.OrdinalIgnoreCase),
        CanViewCost: row.Cost,
        CanViewSecrecy: row.Secrecy,
        CanSetup: row.Setup,
        DeniedMasterFields: ParseDenied(row.DenyViewMaster),
        DeniedDetailFields: ParseDenied(row.DenyViewDetail),
        CanAddNew: row.AddNew,
        CanEdit: row.Edit,
        CanDelete: row.Delete,
        DenyNewMasterFields: ParseDenied(row.DenyNewMaster),
        DenyNewDetailFields: ParseDenied(row.DenyNewDetail),
        DenyModiMasterFields: ParseDenied(row.DenyModiMaster),
        DenyModiDetailFields: ParseDenied(row.DenyModiDetail),
        DataFilter: row.DataFilter.Trim(),
        ExecuteTag: string.IsNullOrWhiteSpace(row.Execute) ? "A" : row.Execute.Trim());

    public static LegacyModuleRights FromGroups(IReadOnlyList<RightRow> rows)
    {
        if (rows.Count == 0)
        {
            return new LegacyModuleRights(false, false, false, false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                false, false, false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                string.Empty,
                "A");
        }

        var execute = rows.Select(row => row.Execute).OrderByDescending(value => value, StringComparer.Ordinal).First();
        return new LegacyModuleRights(
            !string.Equals(execute, "A", StringComparison.OrdinalIgnoreCase),
            rows.Any(row => row.Cost),
            rows.Any(row => row.Secrecy),
            rows.Any(row => row.Setup),
            IntersectDenied(rows.Select(row => row.DenyViewMaster)),
            IntersectDenied(rows.Select(row => row.DenyViewDetail)),
            rows.Any(row => row.AddNew),
            rows.Any(row => row.Edit),
            rows.Any(row => row.Delete),
            IntersectDenied(rows.Select(row => row.DenyNewMaster)),
            IntersectDenied(rows.Select(row => row.DenyNewDetail)),
            IntersectDenied(rows.Select(row => row.DenyModiMaster)),
            IntersectDenied(rows.Select(row => row.DenyModiDetail)),
            CombineDataFilters(rows),
            execute);
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

internal static class LegacyRightsReaderExtensions
{
    public static bool GetNullableBoolean(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal);
    }
}
