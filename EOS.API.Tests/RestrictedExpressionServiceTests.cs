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
    [InlineData("SUM(A.QTY)", "未知函数")]
    [InlineData("A.QTY; DROP TABLE X", "分号")]
    [InlineData("A.QTY -- x", "分号或注释")]
    [InlineData("A.QTY +", "不完整")]
    [InlineData("(A.QTY", "括号不匹配")]
    [InlineData("UNKNOWN_FN(A.QTY)", "未知函数")]
    public void VirtualArithmetic_InvalidForms_Rejected(string expression, string expectedErrorPart)
    {
        Assert.False(VirtualArithmeticParser.TryParse(expression, out _, out var error));
        Assert.Contains(expectedErrorPart, error);
    }

    [Theory]
    [InlineData("case when A.X=1 then 1 else 0 end")]
    [InlineData("case when A.X<>'' then A.X else 'RMB' end")]
    [InlineData("case when A.X<0 AND -A.X<=B.QTY then ROUND(-A.X,0) else 0 end")]
    [InlineData("case A.X when 0 then 0 else ROUND(B.QTY/A.X,2) end")]
    [InlineData("CASE WHEN A.UNIT_PCS=0 THEN B.QTY ELSE CEILING(B.QTY/A.UNIT_PCS) END")]
    [InlineData("DATENAME(WEEKDAY, A.COUNT_DATE)")]
    [InlineData("DATEDIFF(MM, IN_DATE, GETDATE())")]
    [InlineData("ROUND(A.RETURN_QTY-A.RETURNED_QTY,2)")]
    [InlineData("A.T_ID+'.'+A.F_ID")]
    [InlineData("case when A.X<0 then -A.X else 0 end")]
    [InlineData("rtrim(cast(A.PACK_QTY as char(20)))+'*'+rtrim(cast(A.PACK_JS as char(20)))")]
    [InlineData("dbo.f_get_unit_type_desc(A.UNIT_TYPE)")]
    [InlineData("ltrim(A.X)")]
    public void VirtualArithmetic_ConditionalAndFunctions_Parse(string expression)
    {
        Assert.True(VirtualArithmeticParser.TryParse(expression, out _, out var error), error);
    }

    [Theory]
    [InlineData("case when A.X=1 then 1", "CASE 缺少 END")]
    [InlineData("case A.X when 0 0 else 1 end", "CASE WHEN 缺少 THEN")]
    [InlineData("DATENAME(A.COUNT_DATE)", "第一个参数必须是字符串字面量")]
    [InlineData("ROUND(A.X)", "参数个数须为 2")]
    [InlineData("DATEDIFF(MM, A.X)", "参数个数须为 3")]
    [InlineData("CASE A.X THEN 1 ELSE 0 END", "CASE 至少需要一个 WHEN")]
    [InlineData("cast(A.X as unknown_type)", "目标类型不在白名单内")]
    [InlineData("cast(A.X int)", "CAST 缺少 AS 类型")]
    public void VirtualArithmetic_ConditionalInvalid_Rejected(string expression, string expectedErrorPart)
    {
        Assert.False(VirtualArithmeticParser.TryParse(expression, out _, out var error));
        Assert.Contains(expectedErrorPart, error);
    }

    [Fact]
    public void ParseStructure_VirtualReference_RoundTrips()
    {
        var structure = RestrictedExpressionService.ParseStructure(RestrictedExpressionKind.VirtualExp, " CLIENT.CLIENT_NAME ");
        Assert.Equal("virtual_exp", structure.Kind);
        Assert.Equal(ExpressionStructureModes.Reference, structure.Mode);
        Assert.Equal("CLIENT", structure.Table);
        Assert.Equal("CLIENT_NAME", structure.Column);
    }

    [Fact]
    public void ParseStructure_VirtualArithmetic_NotBuildable()
    {
        var structure = RestrictedExpressionService.ParseStructure(RestrictedExpressionKind.VirtualExp, "A.QTY+B.QTY");
        Assert.Equal(ExpressionStructureModes.Arithmetic, structure.Mode);
        Assert.Null(structure.Table);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1; DROP TABLE X")]
    public void ParseStructure_VirtualInvalid_FallsBackToRaw(string expression)
    {
        Assert.Equal(ExpressionStructureModes.Raw,
            RestrictedExpressionService.ParseStructure(RestrictedExpressionKind.VirtualExp, expression).Mode);
    }

    [Fact]
    public void ParseStructure_ConvertFunction_NormalizesRegistryName()
    {
        var structure = RestrictedExpressionService.ParseStructure(RestrictedExpressionKind.ConvertFunction, "F_GET_EMP_NAME_BY_ID");
        Assert.Equal(ExpressionStructureModes.Registry, structure.Mode);
        Assert.Equal("f_get_emp_name_by_id", structure.Function);
    }

    [Theory]
    [InlineData("CONVERT(X)")]
    [InlineData("f_get_unknown_fn")]
    public void ParseStructure_ConvertFunctionOutsideRegistry_Raw(string expression)
    {
        Assert.Equal(ExpressionStructureModes.Raw,
            RestrictedExpressionService.ParseStructure(RestrictedExpressionKind.ConvertFunction, expression).Mode);
    }

    [Fact]
    public void ParseStructure_DataSourceSql_UnquotesWhereLiteral()
    {
        var structure = RestrictedExpressionService.ParseStructure(
            RestrictedExpressionKind.DataSourceSql,
            "SELECT PRO_NO,PRO_NAME FROM PRODUCT WHERE PRO_TYPE = 'O''K' ORDER BY PRO_NO DESC");
        Assert.Equal(ExpressionStructureModes.TableSql, structure.Mode);
        var dataSource = structure.DataSource;
        Assert.NotNull(dataSource);
        Assert.Equal("PRODUCT", dataSource.Table);
        Assert.Equal(new[] { "PRO_NO", "PRO_NAME" }, dataSource.Columns);
        Assert.Equal("PRO_TYPE", dataSource.WhereColumn);
        Assert.Equal("O'K", dataSource.WhereValue);
        Assert.True(dataSource.WhereValueIsString);
        Assert.Equal("PRO_NO", dataSource.OrderColumn);
        Assert.Equal("DESC", dataSource.OrderDirection);
    }

    [Fact]
    public void ParseStructure_DataSourceSql_NumericLiteralHasNoQuotes()
    {
        var structure = RestrictedExpressionService.ParseStructure(
            RestrictedExpressionKind.DataSourceSql,
            "SELECT G_IDX FROM SYSDG WHERE G_KIND = 2");
        Assert.NotNull(structure.DataSource);
        Assert.Equal("2", structure.DataSource.WhereValue);
        Assert.False(structure.DataSource.WhereValueIsString);
    }

    [Theory]
    [InlineData("SELECT 'P' AS T_KIND UNION SELECT 'S'", ExpressionStructureModes.LiteralUnion)]
    [InlineData("SELECT 1", ExpressionStructureModes.Raw)]
    [InlineData("DELETE FROM SYSDG", ExpressionStructureModes.Raw)]
    public void ParseStructure_DataSourceSql_NonBuildableForms(string expression, string expectedMode)
    {
        var structure = RestrictedExpressionService.ParseStructure(RestrictedExpressionKind.DataSourceSql, expression);
        Assert.Equal(expectedMode, structure.Mode);
        Assert.Null(structure.DataSource);
    }
}
