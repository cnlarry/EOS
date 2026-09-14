using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// Shared correlation helper for service handlers: target rows are located either
/// directly by the module master key values (target == master) or by joining the target
/// table to the master on same-named primary key columns — the cross-table convention
/// across this ERP. Any missing column or missing correlation is a hard configuration
/// error (fail-closed), never an unconditioned update.
/// </summary>
internal static class ServiceEffectSql
{
    public static string Q(string identifier) => InventoryMovePlan.Q(identifier);

    /// <summary>WHERE clause locating master rows by primary key parameter values.</summary>
    public static string MasterKeyFilter(ModuleEffectPlan plan, IReadOnlyList<string> keyValues, List<EffectSqlParameter> parameters)
    {
        if (plan.MasterTable is null || plan.MasterPkOrder.Count == 0 || keyValues.Count == 0)
            throw new EffectConfigException("服务效果缺少主表主键上下文，禁止无条件更新。");
        var keys = Math.Min(plan.MasterPkOrder.Count, keyValues.Count);
        var parts = new List<string>();
        for (var index = 0; index < keys; index++)
        {
            var name = "@mk" + index;
            parameters.Add(new EffectSqlParameter(name, keyValues[index]));
            parts.Add($"M.{Q(plan.MasterPkOrder[index])} = {name}");
        }
        return string.Join(" AND ", parts);
    }

    /// <summary>JOIN condition tying a target table to the master on same-named master key columns.</summary>
    public static string SameNameKeyJoin(ModuleEffectPlan plan, string targetAlias)
    {
        var join = plan.MasterPkOrder
            .Where(pk => pk.Length > 0)
            .Select(pk => $"{targetAlias}.{Q(pk)} = M.{Q(pk)}");
        var condition = string.Join(" AND ", join);
        if (condition.Length == 0)
            throw new EffectConfigException($"目标表与主表不存在同名主键关联列，禁止更新（目标 {targetAlias}）。");
        return condition;
    }

    public static async Task<int> ExecAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        IReadOnlyList<EffectSqlParameter> parameters,
        CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(token);
    }

    public static string Literal(object? value) => value switch
    {
        "now" => "SYSDATETIME()",
        long number => number.ToString(),
        int number => number.ToString(),
        _ => "@lv",
    };
}

/// <summary>
/// set-state: places state markers and derived dates on the module master or a related
/// table. Shapes observed in the configuration data:
/// {targetTable, stateField, stateValue, dateField, dateMode MIN/MAX, sourceField} —
/// date aggregated from the module detail (or a named TABLE scope source) over rows
/// correlated by the master key; {targets:[…], state:{col: value}} — direct placement,
/// where "now" becomes SYSDATETIME().
/// </summary>
public sealed class SetStateHandler : IEffectServiceHandler
{
    public string EffectKey => "set-state";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("set-state 缺少参数。");
        var plan = context.Plan;
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        ValidateParams(root, plan, columns);
        var parameters = new List<EffectSqlParameter>();
        var statements = new List<string>();

        // Deapprove semantics come from the reverse structure: none/no-reverse is a
        // no-op (plan traces are not rolled back), clear-finish rebuilds the state
        // mapping as the cleared inverse, and legacy shapes without a kind keep the
        // forward placement for now.
        var isDeapprove = context.ExecutionEvent == EffectEvent.Deapprove;
        var reverseKind = isDeapprove ? ReverseKind(context) : null;
        if (isDeapprove && reverseKind is "none" or "no-reverse")
        {
            return 0;
        }
        var clear = isDeapprove && reverseKind == "clear-finish";

