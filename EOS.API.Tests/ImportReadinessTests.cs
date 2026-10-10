using EOS.API.Features.Import;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 前置资料就绪度的判定：这段逻辑错了**只表现为"该提醒的没提醒"**——界面上看不出来，
/// 所以结论全部钉在这里，而不是靠人眼观察。
/// </summary>
public sealed class ImportReadinessTests
{
    private static ImportReadinessItem Item(string field, string sourceTable, long rows) =>
        new(field, $"{field} 的标签", sourceTable, sourceTable, rows);

    [Fact]
    public void 只报空表_有数据的来源不提醒()
    {
        var items = ImportService.SelectEmptySources(
            [Item("CURR_ID", "CURR", 28), Item("SALES_ID", "SYSDN", 0)], "CLIENT");

        Assert.Equal(["SYSDN"], items.Select(item => item.SourceTable));
    }

    [Fact]
    public void 自引用不算前置资料没导()
    {
        // 客户基本资料的 CLIENT_ID 指向它自己：导的就是这张表，提醒它是循环说法
        Assert.Empty(ImportService.SelectEmptySources([Item("CLIENT_ID", "CLIENT", 0)], "CLIENT"));
    }

    [Fact]
    public void 同一张空表被多个字段引用时只提醒一次()
    {
        var items = ImportService.SelectEmptySources(
            [Item("PRO_ID", "CUS_PRODUCT", 0), Item("PRO_NO", "CUS_PRODUCT", 0)], "CLIENT");

        Assert.Single(items);
        Assert.Equal("PRO_ID", items[0].Field);
    }

    [Fact]
    public void 大小写不同也算同一张表()
    {
        var items = ImportService.SelectEmptySources(
            [Item("A", "CURR", 0), Item("B", "curr", 0)], "CLIENT");

        Assert.Single(items);
    }

    [Fact]
    public void 全部就绪时返回空_不产生噪音提醒()
    {
        Assert.Empty(ImportService.SelectEmptySources(
            [Item("CURR_ID", "CURR", 28), Item("TAX_ID", "TAX", 4)], "CLIENT"));
    }

    [Fact]
    public void 提醒条数有上限_不会一次糊满屏()
    {
        var many = Enumerable.Range(0, 40).Select(index => Item($"F{index}", $"T{index}", 0)).ToList();

        Assert.Equal(20, ImportService.SelectEmptySources(many, "CLIENT").Count);
    }
}
