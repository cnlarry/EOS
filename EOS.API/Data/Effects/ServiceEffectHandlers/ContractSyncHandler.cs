using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// employee-contract-sync: on approval the employee master contract facts are
/// recomputed from the confirmed contract set (ported from P_WF_HR_CONTRACT).
/// The document's employee set is cleared first (CONTRACT_DATE=NULL), then each
/// employee is backfilled with max(END_DATE) / max(BEGIN_DATE) / max(CONTRACT_NO)
/// aggregated over confirmed contracts; the approve run includes this document,
/// the deapprove run excludes it (reverse kind "recompute-excluding-self").
/// All identifiers come from closed configuration checked against physical
/// columns; only key values travel as parameters.
/// </summary>
public sealed class ContractSyncHandler : IEffectServiceHandler
{
    public string EffectKey => "employee-contract-sync";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("employee-contract-sync 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null)
            throw new EffectConfigException("employee-contract-sync 需要主表形态。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("employee-contract-sync 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var spec = ContractSyncSpec.Parse(root, plan, columns);

        bool includeSelf;
        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
        {
            includeSelf = true;
        }
        else
        {
            var kind = ReverseKind(context);
            if (!kind.Equals("recompute-excluding-self", StringComparison.OrdinalIgnoreCase))
                throw new EffectConfigException(
                    $"employee-contract-sync 解批 reverse.kind 仅支持 recompute-excluding-self（当前 '{kind}'）。");
            includeSelf = false;
        }

        var parameters = new List<EffectSqlParameter>
        {
            new("@ct", context.MasterKeyValues[0]),
            new("@cn", context.MasterKeyValues[1]),
        };
        var affected = 0;
        foreach (var sql in BuildStatements(spec, includeSelf))
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
        return affected;
    }

    /// <summary>
    /// Clear-then-backfill pair. The clear targets only this document's employees;
    /// the backfill aggregates max() over the confirmed set (including or excluding
    /// this document), so employees with no confirmed contract keep NULL.
    /// </summary>
    internal static IReadOnlyList<string> BuildStatements(ContractSyncSpec spec, bool includeSelf)
    {
        var q = ServiceEffectSql.Q;
        var clear = $"UPDATE E SET {q("CONTRACT_DATE")} = NULL "
            + $"FROM dbo.{q(spec.TargetTable)} E "
            + $"WHERE EXISTS (SELECT 1 FROM dbo.{q(spec.DetailTable)} d "
            + $"WHERE d.{q(spec.MasterTypeColumn)} = @ct AND d.{q(spec.MasterNoColumn)} = @cn "
            + $"AND d.{q("EMP_ID")} = E.{q("EMP_ID")})";
        var membership = includeSelf
            ? "(m.CONFIRM_TAG = 1 OR (m.CONT_TYPE = @ct AND m.CONT_NO = @cn))"
            : "(m.CONFIRM_TAG = 1 AND NOT (m.CONT_TYPE = @ct AND m.CONT_NO = @cn))";
        var backfill = $"UPDATE E SET {q("CONTRACT_DATE")} = t.{q("END_DATE")}, "
            + $"{q("CONTRACT_BEGIN_DATE")} = t.{q("BEGIN_DATE")}, {q("CONTRACT_NO")} = t.{q("CONTRACT_NO")} "
            + $"FROM dbo.{q(spec.TargetTable)} E JOIN ("
            + $"SELECT d.{q("EMP_ID")}, MAX(d.{q("END_DATE")}) {q("END_DATE")}, "
            + $"MAX(d.{q("BEGIN_DATE")}) {q("BEGIN_DATE")}, "
            + $"MAX(RTRIM(d.{q("CONTRACT_NO")})) {q("CONTRACT_NO")} "
            + $"FROM dbo.{q(spec.MasterTable)} m JOIN dbo.{q(spec.DetailTable)} d "
            + $"ON m.{q(spec.MasterTypeColumn)} = d.{q(spec.MasterTypeColumn)} "
            + $"AND m.{q(spec.MasterNoColumn)} = d.{q(spec.MasterNoColumn)} "
            + $"WHERE {membership} "
            + $"AND d.{q("EMP_ID")} IN (SELECT {q("EMP_ID")} FROM dbo.{q(spec.DetailTable)} "
            + $"WHERE {q(spec.MasterTypeColumn)} = @ct AND {q(spec.MasterNoColumn)} = @cn) "
            + $"GROUP BY d.{q("EMP_ID")}) t ON t.{q("EMP_ID")} = E.{q("EMP_ID")}";
        return new[] { clear, backfill };
    }

    private static string ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse
            || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            throw new EffectConfigException("employee-contract-sync 解批缺少 reverse.kind，禁止无守卫执行。");
        return kind.GetString()!;
    }
}

