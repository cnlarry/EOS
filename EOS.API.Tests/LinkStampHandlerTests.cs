using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit tests for the link-stamp service handler (no DB): the detail-source shape that
/// locates the target rows through the document detail's reference keys, the deapprove
/// clear semantics and the fail-closed parameter validation.
/// </summary>
public class LinkStampHandlerTests
{
    private static ModuleEffectPlan QuotePlan()
    {
        var plan = new ModuleEffectPlan(1404, "COP_QUOTE_M", "COP_QUOTE_D", "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        return plan with { MasterPkOrder = new[] { "QUOTE_TYPE", "QUOTE_NO" } };
    }

    private static ISet<string> QuoteColumns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "COP_QUOTE_M.QUOTE_TYPE", "COP_QUOTE_M.QUOTE_NO",
        "COP_QUOTE_D.QUOTE_TYPE", "COP_QUOTE_D.QUOTE_NO", "COP_QUOTE_D.SERIAL_NO",
        "COP_QUOTE_D.CHAFFER_TYPE", "COP_QUOTE_D.CHAFFER_NO", "COP_QUOTE_D.CHAFFER_SERIAL_NO",
        "COP_CHAFFER_D.CHAFFER_TYPE", "COP_CHAFFER_D.CHAFFER_NO", "COP_CHAFFER_D.SERIAL_NO",
        "COP_CHAFFER_D.QUOTE_TYPE", "COP_CHAFFER_D.QUOTE_NO", "COP_CHAFFER_D.QUOTE_SERIAL_NO",
        "COP_CHAFFER_D.FINISHED_TAG",
    };

    private const string DetailShapeJson = """
        {"targets":[{"table":"COP_CHAFFER_D","refs":["CHAFFER_TYPE","CHAFFER_NO","SERIAL_NO"],
                     "fromDetail":true,
                     "sourceRefs":["CHAFFER_TYPE","CHAFFER_NO","CHAFFER_SERIAL_NO"]}],
         "fields":["QUOTE_TYPE","QUOTE_NO",{"target":"QUOTE_SERIAL_NO","source":"SERIAL_NO"}]}
        """;

    private static LinkStampTarget DetailShapeTarget() =>
        Assert.Single(LinkStampSpec.Parse(JsonDocument.Parse(DetailShapeJson).RootElement, QuotePlan(), QuoteColumns()).Targets);

    [Fact]
    public void LinkStamp_ExplicitRefSource_LocatesThroughNonKeyMasterColumns()
    {
        // 2904 mould accept: the application row is found through the APPLY keys —
        // not the accept master's own primary key.
        var plan = new ModuleEffectPlan(2904, "MOU_ACCEPT_M", "MOU_ACCEPT_D", "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        plan = plan with { MasterPkOrder = new[] { "ACCEPT_TYPE", "ACCEPT_NO" } };
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MOU_ACCEPT_M.ACCEPT_TYPE", "MOU_ACCEPT_M.ACCEPT_NO",
            "MOU_ACCEPT_M.APPLY_TYPE", "MOU_ACCEPT_M.APPLY_NO",
            "MOU_ACCEPT_M.ACCEPT_STATE",
            "MOU_APPLY_M.APPLY_TYPE", "MOU_APPLY_M.APPLY_NO",
            "MOU_APPLY_M.ACCEPT_STATE", "MOU_APPLY_M.ACCEPT_TYPE", "MOU_APPLY_M.ACCEPT_NO",
        };
        var json = """{"targets":[{"table":"MOU_APPLY_M","refs":[{"target":"APPLY_TYPE","source":"APPLY_TYPE"},{"target":"APPLY_NO","source":"APPLY_NO"}]}],"fields":["ACCEPT_STATE","ACCEPT_TYPE","ACCEPT_NO"]}""";
        var target = Assert.Single(LinkStampSpec.Parse(JsonDocument.Parse(json).RootElement, plan, columns).Targets);
        var parameters = new List<EffectSqlParameter>();
        var sql = LinkStampHandler.BuildUpdate(plan, target, new[] { "AC", "A26090001" },
            LinkStampHandler.StampAssignments(target), parameters);
        Assert.Contains("T.[APPLY_TYPE] = M.[APPLY_TYPE]", sql);
        Assert.Contains("T.[APPLY_NO] = M.[APPLY_NO]", sql);
        // The locate join must not fall back to the accept primary key.
        Assert.DoesNotContain("ON T.[ACCEPT_TYPE] = M.[ACCEPT_TYPE]", sql);
        Assert.Contains("T.[ACCEPT_STATE] = M.[ACCEPT_STATE]", sql);
    }

