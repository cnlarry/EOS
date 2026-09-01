using System.Text;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// 存量 CHOOSE_FILTER 受限反向解析器（ADR-008 §6 三档转换 + P3 档二全自动转换）：
/// - 档一：纯比较链（表.列 运算符 值 AND 链 + 模板 + ISNULL 简单宏 + DATEDIFF 当日）；
/// - 档二（P3 扩展）：OR 组 / NOT(...) / 列间算术 / 常量条件 / 简单 IN/NOT IN 子查询 /
///   EXISTS / 受控表值函数 IN / IS NULL；
/// - 档三：脏数据/超复杂（f_get_* 自定义函数串、截断、乱码、未闭合引号、无法表达的多表子查询），
///   人工清单，绝不猜测落库。
/// 跨表引用有效性由调用方传入 JoinAliases（源表 QUERY_RELATION 受控解析）；转换结果必须能通过
/// <see cref="ChooserFilterCompiler"/> 编译 smoke test，否则自动降档。
/// </summary>
public static class LegacyChooserFilterConverter
{
    public sealed record ConvertResult(ChooserFilterStruct? Struct, int Tier, string? Error);

    /// <summary>
    /// 转换选项：JoinAliases=源表可引用跨表别名（缺省不允许跨表）；
    /// BareColumnTable=裸列归属（裸列名→物理表，来自 QUERY_RELATION JOIN 链唯一命中列，可选）；
    /// ColumnTypes=表.列→类型（可选）。
    /// </summary>
    public sealed record ConvertOptions(
        IReadOnlySet<string>? JoinAliases = null,
        IReadOnlyDictionary<string, string>? ColumnTypes = null,
        IReadOnlyDictionary<string, string>? BareColumnTable = null);

