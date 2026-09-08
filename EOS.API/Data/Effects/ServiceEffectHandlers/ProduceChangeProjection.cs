using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// The original produce keys referenced by a produce-change document (read from the
/// change master's PRODUCE_TYPE/PRODUCE_NO columns).
/// </summary>
internal sealed record ProduceReference(string ProduceType, string ProduceNo);

/// <summary>
/// produce-change-apply "net-replace" projection (1509): the referenced produce's MRP
/// footprints are re-projected around the document overwrite inside the same apply
/// transaction — the old occupancy (finished-product expected-in on PRODUCT, raw-material
/// expected-get on PRODUCT, planned quantity on MOC_PLAN_MOC, order planned quantities on
/// COP_ORDER_D) is released while the original produce still holds its pre-change values,
/// and reoccupied afterwards from the overwritten produce. Ported from the two matching
/// passes in P_WF_MOC_PRODUCE_CHANGE. Deapprove is not open for change documents (reverse
/// kind "none"), so no reverse path exists here. All identifiers are fixed domain columns
/// verified against the physical schema before any statement runs.
/// </summary>
internal static class ProduceChangeProjection
{
    /// <summary>Product/plan/order footprint tables and columns used by every statement.</summary>
    private static readonly string[] FootprintColumns =
    {
        "PRODUCT", "PRODUCT.PRO_NO", "PRODUCT.NOT_IN_QTY", "PRODUCT.NOT_GET_QTY", "PRODUCT.MRP_QTY",
        "MOC_PLAN_MOC", "MOC_PLAN_MOC.PLAN_TYPE", "MOC_PLAN_MOC.PLAN_NO", "MOC_PLAN_MOC.SERIAL_NO",
        "MOC_PLAN_MOC.PRODUCE_QTY",
        "COP_ORDER_D", "COP_ORDER_D.ORDER_TYPE", "COP_ORDER_D.ORDER_NO", "COP_ORDER_D.SERIAL_NO",
        "COP_ORDER_D.FINISHED_PLAN_QTY", "COP_ORDER_D.FINISHED_PLAN_SPARE_QTY",
    };

