using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 报表过程数据源的结果集列白名单：过程返回什么就下发什么，会绕过成本/保密/禁止字段三类
/// 字段级过滤（列清单是它们唯一的载体）。这里钉住"未登记列一律剔除、只有全部未登记才拒绝"。
/// </summary>
public sealed class ReportSpColumnWhitelistTests
{
    private static readonly HashSet<string> Allowed =
        new(["PRO_NO", "PRO_NAME", "QTY"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void UnregisteredColumns_AreDropped_CaseInsensitiveMatchKept()
    {
        var (kept, dropped) = ReportRepository.FilterSpResultColumns(
            ["pro_no", "SECRET_COST", "QTY", "INTERNAL_NOTE"], Allowed);

        Assert.Equal([0, 2], kept);
        Assert.Equal(["SECRET_COST", "INTERNAL_NOTE"], dropped);
    }

    [Fact]
    public void AllColumnsRegistered_NothingDropped()
    {
        var (kept, dropped) = ReportRepository.FilterSpResultColumns(["PRO_NO", "QTY"], Allowed);

        Assert.Equal([0, 1], kept);
        Assert.Empty(dropped);
    }

    [Fact]
    public void NoColumnRegistered_KeepsNothing_SoCallerRejectsTheReport()
    {
        var (kept, dropped) = ReportRepository.FilterSpResultColumns(["A", "B"], Allowed);

        Assert.Empty(kept);
        Assert.Equal(2, dropped.Count);
    }

    [Fact]
    public void EmptyResultSet_KeepsNothing()
    {
        var (kept, dropped) = ReportRepository.FilterSpResultColumns([], Allowed);

        Assert.Empty(kept);
        Assert.Empty(dropped);
    }
}
