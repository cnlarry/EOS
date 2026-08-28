using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class ReportAdminValidatorTests
{
    [Theory]
    [InlineData("Product_List")]
    [InlineData("RPT-01")]
    [InlineData("A")]
    [InlineData("A.B")]
    [InlineData("INV_Occur_In_List.")]
    public void ValidReportIds_Pass(string id) => ReportAdminValidator.ValidateReportId(id);

    [Theory]
    [InlineData("")]
    [InlineData("A B")]
    [InlineData("中文")]
    public void InvalidReportIds_Throw(string id)
    {
        Assert.Throws<ArgumentException>(() => ReportAdminValidator.ValidateReportId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a4")]
    [InlineData("A3")]
    [InlineData("LETTER")]
    public void Papers_Pass(string? paper) => ReportAdminValidator.ValidatePaper(paper);

    [Theory]
    [InlineData("B5")]
    [InlineData("A2")]
    public void InvalidPapers_Throw(string paper)
    {
        Assert.Throws<ArgumentException>(() => ReportAdminValidator.ValidatePaper(paper));
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
