using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class DocumentLayoutProfileTests
{
    [Fact]
    public void AllBuiltinLayoutProfiles_AreRegistered()
    {
        Assert.Equal(24, DocumentLayoutProfiles.All.Count);
        Assert.Contains(1406, DocumentLayoutProfiles.All.Keys); // 送货单
        Assert.Contains(1404, DocumentLayoutProfiles.All.Keys); // 报价单
        Assert.Contains(1405, DocumentLayoutProfiles.All.Keys); // 客户订单
    }

    [Fact]
    public void DeliveryProfile_HasAmountField()
    {
        var profile = DocumentLayoutProfiles.All[1406];
        Assert.Equal("AMOUNT_TAX", profile.AmountField);
        Assert.Contains("SEND_NO", profile.NoFields);
        Assert.Equal("SEND_DATE", profile.DateField);
    }

    [Theory]
    [InlineData("REMARK", true)]
    [InlineData("PRO_REMARK", true)]
    [InlineData("NOTE", true)]
    [InlineData("PRO_NO", false)]
    [InlineData("QTY", false)]
    public void RemarkFieldDetection(string key, bool expected)
    {
        Assert.Equal(expected, PdfLayout.IsRemarkField(key));
    }
}