    [Fact]
    public void LinkStamp_DetailSourceShape_LocatesTargetRowsByDetailReferenceKeys()
    {
        var plan = QuotePlan();
        var parameters = new List<EffectSqlParameter>();
        var target = DetailShapeTarget();
        var sql = LinkStampHandler.BuildUpdate(plan, target, new[] { "BJD", "Q26090001" },
            LinkStampHandler.StampAssignments(target), parameters);

        Assert.Contains(
            "UPDATE T SET T.[QUOTE_TYPE] = D.[QUOTE_TYPE], T.[QUOTE_NO] = D.[QUOTE_NO], "
            + "T.[QUOTE_SERIAL_NO] = D.[SERIAL_NO]", sql);
        Assert.Contains(
            "FROM dbo.[COP_CHAFFER_D] T JOIN dbo.[COP_QUOTE_D] D "
            + "ON T.[CHAFFER_TYPE] = D.[CHAFFER_TYPE] AND T.[CHAFFER_NO] = D.[CHAFFER_NO] "
            + "AND T.[SERIAL_NO] = D.[CHAFFER_SERIAL_NO]", sql);
        Assert.Contains(
            "JOIN dbo.[COP_QUOTE_M] M ON D.[QUOTE_TYPE] = M.[QUOTE_TYPE] AND D.[QUOTE_NO] = M.[QUOTE_NO]", sql);
        Assert.Contains("WHERE M.[QUOTE_TYPE] = @mk0 AND M.[QUOTE_NO] = @mk1", sql);
        Assert.Equal(new object?[] { "BJD", "Q26090001" }, parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void LinkStamp_ClearRefs_EmptiesReferenceColumnsAndResetsSerialToZero()
    {
        var plan = QuotePlan();
        var target = DetailShapeTarget();
        var assignments = LinkStampHandler.ClearAssignments(target);
        Assert.Equal(
            new[] { "T.[QUOTE_TYPE] = ''", "T.[QUOTE_NO] = ''", "T.[QUOTE_SERIAL_NO] = 0" },
            assignments);

        var sql = LinkStampHandler.BuildUpdate(plan, target, new[] { "BJD", "Q26090001" },
            assignments, new List<EffectSqlParameter>());
        // 解批清引用沿用批核时的同一套定位键，不做全表清空
        Assert.Contains("T.[QUOTE_SERIAL_NO] = 0 FROM dbo.[COP_CHAFFER_D] T JOIN dbo.[COP_QUOTE_D] D", sql);
        Assert.Contains("WHERE M.[QUOTE_TYPE] = @mk0", sql);
    }

    [Fact]
    public void LinkStamp_ReverseKind_AcceptsClearRefsAndNoReverse_RejectsAnythingElse()
    {
        Assert.Equal("clear-refs",
            LinkStampHandler.RequireReverseKind(JsonDocument.Parse("""{"kind":"clear-refs"}""").RootElement));
        Assert.Equal("no-reverse",
            LinkStampHandler.RequireReverseKind(JsonDocument.Parse("""{"kind":"no-reverse"}""").RootElement));
        Assert.Throws<EffectConfigException>(
            () => LinkStampHandler.RequireReverseKind(JsonDocument.Parse("""{"kind":"recompute"}""").RootElement));
        Assert.Throws<EffectConfigException>(() => LinkStampHandler.RequireReverseKind(null));
    }

    [Fact]
    public void LinkStamp_DetailSourceShape_RejectsBrokenConfiguration()
    {
        var plan = QuotePlan();
        var columns = QuoteColumns();

        Assert.Throws<EffectConfigException>(() => LinkStampSpec.Parse(JsonDocument.Parse(
            """
            {"targets":[{"table":"COP_CHAFFER_D","refs":["CHAFFER_TYPE","CHAFFER_NO","SERIAL_NO"],
                         "fromDetail":true,"sourceRefs":["CHAFFER_TYPE","CHAFFER_NO"]}],
             "fields":["QUOTE_NO"]}
            """).RootElement, plan, columns));

        Assert.Throws<EffectConfigException>(() => LinkStampSpec.Parse(JsonDocument.Parse(
            """
            {"targets":[{"table":"COP_CHAFFER_D","refs":["CHAFFER_TYPE","CHAFFER_NO","SERIAL_NO"],"fromDetail":true}],
             "fields":["QUOTE_NO"]}
            """).RootElement, plan, columns));

        Assert.Throws<EffectConfigException>(() => LinkStampSpec.Parse(JsonDocument.Parse(
            """
            {"targets":[{"table":"COP_CHAFFER_D","refs":["CHAFFER_TYPE","CHAFFER_NO","SERIAL_NO"],
                         "fromDetail":true,"sourceRefs":["CHAFFER_TYPE","CHAFFER_NO","QUOTE_SERIAL_NO"]}],
             "fields":["QUOTE_NO"]}
            """).RootElement, plan, columns));

        Assert.Throws<EffectConfigException>(() => LinkStampSpec.Parse(JsonDocument.Parse(
            """
            {"targets":[{"table":"COP_CHAFFER_D","refs":["CHAFFER_TYPE","CHAFFER_NO","SERIAL_NO"],
                         "fromDetail":true,"sourceRefs":["CHAFFER_TYPE","CHAFFER_NO","CHAFFER_SERIAL_NO"]}],
             "fields":["QUOTE_NO","QUOTE_MISSING"]}
            """).RootElement, plan, columns));

        Assert.Throws<EffectConfigException>(() => LinkStampSpec.Parse(JsonDocument.Parse(
            """{"targets":[{"table":"COP_CHAFFER_D","fromDetail":true}],"fields":["QUOTE_NO"]}""").RootElement,
            plan, columns));

        Assert.Throws<EffectConfigException>(() => LinkStampSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"COP_CHAFFER_D"}""").RootElement, plan, columns));
    }

    [Fact]
    public void LinkStamp_MasterShape_StampsFromMasterAndStaysInsideTheDocument()
    {
        var plan = QuotePlan();
        var target = Assert.Single(LinkStampSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"COP_CHAFFER_D","fields":["QUOTE_TYPE","QUOTE_NO"]}""").RootElement,
            plan, QuoteColumns()).Targets);
        var parameters = new List<EffectSqlParameter>();
        var sql = LinkStampHandler.BuildUpdate(plan, target, new[] { "BJD", "Q26090001" },
            LinkStampHandler.StampAssignments(target), parameters);

