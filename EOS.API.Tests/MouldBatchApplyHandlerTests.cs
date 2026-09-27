using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit (no DB) tests for the mould-batch-apply service handler: fail-closed
/// parameter validation, the BATCH_SORT approve branches with the over-apply
/// guard, and the asymmetric deapprove state machine (decision H2: the else
/// branch drops the baseline FINISHED_QTY decrement; H3: sort '2' keeps no
/// PRODUCT writeback).
/// </summary>
public class MouldBatchApplyHandlerTests
{
    private static ModuleEffectPlan Plan() => new(
        2906, "MOU_BATCH_M", "MOU_BATCH_D", "v-test",
        new[] { "BATCH_TYPE", "BATCH_NO" },
        Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

    private static ISet<string> Columns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MOU_BATCH_M", "MOU_BATCH_M.BATCH_TYPE", "MOU_BATCH_M.BATCH_NO",
        "MOU_BATCH_M.ACCEPT_TYPE", "MOU_BATCH_M.ACCEPT_NO",
        "MOU_BATCH_M.SCRAP_TYPE", "MOU_BATCH_M.SCRAP_NO", "MOU_BATCH_M.SCRAP_SERIAL_NO",
        "MOU_BATCH_M.BATCH_SORT", "MOU_BATCH_M.QTY", "MOU_BATCH_M.PRO_NO", "MOU_BATCH_M.LIAOCHANG",
        "MOU_ACCEPT_M", "MOU_ACCEPT_M.ACCEPT_TYPE", "MOU_ACCEPT_M.ACCEPT_NO",
        "MOU_ACCEPT_M.BATCH_STATE", "MOU_ACCEPT_M.FINISHED_QTY", "MOU_ACCEPT_M.QTY",
        "MOU_SCRAP_D", "MOU_SCRAP_D.SCRAP_TYPE", "MOU_SCRAP_D.SCRAP_NO",
        "MOU_SCRAP_D.SERIAL_NO", "MOU_SCRAP_D.BATCH_STATE",
        "PRODUCT", "PRODUCT.PRO_NO", "PRODUCT.P_LENGTH", "PRODUCT.P_WIDTH",
    };

    private const string LandedParamsJson =
        """{"branchField":"BATCH_SORT","acceptTargets":{"stateField":"BATCH_STATE","finishedField":"FINISHED_QTY"},"productFields":["P_LENGTH","P_WIDTH"],"scrapTarget":"MOU_SCRAP_D"}""";

    private static MouldBatchApplySpec ParseLanded() =>
        MouldBatchApplySpec.Parse(JsonDocument.Parse(LandedParamsJson).RootElement, Plan(), Columns());

    [Fact]
    public void EffectKey_RegisteredAsService()
    {
        Assert.Equal("mould-batch-apply", new MouldBatchApplyHandler().EffectKey);
        Assert.True(EffectRegistry.IsImplemented("mould-batch-apply"));
    }

    [Fact]
    public void Parse_AcceptsLandedParams()
    {
        var spec = ParseLanded();
        Assert.Equal("BATCH_STATE", spec.AcceptStateField, ignoreCase: true);
        Assert.Equal("FINISHED_QTY", spec.AcceptFinishedField, ignoreCase: true);
        Assert.Equal("MOU_SCRAP_D", spec.ScrapTarget, ignoreCase: true);
    }

