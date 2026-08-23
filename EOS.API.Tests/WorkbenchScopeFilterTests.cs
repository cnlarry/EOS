using EOS.API.Data;
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
}
