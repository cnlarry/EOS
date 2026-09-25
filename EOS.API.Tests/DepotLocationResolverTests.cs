using EOS.API.Data.Inventory;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 入库存放位置的三级退化决策表（纯函数，不连库）。
///
/// 覆盖判据：**每一级各自可被独立命中**——把任一级从实现里去掉，对应用例必须变红；
/// 以及"查不到就回落哨兵、不抛异常"这条底线（`FIXED` 档没有主货位是配置缺口，
/// 不是调用方单据的失败）。
/// </summary>
public sealed class DepotLocationResolverTests
{
    private const string Sentinel = DepotLocationResolver.DefaultSentinel;

    private static LocationResolution Resolve(
        string? storageMode,
        IReadOnlyList<string>? primary = null,
        string? occupied = null,
        string? lastPlaced = null,
        IReadOnlyList<string>? emptyInArea = null,
        string? sentinel = Sentinel)
        => DepotLocationResolver.Resolve(
            new LocationResolutionInput(storageMode, sentinel, primary, occupied, lastPlaced, emptyInArea));

    [Fact]
    public void FIXED_取物料默认货位()
    {
        var result = Resolve(DepotLocationResolver.FixedMode, primary: ["A-01"]);

        Assert.Equal("A-01", result.LocationNo);
        Assert.Equal(LocationResolutionSource.PrimaryLocation, result.Source);
        Assert.False(result.IsSentinel);
    }

    [Fact]
    public void FIXED_多个主货位按传入优先序取第一个()
    {
        Assert.Equal("A-02", Resolve(DepotLocationResolver.FixedMode, primary: ["A-02", "A-01"]).LocationNo);
    }

    [Fact]
    public void FIXED_没有主货位时回落哨兵且不抛异常()
    {
        var result = Resolve(DepotLocationResolver.FixedMode, primary: [], lastPlaced: "R-09", emptyInArea: ["R-01"]);

        // FIXED 不参与三级退化：它只认物料默认货位
        Assert.Equal(Sentinel, result.LocationNo);
        Assert.True(result.IsSentinel);
    }

    [Fact]
    public void RANDOM_第一级_取该物料已占用的位置()
    {
        var result = Resolve(
            DepotLocationResolver.RandomMode,
            occupied: "O-07",
            lastPlaced: "L-03",
            emptyInArea: ["E-01"]);

        Assert.Equal("O-07", result.LocationNo);
        Assert.Equal(LocationResolutionSource.Occupied, result.Source);
    }

    [Fact]
    public void RANDOM_第二级_没有已占用位置时取上次放置位置()
    {
        // 判别性：去掉第 2 级后本用例会落到第 3 级的 "E-01"，随即变红
        var result = Resolve(
            DepotLocationResolver.RandomMode,
            lastPlaced: "L-03",
            emptyInArea: ["E-01"]);

        Assert.Equal("L-03", result.LocationNo);
        Assert.Equal(LocationResolutionSource.LastPlaced, result.Source);
    }

    [Fact]
    public void RANDOM_第三级_前两级都没有时取库区内第一个空位且按传入顺序()
    {
        var result = Resolve(
            DepotLocationResolver.RandomMode,
            emptyInArea: ["R-02", "R-05"]);

        Assert.Equal("R-02", result.LocationNo);
        Assert.Equal(LocationResolutionSource.EmptyInArea, result.Source);
    }

    [Fact]
    public void RANDOM_三级都没有候选时回落哨兵()
    {
        var result = Resolve(DepotLocationResolver.RandomMode);

        Assert.Equal(Sentinel, result.LocationNo);
        Assert.True(result.IsSentinel);
    }

    [Fact]
    public void MIXED_与RANDOM同一口径()
    {
        Assert.Equal("L-03", Resolve(DepotLocationResolver.MixedMode, lastPlaced: "L-03").LocationNo);
        Assert.Equal(Sentinel, Resolve(DepotLocationResolver.MixedMode).LocationNo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData(null)]
    public void 哨兵值与空白不算有效位置(string? candidate)
    {
        // 流水里未记录位置的入库写的是哨兵 "-"：它必须不被当成"上次放置的位置"回填回去
        var result = Resolve(
            DepotLocationResolver.RandomMode,
            occupied: candidate,
            lastPlaced: candidate,
            emptyInArea: ["R-01"]);

        Assert.Equal("R-01", result.LocationNo);
        Assert.Equal(LocationResolutionSource.EmptyInArea, result.Source);
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData(" RANDOM ")]
    public void 存放方式大小写与空白不敏感(string mode)
    {
        var result = Resolve(mode, primary: ["A-01"], lastPlaced: "L-03");

        Assert.Equal(mode.Trim().ToUpperInvariant() == DepotLocationResolver.FixedMode ? "A-01" : "L-03", result.LocationNo);
    }

    [Fact]
    public void 未登记的存放方式不解析_回落哨兵()
    {
        // 位置是库存键的一部分，猜错比不猜更贵
        var result = Resolve("WHATEVER", primary: ["A-01"], lastPlaced: "L-03", emptyInArea: ["R-01"]);

        Assert.Equal(Sentinel, result.LocationNo);
        Assert.True(result.IsSentinel);
    }

    [Fact]
    public void 哨兵可被调用方改写_且回落时用调用方的哨兵()
    {
        Assert.Equal("N/A", Resolve(DepotLocationResolver.RandomMode, sentinel: "N/A").LocationNo);
    }
}
