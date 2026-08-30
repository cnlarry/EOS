using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

using EOS.API.Telemetry;
namespace EOS.API.Data;

/// <summary>
/// 权限管理写白名单（§6 服务端固定列集合）。
/// SYSDD / SYSDH 共用同一组列；所有写入列必须来自本集合，不接受任意列名。
/// </summary>
public static class RightsColumnWhitelist
{
    public const string ExecTag = "EXEC_TAG";

    public static readonly string[] BitColumns =
    [
        "ADDNEW_TAG", "EDIT_TAG", "DELETE_TAG", "APPROVE_TAG", "DEAPPROVE_TAG", "REPORT_TAG",
        "COST_TAG", "SETUP_TAG", "SECRECY_TAG", "ENDCASE_TAG", "UNENDCASE_TAG",
        "OTHER1_TAG", "OTHER2_TAG", "OTHER3_TAG", "OTHER4_TAG",
        "FILE_VIEW_TAG", "FILE_UPDA_TAG", "FILE_EDIT_TAG", "FILE_DELE_TAG",
    ];

    public static readonly string[] DenyColumns =
    [
        "DENY_VIEW_FIELD_MASTER", "DENY_VIEW_FIELD_DETAIL",
        "DENY_NEW_FIELD_MASTER", "DENY_NEW_FIELD_DETAIL",
        "DENY_MODI_FIELD_MASTER", "DENY_MODI_FIELD_DETAIL",
    ];

    public static readonly string[] AllColumns = [ExecTag, .. BitColumns, .. DenyColumns, "DATA_FILTER"];
}

/// <summary>
/// 权限管理纯逻辑（与数据库解耦，便于单元测试）：
/// 全默认空判定、EXEC_TAG/字段拒绝串规范化、生效值聚合
/// （个人覆盖组；组布尔 OR、EXEC_TAG 取最大、禁止字段交集、DATA_FILTER 组合）。
/// </summary>
internal static class RightsAdminLogic
{
    /// <summary>全默认空（EXEC_TAG='A' 或空 + 全位 0 + DENY_*/DATA_FILTER 空）→ 删除行（回退组权限）。</summary>
    public static bool IsDefaultEmpty(ModuleRightsInput input)
    {
        var execTagEmpty = string.IsNullOrWhiteSpace(input.ExecTag)
            || string.Equals(input.ExecTag.Trim(), "A", StringComparison.OrdinalIgnoreCase);
        return execTagEmpty
            && !input.AddNew && !input.Edit && !input.Delete && !input.Approve && !input.Deapprove && !input.Report
            && !input.Cost && !input.Setup && !input.Secrecy && !input.EndCase && !input.UnEndCase
            && !input.Other1 && !input.Other2 && !input.Other3 && !input.Other4
            && !input.FileView && !input.FileUpda && !input.FileEdit && !input.FileDele
            && string.IsNullOrWhiteSpace(input.DenyViewMaster) && string.IsNullOrWhiteSpace(input.DenyViewDetail)
            && string.IsNullOrWhiteSpace(input.DenyNewMaster) && string.IsNullOrWhiteSpace(input.DenyNewDetail)
            && string.IsNullOrWhiteSpace(input.DenyModiMaster) && string.IsNullOrWhiteSpace(input.DenyModiDetail)
            && string.IsNullOrWhiteSpace(input.DataFilter);
    }

    /// <summary>报表权限全默认空（PREVIEW/PRINT/EXPORT 全 0 且 DATA_FILTER 空）→ 删除行。</summary>
    public static bool IsDefaultEmpty(ReportRightsInput input) =>
        !input.Preview && !input.Print && !input.Export && string.IsNullOrWhiteSpace(input.DataFilter);

    /// <summary>EXEC_TAG 规范化：A~Z 单字符（空 → 'A'），非法抛 ArgumentException。</summary>
    public static string NormalizeExecTag(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (text.Length == 0) return "A";
        if (text.Length != 1 || text[0] is < 'A' or > 'Z')
            throw new ArgumentException("EXEC_TAG 必须是 A~Z 的单字符（A=禁止执行）。", nameof(value));
        return text;
    }

    /// <summary>
    /// 字段拒绝串规范化：兼容逗号/分号分隔，去空、去重（忽略大小写），
    /// 以分号存储（对齐引擎 RightsAggregator 的 ';' 解析）。
    /// </summary>
    public static string NormalizeDenyList(string? value) =>
        string.Join(";", ParseDenyList(value));

