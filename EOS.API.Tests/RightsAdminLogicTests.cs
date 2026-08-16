using EOS.API.Data;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

public class RightsAdminLogicTests
{
    private static ModuleRightsInput Module(
        int moduleId = 2306,
        string? execTag = null,
        bool addNew = false,
        bool edit = false,
        bool delete = false,
        bool approve = false,
        bool deapprove = false,
        bool report = false,
        bool cost = false,
        bool setup = false,
        bool secrecy = false,
        bool endCase = false,
        bool unEndCase = false,
        bool other1 = false,
        bool other2 = false,
        bool other3 = false,
        bool other4 = false,
        bool fileView = false,
        bool fileUpda = false,
        bool fileEdit = false,
        bool fileDele = false,
        string? denyViewMaster = null,
        string? denyViewDetail = null,
        string? denyNewMaster = null,
        string? denyNewDetail = null,
        string? denyModiMaster = null,
        string? denyModiDetail = null,
        string? dataFilter = null) =>
        new(moduleId, execTag, addNew, edit, delete, approve, deapprove, report, cost, setup, secrecy,
            endCase, unEndCase, other1, other2, other3, other4,
            fileView, fileUpda, fileEdit, fileDele,
            denyViewMaster, denyViewDetail, denyNewMaster, denyNewDetail,
            denyModiMaster, denyModiDetail, dataFilter);

    [Fact]
    public void DefaultEmpty_WhenExecTagAAndAllBitsOff()
    {
        Assert.True(RightsAdminLogic.IsDefaultEmpty(Module()));
        Assert.True(RightsAdminLogic.IsDefaultEmpty(Module(execTag: "A")));
        Assert.False(RightsAdminLogic.IsDefaultEmpty(Module(execTag: "B")));
        Assert.False(RightsAdminLogic.IsDefaultEmpty(Module(addNew: true)));
        Assert.False(RightsAdminLogic.IsDefaultEmpty(Module(denyViewMaster: "PRO_NO")));
        Assert.False(RightsAdminLogic.IsDefaultEmpty(Module(dataFilter: "1=1")));
        Assert.False(RightsAdminLogic.IsDefaultEmpty(Module(fileDele: true)));
    }

    [Fact]
    public void ReportDefaultEmpty_WhenAllFlagsOffAndNoFilter()
    {
        Assert.True(RightsAdminLogic.IsDefaultEmpty(new ReportRightsInput(129801, "R1", false, false, false, null)));
        Assert.False(RightsAdminLogic.IsDefaultEmpty(new ReportRightsInput(129801, "R1", true, false, false, null)));
        Assert.False(RightsAdminLogic.IsDefaultEmpty(new ReportRightsInput(129801, "R1", false, false, false, " A=1 ")));
    }

    [Theory]
    [InlineData(null, "A")]
    [InlineData("", "A")]
    [InlineData("  ", "A")]
    [InlineData("b", "B")]
    [InlineData("Z", "Z")]
    [InlineData("a", "A")]
    public void NormalizeExecTag_AcceptsSingleLetter(string? value, string expected) =>
        Assert.Equal(expected, RightsAdminLogic.NormalizeExecTag(value));

    [Theory]
    [InlineData("AB")]
    [InlineData("1")]
    public void NormalizeExecTag_RejectsInvalid(string? value)
    {
        Assert.Throws<ArgumentException>(() => RightsAdminLogic.NormalizeExecTag(value));
    }

