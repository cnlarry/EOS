using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>Unit tests for the effect plan loader, condition compiler and formula SQL builder (no DB).</summary>
public class EffectEngineTests
{
    private static WorkbenchDefinition Definition(JsonElement businessActions) => new(
        1607, "收料单", "PUR_RECEIVE_M", "PUR_RECEIVE_D",
        Array.Empty<WorkbenchField>(), Array.Empty<WorkbenchField>(),
        null, true, true, false, Array.Empty<string>(), string.Empty,
        HasWorkflow: false, DefinitionVersion: "module-1607-v1",
        BusinessActions: businessActions);

    private static JsonElement ActionsJson(params string[] items) =>
        JsonDocument.Parse("[" + string.Join(",", items) + "]").RootElement.Clone();

    [Fact]
    public void Load_rejects_unknown_effect_key()
    {
        var loader = new EffectPlanLoader();
        var json = ActionsJson(
            """{"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"not-in-catalog"}""");
        Assert.Throws<EffectConfigException>(() => loader.Load(Definition(json)));
    }

    [Fact]
    public void Load_rejects_unknown_op_code()
    {
        var loader = new EffectPlanLoader();
        var json = ActionsJson(
            """{"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"field-accumulate","ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"IN_BUY_QTY","opCode":"MULTIPLY","sourceScope":"DETAIL","sourceField":"QTY"}]}""");
        Assert.Throws<EffectConfigException>(() => loader.Load(Definition(json)));
    }

    [Fact]
    public void Load_accepts_accum_with_terms_and_reverse_kind()
    {
        var loader = new EffectPlanLoader();
        var json = ActionsJson(
            """{"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"adjust-projection","reverse":{"kind":"auto-reverse","note":"x"},"ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"IN_BUY_QTY","opCode":"DEACCUM","sourceScope":"DETAIL","sourceTerms":[{"field":"QTY","coef":1},{"field":"SPARE_QTY","coef":1}],"match":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]}]}""");
        var plan = loader.Load(Definition(json));
        var action = Assert.Single(plan.Actions);
        var op = Assert.Single(action.Ops);
        Assert.Equal(2, op.Terms!.Count);
        Assert.Equal("DEACCUM", op.OpCode);
    }

    [Fact]
    public void Formula_builds_accum_with_terms_subquery_and_match_exists()
    {
        var executor = new EffectFormulaExecutor();
        var op = new EffectOpPlan(
            1, "PRODUCT", "IN_BUY_QTY", "ACCUM",
            new EffectSourceRef("DETAIL", null, null, null),
            null,
            new[] { new EffectTerm("QTY", 1), new EffectTerm("SPARE_QTY", 1) },
            new[] { new EffectMatchItem("PRO_NO", new EffectSourceRef("DETAIL", null, "PRO_NO", null)) },
            null, null);
        var plan = new ModuleEffectPlan(1607, "PUR_RECEIVE_M", "PUR_RECEIVE_D", "v1",
            Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        var (sql, _) = executor.BuildUpdate(op, plan);
        Assert.Contains("UPDATE T SET T.[IN_BUY_QTY] = ISNULL(T.[IN_BUY_QTY], 0) + (", sql);
        Assert.Contains("(SELECT SUM(ISNULL(D.[QTY], 0) + ISNULL(D.[SPARE_QTY], 0)) FROM dbo.[PUR_RECEIVE_D] D", sql);
        Assert.Contains("EXISTS (SELECT 1 FROM dbo.[PUR_RECEIVE_D] D WHERE D.[PRO_NO] = T.[PRO_NO])", sql);
    }

    [Fact]
    public void Reverse_swaps_accum_and_deaccum_on_deapprove()
    {
        var op = new EffectOpPlan(1, "PRODUCT", "F", "ACCUM",
            new EffectSourceRef("DETAIL", null, "QTY", null), null, null, null, null, null);
        var reversed = EffectFormulaExecutor.ResolveOpForEvent(op, EffectEvent.Deapprove, null);
        Assert.Equal("DEACCUM", reversed!.OpCode);
        var kept = EffectFormulaExecutor.ResolveOpForEvent(
            op, EffectEvent.Deapprove, JsonSerializer.SerializeToElement(new { kind = "recompute" }));
        Assert.Equal("ACCUM", kept!.OpCode);
        var dropped = EffectFormulaExecutor.ResolveOpForEvent(
            op, EffectEvent.Deapprove, JsonSerializer.SerializeToElement(new { kind = "no-reverse" }));
        Assert.Null(dropped);
    }

    [Fact]
    public void Condition_compiler_rejects_expression_in_string_value()
    {
        var compiler = new EffectConditionCompiler();
        var condition = JsonSerializer.SerializeToElement(new
        {
            logic = "AND",
            items = new object[] { new { type = "field-compare", left = new { scope = "TARGET", field = "A" }, op = "GE", right = new { value = "QTY+1" } } },
        });
        var fragment = compiler.Compile(condition, (_, _) => "T", _ => true);
        Assert.Contains("@cp0", fragment.Sql);
        Assert.Equal("QTY+1", fragment.Parameters[0].Value);
    }

    [Fact]
    public void Condition_compiler_builds_switch_predicate()
    {
        var compiler = new EffectConditionCompiler();
        var condition = JsonSerializer.SerializeToElement(new
        {
            logic = "AND",
            items = new object[] { new { type = "switch", key = "PRO_MRP", value = true } },
        });
        var fragment = compiler.Compile(condition, (_, _) => null, column => column == "PRO_MRP");
        Assert.Contains("dbo.SYSSS", fragment.Sql);
        Assert.Contains("= 1", fragment.Sql);
    }
}
