using System.Data;
using EOS.API.Data.Inventory;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 可用量服务真库验收（D7 / WS-20）。
///
/// 验的是"唯一口径出口"这件事本身：可用量 = 库存数量 − 冻结 − 预留，**且哪些占用算数由读取时判定**
/// （D7-⑤ 惰性判定），判不出来的一律**保守计入**并报出来（方向性理由见服务头注）。
///
/// 六条：① 无占用 ⇒ 可用量 = 数量；② 有冻结 + 预留（来源仍有效）⇒ 逐键相减；
/// ③ 已取消/已释放（STATUS='C'）不计入；④ **来源单据已结案 ⇒ 不计入**（惰性判定的正题：
/// "释放钩子没跑到"不该变成假性缺料）；⑤ 来源判不出来 ⇒ **仍然计入**（保守方向）且报出来；
/// ⑥ 料号+库别 汇总 = 行格之和（同一口径的两个粒度）。
/// 判别性：把来源结案那条判据摘掉（等价于只认 STATUS）⇒ 第④条变红。
///
/// 夹具 `ZZAVL` 前缀自造（余额行 + 冻结行 + 预留行 + 一张真实盘点单当来源），结束即删。
/// </summary>
[Collection("live-database")]
public sealed class InventoryAvailabilityServiceLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string Product = "ZZAVLPRO1";
    private const string Depot = "CP";
    private const string Batch = "ZZAVL-B1";
    private const string SourceType = "130101";           // 库存盘点单（模块号）
    private const string SourceNo = "ZZAVL-CS1";
    private const double StockQty = 100d;
    private const double FreezeQty = 30d;
    private const double ReserveQty = 20d;

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
        await ExecAsync(connection, """
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE) VALUES (@pro, N'ZZAVL 可用量料件', N'规格', '3');
            INSERT INTO dbo.INV_CHECK_STOCK_M (CHECK_STOCK_TYPE, CHECK_STOCK_NO, CONFIRM_TAG, FINISHED_TAG)
                VALUES (N'ZZAVL', N'ZZAVL-CS1', 1, 0);
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@pro, @depot, N'-', @batch, @qty, @qty, 3, @qty * 3);
            """, ("@pro", Product), ("@depot", Depot), ("@batch", Batch), ("@qty", StockQty));
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
    }

    private static async Task CleanupAsync(SqlConnection connection)
    {
        await ExecAsync(connection, "DELETE FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product));
        await ExecAsync(connection, "DELETE FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product));
        await ExecAsync(connection, "DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product));
        await ExecAsync(connection, "DELETE FROM dbo.INV_CHECK_STOCK_M WHERE CHECK_STOCK_TYPE = N'ZZAVL';");
        await ExecAsync(connection, "DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product));
    }

    private static readonly InventoryAvailabilityService.SlotKey Slot = new(Product, Depot, "-", Batch);
    private static readonly InventoryAvailabilityService.SlotKey PlainSlot = new(Product, Depot, "-", "");

    // ===== ①② 无占用 ⇒ = 数量；有占用 ⇒ 相减 =====

    [Fact]
    public async Task 无占用时可用量等于数量()
    {
        var result = await ReadAsync(Slot);
        Assert.Equal(StockQty, result.Quantity);
        Assert.Equal(0, result.Frozen);
        Assert.Equal(0, result.Reserved);
        Assert.Equal(StockQty, result.Available);
    }

    [Fact]
    public async Task 有冻结与预留时逐键相减()
    {
        await SeedFreezeAsync("A", SourceType, SourceNo, FreezeQty);
        await SeedReserveAsync("A", SourceType, SourceNo, ReserveQty);

        var result = await ReadAsync(Slot);
        Assert.Equal(StockQty, result.Quantity);
        Assert.Equal(FreezeQty, result.Frozen);
        Assert.Equal(ReserveQty, result.Reserved);
        Assert.Equal(StockQty - FreezeQty - ReserveQty, result.Available);
    }

    // ===== ③ 已取消的不计入 =====

    [Fact]
    public async Task 已取消的预留不计入()
    {
        await SeedReserveAsync("C", SourceType, SourceNo, ReserveQty);

        var result = await ReadAsync(Slot);
        Assert.Equal(0, result.Reserved);
        Assert.Equal(StockQty, result.Available);
        Assert.Equal(ReserveQty, result.DroppedReserved);
        Assert.Contains(result.Notes, note => note.Contains("已取消"));
    }

    // ===== ④ 惰性判定：来源单据已结案 ⇒ 不计入 =====

    [Fact]
    public async Task 来源单据已结案时不计入()
    {
        await SeedFreezeAsync("A", SourceType, SourceNo, FreezeQty);
        await SeedReserveAsync("A", SourceType, SourceNo, ReserveQty);
        // 来源单据结案（释放钩子没跑到）——读取时就不该再占着
        await ExecOnceAsync(
            "UPDATE dbo.INV_CHECK_STOCK_M SET FINISHED_TAG = 1 WHERE CHECK_STOCK_TYPE = N'ZZAVL' AND CHECK_STOCK_NO = N'ZZAVL-CS1';");

        var result = await ReadAsync(Slot);
        Assert.Equal(0, result.Frozen);
        Assert.Equal(0, result.Reserved);
        Assert.Equal(StockQty, result.Available);
        Assert.Equal(FreezeQty, result.DroppedFrozen);
        Assert.Equal(ReserveQty, result.DroppedReserved);
    }

    // ===== ⑤ 判不出来 ⇒ 保守计入（危险方向不放宽） =====

    [Fact]
    public async Task 来源判不出来时保守计入并报出来()
    {
        await SeedReserveAsync("A", string.Empty, string.Empty, ReserveQty);

        var result = await ReadAsync(Slot);
        Assert.Equal(ReserveQty, result.Reserved);          // 仍然占着
        Assert.Equal(StockQty - ReserveQty, result.Available);
        Assert.Contains(result.Notes, note => note.Contains("判不出来") || note.Contains("来源"));
    }

    // ===== ⑥ 两个粒度同一口径 =====

    [Fact]
    public async Task 料号库别汇总等于行格之和()
    {
        await SeedFreezeAsync("A", SourceType, SourceNo, FreezeQty);
        await SeedReserveAsync("A", SourceType, SourceNo, ReserveQty);

        await using var connection = await OpenAsync();
        var pairs = await InventoryAvailabilityService.ForPairsAsync(
            connection, null, [(Product, Depot)], CancellationToken.None);

        var pair = pairs[(Product, Depot)];
        Assert.Equal(StockQty, pair.Quantity);
        Assert.Equal(FreezeQty, pair.Frozen);
        Assert.Equal(ReserveQty, pair.Reserved);
        Assert.Equal(StockQty - FreezeQty - ReserveQty, pair.Available);
    }

    // ===== 装配 =====

    private static async Task<InventoryAvailabilityService.Availability> ReadAsync(
        InventoryAvailabilityService.SlotKey slot)
    {
        await using var connection = await OpenAsync();
        var slots = await InventoryAvailabilityService.ForSlotsAsync(
            connection, null, [slot], CancellationToken.None);
        return slots[slot.Trimmed()];
    }

    private static async Task SeedFreezeAsync(string status, string sourceType, string sourceNo, double qty) =>
        await ExecOnceAsync("""
            INSERT INTO dbo.INV_FREEZE (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SOURCE_TYPE, SOURCE_NO, FREEZE_QTY, STATUS, REASON)
                VALUES (@pro, @depot, N'-', @batch, @type, @no, @qty, @status, N'ZZAVL 用例');
            """, ("@pro", Product), ("@depot", Depot), ("@batch", Batch), ("@type", sourceType),
            ("@no", sourceNo), ("@qty", qty), ("@status", status));

    private static async Task SeedReserveAsync(string status, string sourceType, string sourceNo, double qty) =>
        await ExecOnceAsync("""
            INSERT INTO dbo.INV_RESERVE (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SOURCE_TYPE, SOURCE_NO, RESERVE_QTY, STATUS, REASON)
                VALUES (@pro, @depot, N'-', @batch, @type, @no, @qty, @status, N'ZZAVL 用例');
            """, ("@pro", Product), ("@depot", Depot), ("@batch", Batch), ("@type", sourceType),
            ("@no", sourceNo), ("@qty", qty), ("@status", status));

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>一次性连接的一次性执行：夹具的每一次写自己开、自己关。</summary>
    private static async Task ExecOnceAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection, sql, parameters);
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
