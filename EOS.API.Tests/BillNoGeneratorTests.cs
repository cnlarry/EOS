using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public sealed class BillNoGeneratorTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 10, 30, 0);

    [Theory]
    [InlineData("BJK{YYMM}0000", "BJK2608", 4)]
    [InlineData("DD{YYMM}0000", "DD2608", 4)]
    [InlineData("CGD{YYMM}0000", "CGD2608", 4)]
    [InlineData("QG{YYMMDD}000", "QG260808", 3)]
    [InlineData("YJD{YYYY}000", "YJD2026", 3)]
    [InlineData("SKD{YYMM}000", "SKD2608", 3)]
    [InlineData("YFZK{YYYY}000", "YFZK2026", 3)]
    [InlineData("CHPC00000", "CHPC", 5)]
    [InlineData("{YYYYMM}0000", "202608", 4)]
    public void BuildCode_解析表达式(string expression, string expectedTitle, int expectedWidth)
    {
        var (title, width) = BillNoGenerator.BuildCode(expression, Now);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedWidth, width);
    }

    [Fact]
    public void BuildCode_日期token大小写转换()
    {
        // 旧系统 {yyyymmdd}（小写）等价 {YYYYMMDD}：均输出 20260808
        var (title1, _) = BillNoGenerator.BuildCode("{yyyymmdd}0000", Now);
        var (title2, _) = BillNoGenerator.BuildCode("{YYYYMMDD}0000", Now);
        Assert.Equal("20260808", title1);
        Assert.Equal("20260808", title2);
    }
}

public sealed class ControlledSprocInvokerTests
{
    [Fact]
    public void BuildKeyCondition_构造主键条件并转义单引号()
    {
        var condition = ControlledSprocInvoker.BuildKeyCondition(
            new[] { "QUOTE_TYPE", "QUOTE_NO" },
            new[] { "BJK", "BJK'O8" });
        Assert.Equal("[QUOTE_TYPE]='BJK' AND [QUOTE_NO]='BJK''O8'", condition);
    }

    [Fact]
    public void BuildKeyCondition_列值与数量不一致时抛异常()
    {
        Assert.Throws<ArgumentException>(() =>
            ControlledSprocInvoker.BuildKeyCondition(new[] { "A", "B" }, new[] { "1" }));
    }

    [Theory]
    [InlineData("P_WF_COP_QUOTE", true)]
    [InlineData("P_COP_QUOTE_After_Save", true)]
    [InlineData("P_WF_PUR_PURCHASE", true)]
    [InlineData("P_UNKNOWN_SPROC", false)]
    [InlineData("DROP TABLE X", false)]
    public void 受控白名单_只允许登记过的存储过程(string sproc, bool expected)
    {
        Assert.Equal(expected, ModuleBusinessMap.IsKnownSproc(sproc));
    }
}
