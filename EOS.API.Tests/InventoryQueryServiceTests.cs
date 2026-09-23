using System;
using EOS.API.Data.Inventory;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存读取服务的口径单测（不需要真库，只钉住"与
/// 依赖数据库〕）：聚合维度、库别级字段的 MAX、库位哨兵与空批次的归一化、以及锁提示的开关。
/// 这些是本服务存在的理由——它们一旦漂移，.Forms 侧的四键正确性就无从谈起。
/// </summary>
public sealed class InventoryQueryServiceTests
{
    [Fact]
    public void 未指定的维度被SUM掉_指定的维度进GROUP_BY()
    {
        var (sql, _) = InventoryQueryService.BuildQuantities(
            new InventoryQueryService.QuantityQuery { GroupByDepot = true, GroupByBatch = true },
            InventoryQueryService.ReadLock.None);
        Assert.Contains("SELECT PRO_NO, DEPOT_ID, BATCH_NO, SUM(QTY) AS QTY", sql);
        Assert.Contains("GROUP BY PRO_NO, DEPOT_ID, BATCH_NO", sql);
        Assert.Contains("dbo.INV_PRO_DEPOT", sql);
    }

    [Fact]
    public void MRP库别与表级排他锁_按调用方声明出现()
    {
        var (locked, _) = InventoryQueryService.BuildQuantities(
            new InventoryQueryService.QuantityQuery { MrpDepotsOnly = true },
            InventoryQueryService.ReadLock.ExclusiveTable);
        Assert.Contains("WHERE DEPOT_ID IN (SELECT DEPOT_ID FROM dbo.DEPOT WHERE MRP=1)", locked);
        Assert.Contains("WITH(TABLOCKX)", locked);

        var (plain, _) = InventoryQueryService.BuildQuantities(
            new InventoryQueryService.QuantityQuery(), InventoryQueryService.ReadLock.None);
        Assert.DoesNotContain("TABLOCK", plain);
        Assert.DoesNotContain("UPDLOCK", plain);
    }

    [Fact]
    public void 库位与批次的比较走归一化键_且值一律参数化()
    {
        var (sql, parameters) = InventoryQueryService.BuildQuantities(
            new InventoryQueryService.QuantityQuery { LocationNo = "A-01", BatchNo = "B1", DepotId = "D1" },
            InventoryQueryService.ReadLock.None);
        Assert.Contains("ISNULL(LTRIM(RTRIM(s.LOCATION_NO)), N'-')=@loc", sql);
        Assert.Contains("ISNULL(LTRIM(RTRIM(s.BATCH_NO)), N'')=@batch", sql);
        Assert.Equal(["@depot", "@loc", "@batch"], parameters.Select(item => item.Name).ToArray());
        Assert.DoesNotContain("A-01", sql);
        Assert.DoesNotContain("B1", sql);
    }

    [Fact]
    public void 库别级字段只能MAX_行级字段不能按此口径读()
    {
        Assert.Equal("MAX(ISNULL(s.COST_PRICE, 0))", InventoryQueryService.DepotLevelValue("s", "COST_PRICE"));
        Assert.Equal("MAX(ISNULL(s.INIT_QTY, 0))", InventoryQueryService.DepotLevelValue("s", "INIT_QTY"));
        Assert.Equal("MAX(ISNULL(s.LAST_CHECK_DATE, 0))", InventoryQueryService.DepotLevelValue("s", "LAST_CHECK_DATE"));
        Assert.Throws<InvalidOperationException>(() => InventoryQueryService.DepotLevelValue("s", "QTY"));
    }

    [Fact]
    public void 数量过滤用HAVING而不是把NULL兜成零()
    {
        var (nonZero, _) = InventoryQueryService.BuildQuantities(
            new InventoryQueryService.QuantityQuery { RequireNonZero = true },
            InventoryQueryService.ReadLock.None);
        Assert.Contains("HAVING SUM(QTY) <> 0", nonZero);
        Assert.DoesNotContain("ISNULL(SUM", nonZero);

        var (all, _) = InventoryQueryService.BuildQuantities(
            new InventoryQueryService.QuantityQuery(), InventoryQueryService.ReadLock.None);
        Assert.DoesNotContain("HAVING", all);
    }
}
