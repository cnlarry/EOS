using System.Data;
using EOS.API.Data.Workbench;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 删除仓库前的从属行处置（<see cref="WorkbenchDeleteCascades"/>）：
/// 仓库的库位行是服务端自动生成的（新建仓库即写入一条哨兵行 `LOCATION_NO='-'`），用户没有逐个删除的入口。
///
/// <para>
/// 两条路都不允许出现"外键冲突直接 500"：
/// ① **未被库存使用** ⇒ 库位行随仓库一起清理（否则新建的仓库永远删不掉）；
/// ② **已被库存使用** ⇒ 以可读码 `DEPOT_IN_USE` 拒绝，且**一个字都不改**（拒绝路径不得留下副作用）。
/// </para>
///
/// 真库用例，需 <c>MSSQL_ERP_CONN</c>；全程一个事务内建数、断言、回滚。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class DepotDeleteCascadeLiveTests
{
    private const string Depot = "ZZCASCDP1";

    [Fact]
    public async Task 未被库存使用的仓库连同库位行一起清理()
    {
        var outcome = await RunAsync((c, t) => SeedAsync(c, t, withLocationTree: true));

        Assert.Null(outcome.Refusal);
        Assert.Equal(0, outcome.LocationsRemaining);   // 哨兵行与三层库位树全部随仓库清理
    }

    [Fact]
    public async Task 只有哨兵库位的新建仓库可以删除()
    {
        // 新建仓库最真实的样子：只有服务端写入的哨兵行，没有任何库存痕迹。
        var outcome = await RunAsync((c, t) => SeedAsync(c, t, withLocationTree: false));

        Assert.Null(outcome.Refusal);
        Assert.Equal(0, outcome.LocationsRemaining);
    }

    [Fact]
    public async Task 有库存余额的仓库被拒且库位行原样保留()
    {
        var outcome = await RunAsync(async (c, t) =>
        {
            await SeedAsync(c, t, withLocationTree: true);
            await ExecuteAsync(c, t,
                "INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, QTY, USEABLE_QTY) "
                + "VALUES (N'ZZCASC01', @d, N'-', 1, 1);",
                ("@d", Depot));
        });

        Assert.NotNull(outcome.Refusal);
        Assert.Equal(WorkbenchDeleteCascades.DepotInUseCode, outcome.Refusal!.Code);
        Assert.Contains("库存余额", outcome.Refusal.Message);
        Assert.Equal(5, outcome.LocationsRemaining);   // 拒绝路径不得改动任何行
    }

    [Fact]
    public async Task 已分配物料主货位的仓库被拒()
    {
        var outcome = await RunAsync(async (c, t) =>
        {
            await SeedAsync(c, t, withLocationTree: true);
            await ExecuteAsync(c, t,
                "INSERT INTO dbo.DEPOT_PRODUCT_LOCATION (DEPOT_ID, PRO_NO, LOCATION_NO, IS_PRIMARY) "
                + "VALUES (@d, N'ZZCASC01', N'-', 0);",
                ("@d", Depot));
        });

        Assert.NotNull(outcome.Refusal);
        Assert.Equal(WorkbenchDeleteCascades.DepotInUseCode, outcome.Refusal!.Code);
        Assert.Contains("物料主货位", outcome.Refusal.Message);
        Assert.Equal(5, outcome.LocationsRemaining);
    }

    // ---------- 夹具 ----------

    private sealed record Outcome(WorkbenchDeleteCascades.Refusal? Refusal, int LocationsRemaining);

    private static async Task<Outcome> RunAsync(Func<SqlConnection, SqlTransaction, Task> seed)
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, withLocationTree: false);   // 先摆平基线，避免上一轮残留
            await seed(connection, transaction);
            var refusal = await WorkbenchDeleteCascades.PrepareAsync(
                connection, transaction, "DEPOT", [Depot], CancellationToken.None);
            var remaining = await CountLocationsAsync(connection, transaction);
            return new Outcome(refusal, remaining);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, bool withLocationTree)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.DEPOT_PRODUCT_LOCATION WHERE DEPOT_ID=@d;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@d;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@d;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@d;
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@d, N'ZZ 级联删除用例仓');
            """, ("@d", Depot));

        // 哨兵行由服务端在保存时写入，这里按同一形态造出来
        await ExecuteAsync(connection, transaction,
            "INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, SEQ_NO, STATUS) "
            + "VALUES (@d, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', 0, N'A');", ("@d", Depot));

        if (!withLocationTree) return;
        // 三层树：A（区）→ A-R1（架）→ A-R1-B1（位），另有独立根 B；删除必须从叶子往上
        await ExecuteAsync(connection, transaction, """
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, SEQ_NO, STATUS)
                VALUES (@d, N'A',       NULL,    N'/A',              N'ZONE', 1, N'A'),
                       (@d, N'B',       NULL,    N'/B',              N'ZONE', 2, N'A'),
                       (@d, N'A-R1',    N'A',    N'/A/A-R1',         N'RACK', 1, N'A'),
                       (@d, N'A-R1-B1', N'A-R1', N'/A/A-R1/A-R1-B1', N'BIN',  1, N'A');
            """, ("@d", Depot));
    }

    private static async Task<int> CountLocationsAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@d;", connection, transaction);
        command.Parameters.AddWithValue("@d", Depot);
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
