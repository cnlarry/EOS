using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 送货单库存校验的**维度扩展**：描述符可选配置位置 / 批次后，库存充足性从"库别合计"改为
/// 定位到具体库位 / 批次；不配置时行为与改造前**逐字相同**。
///
/// 判别点在于"库存分散在两个库位行"：合计够、单库位不够。同一份数据只换配置就能区分两种语义，
/// 这是"维度真的生效了"最直接的证据——否则两种配置都会给出同一个答案。
///
/// 真库用例，需 <c>MSSQL_ERP_CONN</c>；全程在一个事务内建数、断言、回滚。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class CopSendStockDimensionLiveTests
{
    private const int ModuleId = 1406;
    private const string Type = "ADR25CS";
    private const string No = "ADR25CS001";
    private const string Depot = "ADR25DP";
    private const string Bin = "A-BIN";
    private const string Pro = "ADR25PR1";
    private const string Unit = "ADR25UN";

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    // ---------- 配置解析（纯逻辑，不需要数据库） ----------

    [Fact]
    public void 位置维度只配一边属于配置错误()
    {
        var columns = ColumnSet();
        // 只配明细侧：建不起关联
        var detailOnly = Assert.Throws<EffectConfigException>(() => CopSendCheck.Parse(
            Config(detailLocation: "LOCATION_NO"), "COP_SEND_M", columns));
        Assert.Contains("需同时配置", detailOnly.Message);

        // 只配余额侧：同样不行
        var stockOnly = Assert.Throws<EffectConfigException>(() => CopSendCheck.Parse(
            Config(stockLocation: "LOCATION_NO"), "COP_SEND_M", columns));
        Assert.Contains("需同时配置", stockOnly.Message);

        // 两边都配：通过
        CopSendCheck.Parse(Config(detailLocation: "LOCATION_NO", stockLocation: "LOCATION_NO"),
            "COP_SEND_M", columns);
    }

    [Fact]
    public void 新维度键指向不存在的列属于配置错误()
    {
        var exception = Assert.Throws<EffectConfigException>(() => CopSendCheck.Parse(
            Config(stockBatch: "NO_SUCH_COLUMN"), "COP_SEND_M", ColumnSet()));
        Assert.Contains("列不存在", exception.Message);
    }

    [Fact]
    public void 未配置新维度键时解析通过()
    {
        var config = CopSendCheck.Parse(Config(), "COP_SEND_M", ColumnSet());
        Assert.Null(config.Stock.LocationField);
        Assert.Null(config.Stock.BatchField);
        Assert.Null(config.Detail.LocationField);
    }

    // ---------- 维度行为（真库） ----------

    /// <summary>同一份数据、只换配置：不配位置时按库别合计放行，配了位置后定位到具体库位被拒。</summary>
    [Fact]
    public async Task 库存分散在两个库位时是否放行取决于位置维度是否启用()
    {
        var withoutDimension = await RunAsync(
            stockLocation: null, stockBatch: null, detailLocation: null, detailQty: 10, detailBin: Bin,
            balances: [("-", 3d), (Bin, 7d)]);
        Assert.Null(withoutDimension);   // 库别合计 10 ≥ 出库 10 ⇒ 放行（改造前的语义）

        var withDimension = await RunAsync(
            stockLocation: "LOCATION_NO", stockBatch: null, detailLocation: "LOCATION_NO",
            detailQty: 10, detailBin: Bin, balances: [("-", 3d), (Bin, 7d)]);
        Assert.NotNull(withDimension);   // 该库位只有 7 < 10 ⇒ 拒绝
        Assert.Contains("库存数量不足", withDimension!.Message);
        Assert.Contains(Depot + "/" + Bin, withDimension.Message);
    }

    [Fact]
    public async Task 启用位置维度后该库位库存充足则放行()
    {
        var failure = await RunAsync(
            stockLocation: "LOCATION_NO", stockBatch: null, detailLocation: "LOCATION_NO",
            detailQty: 7, detailBin: Bin, balances: [("-", 3d), (Bin, 7d)]);
        Assert.Null(failure);
    }

    /// <summary>明细没填位置时对的是哨兵行（"未指定位置"的存量），不是任意库位行。</summary>
    [Fact]
    public async Task 启用位置维度后未填位置的明细对哨兵行()
    {
        var failure = await RunAsync(
            stockLocation: "LOCATION_NO", stockBatch: null, detailLocation: "LOCATION_NO",
            detailQty: 10, detailBin: null, balances: [("-", 3d), (Bin, 100d)]);
        Assert.NotNull(failure);
        // 哨兵行只有 3；且文案里不该出现 "库别/-" 这种写法（哨兵是内部占位）
        Assert.Contains(Depot + "    ", failure!.Message);
        Assert.DoesNotContain(Depot + "/-", failure.Message);
    }

    [Fact]
    public async Task 启用批次维度后按批次定位()
    {
        var failure = await RunAsync(
            stockLocation: null, stockBatch: "BATCH_NO", detailLocation: null,
            detailQty: 10, detailBin: Bin, detailBatch: "L2",
            balances: [],
            batchedBalances: [("-", "L1", 20d), ("-", "L2", 3d)],
            batchLedger: [("L1", 20d), ("L2", 3d)]);
        Assert.NotNull(failure);
        Assert.Contains("库存数量不足", failure!.Message);
        Assert.Contains("L2", failure.Message);
    }

    // ---------- 夹具 ----------

    private static async Task<Exception?> RunAsync(
        string? stockLocation, string? stockBatch, string? detailLocation,
        double detailQty, string? detailBin,
        (string Location, double Qty)[] balances,
        string? detailBatch = null,
        (string Location, string Batch, double Qty)[]? batchedBalances = null,
        (string Batch, double InSum)[]? batchLedger = null)
    {
        var rows = batchedBalances is null
            ? balances.Select(item => (item.Location, "", item.Qty)).ToArray()
            : batchedBalances.Select(item => (item.Location, item.Batch, item.Qty)).ToArray();

        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, rows, batchLedger ?? []);
            await InsertDetailAsync(connection, transaction, detailQty, detailBin, detailBatch);

            var plan = new ModuleEffectPlan(ModuleId, "COP_SEND_M", "COP_SEND_D", "adr25",
                ["SEND_TYPE", "SEND_NO"], Array.Empty<EffectActionPlan>(),
                [new EffectValidationPlan(3, "SAVE", "custom-validation", true,
                    Params(detailLocation, stockLocation, stockBatch), null)]);
            try
            {
                await new EffectValidationExecutor().ValidateAsync(
                    connection, transaction, plan, "SAVE", CancellationToken.None, [Type, No]);
                return null;
            }
            catch (EffectValidationException failure)
            {
                return failure;
            }
            catch (EffectConfigException failure)
            {
                return failure;
            }
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static async Task SeedAsync(
        SqlConnection connection, SqlTransaction transaction,
        (string Location, string Batch, double Qty)[] balances, (string Batch, double InSum)[] batchLedger)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.COP_SEND_D WHERE SEND_TYPE=@Type;
            DELETE FROM dbo.COP_SEND_M WHERE SEND_TYPE=@Type;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro;
            DELETE FROM dbo.INV_BATCH_M WHERE PRO_NO=@Pro AND BATCH_NO LIKE 'L%';
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO=@Pro;
            UPDATE dbo.SYSSS SET PARAM_VALUE=N'1' WHERE OWNER_MODULE=110111 AND PARAM_KEY=N'SEND_TAG';

            INSERT INTO dbo.PRODUCT (PRO_NO, MANAGE_BATCH, UNIT_ID) VALUES (@Pro, 0, @Unit);
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR25 维度仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', 0, N'A'),
                       (@Depot, @Bin, NULL, N'/A-BIN', N'BIN', N'维度测试货位', 1, N'A');
            INSERT INTO dbo.COP_SEND_M (SEND_TYPE, SEND_NO, SEND_DATE, CREATE_DATE)
                VALUES (@Type, @No, '2026-03-01', '2026-03-01');
            """, ("@Type", Type), ("@No", No), ("@Pro", Pro), ("@Depot", Depot), ("@Bin", Bin), ("@Unit", Unit));

        foreach (var (location, batch, qty) in balances)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, COST_PRICE, COST_AMOUNT) "
                + "VALUES (@Pro, @Depot, @Loc, @Batch, @Qty, @Qty, 1, @Qty)",
                ("@Pro", Pro), ("@Depot", Depot), ("@Loc", location), ("@Batch", batch), ("@Qty", qty));

        foreach (var (batch, inSum) in batchLedger)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.INV_BATCH_M (BATCH_NO, PRO_NO, IN_SUM, OUT_SUM) VALUES (@Batch, @Pro, @InSum, 0)",
                ("@Batch", batch), ("@Pro", Pro), ("@InSum", inSum));
    }

    private static async Task InsertDetailAsync(
        SqlConnection connection, SqlTransaction transaction, double qty, string? location, string? batch) =>
        await ExecuteAsync(connection, transaction,
            "INSERT INTO dbo.COP_SEND_D (SEND_TYPE, SEND_NO, SERIAL_NO, PRO_NO, QTY, BATCH_NO, DEPOT_ID, LOCATION_NO, UNIT_ID, CLIENT_ORDER_NO) "
            + "VALUES (@Type, @No, 1, @Pro, @Qty, @Batch, @Depot, @Loc, @Unit, N'ADR25-O1')",
            ("@Type", Type), ("@No", No), ("@Pro", Pro), ("@Qty", qty), ("@Batch", batch),
            ("@Depot", Depot), ("@Loc", location), ("@Unit", Unit));

    /// <summary>校验计划的参数形态：外层 <c>{handler, check}</c>，与 PARAM_STRUCT 的存储形态一致。</summary>
    private static JsonElement Params(
        string? detailLocation = null, string? stockLocation = null, string? stockBatch = null) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["handler"] = CopSendCheck.HandlerKey,
            ["check"] = Config(detailLocation, stockLocation, stockBatch),
        });

    private static JsonElement Config(
        string? detailLocation = null, string? stockLocation = null, string? stockBatch = null)
    {
        var detail = new Dictionary<string, object?>
        {
            ["table"] = "COP_SEND_D", ["productField"] = "PRO_NO", ["qtyField"] = "QTY",
            ["spareQtyField"] = "SPARE_QTY", ["batchField"] = "BATCH_NO", ["depotField"] = "DEPOT_ID",
            ["unitField"] = "UNIT_ID", ["serialField"] = "SERIAL_NO",
        };
        if (detailLocation is not null) detail["locationField"] = detailLocation;

        var stock = new Dictionary<string, object?>
        {
            ["table"] = "INV_PRO_DEPOT", ["productField"] = "PRO_NO", ["depotField"] = "DEPOT_ID", ["qtyField"] = "QTY",
        };
        if (stockLocation is not null) stock["locationField"] = stockLocation;
        if (stockBatch is not null) stock["batchField"] = stockBatch;

        var root = new Dictionary<string, object?>
        {
            ["master"] = new Dictionary<string, object?>
            {
                ["typeField"] = "SEND_TYPE", ["noField"] = "SEND_NO",
                ["dateField"] = "SEND_DATE", ["createDateField"] = "CREATE_DATE",
            },
            ["detail"] = detail,
            ["product"] = new Dictionary<string, object?>
            {
                ["table"] = "PRODUCT", ["keyField"] = "PRO_NO", ["manageBatchField"] = "MANAGE_BATCH",
                ["unitField"] = "UNIT_ID", ["unit1Field"] = "UNIT_ID_1", ["unitRate1Field"] = "UNIT_RATE_1",
                ["unit2Field"] = "UNIT_ID_2", ["unitRate2Field"] = "UNIT_RATE_2",
                ["unit3Field"] = "UNIT_ID_3", ["unitRate3Field"] = "UNIT_RATE_3",
                ["unit4Field"] = "UNIT_ID_4", ["unitRate4Field"] = "UNIT_RATE_4",
            },
            ["depot"] = new Dictionary<string, object?> { ["table"] = "DEPOT", ["keyField"] = "DEPOT_ID" },
            ["stock"] = stock,
            ["batchStock"] = new Dictionary<string, object?>
            {
                ["table"] = "INV_BATCH_M", ["batchField"] = "BATCH_NO", ["productField"] = "PRO_NO",
                ["inField"] = "IN_SUM", ["outField"] = "OUT_SUM",
            },
            ["gateFlag"] = "SEND_TAG",
            ["maxDays"] = 30,
            ["messages"] = new Dictionary<string, object?>
            {
                ["batchRequired"] = "以下序号项需要输入批号 \r\n",
                ["dateTooOld"] = "送货日期不能小于建立日期30天",
                ["depotMissing"] = "以下库别不存在\r\n序号----库别\r\n",
                ["stockNotEnough"] = "库存数量不足\r\n料号---------------库别----出库数量----库存数量---不足数量\r\n",
                ["batchStockNotEnough"] = "批号库存数量不足\r\n",
            },
        };
        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>配置校验用到的物理列集合：与生产一致口径（表名.列名）。</summary>
    private static HashSet<string> ColumnSet()
    {
        var columns = new HashSet<string>();
        foreach (var column in new[]
                 {
                     "SEND_TYPE", "SEND_NO", "SEND_DATE", "CREATE_DATE", "PRO_NO", "QTY", "SPARE_QTY",
                     "BATCH_NO", "DEPOT_ID", "UNIT_ID", "SERIAL_NO", "LOCATION_NO",
                 })
            columns.Add("COP_SEND_M." + column);
        foreach (var column in new[]
                 {
                     "SEND_TYPE", "SEND_NO", "PRO_NO", "QTY", "SPARE_QTY", "BATCH_NO", "DEPOT_ID",
                     "UNIT_ID", "SERIAL_NO", "LOCATION_NO",
                 })
            columns.Add("COP_SEND_D." + column);
        foreach (var column in new[]
                 {
                     "PRO_NO", "MANAGE_BATCH", "UNIT_ID", "UNIT_ID_1", "UNIT_RATE_1", "UNIT_ID_2", "UNIT_RATE_2",
                     "UNIT_ID_3", "UNIT_RATE_3", "UNIT_ID_4", "UNIT_RATE_4",
                 })
            columns.Add("PRODUCT." + column);
        columns.Add("DEPOT.DEPOT_ID");
        foreach (var column in new[] { "PRO_NO", "DEPOT_ID", "QTY", "LOCATION_NO", "BATCH_NO" })
            columns.Add("INV_PRO_DEPOT." + column);
        foreach (var column in new[] { "BATCH_NO", "PRO_NO", "IN_SUM", "OUT_SUM" })
            columns.Add("INV_BATCH_M." + column);
        columns.Add("SYSSS.SEND_TAG");
        return columns;
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