    [Theory]
    [InlineData("PRO_NO,COLOR_ID", "PRO_NO;COLOR_ID")]
    [InlineData(" A;B; a ", "A;B")]
    [InlineData("PRO_NO,COLOR_ID;STUFF_ID", "PRO_NO;COLOR_ID;STUFF_ID")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeDenyList_DeduplicatesAndUsesSemicolon(string? value, string expected) =>
        Assert.Equal(expected, RightsAdminLogic.NormalizeDenyList(value));

    [Fact]
    public void ParseDenyList_AcceptsCommaAndSemicolon() =>
        Assert.Equal(["A", "B", "C"], RightsAdminLogic.ParseDenyList("A;B, C ; b ,a"));

    [Fact]
    public void Effective_PersonalRowCompletelyOverrides()
    {
        var personal = Module(execTag: "B", addNew: true, denyViewMaster: "A;B", dataFilter: "T_ID='X'");
        var effective = RightsAdminLogic.AggregateModuleEffective(personal, [Module(execTag: "Z", edit: true)]);

        Assert.Equal("personal", effective.Source);
        Assert.Equal("B", effective.ExecTag);
        Assert.True(effective.CanBrowse);
        Assert.True(effective.AddNew);
        Assert.False(effective.Edit);
        Assert.Equal(["A", "B"], effective.DenyViewMaster);
        Assert.Equal("T_ID='X'", effective.DataFilter);
    }

    [Fact]
    public void Effective_GroupBitsOrAndExecTagMax()
    {
        var effective = RightsAdminLogic.AggregateModuleEffective(
            null,
            [
                Module(execTag: "B", addNew: true, cost: true),
                Module(execTag: "D", edit: true, delete: true, setup: true),
            ]);

        Assert.Equal("group", effective.Source);
        Assert.Equal("D", effective.ExecTag);
        Assert.True(effective.CanBrowse);
        Assert.True(effective.AddNew);
        Assert.True(effective.Edit);
        Assert.True(effective.Delete);
        Assert.True(effective.Cost);
        Assert.True(effective.Setup);
        Assert.False(effective.Secrecy);
        Assert.False(effective.Approve);
    }

    [Fact]
    public void Effective_GroupExecTagA_MeansNoBrowse()
    {
        var effective = RightsAdminLogic.AggregateModuleEffective(null, [Module(execTag: "A", addNew: true)]);
        Assert.False(effective.CanBrowse);
        Assert.True(effective.AddNew);
        Assert.Equal("A", effective.ExecTag);
    }

    [Fact]
    public void Effective_GroupDenyFieldsIntersect()
    {
        var effective = RightsAdminLogic.AggregateModuleEffective(
            null,
            [
                Module(execTag: "B", denyViewMaster: "PRO_NO;COLOR_ID", denyNewMaster: "QTY;PRICE"),
                Module(execTag: "B", denyViewMaster: "COLOR_ID;STUFF_ID", denyNewMaster: "QTY;UNIT_ID"),
            ]);

        Assert.Equal(["COLOR_ID"], effective.DenyViewMaster);
        Assert.Equal(["QTY"], effective.DenyNewMaster);
        Assert.Empty(effective.DenyModiDetail);
    }

    [Fact]
    public void Effective_GroupDataFiltersCombineWithAnd()
    {
        var effective = RightsAdminLogic.AggregateModuleEffective(
            null,
            [Module(execTag: "B", dataFilter: "A=1"), Module(execTag: "B", dataFilter: "B=2"), Module(execTag: "B")]);
        Assert.Equal("(A=1) AND (B=2)", effective.DataFilter);
    }

    [Fact]
    public void Effective_NoRows_MeansNone()
    {
        var effective = RightsAdminLogic.AggregateModuleEffective(null, []);
        Assert.Equal("none", effective.Source);
        Assert.False(effective.CanBrowse);
        Assert.Equal("A", effective.ExecTag);
        Assert.False(effective.AddNew);
        Assert.Empty(effective.DenyViewMaster);
        Assert.Equal(string.Empty, effective.DataFilter);
    }

    [Fact]
    public void EffectiveReport_PersonalOverridesAndGroupOr()
    {
        var personal = new ReportRightsInput(129801, "R1", false, true, false, "X=1");
        var groups = new[]
        {
            new ReportRightsInput(129801, "R1", true, false, true, null),
            new ReportRightsInput(129801, "R1", false, false, true, "Y=2"),
        };

        var personalEffective = RightsAdminLogic.AggregateReportEffective(personal, groups);
        Assert.Equal("personal", personalEffective.Source);
        Assert.False(personalEffective.Preview);
        Assert.True(personalEffective.Print);
        Assert.Equal("X=1", personalEffective.DataFilter);

        var groupEffective = RightsAdminLogic.AggregateReportEffective(null, groups);
        Assert.Equal("group", groupEffective.Source);
        Assert.True(groupEffective.Preview);
        Assert.True(groupEffective.Export);
        Assert.False(groupEffective.Print);
        Assert.Equal("(Y=2)", groupEffective.DataFilter);

        Assert.Equal("none", RightsAdminLogic.AggregateReportEffective(null, []).Source);
    }

    [Fact]
    public void Whitelist_HasExpectedColumns()
    {
        Assert.Contains("EXEC_TAG", RightsColumnWhitelist.AllColumns);
        Assert.Contains("APPROVE_TAG", RightsColumnWhitelist.AllColumns);
        Assert.Contains("FILE_DELE_TAG", RightsColumnWhitelist.AllColumns);
        Assert.Contains("DENY_VIEW_FIELD_MASTER", RightsColumnWhitelist.AllColumns);
        Assert.Equal(19, RightsColumnWhitelist.BitColumns.Length);
        Assert.Equal(6, RightsColumnWhitelist.DenyColumns.Length);
    }
}