        if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
        {
            if (!root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
                throw new EffectConfigException("set-state targets 形态缺少 state 映射。");
            var targetIndex = 0;
            foreach (var target in targets.EnumerateArray())
            {
                // A plain table name keeps the master-key join convention; an object
                // adds explicit references that locate the target row either through
                // the document detail (source: a detail column, e.g. the source
                // document's application keys) or directly through the document
                // master (masterSource: a master column, for targets keyed by
                // master-row values such as scrap application keys). One target
                // uses a single locating kind, never a mix.
                var (table, refs) = target.ValueKind == JsonValueKind.String
                    ? (target.GetString()!.Trim(), new List<(string Target, string Source, bool FromMaster)>())
                    : ParseTargetObject(target);
                var fromMaster = refs.Count > 0 && refs.All(reference => reference.FromMaster);
                var sets = new List<string>();
                foreach (var property in state.EnumerateObject())
                {
                    var (fragment, needsParameter) = ResolveStateValue(property.Value, clear);
                    var value = needsParameter ? fragment + targetIndex + "_" + property.Name : fragment;
                    if (needsParameter)
                        parameters.Add(new EffectSqlParameter(value, clear ? string.Empty : property.Value.GetString()));
                    sets.Add($"{ServiceEffectSql.Q(property.Name)} = {value}");
                }
                statements.Add(BuildUpdate(plan, table, refs, fromMaster, sets, context.MasterKeyValues, parameters));
                targetIndex++;
            }
        }
        else
        {
            if (clear)
                throw new EffectConfigException("set-state clear-finish 反向仅支持 targets/state 形态。");
            var table = Required(root, "targetTable");
            var stateField = Required(root, "stateField");
            if (!root.TryGetProperty("stateValue", out var stateValueElement)
                || stateValueElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new EffectConfigException("set-state 缺少 stateValue。");
            var (stateFragment, stateParameterized) = ResolveStateValue(stateValueElement, clear: false);
            var stateAssignment = stateParameterized
                ? stateFragment + "sv"
                : stateFragment;
            if (stateParameterized)
                parameters.Add(new EffectSqlParameter(stateAssignment, stateValueElement.ValueKind == JsonValueKind.Number
                    ? stateValueElement.GetRawText()
                    : stateValueElement.GetString()));
            var sets = new List<string> { $"{ServiceEffectSql.Q(stateField)} = {stateAssignment}" };

            if (root.TryGetProperty("dateField", out var dateField) && dateField.ValueKind == JsonValueKind.String)
            {
                var dateColumn = dateField.GetString()!.Trim();
                var sourceField = Required(root, "sourceField");
                var sourceTable = plan.DetailTable;
                if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
                {
                    sourceTable = source.TryGetProperty("table", out var sourceTableValue)
                        && sourceTableValue.ValueKind == JsonValueKind.String
                            ? sourceTableValue.GetString()!.Trim()
                            : sourceTable;
                }
                if (sourceTable is null)
                    throw new EffectConfigException("set-state 缺少日期来源表。");
                var mode = root.TryGetProperty("dateMode", out var dm) && dm.ValueKind == JsonValueKind.String
                    ? dm.GetString()!.Trim().ToUpperInvariant()
                    : "MIN";
                var sourceAlias = table == sourceTable ? "T" : "S";
                var correlation = sourceAlias == "S" ? ServiceEffectSql.SameNameKeyJoin(plan, "S") : ServiceEffectSql.SameNameKeyJoin(plan, "T");
                var aggregate = $"(SELECT {mode}({ServiceEffectSql.Q(sourceField)}) FROM dbo.{ServiceEffectSql.Q(sourceTable)} {sourceAlias} WHERE {correlation})";
                sets.Add($"{ServiceEffectSql.Q(dateColumn)} = {aggregate}");
            }

            var joinWhere = table == plan.MasterTable
                ? MasterWhere(plan, context, parameters)
                : ServiceEffectSql.SameNameKeyJoin(plan, "T");
            statements.Add(
                $"UPDATE T SET {string.Join(", ", sets)} FROM dbo.{ServiceEffectSql.Q(table)} T JOIN dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M ON {ServiceEffectSql.SameNameKeyJoin(plan, "T")} WHERE {joinWhere}");
        }

        var affected = 0;
        foreach (var statement in statements)
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, statement, parameters, token);
        return affected;
    }

