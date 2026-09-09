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
            foreach (var target in targets.EnumerateArray())
            {
                var table = target.GetString()!.Trim();
                if (!columns.Contains(table))
                    throw new EffectConfigException($"set-state 目标表不存在：{table}。");
                var sets = new List<string>();
                foreach (var property in state.EnumerateObject())
                {
                    if (!columns.Contains(table + "." + property.Name))
                        throw new EffectConfigException($"set-state 目标列不存在：{table}.{property.Name}。");
                    var (fragment, needsParameter) = ResolveStateValue(property.Value, clear);
                    var value = needsParameter ? fragment + property.Name : fragment;
                    if (needsParameter)
                        parameters.Add(new EffectSqlParameter(value, clear ? string.Empty : property.Value.GetString()));
                    sets.Add($"{ServiceEffectSql.Q(property.Name)} = {value}");
                }
                // A master-table target is filtered directly by key values; other targets
                // are already tied to the master by the same-name key join.
                var where = table == plan.MasterTable
                    ? MasterWhere(plan, context, parameters)
                    : "1=1";
                statements.Add(
                    $"UPDATE T SET {string.Join(", ", sets)} FROM dbo.{ServiceEffectSql.Q(table)} T JOIN dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M ON {ServiceEffectSql.SameNameKeyJoin(plan, "T")} WHERE {where}");
            }
        }
        else
        {
            if (clear)
                throw new EffectConfigException("set-state clear-finish 反向仅支持 targets/state 形态。");
            var table = Required(root, "targetTable");
            var stateField = Required(root, "stateField");
            var stateValue = Required(root, "stateValue");
            if (!columns.Contains(table + "." + stateField))
                throw new EffectConfigException($"set-state 目标列不存在：{table}.{stateField}。");
            var stateParameter = new EffectSqlParameter("@stateValue", stateValue);
            parameters.Add(stateParameter);
            var sets = new List<string> { $"{ServiceEffectSql.Q(stateField)} = @stateValue" };

            if (root.TryGetProperty("dateField", out var dateField) && dateField.ValueKind == JsonValueKind.String)
            {
                var dateColumn = dateField.GetString()!.Trim();
                if (!columns.Contains(table + "." + dateColumn))
                    throw new EffectConfigException($"set-state 日期列不存在：{table}.{dateColumn}。");
                var sourceField = Required(root, "sourceField");
                var sourceTable = plan.DetailTable;
                if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
                {
                    var scope = source.GetProperty("scope").GetString()!.Trim().ToUpperInvariant();
                    if (scope != "TABLE")
                        throw new EffectConfigException("set-state source.scope 仅支持 TABLE。");
                    sourceTable = source.GetProperty("table").GetString()!.Trim();
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

    private static string? ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            return null;
        return kind.GetString();
    }

    private static string MasterWhere(ModuleEffectPlan plan, ServiceEffectContext context, List<EffectSqlParameter> parameters)
    {
        var parts = new List<string>();
        var keys = Math.Min(plan.MasterPkOrder.Count, context.MasterKeyValues.Count);
        if (keys == 0)
            throw new EffectConfigException("set-state 缺少主表主键值。");
        for (var index = 0; index < keys; index++)
        {
            var name = "@sk" + index;
            parameters.Add(new EffectSqlParameter(name, context.MasterKeyValues[index]));
            parts.Add($"T.{ServiceEffectSql.Q(plan.MasterPkOrder[index])} = {name}");
        }
        return string.Join(" AND ", parts);
    }

    private static string Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"set-state 缺少字符串字段 '{name}'。");

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
