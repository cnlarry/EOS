using System.Text;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// 编译期参数占位符：RawValue 为原始值文本（含模板 token），运行期经
/// <see cref="BindRuntimeValue"/> 做模板替换后绑定为 SqlParameter，绝不拼接原始值进 SQL。
/// </summary>
public sealed record ChooserFilterParameter(string Name, string RawValue);

/// <summary>
/// 结构化条件编译结果：参数化谓词 + 参数绑定清单 + 所需跨表 JOIN（引用别名）+ 跨表列（物理校验用）。
/// </summary>
public sealed record ChooserFilterCompileResult(
    string Predicate,
    IReadOnlyList<ChooserFilterParameter> Parameters,
    IReadOnlyList<string> Joins,
    IReadOnlyList<(string Table, string Column)> ForeignColumns);

/// <summary>
/// FILTER_STRUCT 受控编译器：
/// - 第一版算子：EQ/NE/GT/GE/LT/LE/LIKE/NOT_LIKE + 宏 ISNULL_ZERO / DAYS_FROM_TODAY + nullSafe ZERO/EMPTY；
/// - P3 扩展：表达式（column/literal/template/isnull/arith/datediff）、group 嵌套（AND/OR）、
/// negate（NOT 组）、子查询 IN/NOT_IN/EXISTS/NOT_EXISTS、受控表值函数 IN_FUNCTION；
/// - 跨表字段「表.列」须在源表 QUERY_RELATION JOIN 白名单（joinAliases）内，运行期由
/// <see cref="ChooserJoinCatalog"/> 重建 JOIN 段；
/// - value 模板 {m.X}/{d.X}/{module}/{X} 编译为参数占位符（{X} 按字面量绑定，行为）。
/// 任何无法编译的输入返回 null（fail-closed），调用方不得执行。
/// </summary>
public static class ChooserFilterCompiler
{
    /// <summary>选择器受控表值函数白名单。</summary>
    public static readonly IReadOnlySet<string> SubqueryFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "f_get_pro_units",
        "f_get_under_m_idx",
    };

    private const int MaxNestingDepth = 8;

    /// <summary>模板 token：{m.X}/{d.X}/{module}/{X}（大小写不敏感）。</summary>
    private static readonly Regex TemplateToken = new(
        @"\{m\.(?<m>[A-Za-z_][A-Za-z0-9_]*)\}|\{d\.(?<d>[A-Za-z_][A-Za-z0-9_]*)\}|\{(?<mod>module)\}|\{(?<bare>[A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ComparisonOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "EQ", "NE", "GT", "GE", "LT", "LE", "LIKE", "NOT_LIKE",
    };

    private static readonly HashSet<string> NullSafe = new(StringComparer.OrdinalIgnoreCase)
    {
        "ZERO", "EMPTY",
    };

    public static readonly IReadOnlySet<string> SubqueryOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "IN", "NOT_IN", "EXISTS", "NOT_EXISTS", "IN_FUNCTION",
    };

    public static ChooserFilterCompileResult? Compile(
        ChooserFilterStruct? filter,
        string sourceTable,
        IReadOnlySet<string>? joinAliases = null,
        IReadOnlyDictionary<string, string>? columnTypes = null)
    {
        if (filter is null || filter.Items.Count == 0)
        {
            return new ChooserFilterCompileResult("", [], [], []);
        }
        if (!WorkbenchSql.Identifier.IsMatch(sourceTable))
        {
            return null;
        }
        var aliases = joinAliases ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parameters = new List<ChooserFilterParameter>();
        var joins = new List<string>();
        var foreignColumns = new List<(string Table, string Column)>();
        var predicate = CompileItems(filter, sourceTable, aliases, parameters, joins, foreignColumns, 0);
        return predicate is null
            ? null
            : new ChooserFilterCompileResult(
                predicate,
                parameters,
                joins.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                foreignColumns.Distinct().ToList());
    }

    private static string? CompileItems(
        ChooserFilterStruct group,
        string sourceTable,
        IReadOnlySet<string> joinAliases,
        List<ChooserFilterParameter> parameters,
        List<string> joins,
        List<(string Table, string Column)> foreignColumns,
        int depth)
    {
        if (depth > MaxNestingDepth)
        {
            return null;
        }
        var logic = group.Logic?.Trim();
        if (!string.Equals(logic, "AND", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(logic, "OR", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var parts = new List<string>();
        foreach (var item in group.Items)
        {
            var compiled = CompileItem(item, sourceTable, joinAliases, parameters, joins, foreignColumns, depth);
            if (compiled is null)
            {
                return null;
            }
            parts.Add($"({compiled})");
        }
        return parts.Count == 0 ? null : string.Join($" {logic!.ToUpperInvariant()} ", parts);
    }

    private static string? CompileItem(
        ChooserFilterItem item,
        string sourceTable,
        IReadOnlySet<string> joinAliases,
        List<ChooserFilterParameter> parameters,
        List<string> joins,
        List<(string Table, string Column)> foreignColumns,
        int depth)
    {
        if (item.Group is not null)
        {
            var compiled = CompileItems(item.Group, sourceTable, joinAliases, parameters, joins, foreignColumns, depth + 1);
            if (compiled is null)
            {
                return null;
            }
            return item.Negate == true ? $"NOT ({compiled})" : compiled;
        }
        if (item.Subquery is not null)
        {
            return CompileSubquery(item, sourceTable, joinAliases, parameters, joins, foreignColumns);
        }

        var rawOperator = item.Operator;
        if (string.IsNullOrWhiteSpace(rawOperator))
        {
            return null;
        }
        var op = rawOperator.Trim().ToUpperInvariant();

        // 宏：ISNULL(列,0)=0
        if (op == "ISNULL_ZERO")
        {
            var column = ResolveColumn(item.Field, sourceTable, joinAliases, joins, foreignColumns);
            return column is null ? null : $"ISNULL({column},0) = 0";
        }
        // 宏：DATEDIFF(day,列,GETDATE()) = 值
        if (op == "DAYS_FROM_TODAY")
        {
            var column = ResolveColumn(item.Field, sourceTable, joinAliases, joins, foreignColumns);
            if (column is null)
            {
                return null;
            }
            var (paramName, _) = BindValue(item.Value, parameters);
            return $"DATEDIFF(day,{column},GETDATE()) = {paramName}";
        }
        if (op is "IS_NULL" or "IS_NOT_NULL")
        {
            var column = item.Left is not null
                ? CompileExpression(item.Left, sourceTable, joinAliases, parameters, joins, foreignColumns)
                : ResolveColumn(item.Field, sourceTable, joinAliases, joins, foreignColumns);
            return column is null ? null : $"{column} {(op == "IS_NULL" ? "IS NULL" : "IS NOT NULL")}";
        }

        var left = CompileExpression(
            item.Left ?? new ChooserFilterExpression("column", Table: null, Column: item.Field),
            sourceTable, joinAliases, parameters, joins, foreignColumns);
        if (left is null)
        {
            return null;
        }
        var right = CompileExpression(
            item.Right ?? new ChooserFilterExpression("literal", Value: item.Value ?? ""),
            sourceTable, joinAliases, parameters, joins, foreignColumns);
        if (right is null)
        {
            return null;
        }

        var nullSafe = string.IsNullOrWhiteSpace(item.NullSafe) ? null : item.NullSafe.Trim().ToUpperInvariant();
        if (nullSafe is not null)
        {
            if (!NullSafe.Contains(nullSafe))
            {
                return null;
            }
            left = nullSafe == "ZERO" ? $"ISNULL({left},0)" : $"ISNULL({left},'')";
        }

        if (ComparisonOperators.Contains(op))
        {
            var sqlOp = op switch
            {
                "EQ" => "=",
                "NE" => "<>",
                "GT" => ">",
                "GE" => ">=",
                "LT" => "<",
                "LE" => "<=",
                "LIKE" => "LIKE",
                "NOT_LIKE" => "NOT LIKE",
                _ => throw new InvalidOperationException(),
            };
            return $"{left} {sqlOp} {right}";
        }
        return null;
    }

    private static string? CompileExpression(
        ChooserFilterExpression expr,
        string sourceTable,
        IReadOnlySet<string> joinAliases,
        List<ChooserFilterParameter> parameters,
        List<string> joins,
        List<(string Table, string Column)> foreignColumns)
    {
        switch (expr.Kind?.ToLowerInvariant())
        {
            case "column":
                return ResolveColumn(expr.Column, expr.Table, sourceTable, joinAliases, joins, foreignColumns);
            case "literal":
            {
                var (paramName, _) = BindValue(expr.Value, parameters);
                return paramName;
            }
            case "template":
            {
                var (paramName, _) = BindValue(expr.Value, parameters);
                return paramName;
            }
            case "isnull":
            {
                var inner = CompileExpression(expr.Left!, sourceTable, joinAliases, parameters, joins, foreignColumns);
                if (inner is null)
                {
                    return null;
                }
                return $"ISNULL({inner},{expr.Value ?? "0"})";
            }
            case "arith":
            {
                if (expr.Op is not ("+" or "-" or "*" or "/"))
                {
                    return null;
                }
                var left = CompileExpression(expr.Left!, sourceTable, joinAliases, parameters, joins, foreignColumns);
                var right = CompileExpression(expr.Right!, sourceTable, joinAliases, parameters, joins, foreignColumns);
                if (left is null || right is null)
                {
                    return null;
                }
                return $"({left} {expr.Op} {right})";
            }
            case "datediff":
            {
                var column = CompileExpression(expr.Left!, sourceTable, joinAliases, parameters, joins, foreignColumns);
                if (column is null)
                {
                    return null;
                }
                return $"DATEDIFF({expr.Value ?? "day"},{column},GETDATE())";
            }
            default:
                return null;
        }
    }

    private static string? CompileSubquery(
        ChooserFilterItem item,
        string sourceTable,
        IReadOnlySet<string> joinAliases,
        List<ChooserFilterParameter> parameters,
        List<string> joins,
        List<(string Table, string Column)> foreignColumns)
    {
        var op = item.Operator?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(op) || !SubqueryOperators.Contains(op))
        {
            return null;
        }
        var sub = item.Subquery!;

        string fromSql;
        IReadOnlySet<string> subAliases;
        if (!string.IsNullOrWhiteSpace(sub.Function))
        {
            if (!SubqueryFunctions.Contains(sub.Function!.Trim()))
            {
                return null;
            }
            var function = sub.Function.Trim();
            if (!WorkbenchSql.Identifier.IsMatch(function))
            {
                return null;
            }
            var args = (sub.Args ?? []).Select(arg => BindValue(arg, parameters).ParameterName);
            fromSql = $"dbo.[{function}]({string.Join(",", args)})";
            subAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var tables = sub.From is { Count: > 0 }
                ? sub.From
                : (sub.Table is { Length: > 0 } ? [new ChooserSubqueryTable(sub.Table)] : []);
            if (tables.Count == 0)
            {
                return null;
            }
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var segments = new List<string>();
            foreach (var t in tables)
            {
                if (!WorkbenchSql.Identifier.IsMatch(t.Table))
                {
                    return null;
                }
                var alias = string.IsNullOrWhiteSpace(t.Alias) ? t.Table : t.Alias.Trim();
                if (!WorkbenchSql.Identifier.IsMatch(alias) || !aliases.Add(alias))
                {
                    return null;
                }
                segments.Add($"dbo.[{t.Table}] [{alias}]");
            }
            fromSql = string.Join(", ", segments);
            subAliases = aliases;
        }

        string? whereSql = null;
        if (sub.Filter is { Count: > 0 })
        {
            var whereParts = new List<string>();
            foreach (var filterItem in sub.Filter)
            {
                var compiled = CompileItem(filterItem, sourceTable, subAliases, parameters, joins, foreignColumns, 1);
                if (compiled is null)
                {
                    return null;
                }
                whereParts.Add($"({compiled})");
            }
            whereSql = " WHERE " + string.Join(" AND ", whereParts);
        }

        var left = item.Left ?? new ChooserFilterExpression("column", Table: null, Column: item.Field);
        switch (op)
        {
            case "IN":
            case "NOT_IN":
            {
                if (string.IsNullOrWhiteSpace(sub.Column) || !WorkbenchSql.Identifier.IsMatch(sub.Column.Trim()))
                {
                    return null;
                }
                var leftSql = CompileExpression(left, sourceTable, joinAliases, parameters, joins, foreignColumns);
                if (leftSql is null)
                {
                    return null;
                }
                var keyword = op == "IN" ? "IN" : "NOT IN";
                return $"{leftSql} {keyword} (SELECT [{sub.Column.Trim()}] FROM {fromSql}{whereSql})";
            }
            case "EXISTS":
            case "NOT_EXISTS":
            {
                var keyword = op == "EXISTS" ? "EXISTS" : "NOT EXISTS";
                return $"{keyword} (SELECT 1 FROM {fromSql}{whereSql})";
            }
            case "IN_FUNCTION":
            {
                if (string.IsNullOrWhiteSpace(sub.Column) || !WorkbenchSql.Identifier.IsMatch(sub.Column.Trim()))
                {
                    return null;
                }
                var leftSql = CompileExpression(left, sourceTable, joinAliases, parameters, joins, foreignColumns);
                if (leftSql is null)
                {
                    return null;
                }
                return $"{leftSql} IN (SELECT [{sub.Column.Trim()}] FROM {fromSql})";
            }
            default:
                return null;
        }
    }

    private static string? ResolveColumn(
        string? field,
        string sourceTable,
        IReadOnlySet<string> joinAliases,
        List<string> joins,
        List<(string Table, string Column)> foreignColumns)
        => ResolveColumn(field, null, sourceTable, joinAliases, joins, foreignColumns);

    private static string? ResolveColumn(
        string? field,
        string? explicitTable,
        string sourceTable,
        IReadOnlySet<string> joinAliases,
        List<string> joins,
        List<(string Table, string Column)> foreignColumns)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return null;
        }
        var name = field.Trim();
        if (explicitTable is not null)
        {
            if (!WorkbenchSql.Identifier.IsMatch(explicitTable) || !WorkbenchSql.Identifier.IsMatch(name))
            {
                return null;
            }
            if (string.Equals(explicitTable, sourceTable, StringComparison.OrdinalIgnoreCase))
            {
                return $"[{sourceTable}].[{name}]";
            }
            if (!joinAliases.Contains(explicitTable))
            {
                return null;
            }
            joins.Add(explicitTable);
            foreignColumns.Add((explicitTable, name));
            return $"[{explicitTable}].[{name}]";
        }

        var dot = name.IndexOf('.');
        if (dot < 0)
        {
            if (!WorkbenchSql.Identifier.IsMatch(name))
            {
                return null;
            }
            return $"[{sourceTable}].[{name}]";
        }
        var table = name[..dot].Trim();
        var column = name[(dot + 1)..].Trim();
        if (!WorkbenchSql.Identifier.IsMatch(table) || !WorkbenchSql.Identifier.IsMatch(column))
        {
            return null;
        }
        if (string.Equals(table, sourceTable, StringComparison.OrdinalIgnoreCase))
        {
            return $"[{sourceTable}].[{column}]";
        }
        if (!joinAliases.Contains(table))
        {
            return null;
        }
        joins.Add(table);
        foreignColumns.Add((table, column));
        return $"[{table}].[{column}]";
    }

    private static (string ParameterName, string RawValue) BindValue(
        string? value,
        List<ChooserFilterParameter> parameters)
    {
        var raw = value ?? string.Empty;
        var paramName = $"@cf{parameters.Count}";
        parameters.Add(new ChooserFilterParameter(paramName, raw));
        return (paramName, raw);
    }

    /// <summary>
    /// 运行期参数绑定：把原始值文本中的模板 token 替换为当前主表/明细/模块值后返回，
    /// 供 SqlParameter 使用（{m.X}/{d.X} 取字段值，{module} 取模块号，{X} 裸模板按字面量保留）。
    /// </summary>
    public static string BindRuntimeValue(
        ChooserFilterParameter parameter,
        IReadOnlyDictionary<string, string>? masterValues,
        IReadOnlyDictionary<string, string>? detailValues,
        int moduleId)
    {
        return TemplateToken.Replace(parameter.RawValue, match =>
        {
            if (match.Groups["m"].Success)
            {
                var key = match.Groups["m"].Value;
                return masterValues is not null && masterValues.TryGetValue(key, out var master) ? master : "";
            }
            if (match.Groups["d"].Success)
            {
                var key = match.Groups["d"].Value;
                return detailValues is not null && detailValues.TryGetValue(key, out var detail) ? detail : "";
            }
            if (match.Groups["mod"].Success)
            {
                return moduleId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            // {X} 裸模板：按字面量保留
            return match.Value;
        });
    }
}
