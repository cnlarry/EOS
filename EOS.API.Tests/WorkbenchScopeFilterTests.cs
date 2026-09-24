using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Errors;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

public sealed class WorkbenchScopeFilterTests
{
    private static readonly IReadOnlySet<string> FilterKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CLIENT_ID", "SALES_ID" };

    private static WorkbenchDefinition Definition(string execTag = "Z", bool hasOwner = true, bool hasOwnerGroup = true) =>
        new(
            ModuleId: 1401,
            Title: "客户基本资料",
            MasterTable: "CLIENT",
            DetailTable: null,
            MasterFields: [new WorkbenchField("CLIENT_ID", "客户编号", "nvarchar", 100, null, true)],
            DetailFields: [],
            DefaultSort: null,
            HasAdd: false,
            HasEdit: false,
            DetailNoSave: false,
            MasterPkOrder: ["CLIENT_ID"],
            DetailNoFields: "",
            HasWorkflow: false,
            ModuleFilter: null,
            FilterFieldKeys: FilterKeys,
            UserId: "u1",
            ExecTag: execTag,
            HasOwnerColumn: hasOwner,
            HasOwnerGroupColumn: hasOwnerGroup,
            GroupExpressions: ["", "", "", "", ""]);

    [Fact]
    public void ExecTagB_AddsOwnerPredicate_WithParameterizedUser()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());
        var predicates = new List<string>();
        using var command = new SqlCommand();

        filter.ApplyExecTagScope(Definition(execTag: "B"), predicates, command);

        Assert.Contains("[OWNER]=@execOwner", predicates);
        Assert.Equal("u1", command.Parameters["@execOwner"].Value);
    }

    [Fact]
    public void ExecTagB_WithoutOwnerColumn_ThrowsFailClosed()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());
        var predicates = new List<string>();
        using var command = new SqlCommand();

        Assert.Throws<DataFilterUnsupportedException>(
            () => filter.ApplyExecTagScope(Definition(execTag: "B", hasOwner: false), predicates, command));
        Assert.Empty(predicates);
    }

    [Fact]
    public void ExecTagZ_AddsNoPredicate()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());
        var predicates = new List<string>();
        using var command = new SqlCommand();

        filter.ApplyExecTagScope(Definition(execTag: "Z"), predicates, command);

        Assert.Empty(predicates);
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("F")]
    [InlineData("BB")]
    public void ExecTagUnknown_ThrowsFailClosed(string execTag)
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());
        var predicates = new List<string>();
        using var command = new SqlCommand();

        Assert.Throws<DataFilterUnsupportedException>(
            () => filter.ApplyExecTagScope(Definition(execTag: execTag), predicates, command));
        Assert.Empty(predicates);
    }

    [Fact]
    public void ModuleFilter_ParsesWhitelistedColumn_AndParameterizes()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());
        var definition = Definition() with { ModuleFilter = "CLIENT.SALES_ID='YW2-08'" };
        var predicates = new List<string>();
        using var command = new SqlCommand();

        filter.ApplyModuleFilter(definition, predicates, command);

        var predicate = Assert.Single(predicates);
        Assert.Contains("[SALES_ID]", predicate);
        Assert.Equal("YW2-08", command.Parameters["@df0"].Value);
    }

    [Fact]
    public void ModuleFilter_Unsupported_ThrowsFailClosed()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());
        var definition = Definition() with { ModuleFilter = "CLIENT.SALES_ID IN (SELECT 1 FROM dbo.x)" };
        var predicates = new List<string>();
        using var command = new SqlCommand();

        Assert.Throws<DataFilterUnsupportedException>(
            () => filter.ApplyModuleFilter(definition, predicates, command));
        Assert.Empty(predicates);
    }

    [Fact]
    public void DetailScope_BuildsExistsViaMasterAssociationKey()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());
        var predicates = new List<string>();
        using var command = new SqlCommand();

        filter.ApplyDetailScope(Definition(execTag: "B"), null, "CLIENT", ["CLIENT_ID"], predicates, command);

        var predicate = Assert.Single(predicates);
        Assert.Contains("EXISTS (SELECT 1 FROM dbo.[CLIENT]", predicate);
        Assert.Contains("[CLIENT_ID]=dbo.[CLIENT].[CLIENT_ID]", predicate);
        Assert.Contains("[OWNER]=@execOwner", predicate);
    }

    [Fact]
    public void DetailScope_WithoutKeyColumns_ThrowsFailClosed()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());
        var predicates = new List<string>();
        using var command = new SqlCommand();

        Assert.Throws<DataFilterUnsupportedException>(
            () => filter.ApplyDetailScope(Definition(), null, "CLIENT", [], predicates, command));
        Assert.Empty(predicates);
    }

    [Fact]
    public void TryBuildRecordScopePredicate_CombinesDataFilterAndExecTag()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());
        var ok = filter.TryBuildRecordScopePredicate(
            Definition(execTag: "B"), "CLIENT.SALES_ID='YW2-08'",
            out var predicate, out var parameters);

        Assert.True(ok);
        Assert.Contains("[SALES_ID]", predicate);
        Assert.Contains("[OWNER]=@df1", predicate);
        Assert.Equal(2, parameters.Count);
        Assert.Equal("YW2-08", parameters[0]);
        Assert.Equal("u1", parameters[1]);
    }

    [Fact]
    public void TryBuildRecordScopePredicate_UnsupportedDataFilter_ReturnsFalse()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());
        var ok = filter.TryBuildRecordScopePredicate(
            Definition(), "CLIENT.SALES_ID IN (SELECT 1 FROM dbo.x)",
            out var predicate, out var parameters);

        Assert.False(ok);
        Assert.Equal(string.Empty, predicate);
        Assert.Empty(parameters);
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("F")]
    [InlineData("BB")]
    public void RecordScope_UnknownExecTag_ReturnsFalseFailClosed(string execTag)
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildRecordScopePredicate(
            Definition(execTag: execTag), null, out var predicate, out var parameters);

        Assert.False(ok);
        Assert.Equal(string.Empty, predicate);
        Assert.Empty(parameters);
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("F")]
    public void ChooserScope_UnknownExecTag_ReturnsFalseFailClosed(string execTag)
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildChooserScopePredicate(
            "CLIENT", null, null, null, execTag, "u1", true, true, FilterKeys,
            out var predicate, out var parameters);

        Assert.False(ok);
        Assert.Equal(string.Empty, predicate);
        Assert.Empty(parameters);
    }

    [Fact]
    public void RecordScope_ExecTagD_WithoutOwnerGroupColumn_ReturnsFalseFailClosed()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildRecordScopePredicate(
            Definition(execTag: "D", hasOwnerGroup: false), null, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void ChooserScope_ExecTagB_QualifiesOwnerColumnWithSourceTable()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildChooserScopePredicate(
            "CLIENT", null, null, null, "B", "u1", true, true, FilterKeys,
            out var predicate, out var parameters);

        Assert.True(ok);
        Assert.Equal("[CLIENT].[OWNER]=@df0", predicate);
        Assert.Equal("u1", Assert.Single(parameters));
    }

    [Theory]
    [InlineData("C", "[CLIENT].[OWNER]", "f_get_underling(@df0)")]
    [InlineData("E", "[CLIENT].[OWNER_G]", "f_get_underling(@df0)")]
    public void ChooserScope_UnderlingTags_UseSourceTableQualifierAndSingleParameter(
        string execTag, string expectedColumn, string expectedCall)
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildChooserScopePredicate(
            "CLIENT", null, null, null, execTag, "u1", true, true, FilterKeys,
            out var predicate, out var parameters);

        Assert.True(ok);
        Assert.Contains(expectedColumn, predicate);
        Assert.Contains(expectedCall, predicate);
        Assert.Equal("u1", Assert.Single(parameters));
    }

    [Fact]
    public void RecordScope_ExecTagE_EmitsSingleOwnerGroupParameter()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildRecordScopePredicate(
            Definition(execTag: "E"), null, out var predicate, out var parameters);

        Assert.True(ok);
        Assert.Contains("[OWNER_G] IN (SELECT G_IDX FROM dbo.SYSDG_USER", predicate);
        Assert.Contains("f_get_underling(@df0)", predicate);
        Assert.Equal("u1", Assert.Single(parameters));
    }
}
