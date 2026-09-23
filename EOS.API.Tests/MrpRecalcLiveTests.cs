using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 产品可用量 / MRP 重算（原 `P_UPDATE_PRO_MRP_ALL`）移植为受控 SQL 批的真库验证：
/// ① 夹具覆盖五类占用（订单未送、制令未入、制令未领、请购未采购、采购未收货）与 MRP 库别过滤，
///    并把占用列**预置成垃圾值**，以证明重算会先清零再回填；
/// ② 用 SAVEPOINT 在同一批数据上先跑**原过程本体**、回滚、再跑**移植实现**，比较 `PRODUCT`
///    全表八字段校验和与夹具明细**逐位一致**（校验和同时覆盖"清零全部产品占用列"的全局语义）；
/// ③ 逐产品比对可独立推导的期望值，避免"两边都错得一样"。
/// 整段在事务内进行，结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class MrpRecalcLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const string FixtureSelect =
        "SELECT PRO_NO, QTY, SAFETY_QTY, NOT_SEND_QTY, NOT_IN_QTY, NOT_GET_QTY, IN_BUY_QTY, MRP_QTY FROM dbo.PRODUCT WHERE PRO_NO LIKE N'ADRMRP%' ORDER BY PRO_NO";

    /// <summary>原 `P_UPDATE_PRO_MRP_ALL` 过程本体（逐字保留，作为移植的对照基准；仅测试使用）。</summary>
    private const string LegacySql = """
        create table #tmp (PRO_NO nchar(30), MRP_QTY float)
        begin transaction
        update PRODUCT set NOT_SEND_QTY=0, NOT_IN_QTY=0, NOT_GET_QTY=0, IN_BUY_QTY=0, MRP_QTY=0
        update PRODUCT set QTY=ROUND(d.QTY,2)
            from (select PRO_NO, sum(QTY) QTY from INV_PRO_DEPOT with(tablockx) where DEPOT_ID in(select DEPOT_ID from DEPOT where MRP=1) group by PRO_NO) d
            where PRODUCT.PRO_NO=d.PRO_NO
        update PRODUCT set NOT_SEND_QTY=ROUND(t.QTY,2)
            from (select PRO_NO, sum(d.QTY+d.SPARE_QTY-d.FINISHED_SEND_QTY-d.FINISHED_SPARE_QTY) QTY from COP_ORDER_M m with(tablockx), COP_ORDER_D d with(tablockx) where m.ORDER_TYPE=d.ORDER_TYPE and m.ORDER_NO=d.ORDER_NO and m.CONFIRM_TAG=1 and d.FINISHED_TAG=0 and d.QTY+d.SPARE_QTY>d.FINISHED_SEND_QTY+d.FINISHED_SPARE_QTY group by PRO_NO) t
            where PRODUCT.PRO_NO=t.PRO_NO
        update PRODUCT set NOT_IN_QTY=ROUND(t.QTY,2)
            from (select PRO_NO, sum(QTY+SPARE_QTY-FINISHED_QTY-FINISHED_SPARE_QTY) QTY from MOC_PRODUCE_M with(tablockx) where CONFIRM_TAG=1 and FINISHED_TAG=0 and QTY+SPARE_QTY>FINISHED_QTY+FINISHED_SPARE_QTY group by PRO_NO) t
            where PRODUCT.PRO_NO=t.PRO_NO
        update PRODUCT set NOT_GET_QTY=ROUND(t.QTY,2)
            from (select d.PRO_NO, sum(d.NEED_QTY-d.USED_QTY) QTY from MOC_PRODUCE_M m with(tablockx), MOC_PRODUCE_D d with(tablockx) where m.PRODUCE_TYPE=d.PRODUCE_TYPE and m.PRODUCE_NO=d.PRODUCE_NO and m.CONFIRM_TAG=1 and d.FINISHED_TAG=0 and d.NEED_QTY>d.USED_QTY group by d.PRO_NO) t
            where PRODUCT.PRO_NO=t.PRO_NO
        update PRODUCT set IN_BUY_QTY=ROUND(t.QTY,2)
            from (select d.PRO_NO, sum(d.QTY-d.PURCHASE_QTY) QTY from PUR_APPLY_M m with(tablockx), PUR_APPLY_D d with(tablockx) where m.APPLY_TYPE=d.APPLY_TYPE and m.APPLY_NO=d.APPLY_NO and m.CONFIRM_TAG=1 and d.FINISHED_TAG=0 and d.QTY>d.PURCHASE_QTY group by d.PRO_NO) t
            where PRODUCT.PRO_NO=t.PRO_NO
        update PRODUCT set IN_BUY_QTY=IN_BUY_QTY+ROUND(t.QTY,2)
            from (select d.PRO_NO, sum(d.QTY+d.SPARE_QTY-d.RECEIVE_QTY-d.RECEIVE_SPARE_QTY) QTY from PUR_PURCHASE_M m with(tablockx), PUR_PURCHASE_D d with(tablockx) where m.PURCHASE_TYPE=d.PURCHASE_TYPE and m.PURCHASE_NO=d.PURCHASE_NO and m.CONFIRM_TAG=1 and d.FINISHED_TAG=0 and d.QTY+d.SPARE_QTY>d.RECEIVE_QTY+d.RECEIVE_SPARE_QTY group by d.PRO_NO) t
            where PRODUCT.PRO_NO=t.PRO_NO
        update PRODUCT set MRP_QTY=ROUND(QTY-SAFETY_QTY-NOT_SEND_QTY-NOT_GET_QTY+NOT_IN_QTY+IN_BUY_QTY,2)
        commit transaction
        drop table #tmp
        """;

    [Fact]
    public async Task MRP重算_移植实现与原过程本体的结果逐位一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);
            var seeded = await SnapshotAsync(connection, transaction, token);
            Assert.Equal(SeededRows(), seeded.Fixture);

            // 原过程本体（逐字内联；其内部 BEGIN/COMMIT 在外层事务内只做嵌套计数，不真正提交）
            await using (var savepoint = new SqlCommand("SAVE TRANSACTION beforeLegacy;", connection, transaction))
            {
                await savepoint.ExecuteNonQueryAsync(token);
            }
            await using (var legacy = new SqlCommand(LegacySql, connection, transaction))
            {
                await legacy.ExecuteNonQueryAsync(token);
            }
            var legacySnapshot = await SnapshotAsync(connection, transaction, token);
            await using (var rollback = new SqlCommand("ROLLBACK TRANSACTION beforeLegacy;", connection, transaction))
            {
                await rollback.ExecuteNonQueryAsync(token);
            }
            // 回滚后必须回到播种值（夹具明细与全表校验和都复原，确认两次运行互不污染）
            var rolled = await SnapshotAsync(connection, transaction, token);
            Assert.Equal(SeededRows(), rolled.Fixture);
            Assert.Equal(seeded.Checksum, rolled.Checksum);

            // 移植实现（生产代码路径）
            await MrpRecalcService.RecalcAsync(connection, transaction, token);
            var portedSnapshot = await SnapshotAsync(connection, transaction, token);

            Assert.Equal(legacySnapshot.Checksum, portedSnapshot.Checksum);
            Assert.Equal(legacySnapshot.Fixture, portedSnapshot.Fixture);
            Assert.Equal(ExpectedRows(), portedSnapshot.Fixture);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public void 移植语句与原过程本体的关键判据逐条对齐()
    {
        var sql = MrpRecalcService.ResetSql + "\n" + MrpRecalcService.RecalcSql;
        Assert.Contains("SET NOT_SEND_QTY=0, NOT_IN_QTY=0, NOT_GET_QTY=0, IN_BUY_QTY=0, MRP_QTY=0", sql);
        Assert.Contains("m.CONFIRM_TAG=1 AND d.FINISHED_TAG=0", sql);
        Assert.Contains("d.QTY+d.SPARE_QTY>d.FINISHED_SEND_QTY+d.FINISHED_SPARE_QTY", sql);
        Assert.Contains("QTY+SPARE_QTY>FINISHED_QTY+FINISHED_SPARE_QTY", sql);
        Assert.Contains("d.NEED_QTY>d.USED_QTY", sql);
        Assert.Contains("d.QTY>d.PURCHASE_QTY", sql);
        Assert.Contains("d.QTY+d.SPARE_QTY>d.RECEIVE_QTY+d.RECEIVE_SPARE_QTY", sql);
        Assert.Contains("SET MRP_QTY=ROUND(QTY-SAFETY_QTY-NOT_SEND_QTY-NOT_GET_QTY+NOT_IN_QTY+IN_BUY_QTY,2)", sql);
        // 有意差异：不再自开事务（统一走调用方事务，失败整链回滚）
        Assert.DoesNotContain("BEGIN TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COMMIT TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
        // 读取收口：余额表不再出现在重算的语句里，库存量只能经 InventoryQueryService 取
        // （服务侧的判据，含 TABLOCKX，由 InventoryQueryServiceTests 钉住）。
        Assert.DoesNotContain("INV_PRO_DEPOT", sql);
    }

    /// <summary>全表八字段校验和（覆盖"清零全部产品占用列"的全局语义）+ 夹具产品明细。</summary>
    private static async Task<(string Checksum, string Fixture)> SnapshotAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string checksumValue;
        await using (var checksum = new SqlCommand(
            """
            SELECT CONVERT(nvarchar(40), CHECKSUM_AGG(BINARY_CHECKSUM(PRO_NO, QTY, SAFETY_QTY, NOT_SEND_QTY, NOT_IN_QTY, NOT_GET_QTY, IN_BUY_QTY, MRP_QTY)))
            FROM dbo.PRODUCT;
            """, connection, transaction))
        {
            checksumValue = Convert.ToString(await checksum.ExecuteScalarAsync(token)) ?? "null";
        }
        var parts = new List<string>();
        await using (var fixture = new SqlCommand(FixtureSelect, connection, transaction))
        {
            await using var reader = await fixture.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                for (var index = 0; index < reader.FieldCount; index++)
                    parts.Add(reader.IsDBNull(index) ? "-" : Convert.ToString(reader.GetValue(index))!.Trim());
            }
        }
        return (checksumValue, string.Join('|', parts));
    }

    private static string SeededRows() => string.Join('|', SeededFixture.SelectMany(row => row));
    private static string ExpectedRows() => string.Join('|', ExpectedFixture.SelectMany(row => row));

    /// <summary>播种值：占用列刻意填垃圾值（999），重算必须覆盖。</summary>
    private static readonly string[][] SeededFixture =
    [
        ["ADRMRPA", "0", "0", "999", "999", "999", "999", "999"],
        ["ADRMRPB", "0", "0", "999", "999", "999", "999", "999"],
        ["ADRMRPC", "0", "0", "999", "999", "999", "999", "999"],
        ["ADRMRPD", "0", "0", "999", "999", "999", "999", "999"],
        ["ADRMRPE", "0", "0", "999", "999", "999", "999", "999"],
        ["ADRMRPF", "0", "7", "999", "999", "999", "999", "999"],
    ];

    /// <summary>期望值：逐产品由判据独立推导（库存只取 MRP 库别；占用按各自公式）。</summary>
    private static readonly string[][] ExpectedFixture =
    [
        ["ADRMRPA", "100", "0", "0", "0", "0", "0", "100"],
        ["ADRMRPB", "0", "0", "23", "0", "0", "0", "-23"],
        ["ADRMRPC", "0", "0", "0", "33", "0", "0", "33"],
        ["ADRMRPD", "0", "0", "0", "0", "15", "0", "-15"],
        ["ADRMRPE", "0", "0", "0", "0", "0", "28", "28"],
        ["ADRMRPF", "0", "7", "0", "0", "0", "0", "-7"],
    ];

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var seed = new SqlCommand("""
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME, MRP) VALUES (N'ADRMRP1', N'MRP库别', 1);
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME, MRP) VALUES (N'ADRMRP0', N'非MRP库别', 0);
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, STORAGE_TYPE, SEQ_NO, STATUS)
                VALUES (N'ADRMRP1', N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', NULL, 0, N'A'),
                       (N'ADRMRP0', N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', NULL, 0, N'A');
            INSERT INTO dbo.PRODUCT (PRO_NO, QTY, SAFETY_QTY, NOT_SEND_QTY, NOT_IN_QTY, NOT_GET_QTY, IN_BUY_QTY, MRP_QTY)
                VALUES (N'ADRMRPA', 0, 0, 999, 999, 999, 999, 999),
                       (N'ADRMRPB', 0, 0, 999, 999, 999, 999, 999),
                       (N'ADRMRPC', 0, 0, 999, 999, 999, 999, 999),
                       (N'ADRMRPD', 0, 0, 999, 999, 999, 999, 999),
                       (N'ADRMRPE', 0, 0, 999, 999, 999, 999, 999),
                       (N'ADRMRPF', 0, 7, 999, 999, 999, 999, 999);
            -- 库存：MRP 库别 100 + 非 MRP 库别 50 ⇒ QTY 只应取 100
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, QTY) VALUES (N'ADRMRPA', N'ADRMRP1', 100);
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, QTY) VALUES (N'ADRMRPA', N'ADRMRP0', 50);
            -- 订单未送：30+5-10-2 = 23
            INSERT INTO dbo.COP_ORDER_M (ORDER_TYPE, ORDER_NO, CONFIRM_TAG) VALUES (N'ADRMRP', N'ADRMRPORD', 1);
            INSERT INTO dbo.COP_ORDER_D (ORDER_TYPE, ORDER_NO, SERIAL_NO, PRO_NO, QTY, SPARE_QTY, FINISHED_SEND_QTY, FINISHED_SPARE_QTY, FINISHED_TAG)
                VALUES (N'ADRMRP', N'ADRMRPORD', 1, N'ADRMRPB', 30, 5, 10, 2, 0);
            -- 制令未入 40+4-10-1 = 33（料号 C）；制令未领 20-5 = 15（料号 D）
            INSERT INTO dbo.MOC_PRODUCE_M (PRODUCE_TYPE, PRODUCE_NO, PRO_NO, QTY, SPARE_QTY, FINISHED_QTY, FINISHED_SPARE_QTY, CONFIRM_TAG, FINISHED_TAG)
                VALUES (N'ADRMRP', N'ADRMRPPRO', N'ADRMRPC', 40, 4, 10, 1, 1, 0);
            INSERT INTO dbo.MOC_PRODUCE_D (PRODUCE_TYPE, PRODUCE_NO, SERIAL_NO, PRO_NO, NEED_QTY, USED_QTY, FINISHED_TAG)
                VALUES (N'ADRMRP', N'ADRMRPPRO', 1, N'ADRMRPD', 20, 5, 0);
            -- 请购未采购 25-5 = 20；采购未收货 10+2-3-1 = 8 ⇒ IN_BUY 合计 28
            INSERT INTO dbo.PUR_APPLY_M (APPLY_TYPE, APPLY_NO, CONFIRM_TAG) VALUES (N'ADRMRP', N'ADRMRPAPL', 1);
            INSERT INTO dbo.PUR_APPLY_D (APPLY_TYPE, APPLY_NO, SERIAL_NO, PRO_NO, QTY, PURCHASE_QTY, FINISHED_TAG)
                VALUES (N'ADRMRP', N'ADRMRPAPL', 1, N'ADRMRPE', 25, 5, 0);
            INSERT INTO dbo.PUR_PURCHASE_M (PURCHASE_TYPE, PURCHASE_NO, CONFIRM_TAG) VALUES (N'ADRMRP', N'ADRMRPPUR', 1);
            INSERT INTO dbo.PUR_PURCHASE_D (PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, PRO_NO, QTY, SPARE_QTY, RECEIVE_QTY, RECEIVE_SPARE_QTY, FINISHED_TAG)
                VALUES (N'ADRMRP', N'ADRMRPPUR', 1, N'ADRMRPE', 10, 2, 3, 1, 0);
            """, connection, transaction);
        await seed.ExecuteNonQueryAsync(token);
    }
}
