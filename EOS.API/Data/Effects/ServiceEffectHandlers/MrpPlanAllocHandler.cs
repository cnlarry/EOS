using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// mrp-plan-alloc: allocates available MRP stock to plan/provide quantities.
/// order-open (customer order): writes PLAN_QTY / PLAN_SPARE_QTY / DEPOT_QTY on the
/// order detail with the segmented available-stock semantics (intended behaviour;
/// the legacy cursor never arms its per-product cache, see the design record).
/// material-provide (produce order): recomputes DEPOT_QTY on this document's
/// material lines as min(MRP_QTY, NEED_QTY), correcting the legacy global update.
/// Approve and deapprove share the same recompute statement (reverse kind
/// recompute reads current stock). Gated by SYSSS.PRO_MRP; off means zero rows.
/// All identifiers come from closed configuration checked against physical
/// columns; only key values travel as parameters.
/// </summary>
public sealed class MrpPlanAllocHandler : IEffectServiceHandler
{
    public string EffectKey => "mrp-plan-alloc";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("mrp-plan-alloc missing params.");
        var plan = context.Plan;
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("mrp-plan-alloc requires master-detail shape.");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("mrp-plan-alloc missing document key values.");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var cfg = MrpPlanAllocSpec.Parse(root, plan, columns);
        if (!await IsMrpEnabledAsync(context, token))
            return 0;
        var parameters = new List<EffectSqlParameter>();
        var sql = BuildUpdate(plan, cfg, context.MasterKeyValues, parameters);
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
    }

    internal static async Task<bool> IsMrpEnabledAsync(ServiceEffectContext context, CancellationToken token)
    {
        return await SystemParameterService.GetBoolAsync(
            context.Connection, context.Transaction, SystemParameterService.SystemOwner, "PRO_MRP",
            fallback: false, token);
    }

    internal static string BuildUpdate(
        ModuleEffectPlan plan,
        MrpPlanAllocSpec spec,
        IReadOnlyList<string> keyValues,
        List<EffectSqlParameter> parameters)
    {
        var masterWhere = ServiceEffectSql.MasterKeyFilter(plan, keyValues, parameters);
        var detailJoin = ServiceEffectSql.SameNameKeyJoin(plan, "D");
        var target = ServiceEffectSql.Q(spec.TargetTable);
        var sets = spec.Mode.Equals("order-open", StringComparison.OrdinalIgnoreCase)
            ? OrderOpenAssignments()
            : MaterialProvideAssignments();
        return $"UPDATE D SET {string.Join(", ", sets)} "
            + $"FROM dbo.{target} D "
            + $"JOIN dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M ON {detailJoin} "
            + "JOIN dbo.PRODUCT P ON P.PRO_NO = D.PRO_NO "
            + $"WHERE {masterWhere}";
    }

    private static IReadOnlyList<string> OrderOpenAssignments() => new[]
    {
        "D.[PLAN_QTY] = CASE WHEN P.MRP_QTY > 0 "
            + "THEN CASE WHEN P.MRP_QTY >= D.QTY THEN 0 ELSE D.QTY - P.MRP_QTY END "
            + "ELSE D.QTY END",
        "D.[PLAN_SPARE_QTY] = CASE WHEN P.MRP_QTY - D.QTY > 0 "
            + "THEN CASE WHEN P.MRP_QTY - D.QTY >= D.SPARE_QTY THEN 0 "
            + "ELSE D.SPARE_QTY - (P.MRP_QTY - D.QTY) END "
            + "ELSE D.SPARE_QTY END",
        "D.[DEPOT_QTY] = CASE WHEN P.MRP_QTY > 0 "
            + "THEN CASE WHEN P.MRP_QTY >= D.QTY + D.SPARE_QTY THEN D.QTY + D.SPARE_QTY "
            + "ELSE P.MRP_QTY END "
            + "ELSE 0 END",
    };

    private static IReadOnlyList<string> MaterialProvideAssignments() => new[]
    {
        "D.[DEPOT_QTY] = CASE WHEN P.MRP_QTY > 0 "
            + "THEN CASE WHEN P.MRP_QTY >= D.NEED_QTY THEN D.NEED_QTY ELSE P.MRP_QTY END "
            + "ELSE 0 END",
    };
}