    /// <summary>
    /// Closed-shape and physical validation of the set-state parameters. Shared by the
    /// executor (which calls it before building any statement) and the save-time
    /// configuration check, so one set of rules decides both whether a document may be
    /// approved and whether the configuration may be saved.
    /// </summary>
    internal static void ValidateParams(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("set-state 参数必须是 JSON 对象。");
        if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
        {
            if (!root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
                throw new EffectConfigException("set-state targets 形态缺少 state 映射。");
            if (targets.GetArrayLength() == 0)
                throw new EffectConfigException("set-state targets 不能为空。");
            foreach (var target in targets.EnumerateArray())
            {
                var (table, refs) = target.ValueKind == JsonValueKind.String
                    ? (target.GetString()!.Trim(), new List<(string Target, string Source, bool FromMaster)>())
                    : ParseTargetObject(target);
                if (!columns.Contains(table))
                    throw new EffectConfigException($"set-state 目标表不存在：{table}。");
                var fromMaster = refs.Count > 0 && refs.All(reference => reference.FromMaster);
                if (refs.Count > 0 && !fromMaster && plan.DetailTable is null)
                    throw new EffectConfigException($"set-state 目标 {table} 使用明细定位键，但模块未配置明细表。");
                if (fromMaster && plan.MasterTable is null)
                    throw new EffectConfigException($"set-state 目标 {table} 使用主表定位键，但模块未配置主表。");
                foreach (var reference in refs)
                {
                    if (!columns.Contains(table + "." + reference.Target))
                        throw new EffectConfigException($"set-state 目标定位列不存在：{table}.{reference.Target}。");
                    var sourceTable = reference.FromMaster ? plan.MasterTable! : plan.DetailTable;
                    if (!columns.Contains(sourceTable + "." + reference.Source))
                        throw new EffectConfigException($"set-state {(reference.FromMaster ? "主表" : "明细")}定位列不存在：{sourceTable}.{reference.Source}。");
                }
                foreach (var property in state.EnumerateObject())
                {
                    if (!columns.Contains(table + "." + property.Name))
                        throw new EffectConfigException($"set-state 目标列不存在：{table}.{property.Name}。");
                    _ = ResolveStateValue(property.Value, clear: false);
                }
            }
            return;
        }

        var singleTable = Required(root, "targetTable");
        var stateField = Required(root, "stateField");
        if (!columns.Contains(singleTable + "." + stateField))
            throw new EffectConfigException($"set-state 目标列不存在：{singleTable}.{stateField}。");
        if (!root.TryGetProperty("stateValue", out var stateValue) || stateValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new EffectConfigException("set-state 缺少 stateValue。");
        _ = ResolveStateValue(stateValue, clear: false);

        if (root.TryGetProperty("dateField", out var dateField) && dateField.ValueKind == JsonValueKind.String)
        {
            var dateColumn = dateField.GetString()!.Trim();
            if (!columns.Contains(singleTable + "." + dateColumn))
                throw new EffectConfigException($"set-state 日期列不存在：{singleTable}.{dateColumn}。");
            var sourceField = Required(root, "sourceField");
            var sourceTable = plan.DetailTable;
            if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
            {
                if (!source.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.String
                    || !scope.GetString()!.Trim().Equals("TABLE", StringComparison.OrdinalIgnoreCase))
                    throw new EffectConfigException("set-state source.scope 仅支持 TABLE。");
                if (!source.TryGetProperty("table", out var tableValue) || tableValue.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(tableValue.GetString()))
                    throw new EffectConfigException("set-state source.table 不能为空。");
                sourceTable = tableValue.GetString()!.Trim();
            }
            if (sourceTable is null)
                throw new EffectConfigException("set-state 缺少日期来源表。");
            if (!columns.Contains(sourceTable + "." + sourceField))
                throw new EffectConfigException($"set-state 日期来源列不存在：{sourceTable}.{sourceField}。");
            var mode = root.TryGetProperty("dateMode", out var dm) && dm.ValueKind == JsonValueKind.String
                ? dm.GetString()!.Trim().ToUpperInvariant()
                : "MIN";
            if (mode is not ("MIN" or "MAX"))
                throw new EffectConfigException("set-state dateMode 仅支持 MIN/MAX。");
        }
    }

    private static string? ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            return null;
        return kind.GetString();
    }

    /// <summary>
    /// Builds the UPDATE statement for one targets/state target. A master-table
    /// target is filtered directly by key values; a master-located target is scoped
    /// to the current document through its master row (T correlated to M by the
    /// master refs, M pinned by the document keys); other targets are already tied
    /// to the master by the same-name key join.
    /// </summary>
    internal static string BuildUpdate(
        ModuleEffectPlan plan,
        string table,
        IReadOnlyList<(string Target, string Source, bool FromMaster)> refs,
        bool fromMaster,
        IReadOnlyList<string> sets,
        IReadOnlyList<string> masterKeyValues,
        List<EffectSqlParameter> parameters)
    {
        if (sets.Count == 0)
            throw new EffectConfigException("set-state 缺少可回写字段。");
        var join = refs.Count == 0
            ? ServiceEffectSql.SameNameKeyJoin(plan, "T")
            : fromMaster
                ? string.Join(" AND ", refs.Select(reference =>
                    $"T.{ServiceEffectSql.Q(reference.Target)} = M.{ServiceEffectSql.Q(reference.Source)}"))
                : string.Join(" AND ", refs.Select(reference =>
                    $"T.{ServiceEffectSql.Q(reference.Target)} = D.{ServiceEffectSql.Q(reference.Source)}"));
        var where = table == plan.MasterTable
            ? MasterKeysWhere(plan, masterKeyValues, parameters, "T")
            : fromMaster
                ? MasterKeysWhere(plan, masterKeyValues, parameters, "M")
                : "1=1";
        var from = $"dbo.{ServiceEffectSql.Q(table)} T";
        if (refs.Count > 0 && !fromMaster)
            from += $" JOIN dbo.{ServiceEffectSql.Q(plan.DetailTable!)} D ON {join}"
                + $" JOIN dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M ON {ServiceEffectSql.SameNameKeyJoin(plan, "D")}";
        else
            from += $" JOIN dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M ON {join}";
        return $"UPDATE T SET {string.Join(", ", sets)} FROM {from} WHERE {where}";
    }

    private static string MasterWhere(ModuleEffectPlan plan, ServiceEffectContext context, List<EffectSqlParameter> parameters) =>
        MasterKeysWhere(plan, context.MasterKeyValues, parameters, "T");

    private static string MasterKeysWhere(
        ModuleEffectPlan plan, IReadOnlyList<string> masterKeyValues, List<EffectSqlParameter> parameters, string alias)
    {
        var parts = new List<string>();
        var keys = Math.Min(plan.MasterPkOrder.Count, masterKeyValues.Count);
        if (keys == 0)
            throw new EffectConfigException("set-state 缺少主表主键值。");
        for (var index = 0; index < keys; index++)
        {
            var name = "@sk" + index;
            parameters.Add(new EffectSqlParameter(name, masterKeyValues[index]));
            parts.Add($"{alias}.{ServiceEffectSql.Q(plan.MasterPkOrder[index])} = {name}");
        }
        return string.Join(" AND ", parts);
    }

    private static string Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"set-state 缺少字符串字段 '{name}'。");

