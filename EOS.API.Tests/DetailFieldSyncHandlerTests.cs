using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit tests for the detail-field-sync service handler (no DB): closed parameter
/// parsing, the new/old CASE-WHEN statement shapes and fail-closed validation.
/// </summary>
public class DetailFieldSyncHandlerTests
{
    private static ModuleEffectPlan RedeployPlan()
    {
        var plan = new ModuleEffectPlan(180103, "HR_REDEPLOY_M", "HR_REDEPLOY_D", "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        return plan with { MasterPkOrder = new[] { "REDEPLOY_TYPE", "REDEPLOY_NO" } };
    }

    private static ISet<string> RedeployColumns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "HR_REDEPLOY_M", "HR_REDEPLOY_D", "HR_EMPLOYEE",
        "HR_REDEPLOY_M.REDEPLOY_TYPE", "HR_REDEPLOY_M.REDEPLOY_NO",
        "HR_REDEPLOY_D.REDEPLOY_TYPE", "HR_REDEPLOY_D.REDEPLOY_NO",
        "HR_REDEPLOY_D.EMP_ID", "HR_REDEPLOY_D.DEPT_ID", "HR_REDEPLOY_D.OLD_DEPT_ID",
        "HR_EMPLOYEE.EMP_ID", "HR_EMPLOYEE.DEPT_ID",
    };

    private const string ParamsJson = """
        {"targetTable":"HR_EMPLOYEE","key":{"target":"EMP_ID","source":"EMP_ID"},
         "pairs":[{"column":"DEPT_ID","newField":"DEPT_ID","oldField":"OLD_DEPT_ID"}]}
        """;

    private static DetailFieldSyncSpec Spec() =>
        DetailFieldSyncSpec.Parse(JsonDocument.Parse(ParamsJson).RootElement, RedeployPlan(), RedeployColumns());

    [Fact]
    public void DetailFieldSync_Approve_TakesNewUnlessUnchanged()
    {
        var sql = DetailFieldSyncHandler.BuildUpdateStatement(Spec(), useNewValues: true);
        Assert.Contains(
            "T.[DEPT_ID] = CASE WHEN D.[DEPT_ID] = D.[OLD_DEPT_ID] THEN T.[DEPT_ID] ELSE D.[DEPT_ID] END", sql);
        Assert.Contains("JOIN dbo.[HR_REDEPLOY_D] D ON D.[EMP_ID] = T.[EMP_ID]", sql);
        Assert.Contains("M.[REDEPLOY_TYPE] = @dt AND M.[REDEPLOY_NO] = @dn", sql);
    }

    [Fact]
    public void DetailFieldSync_Deapprove_TakesOldUnlessUnchanged()
    {
        var sql = DetailFieldSyncHandler.BuildUpdateStatement(Spec(), useNewValues: false);
        Assert.Contains(
            "T.[DEPT_ID] = CASE WHEN D.[DEPT_ID] = D.[OLD_DEPT_ID] THEN T.[DEPT_ID] ELSE D.[OLD_DEPT_ID] END", sql);
    }

    [Fact]
    public void DetailFieldSync_Parse_RejectsOpenShapes()
    {
        Assert.Throws<EffectConfigException>(() =>
            DetailFieldSyncSpec.Parse(JsonDocument.Parse("""{"key":{"target":"EMP_ID","source":"EMP_ID"},"pairs":[]}""").RootElement, RedeployPlan(), RedeployColumns()));
        Assert.Throws<EffectConfigException>(() =>
            DetailFieldSyncSpec.Parse(JsonDocument.Parse("""{"targetTable":"HR_EMPLOYEE","pairs":[{"column":"DEPT_ID","newField":"DEPT_ID","oldField":"OLD_DEPT_ID"}]}""").RootElement, RedeployPlan(), RedeployColumns()));
        Assert.Throws<EffectConfigException>(() =>
            DetailFieldSyncSpec.Parse(JsonDocument.Parse("""{"targetTable":"HR_EMPLOYEE","key":{"target":"EMP_ID"},"pairs":[{"column":"DEPT_ID","newField":"DEPT_ID","oldField":"OLD_DEPT_ID"}]}""").RootElement, RedeployPlan(), RedeployColumns()));
        var withoutOld = new HashSet<string>(RedeployColumns(), StringComparer.OrdinalIgnoreCase);
        withoutOld.Remove("HR_REDEPLOY_D.OLD_DEPT_ID");
        var exception = Assert.Throws<EffectConfigException>(() =>
            DetailFieldSyncSpec.Parse(JsonDocument.Parse(ParamsJson).RootElement, RedeployPlan(), withoutOld));
        Assert.Contains("OLD_DEPT_ID", exception.Message);
    }
}