    private static readonly Regex NumberLiteral = new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);

    private static readonly Regex SegmentPattern = new(
        @"^\s*(?<field>[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?)\s*" +
        @"(?<op>=|<>|!=|>=|<=|>|<|LIKE|NOT\s+LIKE)\s*" +
        @"(?<value>'(?:[^']|'')*'|[^\s]+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IsNullZeroMacro = new(
        @"^\s*ISNULL\s*\(\s*(?<field>[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?)\s*,\s*0\s*\)\s*=\s*0\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IsNullEmptyMacro = new(
        @"^\s*ISNULL\s*\(\s*(?<field>[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?)\s*,\s*''\s*\)\s*=\s*''\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IsNullCompare = new(
        @"^\s*ISNULL\s*\(\s*(?<field>[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?)\s*,\s*(?<def>0|'')\s*\)\s*" +
        @"(?<op>=|<>|!=|>=|<=|>|<)\s*(?<value>'(?:[^']|'')*'|[^\s]+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DatediffMacro = new(
        @"^\s*DATEDIFF\s*\(\s*day\s*,\s*(?<field>[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?)\s*,\s*GETDATE\s*\(\s*\)\s*\)\s*" +
        @"(?<op>=|<>|!=|>=|<=|>|<)\s*(?<value>'(?:[^']|'')*'|[^\s]+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Comparison = new(
        @"^(?<left>.+?)\s*(?<op>=|<>|!=|>=|<=|>|<|LIKE|NOT\s+LIKE)\s*(?<right>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SubqueryPattern = new(
        @"^(?<left>.+?)\s+(?<op>NOT\s+IN|IN)\s*\(\s*SELECT\s+(?<col>[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?)\s+FROM\s+(?<from>.+)\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExistsPattern = new(
        @"^(?<op>EXISTS|NOT\s+EXISTS)\s*\(\s*SELECT\s+(?<col>[A-Za-z0-9_*]+(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\s+FROM\s+(?<from>.+)\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IsNullPattern = new(
        @"^(?<left>.+?)\s+(?<op>IS\s+NULL|IS\s+NOT\s+NULL)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NotGroup = new(
        @"^\s*NOT\s*\((?<inner>.+)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>转换入口。sourceTable 用于裸字段限定与错误提示；跨表有效性由 options.JoinAliases 提供。</summary>
    public static ConvertResult Convert(string? legacy, string sourceTable, ConvertOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(legacy))
        {
            return new ConvertResult(null, 1, null);
        }
        var text = Normalize(legacy.Trim());
        if (IsGarbage(text))
        {
            return new ConvertResult(null, 3, "脏数据（乱码/截断/未闭合引号/悬空运算符）");
        }
        var aliases = options?.JoinAliases ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 档一：纯比较链
        var tier1 = TryParseTier1(text, sourceTable, out var tier1Error);
        if (tier1 is not null)
        {
            tier1 = QualifyBareFields(tier1, sourceTable, options?.BareColumnTable);
            return Verify(tier1, sourceTable, aliases)
                ? new ConvertResult(tier1, 1, null)
                : new ConvertResult(null, 2, $"结构合法但跨表不可用：{tier1Error ?? ""}");
        }

        // 档二：扩展解析（OR 组/算术/子查询/常量/函数/IS NULL）
        var bareColumnTable = options?.BareColumnTable;
        var extended = TryParseExtended(text, sourceTable, aliases, bareColumnTable, out var extendedError);
        if (extended is not null)
        {
            extended = QualifyBareFields(extended, sourceTable, bareColumnTable);
            return Verify(extended, sourceTable, aliases)
                ? new ConvertResult(extended, 1, null)
                : new ConvertResult(null, 2, $"结构合法但跨表不可用：{extendedError ?? ""}");
        }
        if (LooksLikeCondition(text))
        {
            return new ConvertResult(null, 2, extendedError ?? "需扩展算子或跨表引用不可用");
        }
        return new ConvertResult(null, 3, $"不可识别：{tier1Error ?? extendedError ?? ""}");
    }

    private static bool Verify(ChooserFilterStruct filter, string sourceTable, IReadOnlySet<string> aliases) =>
        ChooserFilterCompiler.Compile(filter, sourceTable, aliases, null) is not null;

    /// <summary>
    /// 统一裸列归属规范化：item.Field、column 表达式（无显式表）与子查询 filter 的裸字段，
    /// 按裸列归属表（QUERY_RELATION JOIN 链唯一命中）补表前缀；无归属时按 defaultTable。
    /// </summary>
    private static ChooserFilterStruct QualifyBareFields(
        ChooserFilterStruct filter,
        string defaultTable,
        IReadOnlyDictionary<string, string>? bareColumnTable)
    {
        var items = filter.Items
            .Select(item => QualifyBareItem(item, defaultTable, bareColumnTable))
            .ToArray();
        return new ChooserFilterStruct(filter.Logic, items);
    }

    private static ChooserFilterItem QualifyBareItem(
        ChooserFilterItem item,
        string defaultTable,
        IReadOnlyDictionary<string, string>? bareColumnTable)
    {
        var group = item.Group is null
            ? null
            : QualifyBareFields(item.Group, defaultTable, bareColumnTable);
        ChooserFilterSubquery? subquery = item.Subquery;
        if (item.Subquery?.Filter is { Count: > 0 })
        {
            var subFirstTable = item.Subquery.Function is not null
                ? defaultTable
                : (item.Subquery.From is { Count: > 0 }
                    ? (string.IsNullOrWhiteSpace(item.Subquery.From[0].Alias)
                        ? item.Subquery.From[0].Table
                        : item.Subquery.From[0].Alias!)
                    : defaultTable);
            subquery = item.Subquery with
            {
                Filter = QualifyBareFields(new ChooserFilterStruct("AND", item.Subquery.Filter), subFirstTable, bareColumnTable).Items,
            };
        }
        return item with
        {
            Field = !string.IsNullOrWhiteSpace(item.Field) && !item.Field.Contains('.', StringComparison.Ordinal)
                ? QualifyBare(item.Field!, defaultTable, bareColumnTable)
                : item.Field,
            Left = QualifyBareExpression(item.Left, defaultTable, bareColumnTable),
            Right = QualifyBareExpression(item.Right, defaultTable, bareColumnTable),
            Group = group,
            Subquery = subquery,
        };
    }

    private static ChooserFilterExpression? QualifyBareExpression(
        ChooserFilterExpression? expr,
        string defaultTable,
        IReadOnlyDictionary<string, string>? bareColumnTable)
    {
        if (expr is null)
        {
            return null;
        }
        // 兜底拆分：Column 含点但 Table 未拆（个别表达式路径漏拆），统一为 Table/Column 分离
        if (expr.Kind.Equals("column", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(expr.Table)
            && expr.Column is { Length: > 0 }
            && expr.Column.Contains('.', StringComparison.Ordinal))
        {
            var dot = expr.Column.IndexOf('.');
            return expr with
            {
                Table = expr.Column[..dot].Trim(),
                Column = expr.Column[(dot + 1)..].Trim(),
            };
        }
        if (expr.Kind.Equals("column", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(expr.Table)
            && expr.Column is { Length: > 0 }
            && !expr.Column.Contains('.', StringComparison.Ordinal))
        {
            return expr with { Column = QualifyBare(expr.Column, defaultTable, bareColumnTable) };
        }
        return expr with
        {
            Left = QualifyBareExpression(expr.Left, defaultTable, bareColumnTable),
            Right = QualifyBareExpression(expr.Right, defaultTable, bareColumnTable),
        };
    }

    // ==================== 档一（P1 保留） ====================

    private static ChooserFilterStruct? TryParseTier1(string text, string sourceTable, out string error)
    {
        error = "";
        var segments = SplitTopLevelAnd(text);
        if (segments.Count == 0)
        {
            error = "无法拆分 AND 链";
            return null;
        }
        var items = new List<ChooserFilterItem>();
        foreach (var segment in segments)
        {
            if (!TryParseSegment(segment, out var item, out var segmentError))
            {
                error = $"{segment}（{segmentError}）";
                return null;
            }
            items.Add(item!);
        }
        return new ChooserFilterStruct("AND", items);
    }

    private static List<string> SplitTopLevelAnd(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\'')
            {
                if (inQuote && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    current.Append("''");
                    i++;
                    continue;
                }
                inQuote = !inQuote;
                current.Append(ch);
                continue;
            }
            if (!inQuote && char.IsWhiteSpace(ch)
                && text.AsSpan(i).StartsWith(" AND ", StringComparison.OrdinalIgnoreCase))
            {
                var part = current.ToString().Trim();
                if (part.Length > 0) parts.Add(part);
                current.Clear();
                i += 4;
                continue;
            }
            current.Append(ch);
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0) parts.Add(tail);
        return parts;
    }

    private static bool TryParseSegment(string segment, out ChooserFilterItem? item, out string error)
    {
        item = null;
        error = "";
        var m = IsNullZeroMacro.Match(segment);
        if (m.Success)
        {
            item = new ChooserFilterItem(NormalizeField(m.Groups["field"].Value), "ISNULL_ZERO");
            return true;
        }
        m = IsNullEmptyMacro.Match(segment);
        if (m.Success)
        {
            item = new ChooserFilterItem(NormalizeField(m.Groups["field"].Value), "EQ", "", "EMPTY");
            return true;
        }
        m = DatediffMacro.Match(segment);
        if (m.Success)
        {
            item = new ChooserFilterItem(
                NormalizeField(m.Groups["field"].Value),
                "DAYS_FROM_TODAY",
                NormalizeValue(m.Groups["value"].Value));
            return true;
        }
        m = IsNullCompare.Match(segment);
        if (m.Success)
        {
            var defaultValue = m.Groups["def"].Value;
            var nullSafe = defaultValue == "0" ? "ZERO" : "EMPTY";
            item = new ChooserFilterItem(
                NormalizeField(m.Groups["field"].Value),
                MapOperator(m.Groups["op"].Value),
                NormalizeValue(m.Groups["value"].Value),
                nullSafe);
            return true;
        }
        m = SegmentPattern.Match(segment);
        if (m.Success)
        {
            var valueText = m.Groups["value"].Value;
            var value = NormalizeValue(valueText);
            // 未加引号的裸标识符（列=列/裸列引用）模型表达不了 → 交档二（保守，不猜测）
            if (value is not null && !valueText.StartsWith('\'') && WorkbenchSql.Identifier.IsMatch(valueText)
                && !valueText.StartsWith('{') && !string.Equals(valueText, "NULL", StringComparison.OrdinalIgnoreCase))
            {
                error = $"右侧为裸列引用：{valueText}";
                return false;
            }
            item = new ChooserFilterItem(NormalizeField(m.Groups["field"].Value), MapOperator(m.Groups["op"].Value), value);
            return true;
        }
        error = "不匹配比较链语法";
        return false;
    }

    // ==================== 档二（P3 扩展） ====================

    private static ChooserFilterStruct? TryParseExtended(
        string text,
        string sourceTable,
        IReadOnlySet<string> aliases,
        IReadOnlyDictionary<string, string>? bareColumnTable,
        out string error)
    {
        error = "";
        var parsed = ParseCondition(text, sourceTable, sourceTable, aliases, bareColumnTable, out error, 0);
        return parsed;
    }

    /// <summary>递归解析条件：顶层 AND/OR → 原子（比较/宏/子查询/括号组）。defaultTable 用于裸字段限定。</summary>
    private static ChooserFilterStruct? ParseCondition(
        string text,
        string sourceTable,
        string defaultTable,
        IReadOnlySet<string> aliases,
        IReadOnlyDictionary<string, string>? bareColumnTable,
        out string error,
        int depth)
    {
        error = "";
        if (depth > 8)
        {
            error = "条件嵌套过深";
            return null;
        }
        var trimmed = text.Trim();
        // 顶层 OR 优先（SQL 中 OR 优先级低于 AND）
            if (TrySplitTopLevel(trimmed, "OR", out var orParts))
            {
                var items = new List<ChooserFilterItem>();
                foreach (var part in orParts)
                {
                    var nested = ParseCondition(part, sourceTable, defaultTable, aliases, bareColumnTable, out var partError, depth + 1);
                if (nested is null)
                {
                    error = partError;
                    return null;
                }
                items.Add(new ChooserFilterItem(Group: nested));
            }
            return new ChooserFilterStruct("OR", items);
        }
        if (TrySplitTopLevel(trimmed, "AND", out var andParts))
        {
            var items = new List<ChooserFilterItem>();
            foreach (var part in andParts)
            {
                var nested = ParseCondition(part, sourceTable, defaultTable, aliases, bareColumnTable, out var partError, depth + 1);
                if (nested is null)
                {
                    error = partError;
                    return null;
                }
                items.AddRange(nested.Items);
            }
            return new ChooserFilterStruct("AND", items);
        }
        var atom = ParseAtom(trimmed, sourceTable, defaultTable, aliases, bareColumnTable, out error);
        return atom is null ? null : new ChooserFilterStruct("AND", [atom]);
    }

    private static ChooserFilterItem? ParseAtom(
        string text,
        string sourceTable,
        string defaultTable,
        IReadOnlySet<string> aliases,
        IReadOnlyDictionary<string, string>? bareColumnTable,
        out string error)
    {
        error = "";
        var trimmed = text.Trim();

        // NOT ( ... )
        var not = NotGroup.Match(trimmed);
        if (not.Success)
        {
            var inner = ParseCondition(not.Groups["inner"].Value, sourceTable, defaultTable, aliases, bareColumnTable, out error, 1);
            return inner is null ? null : new ChooserFilterItem(Negate: true, Group: inner);
        }
        // 括号组（整体包裹）
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')') && IsBalanced(trimmed))
        {
            var inner = ParseCondition(trimmed[1..^1], sourceTable, defaultTable, aliases, bareColumnTable, out error, 1);
            return inner is null
                ? null
                : inner.Items.Count == 1 ? inner.Items[0] : new ChooserFilterItem(Group: inner);
        }
        // EXISTS / NOT EXISTS
        var exists = ExistsPattern.Match(trimmed);
        if (exists.Success)
        {
            var fromText = SplitWhere(exists.Groups["from"].Value, out var whereText);
            var sub = ParseSubqueryFrom(fromText, sourceTable, aliases, out error);
            if (sub is not null && whereText is not null)
            {
                sub = WithFilter(sub, whereText, sourceTable, aliases, bareColumnTable, out error);
                if (sub is null)
                {
                    return null;
                }
            }
            return sub is null
                ? null
                : new ChooserFilterItem(
                    Operator: exists.Groups["op"].Value.Trim().ToUpperInvariant().Replace(" ", "_"),
                    Subquery: sub);
        }
        // IN / NOT IN 子查询
        var subq = SubqueryPattern.Match(trimmed);
        if (subq.Success)
        {
            var left = ParseExpression(subq.Groups["left"].Value, sourceTable, aliases, out error);
            var fromText = SplitWhere(subq.Groups["from"].Value, out var whereText);
            var sub = ParseSubqueryFrom(fromText, sourceTable, aliases, out error);
            if (sub is not null && whereText is not null)
            {
                sub = WithFilter(sub, whereText, sourceTable, aliases, bareColumnTable, out error);
                if (sub is null)
                {
                    return null;
                }
            }
            if (left is null || sub is null)
            {
                return null;
            }
            var op = subq.Groups["op"].Value.Trim().ToUpperInvariant().Replace(" ", "_");
            // 表值函数子查询 → 受控函数算子（存量仅 IN）
            if (sub.Function is not null && op == "IN")
            {
                op = "IN_FUNCTION";
            }
            var selectColumn = subq.Groups["col"].Value.Trim();
            var dot = selectColumn.IndexOf('.');
            if (dot >= 0)
            {
                selectColumn = selectColumn[(dot + 1)..].Trim();
            }
            return new ChooserFilterItem(
                Operator: op,
                Left: left,
                Subquery: sub with { Column = selectColumn });
        }
        // IS NULL / IS NOT NULL
        var isNull = IsNullPattern.Match(trimmed);
        if (isNull.Success)
        {
            var left = ParseExpression(isNull.Groups["left"].Value, sourceTable, aliases, out error);
            if (left is null)
            {
                return null;
            }
            return new ChooserFilterItem(
                Operator: isNull.Groups["op"].Value.Trim().ToUpperInvariant().Replace(" ", "_"),
                Left: left);
        }
        // ISNULL(...) op 值（含算术表达式）
        var isnullCompare = IsNullCompareExtended.Match(trimmed);
        if (isnullCompare.Success)
        {
            var inner = ParseExpression(isnullCompare.Groups["expr"].Value, sourceTable, aliases, out error);
            var value = ParseValue(isnullCompare.Groups["value"].Value, out error);
            if (inner is null || value is null)
            {
                return null;
            }
            var defaultValue = isnullCompare.Groups["def"].Value;
            return new ChooserFilterItem(
                Operator: MapOperator(isnullCompare.Groups["op"].Value),
                Left: new ChooserFilterExpression("isnull", Left: inner, Value: defaultValue == "0" ? "0" : "''"),
                Right: value);
        }
        // DATEDIFF(day, expr, GETDATE()) op 值
        var datediff = DatediffCompareExtended.Match(trimmed);
        if (datediff.Success)
        {
            var column = ParseExpression(datediff.Groups["expr"].Value, sourceTable, aliases, out error);
            var value = ParseValue(datediff.Groups["value"].Value, out error);
            if (column is null || value is null)
            {
                return null;
            }
            return new ChooserFilterItem(
                Operator: MapOperator(datediff.Groups["op"].Value),
                Left: new ChooserFilterExpression("datediff", Left: column, Value: "day"),
                Right: value);
        }
        // 比较（含常量条件与列=列）
        var comparison = Comparison.Match(trimmed);
        if (comparison.Success)
        {
            var leftText = comparison.Groups["left"].Value.Trim();
            var rightText = comparison.Groups["right"].Value.Trim();
            var left = ParseExpression(leftText, sourceTable, aliases, out error);
            var right = ParseValue(rightText, out error);
            if (left is null || right is null)
            {
                return null;
            }
            var op = MapOperator(comparison.Groups["op"].Value);
            // 简单「列 op 字面量/模板」→ 旧式 item（兼容 P1 结构）
            if (left.Kind == "column" && string.IsNullOrWhiteSpace(left.Table)
                && right.Kind is "literal" or "template")
            {
                return new ChooserFilterItem(
                    Field: QualifyBare(left.Column!, defaultTable, bareColumnTable),
                    Operator: op,
                    Value: right.Value);
            }
            return new ChooserFilterItem(Operator: op, Left: left, Right: right);
        }
        error = "原子条件无法解析";
        return null;
    }

    private static readonly Regex IsNullCompareExtended = new(
        @"^\s*ISNULL\s*\(\s*(?<expr>.+?)\s*,\s*(?<def>0|'')\s*\)\s*" +
        @"(?<op>=|<>|!=|>=|<=|>|<)\s*(?<value>'(?:[^']|'')*'|[^\s]+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DatediffCompareExtended = new(
        @"^\s*DATEDIFF\s*\(\s*day\s*,\s*(?<expr>.+?)\s*,\s*GETDATE\s*\(\s*\)\s*\)\s*" +
        @"(?<op>=|<>|!=|>=|<=|>|<)\s*(?<value>'(?:[^']|'')*'|[^\s]+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>解析子查询 FROM（单表/逗号多表/表值函数）→ ChooserFilterSubquery（Column 由调用方回填）。</summary>
    private static ChooserFilterSubquery? ParseSubqueryFrom(
        string fromText,
        string sourceTable,
        IReadOnlySet<string> aliases,
        out string error)
    {
        error = "";
        var from = fromText.Trim();
        // 表值函数：dbo.f_get_xxx(args)
        var function = Regex.Match(from, @"^dbo\.(?<fn>[A-Za-z_][A-Za-z0-9_]*)\((?<args>.*)\)$", RegexOptions.IgnoreCase);
        if (function.Success)
        {
            var args = SplitArguments(function.Groups["args"].Value);
            return new ChooserFilterSubquery(
                Function: function.Groups["fn"].Value,
                Args: args.Select(NormalizeValue).ToList());
        }
        // 逗号多表：t1,t2（含 WHERE 已在外部剥离）
        var tables = new List<ChooserSubqueryTable>();
        foreach (var part in from.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tableMatch = Regex.Match(part.Trim(), @"^(?<t>[A-Za-z_][A-Za-z0-9_]*)(?:\s+(?<a>[A-Za-z_][A-Za-z0-9_]*))?$", RegexOptions.IgnoreCase);
            if (!tableMatch.Success || !WorkbenchSql.Identifier.IsMatch(tableMatch.Groups["t"].Value))
            {
                error = $"子查询 FROM 无法解析：{part}";
                return null;
            }
            tables.Add(new ChooserSubqueryTable(
                tableMatch.Groups["t"].Value,
                tableMatch.Groups["a"].Success ? tableMatch.Groups["a"].Value : null));
        }
        if (tables.Count == 0)
        {
            error = "子查询缺少 FROM 表";
            return null;
        }
        return new ChooserFilterSubquery(From: tables);
    }

    /// <summary>把子查询 FROM 段按顶层 WHERE 拆分为表列表 + 条件（引号/括号感知；无 WHERE 时条件为 null）。</summary>
    private static string SplitWhere(string fromText, out string? whereText)
    {
        whereText = null;
        var depth = 0;
        var inQuote = false;
        for (var i = 0; i < fromText.Length; i++)
        {
            var ch = fromText[i];
            if (ch == '\'')
            {
                inQuote = !inQuote;
                continue;
            }
            if (inQuote) continue;
            if (ch == '(') depth++;
            if (ch == ')') depth--;
            if (depth == 0 && char.IsWhiteSpace(ch)
                && fromText.AsSpan(i).StartsWith(" WHERE ", StringComparison.OrdinalIgnoreCase))
            {
                var before = fromText[..i].Trim();
                whereText = fromText[(i + 7)..].Trim();
                return before;
            }
        }
        return fromText.Trim();
    }

    /// <summary>
    /// 给子查询附加 WHERE 条件：子查询短别名（如 HR_BASEPAY_M m）规范化为物理表名
    /// （避免与 {m.X} 模板前缀混淆），裸字段按子查询首表/裸列归属限定。
    /// </summary>
    private static ChooserFilterSubquery? WithFilter(
        ChooserFilterSubquery sub,
        string whereText,
        string sourceTable,
        IReadOnlySet<string> aliases,
        IReadOnlyDictionary<string, string>? bareColumnTable,
        out string error)
    {
        var firstTable = sub.Function is not null
            ? sourceTable
            : (sub.From is { Count: > 0 }
                ? (string.IsNullOrWhiteSpace(sub.From[0].Alias) ? sub.From[0].Table : sub.From[0].Alias!)
                : sourceTable);
        // 短别名 → 物理表名（引号/模板感知：{m.X} 前缀不替换）
        if (sub.From is { Count: > 0 })
        {
            foreach (var t in sub.From)
            {
                var alias = string.IsNullOrWhiteSpace(t.Alias) ? t.Table : t.Alias.Trim();
                if (alias.Equals(t.Table, StringComparison.OrdinalIgnoreCase)) continue;
                whereText = Regex.Replace(
                    whereText,
                    $@"(?<![\w{{])\b{Regex.Escape(alias)}\.",
                    $"{t.Table}.",
                    RegexOptions.IgnoreCase);
            }
        }
        var filter = ParseCondition(whereText, sourceTable, firstTable, aliases, bareColumnTable, out error, 1);
        if (filter is null)
        {
            return null;
        }
        return sub with { Filter = filter.Items };
    }

    private static List<string> SplitArguments(string args)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        foreach (var ch in args)
        {
            if (ch == '\'')
            {
                inQuote = !inQuote;
                current.Append(ch);
                continue;
            }
            if (!inQuote && ch == ',')
            {
                result.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0) result.Add(tail);
        return result;
    }

    /// <summary>左侧表达式：column / 算术 / isnull / datediff / 字面量 / 模板。</summary>
    private static ChooserFilterExpression? ParseExpression(
        string text,
        string sourceTable,
        IReadOnlySet<string> aliases,
        out string error)
    {
        error = "";
        var trimmed = text.Trim();
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')') && IsBalanced(trimmed))
        {
            return ParseExpression(trimmed[1..^1], sourceTable, aliases, out error);
        }
        // ISNULL(expr, default)
        var isnull = Regex.Match(trimmed, @"^ISNULL\s*\(\s*(?<expr>.+?)\s*,\s*(?<def>0|'')\s*\)$", RegexOptions.IgnoreCase);
        if (isnull.Success)
        {
            var inner = ParseExpression(isnull.Groups["expr"].Value, sourceTable, aliases, out error);
            return inner is null
                ? null
                : new ChooserFilterExpression("isnull", Left: inner, Value: isnull.Groups["def"].Value == "0" ? "0" : "''");
        }
        // DATEDIFF(part, col, GETDATE())
        var datediff = Regex.Match(trimmed, @"^DATEDIFF\s*\(\s*(?<part>[a-z]+)\s*,\s*(?<col>.+?)\s*,\s*GETDATE\s*\(\s*\)\s*\)$", RegexOptions.IgnoreCase);
        if (datediff.Success)
        {
            var column = ParseExpression(datediff.Groups["col"].Value, sourceTable, aliases, out error);
            return column is null
                ? null
                : new ChooserFilterExpression("datediff", Left: column, Value: datediff.Groups["part"].Value.ToLowerInvariant());
        }
        // 算术：最右 + - * /（避开负数/模板）
        if (TrySplitArithmetic(trimmed, out var arithOp, out var arithLeft, out var arithRight))
        {
            var left = ParseExpression(arithLeft, sourceTable, aliases, out error);
            var right = ParseExpression(arithRight, sourceTable, aliases, out error);
            return left is null || right is null
                ? null
                : new ChooserFilterExpression("arith", Op: arithOp, Left: left, Right: right);
        }
        // 字面量/模板
        if (trimmed.StartsWith('\'') && trimmed.EndsWith('\'') && trimmed.Length >= 2)
        {
            var inner = NormalizeValue(trimmed);
            return inner.StartsWith('{')
                ? new ChooserFilterExpression("template", Value: inner)
                : new ChooserFilterExpression("literal", Value: inner);
        }
        if (NumberLiteral.IsMatch(trimmed))
        {
            return new ChooserFilterExpression("literal", Value: trimmed);
        }
        if (trimmed.StartsWith('{'))
        {
            return new ChooserFilterExpression("template", Value: NormalizeValue(trimmed));
        }
        // 列（表.列 或 裸列）
        if (WorkbenchSql.Identifier.IsMatch(trimmed) || Regex.IsMatch(trimmed, @"^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$"))
        {
            var normalized = NormalizeField(trimmed);
            var dot = normalized.IndexOf('.');
            return dot >= 0
                ? new ChooserFilterExpression("column", Table: normalized[..dot].Trim(), Column: normalized[(dot + 1)..].Trim())
                : new ChooserFilterExpression("column", Column: normalized);
        }
        error = $"表达式无法解析：{trimmed}";
        return null;
    }

    /// <summary>右侧值：字面量/模板/裸列引用（列=列）。</summary>
    private static ChooserFilterExpression? ParseValue(string text, out string error)
    {
        error = "";
        var trimmed = text.Trim();
        if (trimmed.StartsWith('\'') && trimmed.EndsWith('\'') && trimmed.Length >= 2)
        {
            var inner = NormalizeValue(trimmed);
            return inner.StartsWith('{')
                ? new ChooserFilterExpression("template", Value: inner)
                : new ChooserFilterExpression("literal", Value: inner);
        }
        if (NumberLiteral.IsMatch(trimmed))
        {
            return new ChooserFilterExpression("literal", Value: trimmed);
        }
        if (trimmed.StartsWith('{'))
        {
            return new ChooserFilterExpression("template", Value: NormalizeValue(trimmed));
        }
        if (WorkbenchSql.Identifier.IsMatch(trimmed) || Regex.IsMatch(trimmed, @"^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$"))
        {
            var normalized = NormalizeField(trimmed);
            var dot = normalized.IndexOf('.');
            return dot >= 0
                ? new ChooserFilterExpression("column", Table: normalized[..dot].Trim(), Column: normalized[(dot + 1)..].Trim())
                : new ChooserFilterExpression("column", Column: normalized);
        }
        // 右侧也可以是表达式（ISNULL/DATEDIFF/算术，如 ISNULL(A,0)>ISNULL(B,0)）
        return ParseExpression(trimmed, "", new HashSet<string>(StringComparer.OrdinalIgnoreCase), out error);
    }

    private static bool TrySplitArithmetic(string text, out string op, out string left, out string right)
    {
        op = "";
        left = "";
        right = "";
        // 从右往左找第一个不在引号/括号内层的 + - * /
        var depth = 0;
        var inQuote = false;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (ch == '\'')
            {
                inQuote = !inQuote;
                continue;
            }
            if (inQuote) continue;
            if (ch == ')') depth++;
            if (ch == '(') depth--;
            if (depth == 0 && (ch is '+' or '-' or '*' or '/'))
            {
                // 排除负号字面量（- 前无内容）
                if (ch == '-' && (i == 0 || text[i - 1] is '(' or ',' or ' '))
                {
                    continue;
                }
                op = ch.ToString();
                left = text[..i].Trim();
                right = text[(i + 1)..].Trim();
                return left.Length > 0 && right.Length > 0;
            }
        }
        return false;
    }

    /// <summary>顶层逻辑拆分（引号+括号感知）；返回 true 且 parts.Count&gt;1 时表示拆分成功。</summary>
    private static bool TrySplitTopLevel(string text, string keyword, out List<string> parts)
    {
        parts = [];
        var depth = 0;
        var inQuote = false;
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\'')
            {
                if (inQuote && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    current.Append("''");
                    i++;
                    continue;
                }
                inQuote = !inQuote;
                current.Append(ch);
                continue;
            }
            if (!inQuote && ch == '(') depth++;
            if (!inQuote && ch == ')') depth--;
            if (!inQuote && depth == 0 && char.IsWhiteSpace(ch)
                && text.AsSpan(i).StartsWith($" {keyword} ", StringComparison.OrdinalIgnoreCase))
            {
                var part = current.ToString().Trim();
                if (part.Length > 0) parts.Add(part);
                current.Clear();
                i += keyword.Length + 1;
                continue;
            }
            current.Append(ch);
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0) parts.Add(tail);
        return parts.Count > 1;
    }

    private static bool IsBalanced(string text)
    {
        var depth = 0;
        var inQuote = false;
        foreach (var ch in text)
        {
            if (ch == '\'')
            {
                inQuote = !inQuote;
                continue;
            }
            if (inQuote) continue;
            if (ch == '(') depth++;
            if (ch == ')') depth--;
            if (depth < 0) return false;
        }
        return depth == 0;
    }

    // ==================== 通用 ====================

    private static string Normalize(string text)
    {
        // 引号感知规整：引号内字面量原样保留，引号外压缩空白并规整括号/方括号/模板
        var result = CollapseOutsideQuotes(text);
        result = Regex.Replace(result, @"\[([A-Za-z_][A-Za-z0-9_]*)\]\.\[([A-Za-z_][A-Za-z0-9_]*)\]", "$1.$2");
        result = Regex.Replace(result, @"\[([A-Za-z_][A-Za-z0-9_]*)\]\.([A-Za-z_][A-Za-z0-9_]*)", "$1.$2");
        return result.Trim();
    }

    private static string CollapseOutsideQuotes(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\'')
            {
                if (inQuote && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    sb.Append("''");
                    i++;
                    continue;
                }
                inQuote = !inQuote;
                sb.Append(ch);
                continue;
            }
            if (inQuote)
            {
                sb.Append(ch);
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) && sb[^1] != '(')
                {
                    // 保留一个空格作为分隔，除非前一字符是 '('
                    sb.Append(' ');
                }
                continue;
            }
            if (ch == ')' && sb.Length > 0 && sb[^1] == ' ')
            {
                sb.Length--;
            }
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static string NormalizeField(string field)
    {
        var trimmed = field.Trim();
        var dot = trimmed.IndexOf('.');
        if (dot < 0) return trimmed;
        var table = trimmed[..dot].Trim();
        var column = trimmed[(dot + 1)..].Trim();
        return $"{table}.{column}";
    }

    private static string QualifyBare(
        string column,
        string defaultTable,
        IReadOnlyDictionary<string, string>? bareColumnTable)
    {
        if (column.Contains('.', StringComparison.Ordinal))
        {
            return column;
        }
        if (bareColumnTable is not null && bareColumnTable.TryGetValue(column, out var table))
        {
            return $"{table}.{column}";
        }
        return $"{defaultTable}.{column}";
    }

    private static string NormalizeValue(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            value = value[1..^1].Replace("''", "'");
        }
        if (value.StartsWith("{M.", StringComparison.OrdinalIgnoreCase))
        {
            value = "{m." + value[3..];
        }
        else if (value.StartsWith("{D.", StringComparison.OrdinalIgnoreCase))
        {
            value = "{d." + value[3..];
        }
        return value;
    }

    private static string MapOperator(string op) => op.Trim().ToUpperInvariant() switch
    {
        "=" => "EQ",
        "<>" or "!=" => "NE",
        ">" => "GT",
        ">=" => "GE",
        "<" => "LT",
        "<=" => "LE",
        "LIKE" => "LIKE",
        "NOT LIKE" => "NOT_LIKE",
        _ => throw new InvalidOperationException($"未知算子 {op}"),
    };

    private static bool IsGarbage(string text)
    {
        if (text.Contains('\uFFFD')) return true;
        if (text.Any(ch => char.IsControl(ch) && ch != '\t' && ch != '\n' && ch != '\r')) return true;
        if (Regex.IsMatch(text, @"(AND|OR)\s*$", RegexOptions.IgnoreCase)) return true;
        var quoteCount = text.Count(ch => ch == '\'');
        if (quoteCount % 2 != 0) return true;
        return false;
    }

    private static bool LooksLikeCondition(string text) =>
        Regex.IsMatch(text, @"=|<>|!=|>|<|LIKE|IN|EXISTS|IS NULL", RegexOptions.IgnoreCase);
}
