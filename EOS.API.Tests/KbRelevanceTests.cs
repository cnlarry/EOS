using EOS.API.Data;
using EOS.API.Features.Assistant.Kb;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 相关性截断（<see cref="KbRelevance"/>）的边界值。
///
/// <para>
/// 用例里的距离**只用二进制可精确表示的数**（0.5 / 0.25 / 0.75 / 0.125）：相似度差是浮点减法，
/// 用 0.2 与 0.35 这种数去测"正好等于边界"会得到 0.15000000000000002，
/// 于是断言测的是浮点误差而不是规则本身。
/// </para>
/// </summary>
public sealed class KbRelevanceTests
{
    private static KbHit Hit(double distance, int serial = 1) =>
        new(DocId: serial, Title: $"片段{serial}", SourceUri: null, SerialNo: serial, Content: "内容", Distance: distance);

    [Fact]
    public void Margin_Zero_Keeps_Only_The_Best()
    {
        var hits = new[] { Hit(0.5, 1), Hit(0.625, 2), Hit(0.75, 3) };

        var kept = KbRelevance.Cut(hits, 0);

        Assert.Equal(1, kept.Count);
        Assert.Equal(0.5, kept[0].Distance);
    }

    [Fact]
    public void Margin_Zero_Keeps_Ties_At_The_Best_Distance()
    {
        // 并列最佳要一起留：按顺序切成半截，同一批数据在两次检索里会给出不同结果
        var hits = new[] { Hit(0.5, 1), Hit(0.5, 2), Hit(0.75, 3) };

        var kept = KbRelevance.Cut(hits, 0);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Keeps_Within_Margin_And_Drops_The_Tail()
    {
        // 相似度差 0.125 ≤ 0.25 → 留；0.25 边界见下一条；0.5 > 0.25 → 丢
        var hits = new[] { Hit(0.5, 1), Hit(0.625, 2), Hit(1.0, 3) };

        var kept = KbRelevance.Cut(hits, 25);

        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, hit => hit.SerialNo == 3);
    }

    [Fact]
    public void Boundary_Is_Inclusive()
    {
        // 相似度差正好等于幅度 → 留（"不超过"是闭区间；写成开区间会让这条参数的实际效果
        // 比它对管理员承诺的小一档，而没人会去核对这一档）
        var hits = new[] { Hit(0.5, 1), Hit(0.75, 2) };

        Assert.Equal(2, KbRelevance.Cut(hits, 25).Count);
        Assert.Single(KbRelevance.Cut(hits, 24));
    }

    [Fact]
    public void Margin_Hundred_Keeps_Typical_Hits()
    {
        var hits = new[] { Hit(0.125, 1), Hit(0.5, 2), Hit(0.875, 3) };

        Assert.Equal(3, KbRelevance.Cut(hits, 100).Count);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(500)]
    public void Out_Of_Range_Margin_Is_Clamped(int margin)
    {
        var hits = new[] { Hit(0.125, 1), Hit(0.5, 2), Hit(0.875, 3) };

        var kept = KbRelevance.Cut(hits, margin);

        // 越界按边界处理：负数当 0（最严），大于 100 当 100（基本不截断）
        Assert.Equal(margin < 0 ? 1 : 3, kept.Count);
    }

    [Fact]
    public void Cut_Does_Not_Depend_On_Incoming_Order()
    {
        var ascending = new[] { Hit(0.5, 1), Hit(0.75, 2), Hit(1.0, 3) };
        var descending = new[] { Hit(1.0, 3), Hit(0.75, 2), Hit(0.5, 1) };

        // 以"全体最佳"为参照，而不是"第一条"：上游换了排序策略也不会把参照物弄错
        Assert.Equal(
            KbRelevance.Cut(ascending, 25).Select(hit => hit.SerialNo).OrderBy(id => id),
            KbRelevance.Cut(descending, 25).Select(hit => hit.SerialNo).OrderBy(id => id));
    }

    [Fact]
    public void Empty_And_Single_Are_Passed_Through()
    {
        Assert.Empty(KbRelevance.Cut([], 15));
        Assert.Single(KbRelevance.Cut([Hit(1.5)], 0));
    }
}
