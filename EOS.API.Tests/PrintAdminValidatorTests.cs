using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class PrintAdminValidatorTests
{
    [Theory]
    [InlineData("H001")]
    [InlineData("DEFAULT")]
    [InlineData("HEADER_A-1")]
    [InlineData("_x")]
    public void ValidIds_Pass(string id)
    {
        PrintAdminValidator.ValidateId(id, "页头");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A B")]
    [InlineData("A.B")]
    [InlineData("中文")]
    [InlineData("123456789012345678901")] // 21 chars
    public void InvalidIds_Throw(string? id)
    {
        Assert.Throws<ArgumentException>(() => PrintAdminValidator.ValidateId(id, "页头"));
    }
}
