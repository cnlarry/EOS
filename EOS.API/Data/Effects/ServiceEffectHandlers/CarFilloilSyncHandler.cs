using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// car-filloil-sync: on approval a fuel-fill document pushes its readings onto the
/// vehicle (LAST_OIL_MILEAGE/LAST_MILEAGE from the document NOW values) and deducts
/// the filled amount from the oil card (ported from P_WF_CAR_FILLOIL). On deapproval
/// the same rows are restored to the document pre-values with the card amount added
/// back, an asymmetric fixed restore gated by reverse kind "restore-previous".
/// Only document key values travel as parameters; all table/column names come from
/// closed configuration checked against physical columns.
/// </summary>
public sealed class CarFilloilSyncHandler : IEffectServiceHandler
{
    public string EffectKey => "car-filloil-sync";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("car-filloil-sync 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null)
            throw new EffectConfigException("car-filloil-sync 需要主表形态。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("car-filloil-sync 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var spec = CarFilloilSyncSpec.Parse(root, plan, columns);

        var parameters = new List<EffectSqlParameter>
        {
            new("@ft", context.MasterKeyValues[0]),
            new("@fn", context.MasterKeyValues[1]),
        };
        IReadOnlyList<string> statements;
        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
        {
            statements = BuildApproveStatement(spec);
        }
        else
        {
            var kind = ReverseKind(context);
            if (!kind.Equals("restore-previous", StringComparison.OrdinalIgnoreCase))
                throw new EffectConfigException(
                    $"car-filloil-sync 解批 reverse.kind 仅支持 restore-previous（当前 '{kind}'）。");
            statements = BuildDeapproveStatement(spec);
        }
        var affected = 0;
        foreach (var statement in statements)
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, statement, parameters, token);
        return affected;
    }

    /// <summary>
    /// Approve: push the document NOW readings onto the vehicle and deduct the
    /// filled amount from the oil card (inner joins, baseline semantics).
    /// </summary>
    internal static IReadOnlyList<string> BuildApproveStatement(CarFilloilSyncSpec spec)
    {
        var q = ServiceEffectSql.Q;
        return new[]
        {
            $"UPDATE C SET C.{q("LAST_OIL_MILEAGE")} = M.{q("NOW_LAST_OIL_MILEAGE")}, "
            + $"C.{q("LAST_MILEAGE")} = M.{q("MILEAGE")} "
            + $"FROM dbo.{q(spec.CarTable)} C "
            + $"JOIN dbo.{q(spec.MasterTable)} M ON M.{q("CAR_ID")} = C.{q("CAR_ID")} "
            + $"WHERE M.{q(spec.MasterTypeColumn)} = @ft AND M.{q(spec.MasterNoColumn)} = @fn",
            $"UPDATE O SET O.{q("LAST_AMOUNT")} = O.{q("LAST_AMOUNT")} - M.{q("NOW_OIL_AMOUNT")} "
            + $"FROM dbo.{q(spec.OilcardTable)} O "
            + $"JOIN dbo.{q(spec.MasterTable)} M ON M.{q("OIL_CARD")} = O.{q("OILCARD_ID")} "
            + $"WHERE M.{q(spec.MasterTypeColumn)} = @ft AND M.{q(spec.MasterNoColumn)} = @fn",
        };
    }

    /// <summary>
    /// Deapprove: asymmetric fixed restore of the same rows to the document
    /// pre-values with the card amount added back.
    /// </summary>
    internal static IReadOnlyList<string> BuildDeapproveStatement(CarFilloilSyncSpec spec)
    {
        var q = ServiceEffectSql.Q;
        return new[]
        {
            $"UPDATE C SET C.{q("LAST_OIL_MILEAGE")} = M.{q("LAST_OIL_MILEAGE")}, "
            + $"C.{q("LAST_MILEAGE")} = M.{q("LAST_MILEAGE")} "
            + $"FROM dbo.{q(spec.CarTable)} C "
            + $"JOIN dbo.{q(spec.MasterTable)} M ON M.{q("CAR_ID")} = C.{q("CAR_ID")} "
            + $"WHERE M.{q(spec.MasterTypeColumn)} = @ft AND M.{q(spec.MasterNoColumn)} = @fn",
            $"UPDATE O SET O.{q("LAST_AMOUNT")} = O.{q("LAST_AMOUNT")} + M.{q("NOW_OIL_AMOUNT")} "
            + $"FROM dbo.{q(spec.OilcardTable)} O "
            + $"JOIN dbo.{q(spec.MasterTable)} M ON M.{q("OIL_CARD")} = O.{q("OILCARD_ID")} "
            + $"WHERE M.{q(spec.MasterTypeColumn)} = @ft AND M.{q(spec.MasterNoColumn)} = @fn",
        };
    }

    private static string ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse
            || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            throw new EffectConfigException("car-filloil-sync 解批缺少 reverse.kind，禁止无守卫执行。");
        return kind.GetString()!;
    }
}

/// <summary>Parsed car-filloil-sync configuration with closed shapes.</summary>
internal sealed record CarFilloilSyncSpec(
    string MasterTable,
    string CarTable,
    string OilcardTable,
    string MasterTypeColumn,
    string MasterNoColumn)
{
    public static CarFilloilSyncSpec Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("car-filloil-sync 参数必须是 JSON 对象。");
        var master = Req(root, "master");
        if (!master.Equals("CAR_FILLOIL_M", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"car-filloil-sync.master 仅支持 CAR_FILLOIL_M（当前 '{master}'）。");
        var car = Req(root, "carTable");
        if (!car.Equals("CAR", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"car-filloil-sync.carTable 仅支持 CAR（当前 '{car}'）。");
        var oilcard = Req(root, "oilcardTable");
        if (!oilcard.Equals("CAR_OILCARD", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"car-filloil-sync.oilcardTable 仅支持 CAR_OILCARD（当前 '{oilcard}'）。");
        if (plan.MasterTable is null || !plan.MasterTable.Equals(master, StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"car-filloil-sync 仅支持 CAR_FILLOIL_M 主表形态（当前 '{plan.MasterTable}'）。");
        if (plan.MasterPkOrder.Count < 2)
            throw new EffectConfigException("car-filloil-sync 缺少主表主键序。");
        var typeColumn = plan.MasterPkOrder[0];
        var noColumn = plan.MasterPkOrder[1];
        foreach (var reference in new[]
        {
            master, master + "." + typeColumn, master + "." + noColumn,
            master + ".CAR_ID", master + ".MILEAGE",
            master + ".NOW_LAST_OIL_MILEAGE", master + ".LAST_OIL_MILEAGE", master + ".LAST_MILEAGE",
            master + ".OIL_CARD", master + ".NOW_OIL_AMOUNT",
            car, car + ".CAR_ID", car + ".LAST_OIL_MILEAGE", car + ".LAST_MILEAGE",
            oilcard, oilcard + ".OILCARD_ID", oilcard + ".LAST_AMOUNT",
        })
        {
            if (!columns.Contains(reference))
                throw new EffectConfigException($"car-filloil-sync 列不存在：{reference}。");
        }
        return new CarFilloilSyncSpec(master, car, oilcard, typeColumn, noColumn);
    }

    private static string Req(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"car-filloil-sync 缺少字符串字段 '{name}'。");
}
