using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class RestrictedExpressionServiceTests
{
    [Theory]
    [InlineData("f_get_table_kind_desc", null)]
    [InlineData("f_get_emp_name_by_id", null)]
    [InlineData("f_get_approve_state_desc", null)]
    [InlineData("f_get_unknown_fn", "不在受控注册表内")]
    [InlineData("f_get_emp_name_by_id(EMP_ID)", "必须是受控注册表内的函数名")]
    [InlineData("drop table x", "必须是受控注册表内的函数名")]
    [InlineData("", "必须是受控注册表内的函数名")]
    [InlineData("  f_get_emp_name_by_id  ", null)]
    public void ConvertFunction_Registry(string expression, string? expectedErrorPart)
    {
        var error = RestrictedExpressionService.ValidateConvertFunction(expression);
        if (expectedErrorPart is null)
            Assert.Null(error);
        else
            Assert.Contains(expectedErrorPart, error);
    }

    [Fact]
    public void DataSourceSql_SingleTableSelect_Valid()
    {
        Assert.True(RestrictedExpressionService.TryParseDataSourceSql(
            "SELECT G_IDX,G_DESC FROM SYSDG", out var parsed, out var error));
        Assert.Equal("SYSDG", parsed.Table);
        Assert.Equal(new[] { "G_IDX", "G_DESC" }, parsed.Columns);
        Assert.Null(parsed.WhereColumn);
        Assert.Null(parsed.OrderColumn);
        Assert.Equal("", error);
    }

    [Fact]
    public void DataSourceSql_WhereAndOrder_Valid()
    {
        Assert.True(RestrictedExpressionService.TryParseDataSourceSql(
            "SELECT PRO_NO,PRO_NAME FROM PRODUCT WHERE PRO_TYPE = '1' ORDER BY PRO_NO DESC",
            out var parsed, out _));
        Assert.Equal("PRODUCT", parsed.Table);
        Assert.Equal("PRO_TYPE", parsed.WhereColumn);
        Assert.Equal("'1'", parsed.WhereValue);
        Assert.Equal("PRO_NO", parsed.OrderColumn);
        Assert.Equal("DESC", parsed.OrderDirection);
    }

    [Theory]
    [InlineData("DELETE FROM SYSDG", "必须以 SELECT 开头")]
    [InlineData("SELECT * FROM SYSDG", "SELECT 列清单无效")]
    [InlineData("SELECT G_IDX FROM SYSDG; DROP TABLE X", "不允许分号")]
    [InlineData("SELECT G_IDX FROM SYSDG -- comment", "不允许分号或注释")]
    [InlineData("SELECT G_IDX", "缺少 FROM")]
    [InlineData("SELECT G_IDX FROM SYSDG WHERE G_IDX = (SELECT 1)", "仅支持")]
    [InlineData("SELECT G_IDX FROM SYSDG WHERE G_IDX = CONCAT('a','b')", "仅支持")]
    [InlineData("SELECT G_IDX FROM SYSDG UNION SELECT * FROM OTHER", "仅允许纯字面量 UNION")]
    [InlineData("SELECT G_IDX FROM SYSDG JOIN OTHER ON 1=1", "FROM 表名无效")]
    public void DataSourceSql_InvalidForms_Rejected(string sql, string expectedErrorPart)
    {
        Assert.False(RestrictedExpressionService.TryParseDataSourceSql(sql, out _, out var error));
        Assert.Contains(expectedErrorPart, error);
    }

    [Fact]
    public void DataSourceSql_LiteralUnion_Valid()
    {
        var sql = "SELECT 'P' AS T_KIND, '主表' AS T_KIND_DESC UNION SELECT 'S', '副表' UNION SELECT 'O', '其它' UNION SELECT 'V', '视图'";
        Assert.True(RestrictedExpressionService.TryParseDataSourceSql(sql, out var parsed, out _));
        Assert.Null(parsed.Table);
    }

    [Fact]
    public void VirtualExp_SyntaxRouting_InvalidReferenceRejectedBeforeDb()
    {
        // 语法层校验不依赖数据库：非「表.列」形态直接拒绝
        Assert.False(VirtualExpressionParser.TryParseExpression("CLIENT.CLIENT_NAME + 1", out _, out _));
        Assert.False(VirtualExpressionParser.TryParseExpression("CLIENT_NAME", out _, out _));
        Assert.False(VirtualExpressionParser.TryParseExpression("1; DROP TABLE X", out _, out _));
    }

    [Theory]
    [InlineData("COP_ACCOUNT_M.SUM_AMOUNT-COP_ACCOUNT_M.RECEIVE_AMOUNT", 2)]
    [InlineData("A.QTY*B.PRICE", 2)]
    [InlineData("(A.X+B.Y-C.Z)", 3)]
    [InlineData("A.QTY + 1", 1)]
    [InlineData("100", 0)]
    [InlineData("'RMB'", 0)]
    [InlineData("-A.QTY", 1)]
    public void VirtualArithmetic_ValidForms_Parse(string expression, int expectedRefs)
    {
        Assert.True(VirtualArithmeticParser.TryParse(expression, out var tokens, out var error), error);
        Assert.Equal(expectedRefs, tokens.Count(token => token.Kind == "Ref"));
    }

    [Theory]
    [InlineData("A.QTY+'x'", "字符串常量仅允许独立使用")]
    [InlineData("SUM(A.QTY)", "裸标识符")]
    [InlineData("case when A.X=1 then 1 else 0 end", "裸标识符")]
    [InlineData("A.QTY; DROP TABLE X", "分号")]
    [InlineData("A.QTY -- x", "分号或注释")]
    [InlineData("A.QTY +", "不完整")]
    [InlineData("(A.QTY", "括号不匹配")]
    [InlineData("A.QTY = B.QTY", "不允许的字符")]
    public void VirtualArithmetic_InvalidForms_Rejected(string expression, string expectedErrorPart)
    {
        Assert.False(VirtualArithmeticParser.TryParse(expression, out _, out var error));
        Assert.Contains(expectedErrorPart, error);
    }
}
