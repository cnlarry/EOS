using EOS.API.Tests.Tools;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 影子对拍的**比对键**纪律（ADR-014 P1-09 残留）。
///
/// 四键改造后同一个 `(料号, 库别)` 会有多行（库位 / 批次维度）。比对是按 key 分组后
/// **按出现顺序逐行配对**的：键若不唯一，顺序不同就报假差异、顺序巧合就掩盖真差异。
/// 两键口径在"存量全在哨兵行"时恰好等价，所以过去一直看不出问题——这两条用例锁住
/// "一旦键不唯一就必须大声失败"，不让它退化回静默。
/// </summary>
public sealed class EffectShadowRunnerCompareTests
{
    private static EffectShadowRunner.ShadowRow Row(string key, params (string Column, object? Value)[] cells)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, value) in cells)
            map[column] = value;
        return new EffectShadowRunner.ShadowRow(key, map);
    }

    [Fact]
    public void 同键多行时判为不可信而不是逐行配对()
    {
        var oldRows = new[]
        {
            Row("P1|CP", ("QTY", 10d)),
            Row("P1|CP", ("QTY", 20d)),
        };
        var newRows = new[]
        {
            Row("P1|CP", ("QTY", 20d)),
            Row("P1|CP", ("QTY", 10d)),
        };

        var diff = EffectShadowRunner.CompareTable(130104, "APPROVE_EFFECT", "INV_PRO_DEPOT", oldRows, newRows);

        // 顺序调换不该被当成"两边一致"，而应显式落一条不可归一化的差异
        var guard = Assert.Single(diff.Diffs);
        Assert.Equal("*dupkey*", guard.Field);
        Assert.False(guard.Normalized);
        Assert.Equal("diff", guard.Verdict);
        Assert.Contains("比对键不唯一", guard.Decision);
    }

    [Fact]
    public void 键唯一时仍按字段逐项比对()
    {
        var oldRows = new[]
        {
            Row("P1|CP|-|", ("QTY", 10d)),
            Row("P1|CP|A-R1-B1|LOT-A", ("QTY", 20d)),
        };
        var newRows = new[]
        {
            Row("P1|CP|-|", ("QTY", 10d)),
            Row("P1|CP|A-R1-B1|LOT-A", ("QTY", 25d)),
        };

        var diff = EffectShadowRunner.CompareTable(130104, "APPROVE_EFFECT", "INV_PRO_DEPOT", oldRows, newRows);

        // 只有第二个键的 QTY 变了：既证明四键下同料号同库别的两行被分开比，
        // 也证明守卫没有把正常比对一并拦掉
        var only = Assert.Single(diff.Diffs);
        Assert.Equal("QTY", only.Field);
        Assert.Equal("P1|CP|A-R1-B1|LOT-A", only.Key);
    }

    [Fact]
    public void 四键下逐行一致时不产生任何差异()
    {
        var rows = new[]
        {
            Row("P1|CP|-|", ("QTY", 10d)),
            Row("P1|CP|A-R1-B1|", ("QTY", 5d)),
            Row("P1|CP|A-R1-B1|LOT-A", ("QTY", 7d)),
        };

        var diff = EffectShadowRunner.CompareTable(
            130104, "APPROVE_EFFECT", "INV_PRO_DEPOT", rows, rows.Select(row => Row(row.Key, row.Cells.Select(pair => (pair.Key, pair.Value)).ToArray())).ToArray());

        Assert.Empty(diff.Diffs);
    }
}
