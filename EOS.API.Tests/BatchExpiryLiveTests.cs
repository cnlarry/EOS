using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 批次效期（P0）：入库单据上录的效期怎么落到批次账 `INV_BATCH_M.EFFECT_DATE`，
/// 以及"同一批号两个效期"该拦谁、不该拦谁。
///
/// 走的是**真实过账引擎**（<see cref="InventoryMoveSql"/>），不是复写一遍判据：
/// 判据写在引擎里、本用例只造单据看结果，两者分叉时用例才会红。
///
/// 真库用例，需 <c>MSSQL_ERP_CONN</c>；全程在一个事务内建数、断言、回滚。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class BatchExpiryLiveTests
{
    private const string Depot = "ADR25EXDP";
    private const string Product = "ADR25EXPRO";
    private const string Unit = "ADR25EXUN";
    // 单据类型进 INV_BATCH_D.BATCH_ORDER_TYPE（char(4)），超长会被截断报错。
    private const string Type = "AEX1";
    private const string No = "ADR25EXIN01";
    private const string RecordKey = Type + "," + No;

    private static readonly DateTime ExistingExpiry = new(2027, 3, 31);
    private static readonly DateTime NewExpiry = new(2027, 6, 30);

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    [Fact]
    public async Task 入库单据填了效期则落到批次主档()
    {
        var observed = await RunAsync(
            documentExpiry: ExistingExpiry,
            existing: null);

        Assert.Equal(1, observed.BatchMasterCount);
        Assert.Equal<DateTime?>(ExistingExpiry, observed.BatchMasterEffectDate);
        Assert.Equal(5d, observed.BatchMasterInSum, 3);
        // 数量仍按原来的路子落：效期只挂在批次账上，不动余额口径。
        Assert.Equal(5d, observed.BalanceQuantity, 3);
        Assert.Equal(1, observed.LedgerCount);
    }

    [Fact]
    public async Task 不填效期则批次主档落空且数量行为不变()
    {
        var observed = await RunAsync(documentExpiry: null, existing: null);

        Assert.Equal(1, observed.BatchMasterCount);
        Assert.Null(observed.BatchMasterEffectDate);
        Assert.Equal(5d, observed.BatchMasterInSum, 3);
        Assert.Equal(5d, observed.BalanceQuantity, 3);
        Assert.Equal(1, observed.LedgerCount);
    }

    [Fact]
    public async Task 同批号已有非空效期且不一致时拒绝整单()
    {
        var failure = await Assert.ThrowsAsync<EffectValidationException>(() => RunAsync(
            documentExpiry: NewExpiry,
            existing: new ExistingBatch(ExistingExpiry, InSum: 10, OutSum: 0)));

        // 文案必须点名批号与已有有效期（不然用户不知道该改哪个批号）
        Assert.Contains("EXLOT1", failure.Message);
        Assert.Contains("2027-03-31", failure.Message);
        Assert.Contains("2027-06-30", failure.Message);
    }

    /// <summary>
    /// 拒绝之后**整单没写进去**：流水、余额、批次账三处都不该动。
    /// 只断言"抛了异常"是不够的——一路写下来再抛，同样是红的。
    /// </summary>
    [Fact]
    public async Task 拒绝整单时不留下任何写入()
    {
        var observed = await RunAsync(
            documentExpiry: NewExpiry,
            existing: new ExistingBatch(ExistingExpiry, InSum: 10, OutSum: 0),
            expectRejection: true);

        Assert.Equal(0, observed.LedgerCount);
        Assert.Equal(0d, observed.BalanceQuantity, 3);
        Assert.Equal<DateTime?>(ExistingExpiry, observed.BatchMasterEffectDate);
        Assert.Equal(10d, observed.BatchMasterInSum, 3);
        Assert.Equal(0, observed.BatchDetailCount);
    }

    /// <summary>
    /// 主档行**从未有过任何进出量**（`IN_SUM` 与 `OUT_SUM` 都是 0）时不算"已有有效期"：
    /// 允许本单写入。这正是"批核 → 解批 → 改正效期 → 再批核"这条改错路径能走通的原因。
    /// </summary>
    [Fact]
    public async Task 主档无进出量时允许覆盖效期并留审计()
    {
        var observed = await RunAsync(
            documentExpiry: NewExpiry,
            existing: new ExistingBatch(ExistingExpiry, InSum: 0, OutSum: 0));

        Assert.Equal(NewExpiry, observed.BatchMasterEffectDate);
        Assert.Equal(5d, observed.BatchMasterInSum, 3);
        Assert.Equal(1, observed.BatchExpiryAuditCount);
    }

    /// <summary>
    /// 判别点：**进过又出光**（净额为 0，但 `IN_SUM=5` / `OUT_SUM=5`）的批次**不适用**上面的例外。
    /// 判据写成"净额为 0"时这一条会静默放行 —— 那等于允许后来的单据改写一个实打实用过的批次的效期。
    /// </summary>
    [Fact]
    public async Task 进过又出光的批次不算无进出量()
    {
        var failure = await Assert.ThrowsAsync<EffectValidationException>(() => RunAsync(
            documentExpiry: NewExpiry,
            existing: new ExistingBatch(ExistingExpiry, InSum: 5, OutSum: 5)));

        Assert.Contains("2027-03-31", failure.Message);
    }

    /// <summary>同一张单里同一个批号填了两个不同的效期 ⇒ 同样拒绝（否则取哪个取决于执行顺序）。</summary>
    [Fact]
    public async Task 单内同批号两个效期也拒绝()
    {
        var connections = new SqlConnection(RequireConnection());
        await using var connection = connections;
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, ExistingExpiry, existing: null, secondLineExpiry: NewExpiry);

            var failure = await Assert.ThrowsAsync<EffectValidationException>(
                () => MoveAsync(connection, transaction, RequireConnection()));
            Assert.Contains("EXLOT1", failure.Message);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private sealed record ExistingBatch(DateTime EffectDate, double InSum, double OutSum);

    private sealed record Observed(
        int BatchMasterCount,
        DateTime? BatchMasterEffectDate,
        double BatchMasterInSum,
        double BalanceQuantity,
        int LedgerCount,
        int BatchDetailCount,
        int BatchExpiryAuditCount);

    /// <summary>
    /// 一个事务内：建库别/料件/单据与（可选的）既有批次账行 → 批核入库 → 读结果 → 回滚。
    /// <paramref name="documentExpiry"/> 为空表示单据**没填**效期。
    /// <paramref name="expectRejection"/> 为真表示预期被拒（此时仍返回观测值，用于核对"没写进去"）。
    /// </summary>
    private static async Task<Observed> RunAsync(
        DateTime? documentExpiry,
        ExistingBatch? existing,
        bool expectRejection = false,
        DateTime? secondLineExpiry = null)
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, documentExpiry, existing, secondLineExpiry);

            if (expectRejection)
            {
                await Assert.ThrowsAsync<EffectValidationException>(() => MoveAsync(connection, transaction, connectionString));
            }
            else
            {
                await MoveAsync(connection, transaction, connectionString);
            }

            return await ReadAsync(connection, transaction);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    /// <summary>走真实引擎：行集由动作参数生成，与运行期同一条路径。</summary>
    private static async Task MoveAsync(SqlConnection connection, SqlTransaction transaction, string connectionString)
    {
        var columns = await new EffectPhysicalColumns().LoadAsync(connection, CancellationToken.None, transaction);
        var modulePlan = new ModuleEffectPlan(
            130103, "INV_OCCUR_IN_M", "INV_OCCUR_IN_D", "v1", new[] { "OCCUR_TYPE", "OCCUR_NO" },
            Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        var plan = Plan();
        var rowSet = plan.BuildRowSet(modulePlan, new[] { Type, No }, columns);
        await new InventoryMoveSql(
                connection, transaction, plan, EffectEvent.ApproveEffect,
                PolicyServiceFactory.Create(connectionString),
                PolicyServiceFactory.AuditWriter(connectionString),
                130103, RecordKey, "ADR25EX")
            .RunAsync(rowSet, CancellationToken.None);
    }

    private static InventoryMovePlan Plan() => InventoryMovePlan.Parse(JsonSerializer.SerializeToElement(new
    {
        direction = "IN",
        fieldMap = new
        {
            masterDate = "OCCUR_DATE",
            qty = "QTY",
            detail = new[] { "SERIAL_NO", "PRO_NO", "UNIT_ID", "BATCH_NO", "EFFECT_DATE" },
        },
    }));

    private static async Task<Observed> ReadAsync(SqlConnection connection, SqlTransaction transaction)
    {
        var masterCount = 0;
        DateTime? masterEffect = null;
        var masterInSum = 0d;
        await using (var command = new SqlCommand(
            "SELECT COUNT(*), MAX(EFFECT_DATE), ISNULL(SUM(IN_SUM),0) FROM dbo.INV_BATCH_M "
            + "WHERE PRO_NO=@Pro AND BATCH_NO=@Batch", connection, transaction))
        {
            command.Parameters.AddWithValue("@Pro", Product);
            command.Parameters.AddWithValue("@Batch", "EXLOT1");
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            masterCount = reader.GetInt32(0);
            masterEffect = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            masterInSum = reader.GetDouble(2);
        }

        var balance = 0d;
        await using (var command = new SqlCommand(
            "SELECT ISNULL(SUM(QTY),0) FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro AND DEPOT_ID=@Depot",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Pro", Product);
            command.Parameters.AddWithValue("@Depot", Depot);
            balance = Convert.ToDouble(await command.ExecuteScalarAsync());
        }

        var ledger = 0;
        await using (var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE=@Type AND MUTUALITY_NO=@No",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Type", Type);
            command.Parameters.AddWithValue("@No", No);
            ledger = Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        var detailCount = 0;
        await using (var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.INV_BATCH_D WHERE BATCH_ORDER_TYPE=@Type AND BATCH_ORDER_NO=@No",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Type", Type);
            command.Parameters.AddWithValue("@No", No);
            detailCount = Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        var audit = 0;
        await using (var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE ACTION=N'BATCH_EXPIRY' AND RESOURCE_KEY=@Key",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Key", RecordKey);
            audit = Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        return new Observed(masterCount, masterEffect, masterInSum, balance, ledger, detailCount, audit);
    }

    private static async Task SeedAsync(
        SqlConnection connection, SqlTransaction transaction,
        DateTime? documentExpiry, ExistingBatch? existing, DateTime? secondLineExpiry)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE=@Type;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.INV_BATCH_D WHERE BATCH_ORDER_TYPE=@Type;
            DELETE FROM dbo.INV_OCCUR_IN_D WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.INV_OCCUR_IN_M WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.INV_BATCH_M WHERE PRO_NO=@Pro;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO=@Pro;

            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH, UNIT_ID)
                VALUES (@Pro, N'ADR25EX 效期料件', 1, @Unit);
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR25EX 效期仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', 0, N'A');
            -- 库别行只把批次档位设为"保留"，其余维度保持最松：本用例考的是效期，不是档位组合。
            INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH)
                VALUES (@Depot, 0, N'FIXED', 1, 0, 1, 1);
            INSERT INTO dbo.INV_OCCUR_IN_M (OCCUR_TYPE, OCCUR_NO, OCCUR_DATE, LAST_UPDATE_BY)
                VALUES (@Type, @No, '2026-09-01', N'ADR25EX');
            """,
            ("@Depot", Depot), ("@Pro", Product), ("@Unit", Unit), ("@Type", Type), ("@No", No));

        await ExecuteAsync(connection, transaction,
            "INSERT INTO dbo.INV_OCCUR_IN_D (OCCUR_TYPE, OCCUR_NO, SERIAL_NO, PRO_NO, QTY, DEPOT_ID, LOCATION_NO, BATCH_NO, UNIT_ID, EFFECT_DATE) "
            + "VALUES (@Type, @No, 1, @Pro, 5, @Depot, N'-', N'EXLOT1', @Unit, @Effect)",
            ("@Type", Type), ("@No", No), ("@Pro", Product), ("@Depot", Depot), ("@Unit", Unit),
            ("@Effect", documentExpiry));

        if (secondLineExpiry is { } second)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.INV_OCCUR_IN_D (OCCUR_TYPE, OCCUR_NO, SERIAL_NO, PRO_NO, QTY, DEPOT_ID, LOCATION_NO, BATCH_NO, UNIT_ID, EFFECT_DATE) "
                + "VALUES (@Type, @No, 2, @Pro, 1, @Depot, N'-', N'EXLOT1', @Unit, @Effect)",
                ("@Type", Type), ("@No", No), ("@Pro", Product), ("@Depot", Depot), ("@Unit", Unit),
                ("@Effect", second));
        }

        if (existing is { } row)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.INV_BATCH_M (BATCH_NO, PRO_NO, IN_SUM, OUT_SUM, EFFECT_DATE, "
                + "CREATE_PERSON, CREATE_DATE, CONFIRM_TAG, FINISHED_TAG, CI) "
                + "VALUES (N'EXLOT1', @Pro, @InSum, @OutSum, @Effect, N'ADR25EX', SYSDATETIME(), 0, 0, N'')",
                ("@Pro", Product), ("@InSum", row.InSum), ("@OutSum", row.OutSum), ("@Effect", row.EffectDate));
        }
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
