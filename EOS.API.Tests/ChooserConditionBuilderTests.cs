using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 统一选择器高级查询条件构造器纯逻辑测试（不依赖数据库）：
/// 覆盖运算符映射、参数化、AND/OR 连接、字段/运算符/数量白名单与非法输入。
/// </summary>
public sealed class ChooserConditionBuilderTests
{
    private static readonly IReadOnlyDictionary<string, string> Columns =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["T_ID"] = "LTRIM(RTRIM(T_ID))",
            ["T_DESC"] = "LTRIM(RTRIM(T_DESC))",
        };

    private static SqlCommand NewCommand() => new();

    [Fact]
    public void Build_ReturnsNull_WhenNoConditions()
    {
        var command = NewCommand();
        Assert.Null(ChooserConditionBuilder.Build(null, Columns, command));
        Assert.Null(ChooserConditionBuilder.Build([], Columns, command));
        Assert.Empty(command.Parameters);
    }

    [Theory]
    [InlineData("eq", "=")]
    [InlineData("ne", "<>")]
    [InlineData("gt", ">")]
    [InlineData("gte", ">=")]
    [InlineData("lt", "<")]
    [InlineData("lte", "<=")]
    public void Build_ComparisonOperators_MapToSql(string op, string sqlOperator)
    {
        var command = NewCommand();
        var predicate = ChooserConditionBuilder.Build(
            [new UnifiedChooserCondition("T_ID", op, "COMPANY")], Columns, command);
        Assert.Contains($"LTRIM(RTRIM(T_ID)) {sqlOperator} @qc0", predicate);
        Assert.Equal("COMPANY", command.Parameters["@qc0"].Value);
    }

    [Theory]
    [InlineData("contains", "%value%")]
    [InlineData("notcontains", "%value%")]
    [InlineData("startswith", "value%")]
    [InlineData("endswith", "%value")]
    public void Build_LikeOperators_BuildPattern(string op, string expectedPattern)
    {
        var command = NewCommand();
        var predicate = ChooserConditionBuilder.Build(
            [new UnifiedChooserCondition("T_DESC", op, "value")], Columns, command);
        Assert.Contains($"LTRIM(RTRIM(T_DESC)) {(op == "notcontains" ? "NOT LIKE" : "LIKE")} @qc0", predicate);
        Assert.Equal(expectedPattern, command.Parameters["@qc0"].Value);
    }

    [Fact]
    public void Build_EmptyAndNotEmpty_HaveNoParameters()
    {
        var command = NewCommand();
        var empty = ChooserConditionBuilder.Build([new UnifiedChooserCondition("T_ID", "empty")], Columns, command);
        Assert.Contains("IS NULL", empty);
        var notEmpty = ChooserConditionBuilder.Build([new UnifiedChooserCondition("T_ID", "notempty")], Columns, command);
        Assert.Contains("IS NOT NULL", notEmpty);
        Assert.Empty(command.Parameters);
    }

    [Fact]
    public void Build_Between_AddsTwoParameters()
    {
        var command = NewCommand();
        var predicate = ChooserConditionBuilder.Build(
            [new UnifiedChooserCondition("T_ID", "between", "A", "Z")], Columns, command);
        Assert.Contains("BETWEEN @qc0 AND @qc0b", predicate);
        Assert.Equal("A", command.Parameters["@qc0"].Value);
        Assert.Equal("Z", command.Parameters["@qc0b"].Value);
    }

    [Fact]
    public void Build_JoinsRowsWithAndOr()
    {
        var command = NewCommand();
        var predicate = ChooserConditionBuilder.Build(
            [
                new UnifiedChooserCondition("T_ID", "eq", "A"),
                new UnifiedChooserCondition("T_DESC", "contains", "B", Logic: "or"),
                new UnifiedChooserCondition("T_DESC", "eq", "C"),
            ],
            Columns,
            command);
        Assert.StartsWith("(", predicate);
        Assert.EndsWith(")", predicate);
        Assert.Contains(" OR ", predicate);
        Assert.Contains(" AND ", predicate);
        Assert.Equal(3, command.Parameters.Count);
    }

    [Fact]
    public void Build_RejectsUnknownField()
    {
        var command = NewCommand();
        Assert.Throws<ArgumentException>(() =>
            ChooserConditionBuilder.Build([new UnifiedChooserCondition("NOT_A_COLUMN", "eq", "x")], Columns, command));
    }

    [Fact]
    public void Build_RejectsUnknownOperator()
    {
        var command = NewCommand();
        Assert.Throws<ArgumentException>(() =>
            ChooserConditionBuilder.Build([new UnifiedChooserCondition("T_ID", "sideways", "x")], Columns, command));
    }

    [Fact]
    public void Build_RejectsMoreThanTwentyConditions()
    {
        var command = NewCommand();
        var conditions = Enumerable.Range(0, 21)
            .Select(_ => new UnifiedChooserCondition("T_ID", "eq", "x"))
            .ToArray();
        Assert.Throws<ArgumentException>(() => ChooserConditionBuilder.Build(conditions, Columns, command));
    }
}
