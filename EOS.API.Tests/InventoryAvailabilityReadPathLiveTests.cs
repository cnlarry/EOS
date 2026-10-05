using System.Data;
using EOS.API.Data.Inventory;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 可用量的**读取收口**验收（D7-⑧ / WS-24）。
///
/// 钉的是"三处同源、单一服务"这句话：
///   · 口径出口 —— <see cref="InventoryAvailabilityService.ForSlotsAsync"/>（实时算）
///   · 落列     —— `INV_PRO_DEPOT.USEABLE_QTY`（它的物化快照）
///   · 读取侧   —— <see cref="InventoryQueryService"/>（行级 + 聚合两条读法都下发这一列）
/// 三处对该格必须给出**同一个数**；读取侧**不许自己聚合** `INV_FREEZE` / `INV_RESERVE`——
/// 那会长出第二个口径，而两个口径一旦分叉，没人能说清哪个是"能出多少"。
///
/// 第②条用例故意造"忘了同步"的现场：列停在旧值、实时口径已经变了。
/// 读取侧**仍下发列值**（它只是下发者，不是第二个口径），而**分叉本身由
/// `scripts/check-inventory-availability.ps1` 抓**——所以那条门禁不是可选项，
/// 它是"存列"这个设计能成立的前提（D7-②）。
///
/// 判别性：把读取侧的投影从 `USEABLE_QTY` 换成 `QTY` ⇒ 第①条变红。
/// 夹具 `ZZAVRD` 前缀自造，结束即删。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class InventoryAvailabilityReadPathLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string Product = "ZZAVRD1";
    private const string Depot = "CP";
    private const string Location = "-";
    private const string Batch = "";
    private const double StockQty = 100d;
    private const double FreezeQty = 10d;
    private const double ReserveQty = 20d;
    private const double Available = StockQty - FreezeQty - ReserveQty;

    private static InventoryAvailabilityService.SlotKey Slot => new(Product, Depot, Location, Batch);

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync();
        await CleanupAsync(connection);
        await ExecAsync(connection, """
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_TYPE) VALUES (@pro, N'ZZAVRD 读取收口件', '3');
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, USEABLE_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@pro, @depot, @location, @batch, @qty, @qty, @qty, 3, @qty * 3);
            INSERT INTO dbo.INV_FREEZE (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SOURCE_TYPE, SOURCE_NO, FREEZE_QTY, STATUS, REASON)
                VALUES (@pro, @depot, @location, @batch, N'', N'', @freeze, N'A', N'ZZAVRD 用例');
            INSERT INTO dbo.INV_RESERVE (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SOURCE_TYPE, SOURCE_NO, RESERVE_QTY, STATUS)
                VALUES (@pro, @depot, @location, @batch, N'', N'', @reserve, N'A');
            """, ("@pro", Product), ("@depot", Depot), ("@location", Location), ("@batch", Batch),
            ("@qty", StockQty), ("@freeze", FreezeQty), ("@reserve", ReserveQty));

        // 落列只在唯一出口做（这里模拟写路径的动作：有占用就同步一次）
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await InventoryAvailabilityService.SyncSlotsAsync(connection, transaction, [Slot], CancellationToken.None);
        await transaction.CommitAsync();
    }

    public async Task DisposeAsync()
    {
        await using var connection = await OpenAsync();
        await CleanupAsync(connection);
    }

    // ===== ① 三处同源 =====

    [Fact]
    public async Task 三处取值逐键一致()
    {
        await using var connection = await OpenAsync();

        // 口径出口（实时算）
        var calculated = await InventoryAvailabilityService.ForSlotsAsync(
            connection, null, [Slot], CancellationToken.None);
        Assert.Equal(Available, calculated[Slot].Available);
        Assert.Equal(FreezeQty, calculated[Slot].Frozen);
        Assert.Equal(ReserveQty, calculated[Slot].Reserved);

        // 落列（物化快照）
        Assert.Equal(Available, await ScalarAsync<double>(connection,
            "SELECT ISNULL(USEABLE_QTY,0) FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO))=@pro;",
            ("@pro", Product)));

        // 读取侧：行级
        var rows = await InventoryQueryService.GetRowsAsync(
            connection, null, new InventoryQueryService.RowScope { ProductNo = Product, DepotId = Depot },
            InventoryQueryService.ReadLock.None, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(StockQty, row.Quantity);
        Assert.Equal(Available, row.UseableQuantity);

        // 读取侧：聚合（报表路径）
        var quantities = await InventoryQueryService.GetQuantitiesAsync(
            connection, null,
            new InventoryQueryService.QuantityQuery { ProductNo = Product, DepotId = Depot, GroupByDepot = true },
            InventoryQueryService.ReadLock.None, CancellationToken.None);
        var aggregate = Assert.Single(quantities);
        Assert.Equal(StockQty, aggregate.Quantity);
        Assert.Equal(Available, aggregate.UseableQuantity);
    }

    // ===== ② 忘了同步 ⇒ 分叉，读取侧仍是列值，而门禁抓的是"分叉" =====

    [Fact]
    public async Task 未同步即分叉_读取侧仍下发列值且门禁能抓()
    {
        // 把这一格的冻结从 10 加到 25，但**不同步**（模拟写入路径漏了 SyncSlotsAsync 这一步）。
        // 这里改量而不是插新行：`INV_FREEZE` 的键里带来源单，同一格同来源只有一行；
        // "冻结量变了却没同步"才是漏同步的真实形态。
        await ExecAsync(await OpenAsync(),
            "UPDATE dbo.INV_FREEZE SET FREEZE_QTY = 25 WHERE LTRIM(RTRIM(PRO_NO)) = @pro;",
            ("@pro", Product));

        await using var connection = await OpenAsync();

        // 实时口径已经变了（10 + 20 + 15 = 45）
        var calculated = await InventoryAvailabilityService.ForSlotsAsync(
            connection, null, [Slot], CancellationToken.None);
        Assert.Equal(StockQty - 45, calculated[Slot].Available);

        // 读取侧**不下发实时值**，它只下发那一列 —— 列停在 70
        var rows = await InventoryQueryService.GetRowsAsync(
            connection, null, new InventoryQueryService.RowScope { ProductNo = Product, DepotId = Depot },
            InventoryQueryService.ReadLock.None, CancellationToken.None);
        Assert.Equal(Available, Assert.Single(rows).UseableQuantity);

        // 分叉 = 列 − （数量 − Σ有效占用）。这就是 `check-inventory-availability.ps1` 的口径，
        // 也正是"存列"必须配一条门禁的原因（漏同步不是读侧能兜的，得有人常态断言）。
        Assert.Equal(15d, await ScalarAsync<double>(connection, """
            SET NOCOUNT ON;
            WITH occ AS (
                SELECT LTRIM(RTRIM(PRO_NO)) P, LTRIM(RTRIM(DEPOT_ID)) D, LTRIM(RTRIM(ISNULL(LOCATION_NO,N'-'))) L,
                       LTRIM(RTRIM(ISNULL(BATCH_NO,N''))) B, ISNULL(FREEZE_QTY,0) Q
                  FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(ISNULL(STATUS,N'C'))) = N'A'
                UNION ALL
                SELECT LTRIM(RTRIM(PRO_NO)), LTRIM(RTRIM(DEPOT_ID)), LTRIM(RTRIM(ISNULL(LOCATION_NO,N'-'))),
                       LTRIM(RTRIM(ISNULL(BATCH_NO,N''))), ISNULL(RESERVE_QTY,0)
                  FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(ISNULL(STATUS,N'C'))) = N'A'
            ), agg AS (SELECT P, D, L, B, SUM(Q) OCC FROM occ GROUP BY P, D, L, B)
            SELECT ISNULL(d.USEABLE_QTY,0) - (ISNULL(d.QTY,0) - ISNULL(a.OCC,0))
              FROM dbo.INV_PRO_DEPOT d LEFT JOIN agg a
                ON a.P = LTRIM(RTRIM(d.PRO_NO)) AND a.D = LTRIM(RTRIM(d.DEPOT_ID))
               AND a.L = LTRIM(RTRIM(ISNULL(d.LOCATION_NO,N'-'))) AND a.B = LTRIM(RTRIM(ISNULL(d.BATCH_NO,N'')))
             WHERE LTRIM(RTRIM(d.PRO_NO)) = @pro;
            """, ("@pro", Product)));
    }

    // ===== 装配 =====

    private static async Task CleanupAsync(SqlConnection connection) =>
        await ExecAsync(connection, """
            DELETE FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            """, ("@pro", Product));

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        var scalar = await command.ExecuteScalarAsync();
        Assert.NotNull(scalar);
        return (T)Convert.ChangeType(scalar!, typeof(T));
    }

    private static async Task ExecAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }
}
