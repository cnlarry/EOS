using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 运行期混放校验：库别策略禁止混品号 / 混批次时，**往库位里加货**必须被拦住。
///
/// 判别点在于"已有的那一行"：位置空着时任何配置都放行，只有造出"该库位已有库存"的场景，
/// 才分得清"策略生效"与"策略只是存进了库"。
///
/// 真库用例，需 <c>EOS_ERP_TEST_CONNECTION</c>；全程在一个事务内建数、断言、回滚。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class MixingRuleLiveTests
{
    private const string Depot = "ADR14MXDP";
    private const string Bin = "MX-BIN";
    private const string Bin2 = "MX-BIN2";
    private const string ProA = "ADR14MXA";
    private const string ProB = "ADR14MXB";
    private const string Unit = "ADR14MXUN";
    // 单据类型进 INV_BATCH_D.BATCH_ORDER_TYPE（char(4)），超长会被截断报错。
    private const string Type = "AMX1";
    private const string No = "ADR14MXIN01";

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private sealed record Detail(string ProNo, string LocationNo, string BatchNo, double Qty);
    private sealed record Observed(Exception? Failure, IReadOnlyList<Detail> Balances);

    [Fact]
    public async Task 禁止混品号时往已有其它品号的库位入库被拒()
    {
        var observed = await RunAsync(mixProduct: false, mixBatch: true,
            balance: [new Detail(ProA, Bin, "", 10)],
            details: [new Detail(ProB, Bin, "", 5)]);

        Assert.NotNull(observed.Failure);
        Assert.Contains("不允许混放", observed.Failure!.Message);
        Assert.Contains("禁止混品号", observed.Failure.Message);
    }

    [Fact]
    public async Task 允许混品号时同一库位可入其它品号()
    {
        var observed = await RunAsync(mixProduct: true, mixBatch: true,
            balance: [new Detail(ProA, Bin, "", 10)],
            details: [new Detail(ProB, Bin, "", 5)]);

        Assert.Null(observed.Failure);
        Assert.Contains(observed.Balances, row => row.ProNo == ProA && row.LocationNo == Bin);
        Assert.Contains(observed.Balances, row => row.ProNo == ProB && row.LocationNo == Bin);
    }

    [Fact]
    public async Task 禁止混批次时同一品号入其它批次被拒()
    {
        var observed = await RunAsync(mixProduct: true, mixBatch: false,
            balance: [new Detail(ProA, Bin, "L1", 10)],
            details: [new Detail(ProA, Bin, "L2", 5)]);

        Assert.NotNull(observed.Failure);
        Assert.Contains("禁止混批次", observed.Failure!.Message);
    }

    /// <summary>
    /// 哨兵库位（<c>'-'</c>）是"未指定位置"的兜底行，存量本来就堆在一起；若参与校验，
    /// 任何未启用库位管理的库别都会在第二次入库时被判违规。
    /// </summary>
    [Fact]
    public async Task 哨兵库位不参与混放校验()
    {
        var observed = await RunAsync(mixProduct: false, mixBatch: false,
            balance: [new Detail(ProA, "-", "", 10)],
            details: [new Detail(ProB, "-", "", 5)]);

        Assert.Null(observed.Failure);
    }

    /// <summary>
    /// 只查"与已有库存冲突"时，一张单据里放两个品号就能把空库位一次塞混——本单自身也要查。
    /// </summary>
    [Fact]
    public async Task 同一单据把多个品号入同一空库位也被拒()
    {
        var observed = await RunAsync(mixProduct: false, mixBatch: true,
            balance: [],
            details: [new Detail(ProA, Bin2, "", 5), new Detail(ProB, Bin2, "", 3)]);

        Assert.NotNull(observed.Failure);
        Assert.Contains("本单把", observed.Failure!.Message);
    }

    // ---------- 策略收紧时的违规位置清单（P2-18 的 MIX_* 一半） ----------

    /// <summary>
    /// 这两个用例必须用**已提交**的存量数据：`SaveAsync` 只提供"自建连接"一种签名，
    /// 它的违规清单查询跑在那条连接上，看不见测试事务里尚未提交的余额行。
    /// 于是按仓库既有做法——提交后断言，收尾显式清理。
    /// </summary>
    [Fact]
    public async Task 收紧混品号时保存成功并给出违规位置清单()
    {
        var connectionString = RequireConnection();
        try
        {
            await SeedCommittedAsync(connectionString, mixProduct: true, mixBatch: true,
                balance: [new Detail(ProA, Bin, "", 10), new Detail(ProB, Bin, "", 6)]);

            var result = await Policies(connectionString).SaveAsync(
                new DepotStockPolicy(Depot, 0, "FIXED", 0, 0, false, true, true, false),
                "adr14-test", CancellationToken.None);

            Assert.True(result.Saved, string.Join("；", result.Errors));
            var warning = Assert.Single(result.Warnings, message => message.Contains("混放"));
            Assert.Contains(Bin, warning);
            Assert.Contains("1 个库位", warning);
        }
        finally
        {
            await CleanupCommittedAsync(connectionString);
        }
    }

    [Fact]
    public async Task 本来就禁止混放时不产生新的收紧告警()
    {
        var connectionString = RequireConnection();
        try
        {
            await SeedCommittedAsync(connectionString, mixProduct: false, mixBatch: true,
                balance: [new Detail(ProA, Bin, "", 10), new Detail(ProB, Bin, "", 6)]);

            // 再次保存同样的禁令：没有"由允许改禁止"这件事，就不该反复提示存量违规
            var result = await Policies(connectionString).SaveAsync(
                new DepotStockPolicy(Depot, 0, "FIXED", 0, 0, false, true, true, false),
                "adr14-test", CancellationToken.None);

            Assert.True(result.Saved, string.Join("；", result.Errors));
            Assert.DoesNotContain(result.Warnings, message => message.Contains("混放"));
        }
        finally
        {
            await CleanupCommittedAsync(connectionString);
        }
    }

    private static async Task SeedCommittedAsync(
        string connectionString, bool mixProduct, bool mixBatch, IReadOnlyList<Detail> balance)
    {
        await CleanupCommittedAsync(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await SeedAsync(connection, transaction, mixProduct, mixBatch, balance, details: []);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task CleanupCommittedAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            DELETE FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE=@Type;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.INV_BATCH_D WHERE BATCH_ORDER_TYPE=@Type;
            DELETE FROM dbo.INV_OCCUR_IN_D WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.INV_OCCUR_IN_M WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@ProA, @ProB);
            """, connection);
        command.Parameters.AddWithValue("@Type", Type);
        command.Parameters.AddWithValue("@Depot", Depot);
        command.Parameters.AddWithValue("@ProA", ProA);
        command.Parameters.AddWithValue("@ProB", ProB);
        await command.ExecuteNonQueryAsync();
    }

    private static DepotStockPolicyService Policies(string connectionString) =>
        new(new DbConnectionFactory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionString,
            })
            .Build()));

    private static async Task<Observed> RunAsync(
        bool mixProduct, bool mixBatch, IReadOnlyList<Detail> balance, IReadOnlyList<Detail> details)
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, mixProduct, mixBatch, balance, details);

            var columns = await new EffectPhysicalColumns().LoadAsync(connection, CancellationToken.None, transaction);
            var plan = Plan();
            var modulePlan = new ModuleEffectPlan(
                130103, "INV_OCCUR_IN_M", "INV_OCCUR_IN_D", "v1", new[] { "OCCUR_TYPE", "OCCUR_NO" },
                Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
            var rowSet = plan.BuildRowSet(modulePlan, new[] { Type, No }, columns);

            try
            {
                await new InventoryMoveSql(connection, transaction, plan, EffectEvent.ApproveEffect, Policies(connectionString))
                    .RunAsync(rowSet, CancellationToken.None);
            }
            catch (EffectValidationException failure)
            {
                return new Observed(failure, Array.Empty<Detail>());
            }

            return new Observed(null, await ReadBalancesAsync(connection, transaction));
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static InventoryMovePlan Plan() => InventoryMovePlan.Parse(JsonSerializer.SerializeToElement(new
    {
        direction = "IN",
        fieldMap = new
        {
            masterDate = "OCCUR_DATE",
            qty = "QTY",
            detail = new[] { "SERIAL_NO", "PRO_NO", "UNIT_ID", "BATCH_NO" },
        },
    }));

    private static async Task<IReadOnlyList<Detail>> ReadBalancesAsync(SqlConnection connection, SqlTransaction transaction)
    {
        var rows = new List<Detail>();
        await using var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(PRO_NO)), LOCATION_NO, ISNULL(LTRIM(RTRIM(BATCH_NO)), N''), ISNULL(QTY,0) "
            + "FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@Depot ORDER BY PRO_NO, LOCATION_NO", connection, transaction);
        command.Parameters.AddWithValue("@Depot", Depot);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new Detail(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3)));
        return rows;
    }

    private static async Task SeedAsync(
        SqlConnection connection, SqlTransaction transaction,
        bool mixProduct, bool mixBatch, IReadOnlyList<Detail> balance, IReadOnlyList<Detail> details)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE=@Type;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.INV_BATCH_D WHERE BATCH_ORDER_TYPE=@Type;
            DELETE FROM dbo.INV_OCCUR_IN_D WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.INV_OCCUR_IN_M WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@ProA, @ProB);

            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH, UNIT_ID)
                VALUES (@ProA, N'混放测试A', 0, @Unit), (@ProB, N'混放测试B', 0, @Unit);
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'混放测试仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', 0, N'A'),
                       (@Depot, @Bin, NULL, N'/MX-BIN', N'BIN', N'混放测试货位', 1, N'A'),
                       (@Depot, @Bin2, NULL, N'/MX-BIN2', N'BIN', N'混放测试货位二', 2, N'A');
            -- 库别行整行覆盖部署级默认：默认是"允许混放"，用例只把要考的那一维打开限制。
            INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH)
                VALUES (@Depot, 0, N'FIXED', 0, 0, @MixProduct, @MixBatch);
            INSERT INTO dbo.INV_OCCUR_IN_M (OCCUR_TYPE, OCCUR_NO, OCCUR_DATE, LAST_UPDATE_BY)
                VALUES (@Type, @No, '2026-09-01', N'ADR14MX');
            """,
            ("@Depot", Depot), ("@Bin", Bin), ("@Bin2", Bin2), ("@ProA", ProA), ("@ProB", ProB),
            ("@Unit", Unit), ("@Type", Type), ("@No", No), ("@MixProduct", mixProduct), ("@MixBatch", mixBatch));

        var serial = 1;
        foreach (var row in balance)
            await InsertBalanceAsync(connection, transaction, row);
        foreach (var row in details)
            await InsertDetailAsync(connection, transaction, serial++, row);
    }

    private static async Task InsertBalanceAsync(SqlConnection connection, SqlTransaction transaction, Detail row) =>
        await ExecuteAsync(connection, transaction,
            "INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, COST_PRICE, COST_AMOUNT) "
            + "VALUES (@Pro, @Depot, @Loc, @Batch, @Qty, @Qty, 5, @Qty*5)",
            ("@Pro", row.ProNo), ("@Depot", Depot), ("@Loc", row.LocationNo), ("@Batch", row.BatchNo), ("@Qty", row.Qty));

    private static async Task InsertDetailAsync(SqlConnection connection, SqlTransaction transaction, int serial, Detail row) =>
        await ExecuteAsync(connection, transaction,
            "INSERT INTO dbo.INV_OCCUR_IN_D (OCCUR_TYPE, OCCUR_NO, SERIAL_NO, PRO_NO, QTY, DEPOT_ID, LOCATION_NO, BATCH_NO, UNIT_ID) "
            + "VALUES (@Type, @No, @Serial, @Pro, @Qty, @Depot, @Loc, @Batch, @Unit)",
            ("@Type", Type), ("@No", No), ("@Serial", serial), ("@Pro", row.ProNo), ("@Qty", row.Qty),
            ("@Depot", Depot), ("@Loc", row.LocationNo), ("@Batch", row.BatchNo), ("@Unit", Unit));

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
