using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>QUERY_RELATION 中一条受控 LEFT JOIN（表/别名/条件全部来自服务端元数据并经严格校验）。</summary>
public sealed record VirtualJoin(string Table, string Alias, IReadOnlyList<VirtualJoinCondition> Conditions);

/// <summary>JOIN ON 条件：两侧均为「表.列」恒等比较，无值、无函数、无子查询。</summary>
public sealed record VirtualJoinCondition(string LeftTable, string LeftColumn, string RightTable, string RightColumn);

/// <summary>虚拟字段受控解析结果：可解析列片段 + 所需 JOIN 片段 + 已解析/未解析字段。</summary>
public sealed record VirtualColumnResolution(
    IReadOnlyList<string> SelectFragments,
    string JoinFragment,
    IReadOnlyList<string> ResolvedKeys,
    IReadOnlyList<string> UnresolvedKeys,
    IReadOnlyList<string> BaseColumns);

/// <summary>
/// VIRTUAL_EXP 受控解析（阶段 4 v2）。
/// 仅接受「表.列」单跨表取值：表必须出现在本表 TABLES.QUERY_RELATION 的 LEFT JOIN 白名单
/// （按别名解析，别名可不同于物理表名，如 PRODUCT_J），列必须在物理表/视图中真实存在。
/// QUERY_RELATION 本身也按严格语法解析（仅 LEFT JOIN + 表.列=表.列 条件），
/// 任何无法安全解析的表达式/关系一律不进入 SQL（字段不渲染）。
/// </summary>
internal static class VirtualExpressionParser
{
    private static readonly Regex SimpleReference = new(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\.[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex BaseSegment = new(@"^(?<table>[A-Za-z_][A-Za-z0-9_]{0,127})(?:\s+WITH\s*\(\s*NOLOCK\s*\))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JoinSegment = new(
        @"^(?<table>[A-Za-z_][A-Za-z0-9_]{0,127})" +
        @"(?:\s+WITH\s*\(\s*NOLOCK\s*\)|\s+(?<alias>[A-Za-z_][A-Za-z0-9_]{0,127})(?:\s+WITH\s*\(\s*NOLOCK\s*\))?)?" +
        @"\s+ON\s+(?<conds>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Condition = new(
        @"^(?<lt>[A-Za-z_][A-Za-z0-9_]{0,127})\.(?<lc>[A-Za-z_][A-Za-z0-9_]{0,127})\s*=\s*" +
        @"(?<rt>[A-Za-z_][A-Za-z0-9_]{0,127})\.(?<rc>[A-Za-z_][A-Za-z0-9_]{0,127})$",
        RegexOptions.Compiled);

    /// <summary>仅接受「表.列」单跨表取值；其余一律拒绝。</summary>
    public static bool TryParseExpression(string? expression, out string table, out string column)
    {
        table = "";
        column = "";
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var normalized = expression.Trim();
        if (!SimpleReference.IsMatch(normalized)) return false;
        var separator = normalized.IndexOf('.');
        table = normalized[..separator];
        column = normalized[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// 解析 QUERY_RELATION 为受控 JOIN 列表（严格语法，任何异常片段整体拒绝）。
    /// 空关系（仅基表、无 JOIN）合法；条件两侧必须引用基表或已声明的 JOIN 别名。
    /// </summary>
    public static bool TryParseRelation(string? relation, string baseTable, out IReadOnlyList<VirtualJoin> joins, out string? error)
    {
        joins = [];
        error = null;
        if (string.IsNullOrWhiteSpace(relation)) { error = "QUERY_RELATION 为空"; return false; }
        var normalized = Regex.Replace(relation.Trim(), @"\s+", " ");
        // 旧数据存在 WITH(NOLOCK)ON 无空格变体，规整为 ") ON" 后再解析。
        normalized = Regex.Replace(normalized, @"\)\s*ON", ") ON", RegexOptions.IgnoreCase);
        var segments = Regex.Split(normalized, @"\s+LEFT\s+JOIN\s+", RegexOptions.IgnoreCase);
        var baseMatch = BaseSegment.Match(segments[0].Trim());
        if (!baseMatch.Success || !baseMatch.Groups["table"].Value.Equals(baseTable, StringComparison.OrdinalIgnoreCase))
        {
            error = $"QUERY_RELATION 基表不一致：{segments[0].Trim()}";
            return false;
        }
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseTable };
        var result = new List<VirtualJoin>();
        foreach (var segment in segments.Skip(1))
        {
            var match = JoinSegment.Match(segment.Trim());
            if (!match.Success) { error = $"JOIN 段无法解析：{segment.Trim()}"; return false; }
            var table = match.Groups["table"].Value;
            var alias = match.Groups["alias"].Success ? match.Groups["alias"].Value : table;
            if (!aliases.Add(alias)) { error = $"JOIN 别名重复：{alias}"; return false; }
            var conditions = new List<VirtualJoinCondition>();
            foreach (var part in Regex.Split(match.Groups["conds"].Value, @"\s+AND\s+", RegexOptions.IgnoreCase))
            {
                var condition = Condition.Match(part.Trim());
                if (!condition.Success) { error = $"JOIN 条件无法解析：{part.Trim()}"; return false; }
                var leftTable = condition.Groups["lt"].Value;
                var rightTable = condition.Groups["rt"].Value;
                if (!aliases.Contains(leftTable) || !aliases.Contains(rightTable))
                {
                    error = $"JOIN 条件引用未声明表/别名：{part.Trim()}";
                    return false;
                }
                conditions.Add(new(leftTable, condition.Groups["lc"].Value, rightTable, condition.Groups["rc"].Value));
            }
            if (conditions.Count == 0) { error = $"JOIN 缺少 ON 条件：{segment.Trim()}"; return false; }
            result.Add(new(table, alias, conditions));
        }
        joins = result;
        return true;
    }
}

/// <summary>
/// 虚拟字段 → SQL 片段的解析执行器：QUERY_RELATION 白名单 + INFORMATION_SCHEMA 物理存在校验。
/// 任一校验失败只影响该字段（不渲染），不抛错、不泄漏。
/// </summary>
public sealed class VirtualColumnResolver(SqlConnection connection)
{
    private readonly Dictionary<string, HashSet<string>> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _existingTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingTables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 对虚拟字段子集做受控解析，返回可直接拼入 SELECT/FROM 的片段。
    /// 未解析字段全部返回 UnresolvedKeys（调用方不渲染）；解析永不抛 SQL 注入类异常。
    /// </summary>
    public async Task<VirtualColumnResolution> ResolveAsync(
        string table,
        IReadOnlyList<WorkbenchField> virtualFields,
        CancellationToken token,
        string? baseAlias = null)
    {
        baseAlias ??= table;
        var selected = virtualFields.Where(field => field.IsVirtual).ToList();
        if (selected.Count == 0) return new([], "", [], [], []);

        var relation = await ReadQueryRelationAsync(table, token);
        if (!VirtualExpressionParser.TryParseRelation(relation, table, out var joins, out var parseError))
        {
            return new([], "", [], selected.Select(field => field.Key).ToList(), []);
        }

        var byAlias = joins.ToDictionary(join => join.Alias, StringComparer.OrdinalIgnoreCase);
        var candidate = new List<(WorkbenchField Field, string? Alias, string? Column, string? Fragment, HashSet<string> Needed)>();
        var unresolved = new List<string>();
        foreach (var field in selected)
        {
            if (!VirtualExpressionParser.TryParseExpression(field.VirtualExpression, out var reference, out var column))
            {
                // P5：非简单引用走受控算术/常量子集解析（语法白名单 + 表/列物理存在校验）
                if (!VirtualArithmeticParser.TryParse(field.VirtualExpression ?? string.Empty, out var tokens, out _))
                {
                    unresolved.Add(field.Key);
                    continue;
                }
                var fragment = new System.Text.StringBuilder();
                var arithNeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var arithmeticOk = true;
                foreach (var exprToken in tokens)
                {
                    if (exprToken.Kind == "Ref")
                    {
                        string alias;
                        string targetTable;
                        if (exprToken.Table!.Equals(table, StringComparison.OrdinalIgnoreCase))
                        {
                            alias = baseAlias;
                            targetTable = table;
                        }
                        else if (byAlias.TryGetValue(exprToken.Table!, out var arithJoin))
                        {
                            alias = arithJoin.Alias;
                            targetTable = arithJoin.Table;
                            arithNeeded.Add(arithJoin.Alias);
                        }
                        else
                        {
                            arithmeticOk = false;
                            break;
                        }
                        if (!await ColumnExistsAsync(targetTable, exprToken.Column!, token))
                        {
                            arithmeticOk = false;
                            break;
                        }
                        fragment.Append($"[{alias}].[{exprToken.Column}]");
                        continue;
                    }
                    fragment.Append(exprToken.Kind == "String"
                        ? $"N'{exprToken.Text.Replace("'", "''")}'"
                        : exprToken.Text);
                }
                if (!arithmeticOk)
                {
                    unresolved.Add(field.Key);
                    continue;
                }
                candidate.Add((field, null, null, fragment.ToString(), arithNeeded));
                continue;
            }
            if (reference.Equals(table, StringComparison.OrdinalIgnoreCase))
            {
                if (await ColumnExistsAsync(table, column, token)) candidate.Add((field, baseAlias, column, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                else unresolved.Add(field.Key);
                continue;
            }
            if (!byAlias.TryGetValue(reference, out var join))
            {
                unresolved.Add(field.Key);
                continue;
            }
            if (await ColumnExistsAsync(join.Table, column, token)) candidate.Add((field, join.Alias, column, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { join.Alias }));
            else unresolved.Add(field.Key);
        }

        // JOIN 可用性：物理表存在且 ON 条件两侧列存在，且依赖的 JOIN 均可用。
        var usable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await ResolveUsableJoinsAsync(joins, byAlias, table, usable, token);

        // 逐字段计算所需 JOIN 闭包，闭包内任一 JOIN 不可用则字段不渲染。
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectFragments = new List<string>();
        var resolvedKeys = new List<string>();
        foreach (var item in candidate)
        {
            var fieldNeeded = new HashSet<string>(item.Needed, StringComparer.OrdinalIgnoreCase);
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var join in joins)
                {
                    if (!fieldNeeded.Contains(join.Alias)) continue;
                    foreach (var condition in join.Conditions)
                    {
                        foreach (var side in new[] { condition.LeftTable, condition.RightTable })
                        {
                            if (byAlias.TryGetValue(side, out var dependency) && fieldNeeded.Add(dependency.Alias))
                                changed = true;
                        }
                    }
                }
            }
            if (fieldNeeded.All(alias => usable[alias]))
            {
                selectFragments.Add((item.Fragment ?? $"[{item.Alias}].[{item.Column}]") + $" AS [{item.Field.Key}]");
                resolvedKeys.Add(item.Field.Key);
                needed.UnionWith(fieldNeeded);
            }
            else
            {
                unresolved.Add(item.Field.Key);
            }
        }

        var joinFragment = string.Concat(
            joins.Where(join => needed.Contains(join.Alias)).Select(join =>
                $" LEFT JOIN dbo.[{join.Table}] AS [{join.Alias}] WITH (NOLOCK) ON {string.Join(" AND ", join.Conditions.Select(condition => FormatCondition(condition, table, baseAlias)))}"));
        var baseColumns = joins
            .Where(join => needed.Contains(join.Alias))
            .SelectMany(join => join.Conditions)
            .SelectMany(condition => new[]
            {
                (condition.LeftTable, condition.LeftColumn),
                (condition.RightTable, condition.RightColumn),
            })
            .Where(side => side.Item1.Equals(table, StringComparison.OrdinalIgnoreCase))
            .Select(side => side.Item2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new(selectFragments, joinFragment, resolvedKeys, unresolved, baseColumns);
    }

    private static string FormatCondition(VirtualJoinCondition condition, string baseTable, string baseAlias) =>
        $"{FormatSide(condition.LeftTable, condition.LeftColumn, baseTable, baseAlias)} = {FormatSide(condition.RightTable, condition.RightColumn, baseTable, baseAlias)}";

    private static string FormatSide(string table, string column, string baseTable, string baseAlias) =>
        table.Equals(baseTable, StringComparison.OrdinalIgnoreCase)
            ? $"[{baseAlias}].[{column}]"
            : $"[{table}].[{column}]";

    /// <summary>自底向上计算 JOIN 可用性（含传递依赖），结果写入 usable（别名 → 是否可用）。</summary>
    private async Task ResolveUsableJoinsAsync(
        IReadOnlyList<VirtualJoin> joins,
        IReadOnlyDictionary<string, VirtualJoin> byAlias,
        string baseTable,
        Dictionary<string, bool> usable,
        CancellationToken token)
    {
        var order = joins.Select(join => join.Alias).ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var alias in order)
            {
                if (usable.ContainsKey(alias)) continue;
                var join = byAlias[alias];
                // 依赖的 JOIN 尚未评估 → 本轮跳过（QUERY_RELATION 顺序保证无环，下轮必可评估）。
                var pendingDependency = join.Conditions
                    .SelectMany(condition => new[] { condition.LeftTable, condition.RightTable })
                    .FirstOrDefault(side => !side.Equals(alias, StringComparison.OrdinalIgnoreCase)
                                            && byAlias.TryGetValue(side, out _)
                                            && !usable.ContainsKey(side));
                if (pendingDependency is not null) continue;
                var ok = await TableExistsAsync(join.Table, token);
                if (ok)
                {
                    foreach (var condition in join.Conditions)
                    {
                        foreach (var side in new[] { (condition.LeftTable, condition.LeftColumn), (condition.RightTable, condition.RightColumn) })
                        {
                            var physicalTable = side.Item1.Equals(baseTable, StringComparison.OrdinalIgnoreCase)
                                ? baseTable
                                : side.Item1.Equals(alias, StringComparison.OrdinalIgnoreCase)
                                    ? join.Table
                                : byAlias.TryGetValue(side.Item1, out var dependency)
                                    ? (usable.TryGetValue(dependency.Alias, out var dependencyUsable) && dependencyUsable ? dependency.Table : null)
                                    : null;
                            if (physicalTable is null)
                            {
                                ok = false;
                                break;
                            }
                            if (!await ColumnExistsAsync(physicalTable, side.Item2, token))
                            {
                                ok = false;
                                break;
                            }
                        }
                        if (!ok) break;
                    }
                }
                usable[alias] = ok;
                changed = true;
            }
        }
        // 防御：理论不可能残留（无环），但残留时按不可用处理，避免 KeyNotFound 崩溃。
        foreach (var alias in order)
        {
            if (!usable.ContainsKey(alias)) usable[alias] = false;
        }
    }

    private async Task<string?> ReadQueryRelationAsync(string table, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT QUERY_RELATION FROM dbo.TABLES WITH (NOLOCK) WHERE T_ID=@Table",
            connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<bool> TableExistsAsync(string table, CancellationToken token)
    {
        if (_existingTables.Contains(table)) return true;
        if (_missingTables.Contains(table)) return false;
        await using var command = new SqlCommand(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES t WHERE t.TABLE_SCHEMA='dbo' AND t.TABLE_NAME=@Table) THEN 1 ELSE 0 END",
            connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1;
        if (exists) _existingTables.Add(table); else _missingTables.Add(table);
        return exists;
    }

    private async Task<bool> ColumnExistsAsync(string table, string column, CancellationToken token)
    {
        if (!_columns.TryGetValue(table, out var columns))
        {
            columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var command = new SqlCommand(
                "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@Table",
                connection);
            command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) columns.Add(reader.GetString(0));
            _columns[table] = columns;
        }
        return columns.Contains(column);
    }
}
