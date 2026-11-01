using EOS.API.Data;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

public class AssistantQueryBuilderTests
{
    private static WorkbenchDefinition PurchaseDefinition(params WorkbenchField[] extra)
    {
        var fields = new List<WorkbenchField>
        {
            new("PURCHASE_NO", "采购单号", "nvarchar", 100, "left", IsPrimaryKey: true, IsVisible: true, IsQueryable: true),
            new("PURCHASE_DATE", "采购日期", "datetime", 100, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: true),
            new("APPROVED_TIME", "批核日期", "datetime", 100, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
            new("APPROVED_BY", "批核人", "nvarchar", 60, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
            new("APPROVED", "批核状态", "bit", 60, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
            new("FINISHED_TIME", "结案日期", "datetime", 100, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
            new("FINISHED_BY", "结案人", "nvarchar", 60, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
            new("FINISHED", "结案状态", "bit", 60, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
        };
        fields.AddRange(extra);
        return new WorkbenchDefinition(
            1606, "采购单", "PUR_PURCHASE_M", null,
            fields, [], "PURCHASE_DATE DESC",
            HasAdd: false, HasEdit: false, DetailNoSave: false,
            ["PURCHASE_TYPE", "PURCHASE_NO"], string.Empty, HasWorkflow: true);
    }

    [Fact]
    public void DateRange_MapsToPurchaseDateBetween()
    {
        var result = AssistantQueryBuilder.Build(PurchaseDefinition(), "2018-07-01", "2018-07-31", null, null, null);
        Assert.NotNull(result.Query);
        var condition = Assert.Single(result.Query!.Conditions);
        Assert.Equal("PURCHASE_DATE", condition.Field);
        Assert.Equal("between", condition.Operator);
        Assert.Equal("2018-07-01", condition.Value);
        Assert.Equal("2018-07-31", condition.ValueTo);
        Assert.Empty(result.NotApplied);
    }

    [Fact]
    public void SingleSideDate_MapsToGteOrLte()
    {
        var from = AssistantQueryBuilder.Build(PurchaseDefinition(), "2018-07-01", null, null, null, null);
        Assert.Equal("gte", Assert.Single(from.Query!.Conditions).Operator);
        var to = AssistantQueryBuilder.Build(PurchaseDefinition(), null, "2018-07-31", null, null, null);
        Assert.Equal("lte", Assert.Single(to.Query!.Conditions).Operator);
    }

    [Fact]
    public void ApprovedStatus_MapsToConfirmTagEqOne()
    {
        var result = AssistantQueryBuilder.Build(PurchaseDefinition(), null, null, "approved", null, null);
        var condition = Assert.Single(result.Query!.Conditions);
        Assert.Equal("APPROVED", condition.Field);
        Assert.Equal("1", condition.Value);
    }

    [Fact]
    public void UnapprovedAndFinished_MapsToExpectedFields()
    {
        var unapproved = AssistantQueryBuilder.Build(PurchaseDefinition(), null, null, "unapproved", null, null);
        Assert.Equal("APPROVED", Assert.Single(unapproved.Query!.Conditions).Field);
        Assert.Equal("0", Assert.Single(unapproved.Query!.Conditions).Value);
        var finished = AssistantQueryBuilder.Build(PurchaseDefinition(), null, null, "finished", null, null);
        Assert.Equal("FINISHED", Assert.Single(finished.Query!.Conditions).Field);
    }

    [Fact]
    public void AmountFieldPresent_MapsToGte()
    {
        var definition = PurchaseDefinition(
            new WorkbenchField("AMOUNT", "采购金额", "float", 100, "right", IsPrimaryKey: false, IsVisible: true, IsQueryable: true));
        var result = AssistantQueryBuilder.Build(definition, null, null, null, "10000", null);
        var condition = Assert.Single(result.Query!.Conditions);
        Assert.Equal("AMOUNT", condition.Field);
        Assert.Equal("gte", condition.Operator);
        Assert.Empty(result.NotApplied);
    }

    [Fact]
    public void AmountFieldMissing_RecordsNotApplied()
    {
        var result = AssistantQueryBuilder.Build(PurchaseDefinition(), null, null, null, "10000", null);
        Assert.Null(result.Query);
        Assert.Contains("amount", result.NotApplied);
    }

    [Fact]
    public void DateFieldMissing_RecordsNotApplied()
    {
        var definition = new WorkbenchDefinition(
            1606, "采购单", "T", null,
            [new WorkbenchField("PURCHASE_NO", "采购单号", "nvarchar", 100, "left", IsPrimaryKey: true, IsVisible: true, IsQueryable: true)],
            [], null, HasAdd: false, HasEdit: false, DetailNoSave: false, ["PURCHASE_NO"], string.Empty, HasWorkflow: false);
        var result = AssistantQueryBuilder.Build(definition, "2018-07-01", null, null, null, null);
        Assert.Null(result.Query);
        Assert.Contains("date", result.NotApplied);
    }

    [Fact]
    public void UnknownStatus_RecordsNotApplied()
    {
        var result = AssistantQueryBuilder.Build(PurchaseDefinition(), null, null, "weird", null, null);
        Assert.Null(result.Query);
        Assert.Contains("status", result.NotApplied);
    }

    [Fact]
    public void InvalidDate_Throws()
        => Assert.Throws<ArgumentException>(
            () => AssistantQueryBuilder.Build(PurchaseDefinition(), "not-a-date", null, null, null, null));

    [Fact]
    public void NoFilters_ReturnsNullQuery()
    {
        var result = AssistantQueryBuilder.Build(PurchaseDefinition(), null, null, null, null, null);
        Assert.Null(result.Query);
        Assert.Empty(result.NotApplied);
    }

    [Fact]
    public void Explicit_Filters_Map_To_Conditions_And_Report_Rejected_Entries()
    {
        var result = AssistantQueryBuilder.Build(
            PurchaseDefinition(), null, null, null, null, null,
            [
                new AssistantQueryBuilder.Filter("APPROVED", "eq", "0"),
                new AssistantQueryBuilder.Filter("PURCHASE_DATE", "gte", "2026-10-01"),
                new AssistantQueryBuilder.Filter("PURCHASE_NO", "contains", "DD", null, "or"),
                new AssistantQueryBuilder.Filter("NOPE", "eq", "x"),
                new AssistantQueryBuilder.Filter("PURCHASE_NO", "weird", "x"),
                new AssistantQueryBuilder.Filter("PURCHASE_NO", "between", "A"),
            ]);

        Assert.NotNull(result.Query);
        Assert.Equal(3, result.Query!.Conditions.Count);
        Assert.Equal("APPROVED", result.Query.Conditions[0].Field);
        Assert.Equal("0", result.Query.Conditions[0].Value);
        Assert.Equal("or", result.Query.Conditions[2].Logic);
        Assert.Contains("field:NOPE", result.NotApplied);
        Assert.Contains("operator:weird", result.NotApplied);
        Assert.Contains(result.NotApplied, item => item.StartsWith("between:", StringComparison.Ordinal));
    }

    [Fact]
    public void Explicit_Filter_On_NonQueryable_Field_Allows_Comparison_Only()
    {
        var definition = PurchaseDefinition();
        var eq = AssistantQueryBuilder.Build(
            definition, null, null, null, null, null,
            [new AssistantQueryBuilder.Filter("APPROVED", "eq", "1")]);
        Assert.NotNull(eq.Query);

        var contains = AssistantQueryBuilder.Build(
            definition, null, null, null, null, null,
            [new AssistantQueryBuilder.Filter("APPROVED", "contains", "1")]);
        Assert.Null(contains.Query);
        Assert.Contains(contains.NotApplied, item => item.StartsWith("field:APPROVED", StringComparison.Ordinal));
    }

    [Fact]
    public void Sort_Only_Accepts_Visible_Fields_And_Normalizes_Direction()
    {
        var definition = PurchaseDefinition();

        Assert.Equal(("PURCHASE_DATE", "desc"),
            AssistantQueryBuilder.ResolveSort(definition, "purchase_date", "DESC"));
        Assert.Equal(("PURCHASE_DATE", "asc"),
            AssistantQueryBuilder.ResolveSort(definition, "PURCHASE_DATE", "sideways"));
        Assert.Equal((null, "asc"),
            AssistantQueryBuilder.ResolveSort(definition, "NOT_A_FIELD", "asc"));
    }
}
