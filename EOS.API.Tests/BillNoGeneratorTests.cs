using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public sealed class BillNoGeneratorTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 10, 30, 0);

    [Theory]
    [InlineData("BJK{YYMM}0000", "BJK2608", "2608", 4)]
    [InlineData("DD{YYMM}0000", "DD2608", "2608", 4)]
    [InlineData("CGD{YYMM}0000", "CGD2608", "2608", 4)]
    [InlineData("QG{YYMMDD}000", "QG260808", "260808", 3)]
    [InlineData("YJD{YYYY}000", "YJD2026", "2026", 3)]
    [InlineData("SKD{YYMM}000", "SKD2608", "2608", 3)]
    [InlineData("YFZK{YYYY}000", "YFZK2026", "2026", 3)]
    [InlineData("CHPC00000", "CHPC", "", 5)]
    [InlineData("{YYYYMM}0000", "202608", "202608", 4)]
    [InlineData("{YYMM}LLD0000", "2608LLD", "2608", 4)]
    [InlineData("KCRJ{YYYMM}0000", "KCRJ202608", "202608", 4)]
    public void Parse_解析字头日期段与流水宽度(string expression, string expectedTitle, string expectedPeriod, int expectedWidth)
    {
        var template = BillNoGenerator.Parse(expression, Now);
        Assert.Equal(expectedTitle, template.Title);
        Assert.Equal(expectedPeriod, template.Period);
        Assert.Equal(expectedWidth, template.Width);
    }

    [Fact]
    public void Parse_日期token大小写转换()
    {
        // Lowercase {yyyymmdd} is equivalent to {YYYYMMDD}: both produce 20260808
        var lower = BillNoGenerator.Parse("{yyyymmdd}0000", Now);
        var upper = BillNoGenerator.Parse("{YYYYMMDD}0000", Now);
        Assert.Equal("20260808", lower.Period);
        Assert.Equal("20260808", upper.Period);
        Assert.Equal("20260808", lower.Title);
    }

    [Fact]
    public void Parse_无日期令牌时日期段为空串()
    {
        // 无日期令牌 = 流水永不重置（切段维度退化为只有种类码）
        Assert.Equal("", BillNoGenerator.Parse("TJ000000", Now).Period);
    }

    [Fact]
    public void Parse_日期段按月切换_月变即换段()
    {
        var august = BillNoGenerator.Parse("BJK{YYMM}0000", new DateTime(2026, 8, 31, 23, 59, 0));
        var september = BillNoGenerator.Parse("BJK{YYMM}0000", new DateTime(2026, 9, 1, 0, 0, 0));
        Assert.Equal("2608", august.Period);
        Assert.Equal("2609", september.Period);
        Assert.NotEqual(august.Period, september.Period);
        Assert.NotEqual(august.Title, september.Title);
    }

    [Fact]
    public void Parse_日期段按年切换_同年同段()
    {
        // {YYYY} 只按年切段：同一年内不同月份仍共用一个计数器
        var january = BillNoGenerator.Parse("YJD{YYYY}000", new DateTime(2026, 1, 1));
        var december = BillNoGenerator.Parse("YJD{YYYY}000", new DateTime(2026, 12, 31));
        var nextYear = BillNoGenerator.Parse("YJD{YYYY}000", new DateTime(2027, 1, 1));
        Assert.Equal(january.Period, december.Period);
        Assert.NotEqual(january.Period, nextYear.Period);
    }

    [Fact]
    public void Parse_日期段按日切换()
    {
        var morning = BillNoGenerator.Parse("QG{YYMMDD}000", new DateTime(2026, 8, 8, 8, 0, 0));
        var nextDay = BillNoGenerator.Parse("QG{YYMMDD}000", new DateTime(2026, 8, 9, 8, 0, 0));
        Assert.Equal("260808", morning.Period);
        Assert.Equal("260809", nextDay.Period);
    }

    [Fact]
    public void Parse_只有月份令牌时跨年同段()
    {
        // {MM} 不含年份，跨年同月落在同一日期段（与旧系统"按字头取最大号"的语义一致）
        var thisYear = BillNoGenerator.Parse("BG{MM}0000", new DateTime(2026, 8, 8));
        var nextYear = BillNoGenerator.Parse("BG{MM}0000", new DateTime(2027, 8, 8));
        Assert.Equal(thisYear.Period, nextYear.Period);
    }

    [Fact]
    public void Parse_表达式无尾随零时宽度为0()
    {
        var template = BillNoGenerator.Parse("AD23", Now);
        Assert.Equal("AD23", template.Title);
        Assert.Equal(0, template.Width);
    }
}

public sealed class WorkbenchKeyConditionTests
{
    [Fact]
    public void Build_构造主键条件并转义单引号()
    {
        var condition = WorkbenchKeyCondition.Build(
            new[] { "QUOTE_TYPE", "QUOTE_NO" },
            new[] { "BJK", "BJK'O8" });
        Assert.Equal("[QUOTE_TYPE]='BJK' AND [QUOTE_NO]='BJK''O8'", condition);
    }

    [Fact]
    public void Build_列值与数量不一致时抛异常()
    {
        Assert.Throws<ArgumentException>(() =>
            WorkbenchKeyCondition.Build(new[] { "A", "B" }, new[] { "1" }));
    }
}