    public static void ValidateColumns(ChangeApplyConfig cfg, ISet<string> columns)
    {
        if (!cfg.PrefixType.Equals("PRODUCE_TYPE", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException("produce-change-apply 净替换投影仅支持制令变更模块（PRODUCE_TYPE 前缀）。");
        var produceMaster = cfg.MasterTarget;
        var produceDetail = cfg.DetailTarget;
        var references = FootprintColumns.Concat(new[]
        {
            produceMaster, produceMaster + "." + cfg.PrefixType, produceMaster + "." + cfg.PrefixNo,
            produceMaster + ".PRO_NO", produceMaster + ".QTY", produceMaster + ".SPARE_QTY",
            produceMaster + ".PLAN_TYPE", produceMaster + ".PLAN_NO", produceMaster + ".PLAN_SERIAL_NO",
            produceMaster + ".ORDER_TYPE", produceMaster + ".ORDER_NO", produceMaster + ".ORDER_SERIAL_NO",
            produceDetail, produceDetail + "." + cfg.PrefixType, produceDetail + "." + cfg.PrefixNo,
            produceDetail + ".PRO_NO", produceDetail + ".NEED_QTY",
        }).ToArray();
        foreach (var reference in references)
            if (!columns.Contains(reference))
                throw new EffectConfigException($"produce-change-apply 净替换投影列不存在：{reference}。");
    }

    /// <summary>
    /// Reads the referenced produce keys from the change master. Null means the change
    /// carries no usable produce reference, in which case the projection is skipped and
    /// the overwrite alone proceeds (consistent with the legacy no-op for missing links).
    /// </summary>
    public static async Task<ProduceReference?> ReadReferenceAsync(
        ServiceEffectContext context,
        ChangeApplyConfig cfg,
        CancellationToken token)
    {
        var plan = context.Plan;
        var parameters = new List<EffectSqlParameter>();
        var sql = $"SELECT M.{ServiceEffectSql.Q(cfg.PrefixType)}, M.{ServiceEffectSql.Q(cfg.PrefixNo)} "
            + $"FROM dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M "
            + $"WHERE {ServiceEffectSql.MasterKeyFilter(plan, context.MasterKeyValues, parameters)}";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            return null;
        var type = reader.IsDBNull(0) ? null : reader.GetString(0).Trim();
        var no = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
        await reader.DisposeAsync();
        return string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(no)
            ? null
            : new ProduceReference(type!, no!);
    }

    /// <summary>
    /// Executes the release (reoccupy=false, original values still in place) or the
    /// reoccupy (reoccupy=true, produce already overwritten) statements. Parameter names
    /// are unique per command, so each statement carries its own parameter list.
    /// </summary>
    public static async Task<int> ExecuteStatementsAsync(
        ServiceEffectContext context,
        ChangeApplyConfig cfg,
        ProduceReference reference,
        bool reoccupy,
        ISet<string> columns,
        CancellationToken token)
    {
        ValidateColumns(cfg, columns);
        var affected = 0;
        foreach (var sql in BuildStatements(cfg, reoccupy))
        {
            var parameters = new List<EffectSqlParameter>
            {
                new("@pt", reference.ProduceType),
                new("@pn", reference.ProduceNo),
            };
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
        }
        return affected;
    }

    /// <summary>
    /// Builds the four projection statements. The produce alias P is always the referenced
    /// produce row (single row per produce, located by PRODUCE_TYPE/PRODUCE_NO @pt/@pn).
    /// </summary>
    public static IReadOnlyList<string> BuildStatements(ChangeApplyConfig cfg, bool reoccupy)
    {
        var master = cfg.MasterTarget;
        var detail = cfg.DetailTarget;
        var pt = cfg.PrefixType;
        var pn = cfg.PrefixNo;
        var q = ServiceEffectSql.Q;
        var produceFilter = $"P.{q(pt)} = @pt AND P.{q(pn)} = @pn";
        var statements = new List<string>
        {
            // 1) Finished-product expected-in: NOT_IN_QTY/MRP_QTY move with the produce
            //    quantity (old released, new occupied).
            $"UPDATE T SET {q("NOT_IN_QTY")} = ISNULL(T.{q("NOT_IN_QTY")},0) {(reoccupy ? "+" : "-")} ISNULL(P.{q("QTY")},0) {(reoccupy ? "+" : "-")} ISNULL(P.{q("SPARE_QTY")},0), "
                + $"{q("MRP_QTY")} = ISNULL(T.{q("MRP_QTY")},0) {(reoccupy ? "+" : "-")} ISNULL(P.{q("QTY")},0) {(reoccupy ? "+" : "-")} ISNULL(P.{q("SPARE_QTY")},0) "
                + $"FROM dbo.PRODUCT T JOIN dbo.{q(master)} P ON P.{q("PRO_NO")} = T.{q("PRO_NO")} WHERE {produceFilter}",
            // 2) Raw-material expected-get: NOT_GET_QTY follows the needs; MRP_QTY moves in
            //    the opposite direction (release adds, reoccupy subtracts).
            $"UPDATE T SET {q("NOT_GET_QTY")} = ISNULL(T.{q("NOT_GET_QTY")},0) {(reoccupy ? "+" : "-")} A.{q("QTY")}, "
                + $"{q("MRP_QTY")} = ISNULL(T.{q("MRP_QTY")},0) {(reoccupy ? "-" : "+")} A.{q("QTY")} "
                + $"FROM dbo.PRODUCT T JOIN (SELECT D.{q("PRO_NO")}, SUM(ISNULL(D.{q("NEED_QTY")},0)) {q("QTY")} "
                + $"FROM dbo.{q(detail)} D JOIN dbo.{q(master)} P ON D.{q(pt)}=P.{q(pt)} AND D.{q(pn)}=P.{q(pn)} "
                + $"WHERE {produceFilter} GROUP BY D.{q("PRO_NO")}) A ON T.{q("PRO_NO")} = A.{q("PRO_NO")}",
            // 3) Plan planned-produce quantity on the plan line referenced by the produce.
            $"UPDATE L SET {q("PRODUCE_QTY")} = ISNULL(L.{q("PRODUCE_QTY")},0) {(reoccupy ? "+" : "-")} ISNULL(P.{q("QTY")},0) "
                + $"FROM dbo.MOC_PLAN_MOC L JOIN dbo.{q(master)} P "
                + $"ON L.{q("PLAN_TYPE")}=P.{q("PLAN_TYPE")} AND L.{q("PLAN_NO")}=P.{q("PLAN_NO")} AND L.{q("SERIAL_NO")}=P.{q("PLAN_SERIAL_NO")} "
                + $"WHERE {produceFilter}",
            // 4) Order planned-produce quantities on the order line referenced by the produce.
            $"UPDATE O SET {q("FINISHED_PLAN_QTY")} = ISNULL(O.{q("FINISHED_PLAN_QTY")},0) {(reoccupy ? "+" : "-")} ISNULL(P.{q("QTY")},0), "
                + $"{q("FINISHED_PLAN_SPARE_QTY")} = ISNULL(O.{q("FINISHED_PLAN_SPARE_QTY")},0) {(reoccupy ? "+" : "-")} ISNULL(P.{q("SPARE_QTY")},0) "
                + $"FROM dbo.COP_ORDER_D O JOIN dbo.{q(master)} P "
                + $"ON O.{q("ORDER_TYPE")}=P.{q("ORDER_TYPE")} AND O.{q("ORDER_NO")}=P.{q("ORDER_NO")} AND O.{q("SERIAL_NO")}=P.{q("ORDER_SERIAL_NO")} "
                + $"WHERE {produceFilter}",
        };
        return statements;
    }
}
