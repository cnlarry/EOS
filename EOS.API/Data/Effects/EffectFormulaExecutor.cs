using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>
/// Formula effect executor: interprets MODULE_BUSINESS_ACTION_OP rows as parameterized
/// UPDATE statements inside the caller's transaction. Identifiers come exclusively from
/// configuration and are re-validated against the physical column whitelist before use;
/// values travel only as SQL parameters. Reverse semantics (DEAPPROVE): ACCUM and
/// DEACCUM are mutual inverses; clear-finish / clear-refs / clear-refs-unfinish unset
/// the placed marker or reference column; other operators have no default reverse unless
/// the action declares a "recompute" reverse kind, which re-runs the same row.
/// </summary>
public sealed class EffectFormulaExecutor
{
    private const string TargetAlias = "T";

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
        CancellationToken token,
        EffectSimulationProbe? probe = null)
    {
        var effective = ResolveOpForEvent(op, executionEvent, reverseStruct);
        if (effective is null)
        {
            probe?.Add(new EffectOpTrace(op.OpSeq, op.TargetTable, op.TargetField, op.OpCode, 0, []));
            return 0;
        }

        var columns = await _columns.LoadAsync(connection, token, transaction);
        ValidateIdentifiers(effective, plan, columns);

        var (sql, parameters) = BuildUpdate(effective, plan, masterKeyValues);
        if (probe is null)
        {
            return await RunUpdateAsync(connection, transaction, sql, parameters, op, token);
        }

        // Both reads happen inside the caller's transaction: the "after" snapshot sees the
        // uncommitted write, which is exactly what a simulation needs to report.
        var identity = await ResolveIdentityColumnsAsync(connection, transaction, op, token);
        var (selectSql, selectParameters) = BuildRowSelect(
            effective, plan, masterKeyValues, identity, EffectSimulationProbe.MaxRowsPerOp);
        var before = await SnapshotRowsAsync(connection, transaction, selectSql, selectParameters, identity, token);
        var rows = await RunUpdateAsync(connection, transaction, sql, parameters, op, token);
        var after = await SnapshotRowsAsync(connection, transaction, selectSql, selectParameters, identity, token);
        probe.Add(new EffectOpTrace(
            op.OpSeq, op.TargetTable, op.TargetField, effective.OpCode, rows,
            DiffRows(before, after, op.TargetField)));
        return rows;
    }

    private static async Task<int> RunUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        IReadOnlyList<EffectSqlParameter> parameters,
        EffectOpPlan op,
        CancellationToken token)
    {
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

    /// <summary>
    /// Columns that identify a target row in the report: the target table's primary key when
    /// it has one, otherwise the formula row's own locating keys. Without either, rows fall
    /// back to a positional marker rather than pretending to be identifiable.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResolveIdentityColumnsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        EffectOpPlan op,
        CancellationToken token)
    {
        var primaryKeys = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, transaction, op.TargetTable, token);
        if (primaryKeys.Count > 0)
        {
            return primaryKeys;
        }
        var locating = op.Match
            ?.Select(item => item.TargetColumn)
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return locating is { Count: > 0 } ? locating : [];
    }

    private static async Task<List<EffectSnapshotRow>> SnapshotRowsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        IReadOnlyList<EffectSqlParameter> parameters,
        IReadOnlyList<string> identityColumns,
        CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var rows = new List<EffectSnapshotRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var parts = new List<string>();
            for (var index = 0; index < identityColumns.Count; index++)
            {
                parts.Add($"{identityColumns[index]}={FormatValue(reader, index)}");
            }
            var value = FormatValue(reader, identityColumns.Count);
            rows.Add(new EffectSnapshotRow(
                parts.Count == 0 ? NoIdentity : string.Join(", ", parts), value));
        }
        return rows;
    }

    private static string? FormatValue(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal).ToString();

    /// <summary>
    /// Pairs the two snapshots by position: both are read with the same predicate and the same
    /// ordering, so row i before the write is row i after it. Only rows whose value actually
    /// moved are reported — an unchanged row is noise in a report about "what will happen".
    /// </summary>
    private static IReadOnlyList<EffectRowChange> DiffRows(
        List<EffectSnapshotRow> before,
        List<EffectSnapshotRow> after,
        string targetField)
    {
        var changes = new List<EffectRowChange>();
        var count = Math.Max(before.Count, after.Count);
        for (var index = 0; index < count; index++)
        {
            var beforeRow = index < before.Count ? before[index] : null;
            var afterRow = index < after.Count ? after[index] : null;
            if (beforeRow is not null && afterRow is not null
                && string.Equals(beforeRow.Value, afterRow.Value, StringComparison.Ordinal))
            {
                continue;
            }
            changes.Add(new EffectRowChange(
                beforeRow?.Identity ?? afterRow?.Identity ?? NoIdentity,
                [new EffectColumnChange(targetField, beforeRow?.Value, afterRow?.Value)]));
        }
        return changes;
    }

    private sealed record EffectSnapshotRow(string Identity, string? Value);

    private const string NoIdentity = "<无标识>";

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
        if (kind is "clear-finish" or "clear-refs" or "clear-refs-unfinish")
            return ClearOp(op, kind);
        if (kind is "clear-on-deapprove")
            // Restores dates/quantities captured elsewhere; a single row cannot express
            // that, and skipping silently would leave the document half-reversed: refuse.
            throw new EffectConfigException(
                $"公式行的反向 kind '{kind}' 无法由单条公式行表达，请改用 DEAPPROVE 动作承载。");
        return op.OpCode.ToUpperInvariant() switch
        {
            "ACCUM" => op with { OpCode = "DEACCUM" },
            "DEACCUM" => op with { OpCode = "ACCUM" },
            _ => null, // ASSIGN family / APPEND_UNIQ / SET_WHEN leave no reverse by default
        };
    }

    /// <summary>
    /// Reverse of a placement row for the clear kinds. The approve direction places a
    /// marker or a copied value into the target column; the reverse puts that column
    /// back to "unset" with one assignment, mirroring the placement vocabulary:
    /// clear-finish maps a numeric marker to 0, the SYSDATETIME marker to NULL, any
    /// other text marker to the empty string and a copied value to NULL; clear-refs
    /// resets SERIAL-like columns to 0 and empties the remaining reference columns;
    /// clear-refs-unfinish does the reference clearing and re-states the finish triple
    /// the approve direction stamps (FINISHED_TAG 0, FINISHED_PERSON 'SYSTEM',
    /// FINISHED_DATE now). Accumulation rows keep their own reverse (ACCUM/DEACCUM
    /// swap) because the clear kinds never apply to them.
    /// </summary>
    private static EffectOpPlan? ClearOp(EffectOpPlan op, string kind) =>
        op.OpCode.ToUpperInvariant() switch
        {
            "ACCUM" => op with { OpCode = "DEACCUM" },
            "DEACCUM" => op with { OpCode = "ACCUM" },
            "ASSIGN" or "ASSIGN_MAX" or "ASSIGN_MIN" or "SET_WHEN" or "APPEND" or "APPEND_UNIQ" =>
                op with
                {
                    OpCode = "SET_WHEN",
                    Source = new EffectSourceRef("CONSTANT", null, null, ClearValue(op, kind)),
                },
            _ => null,
        };

    /// <summary>Value one reverse assignment writes for the clear kinds.</summary>
    private static string ClearValue(EffectOpPlan op, string kind) => kind switch
    {
        "clear-refs" => IsSerialColumn(op.TargetField) ? "0" : string.Empty,
        "clear-refs-unfinish" => op.TargetField.ToUpperInvariant() switch
        {
            "FINISHED_TAG" => "0",
            "FINISHED_PERSON" => "SYSTEM",
            "FINISHED_DATE" => "SYSDATETIME",
            _ => IsSerialColumn(op.TargetField) ? "0" : string.Empty,
        },
        _ => ClearFinishValue(op.Source),
    };

    /// <summary>Value a clear-finish reverse writes: unset marker of the forward value.</summary>
    private static string ClearFinishValue(EffectSourceRef source)
    {
        if (!source.Scope.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase) || source.Constant is null)
            return "NULL"; // a copied value is unset rather than recomputed
        var constant = source.Constant;
        if (constant.Equals("SYSDATETIME", StringComparison.OrdinalIgnoreCase))
            return "NULL";
        if (decimal.TryParse(constant, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return "0";
        return string.Empty;
    }

    private static bool IsSerialColumn(string column) =>
        column.Contains("SERIAL", StringComparison.OrdinalIgnoreCase);

    internal (string Sql, IReadOnlyList<EffectSqlParameter> Parameters) BuildUpdate(
        EffectOpPlan op,
        ModuleEffectPlan plan,
        IReadOnlyList<string> masterKeyValues)
    {
        var parts = BuildStatement(op, plan, masterKeyValues);
        return ($"UPDATE {TargetAlias} SET {parts.Assignment} FROM {parts.From} WHERE {parts.Where}", parts.Parameters);
    }

    /// <summary>
    /// The row scope of the update built by <see cref="BuildUpdate"/>, rendered as a SELECT of
    /// the identity columns plus the target value. The snapshot and the write share one
    /// statement builder on purpose: a second predicate written by hand would silently drift
    /// from the rows the update actually touches.
    /// </summary>
    internal (string Sql, IReadOnlyList<EffectSqlParameter> Parameters) BuildRowSelect(
        EffectOpPlan op,
        ModuleEffectPlan plan,
        IReadOnlyList<string> masterKeyValues,
        IReadOnlyList<string> identityColumns,
        int maxRows)
    {
        var parts = BuildStatement(op, plan, masterKeyValues);
        var projections = identityColumns
            .Select(column => $"CAST({TargetAlias}.{EffectConditionCompiler.Identifier(column)} AS nvarchar(200))")
            .ToList();
        projections.Add($"CAST({TargetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)} AS nvarchar(400))");
        var orderBy = identityColumns.Count == 0
            ? string.Empty
            : " ORDER BY " + string.Join(',', Enumerable.Range(1, identityColumns.Count));
        var sql = $"SELECT TOP (@__limit) {string.Join(',', projections)} FROM {parts.From} WHERE {parts.Where}{orderBy};";
        var parameters = parts.Parameters.ToList();
        parameters.Add(new EffectSqlParameter("@__limit", Math.Clamp(maxRows, 1, 200)));
        return (sql, parameters);
    }

    /// <summary>The shared statement builder: assignment, FROM (with the optional master join)
    /// and the locating WHERE. Both the update and the simulation snapshot come from here.</summary>
    private EffectStatementParts BuildStatement(
        EffectOpPlan op,
        ModuleEffectPlan plan,
        IReadOnlyList<string> masterKeyValues)
    {
        _plan = plan;
        _masterKeyValues = masterKeyValues;
        var targetAlias = TargetAlias;
        var parameters = new List<EffectSqlParameter>();

        var targetField = $"{targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)}";
        string assignment;
        switch (op.OpCode.ToUpperInvariant())
        {
            case "SET_WHEN":
                assignment = $"{targetField} = {BuildConstantParameter(op, parameters)}";
                break;
            case "APPEND":
            case "APPEND_UNIQ":
                assignment = BuildAppend(op, targetAlias, parameters,
                    op.OpCode.Equals("APPEND_UNIQ", StringComparison.OrdinalIgnoreCase));
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

        var from = new StringBuilder("dbo.").Append(EffectConditionCompiler.Identifier(op.TargetTable))
            .Append(' ').Append(targetAlias);

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
            // A condition referencing the document master (e.g. BACK_CODE on the back
            // document) needs the master alias bound in this statement's FROM. The master
            // is a single row by its own primary key, so the JOIN never multiplies rows.
            if (ConditionReferencesScope(condition, "MASTER"))
            {
                from.Append(" JOIN dbo.").Append(EffectConditionCompiler.Identifier(_plan.MasterTable!))
                    .Append(" M ON ").Append(AddMasterKeyParameters(parameters));
            }
        }
        var documentScope = BuildDocumentScopeFilter(op, targetAlias, parameters);
        if (where.Length == 0)
            where = documentScope;
        else if (documentScope.Length > 0)
            where = where + " AND " + documentScope;
        // Fail closed on an unscoped statement: a formula row whose target is not the
        // document master and that carries neither locating keys nor a condition would
        // otherwise update every row of the target table.
        if (where.Length == 0)
            throw new EffectConfigException(
                $"公式行 OP_SEQ={op.OpSeq}：目标表 {op.TargetTable} 既无定位键也无条件，禁止无条件更新（拒绝全表更新）。");

        return new EffectStatementParts(assignment, from.ToString(), where, parameters);
    }

    /// <summary>The pieces an effect statement is assembled from; shared by the update and the
    /// simulation snapshot so both address exactly the same rows.</summary>
    private sealed record EffectStatementParts(
        string Assignment,
        string From,
        string Where,
        IReadOnlyList<EffectSqlParameter> Parameters);

    /// <summary>Builds the scalar value expression for the op source.</summary>
    private string BuildValue(
        EffectOpPlan op,
        string targetAlias,
        List<EffectSqlParameter> parameters,
        string? aggregateOverride = null)
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
        // PICK takes the value of the single row the locating keys correlate to (one row
        // per key by construction, e.g. a document line referenced by its serial number)
        // and keeps NULL as NULL; every other aggregate is a quantity projection and
        // therefore defaults a missing value to 0.
        var pick = aggregateOverride is null
            && op.SourceAgg is { } declared
            && declared.Equals("PICK", StringComparison.OrdinalIgnoreCase);
        var innerExpression = BuildTermExpression(op, alias, pick);
        // Append sources are single correlated rows (1:1 by line number), so a text
        // column must not be SUMmed; the append path folds with MAX instead.
        var aggregation = aggregateOverride ?? op.SourceAgg?.ToUpperInvariant() switch
        {
            "MAX" => "MAX",
            "MIN" => "MIN",
            "DISTINCT" => "MAX", // single distinct pick per correlated group (e.g. depot-place append)
            "PICK" => "MAX",     // single pick per correlated group, value taken as-is
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

    private string BuildAppend(EffectOpPlan op, string targetAlias, List<EffectSqlParameter> parameters, bool dedupe)
    {
        var field = $"{targetAlias}.{EffectConditionCompiler.Identifier(op.TargetField)}";
        // The appended value must be a scalar bound to this document: a bare D.column
        // has no source table in the UPDATE scope, so DETAIL/TABLE/MASTER sources go
        // through the same scalar-subquery builder as ordinary values (DISTINCT folds
        // to MAX, matching the legacy per-group max() append).
        var value = op.Source.Scope.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase)
            ? BuildConstantParameter(op, parameters)
            : BuildValue(op, targetAlias, parameters, aggregateOverride: "MAX");
        return dedupe
            ? $"{field} = CASE WHEN COALESCE({field}, '') = '' THEN {value} "
                + $"WHEN CHARINDEX({value}, {field}) > 0 THEN {field} "
                + $"ELSE {field} + ',' + {value} END"
            : $"{field} = CASE WHEN {field} IS NULL OR LTRIM(RTRIM({field})) = '' THEN {value} "
                + $"ELSE RTRIM({field}) + {value} END";
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
        "TABLE" when string.IsNullOrWhiteSpace(op.Source.Table) =>
            throw new EffectConfigException($"公式行 OP_SEQ={op.OpSeq}：TABLE 来源缺少 SOURCE_TABLE。"),
        "TABLE" => (op.Source.Table!, "S_" + EffectConditionCompiler.TargetAlias(op.Source.Table!)),
        _ => throw new EffectConfigException($"公式行 OP_SEQ={op.OpSeq}：来源域 '{op.Source.Scope}' 不支持标量取值。"),
    };

    private string BuildTermExpression(EffectOpPlan op, string alias, bool pick = false)
    {
        if (op.Terms is { Count: > 0 })
        {
            var expression = string.Join(" ", op.Terms.Select((term, index) =>
                (index == 0 ? (term.Coef == -1 ? "-" : "") : (term.Coef == -1 ? "- " : "+ "))
                + $"ISNULL({alias}.{EffectConditionCompiler.Identifier(term.Field)}, 0)"));
            return op.Terms[0].Coef == -1 ? "(" + expression + ")" : expression;
        }
        if (op.Source.Field is { Length: > 0 })
        {
            var field = $"{alias}.{EffectConditionCompiler.Identifier(op.Source.Field)}";
            // A pick copies the correlated row's value: NULL stays NULL (the numeric
            // default would turn a missing text/date value into '0'/1900-01-01).
            return pick ? field : $"ISNULL({field}, 0)";
        }
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
                         ? (Table: _plan.DetailTable!, Alias: "D", IsDetail: true)
                         : (Table: ResolveTableSource(op, item), Alias: "S_" + EffectConditionCompiler.TargetAlias(ResolveTableSource(op, item)), IsDetail: false)))
        {
            var (table, alias, isDetail) = group.Key;
            var correlation = string.Join(" AND ", group.Select(item =>
                $"{alias}.{EffectConditionCompiler.Identifier(item.Source.Field!)} = {targetAlias}.{EffectConditionCompiler.Identifier(item.TargetColumn)}"));
            if (isDetail)
            {
                parts.Add($"EXISTS (SELECT 1 FROM dbo.{EffectConditionCompiler.Identifier(table)} {alias} WHERE {correlation})");
                continue;
            }
            // TABLE-scoped location keys reference a registered context table whose rows
            // belong to this document (the context table carries the module master key
            // columns). The EXISTS must join back to the master row, otherwise a global
            // table scan would touch rows of other documents.
            var masterJoin = string.Join(" AND ", _plan.MasterPkOrder.Select(pk =>
                $"{alias}.{EffectConditionCompiler.Identifier(pk)} = M.{EffectConditionCompiler.Identifier(pk)}"));
            var masterFilter = AddMasterKeyParameters(parameters);
            parts.Add($"EXISTS (SELECT 1 FROM dbo.{EffectConditionCompiler.Identifier(table)} {alias} "
                + $"JOIN dbo.{EffectConditionCompiler.Identifier(_plan.MasterTable!)} M ON {masterJoin} "
                + $"WHERE {masterFilter} AND {correlation})");
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

    /// <summary>
    /// Resolves the physical table behind a TABLE-scoped match item. The location
    /// entries written by the translation phase omit an inline table (the formula
    /// row's SOURCE_TABLE carries it), so the op-level source table is authoritative;
    /// an inline table is honoured when present. Missing both is a hard config error.
    /// </summary>
    private string ResolveTableSource(EffectOpPlan op, EffectMatchItem item) =>
        !string.IsNullOrWhiteSpace(op.Source.Table)
            ? op.Source.Table!
            : !string.IsNullOrWhiteSpace(item.Source.Table)
                ? item.Source.Table!
                : throw new EffectConfigException(
                    $"公式行 OP_SEQ={op.OpSeq}：TABLE 定位键缺少源表（公式行 SOURCE_TABLE 或定位键 table）。");

    private string ResolveConditionAlias(string scope, string? table, EffectOpPlan op, string targetAlias) =>
        scope.ToUpperInvariant() switch
        {
            "TARGET" => targetAlias,
            "MASTER" => "M",
            "DETAIL" => "D",
            "TABLE" => "S_" + EffectConditionCompiler.TargetAlias(table ?? string.Empty),
            _ => throw new EffectConfigException($"条件来源域 '{scope}' 不可用。"),
        };

    /// <summary>True when the condition JSON references the given scope anywhere.</summary>
    private static bool ConditionReferencesScope(JsonElement condition, string scope)
    {
        switch (condition.ValueKind)
        {
            case JsonValueKind.Object:
                if (condition.TryGetProperty("scope", out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString()!.Equals(scope, StringComparison.OrdinalIgnoreCase))
                    return true;
                foreach (var property in condition.EnumerateObject())
                {
                    if (ConditionReferencesScope(property.Value, scope))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in condition.EnumerateArray())
                {
                    if (ConditionReferencesScope(item, scope))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static object? ParseScalar(string text) =>
        text.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null
        : long.TryParse(text, out var l) ? l
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
