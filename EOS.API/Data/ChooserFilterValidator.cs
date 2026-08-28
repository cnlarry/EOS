using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// FILTER_STRUCT 保存即校验（ADR-008 §3 + P3）：来源表物理列存在、算子合法、模板占位符可解析、
/// 跨表引用在源表 QUERY_RELATION JOIN 白名单内、子查询表存在；模型表达不了的条件拒绝保存（fail-closed）。
/// 与 <see cref="ChooserFilterCompiler"/> 共用同一套白名单与编译 smoke test。
/// </summary>
public static class ChooserFilterValidator
{
    private static readonly HashSet<string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        "EQ", "NE", "GT", "GE", "LT", "LE", "LIKE", "NOT_LIKE", "ISNULL_ZERO", "DAYS_FROM_TODAY",
        "IS_NULL", "IS_NOT_NULL", "IN", "NOT_IN", "EXISTS", "NOT_EXISTS", "IN_FUNCTION",
    };

    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "bigint", "smallint", "tinyint", "decimal", "numeric", "float", "real", "money", "smallmoney",
    };

    /// <summary>ISNULL(列,0) 兼容类型：数值 + bit（SQL Server 允许 ISNULL(bit 列,0)，旧系统广泛使用）。</summary>
    private static readonly HashSet<string> IsNullZeroTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "bigint", "smallint", "tinyint", "decimal", "numeric", "float", "real", "money", "smallmoney", "bit",
    };

    private static readonly HashSet<string> StringTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "nchar", "nvarchar", "text", "ntext",
    };

    private static readonly HashSet<string> ExpressionKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "column", "literal", "template", "isnull", "arith", "datediff",
    };

    /// <summary>纯静态校验（不查库）：结构/算子/列名格式/类型/编译 smoke；columnTypes 为「表.列 → 类型」。</summary>
    public static IReadOnlyList<string> Validate(
        ChooserFilterStruct? filter,
        string sourceTable,
        IReadOnlySet<string>? joinAliases,
        IReadOnlyDictionary<string, string>? columnTypes)
    {
        var errors = new List<string>();
        if (filter is null) return errors;
        var aliases = joinAliases ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var types = columnTypes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (filter.Items.Count == 0)
        {
            return errors;
        }
        foreach (var item in filter.Items)
        {
            ValidateItem(item, sourceTable, aliases, types, errors, 0);
        }
        if (errors.Count == 0
            && ChooserFilterCompiler.Compile(filter, sourceTable, aliases, types) is null)
        {
            errors.Add("条件无法编译为参数化谓词。");
        }
        return errors;
    }

    /// <summary>
    /// DB 校验：读取源表 QUERY_RELATION JOIN 目录（受控解析）+ 补齐被引用表物理列类型后做静态校验。
    /// </summary>
    public static async Task<IReadOnlyList<string>> ValidateAsync(
        SqlConnection connection,
        ChooserFilterStruct? filter,
        string sourceTable,
        CancellationToken token)
    {
        if (filter is null) return [];
        var catalog = await ChooserJoinCatalog.GetAsync(connection, sourceTable, token);
        if (catalog.Error is not null)
        {
            return [$"源表 {sourceTable} 的 QUERY_RELATION 解析失败（fail-closed）：{catalog.Error}"];
        }
        var referencedTables = new List<string> { sourceTable };
        CollectReferencedTables(filter, catalog.Aliases, referencedTables);
        var columnTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in referencedTables.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!WorkbenchSql.Identifier.IsMatch(table)) continue;
            await using var command = new SqlCommand(
                """
                SELECT c.name, TYPE_NAME(c.user_type_id)
                FROM sys.columns c
                JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                JOIN sys.schemas s ON o.schema_id=s.schema_id
                WHERE s.name=N'dbo' AND o.name=@Table;
                """, connection);
            command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                columnTypes[$"{table}.{reader.GetString(0)}"] = reader.GetString(1);
            }
        }
        return Validate(filter, sourceTable, catalog.Aliases, columnTypes);
    }

    private static void CollectReferencedTables(
        ChooserFilterStruct filter,
        IReadOnlySet<string> aliases,
        ICollection<string> referencedTables)
    {
        foreach (var item in filter.Items)
        {
            CollectReferencedTables(item, aliases, referencedTables);
            if (item.Group is not null)
            {
                CollectReferencedTables(item.Group, aliases, referencedTables);
            }
            if (item.Subquery is not null)
            {
                var tables = item.Subquery.From is { Count: > 0 }
                    ? item.Subquery.From.Select(t => t.Table)
                    : (item.Subquery.Table is { Length: > 0 } ? [item.Subquery.Table] : []);
                foreach (var table in tables)
                {
                    referencedTables.Add(table);
                }
                if (item.Subquery.Filter is { Count: > 0 })
                {
                    foreach (var filterItem in item.Subquery.Filter)
                    {
                        CollectReferencedTables(filterItem, aliases, referencedTables);
                    }
                }
            }
        }
    }

    private static void CollectReferencedTables(
        ChooserFilterItem item,
        IReadOnlySet<string> aliases,
        ICollection<string> referencedTables)
    {
        CollectExpressionTable(item.Left, referencedTables);
        CollectExpressionTable(item.Right, referencedTables);
        if (item.Field is { Length: > 0 } && item.Field.Contains('.', StringComparison.Ordinal))
        {
            var table = item.Field[..item.Field.IndexOf('.')].Trim();
            if (aliases.Contains(table)) referencedTables.Add(table);
        }
    }

    private static void CollectExpressionTable(ChooserFilterExpression? expr, ICollection<string> referencedTables)
    {
        if (expr is null) return;
        if (expr.Kind.Equals("column", StringComparison.OrdinalIgnoreCase))
        {
            if (expr.Table is { Length: > 0 })
            {
                referencedTables.Add(expr.Table);
            }
            else if (expr.Column is { Length: > 0 } && expr.Column.Contains('.', StringComparison.Ordinal))
            {
                referencedTables.Add(expr.Column[..expr.Column.IndexOf('.')].Trim());
            }
        }
        CollectExpressionTable(expr.Left, referencedTables);
        CollectExpressionTable(expr.Right, referencedTables);
    }

    private static void ValidateItem(
        ChooserFilterItem item,
        string sourceTable,
        IReadOnlySet<string> joinAliases,
        IReadOnlyDictionary<string, string> columnTypes,
        ICollection<string> errors,
        int depth)
    {
        if (depth > 8)
        {
            errors.Add("条件嵌套过深（>8 层）。");
            return;
        }
        if (item.Group is not null)
        {
            foreach (var nested in item.Group.Items)
            {
                ValidateItem(nested, sourceTable, joinAliases, columnTypes, errors, depth + 1);
            }
            return;
        }
        if (item.Subquery is not null)
        {
            ValidateSubquery(item, sourceTable, joinAliases, columnTypes, errors);
            return;
        }

        var op = item.Operator?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(op))
        {
            errors.Add("条件缺少 operator。");
            return;
        }
        if (!Operators.Contains(op))
        {
            errors.Add($"算子 {op} 不受支持。");
            return;
        }
        if (op is "ISNULL_ZERO" or "DAYS_FROM_TODAY" or "IS_NULL" or "IS_NOT_NULL")
        {
            if (item.Left is not null)
            {
                ValidateExpression(item.Left, sourceTable, joinAliases, columnTypes, errors);
            }
            else
            {
                ValidateColumnReference(item.Field, null, sourceTable, joinAliases, columnTypes, errors);
            }
            return;
        }
        ValidateExpression(item.Left ?? new ChooserFilterExpression("column", Column: item.Field),
            sourceTable, joinAliases, columnTypes, errors);
        ValidateExpression(item.Right ?? new ChooserFilterExpression("literal", Value: item.Value ?? ""),
            sourceTable, joinAliases, columnTypes, errors);

        var nullSafe = string.IsNullOrWhiteSpace(item.NullSafe) ? null : item.NullSafe.Trim().ToUpperInvariant();
        if (nullSafe is not null && nullSafe is not ("ZERO" or "EMPTY"))
        {
            errors.Add($"nullSafe 仅支持 ZERO/EMPTY。");
        }
        if (nullSafe == "ZERO" && item.Left is not null && item.Left.Kind == "column")
        {
            var type = ResolveType(item.Left.Table, item.Left.Column, sourceTable, columnTypes);
            if (type is not null && !IsNullZeroTypes.Contains(type))
            {
                errors.Add($"{item.Left.Table}.{item.Left.Column} 为 {type}，ISNULL(,0) 仅支持数值/bit 列。");
            }
        }
        if (nullSafe == "EMPTY" && item.Left is not null && item.Left.Kind == "column")
        {
            var type = ResolveType(item.Left.Table, item.Left.Column, sourceTable, columnTypes);
            if (type is not null && !StringTypes.Contains(type))
            {
                errors.Add($"{item.Left.Table}.{item.Left.Column} 为 {type}，ISNULL(,'') 仅支持字符列。");
            }
        }
    }

    private static void ValidateSubquery(
        ChooserFilterItem item,
        string sourceTable,
        IReadOnlySet<string> joinAliases,
        IReadOnlyDictionary<string, string> columnTypes,
        ICollection<string> errors)
    {
        var op = item.Operator?.Trim().ToUpperInvariant();
        if (!ChooserFilterCompiler.SubqueryOperators.Contains(op ?? ""))
        {
            errors.Add($"子查询算子 {op} 不受支持。");
            return;
        }
        var sub = item.Subquery!;
        if (op is "IN" or "NOT_IN" or "IN_FUNCTION")
        {
            ValidateExpression(item.Left ?? new ChooserFilterExpression("column", Column: item.Field),
                sourceTable, joinAliases, columnTypes, errors);
        }
        if (!string.IsNullOrWhiteSpace(sub.Function))
        {
            if (!ChooserFilterCompiler.SubqueryFunctions.Contains(sub.Function.Trim()))
            {
                errors.Add($"表值函数 {sub.Function} 不在受控白名单内。");
            }
            return;
        }
        var tables = sub.From is { Count: > 0 }
            ? sub.From
            : (sub.Table is { Length: > 0 } ? [new ChooserSubqueryTable(sub.Table)] : []);
        if (tables.Count == 0)
        {
            errors.Add("子查询缺少 FROM 表。");
            return;
        }
        if (sub.Filter is { Count: > 0 })
        {
            var subAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                sourceTable,
            };
            foreach (var t in tables)
            {
                subAliases.Add(string.IsNullOrWhiteSpace(t.Alias) ? t.Table : t.Alias.Trim());
            }
            foreach (var filterItem in sub.Filter)
            {
                ValidateItem(filterItem, sourceTable, subAliases, columnTypes, errors, 1);
            }
        }
    }

    private static void ValidateExpression(
        ChooserFilterExpression expr,
        string sourceTable,
        IReadOnlySet<string> joinAliases,
        IReadOnlyDictionary<string, string> columnTypes,
        ICollection<string> errors)
    {
        if (expr is null)
        {
            errors.Add("表达式缺失。");
            return;
        }
        var kind = expr.Kind?.ToLowerInvariant();
        if (kind is null || !ExpressionKinds.Contains(kind))
        {
            errors.Add($"表达式类型 {expr.Kind} 不受支持。");
            return;
        }
        switch (kind)
        {
            case "column":
                ValidateColumnReference(expr.Column, expr.Table, sourceTable, joinAliases, columnTypes, errors);
                break;
            case "isnull":
                if (expr.Left is null)
                {
                    errors.Add("isnull 表达式缺少 inner。");
                }
                else
                {
                    ValidateExpression(expr.Left, sourceTable, joinAliases, columnTypes, errors);
                }
                break;
            case "arith":
                if (expr.Op is not ("+" or "-" or "*" or "/"))
                {
                    errors.Add($"算术算子 {expr.Op} 不受支持。");
                }
                ValidateExpression(expr.Left!, sourceTable, joinAliases, columnTypes, errors);
                ValidateExpression(expr.Right!, sourceTable, joinAliases, columnTypes, errors);
                break;
            case "datediff":
                if (expr.Left is null)
                {
                    errors.Add("datediff 表达式缺少 column。");
                }
                else
                {
                    ValidateExpression(expr.Left, sourceTable, joinAliases, columnTypes, errors);
                }
                break;
        }
    }

    private static void ValidateColumnReference(
        string? field,
        string? explicitTable,
        string sourceTable,
        IReadOnlySet<string> joinAliases,
        IReadOnlyDictionary<string, string> columnTypes,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            errors.Add("列引用缺失。");
            return;
        }
        var name = field.Trim();
        string table;
        string column;
        if (explicitTable is not null)
        {
            table = explicitTable;
            column = name;
        }
        else if (name.Contains('.', StringComparison.Ordinal))
        {
            var dot = name.IndexOf('.');
            table = name[..dot].Trim();
            column = name[(dot + 1)..].Trim();
        }
        else
        {
            table = sourceTable;
            column = name;
        }
        if (!WorkbenchSql.Identifier.IsMatch(table) || !WorkbenchSql.Identifier.IsMatch(column))
        {
            errors.Add($"列引用非法：{name}。");
            return;
        }
        if (!string.Equals(table, sourceTable, StringComparison.OrdinalIgnoreCase)
            && !joinAliases.Contains(table))
        {
            errors.Add($"跨表引用 {table} 不在源表 QUERY_RELATION JOIN 白名单内。");
            return;
        }
        if (columnTypes.Count > 0 && ResolveType(table, column, sourceTable, columnTypes) is null)
        {
            errors.Add($"{table} 无物理列 {column}。");
        }
    }

    private static string? ResolveType(
        string? table,
        string? column,
        string sourceTable,
        IReadOnlyDictionary<string, string> columnTypes)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            return null;
        }
        var qualifiedTable = string.IsNullOrWhiteSpace(table) ? sourceTable : table!.Trim();
        if (columnTypes.TryGetValue($"{qualifiedTable}.{column.Trim()}", out var type))
        {
            return type;
        }
        return columnTypes.TryGetValue(column.Trim(), out var bare) ? bare : null;
    }
}
