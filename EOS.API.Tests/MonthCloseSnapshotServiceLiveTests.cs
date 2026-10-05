using System.Data;
using EOS.API.Data;
using EOS.API.Features.Inventory;
using EOS.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 月结快照生成的真库用例（整段在事务内，结束回滚）。
///
/// 覆盖 WS-8 的三条验收判据：
///   ⒜ **恒等式** `期初 + 本期收发 = 期末` 成立（数量与移动加权单价都算出来对）；
///   ⒝ **按期末时点反算，不读当前余额**：补结一个已经过去的期间时，得到的是**那一期期末值**——
///      夹具把余额表的数量故意写成 9999、并在该期之后又放一笔流水，两种"读当前余额"的写法都会露馅；
///   ⒞ **粒度按参数展开**：`MONTH_CLOSE_BY_BATCH` 0 → 1 ⇒ 行数按批次展开。
///
/// 另断两条边界：已批核的期**拒绝生成**；未批核的期重复生成是**重写**（先删本级明细）而不是追加。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class MonthCloseSnapshotServiceLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string MonthType = "ZZMC";
    private const string OpeningPeriod = "ZZMCM01";
    private const string SubjectPeriod = "ZZMCM02";
    private const string TestDepot = "ZZMC0001";
    private const string TestProduct = "ZZMCAPRO1";
    private const string TestBatch = "ZZMCB1";

    /// <summary>期初（上一期已批核快照）：100 @ 5。</summary>
    private const double OpeningQuantity = 100;
    private const double OpeningPrice = 5;

    [Fact]
    public async Task 生成快照_期末等于期初加本期收发()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, byBatch: false);

            var result = await SnapshotService().GenerateAsync(
                connection, transaction, Request(SubjectPeriod), CancellationToken.None);

            Assert.Equal(1, result.RowCount);
            Assert.False(result.ByBatch);

            var row = Assert.Single(await ReadSnapshotAsync(connection, transaction, SubjectPeriod));
            // 期初 100 + 本期收发（+30 入、−20 出）= 110
            Assert.Equal(110.0, row.Quantity, 4);
            // 移动加权：(100×5 + 30×6) / (100 + 30) = 5.230000…
            Assert.Equal(680.0 / 130.0, row.Price, 4);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task 生成快照_粒度按参数展开_批次从关到开行数增加()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, byBatch: false);
            var service = SnapshotService();

            var withoutBatch = await service.GenerateAsync(connection, transaction, Request(SubjectPeriod), CancellationToken.None);
            var rowsWithoutBatch = await ReadSnapshotAsync(connection, transaction, SubjectPeriod);

            // 参数 0 → 1：同一批数据按批次展开，期初行与本期行不再合并
            await SetMonthCloseScopeAsync(connection, transaction, byBatch: true, byLocation: false);
            var withBatch = await service.GenerateAsync(connection, transaction, Request(SubjectPeriod), CancellationToken.None);
            var rowsWithBatch = await ReadSnapshotAsync(connection, transaction, SubjectPeriod);

            Assert.Equal(1, withoutBatch.RowCount);
            Assert.Equal(string.Empty, Assert.Single(rowsWithoutBatch).BatchNo);
            Assert.Equal(110.0, Assert.Single(rowsWithoutBatch).Quantity, 4);

            Assert.Equal(2, withBatch.RowCount);
            Assert.Equal(new[] { string.Empty, TestBatch }, rowsWithBatch.Select(row => row.BatchNo).OrderBy(value => value).ToArray());
            Assert.Equal(100.0, rowsWithBatch.Single(row => row.BatchNo.Length == 0).Quantity, 4);
            Assert.Equal(10.0, rowsWithBatch.Single(row => row.BatchNo == TestBatch).Quantity, 4);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task 生成快照_补结过去期间得到该期期末值而不是当前余额()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, byBatch: false);

            // 该期之后又发生一笔 500 的入库：它属于下一期，不属于本期
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dbo.INV_DEPOT_LOG (PRO_NO, MUTUALITY_DATE, MUTUALITY_TYPE, MUTUALITY_NO, MUTUALITY_SERIAL_NO, IN_OUT, QTY, PRICE, DEPOT_ID, LOCATION_NO)
                    VALUES (@Pro, '2024-02-15', 'ZZMC', 'ZZMC0003', 1, 'I', 500, 7, @Depot, N'-');
                """, ("@Pro", TestProduct), ("@Depot", TestDepot));

            var result = await SnapshotService().GenerateAsync(
                connection, transaction, Request(SubjectPeriod), CancellationToken.None);

            var row = Assert.Single(await ReadSnapshotAsync(connection, transaction, SubjectPeriod));
            // 期初 100 + 本期净额 10 = 110；既不是"当前余额"（夹具里写的 9999），也不是含下一期的 610
            Assert.Equal(110.0, row.Quantity, 4);
            Assert.NotEqual(610.0, row.Quantity, 4);
            Assert.NotEqual(9999.0, row.Quantity, 4);
            Assert.Equal(1, result.RowCount);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task 生成快照_已批核的期拒绝生成()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, byBatch: false);
            await ExecuteAsync(connection, transaction,
                "UPDATE dbo.INV_PRO_MONTH_M SET CONFIRM_TAG = 1 WHERE MONTH_NO = @No;", ("@No", SubjectPeriod));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SnapshotService().GenerateAsync(
                connection, transaction, Request(SubjectPeriod), CancellationToken.None));

            Assert.Contains("已批核", error.Message, StringComparison.Ordinal);
            Assert.Empty(await ReadSnapshotAsync(connection, transaction, SubjectPeriod));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task 生成快照_重复生成是重写而不是追加()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, byBatch: true);
            var service = SnapshotService();

            await service.GenerateAsync(connection, transaction, Request(SubjectPeriod), CancellationToken.None);
            await service.GenerateAsync(connection, transaction, Request(SubjectPeriod), CancellationToken.None);

            var rows = await ReadSnapshotAsync(connection, transaction, SubjectPeriod);
            Assert.Equal(2, rows.Count);
            // 项次从 1 起连续，没有两套行叠在一起
            Assert.Equal(new[] { 1, 2 }, rows.Select(row => row.SerialNo).OrderBy(value => value).ToArray());
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task 生成快照_探路不写任何行()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, byBatch: false);

            var probe = await SnapshotService().GenerateAsync(
                connection, transaction,
                new MonthCloseSnapshotRequest(MonthType, SubjectPeriod, new DateTime(2024, 1, 31), Preview: true),
                CancellationToken.None);

            Assert.Equal(1, probe.RowCount);
            Assert.Equal(110.0, probe.EndingQuantity, 4);
            Assert.Empty(await ReadSnapshotAsync(connection, transaction, SubjectPeriod));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static MonthCloseSnapshotRequest Request(string monthNo)
        => new(MonthType, monthNo, new DateTime(2024, 1, 31), Preview: false);

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static MonthCloseSnapshotService SnapshotService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        var connections = new DbConnectionFactory(configuration);
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var audit = new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider, Options.Create(new AuditSettings()));
        return new MonthCloseSnapshotService(new DepotStockPolicyService(connections, audit));
    }

    private sealed record SnapshotRow(int SerialNo, string BatchNo, string LocationNo, double Quantity, double Price);

    private static async Task<List<SnapshotRow>> ReadSnapshotAsync(
        SqlConnection connection, SqlTransaction transaction, string monthNo)
    {
        await using var command = new SqlCommand(
            "SELECT SERIAL_NO, ISNULL(BATCH_NO, N''), ISNULL(LOCATION_NO, N''), ISNULL(QTY, 0), ISNULL(PRICE, 0) "
            + "FROM dbo.INV_PRO_MONTH_D WHERE MONTH_TYPE = @Type AND MONTH_NO = @No ORDER BY SERIAL_NO;",
            connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = MonthType;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = monthNo;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<SnapshotRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new SnapshotRow(
                reader.GetInt32(0),
                reader.GetString(1).Trim(),
                reader.GetString(2).Trim(),
                Convert.ToDouble(reader.GetValue(3)),
                Convert.ToDouble(reader.GetValue(4))));
        }
        return rows;
    }

    private static async Task SetMonthCloseScopeAsync(
        SqlConnection connection, SqlTransaction transaction, bool byBatch, bool byLocation)
    {
        await using var command = new SqlCommand(
            "UPDATE dbo.DEPOT_STOCK_POLICY SET MONTH_CLOSE_BY_BATCH = @Batch, MONTH_CLOSE_BY_LOCATION = @Location "
            + "WHERE RTRIM(DEPOT_ID) = '*';", connection, transaction);
        command.Parameters.Add("@Batch", SqlDbType.Bit).Value = byBatch;
        command.Parameters.Add("@Location", SqlDbType.Bit).Value = byLocation;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, bool byBatch)
    {
        await SetMonthCloseScopeAsync(connection, transaction, byBatch, byLocation: false);

        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_PRO_MONTH_D WHERE MONTH_TYPE = @Type;
            DELETE FROM dbo.INV_PRO_MONTH_M WHERE MONTH_TYPE = @Type;
            DELETE FROM dbo.INV_DEPOT_LOG WHERE DEPOT_ID = @Depot;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID = @Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO = @Pro;
            IF NOT EXISTS(SELECT 1 FROM dbo.DEPOT WHERE DEPOT_ID = @Depot)
                INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ZZMC 月结仓');
            IF NOT EXISTS(SELECT 1 FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID = @Depot AND LOCATION_NO = N'-')
                INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, STORAGE_TYPE, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', NULL, 0, N'A');
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE) VALUES (@Pro, N'ZZMC 月结料件', N'规格A', '3');

            -- 余额表的数量刻意与"该期期末"无关：生成快照若读了它，断言立刻露馅
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, QTY) VALUES (@Pro, @Depot, 9999);

            -- 上一期（已批核）：100 @ 5
            INSERT INTO dbo.INV_PRO_MONTH_M (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CREATE_PERSON, CREATE_DATE, CI)
                VALUES (@Type, @Opening, '2023-12-31', 1, N'ZZMC', GETDATE(), 'ZZMC');
            INSERT INTO dbo.INV_PRO_MONTH_D (MONTH_TYPE, MONTH_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY, PRICE)
                VALUES (@Type, @Opening, 1, @Pro, @Depot, 100, 5);

            -- 本期（未批核，待生成）
            INSERT INTO dbo.INV_PRO_MONTH_M (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CREATE_PERSON, CREATE_DATE, CI)
                VALUES (@Type, @Subject, '2024-01-31', 0, N'ZZMC', GETDATE(), 'ZZMC');

            -- 本期收发：+30 入（批次 ZZMCB1）、−20 出
            INSERT INTO dbo.INV_DEPOT_LOG (PRO_NO, MUTUALITY_DATE, MUTUALITY_TYPE, MUTUALITY_NO, MUTUALITY_SERIAL_NO, IN_OUT, QTY, PRICE, DEPOT_ID, LOCATION_NO, BATCH_NO)
                VALUES (@Pro, '2024-01-10', 'ZZMC', 'ZZMC0001', 1, 'I', 30, 6, @Depot, N'-', @Batch),
                       (@Pro, '2024-01-20', 'ZZMC', 'ZZMC0002', 1, 'O', 20, 0, @Depot, N'-', @Batch);
            """,
            ("@Type", MonthType), ("@Opening", OpeningPeriod), ("@Subject", SubjectPeriod),
            ("@Pro", TestProduct), ("@Depot", TestDepot), ("@Batch", TestBatch));
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