    /// <summary>
    /// 解析字段拒绝串（逗号/分号分隔）。跳过遗留哨兵值 '0'：
    /// 旧系统导入的历史行把「无禁止字段」写成 '0'（旧网格 Split(';') 后按列名隐藏，无此列即无效果）；
    /// 新系统视为空，读侧聚合与写侧规范化统一剔除。
    /// </summary>
    public static IReadOnlyList<string> ParseDenyList(string? value) =>
        (value ?? string.Empty)
            .Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !string.Equals(token, "0", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>用户/组模块权限生效值：个人行存在 → 完全采用；否则组 OR 聚合；无记录全禁。</summary>
    public static EffectiveModuleRights AggregateModuleEffective(
        ModuleRightsInput? personal,
        IReadOnlyList<ModuleRightsInput> groups)
    {
        if (personal is not null)
        {
            var execTag = NormalizeExecTag(personal.ExecTag);
            return FromValues(
                "personal", execTag, !string.Equals(execTag, "A", StringComparison.OrdinalIgnoreCase), personal);
        }
        if (groups.Count == 0) return None();

        var exec = groups.Select(group => NormalizeExecTag(group.ExecTag))
            .OrderByDescending(value => value, StringComparer.Ordinal).First();
        bool Or(Func<ModuleRightsInput, bool> pick) => groups.Any(pick);
        return new EffectiveModuleRights(
            "group",
            !string.Equals(exec, "A", StringComparison.OrdinalIgnoreCase),
            exec,
            Or(group => group.AddNew), Or(group => group.Edit), Or(group => group.Delete),
            Or(group => group.Approve), Or(group => group.Deapprove), Or(group => group.Report),
            Or(group => group.Cost), Or(group => group.Setup), Or(group => group.Secrecy),
            Or(group => group.EndCase), Or(group => group.UnEndCase),
            Or(group => group.Other1), Or(group => group.Other2), Or(group => group.Other3), Or(group => group.Other4),
            Or(group => group.FileView), Or(group => group.FileUpda), Or(group => group.FileEdit), Or(group => group.FileDele),
            Intersect(groups, group => ParseDenyList(group.DenyViewMaster)),
            Intersect(groups, group => ParseDenyList(group.DenyViewDetail)),
            Intersect(groups, group => ParseDenyList(group.DenyNewMaster)),
            Intersect(groups, group => ParseDenyList(group.DenyNewDetail)),
            Intersect(groups, group => ParseDenyList(group.DenyModiMaster)),
            Intersect(groups, group => ParseDenyList(group.DenyModiDetail)),
            CombineDataFilters(groups.Select(group => group.DataFilter), " AND "));
    }

    /// <summary>
    /// 用户/组报表权限生效值（ADR-009 §2 语义）：个人 override 行存在 → 完全采用；
    /// 否则组 override OR（DATA_FILTER 按 OR 拼接）；无任何 override 行 → 默认开放
    /// （跟随模块 REPORT_TAG 全开，source="default_open"）。
    /// </summary>
    public static EffectiveReportRights AggregateReportEffective(
        ReportRightsInput? personal,
        IReadOnlyList<ReportRightsInput> groups)
    {
        if (personal is not null)
            return new("personal", personal.Preview, personal.Print, personal.Export, (personal.DataFilter ?? string.Empty).Trim());
        if (groups.Count == 0)
            return new("default_open", true, true, true, string.Empty);
        return new(
            "group",
            groups.Any(group => group.Preview),
            groups.Any(group => group.Print),
            groups.Any(group => group.Export),
            CombineDataFilters(groups.Select(group => group.DataFilter), " OR "));
    }

    private static EffectiveModuleRights FromValues(
        string source, string execTag, bool canBrowse, ModuleRightsInput input) => new(
        source, canBrowse, execTag,
        input.AddNew, input.Edit, input.Delete, input.Approve, input.Deapprove, input.Report,
        input.Cost, input.Setup, input.Secrecy, input.EndCase, input.UnEndCase,
        input.Other1, input.Other2, input.Other3, input.Other4,
        input.FileView, input.FileUpda, input.FileEdit, input.FileDele,
        ParseDenyList(input.DenyViewMaster), ParseDenyList(input.DenyViewDetail),
        ParseDenyList(input.DenyNewMaster), ParseDenyList(input.DenyNewDetail),
        ParseDenyList(input.DenyModiMaster), ParseDenyList(input.DenyModiDetail),
        (input.DataFilter ?? string.Empty).Trim());

    private static EffectiveModuleRights None() => new(
        Source: "none",
        CanBrowse: false,
        ExecTag: "A",
        AddNew: false, Edit: false, Delete: false, Approve: false, Deapprove: false, Report: false,
        Cost: false, Setup: false, Secrecy: false, EndCase: false, UnEndCase: false,
        Other1: false, Other2: false, Other3: false, Other4: false,
        FileView: false, FileUpda: false, FileEdit: false, FileDele: false,
        DenyViewMaster: [], DenyViewDetail: [], DenyNewMaster: [], DenyNewDetail: [],
        DenyModiMaster: [], DenyModiDetail: [], DataFilter: string.Empty);

    private static IReadOnlyList<string> Intersect(
        IReadOnlyList<ModuleRightsInput> groups,
        Func<ModuleRightsInput, IReadOnlyList<string>> pick)
    {
        HashSet<string>? result = null;
        foreach (var group in groups)
        {
            var current = pick(group).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (result is null) result = current;
            else result.IntersectWith(current);
        }
        return result?.OrderBy(field => field, StringComparer.Ordinal).ToList()
            ?? new List<string>();
    }

    private static string CombineDataFilters(IEnumerable<string?> filters, string separator)
    {
        var parts = filters
            .Select(filter => (filter ?? string.Empty).Trim())
            .Where(filter => filter.Length > 0)
            .Select(filter => $"({filter})")
            .ToList();
        return parts.Count == 0 ? string.Empty : string.Join(separator, parts);
    }
}

/// <summary>
/// 权限管理仓储（T1）：个人/组模块权限与报表权限矩阵读取、按 §4 语义 upsert/删除、
/// 生效值聚合预览、用户组与成员关系、字段级拒绝元数据、写审计。
/// 所有写入走服务端固定列白名单 + 参数化 SQL + 事务包裹。
/// </summary>
public sealed class RightsAdminRepository(
    DbConnectionFactory connections,
    NavigationRepository navigationRepository,
    EOS.API.Security.PermissionCache permissionCache,
    ILogger<RightsAdminRepository> logger)
{
    private const int AuditModuleId = 2306;

    private static readonly string[] ModuleRowColumns =
    [
        "M_IDX",
        "EXEC_TAG",
        "ADDNEW_TAG", "EDIT_TAG", "DELETE_TAG", "APPROVE_TAG", "DEAPPROVE_TAG", "REPORT_TAG",
        "COST_TAG", "SETUP_TAG", "SECRECY_TAG", "ENDCASE_TAG", "UNENDCASE_TAG",
        "OTHER1_TAG", "OTHER2_TAG", "OTHER3_TAG", "OTHER4_TAG",
        "FILE_VIEW_TAG", "FILE_UPDA_TAG", "FILE_EDIT_TAG", "FILE_DELE_TAG",
        "DENY_VIEW_FIELD_MASTER", "DENY_VIEW_FIELD_DETAIL",
        "DENY_NEW_FIELD_MASTER", "DENY_NEW_FIELD_DETAIL",
        "DENY_MODI_FIELD_MASTER", "DENY_MODI_FIELD_DETAIL",
        "DATA_FILTER",
    ];

    private static readonly string[] ReportRowColumns =
        ["M_IDX", "REPORT_ID", "PREVIEW_TAG", "PRINT_TAG", "EXPORT_TAG", "DATA_FILTER"];

    /// <summary>用户模块权限矩阵（§5.1）：当前管理员可见模块树 + 个人/组权限 + 生效值。</summary>
    public async Task<IReadOnlyList<ModuleRightsRow>> GetUserModuleMatrixAsync(
        string adminUserId, string targetUserId, CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        await EnsureUserExistsAsync(targetUserId, token);
        var modules = await navigationRepository.GetForUserAsync(adminUserId, token);
        var modulesById = modules.ToDictionary(module => module.Id);
        var personal = (await ReadUserModuleRowsAsync(targetUserId, token)).ToDictionary(row => row.ModuleId);
        var groups = (await ReadUserGroupModuleRowsAsync(targetUserId, token))
            .GroupBy(row => row.ModuleId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.ToInput()).ToList());
        return modules
            .Select(module => BuildModuleRow(module, modulesById, personal.GetValueOrDefault(module.Id)?.ToInput(),
                groups.GetValueOrDefault(module.Id) ?? []))
            .ToList();
    }

    /// <summary>组模块权限矩阵（§5.2）：该组 SYSDH 行（无个人概念，生效值 = 本组行）。</summary>
    public async Task<IReadOnlyList<ModuleRightsRow>> GetGroupModuleMatrixAsync(
        string adminUserId, string groupId, CancellationToken token)
    {
        await EnsureGroupExistsAsync(groupId, token);
        var modules = await navigationRepository.GetForUserAsync(adminUserId, token);
        var modulesById = modules.ToDictionary(module => module.Id);
        var rows = (await ReadGroupModuleRowsAsync(groupId, token)).ToDictionary(row => row.ModuleId);
        return modules
            .Select(module =>
            {
                var input = rows.GetValueOrDefault(module.Id)?.ToInput();
                var effective = RightsAdminLogic.AggregateModuleEffective(null, input is null ? [] : [input]);
                return BuildModuleRow(module, modulesById, input, [], effective);
            })
            .ToList();
    }

