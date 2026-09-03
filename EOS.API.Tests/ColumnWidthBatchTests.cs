using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public sealed class ColumnWidthBatchTests
{
    [Fact]
    public void FilterColumnWidths_KeepsWhitelistedFieldsAndClampsWidths()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRO_NO", "QTY" };
        var input = new Dictionary<string, int>
        {
            { "PRO_NO", 48 },
            { "qty", 999 },
            { "HACK", 80 },
            { "select", 60 },
            { "BAD KEY", 70 },
        };

        var result = WorkbenchFieldMetaMapper.FilterColumnWidths(input, allowed);

        Assert.Equal(2, result.Count);
        Assert.Equal(48, result["PRO_NO"]);
        Assert.Equal(300, result["QTY"]);
        Assert.False(result.ContainsKey("HACK"));
        Assert.False(result.ContainsKey("select"));
        Assert.False(result.ContainsKey("BAD KEY"));
    }

    [Fact]
    public void FilterColumnWidths_ClampsLowerBoundToMinimum()
    {
        var result = WorkbenchFieldMetaMapper.FilterColumnWidths(
            new Dictionary<string, int> { { "F", 10 } },
            new HashSet<string> { "F" });

        Assert.Equal(40, result["F"]);
    }

    [Fact]
    public void FilterColumnWidths_EmptyInputYieldsEmptyResult()
    {
        var result = WorkbenchFieldMetaMapper.FilterColumnWidths(
            new Dictionary<string, int>(),
            new HashSet<string> { "A" });

        Assert.Empty(result);
    }
}
