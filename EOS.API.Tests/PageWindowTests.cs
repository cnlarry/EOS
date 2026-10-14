using EOS.API.Data.Query;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 分页偏移量：页号普遍只夹下界（<c>Math.Max(1, page)</c>）而不夹上界，
/// 于是 <c>(page-1)*pageSize</c> 这个 int 乘法在页号取到 int 上界时溢出成负数，
/// SQL Server 报 "@Offset 附近有语法错误"，表现在接口上是 500。
///
/// 为什么这些断言必须有：常规的 0 / -1 / 空值早被引擎各自的归一化挡住，
/// 针对常规边界的断言天然是绿的——真正会坏事的是 int 上界那一档，
/// 而它恰恰是"谁也不会去构造"的输入。
/// </summary>
public sealed class PageWindowTests
{
    [Theory]
    [InlineData(1, 20, 0)]
    [InlineData(2, 20, 20)]
    [InlineData(3, 100, 200)]
    [InlineData(0, 20, 0)]
    [InlineData(-5, 20, 0)]
    [InlineData(100000, 100, 9999900)]
    public void ResolveOffset_常规页号_与普通乘法一致(int page, int pageSize, int expected)
    {
        Assert.Equal(expected, PageWindow.ResolveOffset(page, pageSize));
    }

    [Theory]
    [InlineData(int.MaxValue, 100)]
    [InlineData(int.MaxValue, 10)]
    [InlineData(int.MaxValue - 1, 100)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ResolveOffset_页号取到上界_不溢出成负数(int page, int pageSize)
    {
        var offset = PageWindow.ResolveOffset(page, pageSize);

        // 判别点：改回 int 乘法后 (2147483646)*100 溢出为 -200，第一个断言即失败
        Assert.True(offset > 0, $"溢出会落成负数，随即被 SQL Server 拒绝；实得 {offset}");
        Assert.Equal(int.MaxValue, offset);
    }

    [Fact]
    public void ResolveOffset_任意超大组合_结果始终非负()
    {
        foreach (var page in new[] { int.MaxValue, int.MaxValue / 2, 1_000_000_000, 2_000_000_000 })
        {
            foreach (var size in new[] { 10, 100, 200 })
            {
                var offset = PageWindow.ResolveOffset(page, size);
                Assert.InRange(offset, 0, int.MaxValue);
            }
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, -10)]
    public void ResolveOffset_页大小非正_按零宽处理而不抛(int page, int pageSize)
    {
        Assert.Equal(0, PageWindow.ResolveOffset(page, pageSize));
    }
}
