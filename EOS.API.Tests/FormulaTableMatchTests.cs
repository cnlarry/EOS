using EOS.API.Data.Effects;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// TABLE-scoped formula location keys: the physical source table comes from the
/// formula row's SOURCE_TABLE (translation entries omit an inline table), and the
/// EXISTS must be scoped to the current document through the module master key —
/// never a global table scan. Regression for 1615/1616 produce-apply rows.
/// </summary>
public class FormulaTableMatchTests
{
    private static ModuleEffectPlan Plan() => new(
        1615, "PUR_APPLY_M", "PUR_APPLY_D", "v-test",
        new[] { "APPLY_TYPE", "APPLY_NO" },
        System.Array.Empty<EffectActionPlan>(), System.Array.Empty<EffectValidationPlan>());

    private static EffectMatchItem[] Match() => new[]
    {
        new EffectMatchItem("PRODUCE_TYPE", new EffectSourceRef("TABLE", null, "PRODUCE_TYPE", null)),
        new EffectMatchItem("PRODUCE_NO", new EffectSourceRef("TABLE", null, "PRODUCE_NO", null)),
        new EffectMatchItem("SERIAL_NO", new EffectSourceRef("TABLE", null, "PRODUCE_SERIAL_NO", null)),
    };

    [Fact]
    public void Table_match_resolves_source_table_from_op_and_scopes_to_document()
    {
        var op = new EffectOpPlan(1, "MOC_PRODUCE_D", "APPLY_QTY", "ACCUM",
            new EffectSourceRef("TABLE", "PUR_APPLY_MORE", "QTY", null), null, null,
            Match(), null, null);
        var (sql, _) = new EffectFormulaExecutor().BuildUpdate(op, Plan(), new[] { "T", "N" });
        Assert.Contains("PUR_APPLY_MORE", sql);
        Assert.Contains("JOIN dbo.[PUR_APPLY_M] M", sql);
        Assert.Contains("M.[APPLY_TYPE] = @", sql);
        Assert.DoesNotContain("WHERE 1=1", sql);
    }

    [Fact]
    public void Table_match_without_source_table_fails_closed()
    {
        var op = new EffectOpPlan(1, "MOC_PRODUCE_D", "APPLY_QTY", "ACCUM",
            new EffectSourceRef("TABLE", null, "QTY", null), null, null,
            new[] { new EffectMatchItem("PRODUCE_TYPE", new EffectSourceRef("TABLE", null, "PRODUCE_TYPE", null)) },
            null, null);
        var ex = Assert.Throws<EffectConfigException>(() =>
            new EffectFormulaExecutor().BuildUpdate(op, Plan(), new[] { "T", "N" }));
        Assert.Contains("SOURCE_TABLE", ex.Message);
    }
}