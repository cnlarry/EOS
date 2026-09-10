using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit (no DB) tests for the employee-dimission-sync service handler: closed
/// parameter validation, the approve statement (STATE=5 with the effective
/// date read through the detail dimission reference), and the asymmetric
/// deapprove restore (STATE=1, DIMISSION_DATE=NULL) gated by reverse kind
/// "restore-active".
/// </summary>
public class DimissionSyncHandlerTests
{
    private static ModuleEffectPlan Plan() => new(
        180310, "HR_WAGE_M", "HR_WAGE_D", "v-test",
        new[] { "WAGE_TYPE", "WAGE_NO" },
        Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

    private static ISet<string> Columns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "HR_EMPLOYEE", "HR_EMPLOYEE.EMP_ID", "HR_EMPLOYEE.STATE", "HR_EMPLOYEE.DIMISSION_DATE",
        "HR_WAGE_M", "HR_WAGE_M.WAGE_TYPE", "HR_WAGE_M.WAGE_NO",
        "HR_WAGE_D", "HR_WAGE_D.WAGE_TYPE", "HR_WAGE_D.WAGE_NO",
        "HR_WAGE_D.EMP_ID", "HR_WAGE_D.DIMISSION_TYPE", "HR_WAGE_D.DIMISSION_NO",
        "HR_DIMISSION_M", "HR_DIMISSION_M.DIMISSION_TYPE", "HR_DIMISSION_M.DIMISSION_NO",
        "HR_DIMISSION_M.INURE_DATE",
    };

    private const string LandedParamsJson =
        """{"targetTable":"HR_EMPLOYEE","source":"HR_WAGE_D/HR_DIMISSION_M"}""";

    private static DimissionSyncSpec ParseLanded() =>
        DimissionSyncSpec.Parse(JsonDocument.Parse(LandedParamsJson).RootElement, Plan(), Columns());

    [Fact]
    public void EffectKey_RegisteredAsService()
    {
        Assert.Equal("employee-dimission-sync", new DimissionSyncHandler().EffectKey);
        Assert.True(EffectRegistry.IsImplemented("employee-dimission-sync"));
        Assert.True(BusinessActionCatalog.IsKnownEffectKey("employee-dimission-sync"));
    }

    [Fact]
    public void Parse_AcceptsLandedParams()
    {
        var spec = ParseLanded();
        Assert.Equal("HR_EMPLOYEE", spec.TargetTable, ignoreCase: true);
        Assert.Equal("HR_DIMISSION_M", spec.DimissionTable, ignoreCase: true);
        Assert.Equal("WAGE_TYPE", spec.MasterTypeColumn, ignoreCase: true);
        Assert.Equal("WAGE_NO", spec.MasterNoColumn, ignoreCase: true);
    }

    [Fact]
    public void Parse_RejectsBadConfigurations()
    {
        var plan = Plan();
        var columns = Columns();
        Assert.Throws<EffectConfigException>(() => DimissionSyncSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"HR_EMPLOYEX","source":"HR_WAGE_D/HR_DIMISSION_M"}""").RootElement, plan, columns));
        Assert.Throws<EffectConfigException>(() => DimissionSyncSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"HR_EMPLOYEE","source":"HR_WAGE_D"}""").RootElement, plan, columns));
        Assert.Throws<EffectConfigException>(() => DimissionSyncSpec.Parse(JsonDocument.Parse(
            LandedParamsJson).RootElement,
            new ModuleEffectPlan(180310, "HR_WAGEX_M", "HR_WAGE_D", "v-test",
                new[] { "WAGE_TYPE", "WAGE_NO" },
                Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>()),
            columns));
        Assert.Throws<EffectConfigException>(() => DimissionSyncSpec.Parse(JsonDocument.Parse(
            LandedParamsJson).RootElement, plan,
            new HashSet<string>(columns.Where(item => item != "HR_DIMISSION_M.INURE_DATE"), StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void BuildApprove_MarksDimittedWithCrossDocDate()
    {
        var sql = DimissionSyncHandler.BuildApproveStatement(ParseLanded());
        Assert.Contains("[STATE] = 5", sql);
        Assert.Contains("[DIMISSION_DATE] = D.[INURE_DATE]", sql);
        Assert.Contains("JOIN dbo.[HR_WAGE_D] W ON", sql);
        Assert.Contains("W.[WAGE_TYPE] = @wt AND W.[WAGE_NO] = @wn", sql);
        Assert.Contains("JOIN dbo.[HR_DIMISSION_M] D ON", sql);
        Assert.Contains("D.[DIMISSION_TYPE] = W.[DIMISSION_TYPE]", sql);
        Assert.Contains("D.[DIMISSION_NO] = W.[DIMISSION_NO]", sql);
        Assert.Contains("W.[EMP_ID] = E.[EMP_ID]", sql);
    }

    [Fact]
    public void BuildDeapprove_RestoresActiveAsymmetrically()
    {
        var sql = DimissionSyncHandler.BuildDeapproveStatement(ParseLanded());
        Assert.Contains("[STATE] = 1", sql);
        Assert.Contains("[DIMISSION_DATE] = NULL", sql);
        Assert.Contains("WHERE EXISTS (SELECT 1 FROM dbo.[HR_WAGE_D] W", sql);
        Assert.Contains("W.[WAGE_TYPE] = @wt AND W.[WAGE_NO] = @wn", sql);
        Assert.DoesNotContain("HR_DIMISSION_M", sql);
        Assert.DoesNotContain("INURE_DATE", sql);
    }

    [Fact]
    public void StructSchemas_AcceptLandedShapes()
    {
        Assert.Empty(EffectStructSchemas.ValidateParams("employee-dimission-sync", LandedParamsJson));
        Assert.Empty(EffectStructSchemas.ValidateReverse("""{"kind":"restore-active"}"""));
        Assert.Contains("restore-active", EffectStructSchemas.AllReverseKinds());
    }

    [Fact]
    public void StructSchemas_RejectBadShapes()
    {
        var unknownRoot = EffectStructSchemas.ValidateParams("employee-dimission-sync",
            """{"targetTable":"HR_EMPLOYEE","source":"HR_WAGE_D/HR_DIMISSION_M","stateValue":5}""");
        Assert.NotEmpty(unknownRoot);
        var unknownKind = EffectStructSchemas.ValidateReverse("""{"kind":"magic"}""");
        Assert.NotEmpty(unknownKind);
    }
}
