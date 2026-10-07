using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class GroupExpressionParserTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEPT_ID", "SALES_ID", "PAY_DATE", "FINISHED_TAG", "QUOTE_DATE", "QTY",
        "COUNT_MONTH", "IN_DATE", "SORT_ID", "CLIENT_ID", "APPROVE_STATE", "PURCHASE_ID",
    };

    [Theory]
    [InlineData("HRM_PLAN_M.DEPT_ID", "[DEPT_ID]")]
    [InlineData("PURCHASE_ID", "[PURCHASE_ID]")]
    [InlineData("APPROVE_STATE", "[APPROVE_STATE]")]
    [InlineData("HRM_PLAN_M.COUNT_MONTH+'.'", "[COUNT_MONTH]+N'.'")]
    public void Compiles_SimpleColumnAndConcat(string expression, string expected)
    {
        var ok = GroupExpressionParser.TryCompile(expression, "HRM_PLAN_M", Allowed, out var compiled);
        Assert.True(ok);
        Assert.Equal(expected, compiled);
    }

    [Fact]
    public void Compiles_ColumnConcatWithTablePrefix()
    {
        var ok = GroupExpressionParser.TryCompile("CLIENT.SALES_ID+CLIENT.PAY_DATE", "CLIENT", Allowed, out var compiled);
        Assert.True(ok);
        Assert.Equal("[SALES_ID]+[PAY_DATE]", compiled);
    }

    [Fact]
    public void Compiles_ColumnOfAnotherMasterTable()
    {
        var ok = GroupExpressionParser.TryCompile("HR_EMPLOYEE.IN_DATE", "HR_EMPLOYEE", Allowed, out var compiled);
        Assert.True(ok);
        Assert.Equal("[IN_DATE]", compiled);
    }

    [Fact]
    public void Compiles_YearMonthReplicateChain()
    {
        const string expression = "(CAST(YEAR(PAY_DATE) AS VARCHAR)+REPLICATE(0,1-MONTH(PAY_DATE)/10)+CAST(MONTH(PAY_DATE) AS VARCHAR))";
        var ok = GroupExpressionParser.TryCompile(expression, "PUR_PAY_OTHER", Allowed, out var compiled);
        Assert.True(ok);
        Assert.Equal("(CAST(YEAR([PAY_DATE]) AS VARCHAR)+REPLICATE(0,1-MONTH([PAY_DATE])/10)+CAST(MONTH([PAY_DATE]) AS VARCHAR))", compiled);
    }

    [Fact]
    public void Compiles_DatePartCastChain()
    {
        const string expression = "(CAST(DATEPART(year,QUOTE_DATE) AS VARCHAR)+CAST(DATEPART(week,QUOTE_DATE) AS VARCHAR))";
        var ok = GroupExpressionParser.TryCompile(expression, "COP_QUOTE_M", Allowed, out var compiled);
        Assert.True(ok);
        Assert.Equal("(CAST(DATEPART(year,[QUOTE_DATE]) AS VARCHAR)+CAST(DATEPART(week,[QUOTE_DATE]) AS VARCHAR))", compiled);
    }

    [Fact]
    public void Compiles_SimpleCase()
    {
        const string expression = "case PUR_PAY_OTHER.FINISHED_TAG when 1 then 'YES' else 'NO' end";
        var ok = GroupExpressionParser.TryCompile(expression, "PUR_PAY_OTHER", Allowed, out var compiled);
        Assert.True(ok);
        Assert.Equal("CASE [FINISHED_TAG] WHEN 1 THEN N'YES' ELSE N'NO' END", compiled);
    }

    [Fact]
    public void Compiles_SearchedCase()
    {
        const string expression = "case when QTY>10 then 'big' else 'small' end";
        var ok = GroupExpressionParser.TryCompile(expression, "HRM_PLAN_M", Allowed, out var compiled);
        Assert.True(ok);
        Assert.Equal("CASE WHEN [QTY] > 10 THEN N'big' ELSE N'small' END", compiled);
    }

    [Fact]
    public void Compiles_ConvertWithStyle()
    {
        const string expression = "CONVERT(varchar(7),PAY_DATE,120)";
        var ok = GroupExpressionParser.TryCompile(expression, "PUR_PAY_OTHER", Allowed, out var compiled);
        Assert.True(ok);
        Assert.Equal("CONVERT(VARCHAR(7),[PAY_DATE], 120)", compiled);
    }

    [Fact]
    public void Rejects_UnknownFunction()
    {
        Assert.False(GroupExpressionParser.TryCompile("foo(PAY_DATE)", "PUR_PAY_OTHER", Allowed, out _));
    }

    [Fact]
    public void Rejects_NonWhitelistedColumn()
    {
        Assert.False(GroupExpressionParser.TryCompile("SECRET_COL", "HRM_PLAN_M", Allowed, out _));
    }

    [Fact]
    public void Rejects_WrongTablePrefix()
    {
        Assert.False(GroupExpressionParser.TryCompile("OTHER_TABLE.DEPT_ID", "HRM_PLAN_M", Allowed, out _));
    }

    [Fact]
    public void Rejects_SqlInjectionFragments()
    {
        Assert.False(GroupExpressionParser.TryCompile("1; DROP TABLE x", "HRM_PLAN_M", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("DEPT_ID -- comment", "HRM_PLAN_M", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("DEPT_ID' OR '1'='1", "HRM_PLAN_M", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("(SELECT 1)", "HRM_PLAN_M", Allowed, out _));
    }

    [Fact]
    public void Rejects_MalformedExpressions()
    {
        Assert.False(GroupExpressionParser.TryCompile("", "HRM_PLAN_M", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("   ", "HRM_PLAN_M", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("'unclosed", "HRM_PLAN_M", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("(DEPT_ID", "HRM_PLAN_M", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("case DEPT_ID when 1 then 'a'", "HRM_PLAN_M", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("case when DEPT_ID=1 'a' end", "HRM_PLAN_M", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("DATEPART(quarter,PAY_DATE)", "PUR_PAY_OTHER", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("CAST(PAY_DATE AS int)", "PUR_PAY_OTHER", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("DEPT_ID LIKE 'a%'", "HRM_PLAN_M", Allowed, out _));
        Assert.False(GroupExpressionParser.TryCompile("DEPT_ID + @p", "HRM_PLAN_M", Allowed, out _));
    }

    [Fact]
    public void Rejects_DeepNesting()
    {
        var expression = string.Concat(Enumerable.Repeat("(", 20)) + "DEPT_ID" + string.Concat(Enumerable.Repeat(")", 20));
        Assert.False(GroupExpressionParser.TryCompile(expression, "HRM_PLAN_M", Allowed, out _));
    }
}
