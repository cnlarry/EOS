using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 位置档位降档的前置归并（§3.10.2）：档位一降，系统就只看哨兵行；还留在位置行上的数量
/// 会**看着凭空减少**。因此"拒绝直接降档、必须先归并"，且归并与写档位在同一事务里。
///
/// 这里必须用**已提交**的存量数据：`SaveAsync` 只有"自建连接"一种签名，看不见测试事务里
/// 尚未提交的余额行。按仓库既有做法——提交后断言、收尾显式清理。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class DepotStockPolicyDowngradeLiveTests
{
    private const string Depot = "ADR27DP";
    private const string ProA = "ADR27PA";
    private const string ProB = "ADR27PB";
    private const string BinA = "A-BIN";
    private const string BinB = "B-BIN";

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private sealed record BalanceRow(string ProNo, string Location, string Batch, double Qty,
        double InitQty, double CostPrice, double CostAmount);

    // ---------- ① 未确认：拒存且数据一行不动 ----------

    [Fact]
    public async Task 位置降档未确认时被拒且不写入任何改动()
    {
        var observed = await SaveDowngradeAsync(confirm: false,
            balances: [new(ProA, "-", "", 10, 10, 5, 75), new(ProA, BinA, "", 5, 10, 5, 75),
                       new(ProA, BinB, "", 7, 10, 5, 75)]);
        var result = observed.Result;

        Assert.False(result.Saved);
        Assert.True(result.RequiresConfirmation);
        Assert.Contains(result.Errors, m => m.Contains("归并"));
        Assert.Contains(result.Errors, m => m.Contains("凭空减少"));

        // 拒存 = 什么都没发生
        Assert.Equal(10d, observed.At("-").Qty, 3);
        Assert.Equal(5d, observed.At(BinA).Qty, 3);
        Assert.Equal(7d, observed.At(BinB).Qty, 3);
        Assert.Equal(3, observed.LocationMode);
    }

    // ---------- ② 已确认：先归并、再降档，总量守恒 ----------

    [Fact]
    public async Task 位置降档确认后先把存量并入哨兵行且总量不变()
    {
        var observed = await SaveDowngradeAsync(confirm: true,
            balances: [new(ProA, "-", "", 10, 10, 5, 75), new(ProA, BinA, "", 5, 10, 5, 75),
                       new(ProA, BinB, "", 7, 10, 5, 75)]);
        var result = observed.Result;

        Assert.True(result.Saved, string.Join("；", result.Errors));
        Assert.Contains(result.Warnings, m => m.Contains("已完成归并"));

        Assert.Equal(22d, observed.At("-").Qty, 3);       // 10 + 5 + 7
        Assert.Equal(0d, observed.At(BinA).Qty, 3);       // 行保留、数量清零
        Assert.Equal(0d, observed.At(BinB).Qty, 3);
        Assert.Equal(22d, observed.Rows.Sum(r => r.Qty), 3);   // 库别合计守恒
        Assert.Equal(0, observed.LocationMode);                // 降档完成

        // 库别级三字段一律不动：同键内仍然一致
        Assert.All(observed.Rows, r =>
        {
            Assert.Equal(10d, r.InitQty, 3);
            Assert.Equal(5d, r.CostPrice, 3);
            Assert.Equal(75d, r.CostAmount, 3);
        });
    }

    // ---------- ③ 没有位置存量：降档没有破坏性，直接放行 ----------

    [Fact]
    public async Task 没有位置存量时降档不需要确认()
    {
        var observed = await SaveDowngradeAsync(confirm: false,
            balances: [new(ProA, "-", "", 10, 10, 5, 75)]);

        Assert.True(observed.Result.Saved, string.Join("；", observed.Result.Errors));
        Assert.False(observed.Result.RequiresConfirmation);
        Assert.Equal(0, observed.LocationMode);
    }

    // ---------- ④ 只丢"位置"这一维，批次粒度保留 ----------

    [Fact]
    public async Task 归并只合并位置维度并保留批次粒度()
    {
        var observed = await SaveDowngradeAsync(confirm: true,
            balances:
            [
                new(ProA, "-", "L1", 1, 10, 5, 75), new(ProA, BinA, "L1", 2, 10, 5, 75),
                new(ProA, "-", "L2", 3, 10, 5, 75), new(ProA, BinB, "L2", 4, 10, 5, 75),
            ]);

        Assert.True(observed.Result.Saved, string.Join("；", observed.Result.Errors));

        Assert.Equal(3d, observed.At("-", "L1").Qty, 3);   // 1 + 2
        Assert.Equal(7d, observed.At("-", "L2").Qty, 3);   // 3 + 4
        Assert.Equal(0d, observed.At(BinA, "L1").Qty, 3);
        Assert.Equal(0d, observed.At(BinB, "L2").Qty, 3);
        Assert.Equal(10d, observed.Rows.Sum(r => r.Qty), 3);
    }

    // ---------- ⑤ 缺哨兵行的料号：补出来再并，数量不能丢 ----------

    [Fact]
    public async Task 只有位置行而没有哨兵行的料号在归并时先补哨兵行()
    {
        var observed = await SaveDowngradeAsync(confirm: true,
            balances: [new(ProA, "-", "", 4, 10, 5, 75), new(ProB, BinA, "", 9, 10, 5, 75)]);

        Assert.True(observed.Result.Saved, string.Join("；", observed.Result.Errors));

        // ProB 原本只有位置行；不先补哨兵行的话，清零就等于把 9 直接抹掉
        Assert.Equal(9d, observed.Of(ProB, "-").Qty, 3);
        Assert.Equal(0d, observed.Of(ProB, BinA).Qty, 3);
        Assert.Equal(13d, observed.Rows.Sum(r => r.Qty), 3);   // 4 + 9
    }

    // ---------- 夹具 ----------

    private sealed record Observed(
        DepotStockPolicyService.SavePolicyResult Result,
        IReadOnlyList<BalanceRow> Rows,
        int LocationMode)
    {
        public BalanceRow At(string location, string batch = "") =>
            Rows.Single(r => r.Location == location && r.Batch == batch);
        public BalanceRow Of(string proNo, string location) => Rows.Single(r => r.ProNo == proNo && r.Location == location);
    }

    /// <summary>先建数 → 保存 → **在同一轮里读回结果** → 再清理。清理放到断言之后，否则读到的是空表。</summary>
    private static async Task<Observed> SaveDowngradeAsync(
        bool confirm, IReadOnlyList<BalanceRow> balances)
    {
        var connectionString = RequireConnection();
        await SeedCommittedAsync(connectionString, balances);
        try
        {
            var result = await PolicyServiceFactory.Create(connectionString).SaveAsync(
                new DepotStockPolicy(Depot, 0, "FIXED", 0, 0, true, true, true, false),
                "adr27-test", confirmDestructive: confirm, CancellationToken.None);
            return new Observed(result, await ReadBalancesAsync(), await ReadLocationModeAsync());
        }
        finally
        {
            await CleanupCommittedAsync(connectionString);
        }
    }

    private static async Task SeedCommittedAsync(string connectionString, IReadOnlyList<BalanceRow> balances)
    {
        await CleanupCommittedAsync(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@d, N'ADR27 降档测试仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, SEQ_NO, STATUS)
                VALUES (@d, N'-', NULL, N'/-', N'BIN', 0, N'A'),
                       (@d, @binA, NULL, N'/A-BIN', N'BIN', 1, N'A'),
                       (@d, @binB, NULL, N'/B-BIN', N'BIN', 2, N'A');
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH, UNIT_ID)
                VALUES (@pa, N'ADR27 料件A', 0, N'ADR27UN'), (@pb, N'ADR27 料件B', 0, N'ADR27UN');
            -- 库别策略：位置档位 3（强制），降档到 0 才是有破坏性的那一步
            INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH)
                VALUES (@d, 3, N'FIXED', 0, 0, 1, 1);
            """, ("@d", Depot), ("@binA", BinA), ("@binB", BinB), ("@pa", ProA), ("@pb", ProB));

        foreach (var row in balances)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, COST_PRICE, COST_AMOUNT) "
                + "VALUES (@pro, @d, @loc, @batch, @qty, @init, @price, @amount)",
                ("@pro", row.ProNo), ("@d", Depot), ("@loc", row.Location), ("@batch", row.Batch),
                ("@qty", row.Qty), ("@init", row.InitQty), ("@price", row.CostPrice), ("@amount", row.CostAmount));

        await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task CleanupCommittedAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@d;
            DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@d;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@d;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@d;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@pa, @pb);
            """, connection);
        command.Parameters.AddWithValue("@d", Depot);
        command.Parameters.AddWithValue("@pa", ProA);
        command.Parameters.AddWithValue("@pb", ProB);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<BalanceRow>> ReadBalancesAsync()
    {
        await using var connection = new SqlConnection(RequireConnection());
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(PRO_NO)), LOCATION_NO, ISNULL(LTRIM(RTRIM(BATCH_NO)), N''), ISNULL(QTY,0), "
            + "ISNULL(INIT_QTY,0), ISNULL(COST_PRICE,0), ISNULL(COST_AMOUNT,0) "
            + "FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@d ORDER BY PRO_NO, LOCATION_NO, BATCH_NO", connection);
        command.Parameters.AddWithValue("@d", Depot);
        var rows = new List<BalanceRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new BalanceRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6)));
        return rows;
    }

    private static async Task<int> ReadLocationModeAsync()
    {
        await using var connection = new SqlConnection(RequireConnection());
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT LOCATION_MODE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@d", connection);
        command.Parameters.AddWithValue("@d", Depot);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

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
