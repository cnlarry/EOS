using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit tests for the half-stock-move service handler (no DB): closed parameter
/// parsing, the reverse-flow-only gate, physical column validation and the
/// document-scoped row-set statement.
/// </summary>
public class HalfStockMoveHandlerTests
{
    private static ModuleEffectPlan HalfInPlan()
    {
        var plan = new ModuleEffectPlan(2603, "HALF_IN_M", "HALF_IN_D", "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        return plan with { MasterPkOrder = new[] { "IN_TYPE", "IN_NO" } };
    }

    private static ISet<string> HalfColumns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "HALF_IN_M", "HALF_IN_D", "HALF_PRO_DEPOT",
        "HALF_IN_M.IN_TYPE", "HALF_IN_M.IN_NO", "HALF_IN_M.IN_DATE",
        "HALF_IN_D.IN_TYPE", "HALF_IN_D.IN_NO", "HALF_IN_D.SERIAL_NO",
        "HALF_IN_D.PRO_NO", "HALF_IN_D.PROCEDURE_TYPE_ID", "HALF_IN_D.DEPOT_ID", "HALF_IN_D.QTY",
        "HALF_PRO_DEPOT.PRO_NO", "HALF_PRO_DEPOT.PROCEDURE_TYPE_ID", "HALF_PRO_DEPOT.DEPOT_ID",
        "HALF_PRO_DEPOT.QTY", "HALF_PRO_DEPOT.INIT_QTY",
    };

    private const string InParamsJson = """
        {"direction":"IN","fieldMap":{"masterDate":"IN_DATE","qty":"QTY"}}
        """;

    private static HalfStockMovePlan InPlan() =>
        HalfStockMovePlan.Parse(JsonDocument.Parse(InParamsJson).RootElement);

    [Fact]
    public void HalfStockMove_Parse_AcceptsMinimalClosedShape()
    {
        var plan = InPlan();
        Assert.Equal(1, plan.Direction);
        Assert.Equal("IN_DATE", plan.MasterDateField);
        Assert.Equal("QTY", plan.QtyField);
    }

    [Fact]
    public void HalfStockMove_Parse_RejectsOpenShapes()
    {
        Assert.Throws<EffectConfigException>(() =>
            HalfStockMovePlan.Parse(JsonDocument.Parse("""{"fieldMap":{"masterDate":"IN_DATE","qty":"QTY"}}""").RootElement));
        Assert.Throws<EffectConfigException>(() =>
            HalfStockMovePlan.Parse(JsonDocument.Parse("""{"direction":"SIDEWAYS","fieldMap":{"masterDate":"IN_DATE","qty":"QTY"}}""").RootElement));
        Assert.Throws<EffectConfigException>(() =>
            HalfStockMovePlan.Parse(JsonDocument.Parse("""{"direction":"IN"}""").RootElement));
        Assert.Throws<EffectConfigException>(() =>
            HalfStockMovePlan.Parse(JsonDocument.Parse("""{"direction":"IN","fieldMap":{"masterDate":"IN_DATE"}}""").RootElement));
        Assert.Throws<EffectConfigException>(() =>
            HalfStockMovePlan.Parse(JsonDocument.Parse("""{"direction":"IN","fieldMap":{"masterDate":"IN_DATE","qty":{"terms":[]}}}""").RootElement));
    }

    [Fact]
    public void HalfStockMove_Reverse_OnlyReverseFlowRuns()
    {
        Assert.Equal("reverse-flow",
            HalfStockMoveHandler.RequireReverseKind(JsonDocument.Parse("""{"kind":"reverse-flow"}""").RootElement));
        Assert.Throws<EffectConfigException>(() =>
            HalfStockMoveHandler.RequireReverseKind(JsonDocument.Parse("""{"kind":"no-reverse"}""").RootElement));
        Assert.Throws<EffectConfigException>(() => HalfStockMoveHandler.RequireReverseKind(null));
    }

    [Fact]
    public void HalfStockMove_ValidateColumns_FailClosedOnMissingColumns()
    {
        InPlan().ValidateColumns(HalfInPlan(), HalfColumns());
        var withoutProcedure = new HashSet<string>(HalfColumns(), StringComparer.OrdinalIgnoreCase);
        withoutProcedure.Remove("HALF_IN_D.PROCEDURE_TYPE_ID");
        var exception = Assert.Throws<EffectConfigException>(() =>
            InPlan().ValidateColumns(HalfInPlan(), withoutProcedure));
        Assert.Contains("PROCEDURE_TYPE_ID", exception.Message);
    }

    [Fact]
    public void HalfStockMove_BuildRowSet_ScopesLinesByDocumentKeys()
    {
        var rowSet = InPlan().BuildRowSet(HalfInPlan(), new[] { "AD", "H26090001" }, HalfColumns());
        Assert.Contains("JOIN dbo.[HALF_IN_D] D ON D.[IN_TYPE] = M.[IN_TYPE] AND D.[IN_NO] = M.[IN_NO]", rowSet.Sql);
        Assert.Contains("M.[IN_TYPE] = @mk0 AND M.[IN_NO] = @mk1", rowSet.Sql);
        Assert.Contains("ISNULL(D.[QTY], 0) AS QTY", rowSet.Sql);
        Assert.Equal("AD", rowSet.Parameters[0].Value);
        Assert.Equal("H26090001", rowSet.Parameters[1].Value);
    }

    [Fact]
    public void HalfStockMove_BuildRowSet_RejectsSingleKeyExecution()
    {
        var plan = HalfInPlan() with { MasterPkOrder = new[] { "IN_NO" } };
        Assert.Throws<EffectConfigException>(() =>
            InPlan().BuildRowSet(plan, new[] { "H26090001" }, HalfColumns()));
    }
}
