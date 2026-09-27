using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// detail-field-sync: on approval each target row takes the document detail "new"
/// values except where the detail row itself marks the field unchanged (new equals
/// old, keeping the current target value); on deapproval the same rows take the
/// "old" values instead (ported from P_WF_HR_REDEPLOY, whose 28调动 columns follow
/// exactly this CASE WHEN shape). The NULL-unsafe equality is intentional baseline
/// parity: NULL=new/old comparisons fall into the ELSE branch on both paths.
/// The mirror direction is gated by reverse kind "restore-previous". Only document
/// key values travel as parameters; all table/column names come from closed
/// configuration checked against physical columns.
/// </summary>
public sealed class DetailFieldSyncHandler : IEffectServiceHandler
{
    public string EffectKey => "detail-field-sync";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("detail-field-sync 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("detail-field-sync 需要主表+明细表模块形态。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("detail-field-sync 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var spec = DetailFieldSyncSpec.Parse(root, plan, columns);

        var parameters = new List<EffectSqlParameter>
        {
            new("@dt", context.MasterKeyValues[0]),
            new("@dn", context.MasterKeyValues[1]),
        };
        string sql;
        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
        {
            sql = BuildUpdateStatement(spec, useNewValues: true);
        }
        else
        {
            var kind = ReverseKind(context);
            if (!kind.Equals("restore-previous", StringComparison.OrdinalIgnoreCase))
                throw new EffectConfigException(
                    $"detail-field-sync 解批 reverse.kind 仅支持 restore-previous（当前 '{kind}'）。");
            sql = BuildUpdateStatement(spec, useNewValues: false);
        }
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
    }

    /// <summary>
    /// Builds the single UPDATE covering all pairs: each target column takes the
    /// detail new (approve) or old (deapprove) value unless the detail row marks it
    /// unchanged. Target rows are located through the document detail scoped to the
    /// current document by its master keys.
    /// </summary>
    internal static string BuildUpdateStatement(DetailFieldSyncSpec spec, bool useNewValues)
    {
        var q = ServiceEffectSql.Q;
        var sets = spec.Pairs.Select(pair =>
        {
            var source = useNewValues ? pair.NewField : pair.OldField;
            return $"T.{q(pair.Column)} = CASE WHEN D.{q(pair.NewField)} = D.{q(pair.OldField)} "
                + $"THEN T.{q(pair.Column)} ELSE D.{q(source)} END";
        });
        return $"UPDATE T SET {string.Join(", ", sets)} "
            + $"FROM dbo.{q(spec.TargetTable)} T "
            + $"JOIN dbo.{q(spec.DetailTable)} D ON D.{q(spec.Key.Source)} = T.{q(spec.Key.Target)} "
            + $"JOIN dbo.{q(spec.MasterTable)} M ON {ServiceEffectSql.SameNameKeyJoin(spec.Plan, "D")} "
            + $"WHERE M.{q(spec.MasterTypeColumn)} = @dt AND M.{q(spec.MasterNoColumn)} = @dn";
    }

    private static string ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse
            || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            throw new EffectConfigException("detail-field-sync 解批缺少 reverse.kind，禁止无守卫执行。");
        return kind.GetString()!;
    }
}

/// <summary>One synced column: the target column with its detail new/old sources.</summary>
internal sealed record DetailFieldSyncPair(string Column, string NewField, string OldField);

/// <summary>Target locating key: the target column fed by a document detail column.</summary>
internal sealed record DetailFieldSyncKey(string Target, string Source);

/// <summary>Parsed detail-field-sync configuration with closed shapes.</summary>
internal sealed record DetailFieldSyncSpec(
    ModuleEffectPlan Plan,
    string MasterTable,
    string DetailTable,
    string TargetTable,
    string MasterTypeColumn,
    string MasterNoColumn,
    DetailFieldSyncKey Key,
    IReadOnlyList<DetailFieldSyncPair> Pairs)
{
    public static DetailFieldSyncSpec Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("detail-field-sync 参数必须是 JSON 对象。");
        var target = Req(root, "targetTable");
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("detail-field-sync 需要主表+明细表模块形态。");
        if (plan.MasterPkOrder.Count < 2)
            throw new EffectConfigException("detail-field-sync 缺少主表主键序。");
        if (!root.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("detail-field-sync 缺少 key 对象（{target, source}）。");
        var key = new DetailFieldSyncKey(Req(keyElement, "target"), Req(keyElement, "source"));
        if (!root.TryGetProperty("pairs", out var pairsElement) || pairsElement.ValueKind != JsonValueKind.Array
            || pairsElement.GetArrayLength() == 0)
            throw new EffectConfigException("detail-field-sync pairs 必须是非空数组。");
        var pairs = new List<DetailFieldSyncPair>();
        foreach (var item in pairsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new EffectConfigException("detail-field-sync pairs 项必须是 {column, newField, oldField} 对象。");
            pairs.Add(new DetailFieldSyncPair(Req(item, "column"), Req(item, "newField"), Req(item, "oldField")));
        }
        var references = new List<string>
        {
            target, target + "." + key.Target,
            plan.MasterTable, plan.DetailTable,
            plan.DetailTable + "." + key.Source,
        };
        foreach (var pk in plan.MasterPkOrder)
        {
            references.Add(plan.MasterTable + "." + pk);
            references.Add(plan.DetailTable + "." + pk);
        }
        foreach (var pair in pairs)
        {
            references.Add(target + "." + pair.Column);
            references.Add(plan.DetailTable + "." + pair.NewField);
            references.Add(plan.DetailTable + "." + pair.OldField);
        }
        foreach (var reference in references)
        {
            if (!columns.Contains(reference))
                throw new EffectConfigException($"detail-field-sync 列不存在：{reference}。");
        }
        return new DetailFieldSyncSpec(plan, plan.MasterTable, plan.DetailTable, target,
            plan.MasterPkOrder[0], plan.MasterPkOrder[1], key, pairs);
    }

    private static string Req(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"detail-field-sync 缺少字符串字段 '{name}'。");
}
