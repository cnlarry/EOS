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
            new("CONFIRM_DATE", "批核日期", "datetime", 100, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
            new("CONFIRM_PERSON", "批核人", "nvarchar", 60, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
            new("CONFIRM_TAG", "批核状态", "bit", 60, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
            new("FINISHED_DATE", "结案日期", "datetime", 100, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
            new("FINISHED_PERSON", "结案人", "nvarchar", 60, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
            new("FINISHED_TAG", "结案状态", "bit", 60, "left", IsPrimaryKey: false, IsVisible: true, IsQueryable: false),
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
        Assert.Equal("CONFIRM_TAG", condition.Field);
        Assert.Equal("1", condition.Value);
    }

    [Fact]
    public void UnapprovedAndFinished_MapsToExpectedFields()
    {
        var unapproved = AssistantQueryBuilder.Build(PurchaseDefinition(), null, null, "unapproved", null, null);
        Assert.Equal("CONFIRM_TAG", Assert.Single(unapproved.Query!.Conditions).Field);
        Assert.Equal("0", Assert.Single(unapproved.Query!.Conditions).Value);
        var finished = AssistantQueryBuilder.Build(PurchaseDefinition(), null, null, "finished", null, null);
        Assert.Equal("FINISHED_TAG", Assert.Single(finished.Query!.Conditions).Field);
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
}
