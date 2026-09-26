using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 批次档位（`BATCH_MODE` 0 归零 / 1 保留 / 2 必填）在库存移动上的落地差异。
///
/// 三档的差别只在**非批管料件**上显形：批管料件（`MANAGE_BATCH=1`）任何档位都必填批号，
/// 所以档 0 与档 1 只有造出「产品级不管批次、仓库侧却填了批号」的单据才能区分 ——
/// 拿批管料件造例，三档的结果完全一样，用例等于没测。
///
/// 真库用例，需 <c>EOS_ERP_TEST_CONNECTION</c>；全程在一个事务内建数、断言、回滚。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class BatchModeTierLiveTests
{
    private const string Depot = "ADR14BMDP";
    private const string Plain = "ADR14BMPLN";
    private const string Unit = "ADR14BMUN";
    // 单据类型进 INV_BATCH_D.BATCH_ORDER_TYPE（char(4)），超长会被截断报错。
    private const string Type = "ABM1";

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    /// <summary>策略求值服务：档位判据必须走它，本用例不自行拼默认值。</summary>
    private static DepotStockPolicyService Policies(string connectionString) =>
        PolicyServiceFactory.Create(connectionString);

    [Fact]
    public async Task 档0把非批管料件的批号归零()
    {
        var observed = await RunAsync(depotBatchMode: 0, batchNo: "LOT0");

        // 余额行落哨兵批号，且不进批次账 —— 与四键改造前的行为一致。
        Assert.Equal(string.Empty, observed.BalanceBatch);
        Assert.Equal(0, observed.BatchMasterCount);
        Assert.Equal(0, observed.BatchDetailCount);
    }

    [Fact]
    public async Task 档1保留非批管料件的批号并进批次账()
    {
        var observed = await RunAsync(depotBatchMode: 1, batchNo: "LOT1");

        Assert.Equal("LOT1", observed.BalanceBatch);
        Assert.Equal(1, observed.BatchMasterCount);
        // 出库计的是批号主档的累计出库（IN_SUM 只在入库侧累加）。
        Assert.Equal(4d, observed.BatchMasterOutSum, 3);
        Assert.Equal(1, observed.BatchDetailCount);
        Assert.Equal(4d, observed.BatchDetailQty, 3);
    }

    [Fact]
    public async Task 档2要求该库别所有料件都填批号()
    {
        // 非批管料件未填批号 ⇒ 被拒（档 0/1 都放行）
        var exception = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunAsync(depotBatchMode: 2, batchNo: string.Empty));
        Assert.Contains("需要输入批号", exception.Message);

        // 填了批号 ⇒ 放行，且落在那一个批次行上
        var observed = await RunAsync(depotBatchMode: 2, batchNo: "LOT2");
        Assert.Equal("LOT2", observed.BalanceBatch);
        Assert.Equal(1, observed.BatchDetailCount);
    }

    /// <summary>
    /// 库别无策略行时按**部署级默认**的档位执行，而不是"没有行就算档 0"。
    ///
    /// 这条是本轮真正的判别点：档位静默失效的那个缺陷（策略清单在行集落表之前读，读到空表）
    /// 在三档用例下表现为"全部按档 0"，而**只有当部署级默认不是 0 时**，
    /// "库别无行"与"档 0"才会给出不同结果。
    /// </summary>
    [Fact]
    public async Task 库别无策略行时回落部署级默认档位()
    {
        var observed = await RunAsync(depotBatchMode: null, batchNo: "LOTD", deploymentBatchMode: 1);

        Assert.Equal("LOTD", observed.BalanceBatch);
        Assert.Equal(1, observed.BatchMasterCount);
        Assert.Equal(1, observed.BatchDetailCount);
    }

    private sealed record Observed(
        string BalanceBatch,
        int BatchMasterCount,
        double BatchMasterOutSum,
        int BatchDetailCount,
        double BatchDetailQty);

    /// <summary>
    /// 在一个事务内：建策略行与单据 → 批核出库 → 读出结果 → 回滚。
    /// <paramref name="depotBatchMode"/> 为 <c>null</c> 表示该库别**没有策略行**（走部署级默认）；
    /// <paramref name="deploymentBatchMode"/> 非空时临时改写部署级默认行的档位。
    /// </summary>
    private static async Task<Observed> RunAsync(int? depotBatchMode, string batchNo, int? deploymentBatchMode = null)
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            // 余额行的批号必须是**归一化之后**的那个：档 0 归零成哨兵，档 ≥1 保留原值，
            // 否则 CheckStock 会在另一行走空。
            var effectiveMode = depotBatchMode ?? deploymentBatchMode ?? 0;
            await SeedAsync(connection, transaction, depotBatchMode, deploymentBatchMode,
                effectiveMode == 0 ? string.Empty : batchNo.Trim());

            var columns = await new EffectPhysicalColumns().LoadAsync(connection, CancellationToken.None, transaction);
            var plan = Plan();
            var modulePlan = new ModuleEffectPlan(
                130104, "INV_OCCUR_OUT_M", "INV_OCCUR_OUT_D", "v1", new[] { "OCCUR_TYPE", "OCCUR_NO" },
                Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

            await using (var insert = new SqlCommand(
                "UPDATE dbo.INV_OCCUR_OUT_D SET BATCH_NO=@batch WHERE OCCUR_TYPE=@Type AND OCCUR_NO=@No",
                connection, transaction))
            {
                insert.Parameters.AddWithValue("@batch", batchNo);
                insert.Parameters.AddWithValue("@Type", Type);
                insert.Parameters.AddWithValue("@No", No);
                await insert.ExecuteNonQueryAsync();
            }

            var rowSet = plan.BuildRowSet(modulePlan, new[] { Type, No }, columns);
            await new InventoryMoveSql(connection, transaction, plan, EffectEvent.ApproveEffect, Policies(connectionString))
                .RunAsync(rowSet, CancellationToken.None);

            return await ReadAsync(connection, transaction);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static string No => "ADR14BMOUT01";

    private static InventoryMovePlan Plan() => InventoryMovePlan.Parse(JsonSerializer.SerializeToElement(new
    {
        direction = "OUT",
        fieldMap = new
        {
            masterDate = "OCCUR_DATE",
            qty = "QTY",
            detail = new[] { "SERIAL_NO", "PRO_NO", "UNIT_ID", "BATCH_NO" },
        },
    }));

    private static async Task<Observed> ReadAsync(SqlConnection connection, SqlTransaction transaction)
    {
        var balanceBatch = string.Empty;
        await using (var command = new SqlCommand(
            "SELECT ISNULL(LTRIM(RTRIM(BATCH_NO)), N'') FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro AND DEPOT_ID=@Depot",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Pro", Plain);
            command.Parameters.AddWithValue("@Depot", Depot);
            balanceBatch = (string?)await command.ExecuteScalarAsync() ?? string.Empty;
        }

        var masterCount = 0;
        var masterOutSum = 0d;
        await using (var command = new SqlCommand(
            "SELECT COUNT(*), ISNULL(SUM(OUT_SUM),0) FROM dbo.INV_BATCH_M WHERE PRO_NO=@Pro", connection, transaction))
        {
            command.Parameters.AddWithValue("@Pro", Plain);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            masterCount = reader.GetInt32(0);
            masterOutSum = reader.GetDouble(1);
        }

        var detailCount = 0;
        var detailQty = 0d;
        await using (var command = new SqlCommand(
            "SELECT COUNT(*), ISNULL(SUM(QTY),0) FROM dbo.INV_BATCH_D WHERE PRO_NO=@Pro AND BATCH_ORDER_TYPE=@Type",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Pro", Plain);
            command.Parameters.AddWithValue("@Type", Type);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            detailCount = reader.GetInt32(0);
            detailQty = reader.GetDouble(1);
        }

        return new Observed(balanceBatch, masterCount, masterOutSum, detailCount, detailQty);
    }

    private static async Task SeedAsync(
        SqlConnection connection, SqlTransaction transaction,
        int? depotBatchMode, int? deploymentBatchMode, string balanceBatch)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE=@Type;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro;
            DELETE FROM dbo.INV_BATCH_D WHERE PRO_NO=@Pro;
            DELETE FROM dbo.INV_BATCH_M WHERE PRO_NO=@Pro;
            DELETE FROM dbo.INV_OCCUR_OUT_D WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.INV_OCCUR_OUT_M WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO=@Pro;

            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH, UNIT_ID) VALUES (@Pro, N'ADR14BM 普通料件', 0, @Unit);
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR14BM 档位仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', 0, N'A');

            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, USEABLE_QTY, INIT_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@Pro, @Depot, N'-', @BalBatch, 10, 10, 10, 5, 50);

            INSERT INTO dbo.INV_OCCUR_OUT_M (OCCUR_TYPE, OCCUR_NO, OCCUR_DATE, CREATE_PERSON, CREATE_DATE)
                VALUES (@Type, @No, '2026-09-01', N'ADR14BM', '2026-09-01');
            INSERT INTO dbo.INV_OCCUR_OUT_D (OCCUR_TYPE, OCCUR_NO, SERIAL_NO, PRO_NO, QTY, DEPOT_ID, LOCATION_NO, UNIT_ID)
                VALUES (@Type, @No, 1, @Pro, 4, @Depot, N'-', @Unit);
            """,
            ("@Pro", Plain), ("@Depot", Depot), ("@Unit", Unit), ("@Type", Type), ("@No", No),
            ("@BalBatch", balanceBatch));

        // 库别行整行覆盖部署级默认：只有 BATCH_MODE 随用例变化，其余维度保持最松配置。
        // 传 null 表示该库别**不建策略行**，用于验证回落部署级默认的那一跳。
        if (depotBatchMode is { } depotMode)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH) "
                + "VALUES (@Depot, 0, N'FIXED', @Mode, 0, 1, 1)",
                ("@Depot", Depot), ("@Mode", depotMode));

        if (deploymentBatchMode is { } deploymentMode)
            await ExecuteAsync(connection, transaction,
                "UPDATE dbo.DEPOT_STOCK_POLICY SET BATCH_MODE=@Mode WHERE DEPOT_ID=N'*'",
                ("@Mode", deploymentMode));
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