    /// <summary>批量保存个人模块权限（§4）：按 (USER_ID, M_IDX) upsert；全默认空 → 删除行（回退组权限）。</summary>
    public async Task SaveUserModuleRightsAsync(
        string targetUserId, IReadOnlyList<ModuleRightsInput> items,
        string adminUserId, string adminName, CancellationToken token)
    {
        await EnsureUserExistsAsync(targetUserId, token);
        var validation = await BuildValidationAsync(adminUserId, items, token);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            foreach (var item in items)
            {
                validation.Validate(item);
                await SaveModuleRowAsync(connection, transaction, "SYSDD", "USER_ID", targetUserId, item, token);
            }
            await WriteAuditAsync(connection, transaction, targetUserId,
                $"保存用户 {targetUserId} 个人模块权限（{items.Count} 行）", adminName, token);
            await transaction.CommitAsync(token);
            permissionCache.InvalidateAll();
            logger.LogInformation("保存用户模块权限 userId={Target} rows={Count} by={By}",
                targetUserId, items.Count, adminName);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>批量保存组模块权限（§4 同语义，SYSDH）。</summary>
    public async Task SaveGroupModuleRightsAsync(
        string groupId, IReadOnlyList<ModuleRightsInput> items,
        string adminUserId, string adminName, CancellationToken token)
    {
        await EnsureGroupExistsAsync(groupId, token);
        var validation = await BuildValidationAsync(adminUserId, items, token);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            foreach (var item in items)
            {
                validation.Validate(item);
                await SaveModuleRowAsync(connection, transaction, "SYSDH", "G_IDX", groupId, item, token);
            }
            await WriteAuditAsync(connection, transaction, groupId,
                $"保存用户组 {groupId} 模块权限（{items.Count} 行）", adminName, token);
            await transaction.CommitAsync(token);
            permissionCache.InvalidateAll();
            logger.LogInformation("保存组模块权限 groupId={Target} rows={Count} by={By}",
                groupId, items.Count, adminName);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>用户报表权限矩阵（§5.1）：可见模块 × REPORT，个人/组生效值。</summary>
    public async Task<IReadOnlyList<ReportRightsRow>> GetUserReportMatrixAsync(
        string adminUserId, string targetUserId, CancellationToken token)
    {
        await EnsureUserExistsAsync(targetUserId, token);
        var modules = await navigationRepository.GetForUserAsync(adminUserId, token);
        var moduleTitles = modules.ToDictionary(module => module.Id, module => module.Label);
        var reports = (await ReadAllReportsAsync(token))
            .Where(report => moduleTitles.ContainsKey(report.ModuleId))
            .ToList();
        var personal = (await ReadUserReportRowsAsync(targetUserId, token))
            .ToDictionary(row => (row.ModuleId, row.ReportId));
        var groups = (await ReadUserGroupReportRowsAsync(targetUserId, token))
            .GroupBy(row => (row.ModuleId, row.ReportId))
            .ToDictionary(group => group.Key, group => group.Select(row => row.ToInput()).ToList());
        return reports
            .Select(report =>
            {
                var personalInput = personal.GetValueOrDefault((report.ModuleId, report.ReportId))?.ToInput();
                var groupInputs = groups.GetValueOrDefault((report.ModuleId, report.ReportId)) ?? [];
                return BuildReportRow(report, moduleTitles[report.ModuleId], personalInput, groupInputs);
            })
            .ToList();
    }

    /// <summary>组报表权限矩阵（§5.2）。</summary>
    public async Task<IReadOnlyList<ReportRightsRow>> GetGroupReportMatrixAsync(
        string adminUserId, string groupId, CancellationToken token)
    {
        await EnsureGroupExistsAsync(groupId, token);
        var modules = await navigationRepository.GetForUserAsync(adminUserId, token);
        var moduleTitles = modules.ToDictionary(module => module.Id, module => module.Label);
        var reports = (await ReadAllReportsAsync(token))
            .Where(report => moduleTitles.ContainsKey(report.ModuleId))
            .ToList();
        var rows = (await ReadGroupReportRowsAsync(groupId, token))
            .ToDictionary(row => (row.ModuleId, row.ReportId));
        return reports
            .Select(report =>
            {
                var input = rows.GetValueOrDefault((report.ModuleId, report.ReportId))?.ToInput();
                var effective = RightsAdminLogic.AggregateReportEffective(null, input is null ? [] : [input]);
                return BuildReportRow(report, moduleTitles[report.ModuleId], input, [], effective);
            })
            .ToList();
    }

    /// <summary>批量保存个人报表权限（§4）：全 0 且 DATA_FILTER 空 → 删除行。</summary>
    public async Task SaveUserReportRightsAsync(
        string targetUserId, IReadOnlyList<ReportRightsInput> items,
        string adminUserId, string adminName, CancellationToken token)
    {
        await EnsureUserExistsAsync(targetUserId, token);
        var validation = await BuildReportValidationAsync(adminUserId, items, token);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            foreach (var item in items)
            {
                validation.Validate(item);
                await SaveReportRowAsync(connection, transaction, "SYSDD_REPORT", "USER_ID", targetUserId, item, token);
            }
            await WriteAuditAsync(connection, transaction, targetUserId,
                $"保存用户 {targetUserId} 个人报表权限（{items.Count} 行）", adminName, token);
            await transaction.CommitAsync(token);
            permissionCache.InvalidateAll();
            logger.LogInformation("保存用户报表权限 userId={Target} rows={Count} by={By}",
                targetUserId, items.Count, adminName);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>批量保存组报表权限（SYSDH_REPORT）。</summary>
    public async Task SaveGroupReportRightsAsync(
        string groupId, IReadOnlyList<ReportRightsInput> items,
        string adminUserId, string adminName, CancellationToken token)
    {
        await EnsureGroupExistsAsync(groupId, token);
        var validation = await BuildReportValidationAsync(adminUserId, items, token);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            foreach (var item in items)
            {
                validation.Validate(item);
                await SaveReportRowAsync(connection, transaction, "SYSDH_REPORT", "G_IDX", groupId, item, token);
            }
            await WriteAuditAsync(connection, transaction, groupId,
                $"保存用户组 {groupId} 报表权限（{items.Count} 行）", adminName, token);
            await transaction.CommitAsync(token);
            permissionCache.InvalidateAll();
            logger.LogInformation("保存组报表权限 groupId={Target} rows={Count} by={By}",
                groupId, items.Count, adminName);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>组列表（§5.2）：G_IDX/G_DESC/成员数。</summary>
    public async Task<IReadOnlyList<UserGroupSummary>> GetGroupsAsync(CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(g.G_IDX)),LTRIM(RTRIM(g.G_DESC)),COUNT(u.USER_ID),
                   LTRIM(RTRIM(ISNULL(g.REMARK,'')))
            FROM dbo.SYSDG g WITH (NOLOCK)
            LEFT JOIN dbo.SYSDG_USER u WITH (NOLOCK) ON u.G_IDX=g.G_IDX
            GROUP BY g.G_IDX,g.G_DESC,g.REMARK
            ORDER BY g.G_IDX;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<UserGroupSummary>();
        while (await reader.ReadAsync(token))
        {
            var remark = reader.IsDBNull(3) ? null : reader.GetString(3);
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), remark));
        }
        return result;
    }

