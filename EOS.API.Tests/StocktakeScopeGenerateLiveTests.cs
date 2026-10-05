using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 盘点单按库区范围生成明细（`stocktake-scope-generate`）：
/// ① 生成的行集 = 该库别下**当前**记在范围内库位（含库区自身）上、数量不为零的库存行；
///    区属取库位主档的物化路径（当前态），所以库位改挂到别的库区后就不再出现在原库区的盘点明细里。
/// ② 位置与批次原样带进明细：按库位盘出的差异若在库别层面被平均掉，盘点等于白盘。
/// ③ 已经有人工明细时不生成——生成不得覆盖人工录入。
///
/// 真库用例，需 <c>MSSQL_ERP_CONN</c>；全程在一个事务内建数、断言、回滚。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class StocktakeScopeGenerateLiveTests
{
    private const string Depot = "ADR27DP";
    private const string OtherDepot = "ADR27DQ";
    private const string ZoneA = "A";            // /A
    private const string BinA1 = "A-R1-B1";      // /A/A-R1/A-R1-B1
    private const string BinA2 = "A-R1-B2";      // /A/A-R1/A-R1-B2
    private const string EmptyZone = "C";        // /C，刻意不放库存
    private const string BinB1 = "B-R1-B1";      // /B/B-R1/B-R1-B1
    private const string ProductA = "ADR27P1";
    private const string ProductB = "ADR27P2";
    private const string ProductOther = "ADR27P3";

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    // ---------- 生成 ----------

    [Fact]
    public async Task 按库区生成明细_行集等于该区当前有量的库存行()
    {
        var outcome = await RunAsync(scope: ZoneA, seedStock: StandardStock, documentNo: "ADR27001");

        Assert.Equal(3, outcome.Generated);      // A-R1-B1 两行（不同批次）+ A-R1-B2 一行
        var rows = outcome.Rows.OrderBy(row => row.Product, StringComparer.Ordinal)
            .ThenBy(row => row.Batch, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { ProductA, ProductA, ProductB }, rows.Select(row => row.Product).ToArray());
        Assert.Equal(new[] { "", "LOT-A", "LOT-A" }, rows.Select(row => row.Batch).ToArray());
        Assert.Equal(new[] { BinA1, BinA1, BinA2 }, rows.Select(row => row.Location).ToArray());
    }

    [Fact]
    public async Task 按库区生成明细_账面数与盘点数都取当前库存量()
    {
        var outcome = await RunAsync(scope: ZoneA, seedStock: StandardStock, documentNo: "ADR27002");

        // 差异为 0 起算：人只需改实际数
        Assert.All(outcome.Rows, row => Assert.Equal(row.AccountQty, row.CheckQty));
        Assert.Equal(5d, outcome.Rows.Single(row => row.Location == BinA1 && row.Batch == "").CheckQty);
        Assert.Equal(7d, outcome.Rows.Single(row => row.Location == BinA1 && row.Batch == "LOT-A").CheckQty);
    }

    [Fact]
    public async Task 按库区生成明细_区属按当前位置_库位改挂后不再出现在原库区()
    {
        var before = await RunAsync(scope: ZoneA, seedStock: StandardStock, documentNo: "ADR27003");
        Assert.DoesNotContain(before.Rows, row => row.Location == BinB1);

        // 托盘所在库位改挂到 A 区下（当前位置变了）：按 A 区盘点从此应当盘到它
        var after = await RunAsync(
            scope: ZoneA, seedStock: StandardStock, documentNo: "ADR27004", reparentBinToZoneA: true);
        Assert.Contains(after.Rows, row => row.Location == BinB1);
        Assert.Equal(4, after.Generated);
    }

    [Fact]
    public async Task 按库区生成明细_数量为零的库存行不进明细()
    {
        var outcome = await RunAsync(scope: ZoneA, seedStock: StockWithZeroRow, documentNo: "ADR27005");

        // 零量行是降档归并留下的空壳，把"没有货"当成一行待盘点是噪声
        Assert.DoesNotContain(outcome.Rows, row => row.Location == BinA2);
        Assert.Equal(2, outcome.Generated);
    }

    [Fact]
    public async Task 按库区生成明细_同料号不同批次各成一行()
    {
        var outcome = await RunAsync(scope: ZoneA, seedStock: StandardStock, documentNo: "ADR27006");

        Assert.Equal(2, outcome.Rows.Count(row => row.Product == ProductA));
        Assert.Single(outcome.Rows, row => row.Product == ProductA && row.Batch == "LOT-A");
        Assert.Single(outcome.Rows, row => row.Product == ProductA && row.Batch == "");
    }

    [Fact]
    public async Task 按库区生成明细_哨兵位置上的货不属于任何库区()
    {
        var outcome = await RunAsync(scope: ZoneA, seedStock: StandardStock, documentNo: "ADR27007");

        // 未指定位置（哨兵行）的存量不属于任何库区：它恰恰是"要归位"的那一批
        Assert.DoesNotContain(outcome.Rows, row => row.Location == "-");
    }

    [Fact]
    public async Task 已有明细时不生成_不覆盖人工录入()
    {
        var outcome = await RunAsync(scope: ZoneA, seedStock: StandardStock, documentNo: "ADR27008",
            seedManualDetail: true);

        Assert.Equal(0, outcome.Generated);
        Assert.Single(outcome.Rows);
        Assert.Equal(99d, outcome.Rows[0].CheckQty);   // 人工填的值原样保留
    }

    // ---------- 拒绝路径 ----------

    [Fact]
    public async Task 未填盘点范围且无明细时报错并给出可执行的提示()
    {
        var failure = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunAsync(scope: null, seedStock: StandardStock, documentNo: "ADR27009"));

        // 文案必须带"无明细资料不可保存"：保存路径与既有验收脚本都按这句判定"该补明细了"
        Assert.Contains("无明细资料不可保存", failure.Message);
        Assert.Contains("盘点范围", failure.Message);
    }

    [Fact]
    public async Task 盘点范围不存在时报错()
    {
        var failure = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunAsync(scope: "NO-SUCH-ZONE", seedStock: StandardStock, documentNo: "ADR27010"));

        Assert.Contains("不存在", failure.Message);
    }

    [Fact]
    public async Task 盘点范围指向哨兵位置时报错()
    {
        var failure = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunAsync(scope: "-", seedStock: StandardStock, documentNo: "ADR27011"));

        Assert.Contains("未指定位置", failure.Message);
    }

    [Fact]
    public async Task 范围内没有账面库存时报错()
    {
        var failure = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunAsync(scope: EmptyZone, seedStock: StandardStock, documentNo: "ADR27012"));

        Assert.Contains("没有账面库存", failure.Message);
        Assert.Contains("无明细资料不可保存", failure.Message);
    }

    [Fact]
    public async Task 其他库别的库存不会被生成进来()
    {
        var outcome = await RunAsync(scope: ZoneA, seedStock: StandardStock, documentNo: "ADR27013");

        // 另一个库别存在同名库位 A，范围匹配必须限定在本单库别内
        Assert.DoesNotContain(outcome.Rows, row => row.Product == ProductOther);
    }

    // ---------- 保存路径的判定时点 ----------

    [Fact]
    public void 声明了明细生成者的模块不按未提交明细就地拒绝()
    {
        Assert.Equal(WorkbenchCommandHandler.EmptyDetailPolicy.RejectAfterEffects,
            WorkbenchCommandHandler.EmptyDetailPolicyFor(detailNoSave: true, generatesDetailRows: true));
        Assert.Equal(WorkbenchCommandHandler.EmptyDetailPolicy.Reject,
            WorkbenchCommandHandler.EmptyDetailPolicyFor(detailNoSave: true, generatesDetailRows: false));
        Assert.Equal(WorkbenchCommandHandler.EmptyDetailPolicy.Clear,
            WorkbenchCommandHandler.EmptyDetailPolicyFor(detailNoSave: false, generatesDetailRows: false));
        Assert.Equal(WorkbenchCommandHandler.EmptyDetailPolicy.Clear,
            WorkbenchCommandHandler.EmptyDetailPolicyFor(detailNoSave: false, generatesDetailRows: true));
    }

    [Fact]
    public void 只有保存期的明细生成者才算数()
    {
        Assert.True(EffectEngineInvoker.DeclaresDetailGenerator(Actions(
            new { seq = 1, eventCode = "SAVE", effectKey = "stocktake-scope-generate", enabled = true })));
        // 批核期不算：保存当下明细仍然是空的，判定不能后移
        Assert.False(EffectEngineInvoker.DeclaresDetailGenerator(Actions(
            new { seq = 1, eventCode = "APPROVE_EFFECT", effectKey = "stocktake-scope-generate", enabled = true })));
        // 停用的动作不算
        Assert.False(EffectEngineInvoker.DeclaresDetailGenerator(Actions(
            new { seq = 1, eventCode = "SAVE", effectKey = "stocktake-scope-generate", enabled = false })));
        // 不是生成者不算
        Assert.False(EffectEngineInvoker.DeclaresDetailGenerator(Actions(
            new { seq = 1, eventCode = "SAVE", effectKey = "stamp-last-activity", enabled = true })));
        Assert.False(EffectEngineInvoker.DeclaresDetailGenerator(null));
    }

    private static JsonElement Actions(params object[] actions) => JsonSerializer.SerializeToElement(actions);

    // ---------- 夹具 ----------

    private sealed record DetailRow(string Product, string Location, string Batch, double AccountQty, double CheckQty);

    private sealed record Outcome(int Generated, IReadOnlyList<DetailRow> Rows);

    private static async Task<Outcome> RunAsync(
        string? scope,
        string seedStock,
        string documentNo,
        bool reparentBinToZoneA = false,
        bool seedManualDetail = false)
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, seedStock);
            if (reparentBinToZoneA)
            {
                // 改挂上级后物化路径必须跟着变（路径重算的唯一收口点）
                await ExecuteAsync(connection, transaction,
                    "UPDATE dbo.DEPOT_LOCATION SET PARENT_NO=N'A-R1' WHERE DEPOT_ID=@d AND LOCATION_NO=@bin",
                    ("@d", Depot), ("@bin", BinB1));
                await new LocationPathRecalcHandler().ExecuteAsync(
                    new ServiceEffectContext(connection, transaction, LocationPlan(), PathActionPlan(),
                        EffectEvent.Save, Depot + "," + BinB1, [Depot, BinB1], "adr27-test"),
                    CancellationToken.None);
            }

            // 范围为空用 NULLIF 归一：空串与未填在单头是同一件事
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.INV_CHECK_STOCK_M (CHECK_STOCK_TYPE, CHECK_STOCK_NO, DEPOT_ID, LOCATION_ROOT_NO, CHECK_DATE, CREATE_PERSON, CI) "
                + "VALUES (N'PD', @no, @depot, NULLIF(@scope, N''), SYSDATETIME(), N'adr27', N'')",
                ("@no", documentNo), ("@depot", Depot), ("@scope", scope ?? string.Empty));

            if (seedManualDetail)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO dbo.INV_CHECK_STOCK_D (CHECK_STOCK_TYPE, CHECK_STOCK_NO, SERIAL_NO, PRO_NO, DEPOT_ID, ACCOUNT_QTY, CHECK_QTY) "
                    + "VALUES (N'PD', @no, 1, @pro, @depot, 1, 99)",
                    ("@no", documentNo), ("@pro", ProductA), ("@depot", Depot));
            }

            var generated = await new StocktakeScopeGenerateHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, ScopePlan(), ActionPlan(),
                    EffectEvent.Save, "PD," + documentNo, ["PD", documentNo], "adr27-test"),
                CancellationToken.None);

            var rows = new List<DetailRow>();
            await using (var command = new SqlCommand(
                "SELECT PRO_NO, LOCATION_NO, BATCH_NO, ACCOUNT_QTY, CHECK_QTY FROM dbo.INV_CHECK_STOCK_D "
                + "WHERE CHECK_STOCK_TYPE=N'PD' AND CHECK_STOCK_NO=@no ORDER BY SERIAL_NO", connection, transaction))
            {
                command.Parameters.AddWithValue("@no", documentNo);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rows.Add(new DetailRow(
                        ReadText(reader, 0), ReadText(reader, 1), ReadText(reader, 2),
                        reader.GetDouble(3), reader.GetDouble(4)));
                }
            }
            return new Outcome(generated, rows);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static string ReadText(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal).Trim();

    private const string StandardStock = "standard";
    private const string StockWithZeroRow = "with-zero";

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, string stock)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID IN (@d, @o);
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID IN (@d, @o);
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID IN (@d, @o);
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@d, N'ADR27 盘点测试仓'), (@o, N'ADR27 另一仓');

            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, SEQ_NO, STATUS)
                VALUES (@d, N'-',       NULL,    N'/-',              N'BIN',  0, N'A'),
                       (@d, N'A',       NULL,    N'/A',              N'ZONE', 1, N'A'),
                       (@d, N'B',       NULL,    N'/B',              N'ZONE', 2, N'A'),
                       (@d, N'C',       NULL,    N'/C',              N'ZONE', 3, N'A'),
                       (@d, N'A-R1',    N'A',    N'/A/A-R1',         N'RACK', 1, N'A'),
                       (@d, N'A-R1-B1', N'A-R1', N'/A/A-R1/A-R1-B1', N'BIN',  1, N'A'),
                       (@d, N'A-R1-B2', N'A-R1', N'/A/A-R1/A-R1-B2', N'BIN',  2, N'A'),
                       (@d, N'B-R1',    N'B',    N'/B/B-R1',         N'RACK', 1, N'A'),
                       (@d, N'B-R1-B1', N'B-R1', N'/B/B-R1/B-R1-B1', N'BIN',  1, N'A'),
                       (@o, N'-',       NULL,    N'/-',              N'BIN',  0, N'A'),
                       (@o, N'A',       NULL,    N'/A',              N'ZONE', 1, N'A');
            """, ("@d", Depot), ("@o", OtherDepot));

        // 库别级三字段（INIT_QTY / COST_PRICE / COST_AMOUNT）同键必须一致：
        // 同一 (料号, 库别) 的每一行都写同一个值，否则违反库别级字段的同键一致性口径。
        await ExecuteAsync(connection, transaction, """
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, QTY, INIT_QTY, USEABLE_QTY, COST_PRICE, COST_AMOUNT, LOCATION_NO, BATCH_NO, CREATE_PERSON, CREATE_DATE, CI)
                VALUES (@p1, @d, 5, 20, 5, 3, 60, N'A-R1-B1', N'',      N'adr27', SYSDATETIME(), N''),
                       (@p1, @d, 7, 20, 7, 3, 60, N'A-R1-B1', N'LOT-A', N'adr27', SYSDATETIME(), N''),
                       (@p2, @d, 4, 12, 4, 2, 24, N'A-R1-B2', N'LOT-A', N'adr27', SYSDATETIME(), N''),
                       (@p2, @d, 9, 12, 9, 2, 24, N'B-R1-B1', N'LOT-B', N'adr27', SYSDATETIME(), N''),
                       (@p2, @d, 3, 12, 3, 2, 24, N'-',       N'',      N'adr27', SYSDATETIME(), N''),
                       (@p3, @o, 6, 6,  6, 1, 6,  N'A',       N'',      N'adr27', SYSDATETIME(), N'');
            """, ("@p1", ProductA), ("@p2", ProductB), ("@p3", ProductOther),
                 ("@d", Depot), ("@o", OtherDepot));

        if (stock == StockWithZeroRow)
        {
            // 无货的空壳行（降档归并会留下这种行）
            await ExecuteAsync(connection, transaction,
                "UPDATE dbo.INV_PRO_DEPOT SET QTY=0 WHERE DEPOT_ID=@d AND PRO_NO=@p AND LOCATION_NO=@bin",
                ("@d", Depot), ("@p", ProductB), ("@bin", BinA2));
        }
    }

    private static ModuleEffectPlan ScopePlan() => new(
        130101, "INV_CHECK_STOCK_M", "INV_CHECK_STOCK_D", "adr27",
        ["CHECK_STOCK_TYPE", "CHECK_STOCK_NO"], [ActionPlan()], []);

    private static ModuleEffectPlan LocationPlan() => new(
        110309, "DEPOT_LOCATION", null, "adr27", ["DEPOT_ID", "LOCATION_NO"], [PathActionPlan()], []);

    private static EffectActionPlan PathActionPlan() => new(
        1, "SAVE", "location-path-recalc", "库位路径重算", true, "BLOCK", null,
        JsonSerializer.SerializeToElement(new
        {
            table = "DEPOT_LOCATION", depotField = "DEPOT_ID", locationField = "LOCATION_NO",
            parentField = "PARENT_NO", pathField = "LOCATION_PATH",
        }), null, Array.Empty<EffectOpPlan>());

    private static EffectActionPlan ActionPlan() => new(
        1, "SAVE", "stocktake-scope-generate", "按盘点范围生成明细", true, "BLOCK", null,
        ScopeParams(), null, Array.Empty<EffectOpPlan>());

    private static JsonElement ScopeParams() => JsonSerializer.SerializeToElement(new
    {
        typeField = "CHECK_STOCK_TYPE", noField = "CHECK_STOCK_NO", scopeField = "LOCATION_ROOT_NO",
        masterDepotField = "DEPOT_ID",
        locationTable = "DEPOT_LOCATION", locationDepotField = "DEPOT_ID", locationField = "LOCATION_NO",
        pathField = "LOCATION_PATH",
        stockTable = "INV_PRO_DEPOT", stockDepotField = "DEPOT_ID", stockProductField = "PRO_NO",
        stockLocationField = "LOCATION_NO", stockBatchField = "BATCH_NO", stockQtyField = "QTY",
        detailProductField = "PRO_NO", detailDepotField = "DEPOT_ID", detailAccountField = "ACCOUNT_QTY",
        detailCheckField = "CHECK_QTY", detailLocationField = "LOCATION_NO", detailBatchField = "BATCH_NO",
        serialField = "SERIAL_NO", sentinelLocationNo = "-",
    });

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