    [Fact]
    public void Parse_RejectsBadConfigurations()
    {
        var plan = Plan();
        var columns = Columns();
        Assert.Throws<EffectConfigException>(() => MouldBatchApplySpec.Parse(JsonDocument.Parse(
            """{"branchField":"QTY","acceptTargets":{"stateField":"BATCH_STATE","finishedField":"FINISHED_QTY"},"productFields":["P_LENGTH","P_WIDTH"],"scrapTarget":"MOU_SCRAP_D"}""").RootElement, plan, columns));
        Assert.Throws<EffectConfigException>(() => MouldBatchApplySpec.Parse(JsonDocument.Parse(
            """{"branchField":"BATCH_SORT","acceptTargets":{"stateField":"STATE","finishedField":"FINISHED_QTY"},"productFields":["P_LENGTH","P_WIDTH"],"scrapTarget":"MOU_SCRAP_D"}""").RootElement, plan, columns));
        Assert.Throws<EffectConfigException>(() => MouldBatchApplySpec.Parse(JsonDocument.Parse(
            """{"branchField":"BATCH_SORT","acceptTargets":{"stateField":"BATCH_STATE","finishedField":"FINISHED_QTY"},"productFields":["P_LENGTH"],"scrapTarget":"MOU_SCRAP_D"}""").RootElement, plan, columns));
        Assert.Throws<EffectConfigException>(() => MouldBatchApplySpec.Parse(JsonDocument.Parse(
            """{"branchField":"BATCH_SORT","acceptTargets":{"stateField":"BATCH_STATE","finishedField":"FINISHED_QTY"},"productFields":["P_LENGTH","P_WIDTH"],"scrapTarget":"MOU_SCRAP_M"}""").RootElement, plan, columns));
        Assert.Throws<EffectConfigException>(() => MouldBatchApplySpec.Parse(JsonDocument.Parse(
            LandedParamsJson).RootElement, plan,
            new HashSet<string>(columns.Where(item => item != "PRODUCT.P_WIDTH"), StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void BuildApprove_AcceptBranch_UpdatesAcceptAndProduct()
    {
        var statements = MouldBatchApplyHandler.BuildApproveStatements(ParseLanded(), "1", new List<EffectSqlParameter>());
        Assert.Equal(2, statements.Count);
        Assert.Contains("[BATCH_STATE] = @st", statements[0]);
        Assert.Contains("[FINISHED_QTY] = ISNULL([FINISHED_QTY], 0) + @qty", statements[0]);
        Assert.Contains("WHERE [ACCEPT_TYPE] = @at AND [ACCEPT_NO] = @an", statements[0]);
        Assert.Contains("[P_LENGTH] = a.[LIAOCHANG]", statements[1]);
        Assert.Contains("[P_WIDTH] = P.[P_WIDTH] + ISNULL(a.[QTY], 0)", statements[1]);
        Assert.Contains("a.[BATCH_TYPE] = @bt AND a.[BATCH_NO] = @bn", statements[1]);
    }

    [Fact]
    public void BuildApprove_ScrapBranch_SetsScrapStateOnly()
    {
        var statements = MouldBatchApplyHandler.BuildApproveStatements(ParseLanded(), "3", new List<EffectSqlParameter>());
        Assert.Single(statements);
        Assert.Contains("MOU_SCRAP_D", statements[0]);
        Assert.Contains("[BATCH_STATE] = 1", statements[0]);
        Assert.DoesNotContain("PRODUCT", statements[0]);
    }

    [Fact]
    public void BuildOverApplyGuard_MatchesBaselinePredicate()
    {
        var sql = MouldBatchApplyHandler.BuildOverApplyGuard();
        Assert.Contains("FROM dbo.[MOU_ACCEPT_M]", sql);
        Assert.Contains("[QTY] < [FINISHED_QTY]", sql);
        Assert.Contains("[ACCEPT_TYPE] = @at AND [ACCEPT_NO] = @an", sql);
    }

    [Fact]
    public void BuildDeapprove_Sort1_RollsBackAcceptAndProduct()
    {
        var statements = MouldBatchApplyHandler.BuildDeapproveStatements(ParseLanded(), "1", new List<EffectSqlParameter>());
        Assert.Equal(2, statements.Count);
        Assert.Contains("[FINISHED_QTY] = ISNULL([FINISHED_QTY], 0) - @qty", statements[0]);
        Assert.Contains("[P_WIDTH] = P.[P_WIDTH] - ISNULL(a.[QTY], 0)", statements[1]);
    }

    [Fact]
    public void BuildDeapprove_Sort2_KeepsNoProductWriteback()
    {
        // H3: replicate the baseline asymmetry (approve wrote PRODUCT, deapprove does not).
        var statements = MouldBatchApplyHandler.BuildDeapproveStatements(ParseLanded(), "2", new List<EffectSqlParameter>());
        Assert.Single(statements);
        Assert.Contains("[FINISHED_QTY] = ISNULL([FINISHED_QTY], 0) - @qty", statements[0]);
        Assert.DoesNotContain("PRODUCT", statements[0]);
    }

    [Fact]
    public void BuildDeapprove_ElseBranch_DoesNotDecrementFinished()
    {
        // H2: approve never added FINISHED_QTY on the scrap branch, so deapprove
        // must not subtract it (baseline drift corrected).
        var statements = MouldBatchApplyHandler.BuildDeapproveStatements(ParseLanded(), "3", new List<EffectSqlParameter>());
        Assert.Single(statements);
        Assert.Contains("[BATCH_STATE] = 0", statements[0]);
        Assert.DoesNotContain("FINISHED_QTY", statements[0]);
    }
}