        Assert.Equal(
            "UPDATE T SET T.[QUOTE_TYPE] = M.[QUOTE_TYPE], T.[QUOTE_NO] = M.[QUOTE_NO] "
            + "FROM dbo.[COP_CHAFFER_D] T JOIN dbo.[COP_QUOTE_M] M "
            + "ON T.[QUOTE_TYPE] = M.[QUOTE_TYPE] AND T.[QUOTE_NO] = M.[QUOTE_NO] "
            + "WHERE M.[QUOTE_TYPE] = @mk0 AND M.[QUOTE_NO] = @mk1",
            sql);
    }

    [Fact]
    public void LinkStamp_Finish_AddsFinishedTagOnTheTarget()
    {
        var plan = QuotePlan();
        var target = Assert.Single(LinkStampSpec.Parse(JsonDocument.Parse(
            """
            {"targets":[{"table":"COP_CHAFFER_D","ref":["QUOTE_TYPE","QUOTE_NO"]}],
             "fields":["QUOTE_NO"],"finish":true}
            """).RootElement, plan, QuoteColumns()).Targets);
        Assert.Equal(
            new[]
            {
                "T.[QUOTE_NO] = M.[QUOTE_NO]",
                "T.[FINISHED_TAG] = 1",
                "T.[FINISHED_PERSON] = 'SYSTEM'",
                "T.[FINISHED_DATE] = SYSDATETIME()",
            },
            LinkStampHandler.StampAssignments(target));
    }
}
