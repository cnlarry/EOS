using System.Data;
using EOS.API.Data.Inventory;
using EOS.API.Errors;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 关账拦截的真库用例（整段在事务内，结束回滚）。
///
/// 覆盖四条边界（与 的验收一一对应）：
///   ① 业务日期落在**已关账期** ⇒ 拒（`PeriodClosedException`，文案点名期间与月结单号）；
///   ② 这张单**已有**的流水落在已关账期 ⇒ 其解批 / 反向也拒；
///   ③ 反向流水的日期（非批核方向记的是当前时间）落进已关账期 ⇒ 也拒；
///   ④ **草稿月结单不算关账**（只有已批核的期才拦）——与库存日报期初同一条规矩。
///   另断边界值：期末**当天**算在期内，期末**次日**才是开账期。
///
/// 判别性：把 `CONFIRM_TAG = 1` 从查询里去掉 ⇒ ④ 变红（草稿期会被当关账期）；
/// 去掉"已有流水"那段 ⇒ ② 变红；去掉整条守卫 ⇒ ①③ 变红。
/// </summary>
[Collection("live-database")]
public sealed class InventoryPeriodServiceLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string MonthType = "ADR20C";
    private const string ClosedPeriod = "ADR20C001";   // 期末 2023-12-31，已批核
    private const string DraftPeriod = "ADR20C002";    // 期末 2024-02-29，草稿
    private const string ClosedBill = "ADR20CX01";     // 流水落在已关账期内的单据
    private const string OpenBill = "ADR20CY01";       // 流水落在开账期的单据
    private const string NoLedgerBill = "ADR20CZ01";   // 完全没有流水的单据
    private const string TestDepot = "ADR20C01";
    private const string TestProduct = "ADR20CPRO1";

    [Fact]
    public async Task 边界一_业务日期落在已关账期_拒绝并点名期间()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction);

            var error = await Assert.ThrowsAsync<PeriodClosedException>(() =>
                InventoryPeriodService.EnsureLedgerWritableAsync(
                    connection, transaction, new DateTime(2023, 12, 20), MonthType, NoLedgerBill, CancellationToken.None));

            Assert.Equal(ClosedPeriod, error.MonthNo);
            Assert.Equal(new DateTime(2023, 12, 31), error.MonthDate);
            Assert.Contains("已关账", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Theory]
    [InlineData("2023-12-31", true)]    // 期末当天算在期内
    [InlineData("2024-01-01", false)]   // 期末次日是开账期
    public async Task 边界二_期末当天与次日(string ledgerDate, bool closed)
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction);

            var act = () => InventoryPeriodService.EnsureLedgerWritableAsync(
                connection, transaction, DateTime.Parse(ledgerDate), MonthType, NoLedgerBill, CancellationToken.None);

            if (closed)
                await Assert.ThrowsAsync<PeriodClosedException>(act);
            else
                await act();
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task 边界三_草稿月结单不算关账()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction);

            // 2024-02-10 落在**草稿期**（期末 2024-02-29）之内，但草稿不是关账 ⇒ 放行。
            // 若查询漏了 CONFIRM_TAG=1，这里会取到草稿期而拒绝，本用例即红。
            await InventoryPeriodService.EnsureLedgerWritableAsync(
                connection, transaction, new DateTime(2024, 2, 10), MonthType, NoLedgerBill, CancellationToken.None);

            // 同时证明草稿期确实存在、且期末更晚（否则上面那句只是"没命中任何期"）：
            // 夹具里有这张草稿单，只是判据不看它。
            await using (var command = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.INV_PRO_MONTH_M WHERE MONTH_TYPE = @type AND MONTH_NO = @no "
                + "AND CONFIRM_TAG = 0 AND MONTH_DATE = '2024-02-29';", connection, transaction))
            {
                command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = MonthType;
                command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = DraftPeriod;
                Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
            }

            // 反证：把它当成关账期（即在判据里不过滤批核位）就会命中 ⇒ 上面那句必须失败
            Assert.Null(await InventoryPeriodService.FindAsync(
                connection, transaction, new DateTime(2024, 2, 29), CancellationToken.None));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task 边界四_已有流水落在已关账期_反向记账同样拒绝()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction);

            // 记账日期本身在开账期（2024-06-01），但这张单**当初**的流水在已关账期
            // （2023-12-15）——解批 / 反向冲销会改动已关账期间的结存，故拒。
            var error = await Assert.ThrowsAsync<PeriodClosedException>(() =>
                InventoryPeriodService.EnsureLedgerWritableAsync(
                    connection, transaction, new DateTime(2024, 6, 1), MonthType, ClosedBill, CancellationToken.None));

            Assert.Contains("已有的库存流水", error.Message, StringComparison.Ordinal);
            Assert.Equal(ClosedPeriod, error.MonthNo);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task 边界五_开账期单据的正常记账放行()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction);

            // 记账日期与这张单已有流水都在开账期 ⇒ 放行
            await InventoryPeriodService.EnsureLedgerWritableAsync(
                connection, transaction, new DateTime(2024, 6, 1), MonthType, OpenBill, CancellationToken.None);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task 反向流水的日期落进已关账期_也拒绝()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction);
            // 再关一期：期末 = 今天 ⇒ 非批核方向写的"当前时间"落在这期之内
            //（这正是"单据在开账期、反向流水却落进已关账期"的形态）
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dbo.INV_PRO_MONTH_M (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CREATE_PERSON, CREATE_DATE, CI)
                    VALUES (@Type, N'ADR20C003', @Today, 1, N'ADR20C', GETDATE(), 'ADR20C');
                """, ("@Type", MonthType), ("@Today", DateTime.Today));

            var error = await Assert.ThrowsAsync<PeriodClosedException>(() =>
                InventoryPeriodService.EnsureLedgerWritableAsync(
                    connection, transaction, DateTime.Now, MonthType, OpenBill, CancellationToken.None));

            Assert.Equal("ADR20C003", error.MonthNo);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction)
        => await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_PRO_MONTH_M WHERE MONTH_TYPE = @Type;
            DELETE FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE = @Type;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID = @Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO = @Pro;
            IF NOT EXISTS(SELECT 1 FROM dbo.DEPOT WHERE DEPOT_ID = @Depot)
                INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR20C 关账测试仓');
            IF NOT EXISTS(SELECT 1 FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID = @Depot AND LOCATION_NO = N'-')
                INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, STORAGE_TYPE, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', NULL, 0, N'A');
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE) VALUES (@Pro, N'ADR20C 关账料件', N'规格C', '3');

            -- 已批核期（期末 2023-12-31）与草稿期（期末 2024-02-29）
            INSERT INTO dbo.INV_PRO_MONTH_M (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CREATE_PERSON, CREATE_DATE, CI)
                VALUES (@Type, @Closed, '2023-12-31', 1, N'ADR20C', GETDATE(), 'ADR20C'),
                       (@Type, @Draft, '2024-02-29', 0, N'ADR20C', GETDATE(), 'ADR20C');

            -- 一张单的流水在已关账期内，另一张在开账期
            INSERT INTO dbo.INV_DEPOT_LOG (PRO_NO, MUTUALITY_DATE, MUTUALITY_TYPE, MUTUALITY_NO, MUTUALITY_SERIAL_NO, IN_OUT, QTY, PRICE, DEPOT_ID, LOCATION_NO)
                VALUES (@Pro, '2023-12-15', @Type, @ClosedBill, 1, 'I', 1, 1, @Depot, N'-'),
                       (@Pro, '2024-03-10', @Type, @OpenBill, 1, 'I', 1, 1, @Depot, N'-');
            """,
            ("@Type", MonthType), ("@Closed", ClosedPeriod), ("@Draft", DraftPeriod),
            ("@ClosedBill", ClosedBill), ("@OpenBill", OpenBill),
            ("@Depot", TestDepot), ("@Pro", TestProduct));

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