/// <summary>Parsed employee-contract-sync configuration with closed shapes.</summary>
internal sealed record ContractSyncSpec(
    string TargetTable,
    string MasterTable,
    string DetailTable,
    string MasterTypeColumn,
    string MasterNoColumn)
{
    public static ContractSyncSpec Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("employee-contract-sync 参数必须是 JSON 对象。");
        var target = Req(root, "targetTable");
        if (!target.Equals("HR_EMPLOYEE", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"employee-contract-sync.targetTable 仅支持 HR_EMPLOYEE（当前 '{target}'）。");
        var fields = StrArr(root, "fields");
        var required = new[] { "CONTRACT_DATE", "CONTRACT_BEGIN_DATE", "CONTRACT_NO" };
        if (fields.Length != required.Length || required.Any(field => !fields.Contains(field, StringComparer.OrdinalIgnoreCase)))
            throw new EffectConfigException("employee-contract-sync.fields 必须是 [CONTRACT_DATE, CONTRACT_BEGIN_DATE, CONTRACT_NO]。");
        var source = Req(root, "source");
        if (!source.Equals("HR_CONTRACT_D/M", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"employee-contract-sync.source 仅支持 HR_CONTRACT_D/M（当前 '{source}'）。");
        if (root.TryGetProperty("includeSelfOnApprove", out var include)
            && !(include.ValueKind == JsonValueKind.True))
            throw new EffectConfigException("employee-contract-sync.includeSelfOnApprove 必须为 true（批核含本单是旧语义）。");
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("employee-contract-sync 需要主子表形态。");
        if (plan.MasterPkOrder.Count < 2)
            throw new EffectConfigException("employee-contract-sync 缺少主表主键序。");
        var typeColumn = plan.MasterPkOrder[0];
        var noColumn = plan.MasterPkOrder[1];
        foreach (var reference in new[]
        {
            target, target + ".EMP_ID", target + ".CONTRACT_DATE",
            target + ".CONTRACT_BEGIN_DATE", target + ".CONTRACT_NO",
            plan.MasterTable, plan.MasterTable + "." + typeColumn, plan.MasterTable + "." + noColumn,
            plan.MasterTable + ".CONFIRM_TAG",
            plan.DetailTable, plan.DetailTable + "." + typeColumn, plan.DetailTable + "." + noColumn,
            plan.DetailTable + ".EMP_ID", plan.DetailTable + ".BEGIN_DATE",
            plan.DetailTable + ".END_DATE", plan.DetailTable + ".CONTRACT_NO",
        })
        {
            if (!columns.Contains(reference))
                throw new EffectConfigException($"employee-contract-sync 列不存在：{reference}。");
        }
        return new ContractSyncSpec(target, plan.MasterTable, plan.DetailTable, typeColumn, noColumn);
    }

    private static string Req(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"employee-contract-sync 缺少字符串字段 '{name}'。");

    private static string[] StrArr(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException($"employee-contract-sync.{name} 必须是非空数组。");
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new EffectConfigException($"employee-contract-sync.{name} 存在空字段名。");
            list.Add(item.GetString()!.Trim());
        }
        if (list.Count == 0)
            throw new EffectConfigException($"employee-contract-sync.{name} 必须是非空数组。");
        return list.ToArray();
    }
}
