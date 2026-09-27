using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit (no DB) tests for the hr-usage-sync service handler (180206 month scope
/// and 180207 date scope sharing one key): fail-closed parameter validation,
/// the per-employee SUM aggregation (decision H1, not the baseline emp-only join
/// overwrite), and the month/date target location shapes.
/// </summary>
public class HrUsageSyncHandlerTests
{
    private static ModuleEffectPlan EnactmentPlan() => new(
        180206, "HR_APPLY_M", "HR_APPLY_D", "v-test",
        new[] { "APPLY_TYPE", "APPLY_NO" },
        Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

    private static ModuleEffectPlan ApplyPlan() => new(
        180207, "HR_WORKTIME_M", "HR_WORKTIME_D", "v-test",
        new[] { "WORKTIME_TYPE", "WORKTIME_NO" },
        Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

    private static ISet<string> EnactmentColumns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "HR_ENACTMENT_D", "HR_ENACTMENT_D.EMP_ID",
        "HR_ENACTMENT_D.USED_WORKTIME", "HR_ENACTMENT_D.USED_OVERTIME",
        "HR_ENACTMENT_D.USED_REST_OVERTIME", "HR_ENACTMENT_D.USED_HOLIDAY_OVERTIME",
        "HR_ENACTMENT_M", "HR_ENACTMENT_M.ENACTMENT_TYPE", "HR_ENACTMENT_M.ENACTMENT_NO",
        "HR_ENACTMENT_M.COUNT_MONTH",
        "HR_APPLY_M", "HR_APPLY_M.APPLY_TYPE", "HR_APPLY_M.APPLY_NO", "HR_APPLY_M.COUNT_DATE",
        "HR_APPLY_D", "HR_APPLY_D.APPLY_TYPE", "HR_APPLY_D.APPLY_NO", "HR_APPLY_D.EMP_ID",
        "HR_APPLY_D.WORKTIME", "HR_APPLY_D.OVERTIME", "HR_APPLY_D.REST_OVERTIME", "HR_APPLY_D.HOLIDAY_OVERTIME",
    };

    private static ISet<string> WorktimeColumns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "HR_APPLY_D", "HR_APPLY_D.EMP_ID",
        "HR_APPLY_D.USED_WORKTIME", "HR_APPLY_D.USED_OVERTIME",
        "HR_APPLY_D.USED_REST_OVERTIME", "HR_APPLY_D.USED_HOLIDAY_OVERTIME",
        "HR_APPLY_M", "HR_APPLY_M.APPLY_TYPE", "HR_APPLY_M.APPLY_NO", "HR_APPLY_M.COUNT_DATE",
        "HR_WORKTIME_M", "HR_WORKTIME_M.WORKTIME_TYPE", "HR_WORKTIME_M.WORKTIME_NO", "HR_WORKTIME_M.COUNT_DATE",
        "HR_WORKTIME_D", "HR_WORKTIME_D.WORKTIME_TYPE", "HR_WORKTIME_D.WORKTIME_NO", "HR_WORKTIME_D.EMP_ID",
        "HR_WORKTIME_D.WORKTIME", "HR_WORKTIME_D.OVERTIME",
        "HR_WORKTIME_D.REST_OVERTIME", "HR_WORKTIME_D.HOLIDAY_OVERTIME",
    };

    private const string EnactmentParamsJson =
        """{"targetTable":"HR_ENACTMENT_D","targetFields":["USED_WORKTIME","USED_OVERTIME","USED_REST_OVERTIME","USED_HOLIDAY_OVERTIME"],"sourceFields":["WORKTIME","OVERTIME","REST_OVERTIME","HOLIDAY_OVERTIME"],"scopeKey":{"emp":"EMP_ID","month":"COUNT_MONTH"}}""";

    private const string ApplyParamsJson =
        """{"targetTable":"HR_APPLY_D","targetFields":["USED_WORKTIME","USED_OVERTIME","USED_REST_OVERTIME","USED_HOLIDAY_OVERTIME"],"sourceFields":["WORKTIME","OVERTIME","REST_OVERTIME","HOLIDAY_OVERTIME"],"scopeKey":{"emp":"EMP_ID","date":"COUNT_DATE"}}""";

    [Fact]
    public void EffectKey_RegisteredAsService()
    {
        Assert.Equal("hr-usage-sync", new HrUsageSyncHandler().EffectKey);
        Assert.True(EffectRegistry.IsImplemented("hr-usage-sync"));
    }

    [Fact]
    public void Parse_AcceptsBothLandedInstances()
    {
        var month = HrUsageSyncSpec.Parse(
            JsonDocument.Parse(EnactmentParamsJson).RootElement, EnactmentPlan(), EnactmentColumns());
        Assert.True(month.ByMonth);
        Assert.Equal("HR_ENACTMENT_D", month.TargetTable, ignoreCase: true);
        Assert.Equal(4, month.Pairs.Count);
        var date = HrUsageSyncSpec.Parse(
            JsonDocument.Parse(ApplyParamsJson).RootElement, ApplyPlan(), WorktimeColumns());
        Assert.False(date.ByMonth);
        Assert.Equal("HR_APPLY_D", date.TargetTable, ignoreCase: true);
    }

    [Fact]
    public void Parse_RejectsBadConfigurations()
    {
        var plan = EnactmentPlan();
        var columns = EnactmentColumns();
        // Unknown target table.
        Assert.Throws<EffectConfigException>(() => HrUsageSyncSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"HR_ENACTMENT_M","targetFields":["USED_WORKTIME","USED_OVERTIME","USED_REST_OVERTIME","USED_HOLIDAY_OVERTIME"],"sourceFields":["WORKTIME","OVERTIME","REST_OVERTIME","HOLIDAY_OVERTIME"],"scopeKey":{"emp":"EMP_ID","month":"COUNT_MONTH"}}""").RootElement, plan, columns));
        // Field count must be four pairs.
        Assert.Throws<EffectConfigException>(() => HrUsageSyncSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"HR_ENACTMENT_D","targetFields":["USED_WORKTIME"],"sourceFields":["WORKTIME"],"scopeKey":{"emp":"EMP_ID","month":"COUNT_MONTH"}}""").RootElement, plan, columns));
        // Month scope on a date target (and vice versa) is rejected.
        Assert.Throws<EffectConfigException>(() => HrUsageSyncSpec.Parse(JsonDocument.Parse(
            ApplyParamsJson).RootElement, plan, columns));
        Assert.Throws<EffectConfigException>(() => HrUsageSyncSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"HR_ENACTMENT_D","targetFields":["USED_WORKTIME","USED_OVERTIME","USED_REST_OVERTIME","USED_HOLIDAY_OVERTIME"],"sourceFields":["WORKTIME","OVERTIME","REST_OVERTIME","HOLIDAY_OVERTIME"],"scopeKey":{"emp":"EMP_ID","date":"COUNT_DATE"}}""").RootElement, plan, columns));
        // Non-EMP_ID employee key is rejected.
        Assert.Throws<EffectConfigException>(() => HrUsageSyncSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"HR_ENACTMENT_D","targetFields":["USED_WORKTIME","USED_OVERTIME","USED_REST_OVERTIME","USED_HOLIDAY_OVERTIME"],"sourceFields":["WORKTIME","OVERTIME","REST_OVERTIME","HOLIDAY_OVERTIME"],"scopeKey":{"emp":"DEPT_ID","month":"COUNT_MONTH"}}""").RootElement, plan, columns));
        // Missing physical objects fail closed.
        Assert.Throws<EffectConfigException>(() => HrUsageSyncSpec.Parse(JsonDocument.Parse(
            EnactmentParamsJson).RootElement, plan,
            new HashSet<string>(columns.Where(item => item != "HR_ENACTMENT_M.COUNT_MONTH"), StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void BuildUpdate_MonthScope_SumsPerEmployeeWithinMonth()
    {
        var plan = EnactmentPlan();
        var spec = HrUsageSyncSpec.Parse(
            JsonDocument.Parse(EnactmentParamsJson).RootElement, plan, EnactmentColumns());
        var parameters = new List<EffectSqlParameter>();
        var sql = HrUsageSyncHandler.BuildUpdate(spec, plan, new[] { "JBSQ", "JBSQ202608001" }, parameters);

        // H1: per-employee SUM (not the baseline emp-only join overwrite).
        Assert.Contains("SUM(ISNULL(D.[WORKTIME], 0))", sql);
        Assert.Contains("GROUP BY D.[EMP_ID]", sql);
        // Month location via the target master month string.
        Assert.Contains("MM.[COUNT_MONTH] = CONVERT(varchar(6), @cd, 112)", sql);
        Assert.Contains("FROM dbo.[HR_ENACTMENT_D] T", sql);
        Assert.Contains("JOIN dbo.[HR_ENACTMENT_M] MM", sql);
        // Signed accumulation; keys travel as parameters.
        Assert.Contains("ISNULL(T.[USED_WORKTIME], 0) + s.[w1] * @sign", sql);
        Assert.Contains("M.[APPLY_TYPE] = @mk0 AND M.[APPLY_NO] = @mk1", sql);
        Assert.Equal(new object?[] { "JBSQ", "JBSQ202608001" }, parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void BuildUpdate_DateScope_SumsPerEmployeeOnExactDate()
    {
        var plan = ApplyPlan();
        var spec = HrUsageSyncSpec.Parse(
            JsonDocument.Parse(ApplyParamsJson).RootElement, plan, WorktimeColumns());
        var parameters = new List<EffectSqlParameter>();
        var sql = HrUsageSyncHandler.BuildUpdate(spec, plan, new[] { "GS", "GS202608001" }, parameters);

        Assert.Contains("SUM(ISNULL(D.[OVERTIME], 0))", sql);
        Assert.Contains("GROUP BY D.[EMP_ID]", sql);
        Assert.Contains("MM.[COUNT_DATE] = @cd", sql);
        Assert.Contains("FROM dbo.[HR_APPLY_D] T", sql);
        Assert.Contains("M.[WORKTIME_TYPE] = @mk0 AND M.[WORKTIME_NO] = @mk1", sql);
    }
}
