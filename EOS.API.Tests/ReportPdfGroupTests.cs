using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class ReportPdfGroupTests
{
    private static Dictionary<string, object?> Row(string proNo, decimal qty) =>
        new() { ["PRO_NO"] = proNo, ["QTY"] = qty };

    [Fact]
    public void GroupSummaries_AccumulatePerGroup()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            Row("A", 1m), Row("A", 2m), Row("B", 5m),
        };
        var summaries = ReportPdfService.BuildGroupSummaries(rows, ["PRODUCT.PRO_NO"], true, true, ["QTY"]);

        Assert.Equal(2, summaries.Count);
        Assert.Equal("A", summaries[0].Key);
        Assert.Equal(3m, summaries[0].Totals.Single(pair => pair.Column == "QTY").Total);
        Assert.Equal("B", summaries[1].Key);
        Assert.Equal(5m, summaries[1].Totals.Single(pair => pair.Column == "QTY").Total);
    }

    [Fact]
    public void GroupSummaries_HideDetail_ProducesZeroTotals()
    {
        var rows = new List<Dictionary<string, object?>> { Row("A", 9m) };
        var summaries = ReportPdfService.BuildGroupSummaries(rows, ["PRODUCT.PRO_NO"], true, false, ["QTY"]);
        Assert.Single(summaries);
        Assert.Equal(0m, summaries[0].Totals.Single(pair => pair.Column == "QTY").Total);
    }

    [Fact]
    public void GroupSummaries_NoGrouping_ReturnsEmpty()
    {
        var rows = new List<Dictionary<string, object?>> { Row("A", 9m) };
        Assert.Empty(ReportPdfService.BuildGroupSummaries(rows, [], true, true, ["QTY"]));
    }
}
