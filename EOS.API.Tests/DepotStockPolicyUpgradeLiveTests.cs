using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 位置档位**升档**时的归位（P2-14 A 案，§3.10.2）：档位一升，系统就按库位出入库，
/// 而还记在『未指定位置』上的货**按库位取不出来**。给了目标库位就由系统代搬；
/// 没给就要求明确表态，不能静默放过。
///
/// 与降档归并严格对称：只丢/只补「位置」这一维，批次粒度保留；库别级三字段不动；`SUM(QTY)` 守恒。
///
/// 必须用**已提交**的存量数据：`SaveAsync` 只有"自建连接"一种签名。按仓库既有做法——提交后断言、收尾清理。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class DepotStockPolicyUpgradeLiveTests
{
    private const string Depot = "ADR28DP";
    private const string ProA = "ADR28PA";
    private const string ProB = "ADR28PB";
    private const string Rcv = "RCV-BIN";   // 收货暂存位

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private sealed record BalanceRow(string ProNo, string Location, string Batch, double Qty);

    private sealed record Observed(
        DepotStockPolicyService.SavePolicyResult Result,
        IReadOnlyList<(string ProNo, string Location, string Batch, double Qty, double CostAmount)> Rows,
        int LocationMode)
    {
        public (string ProNo, string Location, string Batch, double Qty, double CostAmount) At(
            string location, string batch = "") =>
            Rows.Single(r => r.Location == location && r.Batch == batch);
        public (string ProNo, string Location, string Batch, double Qty, double CostAmount) Of(string proNo, string location) =>
            Rows.Single(r => r.ProNo == proNo && r.Location == location);
    }

    // ---------- ① 未指定目标库位：要求明确表态，且数据一行不动 ----------

    [Fact]
    public async Task 升档时哨兵行仍有存量且未指定目标库位会被拒()
    {
        var observed = await SaveUpgradeAsync(relocateTo: null, confirm: false,
            balances: [new(ProA, "-", "", 12), new(ProA, Rcv, "", 3)]);

        Assert.False(observed.Result.Saved);
        Assert.True(observed.Result.RequiresConfirmation);
        Assert.Contains(observed.Result.Errors, m => m.Contains("取不出来"));
        Assert.Contains(observed.Result.Errors, m => m.Contains("relocateTo"));

        Assert.Equal(12d, observed.At("-").Qty, 3);      // 未动
        Assert.Equal(3d, observed.At(Rcv).Qty, 3);
        Assert.Equal(0, observed.LocationMode);          // 档位也没动
    }

    // ---------- ② 指定了目标库位：同一事务内代搬，总量守恒 ----------

    [Fact]
    public async Task 升档时指定目标库位会把哨兵行存量搬过去且总量不变()
    {
        var observed = await SaveUpgradeAsync(relocateTo: Rcv, confirm: false,
            balances: [new(ProA, "-", "", 12), new(ProA, Rcv, "", 3)]);

        Assert.True(observed.Result.Saved, string.Join("；", observed.Result.Errors));
        Assert.Contains(observed.Result.Warnings, m => m.Contains("已完成归位"));

        Assert.Equal(0d, observed.At("-").Qty, 3);       // 哨兵行清零（行保留）
        Assert.Equal(15d, observed.At(Rcv).Qty, 3);      // 12 + 3
        Assert.Equal(15d, observed.Rows.Sum(r => r.Qty), 3);
        Assert.Equal(3, observed.LocationMode);
        Assert.All(observed.Rows, r => Assert.Equal(75d, r.CostAmount, 3));   // 库别级字段不动
    }

    // ---------- ③ 没有哨兵存量：升档没有归位问题，不拦 ----------

    [Fact]
    public async Task 哨兵行没有存量时升档不需要归位也不需要确认()
    {
        var observed = await SaveUpgradeAsync(relocateTo: null, confirm: false,
            balances: [new(ProA, Rcv, "", 8)]);

        Assert.True(observed.Result.Saved, string.Join("；", observed.Result.Errors));
        Assert.False(observed.Result.RequiresConfirmation);
        Assert.Equal(3, observed.LocationMode);
    }

    // ---------- ④ 带确认标志 = 暂不归位，放行但要留告警 ----------

    [Fact]
    public async Task 带确认标志表示暂不归位时放行并给出告警()
    {
        var observed = await SaveUpgradeAsync(relocateTo: null, confirm: true,
            balances: [new(ProA, "-", "", 12)]);

        Assert.True(observed.Result.Saved, string.Join("；", observed.Result.Errors));
        Assert.Contains(observed.Result.Warnings, m => m.Contains("按库位出库取不到"));
        Assert.Equal(12d, observed.At("-").Qty, 3);      // 货还在哨兵行上
        Assert.Equal(3, observed.LocationMode);
    }

    // ---------- ⑤ 目标库位非法：拒存 ----------

    [Fact]
    public async Task 目标库位不存在或就是哨兵行本身时被拒()
    {
        var missing = await SaveUpgradeAsync(relocateTo: "NO-SUCH-BIN", confirm: false,
            balances: [new(ProA, "-", "", 12)]);
        Assert.False(missing.Result.Saved);
        Assert.Contains(missing.Result.Errors, m => m.Contains("不存在或已停用"));
        Assert.Equal(0, missing.LocationMode);

        var asSentinel = await SaveUpgradeAsync(relocateTo: "-", confirm: false,
            balances: [new(ProA, "-", "", 12)]);
        Assert.False(asSentinel.Result.Saved);
        Assert.Contains(asSentinel.Result.Errors, m => m.Contains("不能是『未指定位置』本身"));
    }

    // ---------- ⑥ 只搬"位置"这一维，批次粒度保留；缺目标行的料号先补行 ----------

    [Fact]
    public async Task 归位保留批次粒度并给缺目标行的料号补行()
    {
        var observed = await SaveUpgradeAsync(relocateTo: Rcv, confirm: false,
            balances:
            [
                new(ProA, "-", "L1", 5), new(ProA, Rcv, "L1", 1),
                new(ProA, "-", "L2", 7),
                new(ProB, "-", "", 9),          // ProB 在目标库位上原本没有行
            ]);

        Assert.True(observed.Result.Saved, string.Join("；", observed.Result.Errors));

        Assert.Equal(6d, observed.At(Rcv, "L1").Qty, 3);            // 5 + 1
        Assert.Equal(7d, observed.At(Rcv, "L2").Qty, 3);            // 补行后落 7
        Assert.Equal(9d, observed.Of(ProB, Rcv).Qty, 3);
        Assert.Equal(0d, observed.At("-", "L1").Qty, 3);
        Assert.Equal(0d, observed.At("-", "L2").Qty, 3);
        Assert.Equal(0d, observed.Of(ProB, "-").Qty, 3);
        Assert.Equal(22d, observed.Rows.Sum(r => r.Qty), 3);        // 5+1+7+9
    }

    // ---------- 夹具 ----------

    private static async Task<Observed> SaveUpgradeAsync(
        string? relocateTo, bool confirm, IReadOnlyList<BalanceRow> balances)
    {
        var connectionString = RequireConnection();
        await SeedCommittedAsync(connectionString, balances);
        try
        {
            // 升档：位置档位 0 → 3
            var result = await PolicyServiceFactory.Create(connectionString).SaveAsync(
                new DepotStockPolicy(Depot, 3, "FIXED", 0, 0, true, true, true, false),
                "adr28-test", confirmDestructive: confirm, relocateTo: relocateTo, token: CancellationToken.None);
            return new Observed(result, await ReadRowsAsync(), await ReadLocationModeAsync());
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
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@d, N'ADR28 升档测试仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, SEQ_NO, STATUS)
                VALUES (@d, N'-', NULL, N'/-', N'BIN', 0, N'A'),
                       (@d, @rcv, NULL, N'/RCV-BIN', N'BIN', 1, N'A');
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH, UNIT_ID)
                VALUES (@pa, N'ADR28 料件A', 0, N'ADR28UN'), (@pb, N'ADR28 料件B', 0, N'ADR28UN');
            -- 升档前的策略：位置档位 0
            INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH)
                VALUES (@d, 0, N'FIXED', 0, 0, 1, 1);
            """, ("@d", Depot), ("@rcv", Rcv), ("@pa", ProA), ("@pb", ProB));

        foreach (var row in balances)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, COST_PRICE, COST_AMOUNT) "
                + "VALUES (@pro, @d, @loc, @batch, @qty, 10, 5, 75)",
                ("@pro", row.ProNo), ("@d", Depot), ("@loc", row.Location), ("@batch", row.Batch), ("@qty", row.Qty));

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

    private static async Task<IReadOnlyList<(string, string, string, double, double)>> ReadRowsAsync()
    {
        await using var connection = new SqlConnection(RequireConnection());
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(PRO_NO)), LOCATION_NO, ISNULL(LTRIM(RTRIM(BATCH_NO)), N''), ISNULL(QTY,0), ISNULL(COST_AMOUNT,0) "
            + "FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@d ORDER BY PRO_NO, LOCATION_NO, BATCH_NO", connection);
        command.Parameters.AddWithValue("@d", Depot);
        var rows = new List<(string, string, string, double, double)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4)));
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
