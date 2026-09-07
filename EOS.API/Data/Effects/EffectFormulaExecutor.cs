using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>
/// Formula effect executor: interprets MODULE_BUSINESS_ACTION_OP rows as parameterized
/// UPDATE statements inside the caller's transaction. Identifiers come exclusively from
/// configuration and are re-validated against the physical column whitelist before use;
/// values travel only as SQL parameters. Reverse semantics (DEAPPROVE): ACCUM and
/// DEACCUM are mutual inverses; other operators have no default reverse unless the
/// action declares a "recompute" reverse kind, which re-runs the same row.
/// </summary>
public sealed class EffectFormulaExecutor
{
    private readonly EffectConditionCompiler _conditions = new();
    private readonly EffectPhysicalColumns _columns = new();

    public async Task<int> ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectOpPlan op,
        EffectEvent executionEvent,
        JsonElement? reverseStruct,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        var effective = ResolveOpForEvent(op, executionEvent, reverseStruct);
        if (effective is null)
            return 0;

        var columns = await _columns.LoadAsync(connection, token, transaction);
        ValidateIdentifiers(effective, plan, columns);

        var (sql, parameters) = BuildUpdate(effective, plan, masterKeyValues);
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        try
        {
            return await command.ExecuteNonQueryAsync(token);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"公式行执行失败 OP_SEQ={op.OpSeq} table={op.TargetTable} field={op.TargetField}：{exception.Message}",
                exception);
        }
    }

    /// <summary>Returns the op adjusted for the running event, or null when the step is a no-op.</summary>
    internal static EffectOpPlan? ResolveOpForEvent(
        EffectOpPlan op,
        EffectEvent executionEvent,
        JsonElement? reverseStruct)
    {
        if (executionEvent != EffectEvent.Deapprove)
            return op;
        var kind = reverseStruct.HasValue
            && reverseStruct.Value.ValueKind == JsonValueKind.Object
            && reverseStruct.Value.TryGetProperty("kind", out var k)
            && k.ValueKind == JsonValueKind.String
                ? k.GetString()!.ToLowerInvariant()
                : null;
        if (kind == "recompute" || kind == "reverse-flow" || kind == "snapshot")
            return op; // recomputed from current facts / handled by a service handler
        if (kind == "no-reverse")
            return null;
        return op.OpCode.ToUpperInvariant() switch
        {
            "ACCUM" => op with { OpCode = "DEACCUM" },
            "DEACCUM" => op with { OpCode = "ACCUM" },
            _ => null, // ASSIGN family / APPEND_UNIQ / SET_WHEN leave no reverse by default
        };
    }

    internal (string Sql, IReadOnlyList<EffectSqlParameter> Parameters) BuildUpdate(
        EffectOpPlan op,
        ModuleEffectPlan plan,
        IReadOnlyList<string> masterKeyValues)
    {
        _plan = plan;
        _masterKeyValues = masterKeyValues;
        var targetAlias = "T";
        var parameters = new List<EffectSqlParameter>();
        var builder = new StringBuilder();

        var targetField = $"{targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)}";
        string assignment;
        switch (op.OpCode.ToUpperInvariant())
        {
            case "SET_WHEN":
                assignment = $"{targetField} = {BuildConstantParameter(op, parameters)}";
                break;
            case "APPEND_UNIQ":
                assignment = BuildAppendUniq(op, targetAlias, parameters);
                break;
            default:
                var value = BuildValue(op, targetAlias, parameters);
                assignment = op.OpCode.ToUpperInvariant() switch
                {
                    "ACCUM" => $"{targetField} = ISNULL({targetField}, 0) + ({value})",
                    "DEACCUM" => $"{targetField} = ISNULL({targetField}, 0) - ({value})",
                    "ASSIGN" => $"{targetField} = ({value})",
                    "ASSIGN_MAX" => $"{targetField} = CASE WHEN ({value}) > {targetField} OR {targetField} IS NULL THEN ({value}) ELSE {targetField} END",
                    "ASSIGN_MIN" => $"{targetField} = CASE WHEN ({value}) < {targetField} OR {targetField} IS NULL THEN ({value}) ELSE {targetField} END",
                    _ => throw new EffectConfigException($"公式行算子 '{op.OpCode}' 不在封闭算子集内。"),
                };
                break;
        }

        builder.Append("UPDATE ").Append(targetAlias).Append(" SET ").Append(assignment)
            .Append(" FROM dbo.").Append(EffectConditionCompiler.Identifier(op.TargetTable)).Append(' ').Append(targetAlias);

        var where = BuildMatchWhere(op, targetAlias, parameters);
        if (op.Condition is { } condition)
        {
            var fragment = _conditions.Compile(
                condition,
                (scope, table) => ResolveConditionAlias(scope, table, op, targetAlias),
                column => true, // switch columns validated inside the compiler
                targetAlias);
            where = where.Length == 0 ? fragment.Sql : where + " AND " + fragment.Sql;
            parameters.AddRange(fragment.Parameters);
        }
        var documentScope = BuildDocumentScopeFilter(op, targetAlias, parameters);
        if (where.Length == 0)
            where = documentScope;
        else if (documentScope.Length > 0)
            where = where + " AND " + documentScope;
        builder.Append(" WHERE ").Append(where.Length == 0 ? "1=1" : where);

        return (builder.ToString(), parameters);
    }

    /// <summary>Builds the scalar value expression for the op source.</summary>
    private string BuildValue(EffectOpPlan op, string targetAlias, List<EffectSqlParameter> parameters)
    {
        var source = op.Source;
        switch (source.Scope.ToUpperInvariant())
        {
            case "CONSTANT":
                return BuildConstantParameter(op, parameters);
            case "TARGET":
                return $"{targetAlias}.{EffectConditionCompiler.Identifier(source.Field!)}";
            case "MASTER":
                // Master rows are the single current document row: the value is the
                // master field of this document, located by its own primary key. Match
                // keys in the op only position the target rows (e.g. via detail rows)
                // and never constrain the master sub-query.
                return $"(SELECT M.{EffectConditionCompiler.Identifier(source.Field!)} FROM dbo."
                    + $"{EffectConditionCompiler.Identifier(_plan.MasterTable!)} M WHERE {AddMasterKeyParameters(parameters)})";
        }

        var (table, alias) = SourceTableFor(op);
        var innerExpression = BuildTermExpression(op, alias);
        var aggregation = op.SourceAgg?.ToUpperInvariant() switch
        {
            "MAX" => "MAX",
            "MIN" => "MIN",
            "DISTINCT" => "MAX", // single distinct pick per correlated group (e.g. depot-place append)
            _ => "SUM",
        };
        var correlation = BuildCorrelation(op, alias, targetAlias, parameters, subQuery: true);
        if (correlation.Length == 0)
            throw new EffectConfigException(
                $"公式行 OP_SEQ={op.OpSeq}：来源域 {source.Scope} 缺少定位键，禁止无条件读取 {table}。");
        var documentScope = BuildSourceDocumentScope(alias, parameters);
        return $"(SELECT {aggregation}({innerExpression}) FROM dbo.{EffectConditionCompiler.Identifier(table)} {alias} WHERE {correlation}{documentScope})";
    }

    private string BuildConstantParameter(EffectOpPlan op, List<EffectSqlParameter> parameters)
    {
        var constant = op.Source.Constant
            ?? throw new EffectConfigException($"公式行 OP_SEQ={op.OpSeq}：CONSTANT 来源缺少 sourceConstant。");
        if (constant.Equals("SYSDATETIME", StringComparison.OrdinalIgnoreCase))
        {
            // Reserved marker for the current database time; only the exact marker is
            // mapped to the built-in function, every other constant stays parameterized.
            return "SYSDATETIME()";
        }
        var name = _conditions.NextParameterName();
        parameters.Add(new EffectSqlParameter(name, ParseScalar(constant)));
        return name;
    }

    private string BuildAppendUniq(EffectOpPlan op, string targetAlias, List<EffectSqlParameter> parameters)
    {
        var field = $"{targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)}";
        // The appended value must be a scalar bound to this document: a bare D.column
        // has no source table in the UPDATE scope, so DETAIL/TABLE/MASTER sources go
        // through the same scalar-subquery builder as ordinary values (DISTINCT folds
        // to MAX, matching the legacy per-group max() append).
        var value = op.Source.Scope.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase)
            ? BuildConstantParameter(op, parameters)
            : BuildValue(op, targetAlias, parameters);
        return $"{field} = CASE WHEN COALESCE({field}, '') = '' THEN {value} "
            + $"WHEN CHARINDEX({value}, {field}) > 0 THEN {field} "
            + $"ELSE {field} + ',' + {value} END";
    }

    private ModuleEffectPlan _plan = null!;
    private IReadOnlyList<string> _masterKeyValues = Array.Empty<string>();

    private string AddMasterKeyParameters(List<EffectSqlParameter> parameters)
    {
        var keys = Math.Min(_plan.MasterPkOrder.Count, _masterKeyValues.Count);
        if (keys == 0)
            throw new EffectConfigException("公式行缺少主表主键值，禁止无单据范围执行。");
        var parts = new List<string>();
        for (var index = 0; index < keys; index++)
        {
            var name = _conditions.NextParameterName();
            parameters.Add(new EffectSqlParameter(name, _masterKeyValues[index]));
            parts.Add($"M.{EffectConditionCompiler.Identifier(_plan.MasterPkOrder[index])} = {name}");
        }
        return string.Join(" AND ", parts);
    }

    /// <summary>
    /// Restricts the statement to the current document: DETAIL-sourced ops locate target
    /// rows through this document's detail rows (master-joined); master-table targets are
    /// constrained by the key values directly. Without this the match correlation alone
    /// would touch rows of unrelated documents.
    /// </summary>
    private string BuildDocumentScopeFilter(EffectOpPlan op, string targetAlias, List<EffectSqlParameter> parameters)
    {
        var masterFilter = AddMasterKeyParameters(parameters);
        if (op.Match is { Count: > 0 }
            && op.Match.Any(item => item.Source.Scope.Equals("DETAIL", StringComparison.OrdinalIgnoreCase))
            && _plan.DetailTable is not null)
        {
            var masterJoin = string.Join(" AND ", _plan.MasterPkOrder.Select(pk =>
                $"D.{EffectConditionCompiler.Identifier(pk)} = M.{EffectConditionCompiler.Identifier(pk)}"));
            return $"EXISTS (SELECT 1 FROM dbo.{EffectConditionCompiler.Identifier(_plan.DetailTable)} D "
                + $"JOIN dbo.{EffectConditionCompiler.Identifier(_plan.MasterTable!)} M ON {masterJoin} WHERE {masterFilter})";
        }
        if (op.TargetTable.Equals(_plan.MasterTable, StringComparison.OrdinalIgnoreCase))
            return masterFilter.Replace("M.", targetAlias + ".");
        return string.Empty;
    }

    /// <summary>Document scope for scalar source subqueries (DETAIL/TABLE sources).</summary>
    private string BuildSourceDocumentScope(string alias, List<EffectSqlParameter> parameters)
    {
        var masterFilter = AddMasterKeyParameters(parameters);
        var join = string.Join(" AND ", _plan.MasterPkOrder.Select(pk =>
            $"{alias}.{EffectConditionCompiler.Identifier(pk)} = M.{EffectConditionCompiler.Identifier(pk)}"));
        return $" AND EXISTS (SELECT 1 FROM dbo.{EffectConditionCompiler.Identifier(_plan.MasterTable!)} M "
            + $"WHERE {join} AND {masterFilter})";
    }

    private (string Table, string Alias) SourceTableFor(EffectOpPlan op) => op.Source.Scope.ToUpperInvariant() switch
    {
        "MASTER" => (_plan.MasterTable!, "M"),
        "DETAIL" => (_plan.DetailTable!, "D"),
        "TABLE" => (op.Source.Table!, "S_" + EffectConditionCompiler.TargetAlias(op.Source.Table!)),
        _ => throw new EffectConfigException($"公式行 OP_SEQ={op.OpSeq}：来源域 '{op.Source.Scope}' 不支持标量取值。"),
    };

    private string BuildTermExpression(EffectOpPlan op, string alias)
    {
        if (op.Terms is { Count: > 0 })
        {
            var expression = string.Join(" ", op.Terms.Select((term, index) =>
                (index == 0 ? (term.Coef == -1 ? "-" : "") : (term.Coef == -1 ? "- " : "+ "))
                + $"ISNULL({alias}.{EffectConditionCompiler.Identifier(term.Field)}, 0)"));
            return op.Terms[0].Coef == -1 ? "(" + expression + ")" : expression;
        }
        if (op.Source.Field is { Length: > 0 })
            return $"ISNULL({alias}.{EffectConditionCompiler.Identifier(op.Source.Field)}, 0)";
        throw new EffectConfigException($"公式行 OP_SEQ={op.OpSeq}：缺少来源字段或加减项。");
    }

    /// <summary>Correlation predicates linking the source rows to the target rows via match keys.</summary>
    private string BuildCorrelation(
        EffectOpPlan op,
        string sourceAlias,
        string targetAlias,
        List<EffectSqlParameter> parameters,
        bool subQuery)
    {
        if (op.Match is not { Count: > 0 })
            return string.Empty;
        var parts = new List<string>();
        foreach (var item in op.Match)
        {
            if (item.Source.Scope.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase))
            {
                var name = _conditions.NextParameterName();
                parameters.Add(new EffectSqlParameter(name, ParseScalar(item.Source.Constant ?? string.Empty)));
                parts.Add($"{sourceAlias}.{EffectConditionCompiler.Identifier(item.Source.Field!)} = {name}");
                continue;
            }
            var sourceSide = item.Source.Scope.Equals("TARGET", StringComparison.OrdinalIgnoreCase)
                ? $"{targetAlias}.{EffectConditionCompiler.Identifier(item.Source.Field!)}"
                : $"{sourceAlias}.{EffectConditionCompiler.Identifier(item.Source.Field!)}";
            parts.Add($"{sourceSide} = {targetAlias}.{EffectConditionCompiler.Identifier(item.TargetColumn)}");
        }
        return string.Join(" AND ", parts);
    }

    private string BuildMatchWhere(EffectOpPlan op, string targetAlias, List<EffectSqlParameter> parameters)
    {
        if (op.Match is not { Count: > 0 })
            return string.Empty;
        var parts = new List<string>();
        foreach (var group in op.Match
                     .Where(item => item.Source.Scope.Equals("DETAIL", StringComparison.OrdinalIgnoreCase)
                         || item.Source.Scope.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(item => item.Source.Scope.Equals("DETAIL", StringComparison.OrdinalIgnoreCase)
                         ? (_plan.DetailTable!, "D")
                         : (item.Source.Table!, "S_" + EffectConditionCompiler.TargetAlias(item.Source.Table!))))
        {
            var (table, alias) = group.Key;
            parts.Add(
                $"EXISTS (SELECT 1 FROM dbo.{EffectConditionCompiler.Identifier(table)} {alias} "
                + $"WHERE {string.Join(" AND ", group.Select(item =>
                    $"{alias}.{EffectConditionCompiler.Identifier(item.Source.Field!)} = {targetAlias}.{EffectConditionCompiler.Identifier(item.TargetColumn)}"))})");
        }
        foreach (var item in op.Match.Where(item =>
                     !item.Source.Scope.Equals("DETAIL", StringComparison.OrdinalIgnoreCase)
                     && !item.Source.Scope.Equals("TABLE", StringComparison.OrdinalIgnoreCase)))
        {
            if (item.Source.Scope.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase))
            {
                var name = _conditions.NextParameterName();
                parameters.Add(new EffectSqlParameter(name, ParseScalar(item.Source.Constant ?? string.Empty)));
                parts.Add($"{targetAlias}.{EffectConditionCompiler.Identifier(item.TargetColumn)} = {name}");
                continue;
            }
            // MASTER / TARGET: correlated directly to the outer statement.
            var sourceSide = item.Source.Scope.Equals("MASTER", StringComparison.OrdinalIgnoreCase)
                ? $"(SELECT M.{EffectConditionCompiler.Identifier(item.Source.Field!)} FROM dbo.{EffectConditionCompiler.Identifier(_plan.MasterTable!)} M WHERE {AddMasterKeyParameters(parameters)})"
                : $"{targetAlias}.{EffectConditionCompiler.Identifier(item.Source.Field!)}";
            parts.Add($"{targetAlias}.{EffectConditionCompiler.Identifier(item.TargetColumn)} = {sourceSide}");
        }
        return string.Join(" AND ", parts);
    }

    private string ResolveConditionAlias(string scope, string? table, EffectOpPlan op, string targetAlias) =>
        scope.ToUpperInvariant() switch
        {
            "TARGET" => targetAlias,
            "MASTER" => "M",
            "DETAIL" => "D",
            "TABLE" => "S_" + EffectConditionCompiler.TargetAlias(table ?? string.Empty),
            _ => throw new EffectConfigException($"条件来源域 '{scope}' 不可用。"),
        };

    private static object? ParseScalar(string text) =>
        long.TryParse(text, out var l) ? l
        : decimal.TryParse(text, out var d) ? d
        : bool.TryParse(text, out var b) ? b
        : text;

    private static void ValidateIdentifiers(EffectOpPlan op, ModuleEffectPlan plan, ISet<string> columns)
    {
        ValidateColumn(op.TargetTable, op.TargetField, columns, $"OP_SEQ={op.OpSeq} 目标");
        var source = op.Source;
        if (source.Field is { Length: > 0 })
        {
            var table = source.Scope.ToUpperInvariant() switch
            {
                "MASTER" => plan.MasterTable,
                "DETAIL" => plan.DetailTable,
                "TABLE" => source.Table,
                "TARGET" => op.TargetTable,
                _ => null,
            };
            if (table is not null)
                ValidateColumn(table, source.Field, columns, $"OP_SEQ={op.OpSeq} 来源");
        }
        if (op.Terms is { Count: > 0 })
        {
            var table = source.Scope.ToUpperInvariant() switch
            {
                "MASTER" => plan.MasterTable,
                "DETAIL" => plan.DetailTable,
                "TABLE" => source.Table,
                _ => null,
            };
            if (table is not null)
                foreach (var term in op.Terms)
                    ValidateColumn(table, term.Field, columns, $"OP_SEQ={op.OpSeq} 加减项");
        }
    }

    private static void ValidateColumn(string table, string field, ISet<string> columns, string where)
    {
        if (!WorkbenchSql.Identifier.IsMatch(table) || !WorkbenchSql.Identifier.IsMatch(field))
            throw new EffectConfigException($"{where}标识符非法：{table}.{field}。");
        if (!columns.Contains(table + "." + field))
            throw new EffectConfigException($"{where}表/字段不存在：{table}.{field}。");
    }

}
