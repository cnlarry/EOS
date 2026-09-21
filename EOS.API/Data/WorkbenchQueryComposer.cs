using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;

using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 工作台查询编排：
/// 列表/详情/子表/导出 SQL 构建、排序、筛选、字段选择与分页；数据范围统一经
/// WorkbenchScopeFilter（fail-closed），dbElapsedMs 经 DbTimingCollector 计时。
/// 动态标识符一律来自服务端 Definition 白名单，用户值全部参数化。
/// </summary>
public sealed class WorkbenchQueryComposer(
    DbConnectionFactory connections,
    WorkbenchScopeFilter scopeFilter,
    ApiMetrics metrics,
    WorkbenchVirtualColumnResolver virtualColumns,
    ILogger<WorkbenchQueryComposer> logger)
{
    public async Task<WorkbenchData> GetRowsAsync(
        WorkbenchDefinition definition,
        bool detail,
        IReadOnlyDictionary<string, string> keys,
        int page,
        int pageSize,
        CancellationToken token,
        WorkbenchQuery? query = null,
        string? keyword = null,
        string? sortField = null,
        string? sortDirection = null,
        int? groupIndex = null,
        string? groupValue = null,
        string? dataFilter = null)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        var table = detail ? definition.DetailTable : definition.MasterTable;
        var fields = detail ? definition.DetailFields : definition.MasterFields;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        if (table is null || fields.Count == 0)
        {
            return new([], 0, page, pageSize);
        }
        var selected = fields.Take(30).ToList();
        // 行标识必须稳定：物理主键列无论是否可见/是否被截断，都强制包含在返回行中。
        foreach (var pk in definition.MasterPkOrder)
        {
            if (selected.Any(field => field.Key.Equals(pk, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var field = fields.FirstOrDefault(item => item.Key.Equals(pk, StringComparison.OrdinalIgnoreCase));
            if (field is null && WorkbenchSql.Identifier.IsMatch(pk))
            {
                field = new WorkbenchField(pk, pk, "nvarchar", 100, "left", true, false, false);
            }
            if (field is not null)
            {
                selected.Add(field);
            }
        }
        // 跨模块浏览链接（BROWSE_M_IDX/BrowseKeyFields）的键源列：不在可见列内时
        // 强制加入返回行（隐藏、不渲染），供前端 FieldBrowseLink 组装目标记录主键数组。
        foreach (var sourceKey in fields
            .Where(field => field.BrowseKeyFields is { Count: > 0 })
            .SelectMany(field => field.BrowseKeyFields!)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (selected.Any(field => field.Key.Equals(sourceKey, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (!WorkbenchSql.Identifier.IsMatch(sourceKey))
            {
                continue;
            }
            selected.Add(new WorkbenchField(sourceKey, sourceKey, "nvarchar", 100, "left", false, false, false));
        }
        var predicates = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand();
        command.Connection = connection;
        if (detail)
        {
            // 关联键与显示列解耦：键列只要求在子表物理存在，用户未勾选的键列仅用于关联，不渲染。
            var detailKeyColumns = await ResolveDetailKeyColumnsAsync(connection, definition, table, token);
            foreach (var key in detailKeyColumns)
            {
                if (!keys.TryGetValue(key, out var value))
                {
                    continue;
                }
                var name = $"@k{predicates.Count}";
                predicates.Add($"[{key}]={name}");
                command.Parameters.AddWithValue(name, value);
            }
            if (predicates.Count == 0)
            {
                logger.LogDebug("子表查询缺少主表关联键，跳过 detail={Detail} table={Table}", detail, table);
                return new([], 0, page, pageSize);
            }
            foreach (var key in detailKeyColumns.Where(key => !selected.Any(field => field.Key.Equals(key, StringComparison.OrdinalIgnoreCase))))
            {
                selected.Add(new WorkbenchField(key, key, "nvarchar", 100, "left", true, false, false));
            }
            scopeFilter.ApplyDetailScope(definition, dataFilter, table, detailKeyColumns, predicates, command);
        }
        if (!detail && query is not null)
        {
            AddQueryPredicates(query, definition.MasterFields, predicates, command);
        }
        if (!detail && !string.IsNullOrWhiteSpace(keyword))
        {
            AddKeywordPredicates(keyword, fields, predicates, command);
        }
        if (!detail)
        {
            scopeFilter.ApplyScope(definition, dataFilter, groupIndex, groupValue, predicates, command);
        }
        var where = predicates.Count > 0 ? " WHERE " + string.Join(" AND ", predicates) : "";
        var order = ResolveOrder(definition, fields, selected, detail, sortField, sortDirection);
        // 排序字段必须并入投影：30 列截断后若排序列落在截断外，派生表投影缺列会导致 ORDER BY 无效列 500。
        EnsureOrderColumnsInProjection(fields, selected, order);
        var selection = await BuildListSelectionAsync(connection, table, selected, token);
        command.CommandText = selection.HasVirtual
            ? $"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK){where}; SELECT {selection.OuterColumns} FROM (SELECT {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK){where}) AS [__base]{selection.JoinFragment} ORDER BY {QualifyOrder(order)} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;"
            : $"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK){where}; SELECT {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK){where} ORDER BY {order} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = (page - 1) * pageSize;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = await WorkbenchSql.ReadRowsAsync(reader, token);
        // P4a：带 CONVERT_FUNCTION 的字段按受控函数转换为展示值（函数白名单 + 参数化）
        await virtualColumns.ApplyConvertFunctionsAsync(connection, detail ? definition.DetailFields : definition.MasterFields, rows, token);
        logger.LogDebug("工作台查询完成 detail={Detail} table={Table} page={Page} pageSize={PageSize} total={Total} returned={Returned} elapsedMs={ElapsedMs:F0}",
            detail, table, page, pageSize, total, rows.Count, stopwatch.Elapsed.TotalMilliseconds);
        metrics.ObserveWorkbenchQuery(stopwatch.Elapsed.TotalMilliseconds);
        return new(rows, total, page, pageSize);
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsAsync(
        WorkbenchDefinition definition,
        WorkbenchQuery? query,
        string? keyword,
        CancellationToken token,
        string? sortField = null,
        string? sortDirection = null,
        int? groupIndex = null,
        string? groupValue = null,
        IReadOnlyList<WorkbenchField>? exportFields = null,
        string? dataFilter = null)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        var table = definition.MasterTable;
        var fields = definition.MasterFields;
        if (table is null || fields.Count == 0)
        {
            return [];
        }
        const int maxExportRows = 100000;
        var selected = exportFields is { Count: > 0 } ? exportFields.ToList() : fields.Take(30).ToList();
        var predicates = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand();
        command.Connection = connection;
        if (query is not null)
        {
            AddQueryPredicates(query, definition.MasterFields, predicates, command);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            AddKeywordPredicates(keyword, fields, predicates, command);
        }
        scopeFilter.ApplyScope(definition, dataFilter, groupIndex, groupValue, predicates, command);
        var where = predicates.Count > 0 ? " WHERE " + string.Join(" AND ", predicates) : "";
        var order = ResolveOrder(definition, fields, selected, false, sortField, sortDirection);
        EnsureOrderColumnsInProjection(fields, selected, order);
        var selection = await BuildListSelectionAsync(connection, table, selected, token);
        command.CommandText = selection.HasVirtual
            ? $"SELECT TOP {maxExportRows} {selection.OuterColumns} FROM (SELECT {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK){where}) AS [__base]{selection.JoinFragment} ORDER BY {QualifyOrder(order)};"
            : $"SELECT TOP {maxExportRows} {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK){where} ORDER BY {order};";
        var rows = await WorkbenchSql.ReadRowsAsync(command, token);
        logger.LogDebug("工作台导出完成 table={Table} returned={RowCount} elapsedMs={ElapsedMs:F0}", table, rows.Count, stopwatch.Elapsed.TotalMilliseconds);
        metrics.ObserveWorkbenchQuery(stopwatch.Elapsed.TotalMilliseconds);
        return rows;
    }

    /// <summary>按主键集合导出（导出所选行）：全部条件参数化，字段沿用权限过滤后的定义白名单。</summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<IReadOnlyList<string>> keys,
        CancellationToken token,
        int? groupIndex = null,
        string? groupValue = null,
        IReadOnlyList<WorkbenchField>? exportFields = null,
        string? dataFilter = null)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        var table = definition.MasterTable;
        var fields = definition.MasterFields;
        if (table is null || fields.Count == 0 || keys.Count == 0)
        {
            return [];
        }
        // 定位主键取自物理定义而非显示列：用户未勾选的主键列仍可用于定位（导出所选、助手取详情）
        var pks = definition.MasterPkOrder.Where(key => WorkbenchSql.Identifier.IsMatch(key)).ToList();
        if (pks.Count == 0 || pks.Count != definition.MasterPkOrder.Count)
        {
            return [];
        }
        var selected = exportFields is { Count: > 0 } ? exportFields.ToList() : fields.Take(30).ToList();
        foreach (var pk in pks.Where(pk => !selected.Any(field => field.Key.Equals(pk, StringComparison.OrdinalIgnoreCase))))
        {
            selected.Add(fields.FirstOrDefault(field => field.Key.Equals(pk, StringComparison.OrdinalIgnoreCase))
                ?? new WorkbenchField(pk, pk, "nvarchar", 100, "left", true, false, false));
        }
        var filterPredicates = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand();
        command.Connection = connection;
        scopeFilter.ApplyScope(definition, dataFilter, groupIndex, groupValue, filterPredicates, command);
        var orParts = new List<string>();
        for (var rowIndex = 0; rowIndex < keys.Count; rowIndex++)
        {
            var row = keys[rowIndex];
            if (row.Count != pks.Count)
            {
                continue;
            }
            var andParts = new List<string>();
            for (var i = 0; i < pks.Count; i++)
            {
                var name = $"@k{rowIndex}_{i}";
                andParts.Add($"[{pks[i]}]={name}");
                command.Parameters.AddWithValue(name, row[i] ?? "");
            }
            orParts.Add("(" + string.Join(" AND ", andParts) + ")");
        }
        if (orParts.Count == 0)
        {
            return [];
        }
        var filterWhere = filterPredicates.Count > 0 ? " AND " + string.Join(" AND ", filterPredicates) : "";
        var selection = await BuildListSelectionAsync(connection, table, selected, token);
        command.CommandText = selection.HasVirtual
            ? $"SELECT {selection.OuterColumns} FROM (SELECT {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK) WHERE {string.Join(" OR ", orParts)}{filterWhere}) AS [__base]{selection.JoinFragment};"
            : $"SELECT {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK) WHERE {string.Join(" OR ", orParts)}{filterWhere};";
        var rows = await WorkbenchSql.ReadRowsAsync(command, token);
        metrics.ObserveWorkbenchQuery(stopwatch.Elapsed.TotalMilliseconds);
        return rows;
    }

    /// <summary>导出列解析：请求列与权限过滤后的定义白名单求交（保持请求顺序），空请求/无匹配时回退前 30 列；上限 30 列。</summary>
    public static IReadOnlyList<WorkbenchField> ResolveExportFields(
        IReadOnlyList<WorkbenchField> fields,
        IReadOnlyList<string>? columnKeys)
    {
        if (columnKeys is { Count: > 0 })
        {
            var requested = new HashSet<string>(columnKeys, StringComparer.OrdinalIgnoreCase);
            var matched = fields.Where(field => requested.Contains(field.Key)).Take(30).ToList();
            if (matched.Count > 0)
            {
                return matched;
            }
        }
        return fields.Take(30).ToList();
    }

    /// <summary>
    /// 子表关联键列：主表主键中在子表物理存在的列（保持主键顺序）。
    /// 键列是否出现在用户列配置里不影响关联——列配置只决定渲染哪些列。
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResolveDetailKeyColumnsAsync(
        SqlConnection connection,
        WorkbenchDefinition definition,
        string table,
        CancellationToken token)
    {
        var candidates = definition.MasterPkOrder.Where(key => WorkbenchSql.Identifier.IsMatch(key)).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }
        var physical = await WorkbenchSql.GetPhysicalColumnsAsync(connection, null, table, token);
        return candidates.Where(physical.Contains).ToList();
    }

    private async Task<ListSelection> BuildListSelectionAsync(
        SqlConnection connection,
        string table,
        IReadOnlyList<WorkbenchField> selected,
        CancellationToken token)
    {
        var physical = selected.Where(field => !field.IsVirtual).ToList();
        var innerColumns = physical.Select(field => $"[{field.Key}]").ToList();
        var outerColumns = physical.Select(field => $"[__base].[{field.Key}]").ToList();
        var virtualFields = selected.Where(field => field.IsVirtual).ToList();
        var joinFragment = "";
        if (virtualFields.Count > 0)
        {
            var resolution = await virtualColumns.ResolveDefinitionAsync(connection, table, virtualFields, token, baseAlias: "__base");
            outerColumns.AddRange(resolution.SelectFragments);
            joinFragment = resolution.JoinFragment;
            foreach (var column in resolution.BaseColumns)
            {
                if (!physical.Any(field => field.Key.Equals(column, StringComparison.OrdinalIgnoreCase)))
                {
                    innerColumns.Add($"[{column}]");
                }
            }
            foreach (var key in resolution.UnresolvedKeys)
            {
                logger.LogDebug("虚拟字段运行期无法解析，列表不渲染：table={Table} field={Field}", table, key);
            }
        }
        if (outerColumns.Count == 0)
        {
            innerColumns.Add($"[{selected[0].Key}]");
            outerColumns.Add($"[__base].[{selected[0].Key}]");
        }
        return new(string.Join(',', innerColumns), string.Join(',', outerColumns), joinFragment, virtualFields.Count > 0);
    }

    private sealed record ListSelection(string InnerColumns, string OuterColumns, string JoinFragment, bool HasVirtual);

    /// <summary>把 ResolveOrder 生成的 `[列] ASC` 改写为派生表限定 `[__base].[列] ASC`。</summary>
    private static string QualifyOrder(string order) =>
        Regex.Replace(order, @"\[([A-Za-z_][A-Za-z0-9_]{0,127})\]", "[__base].[$1]");

    /// <summary>把 ORDER BY 引用的列并入投影（30 列截断后仍可排序）。</summary>
    private static void EnsureOrderColumnsInProjection(
        IReadOnlyList<WorkbenchField> fields,
        ICollection<WorkbenchField> selected,
        string order)
    {
        foreach (Match match in Regex.Matches(order, @"\[([A-Za-z_][A-Za-z0-9_]{0,127})\]"))
        {
            var sortColumn = match.Groups[1].Value;
            if (selected.Any(field => field.Key.Equals(sortColumn, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var field = fields.FirstOrDefault(item => item.Key.Equals(sortColumn, StringComparison.OrdinalIgnoreCase));
            if (field is null && WorkbenchSql.Identifier.IsMatch(sortColumn))
            {
                field = new WorkbenchField(sortColumn, sortColumn, "nvarchar", 100, "left", false, false, false);
            }
            if (field is not null)
            {
                selected.Add(field);
            }
        }
    }

    private static string ResolveOrder(
        WorkbenchDefinition definition,
        IReadOnlyList<WorkbenchField> fields,
        IReadOnlyList<WorkbenchField> selected,
        bool detail,
        string? sortFields,
        string? sortDirections)
    {
        if (!string.IsNullOrWhiteSpace(sortFields))
        {
            var names = sortFields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (names.Length > 5)
            {
                throw new ArgumentException("排序字段不能超过 5 个。");
            }
            var dirs = (sortDirections ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var parts = new List<string>();
            for (var i = 0; i < names.Length; i++)
            {
                var field = fields.FirstOrDefault(item => !item.IsVirtual && item.Key.Equals(names[i], StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("排序字段无效。");
                var desc = i < dirs.Length && dirs[i].Equals("desc", StringComparison.OrdinalIgnoreCase);
                parts.Add($"[{field.Key}] {(desc ? "DESC" : "ASC")}");
            }
            return string.Join(',', parts);
        }
        if (!detail && !string.IsNullOrWhiteSpace(definition.DefaultSort))
        {
            return definition.DefaultSort;
        }
        var keys = fields.Where(field => field.IsPrimaryKey && !field.IsVirtual).ToList();
        if (keys.Count == 0)
        {
            keys = selected.Where(field => !field.IsVirtual).Take(1).ToList();
        }
        if (keys.Count == 0)
        {
            throw new InvalidOperationException("工作台列表缺少可排序列。");
        }
        return string.Join(',', keys.Select(field => $"[{field.Key}]"));
    }

    private static void AddQueryPredicates(WorkbenchQuery query, IReadOnlyList<WorkbenchField> fields, List<string> predicates, SqlCommand command)
    {
        if (query.Conditions.Count > 20)
        {
            throw new ArgumentException("查询条件不能超过 20 个。");
        }
        var queryPredicates = new List<string>();
        foreach (var condition in query.Conditions)
        {
            var field = fields.FirstOrDefault(item => item.Key.Equals(condition.Field, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"无效查询字段：{condition.Field}");
            if (field.IsVirtual)
            {
                throw new ArgumentException($"虚拟字段不可查询：{condition.Field}");
            }
            var comparisonOnly = condition.Operator is "eq" or "ne" or "gt" or "gte" or "lt" or "lte" or "between";
            if (!comparisonOnly && !field.IsQueryable)
            {
                throw new ArgumentException($"字段不可查询：{condition.Field}");
            }
            var name = $"@q{command.Parameters.Count}";
            var column = $"[{field.Key}]";
            var op = condition.Operator.ToLowerInvariant();
            string expression;
            switch (op)
            {
                case "eq":
                case "ne":
                case "gt":
                case "gte":
                case "lt":
                case "lte":
                    var sqlOperator = op switch { "eq" => "=", "ne" => "<>", "gt" => ">", "gte" => ">=", "lt" => "<", _ => "<=" };
                    expression = $"{column} {sqlOperator} {name}";
                    command.Parameters.AddWithValue(name, condition.Value ?? "");
                    break;
                case "contains":
                case "notcontains":
                case "startswith":
                case "endswith":
                    expression = $"{column} {(op == "notcontains" ? "NOT LIKE" : "LIKE")} {name}";
                    var value = condition.Value ?? "";
                    command.Parameters.AddWithValue(name, op is "contains" or "notcontains" ? $"%{value}%" : op == "startswith" ? $"{value}%" : $"%{value}");
                    break;
                case "empty":
                    expression = $"({column} IS NULL OR {column}='')";
                    break;
                case "notempty":
                    expression = $"({column} IS NOT NULL AND {column}<>'')";
                    break;
                case "between":
                    expression = $"{column} BETWEEN {name} AND {name}b";
                    command.Parameters.AddWithValue(name, condition.Value ?? "");
                    command.Parameters.AddWithValue(name + "b", condition.ValueTo ?? "");
                    break;
                default:
                    throw new ArgumentException($"无效查询运算符：{condition.Operator}");
            }
            queryPredicates.Add((queryPredicates.Count > 0 && condition.Logic.Equals("or", StringComparison.OrdinalIgnoreCase) ? "OR " : "AND ") + expression);
        }
        if (queryPredicates.Count > 0)
        {
            queryPredicates[0] = queryPredicates[0][4..];
            predicates.Add("(" + string.Join(' ', queryPredicates) + ")");
        }
    }

    private static void AddKeywordPredicates(string keyword, IReadOnlyList<WorkbenchField> fields, List<string> predicates, SqlCommand command)
    {
        var textFields = fields.Where(field => field.IsQueryable && IsTextLike(field.DataType)).ToList();
        if (textFields.Count == 0)
        {
            return;
        }
        var name = $"@kw{predicates.Count}";
        command.Parameters.AddWithValue(name, $"%{keyword.Trim()}%");
        predicates.Add("(" + string.Join(" OR ", textFields.Select(field => IsDateLike(field.DataType) ? $"CONVERT(varchar(23),[{field.Key}],120) LIKE {name}" : $"[{field.Key}] LIKE {name}")) + ")");
    }

    private static bool IsTextLike(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        return type.Contains("char") || type.Contains("text") || type.Contains("date") || type.Contains("time")
            || type is "idcard" or "url" or "email" or "phoneno" or "zipcode";
    }

    private static bool IsDateLike(string dataType) =>
        dataType.Contains("date", StringComparison.OrdinalIgnoreCase) || dataType.Contains("time", StringComparison.OrdinalIgnoreCase);

    private SqlConnection CreateConnection() => connections.Create();
}
