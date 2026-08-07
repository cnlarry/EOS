using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class RightsAggregatorTests
{
    private static RightRow Row(
        string execute = "A",
        bool addNew = false,
        bool delete = false,
        bool edit = false,
        bool cost = false,
        bool secrecy = false,
        bool setup = false,
        string denyViewMaster = "",
        string denyViewDetail = "",
        string denyNewMaster = "",
        string denyNewDetail = "",
        string denyModiMaster = "",
        string denyModiDetail = "",
        string dataFilter = "") =>
        new(execute, addNew, delete, edit, cost, secrecy, setup,
            denyViewMaster, denyViewDetail, denyNewMaster, denyNewDetail,
            denyModiMaster, denyModiDetail, dataFilter);

    [Fact]
    public void NoGroups_MeansNoRights()
    {
        var rights = RightsAggregator.FromGroups([]);

        Assert.False(rights.CanBrowse);
        Assert.False(rights.CanAddNew);
        Assert.False(rights.CanEdit);
        Assert.False(rights.CanDelete);
        Assert.Empty(rights.DeniedMasterFields);
        Assert.Empty(rights.DeniedDetailFields);
        Assert.Empty(rights.DenyNewMasterFields);
        Assert.Empty(rights.DenyModiMasterFields);
        Assert.Equal(string.Empty, rights.DataFilter);
    }

    [Fact]
    public void GroupBooleanBits_AreOrAggregated()
    {
        var rights = RightsAggregator.FromGroups(
        [
            Row("B", addNew: true, cost: true),
            Row("C", edit: true, delete: true, setup: true),
        ]);

        Assert.True(rights.CanBrowse); // EXEC_TAG 取最大 = C
        Assert.True(rights.CanAddNew);
        Assert.True(rights.CanEdit);
        Assert.True(rights.CanDelete);
        Assert.True(rights.CanViewCost);
        Assert.True(rights.CanSetup);
        Assert.False(rights.CanViewSecrecy);
    }

    [Fact]
    public void ExecuteTag_UsesMaxByStringComparison()
    {
        var rights = RightsAggregator.FromGroups([Row("A"), Row("D"), Row("B")]);
        Assert.True(rights.CanBrowse);

        var none = RightsAggregator.FromGroups([Row("A"), Row("A")]);
        Assert.False(none.CanBrowse);
    }

    [Fact]
    public void DeniedFields_AreIntersectedAcrossGroups()
    {
        var rights = RightsAggregator.FromGroups(
        [
            Row(denyViewMaster: "PRO_NO;COLOR_ID", denyNewMaster: "QTY;PRICE", denyModiDetail: "CURR_ID"),
            Row(denyViewMaster: "COLOR_ID;STUFF_ID", denyNewMaster: "QTY;UNIT_ID", denyModiDetail: "CURR_ID;RATE"),
        ]);

        Assert.Equal(["COLOR_ID"], rights.DeniedMasterFields.OrderBy(field => field));
        Assert.Equal(["QTY"], rights.DenyNewMasterFields.OrderBy(field => field));
        Assert.Equal(["CURR_ID"], rights.DenyModiDetailFields.OrderBy(field => field));
        Assert.Empty(rights.DeniedDetailFields);
    }

    [Fact]
    public void DeniedFields_ParseTrimsAndIgnoresCase()
    {
        var rights = RightsAggregator.FromGroups([Row(denyViewMaster: " PRO_NO ; color_id ")]);

        Assert.Contains("pro_no", rights.DeniedMasterFields);
        Assert.Contains("COLOR_ID", rights.DeniedMasterFields);
        Assert.Equal(2, rights.DeniedMasterFields.Count);
    }

    [Fact]
    public void PersonalRights_OverrideEverything()
    {
        var rights = RightsAggregator.FromPersonal(Row(
            "B",
            addNew: true,
            cost: true,
            denyViewMaster: "A;B",
            dataFilter: " T_ID = 'X' "));

        Assert.True(rights.CanBrowse);
        Assert.True(rights.CanAddNew);
        Assert.False(rights.CanEdit);
        Assert.True(rights.CanViewCost);
        Assert.Equal(2, rights.DeniedMasterFields.Count);
        Assert.Equal("T_ID = 'X'", rights.DataFilter);
    }

    [Fact]
    public void PersonalExecuteTagA_MeansNoBrowse()
    {
        var rights = RightsAggregator.FromPersonal(Row("A", addNew: true));

        Assert.False(rights.CanBrowse);
        Assert.True(rights.CanAddNew);
    }

    [Fact]
    public void GroupDataFilters_CombinedWithAndAndRegisteredOnly()
    {
        var rights = RightsAggregator.FromGroups(
        [
            Row(dataFilter: "A=1"),
            Row(dataFilter: "B=2"),
            Row(dataFilter: ""),
        ]);

        Assert.Equal("(A=1) AND (B=2)", rights.DataFilter);

        var none = RightsAggregator.FromGroups([Row(dataFilter: "   "), Row()]);
        Assert.Equal(string.Empty, none.DataFilter);
    }
}
