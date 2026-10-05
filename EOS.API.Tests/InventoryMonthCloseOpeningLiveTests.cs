using System.Data;
using System.Globalization;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存日报的**期初取数**在月结快照出现后的正确性（真库用例，整段在事务内，结束回滚）。
///
/// 期初的语义是"上一期已批核月结的快照 + 其后到区间起点的流水"，因此这段 SQL 有三件事
/// 必须同时对，且三者都**不报错、只把数字改小或改大**：
///
///   ① **按维度聚合**：快照按批次 / 库位细分后，同一 (料号, 库别) 在该期有多行明细，
///      取其中一行会静默少算 ⇒ 必须对该期明细求和；
///   ② **只取最近一期**：把它写成"所有已批核期的合计"，期初会从"上月末"变成
///      "开天辟地以来的净额"（比少算错得更多）⇒ 必须先定月、再聚合；
///   ③ **选月时必须过滤批核位**：未批核的月结单是草稿，不得参与任何口径。只在"是否存在
///      已批核期"上过滤是拦不住它的——一张草稿就能把期初改掉，而"更早有过已批核期"仍成立。
///
/// 本用例的夹具让三者同时可判别（区间取 2024-01-10 ~ 2024-01-31，区间起点即期初/本期的分界）：
///   期 A（2023-06-30 已批核）明细 7 ；
///   期 B（2023-12-31 已批核）明细 60（批号 B1）+ 40（批号 B2）；
///   期 C（2024-01-05 **草稿**）明细 999 ；
///   2024-01-08 入库 10（在 B 之后、区间起点之前 ⇒ 进期初）；
///   2024-01-20 入库 5（落在区间内 ⇒ 进本期收入）。
/// 期望期初 = (60 + 40) + 10 = **110**。
/// 三种退化写法分别得到：取单行 ⇒ 60 或 40（+10）；跨月求和 ⇒ 7 + 100 + 10 = 117；
/// 选月不过滤批核位 ⇒ 999（草稿更晚，会被选中）。三者都与 110 不等，故任一退化都会变红。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class InventoryMonthCloseOpeningLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string ReportId = "INV_Pro_Depot_1";
    private const string DepotId = "ADR20DPTA";
    private const string ProNo = "ADR20INV1";

    [Fact]
    public async Task 库存日报期初_取最近已批核月结并按该期明细聚合()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);

            var aggregate = ReportAggregateRegistry.Find(ReportId);
            Assert.NotNull(aggregate);

            var rows = await RunAsync(connection, transaction, aggregate!, token);
            var opening = rows.Single(row => row["APP_DATE"] == "1900-01-01 00:00:00");

            // 期初 = 最近已批核期（两个批次明细合计 100）+ 其后流水（10）
            Assert.Equal(110.0, ToNumber(opening["QTY_Q"]), 4);
            // 判别性：草稿期（999）不得参与；只取单行（60/40）或跨月求和（107）都不是 110
            Assert.NotEqual(999.0, ToNumber(opening["QTY_Q"]), 4);

            // 本期收入仍按流水体现（期初只吃掉起点之前的量）
            var receipt = rows.Single(row => row["APP_DATE"] == "2024-01-20 00:00:00");
            Assert.Equal(5.0, ToNumber(receipt["QTY_J"]), 4);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 库存日报期初_只有草稿月结单时与完全没有月结单等价()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);
            var aggregate = ReportAggregateRegistry.Find(ReportId)!;

            var withDraft = await RunAsync(connection, transaction, aggregate, token);

            // 删掉那张草稿月结单：期初应当**逐字不变**——草稿不参与任何口径
            await ExecuteAsync(connection, transaction, """
                DELETE FROM dbo.INV_PRO_MONTH_D WHERE MONTH_NO = @Draft;
                DELETE FROM dbo.INV_PRO_MONTH_M WHERE MONTH_NO = @Draft;
                """, token, ("@Draft", DraftNo));

            var withoutDraft = await RunAsync(connection, transaction, aggregate, token);

            static string Opening(List<Dictionary<string, string?>> rows)
                => rows.Single(row => row["APP_DATE"] == "1900-01-01 00:00:00")["QTY_Q"] ?? "";

            Assert.Equal(Opening(withoutDraft), Opening(withDraft));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private const string PeriodA = "ADR20M01";
    private const string PeriodB = "ADR20M02";
    private const string DraftNo = "ADR20M03";

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_PRO_MONTH_D WHERE MONTH_NO IN (@A, @B, @C);
            DELETE FROM dbo.INV_PRO_MONTH_M WHERE MONTH_NO IN (@A, @B, @C);
            DELETE FROM dbo.INV_DEPOT_LOG WHERE DEPOT_ID = @Depot;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID = @Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO = @Pro;
            IF NOT EXISTS(SELECT 1 FROM dbo.DEPOT WHERE DEPOT_ID = @Depot)
                INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR20 仓库');
            -- 余额行的库位列有指向库位主档的外键：未指定位置的哨兵行必须先存在
            IF NOT EXISTS(SELECT 1 FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID = @Depot AND LOCATION_NO = N'-')
                INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, STORAGE_TYPE, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', NULL, 0, N'A');
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE)
                VALUES (@Pro, N'ADR20 月结料件', N'规格A', '3');
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, QTY) VALUES (@Pro, @Depot, 110);
            INSERT INTO dbo.INV_DEPOT_LOG (PRO_NO, MUTUALITY_DATE, MUTUALITY_TYPE, MUTUALITY_NO, MUTUALITY_SERIAL_NO, IN_OUT, QTY, PRICE, DEPOT_ID)
                VALUES (@Pro, '2024-01-08', 'ADR20T', 'ADR20001', 1, 'I', 10, 5, @Depot),
                       (@Pro, '2024-01-20', 'ADR20T', 'ADR20002', 1, 'I', 5, 5, @Depot);

            -- 期 A：更早的已批核期（用于证伪"跨月求和"）
            INSERT INTO dbo.INV_PRO_MONTH_M (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CREATE_PERSON, CREATE_DATE, CI)
                VALUES ('ADR20', @A, '2023-06-30', 1, N'ADR20', GETDATE(), 'ADR20');
            INSERT INTO dbo.INV_PRO_MONTH_D (MONTH_TYPE, MONTH_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY, PRICE)
                VALUES ('ADR20', @A, 1, @Pro, @Depot, 7, 5);

            -- 期 B：最近一期已批核，两个批次明细（用于证伪"取单行"）
            INSERT INTO dbo.INV_PRO_MONTH_M (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CREATE_PERSON, CREATE_DATE, CI)
                VALUES ('ADR20', @B, '2023-12-31', 1, N'ADR20', GETDATE(), 'ADR20');
            INSERT INTO dbo.INV_PRO_MONTH_D (MONTH_TYPE, MONTH_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY, PRICE, LOCATION_NO, BATCH_NO)
                VALUES ('ADR20', @B, 1, @Pro, @Depot, 60, 5, N'-', 'ADR20B1'),
                       ('ADR20', @B, 2, @Pro, @Depot, 40, 5, N'-', 'ADR20B2');

            -- 期 C：更晚的**草稿**（未批核；用于证伪"选月不过滤批核位"）
            INSERT INTO dbo.INV_PRO_MONTH_M (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CREATE_PERSON, CREATE_DATE, CI)
                VALUES ('ADR20', @C, '2024-01-05', 0, N'ADR20', GETDATE(), 'ADR20');
            INSERT INTO dbo.INV_PRO_MONTH_D (MONTH_TYPE, MONTH_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY, PRICE)
                VALUES ('ADR20', @C, 1, @Pro, @Depot, 999, 5);
            """, token,
            ("@A", PeriodA), ("@B", PeriodB), ("@C", DraftNo), ("@Depot", DepotId), ("@Pro", ProNo));
    }

    private static async Task<List<Dictionary<string, string?>>> RunAsync(
        SqlConnection connection, SqlTransaction transaction, ReportAggregate aggregate, CancellationToken token)
    {
        var sql = ReportRepository.BuildAggregateSql(aggregate, []);
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@depot1", DepotId);
        command.Parameters.AddWithValue("@depot2", DepotId);
        command.Parameters.AddWithValue("@sort1", "");
        command.Parameters.AddWithValue("@sort2", "");
        command.Parameters.AddWithValue("@pro1", ProNo);
        command.Parameters.AddWithValue("@pro2", ProNo);
        command.Parameters.AddWithValue("@date1", "2024-01-10");
        command.Parameters.AddWithValue("@date2", "2024-01-31");
        command.Parameters.AddWithValue("@cb1", 0);

        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<Dictionary<string, string?>>();
        while (await reader.ReadAsync(token))
        {
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : Format(reader.GetValue(index));
            rows.Add(row);
        }
        return rows;
    }

    private static string? Format(object value)
        => value switch
        {
            string text => text.TrimEnd(),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            double number => number.ToString(CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };

    private static double ToNumber(string? value)
        => double.Parse(value ?? "0", CultureInfo.InvariantCulture);

    private static async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
