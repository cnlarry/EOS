using EOS.API.Features.Assistant.Metrics;
using Xunit;

namespace EOS.API.Tests;

public sealed class MetricExpressionParserTests
{
    [Theory]
    [InlineData("SUM(AMOUNT_TAX)")]
    [InlineData("sum(amount_tax)")]
    [InlineData("SUM(AMOUNT_TAX - COST_AMOUNT)")]
    [InlineData("SUM(QTY) / NULLIF(SUM(IN_QTY),0)")]
    [InlineData("COUNT(DISTINCT ORDER_NO)")]
    [InlineData("SUM(A + B * 2)")]
    [InlineData("(SUM(QTY) - SUM(OUT_QTY)) / NULLIF(SUM(QTY), 0)")]
    [InlineData("MIN(QTY)")]
    [InlineData("AVG(AMOUNT / 1.5)")]
    public void Parse_Accepts_Controlled_Subset(string definition)
    {
        var result = MetricExpressionParser.Parse(definition);
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.Expression);
    }

    [Fact]
    public void Parse_Produces_Typed_Ast()
    {
        var result = MetricExpressionParser.Parse("SUM(QTY) / NULLIF(SUM(IN_QTY),0)");
        var binary = Assert.IsType<MetricBinary>(result.Expression);
        Assert.Equal('/', binary.Operator);
        Assert.IsType<MetricAggregate>(binary.Left);
        var nullIf = Assert.IsType<MetricNullIf>(binary.Right);
        var inner = Assert.IsType<MetricAggregate>(nullIf.Left);
        Assert.Equal("SUM", inner.Function);
        Assert.IsType<MetricColumn>(inner.Argument);
    }

    [Fact]
    public void Parse_Normalizes_Identifiers_To_UpperCase()
    {
        var result = MetricExpressionParser.Parse("sum(qty)");
        var aggregate = Assert.IsType<MetricAggregate>(result.Expression);
        var column = Assert.IsType<MetricColumn>(aggregate.Argument);
        Assert.Equal("QTY", column.Name);
    }

    [Theory]
    [InlineData("SELECT 1 FROM T", "多余内容")]
    [InlineData("LEN(AMOUNT_TAX)", "多余内容")]
    [InlineData("SUM(SELECT AMOUNT_TAX FROM X)", "右括号")]
    [InlineData("SUM(AMOUNT_TAX) -- comment", "语法")]
    [InlineData("SUM(AMOUNT_TAX) WHERE X=1", "字符")]
    [InlineData("SUM(SUM(QTY))", "嵌套")]
    [InlineData("SUM(DISTINCT QTY)", "DISTINCT")]
    [InlineData("MIN(DISTINCT QTY)", "DISTINCT")]
    [InlineData("SUM(AMOUNT_TAX", "括号")]
    [InlineData("COUNT(*)", "语法")]
    [InlineData("SUM('abc')", "字符")]
    [InlineData("SUM(A.B)", "字符")]
    [InlineData("SUM(1 +)", "语法")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parse_Rejects_Out_Of_Subset(string? definition, string? expectedFragment)
    {
        var result = MetricExpressionParser.Parse(definition);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        if (expectedFragment is not null)
        {
            Assert.Contains(expectedFragment, result.Error);
        }
    }

    [Fact]
    public void Parse_Rejects_Overlong_Definition()
    {
        var definition = "SUM(" + new string('A', MetricExpressionParser.MaxLength) + ")";
        var result = MetricExpressionParser.Parse(definition);
        Assert.False(result.Ok);
        Assert.Contains("上限", result.Error);
    }
}
