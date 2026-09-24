using EOS.API.Data.Query;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 工作台列表查询与统一选择器共用的算子表：这里钉住每个运算符产出的 SQL 形状与参数，
/// 任何一次算子语义调整都会先在这里失败，避免两条入口各改一遍、漏一处产生口径分叉。
/// </summary>
public sealed class QueryConditionRendererTests
{
    [Theory]
    [InlineData("eq", "=")]
    [InlineData("ne", "<>")]
    [InlineData("gt", ">")]
    [InlineData("gte", ">=")]
    [InlineData("lt", "<")]
    [InlineData("lte", "<=")]
    public void Render_ComparisonOperators_MapToSqlOperator(string op, string expected)
    {
        using var command = new SqlCommand();

        var expression = QueryConditionRenderer.Render("[QTY]", op, "5", null, "@q0", command);

        Assert.Equal($"[QTY] {expected} @q0", expression);
        Assert.Equal("5", command.Parameters["@q0"].Value);
    }

    [Theory]
    [InlineData("contains", "LIKE", "%纸%")]
    [InlineData("notcontains", "NOT LIKE", "%纸%")]
    [InlineData("startswith", "LIKE", "纸%")]
    [InlineData("endswith", "LIKE", "%纸")]
    public void Render_TextOperators_WrapValueIntoPattern(string op, string sqlOperator, string pattern)
    {
        using var command = new SqlCommand();

        var expression = QueryConditionRenderer.Render("[PRO_NAME]", op, "纸", null, "@q0", command);

        Assert.Equal($"[PRO_NAME] {sqlOperator} @q0", expression);
        Assert.Equal(pattern, command.Parameters["@q0"].Value);
    }

    [Fact]
    public void Render_EmptyAndNotEmpty_RegisterNoParameter()
    {
        using var command = new SqlCommand();

        Assert.Equal("([REMARK] IS NULL OR [REMARK]='')",
            QueryConditionRenderer.Render("[REMARK]", "empty", null, null, "@q0", command));
        Assert.Equal("([REMARK] IS NOT NULL AND [REMARK]<>'')",
            QueryConditionRenderer.Render("[REMARK]", "notempty", null, null, "@q0", command));
        Assert.Empty(command.Parameters);
    }

    [Fact]
    public void Render_Between_RegistersTwoParameters()
    {
        using var command = new SqlCommand();

        var expression = QueryConditionRenderer.Render("[QTY]", "between", "1", "9", "@q0", command);

        Assert.Equal("[QTY] BETWEEN @q0 AND @q0b", expression);
        Assert.Equal("1", command.Parameters["@q0"].Value);
        Assert.Equal("9", command.Parameters["@q0b"].Value);
    }

    [Fact]
    public void Render_NullValue_BecomesEmptyString()
    {
        using var command = new SqlCommand();

        QueryConditionRenderer.Render("[QTY]", "eq", null, null, "@q0", command);

        Assert.Equal(string.Empty, command.Parameters["@q0"].Value);
    }

    [Theory]
    [InlineData(" EQ ")]
    [InlineData("Eq")]
    public void Render_OperatorIsTrimmedAndCaseInsensitive(string op)
    {
        using var command = new SqlCommand();

        Assert.Equal("[QTY] = @q0", QueryConditionRenderer.Render("[QTY]", op, "5", null, "@q0", command));
    }

    [Fact]
    public void Render_UnknownOperator_ThrowsWithRawOperatorInMessage()
    {
        using var command = new SqlCommand();

        var error = Assert.Throws<ArgumentException>(
            () => QueryConditionRenderer.Render("[QTY]", "sideways", "5", null, "@q0", command));

        Assert.Contains("sideways", error.Message);
        Assert.Empty(command.Parameters);
    }

    [Fact]
    public void Prefix_FirstConditionAlwaysAnd_SubsequentOrOnlyWhenLogicIsOr()
    {
        Assert.Equal("AND [A]=@q0", QueryConditionRenderer.Prefix(false, "or", "[A]=@q0"));
        Assert.Equal("AND [A]=@q0", QueryConditionRenderer.Prefix(false, "and", "[A]=@q0"));
        Assert.Equal("OR [B]=@q1", QueryConditionRenderer.Prefix(true, "or", "[B]=@q1"));
        Assert.Equal("OR [B]=@q1", QueryConditionRenderer.Prefix(true, "OR", "[B]=@q1"));
        Assert.Equal("AND [B]=@q1", QueryConditionRenderer.Prefix(true, "and", "[B]=@q1"));
        Assert.Equal("AND [B]=@q1", QueryConditionRenderer.Prefix(true, null, "[B]=@q1"));
    }

    [Fact]
    public void Combine_EmptyParts_ReturnsNull()
    {
        Assert.Null(QueryConditionRenderer.Combine([]));
    }

    [Fact]
    public void Combine_StripsFirstPrefixAndWrapsInParentheses()
    {
        var parts = new List<string> { "AND [A]=@q0", "OR [B]=@q1" };

        Assert.Equal("([A]=@q0 OR [B]=@q1)", QueryConditionRenderer.Combine(parts));
    }
}
