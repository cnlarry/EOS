using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存策略页独立归位与策略行删除的真库验收（策略页自己管，不走单据动作框架）。
///
/// 口径：归位是带预览的两步（先看"将搬几组"，确认后才真写），只改位置不改总量；
/// 删除只是去掉库别的覆盖行，该库别此后按部署级默认行使，存量不动。
///
/// 夹具**自带库别与料号**（`ZZRELDP01` 仓 / `ZZRELPRO01` 料号），用完即删。
/// 不能用"借一个真实库别"的写法：归位是**库别级**动作——它把该库别**全部**哨兵行的数量
/// 搬到目标库位并把整库别的可用量重算一遍，与"该库别上有哪些料号"无关；
/// 借真实库别来跑，等于对那个库别的真实库存做一次账面认定，收尾再按库位删就等于销毁数量。
/// 需要 MSSQL_ERP_CONN。
/// </summary>
[Collection("live-database")]
public sealed class DepotStockPolicyRelocateLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string StockTable = "INV_PRO_DEPOT";
    private const string Depot = "ZZRELDP01";
    private const string TestProduct = "ZZRELPRO01";
    private const string TestLocation = "ZZ-REL-LOC";
    private const string DeleteDepot = "ZZDEL0001";
    private const double StartQty = 100d;

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

    private static async Task<T?> ScalarAsync<T>(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? default : (T)Convert.ChangeType(scalar, typeof(T));
    }

    private static DepotStockPolicyService Service() => PolicyServiceFactory.Create(ConnectionString);

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync();
        // 上一轮被中断时可能留下残留：先按同一批键清干净，重复运行结果才可比。
        await DeleteFixtureAsync(connection);

        await ExecAsync(connection,
            """
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@depot, N'ZZ 归位测试仓');
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH, UNIT_ID)
                VALUES (@pro, N'ZZ 归位用例料号', 0, N'ZZRELUN');
            -- 归位的目标必须是启用中的库位：本库只有哨兵位，故自造一个 ZZ 库位，用完删除。
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID,LOCATION_NO,LOCATION_TYPE,STORAGE_TYPE,STATUS,CONFIRM_TAG)
                VALUES (@depot,N'-',N'STAGE',N'BULK',N'A',1),
                       (@depot,@loc,N'STAGE',N'BULK',N'A',1);
            """,
            ("@depot", Depot), ("@pro", TestProduct), ("@loc", TestLocation));

        // 自造哨兵行：货记在『未指定位置』上（库存表里 '-' 就是"没记位置"）。
        await ExecAsync(connection,
            $"""
            INSERT INTO dbo.{StockTable} (PRO_NO,DEPOT_ID,LOCATION_NO,BATCH_NO,QTY,CREATE_PERSON,CREATE_DATE,CONFIRM_TAG,CI)
            VALUES (@pro,@depot,N'-',N'',@qty,N'DbUp',SYSDATETIME(),0,N'');
            """,
            ("@pro", TestProduct), ("@depot", Depot), ("@qty", StartQty));
    }

    public async Task DisposeAsync()
    {
        await using var connection = await OpenAsync();
        await DeleteFixtureAsync(connection);
        // 审计（AUDIT_EVENT / AUDIT_FIELD_CHANGE）按设计保留：删它等于抹掉痕迹。
    }

    /// <summary>
    /// 只清**本用例自造的键**：库别虽然是自己建的，余额表仍按料号限定——
    /// "整个库别"这种口径一旦碰上库别被复用或借用，就是销毁别人的行。
    /// </summary>
    private static async Task DeleteFixtureAsync(SqlConnection connection)
    {
        await ExecAsync(connection,
            $"DELETE FROM dbo.{StockTable} WHERE DEPOT_ID=@depot AND LTRIM(RTRIM(PRO_NO))=@pro;",
            ("@depot", Depot), ("@pro", TestProduct));
        await ExecAsync(connection,
            "DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@depot;", ("@depot", Depot));
        await ExecAsync(connection,
            "DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID IN (@depot, @del);",
            ("@depot", Depot), ("@del", DeleteDepot));
        await ExecAsync(connection,
            "DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO))=@pro;", ("@pro", TestProduct));
        await ExecAsync(connection,
            "DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@depot;", ("@depot", Depot));
    }

    private async Task<double> DepotTotalAsync()
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<double>(connection,
            $"SELECT ISNULL(SUM(ISNULL(QTY,0)),0) FROM dbo.{StockTable} WHERE DEPOT_ID=@depot AND LTRIM(RTRIM(PRO_NO))=@pro;",
            ("@depot", Depot), ("@pro", TestProduct));
    }

    private async Task<double> QtyAtAsync(string location)
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<double>(connection,
            $"""
            SELECT ISNULL(SUM(ISNULL(QTY,0)),0) FROM dbo.{StockTable}
            WHERE DEPOT_ID=@depot AND LTRIM(RTRIM(PRO_NO))=@pro AND LTRIM(RTRIM(LOCATION_NO))=@loc;
            """,
            ("@depot", Depot), ("@pro", TestProduct), ("@loc", location));
    }

    private async Task<double> AvailableAtAsync(string location)
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<double>(connection,
            $"""
            SELECT ISNULL(SUM(CAST(ISNULL(USEABLE_QTY,0) AS float)),0) FROM dbo.{StockTable}
            WHERE DEPOT_ID=@depot AND LTRIM(RTRIM(PRO_NO))=@pro AND LTRIM(RTRIM(LOCATION_NO))=@loc;
            """,
            ("@depot", Depot), ("@pro", TestProduct), ("@loc", location));
    }

    private async Task<bool> PolicyRowExistsAsync(string depot)
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@depot;",
            ("@depot", depot)) > 0;
    }

    // ===== 用例 =====

    [Fact]
    public async Task StandaloneRelocate_MovesStock_ToTargetLocation_AndKeepsDepotTotal()
    {
        var beforeTotal = await DepotTotalAsync();
        Assert.Equal(StartQty, await QtyAtAsync("-"), 6);

        var result = await Service().RelocateStandaloneAsync(Depot, TestLocation, "测试经办人");

        Assert.True(result.Relocated, string.Join('；', result.Errors));
        Assert.Contains("已完成归位", result.Message);
        // 位置改记：哨兵行清零（行保留），目标库位吃下这批货，库别总量一分不差
        Assert.Equal(0d, await QtyAtAsync("-"), 6);
        Assert.Equal(StartQty, await QtyAtAsync(TestLocation), 6);
        Assert.Equal(beforeTotal, await DepotTotalAsync(), 6);
    }

    /// <summary>
    /// 归位必须**同步可用量**。`USEABLE_QTY` 是存列（可用量 = 数量 − 冻结 − 预留），而新建的目标行
    /// 取的是列默认 `0`：数量搬过去而这一列不重算，它就一直停在 0。出库充足性按可用量判，
    /// 于是**归位之后的货出不去**——判别形态就是出库报「库存数量不足」。
    ///
    /// 这条守的是"数量动过就必须重算"这条纪律：把 <c>SyncAvailabilityForDepotAsync</c> 的调用摘掉，
    /// 本用例即变红。
    /// </summary>
    [Fact]
    public async Task StandaloneRelocate_SyncsUseableQuantity()
    {
        var result = await Service().RelocateStandaloneAsync(Depot, TestLocation, "测试经办人");

        Assert.True(result.Relocated, string.Join('；', result.Errors));
        Assert.Equal(StartQty, await QtyAtAsync(TestLocation), 6);
        Assert.Equal(StartQty, await AvailableAtAsync(TestLocation), 6);
        Assert.Equal(0d, await AvailableAtAsync("-"), 6);
    }

    [Fact]
    public async Task PreviewRelocate_ReportsAndWritesNothing()
    {
        var beforeTotal = await DepotTotalAsync();

        var preview = await Service().PreviewRelocateAsync(Depot, TestLocation);

        Assert.Empty(preview.Errors);
        Assert.Equal(1, preview.PendingGroups);
        Assert.Contains("库别总量不变", preview.Message);
        Assert.Equal(StartQty, await QtyAtAsync("-"), 6);
        Assert.Equal(0d, await QtyAtAsync(TestLocation), 6);
        Assert.Equal(beforeTotal, await DepotTotalAsync(), 6);
    }

    [Fact]
    public async Task StandaloneRelocate_WithoutTarget_IsRefused()
    {
        var result = await Service().RelocateStandaloneAsync(Depot, "", "测试经办人");

        Assert.False(result.Relocated);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(StartQty, await QtyAtAsync("-"), 6);
    }

    [Fact]
    public async Task StandaloneRelocate_UnknownTarget_IsRefused_AndWritesNothing()
    {
        var beforeTotal = await DepotTotalAsync();

        var result = await Service().RelocateStandaloneAsync(Depot, "ZZNOWHERE01", "测试经办人");

        Assert.False(result.Relocated);
        Assert.Contains(result.Errors, error => error.Contains("不存在或已停用"));
        Assert.Equal(StartQty, await QtyAtAsync("-"), 6);
        Assert.Equal(beforeTotal, await DepotTotalAsync(), 6);
    }

    [Fact]
    public async Task DeletePolicyRow_RemovesOverride_FallsBackToDefault()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@depot)
                INSERT INTO dbo.DEPOT_STOCK_POLICY
                    (DEPOT_ID,LOCATION_MODE,STORAGE_MODE,BATCH_MODE,CAPACITY_MODE,
                     MIX_PRODUCT,MIX_BATCH,MONTH_CLOSE_BY_BATCH,MONTH_CLOSE_BY_LOCATION)
                VALUES (@depot,3,N'FIXED',2,0,1,1,1,0);
            """,
            ("@depot", DeleteDepot));
        Assert.True(await PolicyRowExistsAsync(DeleteDepot));

        var result = await Service().DeleteAsync(DeleteDepot, "测试经办人");

        Assert.True(result.Deleted, string.Join('；', result.Errors));
        Assert.False(await PolicyRowExistsAsync(DeleteDepot));
    }

    [Fact]
    public async Task DeleteDeploymentDefault_IsRefused()
    {
        var result = await Service().DeleteAsync("*", "测试经办人");

        Assert.False(result.Deleted);
        Assert.NotEmpty(result.Errors);
        Assert.True(await PolicyRowExistsAsync("*"));
    }

    [Fact]
    public async Task ListDepots_ContainsFixtureDepot()
    {
        var depots = await Service().ListDepotsAsync();

        Assert.Contains(depots, depot => depot.DepotId == Depot);
    }

    [Fact]
    public async Task ListLocations_ContainsEnabledLocation_ExcludesSentinel()
    {
        var locations = await Service().ListLocationsAsync(Depot);

        Assert.Contains(locations, location => location.LocationNo == TestLocation);
        Assert.DoesNotContain(locations, location => location.LocationNo == "-");
    }
}
