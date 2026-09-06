using System.Data;
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

    public async Task<int> ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectOpPlan op,
        EffectEvent executionEvent,
        JsonElement? reverseStruct,
        CancellationToken token)
    {
        var effective = ResolveOpForEvent(op, executionEvent, reverseStruct);
        if (effective is null)
            return 0;

        var columns = await LoadPhysicalColumnsAsync(connection, token);
        ValidateIdentifiers(effective, plan, columns);

        var (sql, parameters) = BuildUpdate(effective, plan);
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(token);
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
        ModuleEffectPlan plan)
    {
        _plan = plan;
        var targetAlias = "T";
        var parameters = new List<EffectSqlParameter>();
        var builder = new StringBuilder();

        var assignment = op.OpCode.ToUpperInvariant() switch
        {
            "ACCUM" => $"{targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} = ISNULL({targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)}, 0) + ({BuildValue(op, targetAlias, parameters)})",
            "DEACCUM" => $"{targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} = ISNULL({targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)}, 0) - ({BuildValue(op, targetAlias, parameters)})",
            "ASSIGN" => $"{targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} = ({BuildValue(op, targetAlias, parameters)})",
            "ASSIGN_MAX" => $"{targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} = CASE WHEN ({BuildValue(op, targetAlias, parameters)}) > {targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} OR {targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} IS NULL THEN ({PopLastValue(op, targetAlias, parameters)}) ELSE {targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} END",
            "ASSIGN_MIN" => $"{targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} = CASE WHEN ({BuildValue(op, targetAlias, parameters)}) < {targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} OR {targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} IS NULL THEN ({PopLastValue(op, targetAlias, parameters)}) ELSE {targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} END",
            "APPEND_UNIQ" => BuildAppendUniq(op, targetAlias, parameters),
            "SET_WHEN" => $"{targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} = {BuildConstantParameter(op, parameters)}",
            _ => throw new EffectConfigException($"公式行算子 '{op.OpCode}' 不在封闭算子集内。"),
        };

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
        builder.Append(" WHERE ").Append(where.Length == 0 ? "1=1" : where);

        return (builder.ToString(), parameters);
    }

    private static string PopLastValue(EffectOpPlan op, string targetAlias, List<EffectSqlParameter> parameters) =>
        parameters[^1].Name;

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
        }

        var (table, alias) = SourceTableFor(op);
        var innerExpression = BuildTermExpression(op, alias);
        var aggregation = op.SourceAgg?.ToUpperInvariant() switch
        {
            "MAX" => "MAX",
            "MIN" => "MIN",
            "DISTINCT" => "MAX", // a distinct string pick is expressed as MAX until a real use appears
            _ => "SUM",
        };
        var correlation = BuildCorrelation(op, alias, targetAlias, parameters, subQuery: true);
        if (correlation.Length == 0)
            throw new EffectConfigException(
                $"公式行 OP_SEQ={op.OpSeq}：来源域 {source.Scope} 缺少定位键，禁止无条件读取 {table}。");
        return $"(SELECT {aggregation}({innerExpression}) FROM dbo.{EffectConditionCompiler.Identifier(table)} {alias} WHERE {correlation})";
    }

    private string BuildConstantParameter(EffectOpPlan op, List<EffectSqlParameter> parameters)
    {
        var constant = op.Source.Constant
            ?? throw new EffectConfigException($"公式行 OP_SEQ={op.OpSeq}：CONSTANT 来源缺少 sourceConstant。");
        var name = _conditions.NextParameterName();
        parameters.Add(new EffectSqlParameter(name, ParseScalar(constant)));
        return name;
    }

    private string BuildAppendUniq(EffectOpPlan op, string targetAlias, List<EffectSqlParameter> parameters)
    {
        var field = $"{targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)}";
        var value = op.Source.Scope.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase)
            ? BuildConstantParameter(op, parameters)
            : $"{BuildSourceAlias(op)}.{EffectConditionCompiler.Identifier(op.Source.Field!)}";
        return $"{field} = CASE WHEN COALESCE({field}, '') = '' THEN {value} "
            + $"WHEN CHARINDEX({value}, {field}) > 0 THEN {field} "
            + $"ELSE {field} + ',' + {value} END";
    }

    private static string BuildSourceAlias(EffectOpPlan op) => op.Source.Scope.ToUpperInvariant() switch
    {
        "MASTER" => "M",
        "DETAIL" => "D",
        "TARGET" => "T",
        _ => "S_" + EffectConditionCompiler.TargetAlias(op.Source.Table ?? string.Empty),
    };

    private ModuleEffectPlan _plan = null!;

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
        foreach (var item in op.Match)
        {
            if (item.Source.Scope.Equals("DETAIL", StringComparison.OrdinalIgnoreCase)
                || (item.Source.Scope.Equals("TABLE", StringComparison.OrdinalIgnoreCase)))
            {
                var table = item.Source.Scope.Equals("DETAIL", StringComparison.OrdinalIgnoreCase)
                    ? _plan.DetailTable!
                    : item.Source.Table!;
                var alias = item.Source.Scope.Equals("DETAIL", StringComparison.OrdinalIgnoreCase)
                    ? "D"
                    : "S_" + EffectConditionCompiler.TargetAlias(table);
                parts.Add(
                    $"EXISTS (SELECT 1 FROM dbo.{EffectConditionCompiler.Identifier(table)} {alias} "
                    + $"WHERE {alias}.{EffectConditionCompiler.Identifier(item.Source.Field!)} = {targetAlias}.{EffectConditionCompiler.Identifier(item.TargetColumn)})");
                continue;
            }
            if (item.Source.Scope.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase))
            {
                var name = _conditions.NextParameterName();
                parameters.Add(new EffectSqlParameter(name, ParseScalar(item.Source.Constant ?? string.Empty)));
                parts.Add($"{targetAlias}.{EffectConditionCompiler.Identifier(item.TargetColumn)} = {name}");
                continue;
            }
            // MASTER / TARGET: correlated directly to the outer statement.
            var sourceSide = item.Source.Scope.Equals("MASTER", StringComparison.OrdinalIgnoreCase)
                ? $"(SELECT M.{EffectConditionCompiler.Identifier(item.Source.Field!)} FROM dbo.{EffectConditionCompiler.Identifier(_plan.MasterTable!)} M)"
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

    private static async Task<ISet<string>> LoadPhysicalColumnsAsync(SqlConnection connection, CancellationToken token)
    {
        const string sql = """
            SELECT o.name,c.name
            FROM sys.objects o
            JOIN sys.columns c ON c.object_id=o.object_id
            WHERE o.type IN ('U','V') AND SCHEMA_NAME(o.schema_id)=N'dbo';
            """;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
            await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(reader.GetString(0) + "." + reader.GetString(1));
        if (!wasOpen)
            await connection.CloseAsync();
        return result;
    }
}
