using System.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 统一选择器数据源（POST /api/chooser/query）。
/// sourceKey → 服务端注册定义：可排序列白名单、关键字表达式、权限模块。
/// 表名/列名只来自本文件注册表（编译期常量），args 逐项白名单校验，查询全部参数化。
/// </summary>
public sealed class ChooserRepository(DbConnectionFactory connections, ILogger<ChooserRepository> logger)
{
    private const int MenuAdminModuleId = 2301;
    private const int ReportAdminModuleId = 2201;
    private const int FieldAdminModuleId = 2302;

    /// <summary>注册数据源 → 可排序列白名单（首列为默认排序列，稳定次序列固定追加）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RegisteredSources =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // 库位主档（权限门 110309）：列与排序白名单由服务端控制，不信任前端提交
            ["depot-admin.locations"] = ["DEPOT_ID", "LOCATION_NO", "LOCATION_NAME", "LOCATION_TYPE", "STORAGE_TYPE", "PARENT_NO", "SEQ_NO", "STATUS"],
            ["menu-admin.tables"] = ["T_DESC", "T_ID", "T_KIND", "T_TYPE"],
            ["field-admin.tables"] = ["T_DESC", "T_ID", "T_KIND", "T_TYPE"],
            ["field-admin.columns"] = ["COLUMN_NAME", "DATA_TYPE"],
            ["menu-admin.columns"] = ["COLUMN_NAME", "DATA_TYPE"],
            ["menu-admin.modules"] = ["M_IDX", "M_DESC"],
            ["field-admin.fields"] = ["F_ID", "F_DESC", "F_TYPE"],
            ["menu-admin.fields"] = ["F_ID", "F_DESC", "F_TYPE"],
            ["menu-admin.sprocs"] = ["SP_NAME"],
            ["report-admin.fields"] = ["T_ID", "F_ID", "F_DESC", "F_TYPE"],
            ["report-admin.modules"] = ["M_IDX", "M_DESC"],
            // 权限复制来源（C11）：SYSDL 用户账号 / SYSDG 用户组
            ["rights-admin.users"] = ["USER_ID", "EMP_NAME"],
            ["rights-admin.groups"] = ["G_IDX", "G_DESC"],
            // 员工源显示列/可排序列来自 110104（SYSDN）字段元数据，运行时动态解析，这里仅登记默认排序列
            ["user-admin.employees"] = ["EMP_ID"],
            // 单据源：列与排序按模块主表的主键在运行时解析（每个模块的主表不同），故这里不登记列
            ["menu-admin.records"] = [],
        };

    /// <summary>注册数据源 → 排序稳定次序列（与排序列去重后追加，保证分页顺序稳定）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> StableSortColumns =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["depot-admin.locations"] = ["DEPOT_ID", "LOCATION_NO"],
            ["menu-admin.tables"] = ["T_ID"],
            ["field-admin.tables"] = ["T_ID"],
            ["field-admin.columns"] = ["COLUMN_NAME"],
            ["menu-admin.columns"] = ["COLUMN_NAME"],
            ["menu-admin.modules"] = ["M_IDX"],
            ["field-admin.fields"] = ["F_ID"],
            ["menu-admin.fields"] = ["F_ID"],
            ["menu-admin.sprocs"] = ["SP_NAME"],
            ["report-admin.fields"] = ["T_ID", "F_ID"],
            ["report-admin.modules"] = ["M_IDX"],
            ["rights-admin.users"] = ["USER_ID"],
            ["rights-admin.groups"] = ["G_IDX"],
            ["user-admin.employees"] = ["EMP_ID"],
        };

    /// <summary>注册数据源 → 列键 → 关键字 LIKE 表达式（编译期常量，安全拼接）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> KeywordExpressions =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["depot-admin.locations"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LOCATION_NO"] = "LTRIM(RTRIM(L.LOCATION_NO)) LIKE @Keyword",
                ["LOCATION_NAME"] = "LTRIM(RTRIM(ISNULL(L.LOCATION_NAME,''))) LIKE @Keyword",
                ["PARENT_NO"] = "LTRIM(RTRIM(ISNULL(L.PARENT_NO,''))) LIKE @Keyword",
            },
            ["menu-admin.tables"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["T_ID"] = "LTRIM(RTRIM(T_ID)) LIKE @Keyword",
                ["T_DESC"] = "LTRIM(RTRIM(T_DESC)) LIKE @Keyword",
                ["T_KIND"] = "LTRIM(RTRIM(ISNULL(T_KIND,''))) LIKE @Keyword",
                ["T_TYPE"] = "LTRIM(RTRIM(ISNULL(T_TYPE,''))) LIKE @Keyword",
            },
            ["field-admin.tables"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["T_ID"] = "LTRIM(RTRIM(T_ID)) LIKE @Keyword",
                ["T_DESC"] = "LTRIM(RTRIM(T_DESC)) LIKE @Keyword",
                ["T_KIND"] = "LTRIM(RTRIM(ISNULL(T_KIND,''))) LIKE @Keyword",
                ["T_TYPE"] = "LTRIM(RTRIM(ISNULL(T_TYPE,''))) LIKE @Keyword",
            },
            ["field-admin.columns"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["COLUMN_NAME"] = "LTRIM(RTRIM(c.name)) LIKE @Keyword",
            },
            ["menu-admin.columns"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["COLUMN_NAME"] = "LTRIM(RTRIM(c.name)) LIKE @Keyword",
            },
            ["menu-admin.modules"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["M_IDX"] = "LTRIM(RTRIM(CAST(m.M_IDX AS nvarchar(20)))) LIKE @Keyword",
                ["M_DESC"] = "LTRIM(RTRIM(ISNULL(m.M_DESC,''))) LIKE @Keyword",
            },
            ["field-admin.fields"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["F_ID"] = "LTRIM(RTRIM(f.F_ID)) LIKE @Keyword",
                // 字段名口径：空串与历史占位文本（'NULL' / '&nbsp;'，曾被一次性元数据补齐脚本
                // 当成"无说明"写进 F_DESC）一律视为"没有名字"，回落字段代号——占位文本非空，
                // 只判空串的兜底拦不住，会原样显示到列上、也搜不到。本文件所有 F_DESC 表达式同此口径。
                ["F_DESC"] = "COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID))) LIKE @Keyword",
                ["F_TYPE"] = "COALESCE(LTRIM(RTRIM(f.F_TYPE)),'nvarchar') LIKE @Keyword",
            },
            ["menu-admin.fields"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["F_ID"] = "LTRIM(RTRIM(f.F_ID)) LIKE @Keyword",
                ["F_DESC"] = "COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID))) LIKE @Keyword",
                ["F_TYPE"] = "COALESCE(LTRIM(RTRIM(f.F_TYPE)),'nvarchar') LIKE @Keyword",
            },
            ["menu-admin.sprocs"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SP_NAME"] = "LTRIM(RTRIM(p.name)) LIKE @Keyword",
            },
            ["report-admin.fields"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["T_ID"] = "LTRIM(RTRIM(t.T_ID)) LIKE @Keyword",
                ["F_ID"] = "LTRIM(RTRIM(c.F_ID)) LIKE @Keyword",
                ["F_DESC"] = "COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(c.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(c.F_ID))) LIKE @Keyword",
            },
            ["report-admin.modules"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["M_IDX"] = "LTRIM(RTRIM(CAST(m.M_IDX AS nvarchar(20)))) LIKE @Keyword",
                ["M_DESC"] = "LTRIM(RTRIM(ISNULL(m.M_DESC,''))) LIKE @Keyword",
            },
            ["rights-admin.users"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["USER_ID"] = "LTRIM(RTRIM(l.USER_ID)) LIKE @Keyword",
                ["EMP_NAME"] = "COALESCE(NULLIF(LTRIM(RTRIM(n.EMP_NAME)),''),LTRIM(RTRIM(l.USER_ID))) LIKE @Keyword",
            },
            ["rights-admin.groups"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["G_IDX"] = "LTRIM(RTRIM(G_IDX)) LIKE @Keyword",
                ["G_DESC"] = "LTRIM(RTRIM(ISNULL(G_DESC,''))) LIKE @Keyword",
            },
        };

    /// <summary>注册数据源 → 列键 → SQL 列表达式（高级查询白名单，编译期常量）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ColumnExpressions =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["depot-admin.locations"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DEPOT_ID"] = "LTRIM(RTRIM(L.DEPOT_ID))",
                ["LOCATION_NO"] = "LTRIM(RTRIM(L.LOCATION_NO))",
                ["LOCATION_TYPE"] = "LTRIM(RTRIM(L.LOCATION_TYPE))",
                ["STORAGE_TYPE"] = "LTRIM(RTRIM(ISNULL(L.STORAGE_TYPE,'')))",
                ["STATUS"] = "LTRIM(RTRIM(L.STATUS))",
            },
            ["menu-admin.tables"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["T_ID"] = "LTRIM(RTRIM(T_ID))",
                ["T_DESC"] = "LTRIM(RTRIM(T_DESC))",
                ["T_KIND"] = "LTRIM(RTRIM(ISNULL(T_KIND,'')))",
                ["T_TYPE"] = "LTRIM(RTRIM(ISNULL(T_TYPE,'')))",
            },
            ["field-admin.tables"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["T_ID"] = "LTRIM(RTRIM(T_ID))",
                ["T_DESC"] = "LTRIM(RTRIM(T_DESC))",
                ["T_KIND"] = "LTRIM(RTRIM(ISNULL(T_KIND,'')))",
                ["T_TYPE"] = "LTRIM(RTRIM(ISNULL(T_TYPE,'')))",
            },
            ["field-admin.columns"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["COLUMN_NAME"] = "LTRIM(RTRIM(c.name))",
            },
            ["menu-admin.columns"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["COLUMN_NAME"] = "LTRIM(RTRIM(c.name))",
            },
            ["menu-admin.modules"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["M_IDX"] = "m.M_IDX",
                ["M_DESC"] = "LTRIM(RTRIM(ISNULL(m.M_DESC,'')))",
            },
            ["field-admin.fields"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["F_ID"] = "LTRIM(RTRIM(f.F_ID))",
                ["F_DESC"] = "COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID)))",
                ["F_TYPE"] = "COALESCE(LTRIM(RTRIM(f.F_TYPE)),'nvarchar')",
            },
            ["menu-admin.fields"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["F_ID"] = "LTRIM(RTRIM(f.F_ID))",
                ["F_DESC"] = "COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID)))",
                ["F_TYPE"] = "COALESCE(LTRIM(RTRIM(f.F_TYPE)),'nvarchar')",
            },
            ["menu-admin.sprocs"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SP_NAME"] = "LTRIM(RTRIM(p.name))",
            },
            ["report-admin.fields"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["T_ID"] = "LTRIM(RTRIM(t.T_ID))",
                ["F_ID"] = "LTRIM(RTRIM(c.F_ID))",
                ["F_DESC"] = "COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(c.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(c.F_ID)))",
                ["F_TYPE"] = "COALESCE(LTRIM(RTRIM(c.F_TYPE)),'nvarchar')",
            },
            ["report-admin.modules"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["M_IDX"] = "m.M_IDX",
                ["M_DESC"] = "LTRIM(RTRIM(ISNULL(m.M_DESC,'')))",
            },
            ["rights-admin.users"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["USER_ID"] = "LTRIM(RTRIM(l.USER_ID))",
                ["EMP_NAME"] = "COALESCE(NULLIF(LTRIM(RTRIM(n.EMP_NAME)),''),LTRIM(RTRIM(l.USER_ID)))",
            },
            ["rights-admin.groups"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["G_IDX"] = "LTRIM(RTRIM(G_IDX))",
                ["G_DESC"] = "LTRIM(RTRIM(ISNULL(G_DESC,'')))",
            },
        };

    public static bool IsRegistered(string? sourceKey) =>
        !string.IsNullOrWhiteSpace(sourceKey) && RegisteredSources.ContainsKey(sourceKey.Trim());

    /// <summary>数据源权限门：返回需要校验的模块号（null 表示仅登录可读，按场景收紧）。</summary>
    public static int? PermissionModuleId(string? sourceKey) => sourceKey?.Trim().ToLowerInvariant() switch
    {
        "menu-admin.tables" or "menu-admin.fields" or "menu-admin.sprocs" or "menu-admin.columns" or "menu-admin.modules" or "menu-admin.records" => MenuAdminModuleId,
        "field-admin.tables" or "field-admin.columns" or "field-admin.fields" => FieldAdminModuleId,
        "report-admin.fields" or "report-admin.modules" => ReportAdminModuleId,
        "rights-admin.users" or "rights-admin.groups" => PermissionModules.SystemManagement,
        "user-admin.employees" => PermissionModules.SystemManagement,
        // 库位主档：权限门挂 110309（库位主档模块）
        "depot-admin.locations" => 110309,
        _ => null,
    };

    /// <summary>排序列白名单解析：非法/缺失回退首列（默认排序）；方向仅 asc/desc。</summary>
    internal static (string Column, string Direction) ResolveSort(string sourceKey, string? sortField, string? sortDirection)
    {
        // A source whose columns are resolved at runtime (its table depends on the arguments)
        // registers no columns: it resolves ordering itself and never comes through here.
        var allowed = RegisteredSources.TryGetValue(sourceKey.Trim(), out var columns) ? columns : [];
        var column = allowed.FirstOrDefault(item => item.Equals(sortField, StringComparison.OrdinalIgnoreCase))
            ?? (allowed.Count > 0 ? allowed[0] : string.Empty);
        var direction = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        return (column, direction);
    }

    internal static int NormalizePage(int page) => Math.Max(1, page);

    internal static int NormalizePageSize(int pageSize) => Math.Clamp(pageSize, 10, 100);

    /// <summary>menu-admin.fields 的 args 校验：仅允许 tableId，且须命中标识符白名单。</summary>
    internal static string? ResolveFieldsTableId(IReadOnlyDictionary<string, string>? args)
    {
        if (args is null || !args.TryGetValue("tableId", out var tableId) || string.IsNullOrWhiteSpace(tableId))
            return null;
        return WorkbenchSql.Identifier.IsMatch(tableId.Trim()) ? tableId.Trim() : null;
    }

    /// <summary>report-admin.fields 共用的 args 校验：moduleId 必须为正整数。</summary>
    internal static int? ResolveReportModuleId(IReadOnlyDictionary<string, string>? args)
    {
        if (args is null || !args.TryGetValue("moduleId", out var raw) || !int.TryParse(raw, out var moduleId) || moduleId <= 0)
            return null;
        return moduleId;
    }

    public async Task<UnifiedChooserResult?> QueryAsync(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        var sourceKey = request.SourceKey?.Trim();
        if (!IsRegistered(sourceKey)) return null;
        return sourceKey!.ToLowerInvariant() switch
        {
            "menu-admin.tables" => await QueryTablesAsync(request, token),
            "field-admin.tables" => await QueryTablesAsync(request, token),
            "field-admin.columns" => await QueryColumnsAsync(request, token, sourceKey!),
            "menu-admin.columns" => await QueryColumnsAsync(request, token, sourceKey!),
            "menu-admin.modules" => await QueryBusinessModulesAsync(request, token),
            "field-admin.fields" => await QueryFieldsAsync(request, token),
            "menu-admin.fields" => await QueryFieldsAsync(request, token),
            "menu-admin.sprocs" => await QuerySprocsAsync(request, token),
            "report-admin.fields" => await QueryReportFieldsAsync(request, token, sourceKey!),
            "report-admin.modules" => await QueryModulesAsync(request, token),
            "rights-admin.users" => await QueryUsersAsync(request, token),
            "rights-admin.groups" => await QueryGroupsAsync(request, token),
            "user-admin.employees" => await QueryEmployeesAsync(request, token),
            "menu-admin.records" => await QueryModuleRecordsAsync(request, token),
            "depot-admin.locations" => await QueryDepotLocationsAsync(request, token),
            _ => null,
        };
    }

    /// <summary>
    /// depot-admin.locations：库位主档数据源（权限门 110309）。
    /// **哨兵行（LOCATION_NO = '-'）一律不出现在结果里**：那是"未指定位置"的内部占位，
    /// 由过账路径在归一化时产生，不允许用户在选择器里手工选中。
    /// </summary>
    private async Task<UnifiedChooserResult?> QueryDepotLocationsAsync(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        const string sourceKey = "depot-admin.locations";
        var (sortColumn, direction) = ResolveSort(sourceKey, request.SortField, request.SortDirection);
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        var keywordPredicate = BuildKeywordPredicate(sourceKey, request.FilterField);
        var orderBy = BuildOrderBy(sourceKey, sortColumn, direction);
        await using var connection = connections.Create();
        await using var command = new SqlCommand { Connection = connection };
        AddCommonParameters(command, keyword, page, pageSize);
        var conditionPredicate = ChooserConditionBuilder.Build(request.Conditions, ColumnExpressions[sourceKey], command);
        var conditionSql = conditionPredicate is null ? string.Empty : $" AND {conditionPredicate}";
        var sql = $"""
            SELECT COUNT_BIG(1) FROM dbo.DEPOT_LOCATION L WITH (NOLOCK)
            WHERE L.LOCATION_NO <> N'-' AND (@Keyword = '' OR {keywordPredicate}){conditionSql};
            SELECT LTRIM(RTRIM(L.DEPOT_ID)) AS DEPOT_ID,LTRIM(RTRIM(L.LOCATION_NO)) AS LOCATION_NO,
                   LTRIM(RTRIM(ISNULL(L.LOCATION_NAME,''))) AS LOCATION_NAME,
                   LTRIM(RTRIM(L.LOCATION_TYPE)) AS LOCATION_TYPE,
                   LTRIM(RTRIM(ISNULL(L.STORAGE_TYPE,''))) AS STORAGE_TYPE,
                   LTRIM(RTRIM(ISNULL(L.PARENT_NO,''))) AS PARENT_NO,
                   L.SEQ_NO AS SEQ_NO,LTRIM(RTRIM(L.STATUS)) AS STATUS
            FROM dbo.DEPOT_LOCATION L WITH (NOLOCK)
            WHERE L.LOCATION_NO <> N'-' AND (@Keyword = '' OR {keywordPredicate}){conditionSql}
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.CommandText = sql;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, ["DEPOT_ID", "LOCATION_NO", "LOCATION_NAME", "LOCATION_TYPE", "STORAGE_TYPE", "PARENT_NO", "SEQ_NO", "STATUS"]);
        logger.LogInformation("统一选择器查询 source={Source} page={Page} size={PageSize} total={Total} rows={Rows}",
            sourceKey, page, pageSize, total, rows.Count);
        return new UnifiedChooserResult(
            [
                new UnifiedChooserColumn("DEPOT_ID", "库别", "nvarchar", null),
                new UnifiedChooserColumn("LOCATION_NO", "库位编号", "nvarchar", null),
                new UnifiedChooserColumn("LOCATION_NAME", "位置名称", "nvarchar", null),
                new UnifiedChooserColumn("LOCATION_TYPE", "位置类型", "nvarchar", null),
                new UnifiedChooserColumn("STORAGE_TYPE", "存放用途", "nvarchar", null),
                new UnifiedChooserColumn("PARENT_NO", "上级库位", "nvarchar", null),
                new UnifiedChooserColumn("SEQ_NO", "排序", "int", null),
                new UnifiedChooserColumn("STATUS", "状态", "nvarchar", null),
            ],
            rows,
            total);
    }

    private async Task<UnifiedChooserResult?> QuerySprocsAsync(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        const string sourceKey = "menu-admin.sprocs";
        var (sortColumn, direction) = ResolveSort(sourceKey, request.SortField, request.SortDirection);
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        var keywordPredicate = BuildKeywordPredicate(sourceKey, request.FilterField);
        var orderBy = BuildOrderBy(sourceKey, sortColumn, direction);
        await using var connection = connections.Create();
        await using var command = new SqlCommand { Connection = connection };
        AddCommonParameters(command, keyword, page, pageSize);
        var conditionPredicate = ChooserConditionBuilder.Build(request.Conditions, ColumnExpressions[sourceKey], command);
        var conditionSql = conditionPredicate is null ? string.Empty : $" AND {conditionPredicate}";
        // 仅 dbo 架构、非系统过程（is_ms_shipped=0 且排除 sp_/xp_/dt_ 前缀），供菜单管理选择 AFTERSAVE_SP/UPDATE_SP。
        var sql = $"""
            SELECT COUNT_BIG(1)
            FROM sys.procedures p
            WHERE p.schema_id=SCHEMA_ID('dbo') AND p.is_ms_shipped=0
              AND p.name NOT LIKE 'sp[_]%' AND p.name NOT LIKE 'xp[_]%' AND p.name NOT LIKE 'dt[_]%'
              AND (@Keyword = '' OR {keywordPredicate}){conditionSql};
            SELECT LTRIM(RTRIM(p.name)) AS SP_NAME
            FROM sys.procedures p
            WHERE p.schema_id=SCHEMA_ID('dbo') AND p.is_ms_shipped=0
              AND p.name NOT LIKE 'sp[_]%' AND p.name NOT LIKE 'xp[_]%' AND p.name NOT LIKE 'dt[_]%'
              AND (@Keyword = '' OR {keywordPredicate}){conditionSql}
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.CommandText = sql;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, ["SP_NAME"]);
        logger.LogInformation("统一选择器存储过程源查询 page={Page} size={PageSize} total={Total} rows={Rows}",
            page, pageSize, total, rows.Count);
        return new UnifiedChooserResult(
            [new UnifiedChooserColumn("SP_NAME", "存储过程名", "nvarchar", null)],
            rows,
            total);
    }

    private async Task<UnifiedChooserResult> QueryTablesAsync(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        const string sourceKey = "menu-admin.tables";
        var (sortColumn, direction) = ResolveSort(sourceKey, request.SortField, request.SortDirection);
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        var keywordPredicate = BuildKeywordPredicate(sourceKey, request.FilterField);
        var orderBy = BuildOrderBy(sourceKey, sortColumn, direction);
        await using var connection = connections.Create();
        await using var command = new SqlCommand { Connection = connection };
        AddCommonParameters(command, keyword, page, pageSize);
        var conditionPredicate = ChooserConditionBuilder.Build(request.Conditions, ColumnExpressions[sourceKey], command);
        var conditionSql = conditionPredicate is null ? string.Empty : $" AND {conditionPredicate}";
        var sql = $"""
            SELECT COUNT_BIG(1) FROM dbo.TABLES WITH (NOLOCK)
            WHERE (@Keyword = '' OR {keywordPredicate}){conditionSql};
            SELECT LTRIM(RTRIM(T_ID)) AS T_ID,LTRIM(RTRIM(T_DESC)) AS T_DESC,
                   LTRIM(RTRIM(ISNULL(T_KIND,''))) AS T_KIND,LTRIM(RTRIM(ISNULL(T_TYPE,''))) AS T_TYPE
            FROM dbo.TABLES WITH (NOLOCK)
            WHERE (@Keyword = '' OR {keywordPredicate}){conditionSql}
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.CommandText = sql;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, ["T_ID", "T_DESC", "T_KIND", "T_TYPE"]);
        logger.LogInformation("统一选择器查询 source={Source} page={Page} size={PageSize} total={Total} rows={Rows}",
            sourceKey, page, pageSize, total, rows.Count);
        return new UnifiedChooserResult(
            [
                new UnifiedChooserColumn("T_ID", "表名", "nvarchar", null),
                new UnifiedChooserColumn("T_DESC", "描述", "nvarchar", null),
                new UnifiedChooserColumn("T_KIND", "类型", "nvarchar", null),
                new UnifiedChooserColumn("T_TYPE", "种类", "nvarchar", null),
            ],
            rows,
            total);
    }

    /// <summary>field-admin.columns / menu-admin.columns：按表返回物理列（sys.columns，仅 dbo 表/视图；权限门 2302 / 2301）。</summary>
    private async Task<UnifiedChooserResult?> QueryColumnsAsync(
        UnifiedChooserQueryRequest request,
        CancellationToken token,
        string sourceKey)
    {
        var tableId = ResolveFieldsTableId(request.Args);
        if (tableId is null) return null;
        var (sortColumn, direction) = ResolveSort(sourceKey, request.SortField, request.SortDirection);
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        var keywordPredicate = BuildKeywordPredicate(sourceKey, request.FilterField);
        var orderBy = BuildOrderBy(sourceKey, sortColumn, direction);
        await using var connection = connections.Create();
        await using var command = new SqlCommand { Connection = connection };
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        AddCommonParameters(command, keyword, page, pageSize);
        var sql = $"""
            SELECT COUNT_BIG(1)
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@TableId AND (@Keyword = '' OR {keywordPredicate});
            SELECT LTRIM(RTRIM(c.name)) AS COLUMN_NAME, TYPE_NAME(c.user_type_id) AS DATA_TYPE
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@TableId AND (@Keyword = '' OR {keywordPredicate})
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.CommandText = sql;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, ["COLUMN_NAME", "DATA_TYPE"]);
        logger.LogInformation("统一选择器物理列查询 table={Table} page={Page} total={Total} rows={Rows}",
            tableId, page, total, rows.Count);
        return new UnifiedChooserResult(
            [
                new UnifiedChooserColumn("COLUMN_NAME", "列名", "nvarchar", null),
                new UnifiedChooserColumn("DATA_TYPE", "类型", "nvarchar", null),
            ],
            rows,
            total);
    }

    private async Task<UnifiedChooserResult> QueryModulesAsync(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        const string sourceKey = "report-admin.modules";
        var (sortColumn, direction) = ResolveSort(sourceKey, request.SortField, request.SortDirection);
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        var keywordPredicate = BuildKeywordPredicate(sourceKey, request.FilterField);
        var orderBy = BuildOrderBy(sourceKey, sortColumn, direction);
        await using var connection = connections.Create();
        await using var command = new SqlCommand { Connection = connection };
        AddCommonParameters(command, keyword, page, pageSize);
        var conditionPredicate = ChooserConditionBuilder.Build(request.Conditions, ColumnExpressions[sourceKey], command);
        var conditionSql = conditionPredicate is null ? string.Empty : $" AND {conditionPredicate}";
        // 报表所属模块候选：启用且具备报表承载能力（RPT/ URL）或已挂报表的模块
        const string scope = """
            ISNULL(m.M_TAG,1)=1
            AND (LTRIM(RTRIM(ISNULL(m.M_URL,''))) LIKE 'RPT/%'
                 OR LTRIM(RTRIM(ISNULL(m.M_URL,''))) LIKE '~/RPT/%'
                 OR EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK) WHERE r.R_M_IDX=m.M_IDX))
            """;
        var sql = $"""
            SELECT COUNT_BIG(1) FROM dbo.MODULES m WITH (NOLOCK)
            WHERE {scope} AND (@Keyword = '' OR {keywordPredicate}){conditionSql};
            SELECT m.M_IDX,LTRIM(RTRIM(ISNULL(m.M_DESC,''))) AS M_DESC
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE {scope} AND (@Keyword = '' OR {keywordPredicate}){conditionSql}
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.CommandText = sql;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, ["M_IDX", "M_DESC"]);
        logger.LogInformation("统一选择器模块源查询 page={Page} size={PageSize} total={Total} rows={Rows}",
            page, pageSize, total, rows.Count);
        return new UnifiedChooserResult(
            [
                new UnifiedChooserColumn("M_IDX", "模块号", "int", null),
                new UnifiedChooserColumn("M_DESC", "模块名", "nvarchar", null),
            ],
            rows,
            total);
    }

    /// <summary>
    /// menu-admin.modules：业务动作配置可克隆来源的模块——只列**已配过业务动作**的模块，
    /// 附带主/副表与动作数，便于挑"相似的加工单"。权限门 2301。
    /// </summary>
    private async Task<UnifiedChooserResult> QueryBusinessModulesAsync(
        UnifiedChooserQueryRequest request,
        CancellationToken token)
    {
        const string sourceKey = "menu-admin.modules";
        var (sortColumn, direction) = ResolveSort(sourceKey, request.SortField, request.SortDirection);
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        var keywordPredicate = BuildKeywordPredicate(sourceKey, request.FilterField);
        var orderBy = BuildOrderBy(sourceKey, sortColumn, direction);
        await using var connection = connections.Create();
        await using var command = new SqlCommand { Connection = connection };
        AddCommonParameters(command, keyword, page, pageSize);
        var conditionPredicate = ChooserConditionBuilder.Build(request.Conditions, ColumnExpressions[sourceKey], command);
        var conditionSql = conditionPredicate is null ? string.Empty : $" AND {conditionPredicate}";
        const string scope = "EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a WITH (NOLOCK) WHERE a.M_IDX=m.M_IDX)";
        var sql = $"""
            SELECT COUNT_BIG(1) FROM dbo.MODULES m WITH (NOLOCK)
            WHERE {scope} AND (@Keyword = '' OR {keywordPredicate}){conditionSql};
            SELECT m.M_IDX,LTRIM(RTRIM(ISNULL(m.M_DESC,''))) AS M_DESC,
                   ISNULL(m.MASTER_TABLE,'') AS MASTER_TABLE,ISNULL(m.DETAIL_TABLE,'') AS DETAIL_TABLE,
                   (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION a WITH (NOLOCK) WHERE a.M_IDX=m.M_IDX) AS ACTION_COUNT
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE {scope} AND (@Keyword = '' OR {keywordPredicate}){conditionSql}
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.CommandText = sql;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, ["M_IDX", "M_DESC", "MASTER_TABLE", "DETAIL_TABLE", "ACTION_COUNT"]);
        logger.LogInformation("统一选择器业务模块源查询 page={Page} size={PageSize} total={Total} rows={Rows}",
            page, pageSize, total, rows.Count);
        return new UnifiedChooserResult(
            [
                new UnifiedChooserColumn("M_IDX", "模块号", "int", null),
                new UnifiedChooserColumn("M_DESC", "模块名", "nvarchar", null),
                new UnifiedChooserColumn("MASTER_TABLE", "操作主表", "nvarchar", null),
                new UnifiedChooserColumn("DETAIL_TABLE", "操作副表", "nvarchar", null),
                new UnifiedChooserColumn("ACTION_COUNT", "动作数", "int", null),
            ],
            rows,
            total);
    }

    /// <summary>权限复制来源用户源（rights-admin.users，C11）：SYSDL 账号 + SYSDN 姓名。</summary>
    private async Task<UnifiedChooserResult> QueryUsersAsync(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        const string sourceKey = "rights-admin.users";
        var (sortColumn, direction) = ResolveSort(sourceKey, request.SortField, request.SortDirection);
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        var keywordPredicate = BuildKeywordPredicate(sourceKey, request.FilterField);
        var orderBy = BuildOrderBy(sourceKey, sortColumn, direction);
        await using var connection = connections.Create();
        await using var command = new SqlCommand { Connection = connection };
        AddCommonParameters(command, keyword, page, pageSize);
        var conditionPredicate = ChooserConditionBuilder.Build(request.Conditions, ColumnExpressions[sourceKey], command);
        var conditionSql = conditionPredicate is null ? string.Empty : $" AND {conditionPredicate}";
        const string fromSql = """
            FROM dbo.SYSDL l WITH (NOLOCK)
            LEFT JOIN dbo.SYSDN n WITH (NOLOCK) ON l.EMP_ID = n.EMP_ID
            """;
        var sql = $"""
            SELECT COUNT_BIG(1)
            {fromSql}
            WHERE (@Keyword = '' OR {keywordPredicate}){conditionSql};
            SELECT LTRIM(RTRIM(l.USER_ID)) AS USER_ID,
                   COALESCE(NULLIF(LTRIM(RTRIM(n.EMP_NAME)),''),LTRIM(RTRIM(l.USER_ID))) AS EMP_NAME
            {fromSql}
            WHERE (@Keyword = '' OR {keywordPredicate}){conditionSql}
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.CommandText = sql;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, ["USER_ID", "EMP_NAME"]);
        logger.LogInformation("统一选择器用户源查询 page={Page} size={PageSize} total={Total} rows={Rows}",
            page, pageSize, total, rows.Count);
        return new UnifiedChooserResult(
            [
                new UnifiedChooserColumn("USER_ID", "用户ID", "nvarchar", null),
                new UnifiedChooserColumn("EMP_NAME", "姓名", "nvarchar", null),
            ],
            rows,
            total);
    }

    /// <summary>权限复制来源组源（rights-admin.groups，C11）：SYSDG 用户组。</summary>
    private async Task<UnifiedChooserResult> QueryGroupsAsync(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        const string sourceKey = "rights-admin.groups";
        var (sortColumn, direction) = ResolveSort(sourceKey, request.SortField, request.SortDirection);
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        var keywordPredicate = BuildKeywordPredicate(sourceKey, request.FilterField);
        var orderBy = BuildOrderBy(sourceKey, sortColumn, direction);
        await using var connection = connections.Create();
        await using var command = new SqlCommand { Connection = connection };
        AddCommonParameters(command, keyword, page, pageSize);
        var conditionPredicate = ChooserConditionBuilder.Build(request.Conditions, ColumnExpressions[sourceKey], command);
        var conditionSql = conditionPredicate is null ? string.Empty : $" AND {conditionPredicate}";
        var sql = $"""
            SELECT COUNT_BIG(1) FROM dbo.SYSDG WITH (NOLOCK)
            WHERE (@Keyword = '' OR {keywordPredicate}){conditionSql};
            SELECT LTRIM(RTRIM(G_IDX)) AS G_IDX, LTRIM(RTRIM(ISNULL(G_DESC,''))) AS G_DESC
            FROM dbo.SYSDG WITH (NOLOCK)
            WHERE (@Keyword = '' OR {keywordPredicate}){conditionSql}
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.CommandText = sql;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, ["G_IDX", "G_DESC"]);
        logger.LogInformation("统一选择器组源查询 page={Page} size={PageSize} total={Total} rows={Rows}",
            page, pageSize, total, rows.Count);
        return new UnifiedChooserResult(
            [
                new UnifiedChooserColumn("G_IDX", "组ID", "nvarchar", null),
                new UnifiedChooserColumn("G_DESC", "组名", "nvarchar", null),
            ],
            rows,
            total);
    }

    private async Task<UnifiedChooserResult?> QueryFieldsAsync(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        const string sourceKey = "menu-admin.fields";
        var tableId = ResolveFieldsTableId(request.Args);
        if (tableId is null)
        {
            logger.LogWarning("统一选择器字段源缺少合法 tableId args={Args}", string.Join(',', request.Args?.Keys ?? []));
            return null;
        }
        var (sortColumn, direction) = ResolveSort(sourceKey, request.SortField, request.SortDirection);
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        var keywordPredicate = BuildKeywordPredicate(sourceKey, request.FilterField);
        var orderBy = BuildOrderBy(sourceKey, sortColumn, direction);
        await using var connection = connections.Create();
        await using var command = new SqlCommand { Connection = connection };
        AddCommonParameters(command, keyword, page, pageSize);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = tableId;
        var conditionPredicate = ChooserConditionBuilder.Build(request.Conditions, ColumnExpressions[sourceKey], command);
        var conditionSql = conditionPredicate is null ? string.Empty : $" AND {conditionPredicate}";
        var sql = $"""
            SELECT COUNT_BIG(1)
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE LTRIM(RTRIM(f.T_ID))=@Table AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM dbo.TABLES t WITH (NOLOCK) WHERE LTRIM(RTRIM(t.T_ID))=@Table)
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@Table AND c.name=f.F_ID)
              AND (@Keyword = '' OR {keywordPredicate}){conditionSql};
            SELECT LTRIM(RTRIM(f.F_ID)) AS F_ID,
                   COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID))) AS F_DESC,
                   COALESCE(LTRIM(RTRIM(f.F_TYPE)),'nvarchar') AS F_TYPE
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE LTRIM(RTRIM(f.T_ID))=@Table AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM dbo.TABLES t WITH (NOLOCK) WHERE LTRIM(RTRIM(t.T_ID))=@Table)
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@Table AND c.name=f.F_ID)
              AND (@Keyword = '' OR {keywordPredicate}){conditionSql}
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.CommandText = sql;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, ["F_ID", "F_DESC", "F_TYPE"]);
        logger.LogInformation("统一选择器字段源查询 table={Table} page={Page} size={PageSize} total={Total} rows={Rows}",
            tableId, page, pageSize, total, rows.Count);
        return new UnifiedChooserResult(
            [
                new UnifiedChooserColumn("F_ID", "字段名", "nvarchar", null),
                new UnifiedChooserColumn("F_DESC", "描述", "nvarchar", null),
                new UnifiedChooserColumn("F_TYPE", "类型", "nvarchar", null),
            ],
            rows,
            total);
    }

    /// <summary>报表字段源（2201 排序/分组：模块主/明细表 FIELDS 白名单字段）。</summary>
    private async Task<UnifiedChooserResult?> QueryReportFieldsAsync(
        UnifiedChooserQueryRequest request, CancellationToken token, string sourceKey)
    {
        var moduleId = ResolveReportModuleId(request.Args);
        if (moduleId is null)
        {
            logger.LogWarning("统一选择器报表字段源缺少合法 moduleId args={Args}", string.Join(',', request.Args?.Keys ?? []));
            return null;
        }
        var (sortColumn, direction) = ResolveSort(sourceKey, request.SortField, request.SortDirection);
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        var keywordPredicate = BuildKeywordPredicate(sourceKey, request.FilterField);
        var orderBy = BuildOrderBy(sourceKey, sortColumn, direction);
        await using var connection = connections.Create();
        await using var command = new SqlCommand { Connection = connection };
        AddCommonParameters(command, keyword, page, pageSize);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId.Value;
        var conditionPredicate = ChooserConditionBuilder.Build(request.Conditions, ColumnExpressions[sourceKey], command);
        var conditionSql = conditionPredicate is null ? string.Empty : $" AND {conditionPredicate}";
        var sql = $"""
            SELECT COUNT_BIG(1)
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
              AND (@Keyword = '' OR {keywordPredicate}){conditionSql};
            SELECT t.T_ID,c.F_ID,
                   COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(c.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(c.F_ID))) AS F_DESC,
                   COALESCE(LTRIM(RTRIM(c.F_TYPE)),'nvarchar') AS F_TYPE
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
              AND (@Keyword = '' OR {keywordPredicate}){conditionSql}
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.CommandText = sql;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, ["T_ID", "F_ID", "F_DESC", "F_TYPE"]);
        logger.LogInformation("统一选择器报表字段源查询 module={ModuleId} page={Page} size={PageSize} total={Total} rows={Rows}",
            moduleId, page, pageSize, total, rows.Count);
        return new UnifiedChooserResult(
            [
                new UnifiedChooserColumn("T_ID", "表名", "nvarchar", null),
                new UnifiedChooserColumn("F_ID", "字段名", "nvarchar", null),
                new UnifiedChooserColumn("F_DESC", "描述", "nvarchar", null),
                new UnifiedChooserColumn("F_TYPE", "类型", "nvarchar", null),
            ],
            rows,
            total);
    }

    /// <summary>
    /// 开户选择器员工源（user-admin.employees）：SYSDN 中尚未开户的员工。
    /// 显示列与默认列集串联 110104（系统员工资料）字段元数据（FIELDS + SYSQL_DEFAULT），
    /// 关键字/过滤/排序/高级查询全部落在元数据白名单上（服务端构造，不信任前端提交）。
    /// </summary>
    private async Task<UnifiedChooserResult?> QueryEmployeesAsync(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        const string sourceKey = "user-admin.employees";
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand { Connection = connection };
        AddCommonParameters(command, keyword, page, pageSize);

        // 1) 110104（SYSDN）字段元数据：可见、非虚拟（或受控虚拟查找）、非成本/保密；SYSQL_DEFAULT 默认列优先
        var columns = await LoadSysdnColumnsAsync(connection, token);
        if (columns.Count == 0)
        {
            logger.LogWarning("统一选择器员工源缺少 SYSDN 可见字段元数据");
            return null;
        }

        var allKeys = columns.Select(column => column.Key).ToArray();
        var allSet = new HashSet<string>(allKeys, StringComparer.OrdinalIgnoreCase);
        var defaultKeys = columns.Where(column => column.IsDefault).Select(column => column.Key).ToArray();
        var columnByKey = columns.ToDictionary(column => column.Key, column => column, StringComparer.OrdinalIgnoreCase);
        var columnExpressions = columns.ToDictionary(column => column.Key, column => column.SelectExpression, StringComparer.OrdinalIgnoreCase);

        var filterKey = string.IsNullOrWhiteSpace(request.FilterField) ? null : request.FilterField.Trim();
        var sortKey = string.IsNullOrWhiteSpace(request.SortField) ? null : request.SortField.Trim();
        if (filterKey is not null && !allSet.Contains(filterKey)) filterKey = null;
        if (sortKey is not null && !allSet.Contains(sortKey)) sortKey = null;

        var joins = columns
            .Where(column => column.JoinClause is not null)
            .Select(column => column.JoinClause!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var keywordPredicate = filterKey is null
            ? $"(@Keyword = '' OR {string.Join(" OR ", allKeys.Select(key => $"{columnByKey[key].SelectExpression} LIKE @Keyword"))})"
            : $"(@Keyword = '' OR {columnByKey[filterKey].SelectExpression} LIKE @Keyword)";
        var sortExpression = sortKey is null ? "[n].[EMP_ID]" : columnByKey[sortKey].SelectExpression;
        var sortDirection = string.Equals(request.SortDirection?.Trim(), "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        var orderBy = string.Equals(sortExpression, "[n].[EMP_ID]", StringComparison.OrdinalIgnoreCase)
            ? $"ORDER BY {sortExpression} {sortDirection}"
            : $"ORDER BY {sortExpression} {sortDirection},[n].[EMP_ID] ASC";
        var selectList = string.Join(",", allKeys.Select(key => $"{columnByKey[key].SelectExpression} AS [{key}]"));
        var joinSql = string.Join(" ", joins);
        var conditionPredicate = ChooserConditionBuilder.Build(request.Conditions, columnExpressions, command);
        var conditionSql = conditionPredicate is null ? string.Empty : $" AND {conditionPredicate}";
        var fromSql = $"""
            FROM dbo.SYSDN n WITH (NOLOCK)
            {joinSql}
            WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSDL l WITH (NOLOCK)
                              WHERE LTRIM(RTRIM(l.EMP_ID))=LTRIM(RTRIM(n.EMP_ID)))
            """;
        var sql = $"""
            SELECT COUNT_BIG(1)
            {fromSql}
              AND {keywordPredicate}{conditionSql};
            SELECT {selectList}
            {fromSql}
              AND {keywordPredicate}{conditionSql}
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, allKeys);
        logger.LogInformation("统一选择器员工源查询 source={Source} page={Page} size={PageSize} total={Total} rows={Rows}",
            sourceKey, page, pageSize, total, rows.Count);
        return new UnifiedChooserResult(
            columns.Select(column => new UnifiedChooserColumn(column.Key, column.Label, column.DataType, null)).ToArray(),
            rows,
            total,
            defaultKeys);
    }

    /// <summary>
    /// menu-admin.records：某模块主表的单据记录（效果链预演选单用）。
    /// 列集合运行时解析——主表主键列（即单据键，同时作为默认列回传）在前，另附少量默认显示列；
    /// 表名只来自 MODULES.MASTER_TABLE，列名只来自 FIELDS 元数据与物理主键，全部参数化。
    /// 无主键的主表无法定位单据，直接拒绝提供服务。
    /// </summary>
    private async Task<UnifiedChooserResult?> QueryModuleRecordsAsync(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        const string sourceKey = "menu-admin.records";
        if (ResolveReportModuleId(request.Args) is not { } moduleId)
        {
            logger.LogWarning("统一选择器单据源缺少合法的 moduleId 参数");
            return null;
        }

        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);
        var keyword = request.Keyword?.Trim() ?? string.Empty;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        var table = await ResolveMasterTableAsync(connection, moduleId, token);
        if (table is null)
        {
            logger.LogWarning("统一选择器单据源模块无主表 module={ModuleId}", moduleId);
            return null;
        }
        var columns = await LoadRecordColumnsAsync(connection, table, token);
        if (columns.Count == 0)
        {
            logger.LogWarning("统一选择器单据源主表无主键 module={ModuleId} table={Table}", moduleId, table);
            return null;
        }

        var allKeys = columns.Select(column => column.Key).ToArray();
        var allSet = new HashSet<string>(allKeys, StringComparer.OrdinalIgnoreCase);
        var defaultKeys = columns.Where(column => column.IsKey).Select(column => column.Key).ToArray();
        var byKey = columns.ToDictionary(column => column.Key, column => column, StringComparer.OrdinalIgnoreCase);
        var expression = (string key) => $"LTRIM(RTRIM(CAST([t].[{byKey[key].Key}] AS nvarchar(200))))";

        var filterKey = string.IsNullOrWhiteSpace(request.FilterField) ? null : request.FilterField.Trim();
        if (filterKey is not null && !allSet.Contains(filterKey)) filterKey = null;
        var sortKey = string.IsNullOrWhiteSpace(request.SortField) ? null : request.SortField.Trim();
        if (sortKey is null || !allSet.Contains(sortKey)) sortKey = allKeys[0];

        await using var command = new SqlCommand { Connection = connection };
        AddCommonParameters(command, keyword, page, pageSize);
        var keywordPredicate = filterKey is null
            ? $"(@Keyword = '' OR {string.Join(" OR ", allKeys.Select(key => $"{expression(key)} LIKE @Keyword"))})"
            : $"(@Keyword = '' OR {expression(filterKey)} LIKE @Keyword)";
        var columnExpressions = allKeys.ToDictionary(key => key, expression, StringComparer.OrdinalIgnoreCase);
        var conditionPredicate = ChooserConditionBuilder.Build(request.Conditions, columnExpressions, command);
        var conditionSql = conditionPredicate is null ? string.Empty : $" AND {conditionPredicate}";
        var direction = string.Equals(request.SortDirection?.Trim(), "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        // 主键列恒作稳定次序：单据键相同时分页顺序不能漂。
        var stable = string.Join(',', defaultKeys.Select(key => $"{expression(key)} ASC"));
        var orderBy = $"ORDER BY {expression(sortKey)} {direction}{(stable.Length == 0 ? string.Empty : "," + stable)}";
        var selectList = string.Join(',', allKeys.Select(key => $"{expression(key)} AS [{key}]"));
        var fromSql = $"FROM dbo.[{table}] t WITH (NOLOCK) WHERE {keywordPredicate}{conditionSql}";
        command.CommandText = $"""
            SELECT COUNT_BIG(1)
            {fromSql};
            SELECT {selectList}
            {fromSql}
            {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = ReadRows(reader, allKeys);
        logger.LogInformation("统一选择器单据源查询 source={Source} module={ModuleId} page={Page} size={PageSize} total={Total} rows={Rows}",
            sourceKey, moduleId, page, pageSize, total, rows.Count);
        return new UnifiedChooserResult(
            columns.Select(column => new UnifiedChooserColumn(column.Key, column.Label, "string", null)).ToArray(),
            rows,
            total,
            defaultKeys);
    }

    private static async Task<string?> ResolveMasterTableAsync(SqlConnection connection, int moduleId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))) FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;", connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        var value = await command.ExecuteScalarAsync(token) as string;
        return value is { Length: > 0 } && WorkbenchSql.Identifier.IsMatch(value) ? value : null;
    }

    private sealed record RecordSourceColumn(string Key, string Label, bool IsKey);

    private static async Task<List<RecordSourceColumn>> LoadRecordColumnsAsync(
        SqlConnection connection, string table, CancellationToken token)
    {
        var primaryKeys = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, null, table, token);
        var columns = new List<RecordSourceColumn>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in primaryKeys)
        {
            if (!WorkbenchSql.Identifier.IsMatch(key) || !taken.Add(key))
            {
                continue;
            }
            columns.Add(new RecordSourceColumn(key, key, IsKey: true));
        }
        if (columns.Count == 0)
        {
            return columns;
        }

        const int MaxDisplayColumns = 2;
        const string metaSql = """
            SELECT TOP (32) LTRIM(RTRIM(f.F_ID)),
                   COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID)))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE LTRIM(RTRIM(f.T_ID))=@Table
              AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0 AND COALESCE(f.IS_DEFAULT_FIELDS,0)=1
            ORDER BY COALESCE(f.VERIFY_INDEX,999),f.F_ID;
            """;
        await using var meta = new SqlCommand(metaSql, connection);
        meta.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await meta.ExecuteReaderAsync(token);
        var display = 0;
        while (display < MaxDisplayColumns && await reader.ReadAsync(token))
        {
            var key = reader.GetString(0);
            if (!WorkbenchSql.Identifier.IsMatch(key) || !taken.Add(key))
            {
                continue;
            }
            columns.Add(new RecordSourceColumn(key, reader.GetString(1), IsKey: false));
            display++;
        }
        return columns;
    }

    /// <summary>SYSDN 虚拟查找列白名单（F_ID → JOIN 与选择表达式），员工选择器受控呈现名称类列。</summary>
    private static readonly Dictionary<string, (string Join, string Select)> SysdnVirtualLookups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DEPT_NAME"] = ("LEFT JOIN dbo.DEPT dd WITH (NOLOCK) ON dd.DEPT_ID = n.DEPT_ID", "[dd].[DEPT_NAME]"),
        ["COMPANY_NAME"] = ("LEFT JOIN dbo.COMPANY cc WITH (NOLOCK) ON cc.COMPANY_ID = n.CI", "[cc].[NAME_CN]"),
        ["DUTY_NAME"] = ("LEFT JOIN dbo.HR_DUTY hd WITH (NOLOCK) ON hd.DUTY_ID = n.DUTY_ID", "[hd].[DUTY_NAME]"),
    };

    private sealed record SysdnColumn(
        string Key, string Label, string DataType, bool IsDefault, string SelectExpression, string? JoinClause);

    /// <summary>读取 SYSDN 字段元数据（FIELDS + SYSQL_DEFAULT），返回受控列集合。</summary>
    private static async Task<List<SysdnColumn>> LoadSysdnColumnsAsync(SqlConnection connection, CancellationToken token)
    {
        var columns = new List<SysdnColumn>();
        const string metaSql = """
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID))),
                   LTRIM(RTRIM(ISNULL(f.F_TYPE,''))),
                   CAST(CASE WHEN d.F_ID IS NULL THEN 0 ELSE 1 END AS bit),
                   COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999)),
                   CAST(COALESCE(f.IS_VIRTUAL,0) AS bit)
            FROM dbo.FIELDS f WITH (NOLOCK)
            LEFT JOIN dbo.SYSQL_DEFAULT d WITH (NOLOCK)
                   ON d.T_ID=@Table AND d.T_ID_R=@Table AND LTRIM(RTRIM(d.F_ID))=LTRIM(RTRIM(f.F_ID))
            WHERE LTRIM(RTRIM(f.T_ID))=@Table
              AND COALESCE(f.IS_COST,0)=0 AND COALESCE(f.IS_SECRECY,0)=0 AND COALESCE(f.IS_VISIBLE,1)=1
            ORDER BY CASE WHEN d.F_ID IS NULL THEN 1 ELSE 0 END,COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID;
            """;
        await using var meta = new SqlCommand(metaSql, connection);
        meta.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = "SYSDN";
        await using var metaReader = await meta.ExecuteReaderAsync(token);
        while (await metaReader.ReadAsync(token))
        {
            var key = metaReader.GetString(0);
            if (!WorkbenchSql.Identifier.IsMatch(key)) continue;
            var isVirtual = metaReader.GetBoolean(5);
            string? selectExpression = null;
            string? joinClause = null;
            if (!isVirtual)
            {
                selectExpression = $"[n].[{key}]";
            }
            else if (SysdnVirtualLookups.TryGetValue(key, out var lookup))
            {
                selectExpression = lookup.Select;
                joinClause = lookup.Join;
            }
            else
            {
                continue; // 无法受控解析的虚拟列不进入选择器
            }
            columns.Add(new SysdnColumn(
                key, metaReader.GetString(1), MapChooserDataType(metaReader.GetString(2)),
                metaReader.GetBoolean(3), selectExpression, joinClause));
        }
        return columns;
    }

    /// <summary>FIELDS.F_TYPE → 统一选择器 dataType。</summary>
    private static string MapChooserDataType(string fieldType)
    {
        var type = fieldType.Trim().ToLowerInvariant();
        if (type.Contains("datetime") || type.Contains("smalldatetime")) return "datetime";
        if (type == "date") return "date";
        if (type == "bit") return "bit";
        if (type.Contains("int") || type.Contains("decimal") || type.Contains("numeric")
            || type.Contains("money") || type.Contains("float") || type.Contains("real")) return "number";
        return "string";
    }

    /// <summary>按 filterField 白名单生成关键字谓词；非法 filterField 回退为跨列 OR。</summary>
    private static string BuildKeywordPredicate(string sourceKey, string? filterField)
    {
        var expressions = KeywordExpressions[sourceKey.Trim()];
        if (!string.IsNullOrWhiteSpace(filterField) && expressions.TryGetValue(filterField.Trim(), out var single))
            return single;
        return string.Join(" OR ", expressions.Values);
    }

    /// <summary>构建稳定排序列：排序列在前，注册的稳定次序列去重后追加，保证分页顺序稳定。</summary>
    private static string BuildOrderBy(string sourceKey, string sortColumn, string direction)
    {
        var stable = StableSortColumns.TryGetValue(sourceKey.Trim(), out var columns) ? columns : [];
        var ordered = stable
            .Where(column => !column.Equals(sortColumn, StringComparison.OrdinalIgnoreCase))
            .Prepend(sortColumn);
        return "ORDER BY " + string.Join(",", ordered.Select(column => $"[{column}] {direction}"));
    }

    private static void AddCommonParameters(SqlCommand command, string keyword, int page, int pageSize)
    {
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 200).Value = keyword.Length > 0 ? "%" + keyword + "%" : string.Empty;
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = (page - 1) * pageSize;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;
    }

    private static List<IReadOnlyDictionary<string, object?>> ReadRows(SqlDataReader reader, IReadOnlyList<string> columns)
    {
        var result = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                var value = reader[column];
                row[column] = value is string text ? text.Trim() : (value is DBNull ? null : value);
            }
            result.Add(row);
        }
        return result;
    }
}
