using System.Text.RegularExpressions;

namespace EOS.API.Features.Assistant.Metrics;

public enum MetricDimensionTarget
{
    Source,
    Master,
}

public enum MetricDimensionOperator
{
    Equals,
    Range,
}

/// <summary>
/// A user-supplied dimension filter. Values are bound as parameters; the column
/// must have passed validator whitelisting (declared in DIMENSION_KEYS and
/// physically present on the routed table).
/// </summary>
public sealed record MetricDimensionFilter(
    string Column,
    MetricDimensionTarget Target,
    MetricDimensionOperator Operator,
    IReadOnlyList<object> Values);

public sealed record MetricPlanParameter(string Name, object Value);

/// <summary>
/// A compiled, fully parameterized metric query. Identifiers come exclusively
/// from validator-whitelisted nodes; business values are always parameters.
/// </summary>
public sealed record MetricPlan(string Sql, IReadOnlyList<MetricPlanParameter> Parameters);

/// <summary>
/// Compiles a validated metric AST + row filter + dimension filters + master-scope
/// predicate into a single parameterized scalar query. Source-table predicates are
/// inlined; master-table predicates (scope, master dimensions, master row filter)
/// are pushed through an EXISTS over the master joined by its primary-key columns —
/// the same derivation the workbench uses for detail queries. No scope predicate
/// without a master join is ever rendered (fail-closed).
/// </summary>
public static class MetricPlanCompiler
{
    private static readonly Regex ScopeParameterName = new("@df(\\d+)", RegexOptions.Compiled);

    public static MetricPlan Compile(
        MetricExpression expression,
        string sourceTable,
        MetricRowFilterPlan? rowFilter,
        IReadOnlyList<MetricDimensionFilter> dimensions,
        string? masterTable,
        IReadOnlyList<string> masterJoinColumns,
        string? scopePredicate,
        IReadOnlyList<object>? scopeParameterValues)
    {
        var parameters = new List<MetricPlanParameter>();
        var next = 0;
        string TakeName(string prefix)
        {
            var name = $"{prefix}{next}";
            next++;
            return name;
        }

        var expressionSql = Render(expression, parameters, TakeName);

        var inlinePredicates = new List<string>();
        var masterPredicates = new List<string>();
        var sameAsMaster = masterTable is null
            || string.Equals(sourceTable, masterTable, StringComparison.OrdinalIgnoreCase);

        foreach (var dimension in dimensions)
        {
            if (dimension.Operator == MetricDimensionOperator.Equals)
            {
                var name = TakeName("@dim");
                parameters.Add(new MetricPlanParameter(name, dimension.Values[0]));
                var predicate = $"[{dimension.Column}]={name}";
                (dimension.Target == MetricDimensionTarget.Source ? inlinePredicates : masterPredicates).Add(predicate);
            }
            else
            {
                var fromName = TakeName("@dim");
                var toName = TakeName("@dim");
                parameters.Add(new MetricPlanParameter(fromName, dimension.Values[0]));
                parameters.Add(new MetricPlanParameter(toName, dimension.Values[1]));
                var predicate = $"([{dimension.Column}]>={fromName} AND [{dimension.Column}]<={toName})";
                (dimension.Target == MetricDimensionTarget.Source ? inlinePredicates : masterPredicates).Add(predicate);
            }
        }

        if (rowFilter is not null)
        {
            foreach (var condition in rowFilter.Conditions)
            {
                var name = TakeName("@rf");
                parameters.Add(new MetricPlanParameter(name, condition.Value));
                var predicate = $"[{condition.Column}]{condition.Operator}{name}";
                if (string.Equals(rowFilter.Table, sourceTable, StringComparison.OrdinalIgnoreCase))
                {
                    inlinePredicates.Add(predicate);
                }
                else
                {
                    masterPredicates.Add(predicate);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(scopePredicate))
        {
            var offset = parameters.Count;
            var renamed = ScopeParameterName.Replace(scopePredicate,
                match => $"@scope{offset + int.Parse(match.Groups[1].Value)}");
            if (scopeParameterValues is not null)
            {
                for (var i = 0; i < scopeParameterValues.Count; i++)
                {
                    parameters.Add(new MetricPlanParameter($"@scope{offset + i}", scopeParameterValues[i]));
                }
            }
            (sameAsMaster ? inlinePredicates : masterPredicates).Add($"({renamed})");
        }

        var needsMasterExists = !sameAsMaster && masterPredicates.Count > 0;
        if (needsMasterExists && masterJoinColumns.Count == 0)
        {
            throw new InvalidOperationException("主表范围谓词缺少有效主表关联键，拒绝编译。");
        }

        var whereParts = new List<string>(inlinePredicates);
        if (needsMasterExists)
        {
            var join = string.Join(" AND ",
                masterJoinColumns.Select(pk => $"[{pk}]=dbo.[{sourceTable}].[{pk}]"));
            whereParts.Add(
                $"EXISTS (SELECT 1 FROM dbo.[{masterTable}] WITH (NOLOCK) WHERE {join} AND {string.Join(" AND ", masterPredicates)})");
        }
        else if (masterPredicates.Count > 0)
        {
            // Master predicates without a master table can only mean the validator
            // admitted a filter table outside {source, master} — refuse to render.
            throw new InvalidOperationException("存在无法路由的主表谓词，拒绝编译。");
        }

        var where = whereParts.Count > 0 ? " WHERE " + string.Join(" AND ", whereParts) : "";
        var sql = $"SELECT {expressionSql} FROM dbo.[{sourceTable}] WITH (NOLOCK){where};";
        return new MetricPlan(sql, parameters);
    }

    private static string Render(
        MetricExpression expression, List<MetricPlanParameter> parameters, Func<string, string> takeName) =>
        expression switch
        {
            MetricColumn column => $"[{column.Name}]",
            MetricLiteral literal => RenderLiteral(literal, parameters, takeName),
            MetricAggregate { Distinct: true } aggregate =>
                $"{aggregate.Function}(DISTINCT {Render(aggregate.Argument, parameters, takeName)})",
            MetricAggregate aggregate =>
                $"{aggregate.Function}({Render(aggregate.Argument, parameters, takeName)})",
            MetricNullIf nullIf =>
                $"NULLIF({Render(nullIf.Left, parameters, takeName)}, {Render(nullIf.Right, parameters, takeName)})",
            MetricBinary binary =>
                $"({Render(binary.Left, parameters, takeName)} {binary.Operator} {Render(binary.Right, parameters, takeName)})",
            _ => throw new InvalidOperationException("口径表达式包含无法渲染的节点。"),
        };

    private static string RenderLiteral(
        MetricLiteral literal, List<MetricPlanParameter> parameters, Func<string, string> takeName)
    {
        var name = takeName("@m");
        parameters.Add(new MetricPlanParameter(name, literal.Value));
        return name;
    }
}
