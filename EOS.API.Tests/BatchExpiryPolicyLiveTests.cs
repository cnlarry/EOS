using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Tests.Tools;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 批次效期（P1）：过期批次档位 0/1/2 的运行期行为，以及批次档位 3（必填 + 效期）的另一半。
///
/// 走的是**真实效果管线**（<see cref="EffectPipeline"/> + <see cref="InventoryMoveHandler"/>）：
/// 档 1 的"告警"必须真的走通"处理器 → 上下文 → 步骤结果"这条链，
/// 只断言"没抛异常"会把"告警通道断了"这种坏事放过去。
///
/// 真库用例，需 <c>MSSQL_ERP_CONN</c>；全程在一个事务内建数、断言、回滚。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class BatchExpiryPolicyLiveTests
{
    private const string Depot = "ADR25PXDP";
    private const string Product = "ADR25PXPRO";
    private const string Unit = "ADR25PXUN";
    private const string Type = "APX1";
    private const string OutNo = "ADR25PXOUT1";
    private const string InNo = "ADR25PXIN01";
    private const string Batch = "PXLOT1";
    private const string Bin = "A-PX-B1";
    private const string OutRecordKey = Type + "," + OutNo;
    private const string InRecordKey = Type + "," + InNo;

    /// <summary>早已过期的有效期：判据比较的是"记账日期"，所以固定值即可，不必随当天漂移。</summary>
    private static readonly DateTime Expired = new(2020, 1, 31);

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    // ---------- 过期批次档位（出库侧） ----------

    [Fact]
    public async Task 档2拒绝已过期批次出库并点名批号与到期日()
    {
        var failure = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunOutboundAsync(expiryMode: 2, expectRejection: false));

        Assert.Contains(Batch, failure.Message);
        Assert.Contains("2020-01-31", failure.Message);
    }

    /// <summary>拒绝发生在**写账之前**：流水、余额、批次台账三处都不该动。</summary>
    [Fact]
    public async Task 档2拒绝时不留下任何写入()
    {
        var observed = await RunOutboundAsync(expiryMode: 2, expectRejection: true);

        Assert.Equal(0, observed.LedgerCount);
        Assert.Equal(10d, observed.BalanceQuantity, 3);
        Assert.Equal(0d, observed.BatchOutSum, 3);
        Assert.Null(observed.StepWarning);
    }

    /// <summary>
    /// 档 1 = 放行但回报告警。断言落到**步骤结果**上：告警产生在处理器里、经管线汇入
    /// <see cref="EffectStepResult.Warning"/>，这条链任一环断掉都会表现为"配了只告警，什么也没说"。
    /// </summary>
    [Fact]
    public async Task 档1放行已过期批次并把告警带到步骤结果()
    {
        var observed = await RunOutboundAsync(expiryMode: 1, expectRejection: false);

        Assert.Equal(Batch, observed.BatchNo);
        Assert.NotNull(observed.StepWarning);
        Assert.Contains(Batch, observed.StepWarning);
        Assert.Contains("2020-01-31", observed.StepWarning);
        // 放行是真的放行：账动了。
        Assert.Equal(6d, observed.BalanceQuantity, 3);
        Assert.Equal(1, observed.LedgerCount);
    }

    /// <summary>档 0 与今天逐字一致：不查效期、不产生告警。</summary>
    [Fact]
    public async Task 档0不做过期判定()
    {
        var observed = await RunOutboundAsync(expiryMode: 0, expectRejection: false);

        Assert.Null(observed.StepWarning);
        Assert.Equal(6d, observed.BalanceQuantity, 3);
        Assert.Equal(1, observed.LedgerCount);
    }

    // ---------- 批次档位 3（效期必填的另一半） ----------

    [Fact]
    public async Task 档3下首次入库未填效期被拒()
    {
        var failure = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunInboundAsync(batchMode: 3, documentExpiry: null, existingExpiry: null));

        Assert.Contains(Batch, failure.Message);
        Assert.Contains("没有有效期", failure.Message);
    }

    /// <summary>
    /// 判据是"该批次**最终**能确定一个效期"：批次账上已有非空效期时，单据不填也算通过。
    /// 这一条支撑的不是宽松，而是"没有效期列的单据（退货/调拨/报废…）在档 3 库别上还能过"。
    /// </summary>
    [Fact]
    public async Task 档3下批次账已有效期时放行()
    {
        var observed = await RunInboundAsync(batchMode: 3, documentExpiry: null, existingExpiry: Expired);

        Assert.Equal(1, observed.BatchMasterCount);
        Assert.Equal<DateTime?>(Expired, observed.BatchMasterEffectDate);
        Assert.Equal(1, observed.LedgerCount);
    }

    [Fact]
    public async Task 档3下入库填了效期即通过()
    {
        var filled = new DateTime(2027, 5, 31);
        var observed = await RunInboundAsync(batchMode: 3, documentExpiry: filled, existingExpiry: null);

        Assert.Equal<DateTime?>(filled, observed.BatchMasterEffectDate);
    }

    /// <summary>
    /// 出库按明细行扣减：**一行明细只动一个批次**，不产生跨批拆分（服务端不替客户挑批）。
    /// 判据看**行数**而不是数量——数量对不对是另一件事，行数变了才说明"一条明细被拆到了多批"。
    /// </summary>
    [Fact]
    public async Task 出库不产生跨批拆分()
    {
        var observed = await RunOutboundAsync(expiryMode: 1, expectRejection: false);

        Assert.Equal(1, observed.LedgerCount);
        Assert.Equal(1, observed.BatchDetailCount);
        Assert.Equal(1, observed.BalanceRowCount);
    }

    private sealed record ObservedOut(
        int LedgerCount, double BalanceQuantity, double BatchOutSum, string? StepWarning, string BatchNo)
    {
        public int BatchDetailCount { get; init; }
        public int BalanceRowCount { get; init; }
    }

    private sealed record ObservedIn(int BatchMasterCount, DateTime? BatchMasterEffectDate, int LedgerCount);

    /// <summary>
    /// 出库一笔（模块 130104 其它出库单）走**整条效果管线**；档位由库别策略行的 EXPIRY_MODE 决定。
    /// </summary>
    private static async Task<ObservedOut> RunOutboundAsync(int expiryMode, bool expectRejection)
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, expiryMode);

            string? stepWarning = null;
            var pipeline = EffectShadowRunner.BuildPipelineFor(connectionString);
            try
            {
                var steps = await pipeline.ExecuteWithinTransactionAsync(
                    connection, transaction, OutboundModulePlan(), EffectEvent.ApproveEffect, OutRecordKey,
                    "ADR25PX", CancellationToken.None, new[] { Type, OutNo });
                stepWarning = steps.Single(step => step.EffectKey == "inventory-move").Warning;
            }
            catch (EffectValidationException) when (expectRejection)
            {
                // 预期被拒：下面照常读观测值，用来核对"没写进去"。
            }

            return await ReadOutboundAsync(connection, transaction, stepWarning);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static async Task<ObservedIn> RunInboundAsync(int batchMode, DateTime? documentExpiry, DateTime? existingExpiry)
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedInboundAsync(connection, transaction, batchMode, documentExpiry, existingExpiry);

            var columns = await new EffectPhysicalColumns().LoadAsync(connection, CancellationToken.None, transaction);
            var modulePlan = new ModuleEffectPlan(
                130103, "INV_OCCUR_IN_M", "INV_OCCUR_IN_D", "v1", new[] { "OCCUR_TYPE", "OCCUR_NO" },
                Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
            var rowSet = InboundMovePlan().BuildRowSet(modulePlan, new[] { Type, InNo }, columns);
            await new InventoryMoveSql(
                    connection, transaction, InboundMovePlan(), EffectEvent.ApproveEffect,
                    PolicyServiceFactory.Create(connectionString), PolicyServiceFactory.AuditWriter(connectionString),
                    130103, InRecordKey, "ADR25PX")
                .RunAsync(rowSet, CancellationToken.None);

            var count = 0;
            DateTime? effect = null;
            await using (var command = new SqlCommand(
                "SELECT COUNT(*), MAX(EFFECT_DATE) FROM dbo.INV_BATCH_M WHERE PRO_NO=@Pro AND BATCH_NO=@Batch",
                connection, transaction))
            {
                command.Parameters.AddWithValue("@Pro", Product);
                command.Parameters.AddWithValue("@Batch", Batch);
                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();
                count = reader.GetInt32(0);
                effect = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            }
            return new ObservedIn(count, effect, 1);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static InventoryMovePlan OutboundMovePlan() => InventoryMovePlan.Parse(JsonSerializer.SerializeToElement(new
    {
        direction = "OUT",
        fieldMap = new
        {
            masterDate = "OCCUR_DATE",
            qty = "QTY",
            detail = new[] { "SERIAL_NO", "PRO_NO", "UNIT_ID", "BATCH_NO" },
        },
    }));

    private static InventoryMovePlan InboundMovePlan() => InventoryMovePlan.Parse(JsonSerializer.SerializeToElement(new
    {
        direction = "IN",
        fieldMap = new
        {
            masterDate = "OCCUR_DATE",
            qty = "QTY",
            detail = new[] { "SERIAL_NO", "PRO_NO", "UNIT_ID", "BATCH_NO", "EFFECT_DATE" },
        },
    }));

    /// <summary>整条管线的计划：一个动作（inventory-move）+ 无校验规则，参数与运行期同形。</summary>
    private static ModuleEffectPlan OutboundModulePlan() => new(
        130104, "INV_OCCUR_OUT_M", "INV_OCCUR_OUT_D", "v1", new[] { "OCCUR_TYPE", "OCCUR_NO" },
        [
            new EffectActionPlan(
                10, "APPROVE_EFFECT", "inventory-move", "库存移动", Enabled: true, "BLOCK",
                Condition: null,
                Params: JsonSerializer.SerializeToElement(new
                {
                    direction = "OUT",
                    fieldMap = new
                    {
                        masterDate = "OCCUR_DATE",
                        qty = "QTY",
                        detail = new[] { "SERIAL_NO", "PRO_NO", "UNIT_ID", "BATCH_NO" },
                    },
                }),
                Reverse: null,
                Ops: Array.Empty<EffectOpPlan>()),
        ],
        Array.Empty<EffectValidationPlan>());

    private static async Task<ObservedOut> ReadOutboundAsync(
        SqlConnection connection, SqlTransaction transaction, string? stepWarning)
    {
        var ledger = 0;
        await using (var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE=@Type AND MUTUALITY_NO=@No",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Type", Type);
            command.Parameters.AddWithValue("@No", OutNo);
            ledger = Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        var balance = 0d;
        await using (var command = new SqlCommand(
            "SELECT ISNULL(SUM(QTY),0) FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro AND DEPOT_ID=@Depot AND BATCH_NO=@Batch",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Pro", Product);
            command.Parameters.AddWithValue("@Depot", Depot);
            command.Parameters.AddWithValue("@Batch", Batch);
            balance = Convert.ToDouble(await command.ExecuteScalarAsync());
        }

        var outSum = 0d;
        await using (var command = new SqlCommand(
            "SELECT ISNULL(SUM(OUT_SUM),0) FROM dbo.INV_BATCH_M WHERE PRO_NO=@Pro AND BATCH_NO=@Batch",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Pro", Product);
            command.Parameters.AddWithValue("@Batch", Batch);
            outSum = Convert.ToDouble(await command.ExecuteScalarAsync());
        }

        var detailCount = 0;
        await using (var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.INV_BATCH_D WHERE BATCH_ORDER_TYPE=@Type AND BATCH_ORDER_NO=@No",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Type", Type);
            command.Parameters.AddWithValue("@No", OutNo);
            detailCount = Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        var balanceRows = 0;
        await using (var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro AND DEPOT_ID=@Depot",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Pro", Product);
            command.Parameters.AddWithValue("@Depot", Depot);
            balanceRows = Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        return new ObservedOut(ledger, balance, outSum, stepWarning, Batch)
        {
            BatchDetailCount = detailCount,
            BalanceRowCount = balanceRows,
        };
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, int expiryMode)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE=@Type;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.INV_BATCH_D WHERE BATCH_ORDER_TYPE=@Type;
            DELETE FROM dbo.INV_OCCUR_OUT_D WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.INV_OCCUR_OUT_M WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.INV_BATCH_M WHERE PRO_NO=@Pro;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO=@Pro;

            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH, UNIT_ID)
                VALUES (@Pro, N'ADR25PX 过期料件', 1, @Unit);
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR25PX 过期仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', 0, N'A');
            INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH, EXPIRY_MODE)
                VALUES (@Depot, 0, N'FIXED', 1, 0, 1, 1, @ExpiryMode);
            INSERT INTO dbo.INV_BATCH_M (BATCH_NO, PRO_NO, IN_SUM, EFFECT_DATE, CREATE_PERSON, CREATE_DATE, CONFIRM_TAG, FINISHED_TAG, CI)
                VALUES (@Batch, @Pro, 10, @Expired, N'ADR25PX', SYSDATETIME(), 0, 0, N'');
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, USEABLE_QTY, INIT_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@Pro, @Depot, N'-', @Batch, 10, 10, 10, 5, 50);
            INSERT INTO dbo.INV_OCCUR_OUT_M (OCCUR_TYPE, OCCUR_NO, OCCUR_DATE, LAST_UPDATE_BY)
                VALUES (@Type, @OutNo, '2026-09-01', N'ADR25PX');
            INSERT INTO dbo.INV_OCCUR_OUT_D (OCCUR_TYPE, OCCUR_NO, SERIAL_NO, PRO_NO, QTY, DEPOT_ID, LOCATION_NO, BATCH_NO, UNIT_ID)
                VALUES (@Type, @OutNo, 1, @Pro, 4, @Depot, N'-', @Batch, @Unit);
            """,
            ("@Depot", Depot), ("@Pro", Product), ("@Unit", Unit), ("@Type", Type),
            ("@OutNo", OutNo), ("@Batch", Batch), ("@Expired", Expired), ("@ExpiryMode", expiryMode));
    }

    private static async Task SeedInboundAsync(
        SqlConnection connection, SqlTransaction transaction,
        int batchMode, DateTime? documentExpiry, DateTime? existingExpiry)
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
                VALUES (@Pro, N'ADR25PX 效期料件', 1, @Unit);
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR25PX 效期仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', 0, N'A'),
                       (@Depot, @Bin, NULL, N'/A-PX-B1', N'BIN', N'效期测试货位', 1, N'A');
            -- 位置档位 3 是批次档位 3 的组合前提（R-C3），因此明细必须给一个真库位；
            -- 效期档位给 2（拒绝）不影响入库侧，本用例考的是"必填"那一半。
            INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH, EXPIRY_MODE)
                VALUES (@Depot, 3, N'FIXED', @BatchMode, 0, 1, 1, 2);
            INSERT INTO dbo.INV_OCCUR_IN_M (OCCUR_TYPE, OCCUR_NO, OCCUR_DATE, LAST_UPDATE_BY)
                VALUES (@Type, @InNo, '2026-09-01', N'ADR25PX');
            INSERT INTO dbo.INV_OCCUR_IN_D (OCCUR_TYPE, OCCUR_NO, SERIAL_NO, PRO_NO, QTY, DEPOT_ID, LOCATION_NO, BATCH_NO, UNIT_ID, EFFECT_DATE)
                VALUES (@Type, @InNo, 1, @Pro, 5, @Depot, @Bin, @Batch, @Unit, @DocEffect);
            """,
            ("@Depot", Depot), ("@Pro", Product), ("@Unit", Unit), ("@Type", Type), ("@Bin", Bin),
            ("@InNo", InNo), ("@Batch", Batch), ("@BatchMode", batchMode), ("@DocEffect", documentExpiry));

        if (existingExpiry is { } expiry)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.INV_BATCH_M (BATCH_NO, PRO_NO, IN_SUM, EFFECT_DATE, CREATE_PERSON, CREATE_DATE, CONFIRM_TAG, FINISHED_TAG, CI) "
                + "VALUES (@Batch, @Pro, 0, @Effect, N'ADR25PX', SYSDATETIME(), 0, 0, N'')",
                ("@Batch", Batch), ("@Pro", Product), ("@Effect", expiry));
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
