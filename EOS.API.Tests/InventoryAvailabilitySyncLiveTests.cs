using System.Data;
using EOS.API.Data.Inventory;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// **可用量列的唯一维护出口**真库验收（`InventoryAvailabilityService.SyncSlotsAsync`，ADR-020 §9.7 D7-②，
/// WS-23 的前置）。
///
/// 这一列是**存列**：任何"数量动过或占用动过"的写入路径都得按口径重算它。本段把"重算 + 落列"
/// 收成一个出口（移动引擎、冻结/解冻、预留/释放、来源结案钩子都调它），所以这里验的就是这个出口：
///   ① 冻结 + 预留混合 ⇒ 列 = 数量 − 冻结 − 预留；
///   ② **来源已结案的预留不计入**（沿用 WS-20 的惰性判据 ⇒ 出口不是另一份减法）；
///   ③ **只碰请求到的格子**（越界去改别人只会制造并发面）；
///   ④ 空清单是空操作。
/// 判别性：把落列值改成 `QTY`（假装按数量写）⇒ 第①②条变红。
///
/// **未验（如实登记，属 WS-23 的第一件事）**：这条出口在**移动引擎路径**上的端到端验证——
/// 夹具单据与引擎归一化后的四键对齐尚未调通（实测引擎对该夹具报"库存数量不足"），
/// 而 WS-23 要改的正是那条校验的判据，届时一并把它做成 WS-23 的用例。
/// </summary>
[Collection("live-database")]
public sealed class InventoryAvailabilitySyncLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const string Product = "ZZSYNPRO1";
    private const string Depot = "CP";
    private const string Batch = "ZZSYN-B1";
    private const string OtherBatch = "ZZSYN-B2";
    private const string SourceType = "1502";
    private const string SourceNo = "ZZSYN-MO1";
    private const string ProduceType = "ZZSYN";
    private const double StockQty = 100d;
    private const double FreezeQty = 10d;
    private const double ReserveQty = 20d;

    private static readonly InventoryAvailabilityService.SlotKey Slot = new(Product, Depot, "-", Batch);
    private static readonly InventoryAvailabilityService.SlotKey OtherSlot = new(Product, Depot, "-", OtherBatch);

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
        await ExecAsync(connection, """
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE) VALUES (@pro, N'ZZSYN 维护点料件', N'规格', '3');
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, USEABLE_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@pro, @depot, N'-', @batch, @qty, @qty, 0, 3, @qty * 3),
                       (@pro, @depot, N'-', @other, 7, 7, 0, 3, 21);
            INSERT INTO dbo.INV_FREEZE (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SOURCE_TYPE, SOURCE_NO, FREEZE_QTY, STATUS, REASON)
                VALUES (@pro, @depot, N'-', @batch, N'', N'', @freeze, N'A', N'ZZSYN 用例');
            INSERT INTO dbo.INV_RESERVE (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SOURCE_TYPE, SOURCE_NO, RESERVE_QTY, STATUS, REASON)
                VALUES (@pro, @depot, N'-', @batch, @sourceType, @sourceNo, @reserve, N'A', N'ZZSYN 用例');
            INSERT INTO dbo.MOC_PRODUCE_M (PRODUCE_TYPE, PRODUCE_NO, FINISHED_TAG) VALUES (@produceType, @sourceNo, 0);
            """, ("@pro", Product), ("@depot", Depot), ("@batch", Batch), ("@other", OtherBatch), ("@qty", StockQty),
            ("@freeze", FreezeQty), ("@sourceType", SourceType), ("@sourceNo", SourceNo), ("@reserve", ReserveQty),
            ("@produceType", ProduceType));
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
    }

    private static async Task CleanupAsync(SqlConnection connection)
    {
        await ExecAsync(connection, """
            DELETE FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.MOC_PRODUCE_M WHERE PRODUCE_TYPE = @produceType;
            DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            """, ("@pro", Product), ("@produceType", ProduceType));
    }

    // ===== ①② 混合占用与惰性判据 =====

    [Fact]
    public async Task 同步按口径落列_且来源结案的预留不计入()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        // 首次同步：列从 0（未初始化）被订正为 100 − 10 − 20 = 70
        await InventoryAvailabilityService.SyncSlotsAsync(connection, null, [Slot], CancellationToken.None);
        Assert.Equal(StockQty - FreezeQty - ReserveQty, await UseableAsync(connection, Slot));

        // 来源结案 ⇒ 惰性判据把它判掉 ⇒ 再同步 ⇒ 列 = 100 − 10 = 90（出口不是另一份减法）
        await ExecAsync(connection,
            "UPDATE dbo.MOC_PRODUCE_M SET FINISHED_TAG = 1 WHERE PRODUCE_TYPE = @type AND PRODUCE_NO = @no;",
            ("@type", ProduceType), ("@no", SourceNo));
        await InventoryAvailabilityService.SyncSlotsAsync(connection, null, [Slot], CancellationToken.None);
        Assert.Equal(StockQty - FreezeQty, await UseableAsync(connection, Slot));
    }

    // ===== ③ 只碰请求到的格子 =====

    [Fact]
    public async Task 只同步请求到的格子()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await InventoryAvailabilityService.SyncSlotsAsync(connection, null, [Slot], CancellationToken.None);

        Assert.Equal(StockQty - FreezeQty - ReserveQty, await UseableAsync(connection, Slot));
        // 另一格没被请求 ⇒ 保持原样（没被顺手改掉）
        Assert.Equal(0d, await UseableAsync(connection, OtherSlot));
    }

    // ===== ④ 空清单是空操作 =====

    [Fact]
    public async Task 空清单不改任何行()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var written = await InventoryAvailabilityService.SyncSlotsAsync(
            connection, null, [], CancellationToken.None);

        Assert.Equal(0, written);
        Assert.Equal(0d, await UseableAsync(connection, Slot));
    }

    private static async Task<double> UseableAsync(SqlConnection connection, InventoryAvailabilityService.SlotKey slot)
    {
        await using var command = new SqlCommand(
            "SELECT ISNULL(USEABLE_QTY,0) FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro "
            + "AND LTRIM(RTRIM(DEPOT_ID)) = @depot AND LTRIM(RTRIM(LOCATION_NO)) = @location AND LTRIM(RTRIM(BATCH_NO)) = @batch;",
            connection);
        command.Parameters.AddWithValue("@pro", slot.ProductNo);
        command.Parameters.AddWithValue("@depot", slot.DepotId);
        command.Parameters.AddWithValue("@location", slot.LocationNo);
        command.Parameters.AddWithValue("@batch", slot.BatchNo);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToDouble(value);
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