    /// <summary>
    /// Parses an object target: { table, refs: [{ target, source }] } locates the
    /// target rows through the module detail columns (the detail is already scoped
    /// to the current document by its master keys); { target, masterSource } locates
    /// them through the document master row itself (for targets keyed by master-row
    /// values the detail does not carry). One target commits to a single locating
    /// kind; mixing source and masterSource in one target is rejected fail-closed.
    /// </summary>
    private static (string Table, List<(string Target, string Source, bool FromMaster)> Refs) ParseTargetObject(JsonElement target)
    {
        if (target.ValueKind != JsonValueKind.Object
            || !target.TryGetProperty("table", out var tableValue) || tableValue.ValueKind != JsonValueKind.String)
            throw new EffectConfigException("set-state targets 项必须是表名或 {table, refs} 对象。");
        var refs = new List<(string Target, string Source, bool FromMaster)>();
        if (target.TryGetProperty("refs", out var refsValue) && refsValue.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in refsValue.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.String)
                    throw new EffectConfigException("set-state targets.refs 项必须是 {target, source|masterSource}。");
                var hasSource = item.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(s.GetString());
                var hasMasterSource = item.TryGetProperty("masterSource", out var m) && m.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(m.GetString());
                if (hasSource == hasMasterSource)
                    throw new EffectConfigException("set-state targets.refs 项必须且只能含 source / masterSource 其一。");
                refs.Add((t.GetString()!.Trim(),
                    hasSource ? s.GetString()!.Trim() : m.GetString()!.Trim(),
                    hasMasterSource));
            }
            if (refs.Count > 0 && refs.Any(reference => reference.FromMaster)
                && refs.Any(reference => !reference.FromMaster))
                throw new EffectConfigException("set-state targets.refs 项不得混用 source 与 masterSource。");
        }
        foreach (var property in target.EnumerateObject())
            if (!property.NameEquals("table") && !property.NameEquals("refs"))
                throw new EffectConfigException($"set-state targets 项含未登记键 '{property.Name}'。");
        return (tableValue.GetString()!.Trim(), refs);
    }

    /// <summary>
    /// Maps a configured state value to its SQL fragment. In clear mode (deapprove
    /// with reverse kind "clear-finish") booleans flip, "now" becomes NULL and other
    /// strings clear to the empty string; otherwise the forward placement applies.
    /// Parameter fragments carry the "@sv_"/"@cv_" prefix and the caller appends the
    /// column name for a unique parameter.
    /// </summary>
    internal static (string Fragment, bool NeedsParameter) ResolveStateValue(JsonElement value, bool clear)
    {
        if (clear)
        {
            return value.ValueKind switch
            {
                JsonValueKind.True => ("0", false),
                JsonValueKind.False => ("1", false),
                JsonValueKind.Number => ("0", false),
                JsonValueKind.String when value.GetString() == "now" => ("NULL", false),
                JsonValueKind.String => ("@cv_", true),
                _ => throw new EffectConfigException("set-state 状态值必须是标量。"),
            };
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number => (value.GetRawText(), false),
            JsonValueKind.String when value.GetString() == "now" => ("SYSDATETIME()", false),
            JsonValueKind.String => ("@sv_", true),
            JsonValueKind.True => ("1", false),
            JsonValueKind.False => ("0", false),
            _ => throw new EffectConfigException("set-state 状态值必须是标量。"),
        };
    }
}