    /// <summary>用户所属组（§5.1 GET users/{id}/groups）。</summary>
    public async Task<IReadOnlyList<UserGroupItem>> GetUserGroupsAsync(string userId, CancellationToken token)
    {
        await EnsureUserExistsAsync(userId, token);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(g.G_IDX)),LTRIM(RTRIM(g.G_DESC))
            FROM dbo.SYSDG g WITH (NOLOCK)
            INNER JOIN dbo.SYSDG_USER u WITH (NOLOCK) ON u.G_IDX=g.G_IDX
            WHERE u.USER_ID=@UserId
            ORDER BY g.G_IDX;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<UserGroupItem>();
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    /// <summary>设置用户所属组（§5.1 PUT，SYSDG_USER 按用户全量替换，事务内）。</summary>
    public async Task SaveUserGroupsAsync(
        string userId, IReadOnlyList<string> groupIds,
        string adminName, CancellationToken token)
    {
        await EnsureUserExistsAsync(userId, token);
        var distinct = groupIds.Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            foreach (var groupId in distinct)
                await EnsureGroupExistsAsync(connection, transaction, groupId, token);
            await using (var delete = new SqlCommand("DELETE FROM dbo.SYSDG_USER WHERE USER_ID=@UserId;", connection, transaction))
            {
                delete.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
                await delete.ExecuteNonQueryAsync(token);
            }
            foreach (var groupId in distinct)
            {
                await using var insert = new SqlCommand(
                    "INSERT INTO dbo.SYSDG_USER (G_IDX,USER_ID) VALUES (@GroupId,@UserId);", connection, transaction);
                insert.Parameters.Add("@GroupId", SqlDbType.NChar, 10).Value = groupId;
                insert.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
                await insert.ExecuteNonQueryAsync(token);
            }
            await WriteAuditAsync(connection, transaction, userId,
                $"设置用户 {userId} 所属组（{distinct.Count} 个：{string.Join(',', distinct)}）", adminName, token);
            await transaction.CommitAsync(token);
            permissionCache.InvalidateAll();
            logger.LogInformation("设置用户所属组 userId={User} groups={Groups} by={By}",
                userId, string.Join(',', distinct), adminName);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>组成员（§5.2 GET groups/{id}/members）。</summary>
    public async Task<IReadOnlyList<GroupMemberSummary>> GetGroupMembersAsync(string groupId, CancellationToken token)
    {
        await EnsureGroupExistsAsync(groupId, token);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(u.USER_ID)),
                   LTRIM(RTRIM(ISNULL(l.EMP_ID,''))),
                   COALESCE(NULLIF(LTRIM(RTRIM(n.EMP_NAME)),''),LTRIM(RTRIM(u.USER_ID)))
            FROM dbo.SYSDG_USER u WITH (NOLOCK)
            LEFT JOIN dbo.SYSDL l WITH (NOLOCK) ON LTRIM(RTRIM(l.USER_ID))=LTRIM(RTRIM(u.USER_ID))
            LEFT JOIN dbo.SYSDN n WITH (NOLOCK) ON l.EMP_ID=n.EMP_ID
            WHERE u.G_IDX=@GroupId
            ORDER BY u.USER_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@GroupId", SqlDbType.NChar, 10).Value = groupId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<GroupMemberSummary>();
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    /// <summary>设置组成员（§5.2 PUT，SYSDG_USER 按组全量替换，事务内）。</summary>
    public async Task SaveGroupMembersAsync(
        string groupId, IReadOnlyList<string> userIds,
        string adminName, CancellationToken token)
    {
        await EnsureGroupExistsAsync(groupId, token);
        var distinct = userIds.Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            foreach (var userId in distinct)
                await EnsureUserExistsAsync(connection, transaction, userId, token);
            await using (var delete = new SqlCommand("DELETE FROM dbo.SYSDG_USER WHERE G_IDX=@GroupId;", connection, transaction))
            {
                delete.Parameters.Add("@GroupId", SqlDbType.NChar, 10).Value = groupId.Trim();
                await delete.ExecuteNonQueryAsync(token);
            }
            foreach (var userId in distinct)
            {
                await using var insert = new SqlCommand(
                    "INSERT INTO dbo.SYSDG_USER (G_IDX,USER_ID) VALUES (@GroupId,@UserId);", connection, transaction);
                insert.Parameters.Add("@GroupId", SqlDbType.NChar, 10).Value = groupId.Trim();
                insert.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId;
                await insert.ExecuteNonQueryAsync(token);
            }
            await WriteAuditAsync(connection, transaction, groupId,
                $"设置用户组 {groupId} 成员（{distinct.Count} 个：{string.Join(',', distinct)}）", adminName, token);
            await transaction.CommitAsync(token);
            permissionCache.InvalidateAll();
            logger.LogInformation("设置组成员 groupId={Group} users={Users} by={By}",
                groupId, string.Join(',', distinct), adminName);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>
    /// 新增用户组（2305 定制页主档）：G_IDX/G_DESC/REMARK + 审计 + 权限缓存失效。
    /// 校验：组ID 必填且 ≤20 字符、组描述必填且 ≤100 字符、备注 ≤1000 字符；重复组ID 拒绝。
    /// </summary>
    public async Task CreateGroupAsync(
        string? groupId, string? groupDescription, string? remark,
        string adminName, CancellationToken token)
    {
        var id = NormalizeGroupId(groupId);
        var description = NormalizeGroupDescription(groupDescription);
        var note = NormalizeRemark(remark);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var exists = new SqlCommand(
                "SELECT COUNT(1) FROM dbo.SYSDG WITH (UPDLOCK,HOLDLOCK) WHERE LTRIM(RTRIM(G_IDX))=@Id;",
                connection, transaction))
            {
                exists.Parameters.Add("@Id", SqlDbType.NChar, 20).Value = id;
                if (Convert.ToInt32(await exists.ExecuteScalarAsync(token)) > 0)
                    throw new ArgumentException($"用户组 {id} 已存在，不能重复登记。", nameof(groupId));
            }
            await using var insert = new SqlCommand(
                """
                INSERT INTO dbo.SYSDG (G_IDX,G_DESC,REMARK,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@Id,@Desc,@Remark,@By,GETDATE(),@By,GETDATE());
                """, connection, transaction);
            insert.Parameters.Add("@Id", SqlDbType.NChar, 20).Value = id;
            insert.Parameters.Add("@Desc", SqlDbType.NVarChar, 100).Value = description;
            insert.Parameters.Add("@Remark", SqlDbType.NVarChar, 1000).Value = (object?)note ?? DBNull.Value;
            insert.Parameters.Add("@By", SqlDbType.NChar, 40).Value = adminName;
            await insert.ExecuteNonQueryAsync(token);
            await WriteAuditAsync(connection, transaction, id,
                $"新增用户组 {id}（{description}）", adminName, token, "GROUP_SAVE");
            await transaction.CommitAsync(token);
            permissionCache.InvalidateAll();
            logger.LogInformation("新增用户组 groupId={Group} by={By}", id, adminName);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>编辑用户组（2305 定制页主档）：G_DESC/REMARK + 审计；G_IDX 不可修改。</summary>
    public async Task UpdateGroupAsync(
        string groupId, string? groupDescription, string? remark,
        string adminName, CancellationToken token)
    {
        var id = NormalizeGroupId(groupId);
        var description = NormalizeGroupDescription(groupDescription);
        var note = NormalizeRemark(remark);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await EnsureGroupExistsAsync(connection, transaction, id, token);
            await using var update = new SqlCommand(
                """
                UPDATE dbo.SYSDG
                SET G_DESC=@Desc,REMARK=@Remark,LAST_UPDATE_BY=@By,LAST_UPDATE_DATE=GETDATE()
                WHERE LTRIM(RTRIM(G_IDX))=@Id;
                """, connection, transaction);
            update.Parameters.Add("@Id", SqlDbType.NChar, 20).Value = id;
            update.Parameters.Add("@Desc", SqlDbType.NVarChar, 100).Value = description;
            update.Parameters.Add("@Remark", SqlDbType.NVarChar, 1000).Value = (object?)note ?? DBNull.Value;
            update.Parameters.Add("@By", SqlDbType.NChar, 40).Value = adminName;
            await update.ExecuteNonQueryAsync(token);
            await WriteAuditAsync(connection, transaction, id,
                $"编辑用户组 {id}（{description}）", adminName, token, "GROUP_SAVE");
            await transaction.CommitAsync(token);
            permissionCache.InvalidateAll();
            logger.LogInformation("编辑用户组 groupId={Group} by={By}", id, adminName);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>
    /// 删除用户组（严格删除，向旧系统求证后修正隐患）：
    /// 旧系统 ModifyToolBar 同事务删除 SYSDG + SYSDH（模块权限）+ SYSDH_REPORT（报表权限），
    /// 但**不清理 SYSDG_USER（成员）**，留下孤儿成员行（组重建后会误挂回）。
    /// 新系统更严格：组仍关联成员时拒绝删除（必须先经「成员」页移除），
    /// 权限/报表权限随组删除同事务级联（对齐旧系统行为）。
    /// </summary>
    public async Task DeleteGroupAsync(string groupId, string adminName, CancellationToken token)
    {
        var id = NormalizeGroupId(groupId);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await EnsureGroupExistsAsync(connection, transaction, id, token);
            await using (var memberCount = new SqlCommand(
                "SELECT COUNT(1) FROM dbo.SYSDG_USER WITH (UPDLOCK,HOLDLOCK) WHERE G_IDX=@Id;",
                connection, transaction))
            {
                memberCount.Parameters.Add("@Id", SqlDbType.NChar, 20).Value = id;
                var count = Convert.ToInt32(await memberCount.ExecuteScalarAsync(token));
                if (count > 0)
                    throw new ArgumentException(
                        $"用户组 {id} 仍关联 {count} 名成员，无法删除；请先在「成员」中移除全部成员后再删除。",
                        nameof(groupId));
            }
            await using (var deleteReport = new SqlCommand(
                "DELETE FROM dbo.SYSDH_REPORT WHERE G_IDX=@Id;", connection, transaction))
            {
                deleteReport.Parameters.Add("@Id", SqlDbType.NChar, 20).Value = id;
                await deleteReport.ExecuteNonQueryAsync(token);
            }
            await using (var deleteRights = new SqlCommand(
                "DELETE FROM dbo.SYSDH WHERE G_IDX=@Id;", connection, transaction))
            {
                deleteRights.Parameters.Add("@Id", SqlDbType.NChar, 20).Value = id;
                await deleteRights.ExecuteNonQueryAsync(token);
            }
            await using (var deleteGroup = new SqlCommand(
                "DELETE FROM dbo.SYSDG WHERE LTRIM(RTRIM(G_IDX))=@Id;", connection, transaction))
            {
                deleteGroup.Parameters.Add("@Id", SqlDbType.NChar, 20).Value = id;
                await deleteGroup.ExecuteNonQueryAsync(token);
            }
            await WriteAuditAsync(connection, transaction, id,
                $"删除用户组 {id}（连同模块权限/报表权限，成员守卫已通过）", adminName, token, "GROUP_DELETE");
            await transaction.CommitAsync(token);
            permissionCache.InvalidateAll();
            logger.LogInformation("删除用户组 groupId={Group} by={By}", id, adminName);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private static string NormalizeGroupId(string? groupId)
    {
        var id = (groupId ?? string.Empty).Trim();
        if (id.Length == 0)
            throw new ArgumentException("组ID不能为空。", nameof(groupId));
        // SYSDG.G_IDX 为 nchar(10)（sys.columns.max_length=20 字节 ÷2）
        if (id.Length > 10)
            throw new ArgumentException("组ID不能超过 10 个字符。", nameof(groupId));
        return id;
    }

    private static string NormalizeGroupDescription(string? value)
    {
        var description = (value ?? string.Empty).Trim();
        if (description.Length == 0)
            throw new ArgumentException("组描述不能为空。", nameof(value));
        if (description.Length > 100)
            throw new ArgumentException("组描述不能超过 100 个字符。", nameof(value));
        return description;
    }

    private static string? NormalizeRemark(string? value)
    {
        var remark = (value ?? string.Empty).Trim();
        if (remark.Length == 0) return null;
        if (remark.Length > 1000)
            throw new ArgumentException("备注不能超过 1000 个字符。", nameof(value));
        return remark;
    }

    /// <summary>模块主/明细物理字段（§5.3，供字段级拒绝选择器；成本/保密字段按管理员权限过滤）。</summary>
    public async Task<RightsModuleFields?> GetModuleFieldsAsync(
        int moduleId, bool includeCost, bool includeSecrecy, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))),LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE,'')))
            FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX=@ModuleId;
            """;
        string master;
        string detail;
        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            master = reader.GetString(0);
            detail = reader.GetString(1);
        }
        var masterFields = await ReadFieldsAsync(connection, master, includeCost, includeSecrecy, token);
        var detailFields = string.IsNullOrEmpty(detail)
            ? []
            : await ReadFieldsAsync(connection, detail, includeCost, includeSecrecy, token);
        return new(master, string.IsNullOrEmpty(detail) ? null : detail, masterFields, detailFields);
    }

    /// <summary>单模块生效权限来源（personal/group/none，供生效值预览组合引擎聚合结果）。</summary>
    public async Task<string> GetEffectiveSourceAsync(string userId, int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.SYSDD WITH (NOLOCK)
                        WHERE USER_ID=@UserId AND M_IDX=@ModuleId) THEN 1 ELSE 0 END AS bit),
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.SYSDH h WITH (NOLOCK)
                        INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX=h.G_IDX
                        WHERE gu.USER_ID=@UserId AND h.M_IDX=@ModuleId) THEN 1 ELSE 0 END AS bit);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return "none";
        var hasPersonal = reader.GetBoolean(0);
        var hasGroup = reader.GetBoolean(1);
        return hasPersonal ? "personal" : hasGroup ? "group" : "none";
    }

    private async Task<ValidationContext> BuildValidationAsync(
        string adminUserId, IReadOnlyList<ModuleRightsInput> items, CancellationToken token)
    {
        var visible = (await navigationRepository.GetForUserAsync(adminUserId, token)).Select(module => module.Id).ToHashSet();
        var unknown = items.Select(item => item.ModuleId).Distinct().FirstOrDefault(moduleId => !visible.Contains(moduleId));
        if (unknown != 0)
            throw new ArgumentException($"模块 {unknown} 不在当前管理员可见模块范围内。", nameof(items));
        var ids = items.Select(item => item.ModuleId).Distinct().ToList();
        var meta = await ReadModuleMetaAsync(ids, token);
        var tables = meta.Values
            .SelectMany(entry => new[] { entry.Master, entry.Detail })
            .Where(table => table.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fields = await ReadFieldsByTableAsync(tables, token);
        return new ValidationContext(meta, fields);
    }

    private async Task<ReportValidationContext> BuildReportValidationAsync(
        string adminUserId, IReadOnlyList<ReportRightsInput> items, CancellationToken token)
    {
        var visible = (await navigationRepository.GetForUserAsync(adminUserId, token)).Select(module => module.Id).ToHashSet();
        var unknown = items.Select(item => item.ModuleId).Distinct().FirstOrDefault(moduleId => !visible.Contains(moduleId));
        if (unknown != 0)
            throw new ArgumentException($"模块 {unknown} 不在当前管理员可见模块范围内。", nameof(items));
        var ids = items.Select(item => item.ModuleId).Distinct().ToList();
        var meta = await ReadModuleMetaAsync(ids, token);
        var reports = await ReadAllReportsAsync(token);
        var tables = meta.Values.Select(entry => entry.Master).Where(table => table.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var fields = await ReadFieldsByTableAsync(tables, token);
        return new ReportValidationContext(meta, reports, fields);
    }

    private static ModuleRightsRow BuildModuleRow(
        LegacyNavigationModule module,
        IReadOnlyDictionary<int, LegacyNavigationModule> modulesById,
        ModuleRightsInput? editable,
        IReadOnlyList<ModuleRightsInput> groups,
        EffectiveModuleRights? effectiveOverride = null)
    {
        var effective = effectiveOverride ?? RightsAdminLogic.AggregateModuleEffective(editable, groups);
        var value = editable ?? new ModuleRightsInput(
            module.Id, null, false, false, false, false, false, false, false, false, false,
            false, false, false, false, false, false, false, false, false, false,
            null, null, null, null, null, null, null);
        return new ModuleRightsRow(
            module.Id, module.Label, BuildGroupPath(modulesById, module.Id), module.Icon,
            module.ParentId, module.RootId, module.SortIndex,
            value.ExecTag, value.AddNew, value.Edit, value.Delete, value.Approve, value.Deapprove, value.Report,
            value.Cost, value.Setup, value.Secrecy, value.EndCase, value.UnEndCase,
            value.Other1, value.Other2, value.Other3, value.Other4,
            value.FileView, value.FileUpda, value.FileEdit, value.FileDele,
            value.DenyViewMaster ?? string.Empty, value.DenyViewDetail ?? string.Empty,
            value.DenyNewMaster ?? string.Empty, value.DenyNewDetail ?? string.Empty,
            value.DenyModiMaster ?? string.Empty, value.DenyModiDetail ?? string.Empty,
            value.DataFilter ?? string.Empty,
            editable is not null, effective);
    }

    private static ReportRightsRow BuildReportRow(
        ReportRow report, string moduleTitle, ReportRightsInput? editable,
        IReadOnlyList<ReportRightsInput> groups, EffectiveReportRights? effectiveOverride = null)
    {
        var effective = effectiveOverride ?? RightsAdminLogic.AggregateReportEffective(editable, groups);
        var value = editable ?? new ReportRightsInput(report.ModuleId, report.ReportId, false, false, false, null);
        return new ReportRightsRow(
            report.ModuleId, moduleTitle, report.ReportId, report.ReportName,
            value.Preview, value.Print, value.Export, value.DataFilter ?? string.Empty,
            editable is not null, effective);
    }

    private static string BuildGroupPath(
        IReadOnlyDictionary<int, LegacyNavigationModule> modulesById, int moduleId)
    {
        var parts = new List<string>();
        var seen = new HashSet<int>();
        var current = moduleId;
        while (current != 0 && modulesById.TryGetValue(current, out var node))
        {
            if (!seen.Add(current)) break;
            if (current != moduleId) parts.Insert(0, node.Label);
            var next = node.ParentId != 0 && node.ParentId != node.Id
                ? node.ParentId
                : node.RootId != 0 && node.RootId != node.Id ? node.RootId : 0;
            if (next == current) break;
            current = next;
        }
        return string.Join(" / ", parts);
    }

    private async Task<IReadOnlyList<ModuleRowData>> ReadUserModuleRowsAsync(string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var sql = $"SELECT {string.Join(',', ModuleRowColumns)} FROM dbo.SYSDD WITH (NOLOCK) WHERE USER_ID=@Id;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.NChar, 10).Value = userId.Trim();
        return await ReadModuleRowsAsync(command, token);
    }

    private async Task<IReadOnlyList<ModuleRowData>> ReadUserGroupModuleRowsAsync(string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var sql = $"""
            SELECT h.{string.Join(",h.", ModuleRowColumns)}
            FROM dbo.SYSDH h WITH (NOLOCK)
            INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX=h.G_IDX
            WHERE gu.USER_ID=@Id;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.NChar, 10).Value = userId.Trim();
        return await ReadModuleRowsAsync(command, token);
    }

    private async Task<IReadOnlyList<ModuleRowData>> ReadGroupModuleRowsAsync(string groupId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var sql = $"SELECT {string.Join(',', ModuleRowColumns)} FROM dbo.SYSDH WITH (NOLOCK) WHERE G_IDX=@Id;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.NChar, 10).Value = groupId.Trim();
        return await ReadModuleRowsAsync(command, token);
    }

    private static async Task<IReadOnlyList<ModuleRowData>> ReadModuleRowsAsync(SqlCommand command, CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<ModuleRowData>();
        while (await reader.ReadAsync(token))
        {
            rows.Add(new(
                reader.GetInt32(0),
                reader.GetNullableString("EXEC_TAG"),
                reader.GetNullableBoolean("ADDNEW_TAG"),
                reader.GetNullableBoolean("EDIT_TAG"),
                reader.GetNullableBoolean("DELETE_TAG"),
                reader.GetNullableBoolean("APPROVE_TAG"),
                reader.GetNullableBoolean("DEAPPROVE_TAG"),
                reader.GetNullableBoolean("REPORT_TAG"),
                reader.GetNullableBoolean("COST_TAG"),
                reader.GetNullableBoolean("SETUP_TAG"),
                reader.GetNullableBoolean("SECRECY_TAG"),
                reader.GetNullableBoolean("ENDCASE_TAG"),
                reader.GetNullableBoolean("UNENDCASE_TAG"),
                reader.GetNullableBoolean("OTHER1_TAG"),
                reader.GetNullableBoolean("OTHER2_TAG"),
                reader.GetNullableBoolean("OTHER3_TAG"),
                reader.GetNullableBoolean("OTHER4_TAG"),
                reader.GetNullableBoolean("FILE_VIEW_TAG"),
                reader.GetNullableBoolean("FILE_UPDA_TAG"),
                reader.GetNullableBoolean("FILE_EDIT_TAG"),
                reader.GetNullableBoolean("FILE_DELE_TAG"),
                reader.GetNullableString("DENY_VIEW_FIELD_MASTER") ?? string.Empty,
                reader.GetNullableString("DENY_VIEW_FIELD_DETAIL") ?? string.Empty,
                reader.GetNullableString("DENY_NEW_FIELD_MASTER") ?? string.Empty,
                reader.GetNullableString("DENY_NEW_FIELD_DETAIL") ?? string.Empty,
                reader.GetNullableString("DENY_MODI_FIELD_MASTER") ?? string.Empty,
                reader.GetNullableString("DENY_MODI_FIELD_DETAIL") ?? string.Empty,
                reader.GetNullableString("DATA_FILTER") ?? string.Empty));
        }
        return rows;
    }

    private async Task<IReadOnlyList<ReportRowData>> ReadUserReportRowsAsync(string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var sql = $"SELECT {string.Join(',', ReportRowColumns)} FROM dbo.SYSDD_REPORT WITH (NOLOCK) WHERE USER_ID=@Id;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.NChar, 10).Value = userId.Trim();
        return await ReadReportRowsAsync(command, token);
    }

    private async Task<IReadOnlyList<ReportRowData>> ReadUserGroupReportRowsAsync(string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var sql = $"""
            SELECT g.{string.Join(",g.", ReportRowColumns)}
            FROM dbo.SYSDH_REPORT g WITH (NOLOCK)
            INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX=g.G_IDX
            WHERE gu.USER_ID=@Id;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.NChar, 10).Value = userId.Trim();
        return await ReadReportRowsAsync(command, token);
    }

    private async Task<IReadOnlyList<ReportRowData>> ReadGroupReportRowsAsync(string groupId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var sql = $"SELECT {string.Join(',', ReportRowColumns)} FROM dbo.SYSDH_REPORT WITH (NOLOCK) WHERE G_IDX=@Id;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.NChar, 10).Value = groupId.Trim();
        return await ReadReportRowsAsync(command, token);
    }

    private static async Task<IReadOnlyList<ReportRowData>> ReadReportRowsAsync(SqlCommand command, CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<ReportRowData>();
        while (await reader.ReadAsync(token))
        {
            rows.Add(new(
                reader.GetInt32(0),
                reader.GetString(1).Trim(),
                reader.GetNullableBoolean("PREVIEW_TAG"),
                reader.GetNullableBoolean("PRINT_TAG"),
                reader.GetNullableBoolean("EXPORT_TAG"),
                reader.GetNullableString("DATA_FILTER") ?? string.Empty));
        }
        return rows;
    }

    private async Task<IReadOnlyList<ReportRow>> ReadAllReportsAsync(CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(REPORT_ID)),LTRIM(RTRIM(ISNULL(REPORT_NAME,''))),R_M_IDX
            FROM dbo.REPORT WITH (NOLOCK);
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<ReportRow>();
        while (await reader.ReadAsync(token))
        {
            var moduleId = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            if (moduleId == 0) continue;
            rows.Add(new(reader.GetString(0), reader.GetString(1), moduleId));
        }
        return rows;
    }

    private async Task<IReadOnlyDictionary<int, ModuleMeta>> ReadModuleMetaAsync(
        IReadOnlyList<int> moduleIds, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var placeholders = string.Join(',', moduleIds.Select((_, index) => $"@m{index}"));
        var sql = $"""
            SELECT M_IDX,LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))),LTRIM(RTRIM(ISNULL(DETAIL_TABLE,'')))
            FROM dbo.MODULES WITH (NOLOCK)
            WHERE M_IDX IN ({placeholders});
            """;
        await using var command = new SqlCommand(sql, connection);
        for (var index = 0; index < moduleIds.Count; index++)
            command.Parameters.Add($"@m{index}", SqlDbType.Int).Value = moduleIds[index];
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<int, ModuleMeta>();
        while (await reader.ReadAsync(token))
            result[reader.GetInt32(0)] = new(reader.GetString(1), reader.GetString(2));
        return result;
    }

    private async Task<IReadOnlyDictionary<string, HashSet<string>>> ReadFieldsByTableAsync(
        IReadOnlyList<string> tables, CancellationToken token)
    {
        if (tables.Count == 0) return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var placeholders = string.Join(',', tables.Select((_, index) => $"@t{index}"));
        var sql = $"""
            SELECT LTRIM(RTRIM(T_ID)),LTRIM(RTRIM(F_ID))
            FROM dbo.FIELDS WITH (NOLOCK)
            WHERE T_ID IN ({placeholders});
            """;
        await using var command = new SqlCommand(sql, connection);
        for (var index = 0; index < tables.Count; index++)
            command.Parameters.Add($"@t{index}", SqlDbType.NVarChar, 100).Value = tables[index];
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            var table = reader.GetString(0);
            if (!result.TryGetValue(table, out var fields))
                result[table] = fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            fields.Add(reader.GetString(1));
        }
        return result;
    }

    private static async Task<IReadOnlyList<RightsFieldInfo>> ReadFieldsAsync(
        SqlConnection connection, string table, bool includeCost, bool includeSecrecy, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(F_ID)),LTRIM(RTRIM(ISNULL(F_DESC,''))),
                   CAST(COALESCE(IS_COST,0) AS bit),CAST(COALESCE(IS_SECRECY,0) AS bit)
            FROM dbo.FIELDS WITH (NOLOCK)
            WHERE LTRIM(RTRIM(T_ID))=@Table AND COALESCE(IS_VISIBLE,1)=1
            ORDER BY F_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<RightsFieldInfo>();
        while (await reader.ReadAsync(token))
        {
            var isCost = reader.GetBoolean(2);
            var isSecrecy = reader.GetBoolean(3);
            if (isCost && !includeCost) continue;
            if (isSecrecy && !includeSecrecy) continue;
            result.Add(new(reader.GetString(0), reader.GetString(1), isCost, isSecrecy));
        }
        return result;
    }

    private async Task SaveModuleRowAsync(
        SqlConnection connection, SqlTransaction transaction, string table, string idColumn,
        string ownerId, ModuleRightsInput item, CancellationToken token)
    {
        if (RightsAdminLogic.IsDefaultEmpty(item))
        {
            await using var delete = new SqlCommand(
                $"DELETE FROM dbo.{table} WHERE {idColumn}=@OwnerId AND M_IDX=@ModuleId;", connection, transaction);
            delete.Parameters.Add("@OwnerId", SqlDbType.NChar, 10).Value = ownerId.Trim();
            delete.Parameters.Add("@ModuleId", SqlDbType.Int).Value = item.ModuleId;
            await delete.ExecuteNonQueryAsync(token);
            return;
        }

        var execTag = RightsAdminLogic.NormalizeExecTag(item.ExecTag);
        var updateSet = string.Join(',', ModuleRowColumns.Skip(1).Select(column => $"{column}=@{column}"));
        var insertColumns = $"{idColumn},{string.Join(',', ModuleRowColumns)}";
        var insertValues = $"@OwnerId,{string.Join(',', ModuleRowColumns.Select(column =>
            column == "M_IDX" ? "@ModuleId" : $"@{column}"))}";
        var sql = $"""
            IF EXISTS (SELECT 1 FROM dbo.{table} WHERE {idColumn}=@OwnerId AND M_IDX=@ModuleId)
                UPDATE dbo.{table} SET {updateSet} WHERE {idColumn}=@OwnerId AND M_IDX=@ModuleId;
            ELSE
                INSERT INTO dbo.{table} ({insertColumns}) VALUES ({insertValues});
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@OwnerId", SqlDbType.NChar, 10).Value = ownerId.Trim();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = item.ModuleId;
        command.Parameters.Add("@EXEC_TAG", SqlDbType.Char, 1).Value = execTag;
        AddBitParameters(command, item);
        AddTextParameters(command, item);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task SaveReportRowAsync(
        SqlConnection connection, SqlTransaction transaction, string table, string idColumn,
        string ownerId, ReportRightsInput item, CancellationToken token)
    {
        if (RightsAdminLogic.IsDefaultEmpty(item))
        {
            await using var delete = new SqlCommand(
                $"DELETE FROM dbo.{table} WHERE {idColumn}=@OwnerId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId;",
                connection, transaction);
            delete.Parameters.Add("@OwnerId", SqlDbType.NChar, 10).Value = ownerId.Trim();
            delete.Parameters.Add("@ModuleId", SqlDbType.Int).Value = item.ModuleId;
            delete.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = item.ReportId.Trim();
            await delete.ExecuteNonQueryAsync(token);
            return;
        }

        var sql = $"""
            IF EXISTS (SELECT 1 FROM dbo.{table} WHERE {idColumn}=@OwnerId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId)
                UPDATE dbo.{table}
                SET PREVIEW_TAG=@PREVIEW_TAG,PRINT_TAG=@PRINT_TAG,EXPORT_TAG=@EXPORT_TAG,DATA_FILTER=@DATA_FILTER
                WHERE {idColumn}=@OwnerId AND M_IDX=@ModuleId AND REPORT_ID=@ReportId;
            ELSE
                INSERT INTO dbo.{table} ({idColumn},M_IDX,REPORT_ID,PREVIEW_TAG,PRINT_TAG,EXPORT_TAG,DATA_FILTER)
                VALUES (@OwnerId,@ModuleId,@ReportId,@PREVIEW_TAG,@PRINT_TAG,@EXPORT_TAG,@DATA_FILTER);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@OwnerId", SqlDbType.NChar, 10).Value = ownerId.Trim();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = item.ModuleId;
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = item.ReportId.Trim();
        command.Parameters.Add("@PREVIEW_TAG", SqlDbType.Bit).Value = item.Preview;
        command.Parameters.Add("@PRINT_TAG", SqlDbType.Bit).Value = item.Print;
        command.Parameters.Add("@EXPORT_TAG", SqlDbType.Bit).Value = item.Export;
        command.Parameters.Add("@DATA_FILTER", SqlDbType.NVarChar, 2000).Value =
            string.IsNullOrWhiteSpace(item.DataFilter) ? DBNull.Value : item.DataFilter.Trim();
        await command.ExecuteNonQueryAsync(token);
    }

    private static void AddBitParameters(SqlCommand command, ModuleRightsInput item)
    {
        foreach (var column in RightsColumnWhitelist.BitColumns)
        {
            var value = column switch
            {
                "ADDNEW_TAG" => item.AddNew,
                "EDIT_TAG" => item.Edit,
                "DELETE_TAG" => item.Delete,
                "APPROVE_TAG" => item.Approve,
                "DEAPPROVE_TAG" => item.Deapprove,
                "REPORT_TAG" => item.Report,
                "COST_TAG" => item.Cost,
                "SETUP_TAG" => item.Setup,
                "SECRECY_TAG" => item.Secrecy,
                "ENDCASE_TAG" => item.EndCase,
                "UNENDCASE_TAG" => item.UnEndCase,
                "OTHER1_TAG" => item.Other1,
                "OTHER2_TAG" => item.Other2,
                "OTHER3_TAG" => item.Other3,
                "OTHER4_TAG" => item.Other4,
                "FILE_VIEW_TAG" => item.FileView,
                "FILE_UPDA_TAG" => item.FileUpda,
                "FILE_EDIT_TAG" => item.FileEdit,
                "FILE_DELE_TAG" => item.FileDele,
                _ => false,
            };
            command.Parameters.Add($"@{column}", SqlDbType.Bit).Value = value;
        }
    }

    private static void AddTextParameters(SqlCommand command, ModuleRightsInput item)
    {
        command.Parameters.Add("@DENY_VIEW_FIELD_MASTER", SqlDbType.VarChar, 3000).Value =
            AddOrNull(RightsAdminLogic.NormalizeDenyList(item.DenyViewMaster));
        command.Parameters.Add("@DENY_VIEW_FIELD_DETAIL", SqlDbType.VarChar, 3000).Value =
            AddOrNull(RightsAdminLogic.NormalizeDenyList(item.DenyViewDetail));
        command.Parameters.Add("@DENY_NEW_FIELD_MASTER", SqlDbType.VarChar, 3000).Value =
            AddOrNull(RightsAdminLogic.NormalizeDenyList(item.DenyNewMaster));
        command.Parameters.Add("@DENY_NEW_FIELD_DETAIL", SqlDbType.VarChar, 3000).Value =
            AddOrNull(RightsAdminLogic.NormalizeDenyList(item.DenyNewDetail));
        command.Parameters.Add("@DENY_MODI_FIELD_MASTER", SqlDbType.VarChar, 3000).Value =
            AddOrNull(RightsAdminLogic.NormalizeDenyList(item.DenyModiMaster));
        command.Parameters.Add("@DENY_MODI_FIELD_DETAIL", SqlDbType.VarChar, 3000).Value =
            AddOrNull(RightsAdminLogic.NormalizeDenyList(item.DenyModiDetail));
        command.Parameters.Add("@DATA_FILTER", SqlDbType.VarChar, 3000).Value =
            AddOrNull(item.DataFilter?.Trim());
    }

    private static object AddOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static async Task WriteAuditAsync(
        SqlConnection connection, SqlTransaction transaction, string record, string content,
        string by, CancellationToken token, string type = "RIGHTS_SAVE")
    {
        await using var command = new SqlCommand(
            "INSERT INTO dbo.SYSDF (M_IDX,RECORD_IDX,CONTENT,TYPE,EXEC_BY,EXEC_DATE,OPERFLAG) VALUES (2306,@Record,@Content,@Type,@By,GETDATE(),1);",
            connection, transaction);
        command.Parameters.Add("@Record", SqlDbType.NVarChar, 100).Value = record;
        command.Parameters.Add("@Content", SqlDbType.NVarChar, 1000).Value = content;
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 50).Value = type;
        command.Parameters.Add("@By", SqlDbType.NVarChar, 50).Value = by;
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task EnsureUserExistsAsync(string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await EnsureUserExistsAsync(connection, null, userId, token);
    }

    private static async Task EnsureUserExistsAsync(
        SqlConnection connection, SqlTransaction? transaction, string userId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.SYSDL WITH (NOLOCK) WHERE LTRIM(RTRIM(USER_ID))=@Id;", connection, transaction);
        command.Parameters.Add("@Id", SqlDbType.NChar, 10).Value = userId.Trim();
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        if (count == 0) throw new KeyNotFoundException($"用户 {userId.Trim()} 不存在。");
    }

    private async Task EnsureGroupExistsAsync(string groupId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await EnsureGroupExistsAsync(connection, null, groupId, token);
    }

    private static async Task EnsureGroupExistsAsync(
        SqlConnection connection, SqlTransaction? transaction, string groupId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.SYSDG WITH (NOLOCK) WHERE G_IDX=@Id;", connection, transaction);
        command.Parameters.Add("@Id", SqlDbType.NChar, 10).Value = groupId.Trim();
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        if (count == 0) throw new KeyNotFoundException($"用户组 {groupId.Trim()} 不存在。");
    }

    private sealed record ModuleRowData(
        int ModuleId,
        string? ExecTag,
        bool AddNew, bool Edit, bool Delete, bool Approve, bool Deapprove, bool Report,
        bool Cost, bool Setup, bool Secrecy, bool EndCase, bool UnEndCase,
        bool Other1, bool Other2, bool Other3, bool Other4,
        bool FileView, bool FileUpda, bool FileEdit, bool FileDele,
        string DenyViewMaster, string DenyViewDetail, string DenyNewMaster, string DenyNewDetail,
        string DenyModiMaster, string DenyModiDetail, string DataFilter)
    {
        public ModuleRightsInput ToInput() => new(
            ModuleId, ExecTag, AddNew, Edit, Delete, Approve, Deapprove, Report,
            Cost, Setup, Secrecy, EndCase, UnEndCase, Other1, Other2, Other3, Other4,
            FileView, FileUpda, FileEdit, FileDele,
            DenyViewMaster, DenyViewDetail, DenyNewMaster, DenyNewDetail,
            DenyModiMaster, DenyModiDetail, DataFilter);
    }

    private sealed record ReportRowData(
        int ModuleId,
        string ReportId,
        bool Preview,
        bool Print,
        bool Export,
        string DataFilter)
    {
        public ReportRightsInput ToInput() => new(ModuleId, ReportId, Preview, Print, Export, DataFilter);
    }

    private sealed record ReportRow(string ReportId, string ReportName, int ModuleId);
    private sealed record ModuleMeta(string Master, string Detail);

    private sealed class ValidationContext(
        IReadOnlyDictionary<int, ModuleMeta> meta,
        IReadOnlyDictionary<string, HashSet<string>> fields)
    {
        public void Validate(ModuleRightsInput item)
        {
            if (!meta.TryGetValue(item.ModuleId, out var moduleMeta))
                throw new KeyNotFoundException($"模块 {item.ModuleId} 不存在。");
            RightsAdminLogic.NormalizeExecTag(item.ExecTag);
            ValidateDeny(item.DenyViewMaster, moduleMeta.Master, "主表禁止查看字段");
            ValidateDeny(item.DenyViewDetail, moduleMeta.Detail, "副表禁止查看字段");
            ValidateDeny(item.DenyNewMaster, moduleMeta.Master, "主表禁止新增字段");
            ValidateDeny(item.DenyNewDetail, moduleMeta.Detail, "副表禁止新增字段");
            ValidateDeny(item.DenyModiMaster, moduleMeta.Master, "主表禁止修改字段");
            ValidateDeny(item.DenyModiDetail, moduleMeta.Detail, "副表禁止修改字段");
            ValidateDataFilter(item.DataFilter, moduleMeta.Master, item.ModuleId);
        }

        private void ValidateDeny(string? value, string table, string label)
        {
            foreach (var field in RightsAdminLogic.ParseDenyList(value))
            {
                if (table.Length == 0 || !fields.TryGetValue(table, out var allowed) || !allowed.Contains(field))
                    throw new ArgumentException($"{label} {field} 不是模块表 {table} 的字段。");
            }
        }

        private void ValidateDataFilter(string? value, string masterTable, int moduleId)
        {
            var filter = (value ?? string.Empty).Trim();
            if (filter.Length == 0) return;
            if (masterTable.Length == 0)
                throw new ArgumentException($"模块 {moduleId} 未配置主表，无法校验 DATA_FILTER。");
            var allowed = fields.GetValueOrDefault(masterTable)
                ?? throw new ArgumentException($"模块 {moduleId} 主表 {masterTable} 无字段元数据，无法校验 DATA_FILTER。");
            if (!DataFilterParser.TryParse(filter, masterTable, allowed, out _, out _))
                throw new ArgumentException($"DATA_FILTER 无法通过受控解析（模块 {moduleId}）：{filter}");
        }
    }

    private sealed class ReportValidationContext(
        IReadOnlyDictionary<int, ModuleMeta> meta,
        IReadOnlyList<ReportRow> reports,
        IReadOnlyDictionary<string, HashSet<string>> fields)
    {
        public void Validate(ReportRightsInput item)
        {
            if (!meta.TryGetValue(item.ModuleId, out var moduleMeta))
                throw new KeyNotFoundException($"模块 {item.ModuleId} 不存在。");
            if (!reports.Any(report => report.ModuleId == item.ModuleId
                    && string.Equals(report.ReportId, item.ReportId.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"报表 {item.ReportId} 不属于模块 {item.ModuleId}。");
            var filter = (item.DataFilter ?? string.Empty).Trim();
            if (filter.Length == 0) return;
            if (moduleMeta.Master.Length == 0)
                throw new ArgumentException($"模块 {item.ModuleId} 未配置主表，无法校验 DATA_FILTER。");
            var allowed = fields.GetValueOrDefault(moduleMeta.Master)
                ?? throw new ArgumentException($"模块 {item.ModuleId} 主表 {moduleMeta.Master} 无字段元数据，无法校验 DATA_FILTER。");
            if (!DataFilterParser.TryParse(filter, moduleMeta.Master, allowed, out _, out _))
                throw new ArgumentException($"DATA_FILTER 无法通过受控解析（模块 {item.ModuleId}）：{filter}");
        }
    }
}
