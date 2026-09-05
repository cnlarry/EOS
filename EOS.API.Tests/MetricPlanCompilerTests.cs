using EOS.API.Features.Assistant.Metrics;
using Xunit;

namespace EOS.API.Tests;

public sealed class MetricPlanCompilerTests
{
    private static MetricExpression Parse(string definition) =>
        MetricExpressionParser.Parse(definition).Expression!;

    [Fact]
    public void Compile_Simple_Aggregate_No_Predicates()
    {
        var plan = MetricPlanCompiler.Compile(
            Parse("SUM(AMOUNT_TAX)"), "COP_ORDER_D", null, [], null, [], null, null);
        Assert.Equal("SELECT SUM([AMOUNT_TAX]) FROM dbo.[COP_ORDER_D] WITH (NOLOCK);", plan.Sql);
        Assert.Empty(plan.Parameters);
    }

    [Fact]
    public void Compile_Literals_And_Distinct_Count_Are_Parameterized()
    {
        var plan = MetricPlanCompiler.Compile(
            Parse("SUM(QTY + 1.5)"), "T", null, [], null, [], null, null);
        Assert.Contains("SUM(([QTY] + @m0))", plan.Sql);
        Assert.Equal(1.5m, Assert.IsType<decimal>(plan.Parameters.Single(p => p.Name == "@m0").Value));

        var count = MetricPlanCompiler.Compile(
            Parse("COUNT(DISTINCT ORDER_NO)"), "T", null, [], null, [], null, null);
        Assert.Equal("SELECT COUNT(DISTINCT [ORDER_NO]) FROM dbo.[T] WITH (NOLOCK);", count.Sql);
    }

    [Fact]
    public void Compile_Routes_Master_Row_Filter_Through_Exists()
    {
        var rowFilter = new MetricRowFilterPlan("COP_ORDER_M", ["ORDER_TYPE", "ORDER_NO"],
            [new MetricFilterCondition("CONFIRM_TAG", "=", true)]);
        var plan = MetricPlanCompiler.Compile(
            Parse("SUM(AMOUNT_TAX)"), "COP_ORDER_D", rowFilter, [],
            "COP_ORDER_M", ["ORDER_TYPE", "ORDER_NO"], null, null);
        Assert.Equal(
            "SELECT SUM([AMOUNT_TAX]) FROM dbo.[COP_ORDER_D] WITH (NOLOCK)" +
            " WHERE EXISTS (SELECT 1 FROM dbo.[COP_ORDER_M] WITH (NOLOCK)" +
            " WHERE [ORDER_TYPE]=dbo.[COP_ORDER_D].[ORDER_TYPE] AND [ORDER_NO]=dbo.[COP_ORDER_D].[ORDER_NO]" +
            " AND [CONFIRM_TAG]=@rf0);",
            plan.Sql);
        Assert.True((bool)plan.Parameters.Single(p => p.Name == "@rf0").Value);
    }

    [Fact]
    public void Compile_Inline_Row_Filter_On_Source_Table()
    {
        var rowFilter = new MetricRowFilterPlan("COP_ORDER_D", [],
            [new MetricFilterCondition("QTY", ">", 0m)]);
        var plan = MetricPlanCompiler.Compile(
            Parse("SUM(AMOUNT_TAX)"), "COP_ORDER_D", rowFilter, [], null, [], null, null);
        Assert.Equal(
            "SELECT SUM([AMOUNT_TAX]) FROM dbo.[COP_ORDER_D] WITH (NOLOCK) WHERE [QTY]>@rf0;",
            plan.Sql);
    }

    [Fact]
    public void Compile_Dimension_Equals_And_Range()
    {
        var dims = new List<MetricDimensionFilter>
        {
            new("CLIENT_ID", MetricDimensionTarget.Source, MetricDimensionOperator.Equals, ["C001"]),
            new("ORDER_DATE", MetricDimensionTarget.Master, MetricDimensionOperator.Range,
                ["2026-01-01", "2026-03-31"]),
        };
        var plan = MetricPlanCompiler.Compile(
            Parse("SUM(AMOUNT_TAX)"), "COP_ORDER_D", null, dims,
            "COP_ORDER_M", ["ORDER_NO"], null, null);
        Assert.Contains("[CLIENT_ID]=@dim0", plan.Sql);
        Assert.Contains("([ORDER_DATE]>=@dim1 AND [ORDER_DATE]<=@dim2)", plan.Sql);
        Assert.Equal("C001", plan.Parameters.Single(p => p.Name == "@dim0").Value);
        Assert.Equal("2026-01-01", plan.Parameters.Single(p => p.Name == "@dim1").Value);
        Assert.Equal("2026-03-31", plan.Parameters.Single(p => p.Name == "@dim2").Value);
    }

    [Fact]
    public void Compile_Scope_Predicate_Goes_Into_Master_Exists_And_Renumbers_Parameters()
    {
        var plan = MetricPlanCompiler.Compile(
            Parse("SUM(AMOUNT_TAX)"), "COP_ORDER_D", null, [],
            "COP_ORDER_M", ["ORDER_NO"], "[OWNER]=@df0", ["u1"]);
        Assert.Contains("EXISTS (SELECT 1 FROM dbo.[COP_ORDER_M] WITH (NOLOCK)" +
                        " WHERE [ORDER_NO]=dbo.[COP_ORDER_D].[ORDER_NO] AND ([OWNER]=@scope0))", plan.Sql);
        Assert.Equal("u1", plan.Parameters.Single(p => p.Name == "@scope0").Value);
    }

    [Fact]
    public void Compile_Scope_On_Master_Source_Is_Inline()
    {
        var plan = MetricPlanCompiler.Compile(
            Parse("COUNT(DISTINCT EMP_ID)"), "HR_EMPLOYEE", null, [],
            "HR_EMPLOYEE", [], "[OWNER]=@df0", ["u1"]);
        Assert.Equal("SELECT COUNT(DISTINCT [EMP_ID]) FROM dbo.[HR_EMPLOYEE] WITH (NOLOCK) WHERE ([OWNER]=@scope0);",
            plan.Sql);
    }

    [Fact]
    public void Compile_Refuses_Master_Predicates_Without_Join_Keys()
    {
        var rowFilter = new MetricRowFilterPlan("COP_ORDER_M", ["ORDER_NO"],
            [new MetricFilterCondition("CONFIRM_TAG", "=", true)]);
        Assert.Throws<InvalidOperationException>(() => MetricPlanCompiler.Compile(
            Parse("SUM(AMOUNT_TAX)"), "COP_ORDER_D", rowFilter, [],
            "COP_ORDER_M", [], null, null));
    }

    [Fact]
    public void Compile_NullIf_And_Division_Shape()
    {
        var plan = MetricPlanCompiler.Compile(
            Parse("SUM(QTY) / NULLIF(SUM(IN_QTY),0)"), "INV_PRO_DEPOT", null, [], null, [], null, null);
        Assert.Equal(
            "SELECT (SUM([QTY]) / NULLIF(SUM([IN_QTY]), @m0)) FROM dbo.[INV_PRO_DEPOT] WITH (NOLOCK);",
            plan.Sql);
        Assert.Equal(0m, Assert.IsType<decimal>(plan.Parameters.Single().Value));
    }
}
