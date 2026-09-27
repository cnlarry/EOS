using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 位置档位（`LOCATION_MODE` 3 强制）在库存移动上的落地差异。
///
/// 档 0/1/2 与档 3 的差别只在**未指定库位**时显形——前三档都允许留空（留空即落哨兵行
/// `N'-'`，档 2 的差异特征是"系统给建议位置"，属 明确不做），档 3 的语义是
/// "必须指定"。所以拿填了库位的单据造例，四档结果完全一样，用例等于没测。
///
/// 真库用例，需 <c>MSSQL_ERP_CONN</c>；全程在一个事务内建数、断言、回滚。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class LocationModeTierLiveTests
{
    private const string Depot = "ADR14LMDP";
    private const string Plain = "ADR14LMPLN";
    private const string Unit = "ADR14LMUN";
    private const string Type = "ALM1";
    private const string Bin = "A-R1-B1";
    private const string No = "ADR14LMOUT01";

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private static DepotStockPolicyService Policies(string connectionString) =>
        PolicyServiceFactory.Create(connectionString);

    [Fact]
    public async Task 档3未指定库位被拒()
    {
        var exception = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunAsync(depotLocationMode: 3, locationNo: string.Empty));

        Assert.Contains("必须指定库位", exception.Message);
    }

    /// <summary>显式填哨兵与压根没填必须同样被拒：否则"填了哨兵"就成了绕过档 3 的口子。</summary>
    [Fact]
    public async Task 档3显式填未指定位置同样被拒()
    {
        var exception = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunAsync(depotLocationMode: 3, locationNo: "-"));

        Assert.Contains("必须指定库位", exception.Message);
    }

    [Fact]
    public async Task 档3指定了库位则放行且库存落到该库位()
    {
        var observed = await RunAsync(depotLocationMode: 3, locationNo: Bin);

        Assert.Equal(Bin, observed.BalanceLocation);
        Assert.Equal(6d, observed.BalanceQty, 3);   // 10 - 4
        Assert.Equal(0d, observed.SentinelQty, 3);  // 哨兵行未被触碰
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task 档012未指定库位仍放行并落哨兵行(int locationMode)
    {
        var observed = await RunAsync(depotLocationMode: locationMode, locationNo: string.Empty);

        Assert.Equal("-", observed.BalanceLocation);
        Assert.Equal(6d, observed.BalanceQty, 3);
    }

    /// <summary>
    /// 库别无策略行时按**部署级默认**的档位执行，而不是"没有行就算档 0"。
    /// 这是本文件的判别点：档位静默失效时四档都会退化成"全部放行"，而**只有当部署级默认
    /// 不是 0 时**，"库别无行"与"档 0"才会给出不同结果。
    /// </summary>
    [Fact]
    public async Task 库别无策略行时回落部署级默认档位()
    {
        var exception = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunAsync(depotLocationMode: null, locationNo: string.Empty, deploymentLocationMode: 3));

        Assert.Contains("必须指定库位", exception.Message);
    }

    private sealed record Observed(string BalanceLocation, double BalanceQty, double SentinelQty);

    /// <summary>
    /// 在一个事务内：建策略行与单据 → 批核出库 → 读出结果 → 回滚。
    /// <paramref name="depotLocationMode"/> 为 <c>null</c> 表示该库别**没有策略行**（走部署级默认）；
    /// <paramref name="deploymentLocationMode"/> 非空时临时改写部署级默认行的档位。
    /// </summary>
    private static async Task<Observed> RunAsync(
        int? depotLocationMode, string locationNo, int? deploymentLocationMode = null)
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, depotLocationMode, deploymentLocationMode, locationNo);

            var columns = await new EffectPhysicalColumns().LoadAsync(connection, CancellationToken.None, transaction);
            var plan = Plan();
            var modulePlan = new ModuleEffectPlan(
                130104, "INV_OCCUR_OUT_M", "INV_OCCUR_OUT_D", "v1", new[] { "OCCUR_TYPE", "OCCUR_NO" },
                Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

            var rowSet = plan.BuildRowSet(modulePlan, new[] { Type, No }, columns);
            await new InventoryMoveSql(
                    connection, transaction, plan, EffectEvent.ApproveEffect, Policies(connectionString),
                    PolicyServiceFactory.AuditWriter(connectionString), 130104, Type + "," + No, "ADR14LM")
                .RunAsync(rowSet, CancellationToken.None);

            return await ReadAsync(connection, transaction);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static InventoryMovePlan Plan() => InventoryMovePlan.Parse(JsonSerializer.SerializeToElement(new
    {
        direction = "OUT",
        fieldMap = new
        {
            masterDate = "OCCUR_DATE",
            qty = "QTY",
            detail = new[] { "SERIAL_NO", "PRO_NO", "UNIT_ID", "BATCH_NO" },
        },
    }));

    private static async Task<Observed> ReadAsync(SqlConnection connection, SqlTransaction transaction)
    {
        var location = string.Empty;
        var qty = 0d;
        var sentinelQty = 0d;
        await using (var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(LOCATION_NO)), ISNULL(QTY,0) FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro AND DEPOT_ID=@Depot",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Pro", Plain);
            command.Parameters.AddWithValue("@Depot", Depot);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                location = reader.GetString(0);
                qty = reader.GetDouble(1);
            }
        }

        await using (var command = new SqlCommand(
            "SELECT ISNULL(QTY,0) FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro AND DEPOT_ID=@Depot AND LOCATION_NO=N'-'",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@Pro", Plain);
            command.Parameters.AddWithValue("@Depot", Depot);
            sentinelQty = Convert.ToDouble(await command.ExecuteScalarAsync() ?? 0d);
        }

        return new Observed(location, qty, sentinelQty);
    }

    private static async Task SeedAsync(
        SqlConnection connection, SqlTransaction transaction,
        int? depotLocationMode, int? deploymentLocationMode, string locationNo)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE=@Type;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro;
            DELETE FROM dbo.INV_OCCUR_OUT_D WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.INV_OCCUR_OUT_M WHERE OCCUR_TYPE=@Type;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO=@Pro;

            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH, UNIT_ID) VALUES (@Pro, N'ADR14LM 普通料件', 0, @Unit);
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR14LM 位置档位仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, SEQ_NO, STATUS)
                VALUES (@Depot, N'-',      NULL,   N'/-',               N'BIN',  N'未指定位置（待归位）', 0, N'A'),
                       (@Depot, N'A',      NULL,   N'/A',              N'ZONE', N'A 区',                1, N'A'),
                       (@Depot, N'A-R1',   N'A',   N'/A/A-R1',         N'RACK', N'A-R1 架',             1, N'A'),
                       (@Depot, N'A-R1-B1',N'A-R1',N'/A/A-R1/A-R1-B1', N'BIN',  N'A-R1-B1 位',          1, N'A');

            -- 期初存量必须落在**单据指定的那个库位**上：出库侧的库存充足性校验是按
            -- (料号, 库别, 库位, 批次) 判定的（P2-04），存量放在别处会被判"库存不足"。
            -- 未填位置时归一为哨兵行，所以这里跟着归一。
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, USEABLE_QTY, INIT_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@Pro, @Depot, @BalLoc, N'', 10, 10, 10, 5, 50);

            INSERT INTO dbo.INV_OCCUR_OUT_M (OCCUR_TYPE, OCCUR_NO, OCCUR_DATE, CREATE_PERSON, CREATE_DATE)
                VALUES (@Type, @No, '2026-09-01', N'ADR14LM', '2026-09-01');
            INSERT INTO dbo.INV_OCCUR_OUT_D (OCCUR_TYPE, OCCUR_NO, SERIAL_NO, PRO_NO, QTY, DEPOT_ID, LOCATION_NO, UNIT_ID)
                VALUES (@Type, @No, 1, @Pro, 4, @Depot, @Loc, @Unit);
            """,
            ("@Pro", Plain), ("@Depot", Depot), ("@Unit", Unit), ("@Type", Type), ("@No", No),
            ("@Loc", locationNo),
            ("@BalLoc", string.IsNullOrWhiteSpace(locationNo) ? "-" : locationNo.Trim()));

        // 库别行整行覆盖部署级默认：只有 LOCATION_MODE 随用例变化，其余维度保持最松配置。
        // 传 null 表示该库别**不建策略行**，用于验证回落部署级默认的那一跳。
        if (depotLocationMode is { } depotMode)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH) "
                + "VALUES (@Depot, @Mode, N'FIXED', 0, 0, 1, 1)",
                ("@Depot", Depot), ("@Mode", depotMode));

        if (deploymentLocationMode is { } deploymentMode)
            await ExecuteAsync(connection, transaction,
                "UPDATE dbo.DEPOT_STOCK_POLICY SET LOCATION_MODE=@Mode WHERE DEPOT_ID=N'*'",
                ("@Mode", deploymentMode));
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
