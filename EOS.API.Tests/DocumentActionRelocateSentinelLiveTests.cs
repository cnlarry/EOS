using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Data.Effects;
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
/// P2 真库验收：库存策略（110310）的自定义按钮「哨兵存量归位」（`relocate-sentinel`）。
///
/// 对上 ADR-018 §11 WS-6 的口径：归位是**带参数的操作**（`PARAM_STRUCT` 声明 `relocateTo`），
/// 不再专属"保存策略"那条路——不改策略配置也能把一个库别记在『未指定位置』上的存量
/// 整批改记到指定库位，且**库别总量不变**（改的是位置，不是货）。
///
/// 夹具自造 ZZ 前缀料号的哨兵行，用完即删：不碰真实库存。
/// 需要 EOS_ERP_TEST_CONNECTION。
/// </summary>
[Collection("live-database")]
public sealed class DocumentActionRelocateSentinelLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 110310;
    private const string MasterTable = "DEPOT_STOCK_POLICY";
    private const string StockTable = "INV_PRO_DEPOT";
    private const string TestUser = "ZZREL0001";
    private const string TestProduct = "ZZRELPRO01";
    private const string TestLocation = "ZZ-REL-LOC";
    private const double StartQty = 100d;

    private DbConnectionFactory _connections = null!;
    private string _depot = string.Empty;
    private string _targetLocation = string.Empty;

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

    /// <summary>借一个真实库别（库位主档里挂过的），其余主档全部自造。</summary>
    private static async Task<string> PickDepotAsync()
    {
        await using var connection = await OpenAsync();
        var depot = await ScalarAsync<string>(connection,
            "SELECT TOP 1 LTRIM(RTRIM(DEPOT_ID)) FROM dbo.DEPOT_LOCATION ORDER BY DEPOT_ID;");
        return depot ?? throw new InvalidOperationException("库位主档里没有库别，无法验证归位。");
    }

    public async Task InitializeAsync()
    {
        _depot = await PickDepotAsync();
        _targetLocation = TestLocation;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        _connections = new DbConnectionFactory(configuration);

        await using var connection = await OpenAsync();
        // 归位的目标必须是启用中的库位：本库只有哨兵位，故自造一个 ZZ 库位，用完删除。
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@depot AND LOCATION_NO=@loc)
                INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID,LOCATION_NO,LOCATION_TYPE,STORAGE_TYPE,STATUS,CONFIRM_TAG)
                VALUES (@depot,@loc,N'STAGE',N'BULK',N'A',1);
            """,
            ("@depot", _depot), ("@loc", _targetLocation));
        // 动作要求主表里真有这一行策略（ wired two-hop 求值之外，"这行存在"本身就是前提）。
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.{MasterTable} WHERE DEPOT_ID=@depot)
                INSERT INTO dbo.{MasterTable} (DEPOT_ID,LOCATION_MODE,STORAGE_MODE,BATCH_MODE,CAPACITY_MODE,
                                               MIX_PRODUCT,MIX_BATCH,MONTH_CLOSE_BY_BATCH,MONTH_CLOSE_BY_LOCATION)
                VALUES (@depot,0,N'RANDOM',0,0,1,1,0,0);
            """,
            ("@depot", _depot));
        // 自造哨兵行：一批 ZZ 料号的货记在『未指定位置』上（库存表里 '-' 就是"没记位置"）。
        await ExecAsync(connection,
            $"""
            DELETE FROM dbo.{StockTable} WHERE LTRIM(RTRIM(PRO_NO))=@pro AND DEPOT_ID=@depot;
            INSERT INTO dbo.{StockTable} (PRO_NO,DEPOT_ID,LOCATION_NO,BATCH_NO,QTY,CREATE_PERSON,CREATE_DATE,CONFIRM_TAG,CI)
            VALUES (@pro,@depot,N'-',N'',@qty,N'DbUp',SYSDATETIME(),0,N'');
            """,
            ("@pro", TestProduct), ("@depot", _depot), ("@qty", StartQty));
        await ExecAsync(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user AND M_IDX=@module AND BUTTON_KEY=@key)
                INSERT INTO dbo.SYSDD_BUTTON (USER_ID,M_IDX,BUTTON_KEY,ALLOW_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@user,@module,@key,1,N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
            """,
            ("@user", TestUser), ("@module", ModuleId), ("@key", RelocateSentinelHandler.ActionKey));
    }

    public async Task DisposeAsync()
    {
        await using var connection = await OpenAsync();
        // 归位按**整个库别**的哨兵行算：本库别的其它料号也可能被补出目标库位的零量行，先按目标库位清干净，
        // 再删本用例自造的那条哨兵行。
        await ExecAsync(connection,
            $"DELETE FROM dbo.{StockTable} WHERE DEPOT_ID=@depot AND LTRIM(RTRIM(LOCATION_NO))=@loc;",
            ("@depot", _depot), ("@loc", _targetLocation));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{StockTable} WHERE LTRIM(RTRIM(PRO_NO))=@pro AND DEPOT_ID=@depot;",
            ("@pro", TestProduct), ("@depot", _depot));
        await ExecAsync(connection,
            "DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@depot AND LOCATION_NO=@loc;",
            ("@depot", _depot), ("@loc", _targetLocation));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{MasterTable} WHERE DEPOT_ID=@depot;", ("@depot", _depot));
        await ExecAsync(connection, "DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user;", ("@user", TestUser));
        await ExecAsync(connection,
            "DELETE FROM dbo.AUDIT_EVENT WHERE ACTION=@action AND RESOURCE_KEY=@key;",
            ("@action", RelocateSentinelHandler.ActionKey), ("@key", _depot));
        await ExecAsync(connection,
            "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE MODULE_ID=@module AND ACTION=N'ACTION';", ("@module", ModuleId));
    }

    // ===== 装配（与 Program.cs 同源：真实服务） =====

    private RelocateSentinelHandler Handler()
    {
        var provider = new WorkbenchDefinitionProvider(_connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(_connections, new HttpContextAccessor(), provider,
            Options.Create(new AuditSettings()));
        return new RelocateSentinelHandler(new DepotStockPolicyService(_connections, auditWriter));
    }

    private DocumentActionExecutor Executor() =>
        new(_connections,
            new DocumentActionRegistry([Handler()], NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(_connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            new WorkbenchAuditWriter(_connections, new HttpContextAccessor(),
                new WorkbenchDefinitionProvider(_connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
                Options.Create(new AuditSettings())),
            NullLogger<DocumentActionExecutor>.Instance);

    private static WorkbenchDefinition Definition() =>
        new(ModuleId: ModuleId, Title: "库存策略", MasterTable: MasterTable, DetailTable: null,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["DEPOT_ID"], DetailNoFields: string.Empty, HasWorkflow: false,
            UserId: TestUser, ExecTag: "Z",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DEPOT_ID" },
            BusinessActions: JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    seq = 1,
                    eventCode = "MANUAL",
                    effectKey = RelocateSentinelHandler.ActionKey,
                    enabled = true,
                    label = "哨兵存量归位",
                    confirmTag = true,
                    failMode = "BLOCK",
                    @params = new
                    {
                        fields = new[]
                        {
                            new
                            {
                                key = RelocateSentinelHandler.TargetParameter,
                                label = "目标库位",
                                type = "string",
                                required = true,
                                maxLength = 50,
                            },
                        },
                    },
                },
            }));

    private static FormDefinition Form() =>
        new(ModuleId, "库存策略", MasterTable, null, true, true, "view", [], [],
            ["DEPOT_ID"], string.Empty, string.Empty);

    private Task<DocumentActionExecution> RunAsync(string? relocateTo, bool confirm = true) =>
        Executor().ExecuteAsync(Definition(), Form(), RelocateSentinelHandler.ActionKey,
            new DocumentActionRequest(
                [_depot],
                relocateTo is null ? null : JsonSerializer.SerializeToElement(new Dictionary<string, string>
                {
                    [RelocateSentinelHandler.TargetParameter] = relocateTo,
                }),
                Confirm: confirm),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

    private async Task<double> DepotTotalAsync()
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<double>(connection,
            $"SELECT ISNULL(SUM(ISNULL(QTY,0)),0) FROM dbo.{StockTable} WHERE DEPOT_ID=@depot AND LTRIM(RTRIM(PRO_NO))=@pro;",
            ("@depot", _depot), ("@pro", TestProduct));
    }

    private async Task<double> QtyAtAsync(string location)
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<double>(connection,
            $"""
            SELECT ISNULL(SUM(ISNULL(QTY,0)),0) FROM dbo.{StockTable}
            WHERE DEPOT_ID=@depot AND LTRIM(RTRIM(PRO_NO))=@pro AND LTRIM(RTRIM(LOCATION_NO))=@loc;
            """,
            ("@depot", _depot), ("@pro", TestProduct), ("@loc", location));
    }

    // ===== 用例 =====

    [Fact]
    public async Task MovesSentinelStock_ToTargetLocation_AndKeepsDepotTotal()
    {
        var beforeTotal = await DepotTotalAsync();
        Assert.Equal(StartQty, await QtyAtAsync("-"), 6);

        var result = await RunAsync(_targetLocation);

        Assert.True(result.Status == DocumentActionStatus.Ok, result.ErrorMessage);
        Assert.Equal(DocumentActionOutcome.Refreshed, result.Result!.Outcome);
        Assert.Contains("已完成归位", result.Result.Message!);
        // 位置改记：哨兵行清零（行保留），目标库位吃下这批货，库别总量一分不差
        Assert.Equal(0d, await QtyAtAsync("-"), 6);
        Assert.Equal(StartQty, await QtyAtAsync(_targetLocation), 6);
        Assert.Equal(beforeTotal, await DepotTotalAsync(), 6);
    }

    [Fact]
    public async Task ProbeOnly_ReportsAndWritesNothing()
    {
        var beforeTotal = await DepotTotalAsync();

        var probe = await RunAsync(_targetLocation, confirm: false);

        Assert.Equal(DocumentActionStatus.Ok, probe.Status);
        Assert.True(probe.RequiresConfirmation);
        // 这一路的写全在调用方事务里，框架"执行后回滚"即可，处理器不必自己实现预检
        Assert.Equal(StartQty, await QtyAtAsync("-"), 6);
        Assert.Equal(0d, await QtyAtAsync(_targetLocation), 6);
        Assert.Equal(beforeTotal, await DepotTotalAsync(), 6);
    }

    [Fact]
    public async Task WithoutTargetParameter_IsRefused()
    {
        var result = await RunAsync(null);

        Assert.Equal(DocumentActionStatus.ValidationFailed, result.Status);
        Assert.NotNull(result.FieldErrors);
        Assert.NotEmpty(result.FieldErrors!);
    }

    [Fact]
    public async Task UnknownTargetLocation_IsRefused()
    {
        var result = await RunAsync("ZZNOWHERE01");

        Assert.Equal(DocumentActionStatus.Failed, result.Status);
        Assert.Contains("不存在或已停用", result.ErrorMessage!);
        Assert.Equal(StartQty, await QtyAtAsync("-"), 6);
    }
}
