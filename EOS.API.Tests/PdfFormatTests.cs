using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class PdfFormatTests
{
    [Theory]
    [InlineData(1234.5, "0.##", "1234.5")]
    [InlineData(1234.567, "0.00", "1234.57")]
    [InlineData(1234567.89, "#,##0.00", "1,234,567.89")]
    [InlineData(5, "0", "5")]
    [InlineData(5.5, "#", "6")]
    [InlineData(0.1234, "0.0000", "0.1234")]
    public void NumericFormats_Apply(object value, string format, string expected)
    {
        Assert.Equal(expected, PdfLayout.FormatValue(value, format));
    }

    [Fact]
    public void DateFormat_Applies()
    {
        var date = new DateTime(2026, 8, 14);
        Assert.Equal("2026-08-14", PdfLayout.FormatValue(date, "yyyy-MM-dd"));
        Assert.Equal("2026/08/14", PdfLayout.FormatValue(date, "yyyy/MM/dd"));
    }

    /// <summary>
    /// 非法/缺失格式串回退的是**默认显示**，不是裸 ToString：数值走与屏幕同源的
    /// 显示推导器（ADR-037 批 3），千分位与小数位跟列语义一致，打印与界面不会出现
    /// "屏幕上 1,234.5、打印出来 1234.5"这种两套口径。
    /// </summary>
    [Fact]
    public void InvalidFormat_FallsBackToDefaultDisplay()
    {
        Assert.Equal("1,234.5", PdfLayout.FormatValue(1234.5, "DISPLAY_FORMAT,SOURCE_SQL=SOURCE_SQL"));
        Assert.Equal("1,234.5", PdfLayout.FormatValue(1234.5, "abc"));
    }

    [Fact]
    public void FormatOnNonNumeric_KeepsRawText()
    {
        Assert.Equal("ABC", PdfLayout.FormatValue("ABC", "0.00"));
        Assert.Equal("1,234.5", PdfLayout.FormatValue(1234.5, null));
    }
}
