using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public sealed class ValuesEqualTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, 1, false)]
    [InlineData("1", 1, true)]
    [InlineData(1, "1", true)]
    [InlineData("1.5", 1.5, true)]
    [InlineData("1.50", 1.5, true)]
    [InlineData(1.0, 1, true)]
    [InlineData(2.5, 2, false)]
    [InlineData("1", 2, false)]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData(1.5, 1.5, true)]
    [InlineData(1.5, 1.5000001, true)]
    [InlineData(10, 10, true)]
    [InlineData("2026-08-09", "2026-08-09", true)]
    public void ValuesEqual_MixedStringNumeric(object? left, object? right, bool expected)
    {
        Assert.Equal(expected, EOS.API.Data.WorkbenchSql.ValuesEqual(left, right));
    }
}
