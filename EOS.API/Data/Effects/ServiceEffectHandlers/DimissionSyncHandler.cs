using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// employee-dimission-sync: on approval the resigned employees of a wage
/// document are marked dimitted (STATE=5) with the dimission effective date
/// read through the detail-mediated dimission reference (ported from
/// P_WF_HR_WAGE_LZ). On deapproval the same employee set is restored to
/// active (STATE=1, DIMISSION_DATE=NULL), an asymmetric fixed restore gated
/// by reverse kind "restore-active". Only document key values travel as
/// parameters; all table/column names come from closed configuration checked
/// against physical columns.
/// </summary>
public sealed class DimissionSyncHandler : IEffectServiceHandler
{
    public string EffectKey => "employee-dimission-sync";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("employee-dimission-sync 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null)
            throw new EffectConfigException("employee-dimission-sync 需要主表形态。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("employee-dimission-sync 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var spec = DimissionSyncSpec.Parse(root, plan, columns);

        var parameters = new List<EffectSqlParameter>
        {
            new("@wt", context.MasterKeyValues[0]),
            new("@wn", context.MasterKeyValues[1]),
        };
        string sql;
        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
        {
            sql = BuildApproveStatement(spec);
        }
        else
        {
            var kind = ReverseKind(context);
            if (!kind.Equals("restore-active", StringComparison.OrdinalIgnoreCase))
                throw new EffectConfigException(
                    $"employee-dimission-sync 解批 reverse.kind 仅支持 restore-active（当前 '{kind}'）。");
            sql = BuildDeapproveStatement(spec);
        }
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
    }

    /// <summary>
    /// Approve: mark the document employees dimitted, reading the effective
    /// date through the detail dimission reference. Employees without a
    /// matching dimission row are untouched (inner join, legacy semantics).
    /// </summary>
    internal static string BuildApproveStatement(DimissionSyncSpec spec)
    {
        var q = ServiceEffectSql.Q;
        return $"UPDATE E SET {q("STATE")} = 5, {q("DIMISSION_DATE")} = D.{q("INURE_DATE")} "
            + $"FROM dbo.{q(spec.TargetTable)} E "
            + $"JOIN dbo.{q(spec.DetailTable)} W ON W.{q("EMP_ID")} = E.{q("EMP_ID")} "
            + $"AND W.{q(spec.MasterTypeColumn)} = @wt AND W.{q(spec.MasterNoColumn)} = @wn "
            + $"JOIN dbo.{q(spec.DimissionTable)} D ON D.{q("DIMISSION_TYPE")} = W.{q("DIMISSION_TYPE")} "
            + $"AND D.{q("DIMISSION_NO")} = W.{q("DIMISSION_NO")}";
    }

    /// <summary>
    /// Deapprove: asymmetric fixed restore of the document employee set.
    /// </summary>
    internal static string BuildDeapproveStatement(DimissionSyncSpec spec)
    {
        var q = ServiceEffectSql.Q;
        return $"UPDATE E SET {q("STATE")} = 1, {q("DIMISSION_DATE")} = NULL "
            + $"FROM dbo.{q(spec.TargetTable)} E "
            + $"WHERE EXISTS (SELECT 1 FROM dbo.{q(spec.DetailTable)} W "
            + $"WHERE W.{q(spec.MasterTypeColumn)} = @wt AND W.{q(spec.MasterNoColumn)} = @wn "
            + $"AND W.{q("EMP_ID")} = E.{q("EMP_ID")})";
    }

    private static string ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse
            || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            throw new EffectConfigException("employee-dimission-sync 解批缺少 reverse.kind，禁止无守卫执行。");
        return kind.GetString()!;
    }
}

/// <summary>Parsed employee-dimission-sync configuration with closed shapes.</summary>
internal sealed record DimissionSyncSpec(
    string TargetTable,
    string MasterTable,
    string DetailTable,
    string DimissionTable,
    string MasterTypeColumn,
    string MasterNoColumn)
{
    public static DimissionSyncSpec Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("employee-dimission-sync 参数必须是 JSON 对象。");
        var target = Req(root, "targetTable");
        if (!target.Equals("HR_EMPLOYEE", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"employee-dimission-sync.targetTable 仅支持 HR_EMPLOYEE（当前 '{target}'）。");
        var source = Req(root, "source");
        if (!source.Equals("HR_WAGE_D/HR_DIMISSION_M", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"employee-dimission-sync.source 仅支持 HR_WAGE_D/HR_DIMISSION_M（当前 '{source}'）。");
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("employee-dimission-sync 需要主子表形态。");
        if (!plan.MasterTable.Equals("HR_WAGE_M", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"employee-dimission-sync 仅支持 HR_WAGE_M 主表形态（当前 '{plan.MasterTable}'）。");
        if (!plan.DetailTable.Equals("HR_WAGE_D", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"employee-dimission-sync 仅支持 HR_WAGE_D 明细形态（当前 '{plan.DetailTable}'）。");
        if (plan.MasterPkOrder.Count < 2)
            throw new EffectConfigException("employee-dimission-sync 缺少主表主键序。");
        var typeColumn = plan.MasterPkOrder[0];
        var noColumn = plan.MasterPkOrder[1];
        foreach (var reference in new[]
        {
            target, target + ".EMP_ID", target + ".STATE", target + ".DIMISSION_DATE",
            plan.MasterTable, plan.MasterTable + "." + typeColumn, plan.MasterTable + "." + noColumn,
            plan.DetailTable, plan.DetailTable + "." + typeColumn, plan.DetailTable + "." + noColumn,
            plan.DetailTable + ".EMP_ID", plan.DetailTable + ".DIMISSION_TYPE", plan.DetailTable + ".DIMISSION_NO",
            "HR_DIMISSION_M", "HR_DIMISSION_M.DIMISSION_TYPE", "HR_DIMISSION_M.DIMISSION_NO",
            "HR_DIMISSION_M.INURE_DATE",
        })
        {
            if (!columns.Contains(reference))
                throw new EffectConfigException($"employee-dimission-sync 列不存在：{reference}。");
        }
        return new DimissionSyncSpec(target, plan.MasterTable, plan.DetailTable, "HR_DIMISSION_M", typeColumn, noColumn);
    }

    private static string Req(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"employee-dimission-sync 缺少字符串字段 '{name}'。");
}
