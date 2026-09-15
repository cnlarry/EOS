using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit tests for the car-filloil-sync service handler (no DB): closed parameter
/// parsing, the asymmetric approve/restore statements and fail-closed validation.
/// </summary>
public class CarFilloilSyncHandlerTests
{
    private static ModuleEffectPlan FilloilPlan()
    {
        var plan = new ModuleEffectPlan(1902, "CAR_FILLOIL_M", null, "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        return plan with { MasterPkOrder = new[] { "FILLOIL_TYPE", "FILLOIL_NO" } };
    }

    private static ISet<string> FilloilColumns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CAR_FILLOIL_M", "CAR", "CAR_OILCARD",
        "CAR_FILLOIL_M.FILLOIL_TYPE", "CAR_FILLOIL_M.FILLOIL_NO",
        "CAR_FILLOIL_M.CAR_ID", "CAR_FILLOIL_M.MILEAGE",
        "CAR_FILLOIL_M.NOW_LAST_OIL_MILEAGE", "CAR_FILLOIL_M.LAST_OIL_MILEAGE",
        "CAR_FILLOIL_M.LAST_MILEAGE", "CAR_FILLOIL_M.OIL_CARD", "CAR_FILLOIL_M.NOW_OIL_AMOUNT",
        "CAR.CAR_ID", "CAR.LAST_OIL_MILEAGE", "CAR.LAST_MILEAGE",
        "CAR_OILCARD.OILCARD_ID", "CAR_OILCARD.LAST_AMOUNT",
    };

    private const string ParamsJson = """
        {"master":"CAR_FILLOIL_M","carTable":"CAR","oilcardTable":"CAR_OILCARD"}
        """;

    private static CarFilloilSyncSpec Spec() =>
        CarFilloilSyncSpec.Parse(JsonDocument.Parse(ParamsJson).RootElement, FilloilPlan(), FilloilColumns());

    [Fact]
    public void CarFilloilSync_Approve_PushesNowReadingsAndDeductsCard()
    {
        var statements = CarFilloilSyncHandler.BuildApproveStatement(Spec());
        Assert.Equal(2, statements.Count);
        Assert.Contains("C.[LAST_OIL_MILEAGE] = M.[NOW_LAST_OIL_MILEAGE]", statements[0]);
        Assert.Contains("C.[LAST_MILEAGE] = M.[MILEAGE]", statements[0]);
        Assert.Contains("M.[FILLOIL_TYPE] = @ft AND M.[FILLOIL_NO] = @fn", statements[0]);
        Assert.Contains("O.[LAST_AMOUNT] = O.[LAST_AMOUNT] - M.[NOW_OIL_AMOUNT]", statements[1]);
        Assert.Contains("M.[OIL_CARD] = O.[OILCARD_ID]", statements[1]);
    }

    [Fact]
    public void CarFilloilSync_Deapprove_RestoresPreValuesAndRefundsCard()
    {
        var statements = CarFilloilSyncHandler.BuildDeapproveStatement(Spec());
        Assert.Equal(2, statements.Count);
        Assert.Contains("C.[LAST_OIL_MILEAGE] = M.[LAST_OIL_MILEAGE]", statements[0]);
        Assert.Contains("C.[LAST_MILEAGE] = M.[LAST_MILEAGE]", statements[0]);
        Assert.DoesNotContain("NOW_LAST_OIL_MILEAGE", statements[0]);
        Assert.Contains("O.[LAST_AMOUNT] = O.[LAST_AMOUNT] + M.[NOW_OIL_AMOUNT]", statements[1]);
    }

    [Fact]
    public void CarFilloilSync_Parse_PinsClosedTableShapes()
    {
        Assert.Throws<EffectConfigException>(() =>
            CarFilloilSyncSpec.Parse(JsonDocument.Parse("""{"master":"OTHER_M","carTable":"CAR","oilcardTable":"CAR_OILCARD"}""").RootElement, FilloilPlan(), FilloilColumns()));
        Assert.Throws<EffectConfigException>(() =>
            CarFilloilSyncSpec.Parse(JsonDocument.Parse("""{"master":"CAR_FILLOIL_M","carTable":"CAR"}""").RootElement, FilloilPlan(), FilloilColumns()));
        var withoutCard = new HashSet<string>(FilloilColumns(), StringComparer.OrdinalIgnoreCase);
        withoutCard.Remove("CAR_FILLOIL_M.OIL_CARD");
        var exception = Assert.Throws<EffectConfigException>(() =>
            CarFilloilSyncSpec.Parse(JsonDocument.Parse(ParamsJson).RootElement, FilloilPlan(), withoutCard));
        Assert.Contains("OIL_CARD", exception.Message);
    }
}
