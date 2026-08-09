using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class DataFilterParserTests
{
    private static IReadOnlySet<string> Fields(params string[] values) =>
        values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool Try(string? filter, string masterTable, IReadOnlySet<string> allowed,
        out string predicate, out IReadOnlyList<object> parameters) =>
        DataFilterParser.TryParse(filter, masterTable, allowed, out predicate, out parameters);

    [Fact]
    public void SimpleEquality_CompilesToParameterizedPredicate()
    {
        Assert.True(Try("CLIENT_ID='C001'", "PRODUCT_EDITION", Fields("CLIENT_ID"), out var predicate, out var parameters));
        Assert.Equal("[CLIENT_ID] = @df0", predicate);
        Assert.Equal(["C001"], parameters);
    }

    [Fact]
    public void MasterTablePrefix_IsAccepted()
    {
        Assert.True(Try("PRODUCT_EDITION.PRO_NO='X'", "PRODUCT_EDITION", Fields("PRO_NO"), out var predicate, out _));
        Assert.Equal("[PRO_NO] = @df0", predicate);
    }

    [Fact]
    public void ForeignTablePrefix_IsRejected()
    {
        Assert.False(Try("CLIENT.SALES_ID='YW2-08'", "PRODUCT_EDITION", Fields("CLIENT_ID"), out _, out _));
    }

    [Fact]
    public void AndOrWithParentheses_IsSupported()
    {
        Assert.True(Try("(A='1') AND (B='2' OR C='3')", "T", Fields("A", "B", "C"), out var predicate, out var parameters));
        Assert.Equal("([A] = @df0) AND ([B] = @df1 OR [C] = @df2)", predicate);
        Assert.Equal(["1", "2", "3"], parameters);
    }

    [Fact]
    public void UnknownField_IsRejected()
    {
        Assert.False(Try("SECRET='1'", "T", Fields("A"), out _, out _));
    }

    [Fact]
    public void UnsupportedOperator_IsRejected()
    {
        Assert.False(Try("A LIKE 'x%'", "T", Fields("A"), out _, out _));
        Assert.False(Try("A BETWEEN 1 AND 2", "T", Fields("A"), out _, out _));
    }

    [Theory]
    [InlineData("T.PRO_TYPE=1", "[PRO_TYPE] = @df0")]
    [InlineData("T.APPLY_TYPE='QG'", "[APPLY_TYPE] = @df0")]
    [InlineData("T.QTY>0.1", "[QTY] > @df0")]
    [InlineData("T.STATE<4", "[STATE] < @df0")]
    [InlineData("T.MOU_SORT=2", "[MOU_SORT] = @df0")]
    [InlineData("FINISHED_TAG=0 AND QTY>10", "[FINISHED_TAG] = @df0 AND [QTY] > @df1")]
    public void ModuleFilter_数值与比较运算符(string filter, string expectedPredicate)
    {
        Assert.True(Try(filter, "T", Fields("PRO_TYPE", "APPLY_TYPE", "QTY", "STATE", "MOU_SORT", "FINISHED_TAG", "RECEIVE_QTY"), out var predicate, out var parameters));
        Assert.Equal(expectedPredicate, predicate);
        Assert.All(parameters, parameter => Assert.True(parameter is decimal or string));
    }

    [Fact]
    public void ModuleFilter_同表列算术_被编译为参数化谓词()
    {
        Assert.True(Try("SUM_AMOUNT-RECEIVE_AMOUNT>0", "COP_ACCOUNT_M", Fields("SUM_AMOUNT", "RECEIVE_AMOUNT"), out var predicate, out var parameters));
        Assert.Equal("[SUM_AMOUNT]-[RECEIVE_AMOUNT] > @df0", predicate);
        Assert.Equal([0m], parameters);
    }

    [Fact]
    public void ModuleFilter_带主表前缀的列算术_被接受()
    {
        Assert.True(Try("PUR_PURCHASE_D.QTY-PUR_PURCHASE_D.RECEIVE_QTY>0", "PUR_PURCHASE_D", Fields("QTY", "RECEIVE_QTY"), out var predicate, out _));
        Assert.Equal("[QTY]-[RECEIVE_QTY] > @df0", predicate);
    }

    [Fact]
    public void ModuleFilter_GetDate_被求值为日期参数()
    {
        Assert.True(Try("PLAN_DELIVERY_DATE<=getdate()", "PUR_PURCHASE_D", Fields("PLAN_DELIVERY_DATE"), out var predicate, out var parameters));
        Assert.Equal("[PLAN_DELIVERY_DATE] <= @df0", predicate);
        var value = Assert.IsType<DateTime>(parameters[0]);
        Assert.True((DateTime.Now - value).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ModuleFilter_ConvertGetDate_求值为年月字符串()
    {
        Assert.True(Try("SEND_DATE<convert(varchar(7),getdate(),120)", "COP_SEND_D", Fields("SEND_DATE"), out var predicate, out var parameters));
        Assert.Equal("[SEND_DATE] < @df0", predicate);
        var value = Assert.IsType<string>(parameters[0]);
        Assert.Matches(@"^\d{4}-\d{2}$", value);
    }

    [Fact]
    public void ModuleFilter_日期边界表达式_求值为上月26号字符串()
    {
        Assert.True(Try("SEND_DATE<convert(varchar(7),dateadd(month,-1,getdate()),120)+'-26'",
            "COP_SEND_D", Fields("SEND_DATE"), out var predicate, out var parameters));
        Assert.Equal("[SEND_DATE] < @df0", predicate);
        var value = Assert.IsType<string>(parameters[0]);
        Assert.Matches(@"^\d{4}-\d{2}-26$", value);
    }

    [Fact]
    public void ModuleFilter_真实组合_今日需到料()
    {
        Assert.True(Try(
            "PUR_PURCHASE_D.finished_tag=0 and PUR_PURCHASE_D.PLAN_DELIVERY_DATE<=getdate() and PUR_PURCHASE_D.QTY-PUR_PURCHASE_D.RECEIVE_QTY>0",
            "PUR_PURCHASE_D", Fields("FINISHED_TAG", "PLAN_DELIVERY_DATE", "QTY", "RECEIVE_QTY"), out var predicate, out var parameters));
        Assert.Equal("[finished_tag] = @df0 AND [PLAN_DELIVERY_DATE] <= @df1 AND [QTY]-[RECEIVE_QTY] > @df2", predicate);
        Assert.Equal(3, parameters.Count);
        Assert.Equal(0m, parameters[0]);
        Assert.IsType<DateTime>(parameters[1]);
        Assert.Equal(0m, parameters[2]);
    }

    [Theory]
    [InlineData("QTY IN (SELECT PRO_NO FROM BOM_STRU_M)")]
    [InlineData("PRO_NO LIKE '%X%'")]
    public void ModuleFilter_不支持表达式被拒绝(string filter)
    {
        Assert.False(Try(filter, "COP_ACCOUNT_M", Fields("SUM_AMOUNT", "RECEIVE_AMOUNT", "QTY", "SEND_DATE", "PRO_NO"), out _, out _));
    }

    [Theory]
    [InlineData("COP_SEND_M.SEND_DATE<getdate()")]
    [InlineData("PLAN_QTY>FINISHED_PLAN_QTY")]
    [InlineData("SEND_DATE<fancy(getdate())")]
    [InlineData("QTY-RECEIVE_QTY")]
    public void ModuleFilter_跨表与未知函数被拒绝(string filter)
    {
        Assert.False(Try(filter, "COP_SEND_D", Fields("SEND_DATE", "PLAN_QTY", "FINISHED_PLAN_QTY", "QTY", "RECEIVE_QTY"), out _, out _));
    }

    [Fact]
    public void ApostropheInLiteral_IsUnescaped()
    {
        Assert.True(Try("NAME='O''Brien'", "T", Fields("NAME"), out _, out var parameters));
        Assert.Equal(["O'Brien"], parameters);
    }

    [Fact]
    public void BlankFilter_IsNotAParsedScope()
    {
        Assert.False(Try("   ", "T", Fields("A"), out _, out _));
        Assert.False(Try(null, "T", Fields("A"), out _, out _));
    }
}
