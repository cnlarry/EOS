using System.Text.Json;

namespace EOS.API.Data.Effects;

/// <summary>One parameter produced while compiling a condition fragment.</summary>
public sealed record EffectSqlParameter(string Name, object? Value);

/// <summary>A compiled, fully parameterized SQL predicate.</summary>
public sealed record EffectSqlFragment(string Sql, IReadOnlyList<EffectSqlParameter> Parameters)
{
    public static readonly EffectSqlFragment True = new("1=1", Array.Empty<EffectSqlParameter>());
}

/// <summary>
/// Compiles CONDITION_STRUCT (closed operator JSON) into a parameterized SQL predicate.
/// Supported closed types: field-compare / value-eq / value-neq / not-exists / switch.
/// Identifiers in the output are configuration identifiers already validated at save
/// time and re-validated here against the physical-column whitelist; user values only
/// ever travel as SQL parameters.
/// </summary>
public sealed class EffectConditionCompiler
{
    private static readonly IReadOnlySet<string> CompareOperators =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EQ", "NEQ", "GT", "GE", "LT", "LE" };

    /// <summary>
    /// Comparison operand: either a single field ({scope, field}) or a closed
    /// signed sum ({scope, terms:[{field, coef:1/-1}]}, same shape as SOURCE_TERMS).
    /// Terms let completion conditions express offset groups such as
    /// FINISHED_SEND_QTY + BACK_MATERIAL + BACK_BAD; coefficients stay ±1, no
    /// arithmetic beyond addition/subtraction, identifiers fail closed.
    /// </summary>
    private static string CompareFieldOrTerms(JsonElement side, string alias)
    {
        if (side.ValueKind == JsonValueKind.Object
            && side.TryGetProperty("terms", out var terms)
            && terms.ValueKind == JsonValueKind.Array
            && terms.GetArrayLength() > 0)
        {
            var parts = new List<string>();
            foreach (var term in terms.EnumerateArray())
            {
                var field = term.ValueKind == JsonValueKind.Object
                    && term.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
                    ? f.GetString()!.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(field))
                    throw new EffectConfigException("比较加减项缺少字符串 field。");
                var coef = term.TryGetProperty("coef", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n)
                    ? n
                    : 1;
                if (coef is not (1 or -1))
                    throw new EffectConfigException($"比较加减项 '{field}' 的 coef 仅允许 1 / -1。");
                var column = $"COALESCE({alias}.{Identifier(field)}, 0)";
                parts.Add(coef == -1 ? "- " + column : (parts.Count == 0 ? "" : "+ ") + column);
            }
            var expression = string.Join(" ", parts);
            return parts.Count > 1 && expression.StartsWith("- ") ? "(" + expression + ")" : expression;
        }
        return $"{alias}.{Identifier(side)}";
    }

    private static string SqlOp(string op) => op.ToUpperInvariant() switch
    {
        "EQ" => "=",
        "NEQ" => "<>",
        "GT" => ">",
        "GE" => ">=",
        "LT" => "<",
        "LE" => "<=",
        _ => throw new EffectConfigException($"条件比较符 '{op}' 不在封闭比较符集内。"),
    };

    /// <summary>
    /// Resolves a condition source scope to the SQL alias it reads from, or null when the
    /// scope cannot participate in the current statement (e.g. DETAIL on a master-only module).
    /// </summary>
    public delegate string? ScopeAliasResolver(string scope, string? table);

    /// <summary>Validates a SYSSS switch column against the physical column whitelist.</summary>
    public delegate bool IsSysssColumn(string column);

    private int _paramIndex;

    public EffectSqlFragment Compile(
        JsonElement condition,
        ScopeAliasResolver resolveAlias,
        IsSysssColumn isSysssColumn,
        string outerAlias = "T")
    {
        if (condition.ValueKind != JsonValueKind.Object
            || !condition.TryGetProperty("logic", out var logic) || logic.ValueKind != JsonValueKind.String
            || logic.GetString() is not ("AND" or "OR")
            || !condition.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException("条件必须是 {logic:AND/OR, items:[…]} 结构化条件。");

        var joiner = logic.GetString()!.ToUpperInvariant() switch
        {
            "AND" => " AND ",
            _ => " OR ",
        };
        var parts = new List<string>();
        var parameters = new List<EffectSqlParameter>();
        foreach (var item in items.EnumerateArray())
        {
            var fragment = CompileItem(item, resolveAlias, isSysssColumn, outerAlias);
            if (fragment.Sql.Length == 0)
                continue;
            parts.Add("(" + fragment.Sql + ")");
            parameters.AddRange(fragment.Parameters);
        }
        if (parts.Count == 0)
            return EffectSqlFragment.True;
        return new EffectSqlFragment(string.Join(joiner, parts), parameters);
    }

    private EffectSqlFragment CompileItem(
        JsonElement item,
        ScopeAliasResolver resolveAlias,
        IsSysssColumn isSysssColumn,
        string outerAlias)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
            throw new EffectConfigException("条件项缺少字符串 type。");

        switch (type.GetString()!.ToUpperInvariant())
        {
            case "FIELD-COMPARE":
                return CompileCompare(
                    Required(item, "left"), Required(item, "op"), item.TryGetProperty("right", out var r) ? r : default,
                    resolveAlias, includeType: false);
            case "VALUE-EQ":
            case "VALUE-NEQ":
            {
                var field = Required(item, "field");
                var alias = AliasFor(field, resolveAlias)
                    ?? throw new EffectConfigException($"条件字段 {field.GetProperty("field")} 的来源域不可用。");
                var parameter = NextParameter(ExtractValue(Required(item, "value")));
                return new EffectSqlFragment(
                    $"{alias}.{Identifier(field)} {(type.GetString()!.EndsWith("EQ", StringComparison.OrdinalIgnoreCase) ? "=" : "<>")} {parameter.Name}",
                    new[] { parameter });
            }
            case "NOT-EXISTS":
                var notExists = CompileNotExists(item, resolveAlias, isSysssColumn, outerAlias);
                // negate:true turns NOT EXISTS into EXISTS — used by master completion
                // reset rows ("there is an unfinished line") that mirror the legacy
                // deapprove branch of the finished-flag procedures.
                if (item.TryGetProperty("negate", out var negate)
                    && negate.ValueKind == JsonValueKind.True)
                {
                    return new EffectSqlFragment("EXISTS" + notExists.Sql[("NOT EXISTS").Length..], notExists.Parameters);
                }
                return notExists;
            case "SWITCH":
                return CompileSwitch(item, isSysssColumn);
            default:
                throw new EffectConfigException($"条件类型 '{type.GetString()}' 不在封闭条件类型集内。");
        }
    }

    /// <summary>
    /// A bare compare shape ({left, op, right} without a type wrapper), used inside
    /// not-exists and param-level conditions.
    /// </summary>
    public EffectSqlFragment CompileCompare(
        JsonElement left,
        JsonElement op,
        JsonElement right,
        ScopeAliasResolver resolveAlias,
        bool includeType = true)
    {
        if (includeType)
            throw new InvalidOperationException("internal caller misuse");
        var leftAlias = AliasFor(left, resolveAlias);
        var leftSql = left.TryGetProperty("value", out var leftValue)
            ? NextParameter(ExtractValue(leftValue)).Name
            : leftAlias is null
                ? throw new EffectConfigException("比较左项来源域不可用。")
                : CompareFieldOrTerms(left, leftAlias);

        string rightSql;
        var rightParameters = Array.Empty<EffectSqlParameter>();
        if (right.ValueKind == JsonValueKind.Object && right.TryGetProperty("value", out var rightValue))
        {
            var parameter = NextParameter(ExtractValue(rightValue));
            rightSql = parameter.Name;
            rightParameters = new[] { parameter };
        }
        else
        {
            var rightAlias = AliasFor(right, resolveAlias)
                ?? throw new EffectConfigException("比较右项来源域不可用。");
            rightSql = CompareFieldOrTerms(right, rightAlias);
        }
        var opElement = op;
        var operatorSql = SqlOp(opElement.ValueKind == JsonValueKind.String ? opElement.GetString()! : string.Empty);
        return new EffectSqlFragment($"{leftSql} {operatorSql} {rightSql}", rightParameters);
    }

    private EffectSqlFragment CompileNotExists(
        JsonElement item,
        ScopeAliasResolver resolveAlias,
        IsSysssColumn isSysssColumn,
        string outerAlias)
    {
        var targetTable = Required(item, "targetTable").GetString()!.Trim();
        if (!WorkbenchSql.Identifier.IsMatch(targetTable))
            throw new EffectConfigException($"not-exists 表名非法：'{targetTable}'。");
        var inner = item.TryGetProperty("condition", out var condition) ? condition : default;
        if (inner.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("not-exists 缺少 condition 比较结构。");
        var alias = "NX_" + TargetAlias(targetTable);
        EffectSqlFragment predicate;
        if (inner.TryGetProperty("type", out _))
        {
            var wrapped = JsonSerializer.SerializeToElement(new { logic = "AND", items = new[] { inner } });
            predicate = Compile(wrapped, (scope, table) => scope.Equals("TARGET", StringComparison.OrdinalIgnoreCase)
                ? alias
                : resolveAlias(scope, table), isSysssColumn, alias + "_INNER");
        }
        else
        {
            predicate = CompileCompare(
                Required(inner, "left"), Required(inner, "op"), inner.TryGetProperty("right", out var r) ? r : default,
                (scope, _) => scope.Equals("TARGET", StringComparison.OrdinalIgnoreCase) ? alias : null,
                includeType: false);
        }
        var parameters = new List<EffectSqlParameter>(predicate.Parameters);
        if (item.TryGetProperty("match", out var match) && match.ValueKind == JsonValueKind.Array)
        {
            var correlations = new List<string>();
            foreach (var pair in match.EnumerateArray())
            {
                var targetColumn = pair.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()!.Trim()
                    : null;
                var sourceField = pair.TryGetProperty("source", out var s)
                    && s.ValueKind == JsonValueKind.Object
                    && s.TryGetProperty("field", out var f)
                    && f.ValueKind == JsonValueKind.String
                        ? f.GetString()!.Trim()
                        : null;
                if (targetColumn is null || sourceField is null)
                    continue;
                correlations.Add($"{alias}.{Identifier(sourceField)} = {outerAlias}.{Identifier(targetColumn)}");
            }
            if (correlations.Count > 0)
                return new EffectSqlFragment(
                    $"NOT EXISTS (SELECT 1 FROM dbo.{Identifier(targetTable)} {alias} WHERE ({predicate.Sql}) AND {string.Join(" AND ", correlations)})",
                    parameters);
        }
        return new EffectSqlFragment(
            $"NOT EXISTS (SELECT 1 FROM dbo.{Identifier(targetTable)} {alias} WHERE ({predicate.Sql}))",
            parameters);
    }

    private EffectSqlFragment CompileSwitch(JsonElement item, IsSysssColumn isSysssColumn)
    {
        var key = Required(item, "key").GetString()!.Trim();
        if (!WorkbenchSql.Identifier.IsMatch(key) || !isSysssColumn(key))
            throw new EffectConfigException($"系统开关 '{key}' 不是 SYSSS 物理列。");
        var expected = item.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True;
        return new EffectSqlFragment(
            $"COALESCE((SELECT MAX(CAST({key} AS int)) FROM dbo.SYSSS WITH (NOLOCK)), 0) {(expected ? "=" : "<>")} 1",
            Array.Empty<EffectSqlParameter>());
    }

    /// <summary>
    /// Correlates the not-exists subquery back to the outer statement when the condition
    /// carries match pairs: the outer reference is always named OUTER by the caller.
    /// </summary>
    internal static string Identifier(JsonElement fieldElement) =>
        fieldElement.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.String
            ? Identifier(field.GetString()!)
            : throw new EffectConfigException("条件项缺少字符串 field。");

    internal static string Identifier(string name)
    {
        if (!WorkbenchSql.Identifier.IsMatch(name))
            throw new EffectConfigException($"标识符非法：'{name}'。");
        return "[" + name + "]";
    }

    internal string NextParameterName() => "@cp" + _paramIndex++;

    private static string? AliasFor(JsonElement field, ScopeAliasResolver resolveAlias)
    {
        var scope = field.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()!.Trim().ToUpperInvariant()
            : string.Empty;
        var table = field.TryGetProperty("table", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!.Trim()
            : null;
        return resolveAlias(scope, table);
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : throw new EffectConfigException($"条件项缺少 '{name}'。");

    private static object? ExtractValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new EffectConfigException("条件值仅支持标量（字符串/数字/布尔/null）。"),
    };

    private EffectSqlParameter NextParameter(object? value) => new(NextParameterName(), value);

    internal static string TargetAlias(string table) =>
        "T_" + new string(table.Where(char.IsLetterOrDigit).Take(6).ToArray());
}
