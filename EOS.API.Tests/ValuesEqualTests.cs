using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public sealed class ValuesEqualTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, 1, false)]
    [InlineData("1", 1, true)]
    [InlineData(1, "1", true)]
    [InlineData("1.5", 1.5, true)]
    [InlineData("1.50", 1.5, true)]
    [InlineData(1.0, 1, true)]
    [InlineData(2.5, 2, false)]
    [InlineData("1", 2, false)]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData(1.5, 1.5, true)]
    [InlineData(1.5, 1.5000001, true)]
    [InlineData(10, 10, true)]
    [InlineData("2026-08-09", "2026-08-09", true)]
    public void ValuesEqual_MixedStringNumeric(object? left, object? right, bool expected)
    {
        Assert.Equal(expected, EOS.API.Data.WorkbenchSql.ValuesEqual(left, right));
    }

    /// <summary>
    /// 空值的两种表示必须视为相等：库里存的是空串（字符列读出后 Trim 成空串），
    /// 表单提交的是空/纯空白（解析成 null）。两侧不当成同一个"没有值"，
    /// 保存时就会把没被动过的字段判成"内容已被他人修改"。
    /// </summary>
    [Theory]
    [InlineData(null, "", true)]
    [InlineData("", null, true)]
    [InlineData(null, "   ", true)]
    [InlineData("   ", null, true)]
    [InlineData(null, "华精仓", false)]
    [InlineData("华精仓", null, false)]
    [InlineData(null, 0, false)]
    [InlineData(null, false, false)]
    [InlineData("", "  ", true)]
    public void ValuesEqual_NullAndBlank(object? left, object? right, bool expected)
    {
        Assert.Equal(expected, EOS.API.Data.WorkbenchSql.ValuesEqual(left, right));
    }
}
