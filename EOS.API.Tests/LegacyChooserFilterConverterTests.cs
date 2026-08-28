using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public sealed class LegacyChooserFilterConverterTests
{
    private static LegacyChooserFilterConverter.ConvertOptions Opts(params string[] aliases) =>
        new(new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Convert_Blank_ReturnsEmpty()
    {
        var result = LegacyChooserFilterConverter.Convert("   ", "ORDER_M");
        Assert.Equal(1, result.Tier);
        Assert.Null(result.Struct);
    }

    [Fact]
    public void Convert_SimpleChain_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "CLIENT.BUSINESS_TAG=0 AND CLIENT.CONFIRM_TAG = 1", "ORDER_M", Opts("CLIENT"));
        Assert.Equal(1, result.Tier);
        Assert.NotNull(result.Struct);
        Assert.Equal(2, result.Struct!.Items.Count);
        Assert.Equal("CLIENT.BUSINESS_TAG", result.Struct.Items[0].Field);
        Assert.Equal("EQ", result.Struct.Items[0].Operator);
        Assert.Equal("0", result.Struct.Items[0].Value);
    }

    [Fact]
    public void Convert_TemplateAndQuotedValue_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "CLIENT_PRICE_M.CLIENT_ID='{M.CLIENT_ID}' AND CLIENT_PRICE_D.CURR_ID='{m.CURR_ID}'",
            "CLIENT_PRICE_D", Opts("CLIENT_PRICE_M"));
        Assert.Equal(1, result.Tier);
        Assert.Equal("{m.CLIENT_ID}", result.Struct!.Items[0].Value); // 大写 M 归一
        Assert.Equal("CLIENT_PRICE_D.CURR_ID", result.Struct.Items[1].Field);
    }

    [Fact]
    public void Convert_IsNullZeroMacro_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1", "ORDER_M", Opts("CLIENT"));
        Assert.Equal(1, result.Tier);
        Assert.Equal("ISNULL_ZERO", result.Struct!.Items[0].Operator);
        Assert.Equal("EQ", result.Struct.Items[1].Operator);
        Assert.Equal("ZERO", result.Struct.Items[1].NullSafe);
        Assert.Equal("1", result.Struct.Items[1].Value);
    }

    [Fact]
    public void Convert_IsNullStateCompare_Tier1NullSafe()
    {
        var result = LegacyChooserFilterConverter.Convert("ISNULL(HR_EMPLOYEE.STATE,0) < 4", "HR_EMPLOYEE");
        Assert.Equal(1, result.Tier);
        Assert.Equal("LT", result.Struct!.Items[0].Operator);
        Assert.Equal("ZERO", result.Struct.Items[0].NullSafe);
        Assert.Equal("4", result.Struct.Items[0].Value);
    }

    [Fact]
    public void Convert_ModuleTemplate_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert("B_M_IDX={module}", "BILLKIND");
        Assert.Equal(1, result.Tier);
        Assert.Equal("EQ", result.Struct!.Items[0].Operator);
        Assert.Equal("{module}", result.Struct.Items[0].Value);
    }

    [Fact]
    public void Convert_DatediffDay_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "Datediff(day,COP_SHIPMENT_M.SHIPMENT_DATE,GETDATE())=0", "COP_SHIPMENT_M");
        Assert.Equal(1, result.Tier);
        Assert.Equal("DAYS_FROM_TODAY", result.Struct!.Items[0].Operator);
        Assert.Equal("0", result.Struct.Items[0].Value);
    }

    [Fact]
    public void Convert_NotInSubquery_Tier2()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_EMPLOYEE_CARD)", "HR_DIARY");
        Assert.Equal(2, result.Tier);
        Assert.Null(result.Struct);
    }

    [Fact]
    public void Convert_ColumnEqualsColumn_Tier2()
    {
        var result = LegacyChooserFilterConverter.Convert("CURR_NAME=CURR_NAME", "CLIENT");
        Assert.Equal(1, result.Tier);
        Assert.NotNull(result.Struct);
        Assert.Equal("column", result.Struct!.Items[0].Left!.Kind);
        Assert.Equal("column", result.Struct.Items[0].Right!.Kind);
    }

    [Fact]
    public void Convert_ConstantLeftSide_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert("'QG'='{m.APPLY_TYPE}'", "ORDER_M");
        Assert.Equal(1, result.Tier);
        Assert.NotNull(result.Struct);
        Assert.Equal("literal", result.Struct!.Items[0].Left!.Kind);
        Assert.Equal("template", result.Struct.Items[0].Right!.Kind);
    }

    [Fact]
    public void Convert_ColumnArithmetic_Tier2()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "COP_ORDER_D.PLAN_QTY-COP_ORDER_D.FINISHED_PLAN_QTY>0", "ORDER_D");
        Assert.Equal(2, result.Tier);
        Assert.Null(result.Struct);
    }

    [Fact]
    public void Convert_OrGroup_Tier2()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "NOT (PRODUCT.BUSINESS_TAG=1 OR PRODUCT.STOP_TAG=1)", "ORDER_M");
        Assert.Equal(2, result.Tier);
        Assert.Null(result.Struct);
    }

    [Fact]
    public void Convert_OrGroupWithJoin_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "NOT (PRODUCT.BUSINESS_TAG=1 OR PRODUCT.STOP_TAG=1)", "ORDER_M", Opts("PRODUCT"));
        Assert.Equal(1, result.Tier);
        Assert.True(result.Struct!.Items[0].Negate);
        Assert.NotNull(result.Struct.Items[0].Group);
    }

    [Fact]
    public void Convert_ArithmeticWithJoin_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "COP_ORDER_D.PLAN_QTY-COP_ORDER_D.FINISHED_PLAN_QTY>0", "ORDER_D", Opts("COP_ORDER_D"));
        Assert.Equal(1, result.Tier);
        Assert.Equal("arith", result.Struct!.Items[0].Left!.Kind);
        Assert.Equal("GT", result.Struct.Items[0].Operator);
    }

    [Fact]
    public void Convert_NotInSubqueryWithJoin_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_EMPLOYEE_CARD)",
            "HR_DIARY", Opts("HR_EMPLOYEE"));
        Assert.Equal(1, result.Tier);
        var subItem = result.Struct!.Items.Single(item => item.Operator == "NOT_IN");
        Assert.NotNull(subItem.Subquery);
        Assert.Equal("HR_EMPLOYEE_CARD", subItem.Subquery!.From![0].Table);
    }

    [Fact]
    public void Convert_MultiTableSubquery_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "HR_EMPLOYEE.EMP_ID NOT IN (SELECT HR_PLAN_D.EMP_ID FROM HR_PLAN_M,HR_PLAN_D WHERE HR_PLAN_M.PLAN_TYPE=HR_PLAN_D.PLAN_TYPE AND HR_PLAN_M.PLAN_NO=HR_PLAN_D.PLAN_NO AND HR_PLAN_M.COUNT_MONTH='{m.COUNT_MONTH}')",
            "HR_WAGE_D", Opts("HR_EMPLOYEE"));
        Assert.Equal(1, result.Tier);
        var subItem = result.Struct!.Items.Single(item => item.Operator == "NOT_IN");
        Assert.Equal(2, subItem.Subquery!.From!.Count);
        Assert.Equal(3, subItem.Subquery.Filter!.Count);
    }

    [Fact]
    public void Convert_InFunction_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units('{d.PRO_NO}'))",
            "CLIENT_PRICE_D", Opts("UNIT"));
        Assert.Equal(1, result.Tier);
        var item = result.Struct!.Items[0];
        Assert.Equal("IN_FUNCTION", item.Operator);
        Assert.Equal("f_get_pro_units", item.Subquery!.Function);
        Assert.Equal("{d.PRO_NO}", item.Subquery.Args![0]);
    }

    [Fact]
    public void Convert_Exists_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "EXISTS (SELECT 1 FROM HR_APPLY_D WHERE HR_APPLY_D.APPLY_NO='{m.APPLY_NO}')", "HR_APPLY_M");
        Assert.Equal(1, result.Tier);
        Assert.Equal("EXISTS", result.Struct!.Items[0].Operator);
        Assert.Single(result.Struct.Items[0].Subquery!.Filter!);
    }

    [Fact]
    public void Convert_IsNull_Tier1()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "HR_EMPLOYEE.DIMISSION_DATE IS NULL AND HR_EMPLOYEE.STATE < 4", "HR_WAGE_D", Opts("HR_EMPLOYEE"));
        Assert.Equal(1, result.Tier);
        Assert.Equal("IS_NULL", result.Struct!.Items[0].Operator);
    }

    [Fact]
    public void Convert_TruncatedGarbage_Tier3()
    {
        var result = LegacyChooserFilterConverter.Convert("etail", "ORDER_M");
        Assert.Equal(3, result.Tier);
    }

    [Fact]
    public void Convert_MojibakeValue_Tier3()
    {
        var result = LegacyChooserFilterConverter.Convert(
            "MOU_ACCEPT_M.BATCH_STATE<'{m.BATCH_SORT}' AND MOU_ACCEPT_M.ACCEPT_STATE='\uFFFD\uFFFD\uFFFD\uFFFD'", "MOU_ACCEPT_D");
        Assert.Equal(3, result.Tier);
    }

    [Fact]
    public void Convert_DanglingAnd_Tier3()
    {
        var result = LegacyChooserFilterConverter.Convert("CLIENT.BUSINESS_TAG=0 AND", "ORDER_M");
        Assert.Equal(3, result.Tier);
    }

    [Fact]
    public void Convert_UnbalancedQuote_Tier3()
    {
        var result = LegacyChooserFilterConverter.Convert("CLIENT.BUSINESS_TAG='0", "ORDER_M");
        Assert.Equal(3, result.Tier);
    }
}
