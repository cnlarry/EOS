using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Models;
using EOS.API.Telemetry;
using EOS.API.Tests.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// P0 真库验收：库存盘点单的自定义按钮「重算账面数量」（`recalc-account`）。
///
/// 逐条对上 ADR-018 §4 的验收：账面数按**最新库存**重算、**盘点数不变**、
/// **明细行数与位置/批次不丢**、重复点击幂等、无授权 403、失败零残留（BLOCK 回滚）。
/// 未批核不允许重算（前置条件 CONFIRM_TAG=1）在服务端每次点击都复核。
///
/// 单据与明细在本用例内造（键以 ZZ 前缀隔离），结束即删除；库存表只读不改。
/// 需要 EOS_ERP_TEST_CONNECTION。
/// </summary>
[Collection("live-database")]
public sealed class DocumentActionRecalcAccountLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 130101;
    private const string MasterTable = "INV_CHECK_STOCK_M";
    private const string DetailTable = "INV_CHECK_STOCK_D";
    private const string StockTable = "INV_PRO_DEPOT";
    private const string TestType = "ZZ";
    private const string TestNo = "ZZRECALC0001";
    private const string TestUser = "ZZRECALC01";

    /// <summary>盘点数（人为录入，重算不得改动）与一条错误的账面数起点。量额列在库内是 float，故用例按 double 比对。</summary>
    private const double CheckedQty = 12.5d;
    private const double WrongAccountQty = 999999d;

    private static DbConnectionFactory Connections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        return new DbConnectionFactory(configuration);
    }

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

    private sealed record StockRow(string Depot, string Product, string Location, string Batch, double Qty);

    /// <summary>取一条真实库存行（库别/料号/库位/批号/数量），用例据此造明细与断言。</summary>
    private static async Task<StockRow> AnyStockRowAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            $"""
            SELECT TOP 1 LTRIM(RTRIM(DEPOT_ID)), LTRIM(RTRIM(PRO_NO)),
                   ISNULL(LTRIM(RTRIM(LOCATION_NO)), N''), ISNULL(LTRIM(RTRIM(BATCH_NO)), N''), QTY
            FROM dbo.{StockTable} WITH (NOLOCK)
            WHERE QTY <> 0 AND LOCATION_NO IS NOT NULL AND BATCH_NO IS NOT NULL
            ORDER BY DEPOT_ID, PRO_NO;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new StockRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDouble(4))
            : throw new InvalidOperationException($"{StockTable} 没有可用的库存行，无法验证重算账面数。");
    }

    /// <summary>造单据：两行有货（一行对得上库存、一行对不上）、一行盘点数由人填过。</summary>
    private async Task FixtureUpAsync(StockRow stock)
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.{MasterTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no)
                INSERT INTO dbo.{MasterTable} (CHECK_STOCK_TYPE,CHECK_STOCK_NO,DEPOT_ID,CONFIRM_TAG,CREATE_PERSON,CREATE_DATE,CI)
                VALUES (@type,@no,@depot,0,N'DbUp',SYSDATETIME(),N'');
            """,
            ("@type", TestType), ("@no", TestNo), ("@depot", stock.Depot));
        await ExecAsync(connection,
            $"""
            INSERT INTO dbo.{DetailTable}
                (CHECK_STOCK_TYPE,CHECK_STOCK_NO,SERIAL_NO,PRO_NO,DEPOT_ID,ACCOUNT_QTY,CHECK_QTY,LOCATION_NO,BATCH_NO)
            VALUES
                (@type,@no,1,@product,@depot,@wrong,@checked,@location,@batch),
                (@type,@no,2,@product,@depot,@wrong,@checked,@location,@batch),
                (@type,@no,3,N'ZZNOSUCHPRO',@depot,@wrong,@checked,N'ZZ-NO-SUCH-LOCATION',@batch);
            """,
            ("@type", TestType), ("@no", TestNo), ("@product", stock.Product), ("@depot", stock.Depot),
            ("@wrong", WrongAccountQty), ("@checked", CheckedQty),
            ("@location", stock.Location), ("@batch", stock.Batch));
        // 两个不同料号各自有货才谈得上"逐行重算"：第二行借另一条真实库存行。
        await ExecAsync(connection,
            $"""
            UPDATE d SET d.PRO_NO = s.PRO_NO, d.LOCATION_NO = s.LOCATION_NO, d.BATCH_NO = s.BATCH_NO
            FROM dbo.{DetailTable} d
            CROSS JOIN (SELECT TOP 1 LTRIM(RTRIM(PRO_NO)) AS PRO_NO, ISNULL(LTRIM(RTRIM(LOCATION_NO)),N'') AS LOCATION_NO,
                               ISNULL(LTRIM(RTRIM(BATCH_NO)),N'') AS BATCH_NO
                        FROM dbo.{StockTable} WITH (NOLOCK)
                        WHERE QTY <> 0 AND DEPOT_ID = @depot AND PRO_NO <> @product
                        ORDER BY PRO_NO) s
            WHERE d.CHECK_STOCK_TYPE=@type AND d.CHECK_STOCK_NO=@no AND d.SERIAL_NO=2;
            """,
            ("@type", TestType), ("@no", TestNo), ("@depot", stock.Depot), ("@product", stock.Product));
        await ExecAsync(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user AND M_IDX=@module AND BUTTON_KEY=@key)
                INSERT INTO dbo.SYSDD_BUTTON (USER_ID,M_IDX,BUTTON_KEY,ALLOW_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@user,@module,@key,1,N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
            """,
            ("@user", TestUser), ("@module", ModuleId), ("@key", RecalcAccountHandler.ActionKey));
    }

    private static async Task FixtureDownAsync()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"DELETE FROM dbo.{DetailTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{MasterTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            "DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user AND M_IDX=@module;",
            ("@user", TestUser), ("@module", ModuleId));
        await ExecAsync(connection,
            "DELETE FROM dbo.AUDIT_EVENT WHERE RESOURCE_KEY=@key AND ACTION=@action;",
            ("@key", $"{TestType},{TestNo}"), ("@action", RecalcAccountHandler.ActionKey));
        await ExecAsync(connection,
            "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE MODULE_ID=@module AND ACTION=N'ACTION';",
            ("@module", ModuleId));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => FixtureDownAsync();

    // ===== 执行器 =====

    private static WorkbenchDefinition Definition(bool confirmed)
    {
        // 与迁移 219 配出来的一致：MANUAL 行 + 已批核才能点（CONFIRM_TAG=1）+ 需二次确认。
        var actions = JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                seq = 1,
                eventCode = "MANUAL",
                effectKey = RecalcAccountHandler.ActionKey,
                enabled = true,
                label = "重算账面数量",
                confirmTag = true,
                failMode = "BLOCK",
                condition = """{"logic":"AND","items":[{"type":"value-eq","field":{"scope":"MASTER","field":"CONFIRM_TAG"},"value":1}]}""",
            },
        });
        return new WorkbenchDefinition(
            ModuleId: ModuleId, Title: "库存盘点单", MasterTable: MasterTable, DetailTable: DetailTable,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["CHECK_STOCK_TYPE", "CHECK_STOCK_NO"], DetailNoFields: "", HasWorkflow: false,
            UserId: TestUser,
            ExecTag: "Z",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CHECK_STOCK_TYPE" },
            BusinessActions: actions);
    }

    private static FormDefinition Form() =>
        new(ModuleId, "库存盘点单", MasterTable, DetailTable, true, true, "view", [], [],
            ["CHECK_STOCK_TYPE", "CHECK_STOCK_NO"], string.Empty, string.Empty);

    private static DocumentActionExecutor Executor()
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider, Options.Create(new AuditSettings()));
        return new DocumentActionExecutor(
            connections,
            new DocumentActionRegistry([new RecalcAccountHandler()], NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            auditWriter,
            NullLogger<DocumentActionExecutor>.Instance);
    }

    private static Task<DocumentActionExecution> RunAsync(string idempotencyKey, bool confirm) =>
        Executor().ExecuteAsync(Definition(confirmed: true), Form(), RecalcAccountHandler.ActionKey,
            new DocumentActionRequest([TestType, TestNo], Confirm: confirm),
            TestUser, "测试经办人", null, idempotencyKey, CancellationToken.None);

    private static async Task SetConfirmedAsync(bool confirmed)
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"UPDATE dbo.{MasterTable} SET CONFIRM_TAG=@tag WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
            ("@tag", confirmed), ("@type", TestType), ("@no", TestNo));
    }

    private sealed record DetailRow(short Serial, string Product, string Location, string Batch, double? Account, double? Checked);

    private static async Task<IReadOnlyList<DetailRow>> ReadDetailsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            $"""
            SELECT SERIAL_NO, LTRIM(RTRIM(PRO_NO)), ISNULL(LTRIM(RTRIM(LOCATION_NO)),N''),
                   ISNULL(LTRIM(RTRIM(BATCH_NO)),N''), ACCOUNT_QTY, CHECK_QTY
            FROM dbo.{DetailTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no ORDER BY SERIAL_NO;
            """, connection);
        command.Parameters.AddWithValue("@type", TestType);
        command.Parameters.AddWithValue("@no", TestNo);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<DetailRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new DetailRow(
                reader.GetInt16(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5)));
        }
        return rows;
    }

    private static async Task<double?> StockQtyAsync(string depot, string product, string location, string batch)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            $"""
            SELECT QTY FROM dbo.{StockTable} WITH (NOLOCK)
            WHERE LTRIM(RTRIM(DEPOT_ID))=@depot AND LTRIM(RTRIM(PRO_NO))=@product
              AND ISNULL(LTRIM(RTRIM(LOCATION_NO)),N'')=@location AND ISNULL(LTRIM(RTRIM(BATCH_NO)),N'')=@batch;
            """, connection);
        command.Parameters.AddWithValue("@depot", depot);
        command.Parameters.AddWithValue("@product", product);
        command.Parameters.AddWithValue("@location", location);
        command.Parameters.AddWithValue("@batch", batch);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToDouble(value);
    }

    // ===== 用例 =====

    [Fact]
    public async Task Recalc_RefreshesAccountQty_KeepsCheckedQtyRowsAndPositions()
    {
        var stock = await AnyStockRowAsync();
        await FixtureUpAsync(stock);
        await SetConfirmedAsync(true);
        var before = await ReadDetailsAsync();

        var result = await Executor().ExecuteAsync(Definition(confirmed: true), Form(), RecalcAccountHandler.ActionKey,
            new DocumentActionRequest([TestType, TestNo], Confirm: true),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.Equal(DocumentActionStatus.Ok, result.Status);
        Assert.Equal(DocumentActionOutcome.Refreshed, result.Result!.Outcome);

        var after = await ReadDetailsAsync();
        // 明细行数与身份（项次/位置/批次）不丢
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Select(row => row.Serial), after.Select(row => row.Serial));
        for (var index = 0; index < before.Count; index++)
        {
            Assert.Equal(before[index].Product, after[index].Product);
            Assert.Equal(before[index].Location, after[index].Location);
            Assert.Equal(before[index].Batch, after[index].Batch);
            // 盘点数由人填，重算不得改动
            Assert.Equal(CheckedQty, after[index].Checked!.Value, 6);
        }
        // 账面数按最新库存重算：对得上的行取库存量，库存里没有的行归零
        for (var index = 0; index < after.Count; index++)
        {
            var expected = await StockQtyAsync(stock.Depot, after[index].Product, after[index].Location, after[index].Batch);
            Assert.Equal(expected ?? 0d, after[index].Account!.Value, 6);
        }
        Assert.Contains(after, row => row.Product == "ZZNOSUCHPRO" && row.Account is 0d);
    }

    [Fact]
    public async Task Recalc_IsIdempotent_ForTheSameKey()
    {
        var stock = await AnyStockRowAsync();
        await FixtureUpAsync(stock);
        await SetConfirmedAsync(true);
        var key = Guid.NewGuid().ToString("N");

        var first = await RunAsync(key, confirm: true);
        var second = await RunAsync(key, confirm: true);

        Assert.Equal(DocumentActionStatus.Ok, first.Status);
        Assert.Equal(DocumentActionStatus.Ok, second.Status);
        var after = await ReadDetailsAsync();
        Assert.All(after, row => Assert.Equal(CheckedQty, row.Checked!.Value, 6));
    }

    [Fact]
    public async Task Recalc_IsRefusedBeforeApproval_AndWritesNothing()
    {
        var stock = await AnyStockRowAsync();
        await FixtureUpAsync(stock);
        await SetConfirmedAsync(false);

        var result = await Executor().ExecuteAsync(Definition(confirmed: false), Form(), RecalcAccountHandler.ActionKey,
            new DocumentActionRequest([TestType, TestNo], Confirm: true),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.Equal(DocumentActionStatus.Failed, result.Status);
        var after = await ReadDetailsAsync();
        // 前置条件不满足时一行都不能动（未批核的单据不许重算）
        Assert.All(after, row => Assert.Equal(WrongAccountQty, row.Account!.Value, 6));
    }

    [Fact]
    public async Task Probe_RunsNothing_AndLeavesTheKeyUnclaimed()
    {
        var stock = await AnyStockRowAsync();
        await FixtureUpAsync(stock);
        await SetConfirmedAsync(true);
        var key = Guid.NewGuid().ToString("N");
        var before = await ReadDetailsAsync();

        var probe = await RunAsync(key, confirm: false);

        Assert.Equal(DocumentActionStatus.Ok, probe.Status);
        Assert.True(probe.RequiresConfirmation);
        var after = await ReadDetailsAsync();
        Assert.All(after, row => Assert.Equal(WrongAccountQty, row.Account!.Value, 6));
        Assert.Equal(before.Count, after.Count);

        // 探路不占幂等键：紧接着的真实执行必须能跑
        var confirmed = await RunAsync(key, confirm: true);
        Assert.Equal(DocumentActionStatus.Ok, confirmed.Status);
        Assert.False(confirmed.RequiresConfirmation);
    }

    [Fact]
    public async Task Recalc_IsForbidden_WithoutButtonAuthorization()
    {
        var stock = await AnyStockRowAsync();
        await FixtureUpAsync(stock);
        await SetConfirmedAsync(true);
        await using (var connection = await OpenAsync())
        {
            await ExecAsync(connection,
                "DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user AND M_IDX=@module;",
                ("@user", TestUser), ("@module", ModuleId));
        }

        var result = await RunAsync(Guid.NewGuid().ToString("N"), confirm: true);

        Assert.Equal(DocumentActionStatus.Forbidden, result.Status);
        var after = await ReadDetailsAsync();
        Assert.All(after, row => Assert.Equal(WrongAccountQty, row.Account!.Value, 6));
    }

    [Fact]
    public async Task Recalc_OnDocumentWithoutDetails_ReportsInsteadOfFailing()
    {
        var stock = await AnyStockRowAsync();
        await FixtureUpAsync(stock);
        await SetConfirmedAsync(true);
        await using (var connection = await OpenAsync())
        {
            await ExecAsync(connection,
                $"DELETE FROM dbo.{DetailTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
                ("@type", TestType), ("@no", TestNo));
        }

        var result = await RunAsync(Guid.NewGuid().ToString("N"), confirm: true);

        Assert.Equal(DocumentActionStatus.Ok, result.Status);
        Assert.Equal(DocumentActionOutcome.Message, result.Result!.Outcome);
        Assert.Contains("还没有明细", result.Result.Message!);
    }
}
