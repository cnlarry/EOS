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