internal sealed record MrpPlanAllocSpec(string TargetTable, string Mode)
{
    private static readonly IReadOnlySet<string> OrderOpenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PLAN_QTY", "PLAN_SPARE_QTY", "DEPOT_QTY",
    };

    public static MrpPlanAllocSpec Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("mrp-plan-alloc params must be a JSON object.");
        var target = Req(root, "targetTable");
        if (!target.Equals("COP_ORDER_D", StringComparison.OrdinalIgnoreCase)
            && !target.Equals("MOC_PRODUCE_D", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"mrp-plan-alloc.targetTable must be COP_ORDER_D or MOC_PRODUCE_D, got '{target}'.");
        var scope = root.TryGetProperty("scope", out var scopeValue)
            && scopeValue.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(scopeValue.GetString())
            ? scopeValue.GetString()!.Trim()
            : "THIS_DOC";
        if (!scope.Equals("THIS_DOC", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"mrp-plan-alloc.scope only supports THIS_DOC, got '{scope}'.");
        var stock = Req(root, "stockSource");
        if (!stock.Equals("PRODUCT.MRP_QTY", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"mrp-plan-alloc.stockSource only supports PRODUCT.MRP_QTY, got '{stock}'.");

        if (plan.DetailTable is null || !target.Equals(plan.DetailTable, StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"mrp-plan-alloc.targetTable must be the module detail table '{plan.DetailTable}'.");
        // Landed produce instances carry no mode; default it from the target table so
        // stored configuration keeps working. An explicit mode must agree with it.
        var defaultMode = target.Equals("COP_ORDER_D", StringComparison.OrdinalIgnoreCase)
            ? "order-open"
            : "material-provide";
        var mode = root.TryGetProperty("mode", out var modeValue)
            && modeValue.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(modeValue.GetString())
            ? modeValue.GetString()!.Trim()
            : defaultMode;
        if (!mode.Equals(defaultMode, StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"mrp-plan-alloc.mode '{mode}' disagrees with target table '{target}'.");
        if (mode.Equals("order-open", StringComparison.OrdinalIgnoreCase))
        {
            var fields = StrArr(root, "fields");
            if (fields.Length != OrderOpenFields.Count || fields.Any(field => !OrderOpenFields.Contains(field)))
                throw new EffectConfigException("mrp-plan-alloc order-open requires fields [PLAN_QTY, PLAN_SPARE_QTY, DEPOT_QTY].");
            if (root.TryGetProperty("field", out _))
                throw new EffectConfigException("mrp-plan-alloc order-open must use fields, not field.");
        }
        else
        {
            var field = root.TryGetProperty("field", out var fieldValue)
                && fieldValue.ValueKind == JsonValueKind.String
                ? fieldValue.GetString()!.Trim()
                : throw new EffectConfigException("mrp-plan-alloc material-provide requires field DEPOT_QTY.");
            if (!field.Equals("DEPOT_QTY", StringComparison.OrdinalIgnoreCase))
                throw new EffectConfigException($"mrp-plan-alloc material-provide field must be DEPOT_QTY, got '{field}'.");
            if (root.TryGetProperty("fields", out _))
                throw new EffectConfigException("mrp-plan-alloc material-provide must use field, not fields.");
        }

        if (!columns.Contains(target))
            throw new EffectConfigException($"mrp-plan-alloc table missing: {target}.");
        if (!columns.Contains("PRODUCT") || !columns.Contains("PRODUCT.MRP_QTY") || !columns.Contains("PRODUCT.PRO_NO"))
            throw new EffectConfigException("mrp-plan-alloc stock source PRODUCT.MRP_QTY is missing.");
        var required = mode.Equals("order-open", StringComparison.OrdinalIgnoreCase)
            ? new[] { "QTY", "SPARE_QTY", "PLAN_QTY", "PLAN_SPARE_QTY", "DEPOT_QTY" }
            : new[] { "NEED_QTY", "DEPOT_QTY" };
        foreach (var column in required)
            if (!columns.Contains(target + "." + column))
                throw new EffectConfigException($"mrp-plan-alloc column missing: {target}.{column}.");
        foreach (var pk in plan.MasterPkOrder)
            if (!columns.Contains(plan.MasterTable + "." + pk))
                throw new EffectConfigException($"mrp-plan-alloc master key column missing: {plan.MasterTable}.{pk}.");

        return new MrpPlanAllocSpec(target, mode);
    }

    private static string Req(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"mrp-plan-alloc missing string field '{name}'.");

    private static string[] StrArr(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new EffectConfigException($"mrp-plan-alloc.{name} must be a non-empty field name array.");
            list.Add(item.GetString()!.Trim());
        }
        return list.ToArray();
    }
}
