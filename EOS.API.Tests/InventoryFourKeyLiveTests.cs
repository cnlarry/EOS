using System.Globalization;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存移动的四键定位：库存余额表按 (料号, 库别, 库位, 批次) 建键后，出库 / 解批必须
/// 落到**指定的那个库位行**，而不是同料号同库别下的全部行。
///
/// 为什么必须有这条用例：现有对拍与回归的数据全部落在哨兵库位上，此时"按两键定位"与
/// "按四键定位"结果完全一致，任何两键写法都能通过。只有造出同一 (料号, 库别) 下的两个
/// 真实库位行，才能区分两者 —— 两键写法会把扣减同时施加到两行上。
///
/// 真库用例，需 <c>MSSQL_ERP_CONN</c>；全程在一个事务内建数、断言、回滚。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class InventoryFourKeyLiveTests
{
    private const string Product = "ADR14P2PRO";
    private const string Depot = "ADR14P2DP";
    private const string Unit = "ADR14P2UN";
    private const string Bin = "ADR14P2-BIN";
    private const string Type = "ADR14P2";
    private const string No = "ADR14P2OUT01";

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    /// <summary>策略求值服务：批次档位判据必须走它，本用例不自行拼默认值。</summary>
    private static DepotStockPolicyService Policies(string connectionString) =>
        PolicyServiceFactory.Create(connectionString);

    [Fact]
    public async Task 出库与解批只作用于指定库位行()
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction);

            var columns = await new EffectPhysicalColumns().LoadAsync(connection, CancellationToken.None, transaction);
            var plan = InventoryMovePlan.Parse(System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                direction = "OUT",
                fieldMap = new
                {
                    masterDate = "OCCUR_DATE",
                    qty = "QTY",
                    detail = new[] { "SERIAL_NO", "PRO_NO", "UNIT_ID" },
                },
            }));
            var modulePlan = new ModuleEffectPlan(
                130104, "INV_OCCUR_OUT_M", "INV_OCCUR_OUT_D", "v1", new[] { "OCCUR_TYPE", "OCCUR_NO" },
                Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
            var rowSet = plan.BuildRowSet(modulePlan, new[] { Type, No }, columns);

            // ---------- 批核出库：只扣指定库位 ----------
            await new InventoryMoveSql(
                    connection, transaction, plan, EffectEvent.ApproveEffect, Policies(connectionString),
                    PolicyServiceFactory.AuditWriter(connectionString), 130104, Type + "," + No, "ADR14P2")
                .RunAsync(rowSet, CancellationToken.None);

            var afterOut = await ReadRowsAsync(connection, transaction);
            Assert.Equal(2, afterOut.Count);
            var sentinel = afterOut.Single(row => row.Location == "-");
            var bin = afterOut.Single(row => row.Location == Bin);

            // 判别点：两键写法会把两行同时扣 3（哨兵 10→7、库位 5→2）。
            Assert.Equal(10d, sentinel.Qty, 3);
            Assert.Equal(2d, bin.Qty, 3);

            // 库别级成本字段在同键内必须逐行一致（每行冗余同一个库别值）。
            Assert.Equal(sentinel.CostPrice, bin.CostPrice, 6);
            Assert.Equal(sentinel.CostAmount, bin.CostAmount, 6);

            // 流水只应记录被移动的那一个库位行（单据只有一行明细，落在 BIN）。
            var logRows = await ReadLogAsync(connection, transaction);
            var outRow = Assert.Single(logRows);
            Assert.Equal(Bin, outRow.Location);
            Assert.Equal("/" + Bin, outRow.Path);

            // ---------- 解批：只回退指定库位，且路径沿用批核当时的快照 ----------
            var undoSet = plan.BuildRowSet(modulePlan, new[] { Type, No }, columns);
            await new InventoryMoveSql(
                    connection, transaction, plan, EffectEvent.Deapprove, Policies(connectionString),
                    PolicyServiceFactory.AuditWriter(connectionString), 130104, Type + "," + No, "ADR14P2")
                .RunAsync(undoSet, CancellationToken.None);

            var afterUndo = await ReadRowsAsync(connection, transaction);
            Assert.Equal(10d, afterUndo.Single(row => row.Location == "-").Qty, 3);
            Assert.Equal(5d, afterUndo.Single(row => row.Location == Bin).Qty, 3);

            var reverseLog = await ReadLogAsync(connection, transaction, reverse: true);
            Assert.All(reverseLog, row => Assert.Equal("/" + row.Location, row.Path));
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE=@Type AND MUTUALITY_NO=@No;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro;
            DELETE FROM dbo.INV_OCCUR_OUT_D WHERE OCCUR_TYPE=@Type AND OCCUR_NO=@No;
            DELETE FROM dbo.INV_OCCUR_OUT_M WHERE OCCUR_TYPE=@Type AND OCCUR_NO=@No;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO=@Pro;

            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH, UNIT_ID) VALUES (@Pro, N'ADR14P2 四键料件', 0, @Unit);
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR14P2 四键仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', 0, N'A'),
                       (@Depot, @Bin, NULL, N'/ADR14P2-BIN', N'BIN', N'四键测试货位', 1, N'A');

            -- 同一 (料号, 库别) 下的两个库位行；库别级三字段两行取同一值。
            -- 可用量是同一次写入的一部分：出库充足性按可用量判（可用量 = 数量 − 冻结 − 预留），
            -- 直接造余额行而漏了它，这里就会造出"有货但不可用"的格子。
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, USEABLE_QTY, INIT_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@Pro, @Depot, N'-', N'', 10, 10, 10, 5, 75),
                       (@Pro, @Depot, @Bin, N'', 5, 5, 5, 5, 75);

            INSERT INTO dbo.INV_OCCUR_OUT_M (OCCUR_TYPE, OCCUR_NO, OCCUR_DATE, CREATE_PERSON, CREATE_DATE)
                VALUES (@Type, @No, '2026-09-01', N'ADR14P2', '2026-09-01');
            INSERT INTO dbo.INV_OCCUR_OUT_D (OCCUR_TYPE, OCCUR_NO, SERIAL_NO, PRO_NO, QTY, DEPOT_ID, LOCATION_NO, UNIT_ID)
                VALUES (@Type, @No, 1, @Pro, 3, @Depot, @Bin, @Unit);
            """, ("@Pro", Product), ("@Depot", Depot), ("@Unit", Unit), ("@Bin", Bin), ("@Type", Type), ("@No", No));
    }

    private sealed record BalanceRow(string Location, double Qty, double CostPrice, double CostAmount);
    private sealed record LogRow(string Location, string Path);

    private static async Task<List<BalanceRow>> ReadRowsAsync(SqlConnection connection, SqlTransaction transaction)
    {
        var rows = new List<BalanceRow>();
        await using var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(LOCATION_NO)), QTY, ISNULL(COST_PRICE,0), ISNULL(COST_AMOUNT,0) FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro ORDER BY LOCATION_NO",
            connection, transaction);
        command.Parameters.AddWithValue("@Pro", Product);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new BalanceRow(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3)));
        return rows;
    }

    private static async Task<List<LogRow>> ReadLogAsync(SqlConnection connection, SqlTransaction transaction, bool reverse = false)
    {
        var rows = new List<LogRow>();
        await using var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(LOCATION_NO)), ISNULL(LOCATION_PATH,'') FROM dbo.INV_DEPOT_LOG "
            + "WHERE MUTUALITY_TYPE=@Type AND MUTUALITY_NO=@No AND IN_OUT=@Io",
            connection, transaction);
        command.Parameters.AddWithValue("@Type", Type);
        command.Parameters.AddWithValue("@No", No);
        // OUT 方向批核写 O、解批写镜像的 I（负数量）。
        command.Parameters.AddWithValue("@Io", reverse ? "I" : "O");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new LogRow(reader.GetString(0), reader.GetString(1)));
        return rows;
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
