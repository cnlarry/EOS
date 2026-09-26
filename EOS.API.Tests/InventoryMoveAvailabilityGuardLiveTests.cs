using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Data.Inventory;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Tests.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 出库校验按**可用量**拦的真库验收（ADR-020 §9.7 D7-⑧ / §10 WS-23）。
///
/// ⚠️ **这是一次行为变更**：口径由 `QTY` 改为 `USEABLE_QTY`（数量 − 有效冻结 − 有效预留）。
/// 钉住的是一对**边界数**：在库 100、其中 10 被冻结 ⇒ 可用量 90
///   · 出 **95** ⇒ **被拒**（旧口径会放行——这正是"冻结拦不住出库"的旧实况）
///   · 出 **90** ⇒ 放行（恰好等于可用量，不多拦一分）
/// 走的是**真实移动路径**：借出单批核 ⇒ `inventory-move`（OUT）⇒ 余额扣减 + 可用量列同步。
///
/// 判别性：把判据改回 `QTY` ⇒ 第①条变红。
/// 夹具 `ZZAVID` 前缀自造（料件 + 余额 + 冻结 + 批次账 + 借出单），结束即删。
/// </summary>
[Collection("live-database")]
public sealed class InventoryMoveAvailabilityGuardLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 130108;
    private const string Product = "ZZAVID1";
    private const string Depot = "CP";
    private const string Location = "-";
    /// <summary>
    /// 批次落**空串**：该库别的批次档位未启用时，移动引擎在出库前会把批次收敛成档位口径（空批），
    /// 所以余额行的格子必须是收敛后的那一格——否则守卫比到的会是一张被 `EnsureDepotRowsAsync`
    /// 新建的空批零行（实测诊断：`BASE_QTY=40 QTY=0 USABLE=0 BATCH=<空>`），永远判"不足"。
    /// </summary>
    private const string Batch = "";
    private const string Unit = "PCS";
    private const string LoanType = "ZZAVID";
    private const string LoanNo = "ZZAVID-OUT1";
    private const double StockQty = 100d;
    private const double FreezeQty = 10d;
    private const double Available = StockQty - FreezeQty;
    private const string RecordKey = LoanType + "," + LoanNo;

    private DbConnectionFactory _connections = null!;
    private WorkbenchAuditWriter _audit = null!;

    public async Task InitializeAsync()
    {
        _connections = Connections();
        _audit = new WorkbenchAuditWriter(_connections, new HttpContextAccessor(),
            new WorkbenchDefinitionProvider(_connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
            Options.Create(new AuditSettings()));

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
        await ExecAsync(connection, """
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE, UNIT_ID, MANAGE_BATCH)
                VALUES (@pro, N'ZZAVID 可用量料件', N'规格', '3', @unit, 0);
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, USEABLE_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@pro, @depot, @location, @batch, @qty, @qty, @usable, 3, @qty * 3);
            INSERT INTO dbo.INV_FREEZE (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SOURCE_TYPE, SOURCE_NO, FREEZE_QTY, STATUS, REASON)
                VALUES (@pro, @depot, @location, @batch, N'', N'', @freeze, N'A', N'ZZAVID 用例');
            INSERT INTO dbo.INV_BATCH_M (BATCH_NO, PRO_NO, IN_SUM, OUT_SUM) VALUES (@batch, @pro, @qty, 0);
            INSERT INTO dbo.INV_LOAN_M (LOAN_TYPE, LOAN_NO, LOAN_DATE, CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE)
                VALUES (@type, @no, '2026-03-01', 0, NULL, NULL);
            """, ("@pro", Product), ("@depot", Depot), ("@location", Location), ("@batch", Batch),
            ("@unit", Unit), ("@qty", StockQty), ("@usable", Available), ("@freeze", FreezeQty),
            ("@type", LoanType), ("@no", LoanNo));
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
    }

    private static async Task CleanupAsync(SqlConnection connection)
    {
        // 这里**不清理幂等表**：本用例直接驱动移动处理器，不走工作台执行器，因此根本不会写幂等行
        // （写幂等是执行器的事）。用例只清理它自己真的写出来的东西——不清理不存在的痕迹，
        // 也就不必去引用那张表的模块列（它刚随全仓改名从 `MODULE_ID` 变成 `M_IDX`，
        // 少一个引用点就少一处会被改名的连带损伤）。
        await ExecAsync(connection, """
            DELETE FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE = @type AND LTRIM(RTRIM(MUTUALITY_NO)) = @no;
            DELETE FROM dbo.INV_LOAN_D WHERE LOAN_TYPE = @type;
            DELETE FROM dbo.INV_LOAN_M WHERE LOAN_TYPE = @type;
            -- 批次账按 (批次, 料号) 双键收敛：空批是**共用的**哨兵格，只按批次删会连累别的料号
            DELETE FROM dbo.INV_BATCH_D WHERE BATCH_NO = @batch AND LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.INV_BATCH_M WHERE BATCH_NO = @batch AND LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            """, ("@module", ModuleId), ("@type", LoanType), ("@no", LoanNo),
            ("@pro", Product), ("@batch", Batch));
    }

    // ===== ① 冻结量之内：被拒（旧口径会放行） =====

    [Fact]
    public async Task 冻结量之内的出库被拒()
    {
        await SeedLoanLineAsync(moveQty: 95);

        var error = await Assert.ThrowsAsync<EffectValidationException>(() => RunMoveAsync());
        Assert.Contains("按可用量判", error.Message);

        // 被拒 ⇒ 一个字都没动
        var (quantity, usable) = await ReadBalanceAsync();
        Assert.Equal(StockQty, quantity);
        Assert.Equal(Available, usable);
    }

    // ===== ② 可用量之内：放行，且列跟着数量走 =====

    [Fact]
    public async Task 可用量之内的出库放行()
    {
        // **精确边界**：正好等于可用量（90）⇒ 放行；多一分（91）在①里已证明被拒。
        await SeedLoanLineAsync(moveQty: Available);

        await RunMoveAsync();

        var (quantity, usable) = await ReadBalanceAsync();
        Assert.Equal(StockQty - Available, quantity);
        // 列 = 新数量 − 既有冻结（10 − 10 = 0）；停在 90 就说明维护点没跑
        Assert.Equal(quantity - FreezeQty, usable);
    }

    // ===== 装配 =====

    private async Task SeedLoanLineAsync(double moveQty) =>
        await ExecAsync(await OpenAsync(), """
            INSERT INTO dbo.INV_LOAN_D (LOAN_TYPE, LOAN_NO, SERIAL_NO, PRO_NO, DEPOT_ID, LOCATION_NO, QTY, BATCH_NO, UNIT_ID)
                VALUES (@type, @no, 1, @pro, @depot, @location, @move, @batch, @unit);
            """, ("@type", LoanType), ("@no", LoanNo), ("@pro", Product), ("@depot", Depot),
            ("@location", Location), ("@move", moveQty), ("@batch", Batch), ("@unit", Unit));

    /// <summary>走一笔真实出库：借出单批核 ⇒ `inventory-move`（OUT）。</summary>
    private async Task RunMoveAsync()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            var handler = new InventoryMoveHandler(
                new EffectPhysicalColumns(), new DepotStockPolicyService(_connections, _audit));
            var moved = await handler.ExecuteAsync(Context(connection, transaction), CancellationToken.None);
            Assert.True(moved > 0, $"移动引擎报告影响 {moved} 行");
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task<(double Quantity, double Usable)> ReadBalanceAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            "SELECT ISNULL(QTY,0), ISNULL(USEABLE_QTY,0) FROM dbo.INV_PRO_DEPOT "
            + "WHERE LTRIM(RTRIM(PRO_NO)) = @pro AND LTRIM(RTRIM(DEPOT_ID)) = @depot "
            + "AND LTRIM(RTRIM(LOCATION_NO)) = @location AND LTRIM(RTRIM(BATCH_NO)) = @batch;", connection);
        command.Parameters.AddWithValue("@pro", Product);
        command.Parameters.AddWithValue("@depot", Depot);
        command.Parameters.AddWithValue("@location", Location);
        command.Parameters.AddWithValue("@batch", Batch);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "余额行不见了");
        return (reader.GetDouble(0), reader.GetDouble(1));
    }

    /// <summary>按模块 130108 的真实配置构造移动上下文（参数直接取自配置行的形状，不手写一套）。</summary>
    private static ServiceEffectContext Context(SqlConnection connection, SqlTransaction transaction) =>
        new(connection,
            transaction,
            new ModuleEffectPlan(ModuleId, "INV_LOAN_M", "INV_LOAN_D", "test", ["LOAN_TYPE", "LOAN_NO"], [], []),
            new EffectActionPlan(1, "APPROVE_EFFECT", "inventory-move", "库存移动", true, "BLOCK", null,
                JsonSerializer.SerializeToElement(new
                {
                    direction = "OUT",
                    depotField = "DEPOT_ID",
                    fieldMap = new
                    {
                        masterDate = "LOAN_DATE",
                        qty = "QTY",
                        detail = new[]
                        {
                            "SERIAL_NO", "PRO_NO", "UNIT_ID", "PRICE", "CURR_ID", "CURR_RATE", "AMOUNT", "BATCH_NO",
                        },
                    },
                }),
                null,
                []),
            EffectEvent.ApproveEffect,
            RecordKey,
            [LoanType, LoanNo],
            "ZZAVID001");

    private static DbConnectionFactory Connections() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build());

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
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
