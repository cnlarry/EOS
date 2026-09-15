using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// sample-edition-bump: on approval a sample document stamps its edition —
/// empty becomes '01', otherwise the numeric edition increments by one, padded to
/// two digits (ported from P_WF_SAMPLE_PRO). The legacy first-time branch also
/// copied the sample row into PRODUCT and launched the product approval workflow;
/// that chain is intentionally not ported: its launcher (P_WF_RUN) was retired with
/// the legacy workflow framework and no product flow definition exists, so a
/// first-time sample without a product row is rejected fail-closed instead of
/// silently skipping the creation. Deapproval is a no-op (the legacy procedure has
/// no deapprove branch), gated by reverse kind "none". Only document key values
/// travel as parameters; all table/column names come from closed configuration
/// checked against physical columns.
/// </summary>
public sealed class SampleEditionBumpHandler : IEffectServiceHandler
{
    public string EffectKey => "sample-edition-bump";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("sample-edition-bump 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null)
            throw new EffectConfigException("sample-edition-bump 需要主表形态。");
        if (context.MasterKeyValues.Count < 1)
            throw new EffectConfigException("sample-edition-bump 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var spec = SampleEditionBumpSpec.Parse(root, plan, columns);

        var parameters = new List<EffectSqlParameter>
        {
            new("@pn", context.MasterKeyValues[0]),
        };
        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
        {
            return await ServiceEffectSql.ExecAsync(
                context.Connection, context.Transaction, BuildApproveStatement(spec), parameters, token);
        }
        var kind = ReverseKind(context);
        if (!kind.Equals("none", StringComparison.OrdinalIgnoreCase)
            && !kind.Equals("no-reverse", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException(
                $"sample-edition-bump 解批 reverse.kind 仅支持 none（当前 '{kind}'）。");
        return 0;
    }

    /// <summary>
    /// Approve: refuse a first-time sample without a product row (its creation chain
    /// is unported), otherwise stamp the bumped edition. Both statements are static
    /// text; only the document key travels as a parameter.
    /// </summary>
    internal static string BuildApproveStatement(SampleEditionBumpSpec spec)
    {
        var q = ServiceEffectSql.Q;
        var trimmed = $"LTRIM(RTRIM(ISNULL(M.{q("EDITION")},'')))";
        var bumped = $"CAST(CAST(LTRIM(RTRIM(M.{q("EDITION")})) AS int) + 1 AS varchar(12))";
        return $"IF EXISTS (SELECT 1 FROM dbo.{q(spec.MasterTable)} M WHERE M.{q(spec.MasterNoColumn)} = @pn "
            + $"AND {trimmed} = '' "
            + $"AND NOT EXISTS (SELECT 1 FROM dbo.{q("PRODUCT")} P WHERE P.{q(spec.MasterNoColumn)} = M.{q(spec.MasterNoColumn)})) "
            + "THROW 50000, N'样品首次转正式：产品资料不存在，转正式需人工处理。', 1; "
            + $"UPDATE M SET M.{q("EDITION")} = CASE WHEN {trimmed} = '' THEN '01' "
            + $"WHEN LEN({bumped}) = 1 THEN '0' + {bumped} ELSE {bumped} END "
            + $"FROM dbo.{q(spec.MasterTable)} M WHERE M.{q(spec.MasterNoColumn)} = @pn";
    }

    private static string ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse
            || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            throw new EffectConfigException("sample-edition-bump 解批缺少 reverse.kind，禁止无守卫执行。");
        return kind.GetString()!;
    }
}

/// <summary>Parsed sample-edition-bump configuration with closed shapes.</summary>
internal sealed record SampleEditionBumpSpec(
    string MasterTable,
    string MasterNoColumn)
{
    public static SampleEditionBumpSpec Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("sample-edition-bump 参数必须是 JSON 对象。");
        var master = Req(root, "master");
        if (!master.Equals("SAMPLE_PRO", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"sample-edition-bump.master 仅支持 SAMPLE_PRO（当前 '{master}'）。");
        if (plan.MasterTable is null || !plan.MasterTable.Equals(master, StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"sample-edition-bump 仅支持 SAMPLE_PRO 主表形态（当前 '{plan.MasterTable}'）。");
        if (plan.MasterPkOrder.Count < 1)
            throw new EffectConfigException("sample-edition-bump 缺少主表主键序。");
        var noColumn = plan.MasterPkOrder[0];
        foreach (var reference in new[]
        {
            master, master + "." + noColumn, master + ".EDITION",
            "PRODUCT", "PRODUCT." + noColumn,
        })
        {
            if (!columns.Contains(reference))
                throw new EffectConfigException($"sample-edition-bump 列不存在：{reference}。");
        }
        return new SampleEditionBumpSpec(master, noColumn);
    }

    private static string Req(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"sample-edition-bump 缺少字符串字段 '{name}'。");
}
