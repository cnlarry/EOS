using System.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 快照对账视图的真库验收（P2-2 / WS-12），整段在事务内、结束回滚。
///
/// 视图 `dbo.V_INV_MONTH_SNAPSHOT_RECON` 回答的是"**整本账**对不对"：
///   最近一期已批核快照 + 该期期末之后的流水净额 == 实时余额？
/// 口径里有三处踩过的坑，本文件逐条钉住：
///   ① 快照**按维度细分**，同一 (料号, 库别) 有多行 ⇒ 必须 SUM（取一行会静默少算）；
///   ② "其后流水"按**日期**比较，期末当天 12:00 的流水属于该期，用时间比较会误算进下一期；
///   ③ 没有任何已批核快照时视图也是 0 行——**与"对完无差异"同形不同义**，不能读成通过
///      （本文件的两个用例都先造出已批核快照，所以 0 行的含义是"比过且一致"）。
///
/// 夹具都是"完整快照"（按当前余额逐键生成），因此断言是**全库逐键**的，不是抽一个键看。
/// </summary>
[Collection("live-database")]
public sealed class MonthSnapshotReconLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string PeriodEnd = "2023-12-31";

    /// <summary>
    /// 快照 = 实时余额、其后无流水（期末取未来）⇒ 视图 0 行；
    /// 人为改一行余额 ⇒ 视图**恰好一行**并点名该键（DIFF = -1）；还原 ⇒ 又 0 行。
    /// 夹具里把一个真实键的快照**拆成两行**，用来钉住"多维度行必须求和"。
    /// </summary>
    [Fact]
    public async Task 对账视图_全库一致时零行_人为改一行则点名该键()
    {
        const string monthType = "ADR20G";
        const string monthNo = "ADR20G001";

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            // 夹具断言的是全库逐键一致，故 latest 必须唯一确定：清掉其它已批核期（回滚即恢复）
            await ExecuteAsync(connection, transaction, """
                DELETE FROM dbo.INV_PRO_MONTH_M WHERE CONFIRM_TAG = 1;
                DELETE FROM dbo.INV_PRO_MONTH_M WHERE MONTH_TYPE = @Mt;
                INSERT INTO dbo.INV_PRO_MONTH_M (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CREATE_PERSON, CREATE_DATE, CI)
                    VALUES (@Mt, @Mn, '2099-12-31', 1, N'ADR20G', GETDATE(), 'ADR20G');
                """, ("@Mt", monthType), ("@Mn", monthNo));

            // 完整快照：把当前余额逐键搬进月结明细（期末在未来 ⇒ 其后流水那条腿为空）
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dbo.INV_PRO_MONTH_D (MONTH_TYPE, MONTH_NO, SERIAL_NO, DEPOT_ID, PRO_NO, QTY, PRICE)
                SELECT @Mt, @Mn, ROW_NUMBER() OVER (ORDER BY p.DEPOT_ID, p.PRO_NO), p.DEPOT_ID, p.PRO_NO,
                       ISNULL(p.QTY, 0), ISNULL(p.COST_PRICE, 0)
                  FROM dbo.INV_PRO_DEPOT p;
                """, ("@Mt", monthType), ("@Mn", monthNo));

            // 取一个真实键，把它的快照拆成两行（和为原值）：视图若只取一行，这里就露馅。
            // 拆行只能靠**维度列**：七列唯一键把 (单别, 单号, 库别, 料号, 库位, 批号, 制程)
            // 定成一行，靠 SERIAL_NO 拆会撞约束（这正是迁移 231 想要的结构）。
            var (proNo, depotId, qty) = await ReadSplitCandidateAsync(connection, transaction);
            await ExecuteAsync(connection, transaction, """
                DELETE FROM dbo.INV_PRO_MONTH_D
                 WHERE MONTH_TYPE = @Mt AND RTRIM(PRO_NO) = @Pro AND RTRIM(DEPOT_ID) = @Depot;
                INSERT INTO dbo.INV_PRO_MONTH_D
                    (MONTH_TYPE, MONTH_NO, SERIAL_NO, DEPOT_ID, PRO_NO, QTY, PRICE, LOCATION_NO, BATCH_NO, PROCEDURE_TYPE_ID)
                    VALUES (@Mt, @Mn, 900001, @Depot, @Pro, FLOOR(@Qty / 2), 0, N'-', N'', N''),
                           (@Mt, @Mn, 900002, @Depot, @Pro, @Qty - FLOOR(@Qty / 2), 0, N'ADR20G-L2', N'', N'');
                """, ("@Mt", monthType), ("@Mn", monthNo), ("@Pro", proNo), ("@Depot", depotId), ("@Qty", qty));

            Assert.Equal(0, await CountDiffAsync(connection, transaction));

            // 人为改一行：绕过引擎直接改余额 ⇒ 必须报出**这一个**键
            await ExecuteAsync(connection, transaction, """
                UPDATE dbo.INV_PRO_DEPOT SET QTY = ISNULL(QTY, 0) + 1
                 WHERE RTRIM(PRO_NO) = @Pro AND RTRIM(DEPOT_ID) = @Depot;
                """, ("@Pro", proNo), ("@Depot", depotId));

            var diffs = await ReadDiffsAsync(connection, transaction);
            var diff = Assert.Single(diffs);
            Assert.Equal(proNo, diff.ProNo);
            Assert.Equal(depotId, diff.DepotId);
            Assert.Equal(monthNo, diff.MonthNo);
            Assert.Equal(-1.0, diff.DiffQty, 4);

            // 还原 ⇒ 又回到 0 行（证明上面那行确实是唯一差异）
            await ExecuteAsync(connection, transaction, """
                UPDATE dbo.INV_PRO_DEPOT SET QTY = ISNULL(QTY, 0) - 1
                 WHERE RTRIM(PRO_NO) = @Pro AND RTRIM(DEPOT_ID) = @Depot;
                """, ("@Pro", proNo), ("@Depot", depotId));
            Assert.Equal(0, await CountDiffAsync(connection, transaction));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    /// <summary>
    /// "快照 + 其后流水 == 实时余额" 在**期末为过去**时逐键成立：夹具按"余额 − 期末之后的流水净额"
    /// 反算快照，视图报出的差异必须**恰好**是"期末之后有流水、却没有余额行"的那些键
    ///（引擎会把归零的余额行删掉，所以正常键不会缺行）。
    ///
    /// 夹具还特意在**期末当天 12:00** 造一笔流水：它属于该期（快照已含它），视图若按时间比较
    /// 就会把它算成"其后流水"，该键立刻差 5 ⇒ 差异集与期望集不等，本用例变红。这是日期边界的判别性。
    /// </summary>
    [Fact]
    public async Task 对账视图_快照加其后流水逐键一致_差异恰好是其后有流水却无余额行的键()
    {
        const string monthType = "ADR20H";
        const string monthNo = "ADR20H001";

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await ExecuteAsync(connection, transaction, """
                DELETE FROM dbo.INV_PRO_MONTH_M WHERE CONFIRM_TAG = 1;
                DELETE FROM dbo.INV_PRO_MONTH_M WHERE MONTH_TYPE = @Mt;
                INSERT INTO dbo.INV_PRO_MONTH_M (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CREATE_PERSON, CREATE_DATE, CI)
                    VALUES (@Mt, @Mn, @PeriodEnd, 1, N'ADR20H', GETDATE(), 'ADR20H');
                """, ("@Mt", monthType), ("@Mn", monthNo), ("@PeriodEnd", PeriodEnd));

            // 期末当天 12:00 的一笔流水：属于该期，绝不能被算进"其后流水"
            var (proNo, depotId) = await ReadFirstKeyAsync(connection, transaction);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dbo.INV_DEPOT_LOG
                    (PRO_NO, MUTUALITY_DATE, MUTUALITY_TYPE, MUTUALITY_NO, MUTUALITY_SERIAL_NO, IN_OUT, QTY, PRICE, DEPOT_ID, LOCATION_NO)
                    VALUES (@Pro, CONVERT(datetime, '2023-12-31 12:00:00', 120), @Mt, @Mn, 1, 'I', 5, 0, @Depot, N'-');
                """, ("@Pro", proNo), ("@Depot", depotId), ("@Mt", monthType), ("@Mn", monthNo));

            // 快照 = 实时余额 − 期末之后的流水净额（逐键；同样按日期比较）
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dbo.INV_PRO_MONTH_D (MONTH_TYPE, MONTH_NO, SERIAL_NO, DEPOT_ID, PRO_NO, QTY, PRICE)
                SELECT @Mt, @Mn, ROW_NUMBER() OVER (ORDER BY p.DEPOT_ID, p.PRO_NO), p.DEPOT_ID, p.PRO_NO,
                       ISNULL(p.QTY, 0) - ISNULL(f.AFTER_QTY, 0), ISNULL(p.COST_PRICE, 0)
                  FROM dbo.INV_PRO_DEPOT p
                  LEFT JOIN (SELECT g.DEPOT_ID, g.PRO_NO,
                                    SUM(CASE WHEN g.IN_OUT = 'I' THEN ISNULL(g.QTY, 0) ELSE -ISNULL(g.QTY, 0) END) AS AFTER_QTY
                               FROM dbo.INV_DEPOT_LOG g
                              WHERE CONVERT(date, g.MUTUALITY_DATE) > CONVERT(date, @PeriodEnd)
                              GROUP BY g.DEPOT_ID, g.PRO_NO) f
                    ON f.DEPOT_ID = p.DEPOT_ID AND f.PRO_NO = p.PRO_NO;
                """, ("@Mt", monthType), ("@Mn", monthNo), ("@PeriodEnd", PeriodEnd));

            // 期望集独立算一遍（不复用视图的算式）：期末之后净额不为 0、却没有余额行的键
            var expected = await ReadOrphanKeysAsync(connection, transaction, PeriodEnd);
            var actual = await ReadDiffsAsync(connection, transaction);
            Assert.Equal(expected.Count, actual.Count);
            foreach (var (depot, pro, after) in expected)
            {
                var row = actual.Single(x => x.DepotId == depot && x.ProNo == pro);
                Assert.Equal(after, row.DiffQty, 4);
                Assert.Equal(monthNo, row.MonthNo);
            }

            // 边界判别：夹具那笔"期末当天 12:00"的流水属于该期，所以它所在的键**不得**出现在差异集里
            //（视图若按时间比较，这个键会因 +5 冒出来，上面的集合断言先红）
            Assert.Equal(1, await ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE = @Mt AND RTRIM(PRO_NO) = @Pro;",
                ("@Mt", monthType), ("@Pro", proNo)));
            Assert.DoesNotContain(actual, x => x.DepotId == depotId && x.ProNo == proNo);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private sealed record DiffRow(string MonthNo, string DepotId, string ProNo, double DiffQty);

    private static async Task<int> CountDiffAsync(SqlConnection connection, SqlTransaction transaction)
        => await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dbo.V_INV_MONTH_SNAPSHOT_RECON;");

    private static async Task<IReadOnlyList<DiffRow>> ReadDiffsAsync(SqlConnection connection, SqlTransaction transaction)
    {
        var rows = new List<DiffRow>();
        await using var command = new SqlCommand(
            "SELECT RTRIM(MONTH_NO), RTRIM(DEPOT_ID), RTRIM(PRO_NO), DIFF_QTY FROM dbo.V_INV_MONTH_SNAPSHOT_RECON;",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new DiffRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Convert.ToDouble(reader.GetValue(3))));
        }
        return rows;
    }

    private static async Task<(string ProNo, string DepotId, double Qty)> ReadSplitCandidateAsync(
        SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand(
            "SELECT TOP 1 RTRIM(PRO_NO), RTRIM(DEPOT_ID), CAST(ISNULL(QTY, 0) AS float) FROM dbo.INV_PRO_DEPOT "
            + "WHERE QTY >= 2 ORDER BY PRO_NO;", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "夹具需要一个余额 >= 2 的键来完成维度拆行。");
        return (reader.GetString(0), reader.GetString(1), reader.GetDouble(2));
    }

    /// <summary>
    /// 期望的差异集（独立于视图算式重算一遍）：期末之后流水净额不为 0、却没有余额行的键。
    /// 这类键才是账上真的对不上的——引擎把归零的余额行删掉了，所以"有流水没余额行"不正常。
    /// </summary>
    private static async Task<IReadOnlyList<(string DepotId, string ProNo, double After)>> ReadOrphanKeysAsync(
        SqlConnection connection, SqlTransaction transaction, string periodEnd)
    {
        var rows = new List<(string, string, double)>();
        await using var command = new SqlCommand("""
            SELECT RTRIM(g.DEPOT_ID), RTRIM(g.PRO_NO),
                   SUM(CASE WHEN g.IN_OUT = 'I' THEN ISNULL(g.QTY, 0) ELSE -ISNULL(g.QTY, 0) END)
              FROM dbo.INV_DEPOT_LOG g
             WHERE CONVERT(date, g.MUTUALITY_DATE) > CONVERT(date, @PeriodEnd)
               AND NOT EXISTS (SELECT 1 FROM dbo.INV_PRO_DEPOT p
                                WHERE RTRIM(p.DEPOT_ID) = RTRIM(g.DEPOT_ID)
                                  AND RTRIM(p.PRO_NO) = RTRIM(g.PRO_NO))
             GROUP BY g.DEPOT_ID, g.PRO_NO
            HAVING ABS(SUM(CASE WHEN g.IN_OUT = 'I' THEN ISNULL(g.QTY, 0) ELSE -ISNULL(g.QTY, 0) END)) > 0.0001;
            """, connection, transaction);
        command.Parameters.AddWithValue("@PeriodEnd", periodEnd);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), Convert.ToDouble(reader.GetValue(2))));
        }
        return rows;
    }

    private static async Task<(string ProNo, string DepotId)> ReadFirstKeyAsync(
        SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand(
            "SELECT TOP 1 RTRIM(PRO_NO), RTRIM(DEPOT_ID) FROM dbo.INV_PRO_DEPOT ORDER BY PRO_NO;",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "夹具需要一个余额键。");
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<int> ScalarAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
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
