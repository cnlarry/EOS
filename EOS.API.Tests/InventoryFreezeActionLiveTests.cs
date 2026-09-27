using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Data.Effects;
using EOS.API.Data.Inventory;
using EOS.API.Models;
using EOS.API.Security;
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
/// 库存冻结 / 解冻的真库验收（D7-⑦ / WS-21）。
///
/// 本段的三件事一起验：**写占用**（`INV_FREEZE`）+ **同步 `USEABLE_QTY`**（按可用量服务的口径，
/// 不自己减）+ **审计前后值**；外加授权这一半（fail-closed：未授权直调 403）。
///
/// 五条：① 冻结 ⇒ 可用量下降、**在库数量不变**、`USEABLE_QTY` 列被同步（本夹具故意把它初始化成 0，
/// 顺带证明"每次写入按口径重算"能治愈 WS-19 登记的那条历史缺口）；② 解冻 ⇒ 回升（整笔与部分释放各一）；
/// ③ 解冻超过当前冻结合计 ⇒ 拒；④ **未授权账号直调 ⇒ 403**（服务端另行鉴权，不靠前端不渲染）；
/// ⑤ 探路不写。
/// 判别性：摘掉同步那一步 ⇒ 第①②条的 `USEABLE_QTY` 断言变红（WS-25 的门禁会常态守这条等式）。
/// </summary>
[Collection("live-database")]
public sealed class InventoryFreezeActionLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1303;
    private const string MasterTable = "INV_PRO_DEPOT";
    private const string Product = "ZZFRZPRO1";
    private const string Depot = "CP";
    private const string Location = "-";
    private const string Batch = "";
    private const string TestUser = "ZZFRZ0001";
    private const string OutsiderUser = "ZZFRZOUT1";
    private const double StockQty = 100d;
    private const string RecordKey = Product + "," + Depot + "," + Location + "," + Batch;

    private DbConnectionFactory _connections = null!;
    private WorkbenchDefinitionProvider _provider = null!;
    private WorkbenchAuditWriter _audit = null!;

    public async Task InitializeAsync()
    {
        _connections = Connections();
        _provider = new WorkbenchDefinitionProvider(_connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        _audit = new WorkbenchAuditWriter(_connections, new HttpContextAccessor(), _provider, Options.Create(new AuditSettings()));

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
        await ExecAsync(connection, """
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE) VALUES (@pro, N'ZZFRZ 冻结料件', N'规格', '3');
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, USEABLE_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@pro, @depot, @location, @batch, @qty, @qty, 0, 3, @qty * 3);
            INSERT INTO dbo.SYSDD_BUTTON (USER_ID, M_IDX, BUTTON_KEY, ALLOW_TAG)
                VALUES (@user, @module, N'inventory-freeze', 1), (@user, @module, N'inventory-unfreeze', 1);
            """, ("@pro", Product), ("@depot", Depot), ("@location", Location), ("@batch", Batch), ("@qty", StockQty),
            ("@user", TestUser), ("@module", ModuleId));
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
    }

    private static async Task CleanupAsync(SqlConnection connection)
    {
        await ExecAsync(connection, """
            DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE M_IDX = @module AND ACTION = N'ACTION';
            DELETE FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID = @user;
            DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            """, ("@module", ModuleId), ("@pro", Product), ("@user", TestUser));
    }

    // ===== ① 冻结：可用量下降、在库不变、列按口径同步（顺带治愈未初始化） =====

    [Fact]
    public async Task 冻结_可用量下降而在库数量不变()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        // 审计是追加型的：先取基线，只认本次冻结新增的那条前后值
        var auditBaseline = await ScalarAsync<long>(connection, "SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT;");

        var result = await RunAsync(InventoryFreezeHandler.ActionKey, "30", "质量扣货");

        Assert.True(result.Status == DocumentActionStatus.Ok, result.ErrorMessage);
        Assert.Equal(DocumentActionOutcome.Refreshed, result.Result!.Outcome);
        Assert.Contains("可用量 0 → 70", result.Result.Message);

        // 在库数量没动，可用量列被同步（夹具故意把它写成 0：这次写入把它治回正确值）
        Assert.Equal(StockQty, await ScalarAsync<double>(connection,
            "SELECT QTY FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
        Assert.Equal(70d, await ScalarAsync<double>(connection,
            "SELECT USEABLE_QTY FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
        Assert.Equal(30d, await ScalarAsync<double>(connection,
            "SELECT SUM(FREEZE_QTY) FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = @pro AND RTRIM(STATUS) = N'A';",
            ("@pro", Product)));

        // 与可用量服务同源：列上的值就是服务算出来的值
        var service = await InventoryAvailabilityService.ForSlotsAsync(connection, null,
            [new InventoryAvailabilityService.SlotKey(Product, Depot, Location, Batch)], CancellationToken.None);
        Assert.Equal(70d, service[new InventoryAvailabilityService.SlotKey(Product, Depot, Location, Batch)].Available);

        // 审计有前后值：冻结那一条的 USEABLE_QTY 旧值/新值要能对上（0 → 70）
        await using (var command = new SqlCommand("""
            SELECT RTRIM(c.FIELD_NAME), c.OLD_VALUE, c.NEW_VALUE
              FROM dbo.AUDIT_FIELD_CHANGE c JOIN dbo.AUDIT_EVENT e ON e.EVENT_ID = c.EVENT_ID
             WHERE e.EVENT_ID > @baseline
               AND e.M_IDX = @module AND e.RESOURCE_KEY = @key AND RTRIM(c.FIELD_NAME) = N'USEABLE_QTY';
            """, connection))
        {
            command.Parameters.AddWithValue("@baseline", auditBaseline);
            command.Parameters.AddWithValue("@module", ModuleId);
            command.Parameters.AddWithValue("@key", RecordKey);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("0", reader.GetString(1));
            Assert.Equal("70", reader.GetString(2));
            Assert.False(await reader.ReadAsync());
        }
    }

    // ===== ② 解冻：整笔与部分释放都能回升 =====

    [Fact]
    public async Task 解冻_可用量回升()
    {
        await RunAsync(InventoryFreezeHandler.ActionKey, "30", "质量扣货");
        var partial = await RunAsync(InventoryUnfreezeHandler.ActionKey, "10", "复检合格");
        Assert.True(partial.Status == DocumentActionStatus.Ok, partial.ErrorMessage);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(80d, await ScalarAsync<double>(connection,
            "SELECT USEABLE_QTY FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
        // 部分释放：那一笔还在（20），状态仍有效
        Assert.Equal(20d, await ScalarAsync<double>(connection,
            "SELECT FREEZE_QTY FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));

        var rest = await RunAsync(InventoryUnfreezeHandler.ActionKey, "20", "全部放行");
        Assert.True(rest.Status == DocumentActionStatus.Ok, rest.ErrorMessage);
        Assert.Equal(100d, await ScalarAsync<double>(connection,
            "SELECT USEABLE_QTY FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
        // 整笔释放：状态置 C
        Assert.Equal("C", await ScalarAsync<string>(connection,
            "SELECT RTRIM(STATUS) FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
    }

    // ===== ③ 解冻超过冻结合计 ⇒ 拒 =====

    [Fact]
    public async Task 解冻超过冻结合计被拒()
    {
        await RunAsync(InventoryFreezeHandler.ActionKey, "30", "质量扣货");
        var result = await RunAsync(InventoryUnfreezeHandler.ActionKey, "31", "多解了");

        Assert.Equal(DocumentActionStatus.Failed, result.Status);
        Assert.Contains("超过该格当前冻结合计", result.ErrorMessage);
    }

    // ===== ④ 未授权：服务端另行鉴权 =====

    [Fact]
    public async Task 未授权账号直调被拒()
    {
        var result = await RunAsync(InventoryFreezeHandler.ActionKey, "30", "质量扣货", OutsiderUser);

        Assert.Equal(DocumentActionStatus.Forbidden, result.Status);
        Assert.Equal(DocumentActionErrorCodes.Forbidden, result.ErrorCode);
    }

    // ===== ⑤ 探路不写 =====

    [Fact]
    public async Task 探路_只说会变到多少_一个字都不写()
    {
        var result = await RunAsync(InventoryFreezeHandler.ActionKey, "30", "质量扣货", TestUser, confirm: false);

        Assert.Equal(DocumentActionStatus.Ok, result.Status);
        Assert.True(result.RequiresConfirmation);
        Assert.Contains("未改动", result.Result!.Message);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(0, await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
        Assert.Equal(0d, await ScalarAsync<double>(connection,
            "SELECT USEABLE_QTY FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
    }

    // ===== 装配（与 Program.cs 同源） =====

    private DocumentActionExecutor Executor() =>
        new(_connections,
            new DocumentActionRegistry(
                [new InventoryFreezeHandler(_audit), new InventoryUnfreezeHandler(_audit)],
                NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(_connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            _audit,
            NullLogger<DocumentActionExecutor>.Instance);

    private Task<DocumentActionExecution> RunAsync(string actionKey, string quantity, string reason,
        string? user = null, bool confirm = true) =>
        Executor().ExecuteAsync(Definition(), Form(), actionKey,
            new DocumentActionRequest([Product, Depot, Location, Batch],
                JsonSerializer.SerializeToElement(new Dictionary<string, string>
                {
                    [InventoryFreezeHandler.QuantityParameter] = quantity,
                    [InventoryFreezeHandler.ReasonParameter] = reason,
                }), confirm),
            user ?? TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

    private static WorkbenchDefinition Definition() =>
        new(ModuleId: ModuleId, Title: "料件库存资料", MasterTable: MasterTable, DetailTable: null,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: false, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: [InventoryQueryService.ProductColumn, InventoryQueryService.DepotColumn,
                InventoryQueryService.LocationColumn, InventoryQueryService.BatchColumn],
            DetailNoFields: "", HasWorkflow: false, UserId: TestUser, ExecTag: "Z",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            BusinessActions: JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    seq = 1,
                    eventCode = "MANUAL",
                    effectKey = InventoryFreezeHandler.ActionKey,
                    enabled = true,
                    label = "库存冻结",
                    confirmTag = true,
                    failMode = "BLOCK",
                    condition = (string?)null,
                    @params = """{"fields":[{"key":"quantity","label":"冻结数量","type":"number","required":true},{"key":"reason","label":"原因","type":"string","maxLength":200}]}""",
                },
                new
                {
                    seq = 2,
                    eventCode = "MANUAL",
                    effectKey = InventoryUnfreezeHandler.ActionKey,
                    enabled = true,
                    label = "库存解冻",
                    confirmTag = true,
                    failMode = "BLOCK",
                    condition = (string?)null,
                    @params = """{"fields":[{"key":"quantity","label":"解冻数量","type":"number","required":true},{"key":"reason","label":"原因","type":"string","maxLength":200}]}""",
                },
            }));

    private static FormDefinition Form() =>
        new(ModuleId, "料件库存资料", MasterTable, null, false, true, "view",
            [
                new FormFieldDefinition("PRO_NO", "料号", "nchar", 30, null, false, null, null, null, false, true,
                    false, false, null, [], true, false, false, false, false, false, 30),
                new FormFieldDefinition("DEPOT_ID", "库别", "nchar", 10, null, false, null, null, null, false, true,
                    false, false, null, [], true, false, false, false, false, false, 10),
                new FormFieldDefinition("LOCATION_NO", "库位", "nvarchar", 30, null, false, null, null, null, false, true,
                    false, false, null, [], true, false, false, false, false, false, 30),
                new FormFieldDefinition("BATCH_NO", "批次", "nchar", 30, null, false, null, null, null, false, true,
                    false, false, null, [], true, false, false, false, false, false, 30),
            ],
            [], [InventoryQueryService.ProductColumn, InventoryQueryService.DepotColumn,
                InventoryQueryService.LocationColumn, InventoryQueryService.BatchColumn],
            string.Empty, string.Empty);

    private static DbConnectionFactory Connections() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build());

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
