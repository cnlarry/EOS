using EOS.API.Data;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

public class ReportConditionValueTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("A", "A", null)]
    [InlineData("A☆B", "A", "B")]
    [InlineData("A☆", "A", null)]
    [InlineData("☆B", null, "B")]
    public void UserConditionValue_SplitsRange(string? raw, string? expectedFrom, string? expectedTo)
    {
        var (from, to) = ReportRepository.SplitUserConditionValue(raw);
        Assert.Equal(expectedFrom, from);
        Assert.Equal(expectedTo, to);
    }

    [Fact]
    public void SubtotalColumn_IncludesBusinessQuantities()
    {
        Assert.True(PdfLayout.IsSubtotalColumn(new ReportColumn("QTY", "数量", "decimal"), ["PRO_NO"]));
        Assert.True(PdfLayout.IsSubtotalColumn(new ReportColumn("AMOUNT_TAX", "金额", "decimal"), ["PRO_NO"]));
        Assert.True(PdfLayout.IsSubtotalColumn(new ReportColumn("WEIGHT", "净重", "float"), ["PRO_NO"]));
    }

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "W1", "W2", "W3" };

    [Theory]
    [InlineData("W1,W3", "W1|W3")]
    [InlineData(" W1 , W2 ,", "W1|W2")]
    [InlineData("W2", "W2")]
    public void MultiSelectValues_FilterAndKeepOrder(string? raw, string expected)
    {
        var chosen = ReportRepository.FilterMultiSelectValues(raw, Allowed);
        Assert.Equal(expected.Split('|'), chosen);
    }

    [Theory]
    [InlineData("W1,HACK,W9")]   // 合法值与坏值混合：坏值丢弃
    [InlineData("HACK")]         // 纯坏值：全部丢弃
    public void MultiSelectValues_DropValuesOutOfWhitelist(string raw)
    {
        var chosen = ReportRepository.FilterMultiSelectValues(raw, Allowed);
        Assert.All(chosen, item => Assert.Contains(item, Allowed));
        Assert.DoesNotContain("HACK", chosen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(",,,")]
    public void MultiSelectValues_EmptyRaw_YieldsEmpty(string? raw)
    {
        Assert.Empty(ReportRepository.FilterMultiSelectValues(raw, Allowed));
    }

    [Fact]
    public void MultiSelectValues_Deduplicate()
    {
        Assert.Equal(["W1", "W2"], ReportRepository.FilterMultiSelectValues("W1,W2,W1", Allowed));
    }

    [Fact]
    public void SubtotalColumn_ExcludesIdentifiersAndDates()
    {
        Assert.False(PdfLayout.IsSubtotalColumn(new ReportColumn("PRO_NO", "料号", "nvarchar"), ["PRO_NO"]));
        Assert.False(PdfLayout.IsSubtotalColumn(new ReportColumn("SERIAL_NO", "序号", "int"), ["PRO_NO"]));
        Assert.False(PdfLayout.IsSubtotalColumn(new ReportColumn("PRO_DATE", "日期", "datetime"), ["PRO_NO"]));
        Assert.False(PdfLayout.IsSubtotalColumn(new ReportColumn("PRO_NO", "料号", "int"), ["PRO_NO"])); // 主键
        Assert.False(PdfLayout.IsSubtotalColumn(new ReportColumn("CHECK_TAG", "标志", "int"), ["PRO_NO"]));
    }
}
