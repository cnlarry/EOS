using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class ReportAdminValidatorTests
{
    [Theory]
    [InlineData("Product_List")]
    [InlineData("RPT-01")]
    [InlineData("INV_Occur_In_List")]
    [InlineData("abc")]
    public void ValidReportIds_Pass(string id) => ReportAdminValidator.ValidateReportId(id);

    [Theory]
    [InlineData("")]
    [InlineData("A B")]
    [InlineData("中文")]
    // 规范收紧的边界（每一条都对应一类真实事故）：
    [InlineData("A")]                    // 太短：与"随手占位"的临时编号无法区分
    [InlineData("AB")]                   // 太短
    [InlineData("A.B")]                  // 点号：URL/文件名里必须转义，尾点号还会被 Windows 吞掉
    [InlineData("INV_Occur_In_List.")]   // 尾点号（存量里真实存在过，已按 275 清理）
    public void InvalidReportIds_Throw(string id)
    {
        Assert.Throws<ArgumentException>(() => ReportAdminValidator.ValidateReportId(id));
    }

    [Fact]
    public void ReportId_Over40Chars_Throws()
    {
        // 列宽是 nchar(50)，原先放行到 100 位：超长编号会被静默截断成另一个编号，而编号是身份。
        var tooLong = new string('A', 41);
        Assert.Throws<ArgumentException>(() => ReportAdminValidator.ValidateReportId(tooLong));
        ReportAdminValidator.ValidateReportId(new string('A', 40));
    }

    [Fact]
    public void FieldList_AcceptsWhitelistTokens()
    {
        var tokens = ReportAdminValidator.ValidateFieldList(
            "PRODUCT.PRO_NO, PRODUCT.PRO_NAME ", "SORT_FIELDS");
        Assert.Equal(["PRODUCT.PRO_NO", "PRODUCT.PRO_NAME"], tokens);
    }

    [Theory]
    [InlineData("PRODUCT PRO_NO")]
    [InlineData("PRODUCT..PRO_NO")]
    [InlineData("PRODUCT.PRO_NO;COLOR_ID")]
    [InlineData("PRODUCT.PRO_NO,1ABC.X")]
    public void FieldList_RejectsInvalidTokens(string raw)
    {
        Assert.Throws<ArgumentException>(() => ReportAdminValidator.ValidateFieldList(raw, "SORT_FIELDS"));
    }

    [Fact]
    public void Logo_PngMagic_Allowed()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(ReportAdminValidator.IsAllowedLogo(png, "logo.png", out var reason), reason);
    }

    [Theory]
    [InlineData("logo.txt")]
    [InlineData("logo.exe")]
    public void Logo_WrongExtension_Rejected(string fileName)
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.False(ReportAdminValidator.IsAllowedLogo(png, fileName, out _));
    }

    [Fact]
    public void Logo_NonImageContent_Rejected()
    {
        byte[] text = "not an image"u8.ToArray();
        Assert.False(ReportAdminValidator.IsAllowedLogo(text, "logo.png", out _));
    }

    [Fact]
    public void Logo_TooLarge_Rejected()
    {
        var bytes = new byte[2 * 1024 * 1024 + 1];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        Assert.False(ReportAdminValidator.IsAllowedLogo(bytes, "logo.png", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(32768)]
    public void SerialNo_OutOfRange_Throws(int serialNo)
    {
        Assert.Throws<ArgumentException>(() => ReportAdminValidator.ValidateSerialNo(serialNo));
    }
}
