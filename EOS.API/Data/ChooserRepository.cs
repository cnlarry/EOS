using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Models;
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
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    /// <summary>注册数据源 → 可排序列白名单（首列为默认排序列，稳定次序列固定追加）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RegisteredSources =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["menu-admin.tables"] = ["T_DESC", "T_ID", "T_KIND", "T_TYPE"],
            ["menu-admin.fields"] = ["F_ID", "F_DESC", "F_TYPE"],
            ["report-admin.fields"] = ["T_ID", "F_ID", "F_DESC", "F_TYPE"],
        };

    /// <summary>注册数据源 → 排序稳定次序列（与排序列去重后追加，保证分页顺序稳定）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> StableSortColumns =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["menu-admin.tables"] = ["T_ID"],
            ["menu-admin.fields"] = ["F_ID"],
            ["report-admin.fields"] = ["T_ID", "F_ID"],
        };

    /// <summary>注册数据源 → 列键 → 关键字 LIKE 表达式（编译期常量，安全拼接）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> KeywordExpressions =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["menu-admin.tables"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["T_ID"] = "LTRIM(RTRIM(T_ID)) LIKE @Keyword",
                ["T_DESC"] = "LTRIM(RTRIM(T_DESC)) LIKE @Keyword",
                ["T_KIND"] = "LTRIM(RTRIM(ISNULL(T_KIND,''))) LIKE @Keyword",
                ["T_TYPE"] = "LTRIM(RTRIM(ISNULL(T_TYPE,''))) LIKE @Keyword",
            },
            ["menu-admin.fields"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["F_ID"] = "LTRIM(RTRIM(f.F_ID)) LIKE @Keyword",
                ["F_DESC"] = "COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))) LIKE @Keyword",
                ["F_TYPE"] = "COALESCE(LTRIM(RTRIM(f.F_TYPE)),'nvarchar') LIKE @Keyword",
            },
            ["report-admin.fields"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["T_ID"] = "LTRIM(RTRIM(t.T_ID)) LIKE @Keyword",
                ["F_ID"] = "LTRIM(RTRIM(c.F_ID)) LIKE @Keyword",
                ["F_DESC"] = "COALESCE(NULLIF(LTRIM(RTRIM(c.F_DESC)),''),LTRIM(RTRIM(c.F_ID))) LIKE @Keyword",
            },
        };

    /// <summary>注册数据源 → 列键 → SQL 列表达式（高级查询白名单，编译期常量）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ColumnExpressions =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["menu-admin.tables"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["T_ID"] = "LTRIM(RTRIM(T_ID))",
                ["T_DESC"] = "LTRIM(RTRIM(T_DESC))",
                ["T_KIND"] = "LTRIM(RTRIM(ISNULL(T_KIND,'')))",
                ["T_TYPE"] = "LTRIM(RTRIM(ISNULL(T_TYPE,'')))",
            },
            ["menu-admin.fields"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["F_ID"] = "LTRIM(RTRIM(f.F_ID))",
                ["F_DESC"] = "COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID)))",
                ["F_TYPE"] = "COALESCE(LTRIM(RTRIM(f.F_TYPE)),'nvarchar')",
            },
            ["report-admin.fields"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["T_ID"] = "LTRIM(RTRIM(t.T_ID))",
                ["F_ID"] = "LTRIM(RTRIM(c.F_ID))",
                ["F_DESC"] = "COALESCE(NULLIF(LTRIM(RTRIM(c.F_DESC)),''),LTRIM(RTRIM(c.F_ID)))",
                ["F_TYPE"] = "COALESCE(LTRIM(RTRIM(c.F_TYPE)),'nvarchar')",
            },
        };

    public static bool IsRegistered(string? sourceKey) =>
        !string.IsNullOrWhiteSpace(sourceKey) && RegisteredSources.ContainsKey(sourceKey.Trim());

    /// <summary>数据源权限门：返回需要校验的模块号（null 表示仅登录可读，按场景收紧）。</summary>
    public static int? PermissionModuleId(string? sourceKey) => sourceKey?.Trim().ToLowerInvariant() switch
    {
        "menu-admin.tables" or "menu-admin.fields" => MenuAdminModuleId,
        "report-admin.fields" => ReportAdminModuleId,
        _ => null,
    };

    /// <summary>排序列白名单解析：非法/缺失回退首列（默认排序）；方向仅 asc/desc。</summary>
    internal static (string Column, string Direction) ResolveSort(string sourceKey, string? sortField, string? sortDirection)
    {
        var allowed = RegisteredSources.TryGetValue(sourceKey.Trim(), out var columns) ? columns : [];
        var column = allowed.FirstOrDefault(item => item.Equals(sortField, StringComparison.OrdinalIgnoreCase)) ?? allowed[0];
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
        return Identifier.IsMatch(tableId.Trim()) ? tableId.Trim() : null;
    }

    /// <summary>report-admin.fields 的 args 校验：moduleId 必须为正整数。</summary>
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
            "menu-admin.fields" => await QueryFieldsAsync(request, token),
            "report-admin.fields" => await QueryReportFieldsAsync(request, token),
            _ => null,
        };
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
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c
                          WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@Table AND c.COLUMN_NAME=f.F_ID)
              AND (@Keyword = '' OR {keywordPredicate}){conditionSql};
            SELECT LTRIM(RTRIM(f.F_ID)) AS F_ID,
                   COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))) AS F_DESC,
                   COALESCE(LTRIM(RTRIM(f.F_TYPE)),'nvarchar') AS F_TYPE
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE LTRIM(RTRIM(f.T_ID))=@Table AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM dbo.TABLES t WITH (NOLOCK) WHERE LTRIM(RTRIM(t.T_ID))=@Table)
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c
                          WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@Table AND c.COLUMN_NAME=f.F_ID)
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

    private async Task<UnifiedChooserResult?> QueryReportFieldsAsync(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        const string sourceKey = "report-admin.fields";
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
            WHERE EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS col
                          WHERE col.TABLE_SCHEMA='dbo' AND col.TABLE_NAME=t.T_ID AND col.COLUMN_NAME=c.F_ID)
              AND (@Keyword = '' OR {keywordPredicate}){conditionSql};
            SELECT t.T_ID,c.F_ID,
                   COALESCE(NULLIF(LTRIM(RTRIM(c.F_DESC)),''),LTRIM(RTRIM(c.F_ID))) AS F_DESC,
                   COALESCE(LTRIM(RTRIM(c.F_TYPE)),'nvarchar') AS F_TYPE
            FROM (
                SELECT LTRIM(RTRIM(MASTER_TABLE)) T_ID FROM dbo.MODULES WHERE M_IDX=@ModuleId
                UNION ALL
                SELECT LTRIM(RTRIM(DETAIL_TABLE)) FROM dbo.MODULES
                WHERE M_IDX=@ModuleId AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE,'')))<>''
            ) t
            INNER JOIN dbo.FIELDS c WITH (NOLOCK) ON c.T_ID=t.T_ID AND COALESCE(c.IS_VIRTUAL,0)=0
            WHERE EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS col
                          WHERE col.TABLE_SCHEMA='dbo' AND col.TABLE_NAME=t.T_ID AND col.COLUMN_NAME=c.F_ID)
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
