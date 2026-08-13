using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class ReportRightsAggregatorTests
{
    private static ReportRightRow Row(bool preview = false, bool print = false, bool export = false, string dataFilter = "") =>
        new(preview, print, export, dataFilter);

    [Fact]
    public void NoRows_MeansNoRights()
    {
        var rights = ReportRightsAggregator.FromGroups([]);
        Assert.False(rights.CanPreview);
        Assert.False(rights.CanPrint);
        Assert.False(rights.CanExport);
        Assert.Equal(string.Empty, rights.DataFilter);
    }

    [Fact]
    public void GroupFlags_AreOrAggregated()
    {
        var rights = ReportRightsAggregator.FromGroups(
        [
            Row(preview: true, export: true),
            Row(print: true),
        ]);
        Assert.True(rights.CanPreview);
        Assert.True(rights.CanPrint);
        Assert.True(rights.CanExport);
    }

    [Fact]
    public void GroupDataFilters_CombinedWithOr()
    {
        var rights = ReportRightsAggregator.FromGroups(
        [
            Row(dataFilter: "A=1"),
            Row(dataFilter: "B=2"),
            Row(),
        ]);
        Assert.Equal("(A=1) OR (B=2)", rights.DataFilter);
    }

    [Fact]
    public void PersonalRow_OverridesGroups()
    {
        var personal = ReportRightsAggregator.FromPersonal(Row(preview: false, print: true, dataFilter: "X=1"));
        Assert.False(personal.CanPreview);
        Assert.True(personal.CanPrint);
        Assert.False(personal.CanExport);
        Assert.Equal("X=1", personal.DataFilter);
    }
}
