using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit tests for the sample-edition-bump service handler (no DB): closed parameter
/// parsing, the first-time creation guard and the edition increment statement.
/// </summary>
public class SampleEditionBumpHandlerTests
{
    private static ModuleEffectPlan SamplePlan()
    {
        var plan = new ModuleEffectPlan(2401, "SAMPLE_PRO", null, "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        return plan with { MasterPkOrder = new[] { "PRO_NO" } };
    }

    private static ISet<string> SampleColumns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SAMPLE_PRO", "PRODUCT",
        "SAMPLE_PRO.PRO_NO", "SAMPLE_PRO.EDITION",
        "PRODUCT.PRO_NO",
    };

    private const string ParamsJson = """{"master":"SAMPLE_PRO"}""";

    private static SampleEditionBumpSpec Spec() =>
        SampleEditionBumpSpec.Parse(JsonDocument.Parse(ParamsJson).RootElement, SamplePlan(), SampleColumns());

    [Fact]
    public void SampleEditionBump_Approve_GuardsFirstTimeWithoutProduct()
    {
        // First-time samples (empty edition) without a product row are refused
        // fail-closed: their creation chain (row copy + workflow) is unported.
        // The refusal is decided by a guard query in C# (a readable 400), not by a
        // THROW inside the update statement (which surfaced as a 500).
        var guard = SampleEditionBumpHandler.BuildFirstPromotionGuardQuery(Spec());
        Assert.Contains("NOT EXISTS (SELECT 1 FROM dbo.[PRODUCT] P WHERE P.[PRO_NO] = M.[PRO_NO])", guard);
        Assert.Contains("M.[PRO_NO] = @pn", guard);
        Assert.Contains("COUNT(*)", guard);

        var sql = SampleEditionBumpHandler.BuildApproveStatement(Spec());
        Assert.Contains("M.[PRO_NO] = @pn", sql);
        Assert.DoesNotContain("THROW", sql);
    }

    [Fact]
    public void SampleEditionBump_Approve_BumpsEditionWithZeroPadding()
    {
        var sql = SampleEditionBumpHandler.BuildApproveStatement(Spec());
        // Empty edition starts at '01'; otherwise numeric increment, mirroring the
        // legacy two-digit padding exactly (only a one-char result gets a '0').
        Assert.Contains("THEN '01'", sql);
        Assert.Contains("CAST(LTRIM(RTRIM(M.[EDITION])) AS int) + 1", sql);
        Assert.Contains("THEN '0' +", sql);
    }

    [Fact]
    public void SampleEditionBump_Parse_PinsSampleMaster()
    {
        Assert.Throws<EffectConfigException>(() =>
            SampleEditionBumpSpec.Parse(JsonDocument.Parse("""{"master":"OTHER_M"}""").RootElement, SamplePlan(), SampleColumns()));
        Assert.Throws<EffectConfigException>(() =>
            SampleEditionBumpSpec.Parse(JsonDocument.Parse("""{}""").RootElement, SamplePlan(), SampleColumns()));
        var withoutEdition = new HashSet<string>(SampleColumns(), StringComparer.OrdinalIgnoreCase);
        withoutEdition.Remove("SAMPLE_PRO.EDITION");
        var exception = Assert.Throws<EffectConfigException>(() =>
            SampleEditionBumpSpec.Parse(JsonDocument.Parse(ParamsJson).RootElement, SamplePlan(), withoutEdition));
        Assert.Contains("EDITION", exception.Message);
    }
}
