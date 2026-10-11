using System.Globalization;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存账实自证：全库只读扫描，逐键断言恒等式。
///
/// 不变式原先各由一条脚本门禁守着（`check-inventory-balance-identity.ps1` /
/// `check-inventory-availability.ps1`），那两条门禁已退役（见 `scripts/README.md` 退役清单）。
/// 本文件把它们改成用例：全库扫描、逐键断言、无连接串即失败。
///
/// 覆盖：流水数量缺失 = 0、流水方向取值封闭、**报表档**（料件类别 2 半成品 / 3 原料）余额与流水累计
/// 逐键恒平、**全库档**不得出现清单外的不平键、可用量恒等式与其存列卫生（全库逐键 + 人为分叉的
/// 判别性注入）。
///
/// **开发库夹具（`E2E%` / `ZZ%`）一律排除在扫描之外**：它们是跑批与真库用例自造的键，
/// 不属于账实问题，残留本身由 `scripts/clean-e2e-fixture-residue.ps1` 按前缀清理；
/// 不排除的话，"某个用例中途失败留下夹具"会让本文件无辜变红。
///
/// 只读为主：唯一写入是最后一条用例自己造的一行探针（`QALED` 前缀），在 finally 里删净。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class InventoryLedgerIdentityLiveTests
{
    private const double Tolerance = 0.0001;

    /// <summary>报表档料件类别：2 半成品 / 3 原料（库存报表取数口径）。</summary>
    private const string ReportTierProTypes = "'2','3'";

    /// <summary>判别性探针用的自造键前缀：刻意**不落**在夹具排除前缀里，好让全库扫描看得见它。</summary>
    private const string ProbeProduct = "QALED1";

    /// <summary>
    /// 全库档已知不平键（棘轮：**只减不增**）。口径与顺序来自退役门禁
    /// `check-inventory-balance-identity.ps1` 的冻结清单；出现清单之外的键即失败——
    /// 键定性并修正后应从本清单移除。
    /// </summary>
    private static readonly HashSet<string> KnownUnbalancedKeys = new(StringComparer.Ordinal)
    {
        "0324020.MXP|BF", "AHW5BMAT001|CP", "AHW5BPRO001|CP", "APF3BPRO001|CP", "APF3BPRO001|PJ",
        "ARH6BMAT001|CP", "ARH6BPRO001|CP", "AZK2BPRO001|CP", "BL-3030-30|PJ", "C2051PCB02-3P|BF",
        "DM-010|PJ", "DPS-0502000C|CP", "G1003-150P03A|BF", "H15-05SZ-200|BF", "H15-10SN02-040|BF",
        "H15-10SN07-050|BF", "H15-10SP01-050|BF", "H15-10SP-150-33|BF", "H15-10SP21-050H|BF",
        "H15-10SZ10-030|BF", "H15-10SZ-ASSY-02|BF", "H15-10SZ-ASSY-03|BF", "H15-10SZ-ASSY-04|BF",
        "H20-16AP01-150-ASSY|BF", "H20-16AP-150C|BF", "H20-16EN-020|BF", "H20-16EP02-015|BF",
        "H20-16GP-150C|BF", "H20-16GZ03-070C|BF", "H20-16HP-200|BF", "H20-16HP-200H|BF",
        "H25-1022P1B|BF", "H25-1623SA-Q5|BF", "HF-505|PJ", "HJP-FR1-01|BF", "HS615S|BF", "HS615S|PJ",
        "KW10-Z0P150附3号动臂|CP", "PB-22E60N|PJ", "PBM12-11M-CBA|BF", "PBM-16Z15-FS-EU3-S7-S|PJ",
        "RCA-1113-0-ACA11-05|CP", "RCA-1113-11B-23|CP", "RV09712NM-FB15A7|BF", "TS-2-2416-CT|BF",
        "TVDM04-050BB-R-10|BF", "V15T16-E1P200|BF", "V15T16-S1P400|CP", "V15T22-C1P300A05-03|BF",
        "V15T22-C1Z200-01|BF", "XDE1YPRO001|BF", "XDE1YPRO001|CP", "XDE1YPRO001|PJ", "YSS-16-2216-CRW|BF",
    };

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    [Fact]
    public async Task 流水卫生_数量缺失为零且方向取值封闭()
    {
        await using var connection = await OpenAsync();

        // 数据前提：表为空时下面几条断言会恒真，所以先断言"确实有流水可查"。
        var logRows = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.INV_DEPOT_LOG;");
        Assert.True(logRows > 0, "流水明细应当存在——表为空时该组断言恒真");

        // 对账算式 SUM(CASE WHEN IN_OUT='I' THEN QTY ELSE -QTY END) 不是 NULL 安全的：
        // QTY IS NULL 的行会被 SUM 静默忽略，于是"流水缺数量"与"账实不符"压成同一个数字。
        var nullQuantity = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.INV_DEPOT_LOG WHERE QTY IS NULL;");
        Assert.Equal(0, nullQuantity);

        var nullInReportTier = await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.INV_DEPOT_LOG l JOIN dbo.PRODUCT pr ON pr.PRO_NO = l.PRO_NO "
            + "WHERE l.QTY IS NULL AND pr.PRO_TYPE IN (" + ReportTierProTypes + ");");
        Assert.Equal(0, nullInReportTier);

        // 方向只允许 I / O：对账算式把非 I 的一律当出库，其它取值会静默改变符号。
        var directions = await RowsAsync(connection, "SELECT DISTINCT ISNULL(IN_OUT, '<null>') FROM dbo.INV_DEPOT_LOG;");
        Assert.NotEmpty(directions);
        Assert.All(directions, direction => Assert.Contains(direction, new[] { "I", "O" }));
    }

    [Fact]
    public async Task 报表档余额与流水累计逐键恒平()
    {
        await using var connection = await OpenAsync();

        // 两个集合都按 (料号, 库别) 聚合后**全外连接**：只在流水里出现的键（无余额行）与
        // 只在余额里出现的键（无流水）同样是"不平"，内连接会把它们静默丢掉。
        var rows = await RowsAsync(connection, """
            SET NOCOUNT ON;
            WITH bal AS (
                SELECT DEPOT_ID, PRO_NO, SUM(QTY) AS BAL_QTY
                  FROM dbo.INV_PRO_DEPOT GROUP BY DEPOT_ID, PRO_NO
            ), led AS (
                SELECT DEPOT_ID, PRO_NO,
                       SUM(CASE WHEN IN_OUT = 'I' THEN QTY ELSE -QTY END) AS LED_QTY
                  FROM dbo.INV_DEPOT_LOG GROUP BY DEPOT_ID, PRO_NO
            ), k AS (
                SELECT COALESCE(b.PRO_NO, l.PRO_NO) AS PRO_NO,
                       COALESCE(b.DEPOT_ID, l.DEPOT_ID) AS DEPOT_ID,
                       ISNULL(b.BAL_QTY, 0) - ISNULL(l.LED_QTY, 0) AS DIFF
                  FROM bal b FULL OUTER JOIN led l
                    ON l.PRO_NO = b.PRO_NO AND l.DEPOT_ID = b.DEPOT_ID
            )
            SELECT CONCAT(RTRIM(k.PRO_NO), '|', RTRIM(k.DEPOT_ID), '|',
                          CONVERT(NVARCHAR(40), CAST(k.DIFF AS DECIMAL(28,4))), '|',
                          CASE WHEN pr.PRO_TYPE IN ('2','3') THEN 1 ELSE 0 END)
              FROM k LEFT JOIN dbo.PRODUCT pr ON pr.PRO_NO = k.PRO_NO
             WHERE k.PRO_NO NOT LIKE N'E2E%' AND k.PRO_NO NOT LIKE N'ZZ%';
            """);
        Assert.NotEmpty(rows);

        var tierKeys = 0;
        var violations = new List<string>();
        foreach (var row in rows)
        {
            var parts = row.Split('|');
            Assert.Equal(4, parts.Length);
            if (parts[3] != "1") { continue; }

            tierKeys++;
            var diff = double.Parse(parts[2], CultureInfo.InvariantCulture);
            if (Math.Abs(diff) > Tolerance)
            {
                violations.Add($"{parts[0]}|{parts[1]} 差异 {diff}");
            }
        }

        Assert.True(tierKeys > 0, $"报表档（料件类别 {ReportTierProTypes}）应当有键——该档为空时逐键断言恒真");
        Assert.True(violations.Count == 0,
            $"报表档出现差异 {violations.Count} 个键（该档必须恒为 0）：{string.Join("、", violations.Take(10))}");
    }

    [Fact]
    public async Task 全库档不得出现冻结清单之外的不平键()
    {
        await using var connection = await OpenAsync();

        var unbalanced = await RowsAsync(connection, """
            SET NOCOUNT ON;
            WITH bal AS (
                SELECT DEPOT_ID, PRO_NO, SUM(QTY) AS BAL_QTY
                  FROM dbo.INV_PRO_DEPOT GROUP BY DEPOT_ID, PRO_NO
            ), led AS (
                SELECT DEPOT_ID, PRO_NO,
                       SUM(CASE WHEN IN_OUT = 'I' THEN QTY ELSE -QTY END) AS LED_QTY
                  FROM dbo.INV_DEPOT_LOG GROUP BY DEPOT_ID, PRO_NO
            ), k AS (
                SELECT COALESCE(b.PRO_NO, l.PRO_NO) AS PRO_NO,
                       COALESCE(b.DEPOT_ID, l.DEPOT_ID) AS DEPOT_ID,
                       ISNULL(b.BAL_QTY, 0) - ISNULL(l.LED_QTY, 0) AS DIFF
                  FROM bal b FULL OUTER JOIN led l
                    ON l.PRO_NO = b.PRO_NO AND l.DEPOT_ID = b.DEPOT_ID
            )
            SELECT CONCAT(RTRIM(k.PRO_NO), '|', RTRIM(k.DEPOT_ID))
              FROM k
             WHERE ABS(k.DIFF) > 0.0001
               AND k.PRO_NO NOT LIKE N'E2E%' AND k.PRO_NO NOT LIKE N'ZZ%';
            """);
        Assert.NotEmpty(unbalanced);

        var unexpected = unbalanced.Where(key => !KnownUnbalancedKeys.Contains(key)).ToList();
        Assert.True(unexpected.Count == 0,
            $"出现冻结清单之外的不平键 {unexpected.Count} 个（棘轮只减不增——先定性再决定是修正还是补进清单）："
            + string.Join("、", unexpected.Take(10)));
    }

    [Fact]
    public async Task 可用量恒等式_逐键与存列一致_且能抓出人为分叉()
    {
        await using var connection = await OpenAsync();

        // 数据前提：余额表为空时逐键断言恒真。
        var balanceRows = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.INV_PRO_DEPOT;");
        Assert.True(balanceRows > 0, "库存余额应当有行——为空时逐键断言恒真");

        // 可用量是**存列**：NULL 会让恒等式两侧一起变成"空"，是最容易被吞掉的一类分叉。
        var nullUsable = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.INV_PRO_DEPOT WHERE USEABLE_QTY IS NULL;");
        Assert.Equal(0, nullUsable);

        // 占用行卫生：STATUS 只允许 A（有效）/ C（已取消·已释放），数量列不得为负。
        var badOccupancy = await ScalarAsync<int>(connection, """
            SET NOCOUNT ON;
            SELECT COUNT(*) FROM (
                SELECT STATUS, FREEZE_QTY AS Q FROM dbo.INV_FREEZE
                UNION ALL
                SELECT STATUS, RESERVE_QTY FROM dbo.INV_RESERVE) o
             WHERE LTRIM(RTRIM(ISNULL(o.STATUS, N'C'))) NOT IN (N'A', N'C') OR ISNULL(o.Q, 0) < 0;
            """);
        Assert.Equal(0, badOccupancy);

        // 全库逐键：现状必须没有分叉。
        var clean = await ScanAvailabilityMismatchesAsync(connection);
        Assert.True(clean.Count == 0,
            $"可用量恒等式不成立 {clean.Count} 个键（USEABLE_QTY = QTY − Σ有效冻结 − Σ有效预留）：{string.Join("；", clean.Take(10))}");

        // 判别性（这一段是"断言能失败"的证据）：造一行"忘了同步"的现场——数量 100、可用量却停在 90，
        // 断言全库扫描**命中它**；再把可用量修正回 100，断言**不再命中**。
        // 插入时**先写平衡值**（100/100），分叉由 UPDATE 造出来：万一用例被硬杀在 finally 之前，
        // 留下的是"平衡的孤儿行"（不破坏恒等式），而不是一行永远偏着的账。
        try
        {
            await ExecAsync(connection, """
                DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
                DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
                INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_TYPE) VALUES (@pro, N'QALED 恒等式探针', '3');
                INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, USEABLE_QTY, COST_PRICE, COST_AMOUNT)
                    VALUES (@pro, N'CP', N'-', N'', 100, 100, 100, 1, 100);
                """, ("@pro", ProbeProduct));

            await ExecAsync(connection, "UPDATE dbo.INV_PRO_DEPOT SET USEABLE_QTY = 90 WHERE LTRIM(RTRIM(PRO_NO)) = @pro;",
                ("@pro", ProbeProduct));
            var injected = await ScanAvailabilityMismatchesAsync(connection);
            Assert.Contains(injected, row => row.StartsWith(ProbeProduct + "|", StringComparison.Ordinal));

            await ExecAsync(connection, "UPDATE dbo.INV_PRO_DEPOT SET USEABLE_QTY = QTY WHERE LTRIM(RTRIM(PRO_NO)) = @pro;",
                ("@pro", ProbeProduct));
            var fixedUp = await ScanAvailabilityMismatchesAsync(connection);
            Assert.DoesNotContain(fixedUp, row => row.StartsWith(ProbeProduct + "|", StringComparison.Ordinal));
        }
        finally
        {
            await ExecAsync(connection, """
                DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
                DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
                """, ("@pro", ProbeProduct));
        }
    }

    // ===== 查询 =====

    /// <summary>全库逐键扫描：返回恒等式不成立的键（四键 + 数量 / 可用量 / 占用 / 应为）。夹具前缀排除在外。</summary>
    private static Task<List<string>> ScanAvailabilityMismatchesAsync(SqlConnection connection) =>
        RowsAsync(connection, """
            SET NOCOUNT ON;
            WITH occ AS (
                SELECT LTRIM(RTRIM(PRO_NO)) P, LTRIM(RTRIM(DEPOT_ID)) D,
                       LTRIM(RTRIM(ISNULL(LOCATION_NO, N'-'))) L, LTRIM(RTRIM(ISNULL(BATCH_NO, N''))) B,
                       ISNULL(FREEZE_QTY, 0) Q
                  FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(ISNULL(STATUS, N'C'))) = N'A'
                UNION ALL
                SELECT LTRIM(RTRIM(PRO_NO)), LTRIM(RTRIM(DEPOT_ID)),
                       LTRIM(RTRIM(ISNULL(LOCATION_NO, N'-'))), LTRIM(RTRIM(ISNULL(BATCH_NO, N''))),
                       ISNULL(RESERVE_QTY, 0)
                  FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(ISNULL(STATUS, N'C'))) = N'A'
            ), agg AS (SELECT P, D, L, B, SUM(Q) OCC FROM occ GROUP BY P, D, L, B)
            SELECT LTRIM(RTRIM(d.PRO_NO)) + N'|' + LTRIM(RTRIM(d.DEPOT_ID)) + N'|'
                 + LTRIM(RTRIM(ISNULL(d.LOCATION_NO, N'-'))) + N'|' + LTRIM(RTRIM(ISNULL(d.BATCH_NO, N'')))
                 + N'|数量=' + CONVERT(nvarchar(40), ISNULL(d.QTY, 0))
                 + N'|可用量=' + CONVERT(nvarchar(40), ISNULL(d.USEABLE_QTY, 0))
                 + N'|占用=' + CONVERT(nvarchar(40), ISNULL(a.OCC, 0))
                 + N'|应为=' + CONVERT(nvarchar(40), ISNULL(d.QTY, 0) - ISNULL(a.OCC, 0))
              FROM dbo.INV_PRO_DEPOT d
              LEFT JOIN agg a ON a.P = LTRIM(RTRIM(d.PRO_NO)) AND a.D = LTRIM(RTRIM(d.DEPOT_ID))
                             AND a.L = LTRIM(RTRIM(ISNULL(d.LOCATION_NO, N'-')))
                             AND a.B = LTRIM(RTRIM(ISNULL(d.BATCH_NO, N'')))
             WHERE ABS(ISNULL(d.USEABLE_QTY, 0) - (ISNULL(d.QTY, 0) - ISNULL(a.OCC, 0))) > 0.0001
               AND d.PRO_NO NOT LIKE N'E2E%' AND d.PRO_NO NOT LIKE N'ZZ%'
            """);

    // ===== 装配 =====

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        var scalar = await command.ExecuteScalarAsync();
        Assert.NotNull(scalar);
        return (T)Convert.ChangeType(scalar!, typeof(T));
    }

    private static async Task<List<string>> RowsAsync(SqlConnection connection, string sql)
    {
        var rows = new List<string>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }
        return rows;
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
