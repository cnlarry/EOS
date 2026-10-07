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

    private static WorkbenchDefinition Definition(string execTag = "Z") =>
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
            GroupExpressions: ["", "", "", "", ""]);

    [Theory]
    [InlineData("Z")]
    [InlineData("A")]
    [InlineData("")]
    public void ExecTag_NoScopeValues_AreAccepted(string execTag)
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        filter.ApplyExecTagScope(Definition(execTag: execTag));
    }

    [Theory]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    [InlineData("E")]
    [InlineData("Q")]
    [InlineData("F")]
    [InlineData("BB")]
    public void ExecTag_RetiredOrUnknownValues_ThrowFailClosed(string execTag)
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        Assert.Throws<DataFilterUnsupportedException>(
            () => filter.ApplyExecTagScope(Definition(execTag: execTag)));
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

        filter.ApplyDetailScope(Definition(), "CLIENT.SALES_ID='YW2-08'", "CLIENT", ["CLIENT_ID"], predicates, command);

        var predicate = Assert.Single(predicates);
        Assert.Contains("EXISTS (SELECT 1 FROM dbo.[CLIENT]", predicate);
        Assert.Contains("[CLIENT_ID]=dbo.[CLIENT].[CLIENT_ID]", predicate);
        Assert.Contains("[SALES_ID]", predicate);
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
    public void TryBuildRecordScopePredicate_CompilesDataFilter()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildRecordScopePredicate(
            Definition(), "CLIENT.SALES_ID='YW2-08'", out var predicate, out var parameters);

        Assert.True(ok);
        Assert.Contains("[SALES_ID]", predicate);
        Assert.Equal("YW2-08", Assert.Single(parameters));
    }

    [Fact]
    public void TryBuildRecordScopePredicate_UnsupportedDataFilter_ReturnsFalse()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildRecordScopePredicate(
            Definition(), "CLIENT.SALES_ID IN (SELECT 1 FROM dbo.x)", out var predicate, out var parameters);

        Assert.False(ok);
        Assert.Equal(string.Empty, predicate);
        Assert.Empty(parameters);
    }

    [Theory]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    [InlineData("E")]
    [InlineData("Q")]
    [InlineData("BB")]
    public void RecordScope_RetiredOrUnknownExecTag_ReturnsFalseFailClosed(string execTag)
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildRecordScopePredicate(
            Definition(execTag: execTag), null, out var predicate, out var parameters);

        Assert.False(ok);
        Assert.Equal(string.Empty, predicate);
        Assert.Empty(parameters);
    }

    [Theory]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    [InlineData("E")]
    [InlineData("Q")]
    public void ChooserScope_RetiredOrUnknownExecTag_ReturnsFalseFailClosed(string execTag)
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildChooserScopePredicate(
            "CLIENT", null, null, null, execTag, FilterKeys,
            out var predicate, out var parameters);

        Assert.False(ok);
        Assert.Equal(string.Empty, predicate);
        Assert.Empty(parameters);
    }

    [Fact]
    public void ChooserScope_ModuleFilterAppliesWhenSourceIsMasterTable()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildChooserScopePredicate(
            "CLIENT", "CLIENT.SALES_ID='YW2-08'", "CLIENT", null, "Z", FilterKeys,
            out var predicate, out var parameters);

        Assert.True(ok);
        Assert.Contains("[SALES_ID]", predicate);
        Assert.Equal("YW2-08", Assert.Single(parameters));
    }

    [Fact]
    public void ChooserScope_NoScopeInputs_YieldsEmptyPredicate()
    {
        var filter = new WorkbenchScopeFilter(new ApiMetrics());

        var ok = filter.TryBuildChooserScopePredicate(
            "CLIENT", null, null, null, "Z", FilterKeys, out var predicate, out var parameters);

        Assert.True(ok);
        Assert.Equal(string.Empty, predicate);
        Assert.Empty(parameters);
    }
}
