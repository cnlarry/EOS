using System.Data;
using System.Globalization;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存日报（`P_RPT_INV_PRO_DEPOT_1`，三个报表变体）移植为受控聚合数据源的真库对拍：
/// 在同一批（隔离造数的）数据上分别执行「原过程」（SSDT 快照正文，按报表编号推导文件名）
/// 与「注册表 SQL」，逐行逐列比较。
/// **结论口径**：结构列（数量、单据、连接出来的名称列）必须逐字一致；单价列按已登记的有意差异
/// （决策 #118）比较——旧实现的两处公式缺陷使期初/发出单价失真，移植按加权平均实现，
/// 因此用例显式断言"旧 = 旧公式值、新 = 加权平均值"，把差异钉住而不是跳过。
/// 整段在事务内进行，结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class InventoryReportPortLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const string ReportId = "INV_Pro_Depot_1";
    private const string DepotId = "ADR12DPTA";
    private const string ProWithHistory = "ADR12INV1";
    private const string ProSingleReceipt = "ADR12INV2";
    private const string SortId = "Z9";

    /// <summary>单价列：按已登记的有意差异比较，不参与"逐字一致"断言。</summary>
    private static readonly string[] PriceColumns = ["PRICE_Q", "PRICE_J", "PRICE_X"];

    private static readonly Dictionary<string, string> LegacyBodies = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task 库存日报_移植实现与原过程结构一致且单价差异符合登记口径()
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

            string[] parameters = [DepotId, DepotId, "", "", "ADR12INV1", "ADR12INV9", "2024-01-01", "2024-01-31"];
            var legacy = await RunLegacyAsync(connection, transaction, token, parameters);
            var ported = await RunPortedAsync(connection, transaction, aggregate!, token, parameters);

            // 行集合一致（同一对 (仓库, 料号) 的期初行 + 本期行）
            Assert.Equal(
                legacy.Select(RowKey).OrderBy(item => item, StringComparer.Ordinal),
                ported.Select(RowKey).OrderBy(item => item, StringComparer.Ordinal));

            var legacyRows = legacy.ToDictionary(RowKey, row => row, StringComparer.Ordinal);
            var portedRows = ported.ToDictionary(RowKey, row => row, StringComparer.Ordinal);
            foreach (var (key, legacyRow) in legacyRows)
            {
                var portedRow = portedRows[key];
                foreach (var column in legacyRow.Keys)
                {
                    if (PriceColumns.Contains(column, StringComparer.OrdinalIgnoreCase)) continue;
                    Assert.True(legacyRow[column] == portedRow[column],
                        $"行 {key} 列 {column}：旧=[{legacyRow[column]}] 新=[{portedRow[column]}]");
                }
            }

            // 期初（APP_DATE=1900-01-01）：旧公式 vs 加权平均
            var legacyOpen1 = legacyRows[RowKey(ProWithHistory, "1900-01-01")];
            var portedOpen1 = portedRows[RowKey(ProWithHistory, "1900-01-01")];
            Assert.Equal("16", legacyOpen1["QTY_Q"]);
            Assert.Equal("16", portedOpen1["QTY_Q"]);
            // 旧：10@5 → 2.5（分母 Q_old+2q=20），再 10@9 → (2.5*20+90)/30，再 -4 不改均价
            Assert.Equal(4.6667, ToNumber(legacyOpen1["PRICE_Q"]), 4);
            // 新：加权平均 (10*5 + 10*9 + (-4)*0) / 16
            Assert.Equal(8.75, ToNumber(portedOpen1["PRICE_Q"]), 4);

            var legacyOpen2 = legacyRows[RowKey(ProSingleReceipt, "1900-01-01")];
            var portedOpen2 = portedRows[RowKey(ProSingleReceipt, "1900-01-01")];
            // 旧：单笔 5@3 → (0*5+15)/(5+5)=1.5；新：加权平均 3
            Assert.Equal(1.5, ToNumber(legacyOpen2["PRICE_Q"]), 4);
            Assert.Equal(3.0, ToNumber(portedOpen2["PRICE_Q"]), 4);

            // 本期发出成本：旧实现两处问题叠加——游标首行（排序第一对的期初行）被首次取值消费掉、
            // 未进入累计，且把当日发出再从累计里扣一次 ⇒ 首对的价格只反映本期收入（42/6=7）；
            // 新实现按当日累计加权平均：(16*8.75 + 42) / 22 = 8.272727
            var legacyIssue = legacyRows[RowKey(ProWithHistory, "2024-01-15")];
            var portedIssue = portedRows[RowKey(ProWithHistory, "2024-01-15")];
            Assert.Equal("5", legacyIssue["QTY_X"]);
            Assert.Equal("5", portedIssue["QTY_X"]);
            Assert.Equal(7.0, ToNumber(legacyIssue["PRICE_X"]), 4);
            Assert.Equal(8.272727, ToNumber(portedIssue["PRICE_X"]), 4);

            // 本期收入行的收入单价 = 流水单价（两侧一致，属结构口径）
            Assert.Equal("7", legacyRows[RowKey(ProWithHistory, "2024-01-10")]["PRICE_J"]);
            Assert.Equal("7", portedRows[RowKey(ProWithHistory, "2024-01-10")]["PRICE_J"]);

            // 区间外的料号不得出现（范围条件生效）
            Assert.DoesNotContain(ported, row => (row["PRO_NO"] ?? "").Contains("OUTSIDE", StringComparison.Ordinal));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 库存日报_成本计法取最近进价时两侧口径一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);
            var aggregate = ReportAggregateRegistry.Find(ReportId)!;

            // 条件 6 = 成本计法（F_TYPE 2 固定单选）：1 表示"最近进价 × 汇率"
            string[] parameters = [DepotId, DepotId, "", "", "ADR12INV1", "ADR12INV9", "2024-01-01", "2024-01-31"];
            var legacy = await RunLegacyAsync(connection, transaction, token, parameters, cb1: 1);
            var ported = await RunPortedAsync(connection, transaction, aggregate, token, parameters, cb1: 1);

            // 造数：LAST_PURCHASE_PRICE=12，CURR_ID='ADR12CUR'（CURR_RATE=2）⇒ 两侧都应为 24
            foreach (var row in legacy.Concat(ported))
            {
                Assert.Equal(24.0, ToNumber(row["PRICE_Q"]), 4);
                Assert.Equal(24.0, ToNumber(row["PRICE_X"]), 4);
            }
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static string RowKey(Dictionary<string, string?> row)
        => $"{row.GetValueOrDefault("PRO_NO")}|{row.GetValueOrDefault("APP_DATE")}";

    /// <summary>按 (料号, 日期) 定位行——日期键与 <see cref="Format"/> 的 datetime 形态一致。</summary>
    private static string RowKey(string proNo, string date) => $"{proNo}|{date} 00:00:00";

    private static double ToNumber(string? value)
        => double.Parse(value ?? "0", CultureInfo.InvariantCulture);

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, """
            IF NOT EXISTS(SELECT 1 FROM dbo.CURR WHERE CURR_ID='ADR12CUR')
                INSERT INTO dbo.CURR (CURR_ID, CURR_NAME, CURR_RATE) VALUES ('ADR12CUR', N'ADR12 币别', 2);
            IF NOT EXISTS(SELECT 1 FROM dbo.SORT WHERE SORT_ID=@SortId)
                INSERT INTO dbo.SORT (SORT_ID, SORT_NAME) VALUES (@SortId, N'ADR12 类别');
            DELETE FROM dbo.INV_DEPOT_LOG WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@Pro1, @Pro2, 'ADR12INV9');
            IF NOT EXISTS(SELECT 1 FROM dbo.DEPOT WHERE DEPOT_ID=@Depot)
                INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR12 仓库');
            IF NOT EXISTS(SELECT 1 FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@Depot AND LOCATION_NO=N'-')
                INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, STORAGE_TYPE, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', NULL, 0, N'A');
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE, SORT_ID, LAST_PURCHASE_PRICE, LAST_PURCHASE_CURR_ID)
            VALUES (@Pro1, N'ADR12 有历史料件', N'规格A', '3', @SortId, 12, 'ADR12CUR'),
                   (@Pro2, N'ADR12 单笔料件', N'规格B', '3', @SortId, 12, 'ADR12CUR'),
                   ('ADR12INV9', N'ADR12 区间外料件', N'规格C', '3', @SortId, 12, 'ADR12CUR');
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, QTY) VALUES (@Pro1, @Depot, 16), (@Pro2, @Depot, 5);
            INSERT INTO dbo.INV_DEPOT_LOG (PRO_NO, MUTUALITY_DATE, MUTUALITY_TYPE, MUTUALITY_NO, MUTUALITY_SERIAL_NO, IN_OUT, QTY, PRICE, DEPOT_ID)
            VALUES (@Pro1, '2023-01-10', 'ADR12T', 'ADR12001', 1, 'I', 10, 5, @Depot),
                   (@Pro1, '2023-06-10', 'ADR12T', 'ADR12002', 1, 'I', 10, 9, @Depot),
                   (@Pro1, '2023-07-05', 'ADR12T', 'ADR12003', 1, 'O', 4, 0, @Depot),
                   (@Pro1, '2024-01-10', 'ADR12T', 'ADR12004', 1, 'I', 6, 7, @Depot),
                   (@Pro1, '2024-01-15', 'ADR12T', 'ADR12005', 1, 'O', 5, 0, @Depot),
                   (@Pro2, '2023-03-03', 'ADR12T', 'ADR12006', 1, 'I', 5, 3, @Depot);
            """, token,
            ("@SortId", SortId), ("@Depot", DepotId), ("@Pro1", ProWithHistory), ("@Pro2", ProSingleReceipt));
    }

    /// <summary>
    /// 跑「原过程」侧：正文取自测试夹具（`Fixtures/legacy-sprocs/`），
    /// 并以 `sp_executesql` 执行（动态批内的 `#temp` 随批结束释放＝过程作用域）。
    /// </summary>
    private static async Task<List<Dictionary<string, string?>>> RunLegacyAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        IReadOnlyList<string> parameters, int cb1 = 0)
    {
        const string declared = "@depot_id1 nchar(10), @depot_id2 nchar(10), @sort_id1 nchar(10), @sort_id2 nchar(10), "
            + "@pro_no1 nvarchar(30), @pro_no2 nvarchar(30), @date1 datetime, @date2 datetime, @jc1 int, @cb1 int";
        var batch = $"""
            DECLARE @depot_id1 nchar(10)=@p_depot1, @depot_id2 nchar(10)=@p_depot2,
                    @sort_id1 nchar(10)=@p_sort1, @sort_id2 nchar(10)=@p_sort2,
                    @pro_no1 nvarchar(30)=@p_pro1, @pro_no2 nvarchar(30)=@p_pro2,
                    @date1 datetime=@p_date1, @date2 datetime=@p_date2, @jc1 int=0, @cb1 int=@p_cb1;
            EXEC sp_executesql @body, N'{declared}', @depot_id1, @depot_id2, @sort_id1, @sort_id2,
                 @pro_no1, @pro_no2, @date1, @date2, @jc1, @cb1;
            """;
        await using var command = new SqlCommand(batch, connection, transaction);
        command.Parameters.Add("@body", SqlDbType.NVarChar, -1).Value = LoadLegacyBody();
        command.Parameters.AddWithValue("@p_depot1", parameters[0]);
        command.Parameters.AddWithValue("@p_depot2", parameters[1]);
        command.Parameters.AddWithValue("@p_sort1", parameters[2]);
        command.Parameters.AddWithValue("@p_sort2", parameters[3]);
        command.Parameters.AddWithValue("@p_pro1", parameters[4]);
        command.Parameters.AddWithValue("@p_pro2", parameters[5]);
        command.Parameters.AddWithValue("@p_date1", parameters[6]);
        command.Parameters.AddWithValue("@p_date2", parameters[7]);
        command.Parameters.AddWithValue("@p_cb1", cb1);
        return await ReadAllAsync(command, token);
    }

    private static async Task<List<Dictionary<string, string?>>> RunPortedAsync(
        SqlConnection connection, SqlTransaction transaction, ReportAggregate aggregate, CancellationToken token,
        IReadOnlyList<string> parameters, int cb1 = 0)
    {
        var command = ReportRepository.BuildAggregateSql(aggregate, []);
        await using var sqlCommand = new SqlCommand(command, connection, transaction);
        // 参数按查询条件序号绑定：仓库/类别/料号/日期取起止值，成本计法取单值
        sqlCommand.Parameters.AddWithValue("@depot1", parameters[0]);
        sqlCommand.Parameters.AddWithValue("@depot2", parameters[1]);
        sqlCommand.Parameters.AddWithValue("@sort1", parameters[2]);
        sqlCommand.Parameters.AddWithValue("@sort2", parameters[3]);
        sqlCommand.Parameters.AddWithValue("@pro1", parameters[4]);
        sqlCommand.Parameters.AddWithValue("@pro2", parameters[5]);
        sqlCommand.Parameters.AddWithValue("@date1", parameters[6]);
        sqlCommand.Parameters.AddWithValue("@date2", parameters[7]);
        sqlCommand.Parameters.AddWithValue("@cb1", cb1);
        return await ReadAllAsync(sqlCommand, token);
    }

    private static string LoadLegacyBody()
    {
        var sproc = "P_RPT_" + ReportId.ToUpperInvariant();
        if (LegacyBodies.TryGetValue(sproc, out var cached)) return cached;
        var path = Path.Combine(RepoRoot(), "EOS.API.Tests", "Fixtures", "legacy-sprocs", $"{sproc}.sql");
        Assert.True(File.Exists(path), $"缺少旧过程基准夹具：{path}");
        var text = File.ReadAllText(path);
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"(?is)^\s*(?:--[^\n]*\n\s*)*CREATE\s+PROCEDURE\s+[^\s(]+.*?\bAS\b");
        Assert.True(match.Success, $"{sproc} 基准缺少 CREATE PROCEDURE ... AS 头");
        var body = text[match.Length..].TrimStart('\r', '\n');
        LegacyBodies[sproc] = body;
        return body;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EOS.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static async Task<List<Dictionary<string, string?>>> ReadAllAsync(SqlCommand command, CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<Dictionary<string, string?>>();
        while (await reader.ReadAsync(token))
        {
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Format(reader.GetValue(i));
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
