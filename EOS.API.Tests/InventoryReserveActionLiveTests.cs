using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
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
/// 库存预留与释放的真库验收（D7-⑥ / WS-22）。
///
/// 预留与冻结共用格子身份与释放原语，差别只有一处、也是本质的一处：**预留必须带来源**，
/// 它的生命周期归来源所有。于是本用例盯的是**两条释放路径**：
///   ① **惰性判据**（WS-20）——来源结案后，**算出来的**可用量立刻回升（不用等任何人跑作业）；
///   ② **结案钩子**（本段）——把占用行与余额行上的 `USEABLE_QTY` **列**一起收干净。
/// 顺序上先有 ①、后有 ②：中间那一小段正是"算得对但存得脏"，也正是 WS-25 门禁要断言的那条等式。
///
/// 五条：① 占料 ⇒ 可用量下降；② **来源结案**：先验惰性判据已救"算"、再跑钩子救"存"；
/// ③ 手工释放兜底（部分释放）；④ 预留不带来源 ⇒ 拒；⑤ 探路不写。
/// 判别性：去掉钩子里的释放动作 ⇒ 第②条变红。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class InventoryReserveActionLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int StockModule = 1303;
    private const int SourceModule = 1502;
    private const string MasterTable = "INV_PRO_DEPOT";
    private const string Product = "ZZRSVPRO1";
    private const string Depot = "CP";
    private const string Location = "-";
    private const string Batch = "";
    private const string ProduceType = "ZZRSV";
    private const string ProduceNo = "ZZRSV-MO1";
    private const string TestUser = "ZZRSV0001";
    private const double StockQty = 100d;
    private const string RecordKey = Product + "," + Depot + "," + Location + "," + Batch;
    private const string SourceKey = ProduceType + "," + ProduceNo;

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
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE) VALUES (@pro, N'ZZRSV 预留料件', N'规格', '3');
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, USEABLE_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@pro, @depot, @location, @batch, @qty, @qty, @qty, 3, @qty * 3);
            INSERT INTO dbo.MOC_PRODUCE_M (PRODUCE_TYPE, PRODUCE_NO, FINISHED_TAG)
                VALUES (N'ZZRSV', N'ZZRSV-MO1', 0);
            INSERT INTO dbo.SYSDD_BUTTON (USER_ID, M_IDX, BUTTON_KEY, ALLOW_TAG)
                VALUES (@user, @module, N'inventory-reserve', 1), (@user, @module, N'inventory-release', 1);
            """, ("@pro", Product), ("@depot", Depot), ("@location", Location), ("@batch", Batch), ("@qty", StockQty),
            ("@user", TestUser), ("@module", StockModule));
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
            DELETE FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            DELETE FROM dbo.MOC_PRODUCE_M WHERE PRODUCE_TYPE = N'ZZRSV';
            DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID = @user;
            DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;
            """, ("@module", StockModule), ("@pro", Product), ("@user", TestUser));
    }

    // ===== ① 占料 ⇒ 可用量下降 =====

    [Fact]
    public async Task 占料后可用量下降()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var auditBaseline = await ScalarAsync<long>(connection, "SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT;");

        var result = await RunReserveAsync("20");

        Assert.True(result.Status == DocumentActionStatus.Ok, result.ErrorMessage);
        Assert.Contains("可用量 100 → 80", result.Result!.Message);
        Assert.Equal(80d, await ScalarAsync<double>(connection,
            "SELECT USEABLE_QTY FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
        Assert.Equal(StockQty, await ScalarAsync<double>(connection,
            "SELECT QTY FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
        Assert.Equal(SourceModule.ToString(), await ScalarAsync<string>(connection,
            "SELECT RTRIM(SOURCE_TYPE) FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
        Assert.Equal(20d, await ScalarAsync<double>(connection,
            "SELECT RESERVE_QTY FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
        // 占料留痕：基线之上本格子恰好一条（按本格资源键限定，别的格子不算）
        Assert.Equal(1, await AuditCountAsync(auditBaseline, StockModule, RecordKey, InventoryReserveHandler.ActionKey));
    }

    // ===== ② 来源结案：惰性判据救"算"，钩子救"存" =====

    [Fact]
    public async Task 来源结案时自动释放并同步列()
    {
        await RunReserveAsync("20");
        await ExecOnceAsync(
            "UPDATE dbo.MOC_PRODUCE_M SET FINISHED_TAG = 1 WHERE PRODUCE_TYPE = N'ZZRSV' AND PRODUCE_NO = N'ZZRSV-MO1';");

        // 钩子的审计就写在紧接着这一步：基线取在此处，正好只圈住它
        var auditBaseline = await AuditBaselineAsync();

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        // ① 惰性判据（WS-20）：不用等任何作业，**算出来的**可用量已经回升
        var service = await InventoryAvailabilityService.ForSlotsAsync(connection, null,
            [new InventoryAvailabilityService.SlotKey(Product, Depot, Location, Batch)], CancellationToken.None);
        Assert.Equal(StockQty, service[new InventoryAvailabilityService.SlotKey(Product, Depot, Location, Batch)].Available);
        // ……但余额行上的**列**还停在旧值（"算得对、存得脏"——正是门禁要守的那条等式）
        Assert.Equal(80d, await ScalarAsync<double>(connection,
            "SELECT USEABLE_QTY FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));

        // ② 结案钩子（本段）：把占用行与列一起收干净
        var released = await RunReleaseHookAsync();
        Assert.Equal(1, released);
        Assert.Equal("C", await ScalarAsync<string>(connection,
            "SELECT RTRIM(STATUS) FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
        Assert.Equal(StockQty, await ScalarAsync<double>(connection,
            "SELECT USEABLE_QTY FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));

        // 钩子留了痕（谁释放的、释放了哪些格子）：基线之上、来源单这一条
        Assert.Equal(1, await AuditCountAsync(auditBaseline, SourceModule, SourceKey, "UNFREEZE"));
    }

    // ===== ③ 手工释放兜底 =====

    [Fact]
    public async Task 手工释放兜底_部分释放()
    {
        await RunReserveAsync("20");
        var result = await RunAsync(InventoryReleaseHandler.ActionKey, "8", null);

        Assert.True(result.Status == DocumentActionStatus.Ok, result.ErrorMessage);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        // 预留 20 后可用量 80；手工释放 8 ⇒ 100 − 12 = 88
        Assert.Equal(88d, await ScalarAsync<double>(connection,
            "SELECT USEABLE_QTY FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
        Assert.Equal(12d, await ScalarAsync<double>(connection,
            "SELECT RESERVE_QTY FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
    }

    // ===== ④ 预留必须带来源 =====

    [Fact]
    public async Task 预留不带来源被拒()
    {
        var result = await RunAsync(InventoryReserveHandler.ActionKey, "20", sourceNo: string.Empty);

        // 两道闸都算数：框架层的"必填参数"先拒（`PARAM_REQUIRED` 落在 FieldErrors 上），
        // 即使漏过去，处理器里的 ReadRequired 也会拒——这里断言的是"确实被拒且指到那个参数"。
        Assert.Equal(DocumentActionStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FieldErrors!, error => error.Field == InventoryReserveHandler.SourceNoParameter);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(0, await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
    }

    // ===== ⑤ 探路不写 =====

    [Fact]
    public async Task 探路_不写占用()
    {
        // 探路同样要带齐**必填参数**（框架在调处理器之前就校验声明）：少了来源那两个，
        // 拿到的不是"将会怎样"而是 VALIDATION_FAILED。
        var result = await RunAsync(InventoryReserveHandler.ActionKey, "20", ProduceNo, Confirm: false);

        Assert.Equal(DocumentActionStatus.Ok, result.Status);
        Assert.True(result.RequiresConfirmation);
        Assert.Contains("未改动", result.Result!.Message);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(0, await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", Product)));
    }

    // ===== 装配 =====

    private Task<DocumentActionExecution> RunReserveAsync(string quantity) =>
        RunAsync(InventoryReserveHandler.ActionKey, quantity, ProduceNo);

    private Task<DocumentActionExecution> RunAsync(string actionKey, string quantity, string? sourceNo = null, bool Confirm = true)
    {
        var parameters = new Dictionary<string, string>
        {
            [InventoryFreezeHandler.QuantityParameter] = quantity,
        };
        if (sourceNo is not null)
        {
            parameters[InventoryReserveHandler.SourceTypeParameter] = SourceModule.ToString();
            parameters[InventoryReserveHandler.SourceNoParameter] = sourceNo;
            parameters[InventoryFreezeHandler.ReasonParameter] = "ZZRSV 用例";
        }

        return Executor().ExecuteAsync(Definition(), Form(), actionKey,
            new DocumentActionRequest([Product, Depot, Location, Batch],
                JsonSerializer.SerializeToElement(parameters), Confirm),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);
    }

    /// <summary>跑一次"来源结案"钩子（配置侧挂在模块 1502 的 `ENDCASE` 上，这里直接驱动它）。</summary>
    private async Task<int> RunReleaseHookAsync()
    {
        await using var connection = await OpenAsync();
        var handler = new InventoryReleaseBySourceHandler(_audit);
        var context = new ServiceEffectContext(
            connection,
            null!,
            new ModuleEffectPlan(SourceModule, "MOC_PRODUCE_M", null, "test",
                ["PRODUCE_TYPE", "PRODUCE_NO"], [], []),
            new EffectActionPlan(7, "ENDCASE", InventoryReleaseBySourceHandler.EffectKeyName, "结案释放预留",
                true, "BLOCK", null, null, null, []),
            EffectEvent.Endcase,
            SourceKey,
            [ProduceType, ProduceNo],
            TestUser);
        return await handler.ExecuteAsync(context, CancellationToken.None);
    }

    private DocumentActionExecutor Executor() =>
        new(_connections,
            new DocumentActionRegistry(
                [new InventoryReserveHandler(_audit), new InventoryReleaseHandler(_audit)],
                NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(_connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            _audit,
            NullLogger<DocumentActionExecutor>.Instance);

    private static WorkbenchDefinition Definition() =>
        new(ModuleId: StockModule, Title: "料件库存资料", MasterTable: MasterTable, DetailTable: null,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: false, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: [InventoryQueryService.ProductColumn, InventoryQueryService.DepotColumn,
                InventoryQueryService.LocationColumn, InventoryQueryService.BatchColumn],
            DetailNoFields: "", HasWorkflow: false, UserId: TestUser, ExecTag: "Z",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            BusinessActions: JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    seq = 3,
                    eventCode = "MANUAL",
                    effectKey = InventoryReserveHandler.ActionKey,
                    enabled = true,
                    label = "库存预留",
                    confirmTag = true,
                    failMode = "BLOCK",
                    condition = (string?)null,
                    @params = """{"fields":[{"key":"quantity","label":"预留数量","type":"number","required":true},{"key":"sourceType","label":"来源类型（模块号）","type":"string","required":true,"maxLength":20},{"key":"sourceNo","label":"来源单号","type":"string","required":true,"maxLength":40},{"key":"reason","label":"原因","type":"string","maxLength":200}]}""",
                },
                new
                {
                    seq = 4,
                    eventCode = "MANUAL",
                    effectKey = InventoryReleaseHandler.ActionKey,
                    enabled = true,
                    label = "手工释放预留",
                    confirmTag = true,
                    failMode = "BLOCK",
                    condition = (string?)null,
                    @params = """{"fields":[{"key":"quantity","label":"释放数量","type":"number","required":true},{"key":"reason","label":"原因","type":"string","maxLength":200}]}""",
                },
            }));

    private static FormDefinition Form() =>
        new(StockModule, "料件库存资料", MasterTable, null, false, true, "view", [],
            [], [InventoryQueryService.ProductColumn, InventoryQueryService.DepotColumn,
                InventoryQueryService.LocationColumn, InventoryQueryService.BatchColumn],
            string.Empty, string.Empty);

    private static DbConnectionFactory Connections() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build());

    /// <summary>审计是追加型的：取当前最大事件号作基线，之后只看基线之上的增量。</summary>
    private static async Task<long> AuditBaselineAsync()
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<long>(connection, "SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT;");
    }

    /// <summary>基线之上、指定（模块 + 资源键 + 动作）的审计条数。</summary>
    private static async Task<int> AuditCountAsync(long baseline, int module, string recordKey, string action)
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<int>(connection, """
            SELECT COUNT(*) FROM dbo.AUDIT_EVENT
             WHERE EVENT_ID > @baseline AND M_IDX=@module AND RESOURCE_KEY=@key AND RTRIM(ACTION)=@action;
            """,
            ("@baseline", baseline), ("@module", module), ("@key", recordKey), ("@action", action));
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>夹具的一次性写：自己开连接、自己关（共享连接不能在这里被释放）。</summary>
    private static async Task ExecOnceAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection, sql, parameters);
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
