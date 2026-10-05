using System.Data;
using EOS.API.Data;
using EOS.API.Features.Inventory;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 月结快照的**半成品侧**真库验收（D3 / WS-18b）。
///
/// 半成品账与主账的算法**必须不同**，这正是本用例要钉住的东西：
///   主账 = 上一期快照 + 区间流水净额；半成品账**不写流水** ⇒ 只能**直取余额**。
///   于是它只有"期末 == 生成当天"时才是准的，补结过去的期间必须**拒绝**（不是写个看着像真的数）。
///
/// 四条：① 参数关 ⇒ 半成品不进快照；② 参数开 + 期末当天 ⇒ 按**制程**落行（主账行仍落哨兵，两本账不混格）；
/// ③ 参数开 + 期末在过去 ⇒ **拒绝**并点名原因；④ 参数开 ⇒ **不吃上一期快照里的半成品行**
/// （把余额清空后再生成，行必须消失——证明来源只有"直取余额"一处）。
/// 判别性：摘掉快照服务里的参数判断（等价于"无条件纳半成品"）⇒ 第①条变红。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class MonthCloseSnapshotHalfStockLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string MonthType = "ZZHSB";
    private const string MonthNo = "ZZHSB-1";
    private const string PrevMonthNo = "ZZHSB-0";
    private const string Product = "ZZHSHPRO1";
    private const string Procedure = "ZZP1";
    private const double HalfQty = 7d;
    private const double HalfCost = 3d;

    private string _depot = string.Empty;
    private DbConnectionFactory _connections = null!;
    private MonthCloseSnapshotService _snapshots = null!;

    public async Task InitializeAsync()
    {
        _connections = Connections();
        var audit = new WorkbenchAuditWriter(_connections, new HttpContextAccessor(),
            new WorkbenchDefinitionProvider(_connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
            Options.Create(new AuditSettings()));
        _snapshots = new MonthCloseSnapshotService(new DepotStockPolicyService(_connections, audit));

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
        _depot = await ScalarAsync<string>(connection, "SELECT TOP 1 RTRIM(DEPOT_ID) FROM dbo.DEPOT ORDER BY DEPOT_ID;")
            ?? throw new InvalidOperationException("库里没有库别主档，无法造半成品账夹具。");
        await ExecAsync(connection, """
            INSERT INTO dbo.HALF_PRO_DEPOT (PRO_NO, PROCEDURE_TYPE_ID, DEPOT_ID, QTY, INIT_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@pro, @proc, @depot, @qty, @qty, @cost, @qty * @cost);
            """, ("@pro", Product), ("@proc", Procedure), ("@depot", _depot),
            ("@qty", HalfQty), ("@cost", HalfCost));
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
    }

    private static async Task CleanupAsync(SqlConnection connection)
    {
        await ExecAsync(connection,
            "DELETE FROM dbo.INV_PRO_MONTH_D WHERE MONTH_TYPE = @type;", ("@type", MonthType));
        await ExecAsync(connection,
            "DELETE FROM dbo.INV_PRO_MONTH_M WHERE MONTH_TYPE = @type;", ("@type", MonthType));
        await ExecAsync(connection,
            "DELETE FROM dbo.HALF_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product));
        await ExecAsync(connection,
            "UPDATE dbo.DEPOT_STOCK_POLICY SET MONTH_CLOSE_SCOPE_HALF_STOCK = 0 WHERE DEPOT_ID = N'*';");
    }

    // ===== ① 参数关：不进快照 =====

    [Fact]
    public async Task 参数关_半成品不进快照()
    {
        await SetScopeAsync(false);
        await SeedMonthAsync(DateTime.Today, confirmed: false);

        var result = await GenerateAsync(DateTime.Today);
        Assert.Equal(0, result.HalfStockRowCount);
        Assert.Equal(0, await CountHalfRowsAsync(MonthNo));
    }

    // ===== ② 参数开 + 期末当天：按制程落行，且不与主账混格 =====

    [Fact]
    public async Task 参数开_期末当天_半成品按制程进快照()
    {
        await SetScopeAsync(true);
        await SeedMonthAsync(DateTime.Today, confirmed: false);

        var result = await GenerateAsync(DateTime.Today);
        // 库里本来就有半成品余额（别人的真实数据也在范围内），所以只断言"至少纳进来"，逐值断言看本夹具那一行
        Assert.True(result.HalfStockRowCount >= 1, $"半成品行数 {result.HalfStockRowCount}");

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        // reader 必须开在自己的作用域里：同一个连接上还有下一条查询，reader 没读完就发下一条会报
        // "已有打开的与此 Connection 相关联的 DataReader"（实测踩过）。
        await using (var command = new SqlCommand("""
            SELECT RTRIM(PROCEDURE_TYPE_ID), QTY, PRICE, RTRIM(LOCATION_NO), LTRIM(RTRIM(BATCH_NO))
              FROM dbo.INV_PRO_MONTH_D WHERE MONTH_TYPE = @type AND MONTH_NO = @no AND LTRIM(RTRIM(PRO_NO)) = @pro;
            """, connection))
        {
            command.Parameters.AddWithValue("@type", MonthType);
            command.Parameters.AddWithValue("@no", MonthNo);
            command.Parameters.AddWithValue("@pro", Product);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(Procedure, reader.GetString(0));
            Assert.Equal(HalfQty, reader.GetDouble(1));
            Assert.Equal(HalfCost, reader.GetDouble(2));      // 单价 = 成本价（期末量 = 结存、本期入库 0）
            Assert.Equal("-", reader.GetString(3));           // 位置对半成品账没有意义 ⇒ 哨兵
            Assert.Equal(string.Empty, reader.GetString(4));  // 批次同理
            Assert.False(await reader.ReadAsync());           // 这个料号在本期只有这一行
        }

        // 两本账不混格：同一料号不得出现"没有制程"的行
        Assert.Equal(0, await ScalarAsync<int>(connection, """
            SELECT COUNT(*) FROM dbo.INV_PRO_MONTH_D
             WHERE MONTH_TYPE = @type AND MONTH_NO = @no AND LTRIM(RTRIM(PRO_NO)) = @pro
               AND ISNULL(LTRIM(RTRIM(PROCEDURE_TYPE_ID)), N'') = N'';
            """, ("@type", MonthType), ("@no", MonthNo), ("@pro", Product)));
    }

    // ===== ③ 参数开 + 期末在过去：拒绝（半成品没有流水可回溯） =====

    [Fact]
    public async Task 参数开_期末在过去_拒绝并点名原因()
    {
        await SetScopeAsync(true);
        var past = DateTime.Today.AddDays(-30);
        await SeedMonthAsync(past, confirmed: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GenerateAsync(past));
        Assert.Contains("没有库存流水", error.Message);
        Assert.Contains(past.ToString("yyyy-MM-dd"), error.Message);
        Assert.Equal(0, await CountHalfRowsAsync(MonthNo));
    }

    // ===== ④ 半成品的来源只有"直取余额"一处：不吃上一期快照 =====

    [Fact]
    public async Task 参数开_不吃上一期快照里的半成品行()
    {
        var previous = DateTime.Today.AddDays(-60);
        await SeedMonthAsync(previous, confirmed: true);
        await SeedMonthAsync(DateTime.Today, confirmed: false);
        // 上一期快照里留一行半成品（制程非哨兵）
        await UseAsync(connection => ExecAsync(connection, """
            INSERT INTO dbo.INV_PRO_MONTH_D (MONTH_TYPE, MONTH_NO, SERIAL_NO, PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO,
                                             PROCEDURE_TYPE_ID, QTY, PRICE)
                VALUES (@type, @no, 1, @pro, @depot, N'-', N'', @proc, @qty, @cost);
            """, ("@type", MonthType), ("@no", PrevMonthNo), ("@pro", Product), ("@depot", _depot),
            ("@proc", Procedure), ("@qty", HalfQty), ("@cost", HalfCost)));
        // 把余额表清空：若快照是"接着上一期滚"，这行还会出现；只有"直取余额"才会消失
        await UseAsync(connection => ExecAsync(connection,
            "DELETE FROM dbo.HALF_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));

        await SetScopeAsync(true);
        await GenerateAsync(DateTime.Today);

        // 只看**本夹具料号**：它的余额已被清空 ⇒ 本期不得有它的半成品行。
        // （库里别人的半成品余额仍在范围内，那是另一回事——所以断言按料号收敛。）
        Assert.Equal(0, await CountHalfRowsAsync(MonthNo, Product));
        // 上一期那一行仍在（本用例只写本期，不动历史）
        Assert.Equal(1, await CountHalfRowsAsync(PrevMonthNo, Product));
    }

    // ===== 装配 =====

    /// <summary>
    /// 生成一次快照并**提交**：本用例要按行断言写出来的东西，所以不能事后回滚
    /// （夹具自带 `ZZHSB` 单别，`DisposeAsync` 按单别清理，不会留痕）。
    /// </summary>
    private async Task<MonthCloseSnapshotResult> GenerateAsync(DateTime monthDate)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            var result = await _snapshots.GenerateAsync(
                connection, transaction, new MonthCloseSnapshotRequest(MonthType, MonthNo, monthDate, false),
                CancellationToken.None);
            await transaction.CommitAsync();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task SeedMonthAsync(DateTime monthDate, bool confirmed) =>
        await UseAsync(connection => ExecAsync(connection, """
            INSERT INTO dbo.INV_PRO_MONTH_M (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CREATE_PERSON, CREATE_DATE, CI)
                VALUES (@type, @no, @date, @tag, N'ZZHSB', GETDATE(), 'ZZHSB');
            """, ("@type", MonthType), ("@no", confirmed ? PrevMonthNo : MonthNo),
            ("@date", monthDate.Date), ("@tag", confirmed)));

    /// <summary>该期内**制程非哨兵**的行数；给了料号就只看那个料号（库里还有别人的半成品余额）。</summary>
    private async Task<int> CountHalfRowsAsync(string monthNo, string? productNo = null)
    {
        var count = 0;
        await UseAsync(async connection => count = await ScalarAsync<int>(connection, """
            SELECT COUNT(*) FROM dbo.INV_PRO_MONTH_D
             WHERE MONTH_TYPE = @type AND MONTH_NO = @no
               AND ISNULL(LTRIM(RTRIM(PROCEDURE_TYPE_ID)), N'') <> N''
               AND (@pro IS NULL OR LTRIM(RTRIM(PRO_NO)) = @pro);
            """, ("@type", MonthType), ("@no", monthNo), ("@pro", productNo)));
        return count;
    }

    private static async Task SetScopeAsync(bool value)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection,
            $"UPDATE dbo.DEPOT_STOCK_POLICY SET MONTH_CLOSE_SCOPE_HALF_STOCK = {(value ? 1 : 0)} WHERE DEPOT_ID = N'*';");
    }

    private static DbConnectionFactory Connections() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build());

    /// <summary>一次性连接：夹具的每一次读写自己开、自己关（共用连接会被前一次调用提前释放）。</summary>
    private static async Task UseAsync(Func<SqlConnection, Task> action)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await action(connection);
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
}
