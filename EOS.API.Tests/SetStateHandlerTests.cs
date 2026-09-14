using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit tests for the set-state master-located shape (no DB): targets keyed by
/// master-row values (masterSource) locate the target through the document master
/// instead of the detail, keep the document-key scope, and stay fail-closed on
/// mixed or dangling references.
/// </summary>
public class SetStateHandlerTests
{
    private static ModuleEffectPlan BatchTopPlan()
    {
        var plan = new ModuleEffectPlan(2915, "MOU_BATCHTOP_M", "MOU_BATCHTOP_D", "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        return plan with { MasterPkOrder = new[] { "BATCH_TYPE", "BATCH_NO" } };
    }

    private static ISet<string> BatchTopColumns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MOU_BATCHTOP_M", "MOU_BATCHTOP_D", "MOU_SCRAP_D",
        "MOU_BATCHTOP_M.BATCH_TYPE", "MOU_BATCHTOP_M.BATCH_NO", "MOU_BATCHTOP_M.BATCH_SORT",
        "MOU_BATCHTOP_M.SCRAP_TYPE", "MOU_BATCHTOP_M.SCRAP_NO", "MOU_BATCHTOP_M.SCRAP_SERIAL_NO",
        "MOU_BATCHTOP_D.BATCH_TYPE", "MOU_BATCHTOP_D.BATCH_NO", "MOU_BATCHTOP_D.SERIAL_NO",
        "MOU_SCRAP_D.SCRAP_TYPE", "MOU_SCRAP_D.SCRAP_NO", "MOU_SCRAP_D.SERIAL_NO",
        "MOU_SCRAP_D.BATCH_STATE",
    };

    private const string MasterLocatedJson = """
        {"targets":[{"table":"MOU_SCRAP_D","refs":[{"target":"SCRAP_TYPE","masterSource":"SCRAP_TYPE"},{"target":"SCRAP_NO","masterSource":"SCRAP_NO"},{"target":"SERIAL_NO","masterSource":"SCRAP_SERIAL_NO"}]}],"state":{"BATCH_STATE":true}}
        """;

    private static JsonElement MasterLocatedRoot() =>
        JsonDocument.Parse(MasterLocatedJson).RootElement;

    [Fact]
    public void SetState_MasterLocatedTarget_CorrelatesTargetToMasterRow()
    {
        var parameters = new List<EffectSqlParameter>();
        var sql = SetStateHandler.BuildUpdate(
            BatchTopPlan(), "MOU_SCRAP_D",
            new List<(string Target, string Source, bool FromMaster)>
            {
                ("SCRAP_TYPE", "SCRAP_TYPE", true),
                ("SCRAP_NO", "SCRAP_NO", true),
                ("SERIAL_NO", "SCRAP_SERIAL_NO", true),
            },
            true, new[] { "T.[BATCH_STATE] = 1" }, new[] { "AD", "B26090001" }, parameters);

        // Target rows are found through the master row values, not the detail.
        Assert.Contains("T.[SCRAP_TYPE] = M.[SCRAP_TYPE]", sql);
        Assert.Contains("T.[SCRAP_NO] = M.[SCRAP_NO]", sql);
        Assert.Contains("T.[SERIAL_NO] = M.[SCRAP_SERIAL_NO]", sql);
        Assert.DoesNotContain(" JOIN dbo.[MOU_BATCHTOP_D] D ", sql);
        // The master side is pinned to the current document keys.
        Assert.Contains("M.[BATCH_TYPE] = @sk0 AND M.[BATCH_NO] = @sk1", sql);
        Assert.Equal("AD", parameters[0].Value);
        Assert.Equal("B26090001", parameters[1].Value);
    }

    [Fact]
    public void SetState_MasterLocatedTarget_PassesSharedValidation()
    {
        // The same JSON the 2915 configuration carries must survive the shared
        // save-time/runtime physical validation (EffectParamPhysicalValidator reuses it).
        SetStateHandler.ValidateParams(MasterLocatedRoot(), BatchTopPlan(), BatchTopColumns());
    }

    [Fact]
    public void SetState_MixedDetailAndMasterRefs_Rejected()
    {
        var json = """{"targets":[{"table":"MOU_SCRAP_D","refs":[{"target":"SCRAP_TYPE","source":"SCRAP_TYPE"},{"target":"SCRAP_NO","masterSource":"SCRAP_NO"}]}],"state":{"BATCH_STATE":true}}""";
        Assert.Throws<EffectConfigException>(() =>
            SetStateHandler.ValidateParams(JsonDocument.Parse(json).RootElement, BatchTopPlan(), BatchTopColumns()));
    }

    [Fact]
    public void SetState_MasterSourceMissingColumn_Rejected()
    {
        var json = """{"targets":[{"table":"MOU_SCRAP_D","refs":[{"target":"SCRAP_TYPE","masterSource":"NO_SUCH_COL"}]}],"state":{"BATCH_STATE":true}}""";
        var exception = Assert.Throws<EffectConfigException>(() =>
            SetStateHandler.ValidateParams(JsonDocument.Parse(json).RootElement, BatchTopPlan(), BatchTopColumns()));
        Assert.Contains("NO_SUCH_COL", exception.Message);
    }

    [Fact]
    public void SetState_RefWithoutAnySource_Rejected()
    {
        var json = """{"targets":[{"table":"MOU_SCRAP_D","refs":[{"target":"SCRAP_TYPE"}]}],"state":{"BATCH_STATE":true}}""";
        Assert.Throws<EffectConfigException>(() =>
            SetStateHandler.ValidateParams(JsonDocument.Parse(json).RootElement, BatchTopPlan(), BatchTopColumns()));
    }

    [Fact]
    public void SetState_DetailLocatedTarget_StillRequiresDetailColumns()
    {
        // The pre-existing detail shape is untouched: sources must be detail columns.
        var json = """{"targets":[{"table":"MOU_SCRAP_D","refs":[{"target":"SCRAP_TYPE","source":"SCRAP_TYPE"}]}],"state":{"BATCH_STATE":true}}""";
        var exception = Assert.Throws<EffectConfigException>(() =>
            SetStateHandler.ValidateParams(JsonDocument.Parse(json).RootElement, BatchTopPlan(), BatchTopColumns()));
        Assert.Contains("MOU_BATCHTOP_D.SCRAP_TYPE", exception.Message);
    }
}
