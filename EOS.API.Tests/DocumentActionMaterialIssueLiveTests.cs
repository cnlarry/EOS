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
/// 领料单「按库别取料」（`material-issue-allocate`）真库验收。
///
/// 口径：SEND_QTY 取 min(需求, 库别库存合计)——同一（料号, 库别）的多库位行先汇总再比，
/// 无库存记录的行按 0 计；待办表按（料号, 序号）顺序逐行分配，分完即停；探路零写入（含审计）。
///
/// 夹具全自造（ZZMI 仓 + 库位 + 库存 + 领料单 + 待办行），用完即删。
/// 需要 EOS_ERP_TEST_CONNECTION。
/// </summary>
[Collection("live-database")]
public sealed class DocumentActionMaterialIssueLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1503;
    private const string MasterTable = "MOC_GET_M";
    private const string DetailTable = "MOC_GET_D";
    private const string MoreTable = "MOC_GET_MORE";
    private const string StockTable = "INV_PRO_DEPOT";
    private const string TestType = "ZZMI";
    private const string TestNo = "ZZMI00001";
    private const string TestUser = "ZZMI00001";
    private const string TestDepot = "ZZMI0001";
    private const string BinA = "ZZMI-A1";
    private const string BinB = "ZZMI-A2";
    private const string ProductA = "ZZMIAPRO1";
    private const string ProductB = "ZZMIBPRO1";
    private const string ProductC = "ZZMICPRO1";

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

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            """
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@d;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@d;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@d;
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@d, N'取料测试仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, SEQ_NO, STATUS)
                VALUES (@d, N'-', NULL, N'/-', N'BIN', 0, N'A'),
                       (@d, @a,  NULL, N'/ZZMI-A1', N'BIN', 1, N'A'),
                       (@d, @b,  NULL, N'/ZZMI-A2', N'BIN', 2, N'A');
            """,
            ("@d", TestDepot), ("@a", BinA), ("@b", BinB));
        // A 料分两行（7 + 3 = 10），B 料 2，C 料无库存行
        await ExecAsync(connection,
            $"""
            INSERT INTO dbo.{StockTable} (PRO_NO,DEPOT_ID,LOCATION_NO,BATCH_NO,QTY,CREATE_PERSON,CREATE_DATE,CONFIRM_TAG,CI)
            VALUES (@pa,@d,@a,N'',7,N'DbUp',SYSDATETIME(),0,N''),
                   (@pa,@d,@b,N'',3,N'DbUp',SYSDATETIME(),0,N''),
                   (@pb,@d,@a,N'',2,N'DbUp',SYSDATETIME(),0,N'');
            """,
            ("@pa", ProductA), ("@pb", ProductB), ("@d", TestDepot),
            ("@a", BinA), ("@b", BinB));
        // 领料单：A 行需求 10，B 行需求 5，C 行需求 4（无库存）
        await ExecAsync(connection,
            $"""
            INSERT INTO dbo.{MasterTable} (GET_TYPE,GET_NO,GET_DATE,CREATE_PERSON,CREATE_DATE,CI)
            VALUES (@type,@no,SYSDATETIME(),N'DbUp',SYSDATETIME(),N'');
            INSERT INTO dbo.{DetailTable} (GET_TYPE,GET_NO,SERIAL_NO,PRO_NO,DEPOT_ID,QTY,SEND_QTY)
            VALUES (@type,@no,1,@pa,@d,10,0),
                   (@type,@no,2,@pb,@d,5,0),
                   (@type,@no,3,@pc,@d,4,0);
            INSERT INTO dbo.{MoreTable} (GET_TYPE,GET_NO,SERIAL_NO,PRO_NO,REQUIRE_QTY,QTY)
            VALUES (@type,@no,1,@pa,6,0),
                   (@type,@no,2,@pa,6,0),
                   (@type,@no,3,@pb,4,0);
            """,
            ("@type", TestType), ("@no", TestNo), ("@d", TestDepot),
            ("@pa", ProductA), ("@pb", ProductB), ("@pc", ProductC));
        await ExecAsync(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user AND M_IDX=@module AND BUTTON_KEY=@key)
                INSERT INTO dbo.SYSDD_BUTTON (USER_ID,M_IDX,BUTTON_KEY,ALLOW_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@user,@module,@key,1,N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
            """,
            ("@user", TestUser), ("@module", ModuleId), ("@key", MaterialIssueAllocateHandler.ActionKey));
    }

    public async Task DisposeAsync()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"DELETE FROM dbo.{MoreTable} WHERE GET_TYPE=@type AND GET_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{DetailTable} WHERE GET_TYPE=@type AND GET_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{MasterTable} WHERE GET_TYPE=@type AND GET_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{StockTable} WHERE DEPOT_ID=@d;", ("@d", TestDepot));
        await ExecAsync(connection,
            "DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@d;", ("@d", TestDepot));
        await ExecAsync(connection,
            "DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@d;", ("@d", TestDepot));
        await ExecAsync(connection, "DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user;", ("@user", TestUser));
        await ExecAsync(connection,
            "DELETE FROM dbo.AUDIT_FIELD_CHANGE WHERE EVENT_ID IN (SELECT EVENT_ID FROM dbo.AUDIT_EVENT WHERE ACTION=@action);",
            ("@action", MaterialIssueAllocateHandler.ActionKey));
        await ExecAsync(connection,
            "DELETE FROM dbo.AUDIT_EVENT WHERE ACTION=@action;", ("@action", MaterialIssueAllocateHandler.ActionKey));
        await ExecAsync(connection,
            "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE M_IDX=@module AND ACTION=N'ACTION';", ("@module", ModuleId));
    }

    // ===== 装配（与 Program.cs 同源：真实服务） =====

    private static DbConnectionFactory Connections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        return new DbConnectionFactory(configuration);
    }

    private DocumentActionExecutor Executor()
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        return new(connections,
            new DocumentActionRegistry([new MaterialIssueAllocateHandler()], NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider,
                Options.Create(new AuditSettings())),
            NullLogger<DocumentActionExecutor>.Instance);
    }

    private static WorkbenchDefinition Definition() =>
        new(ModuleId: ModuleId, Title: "领料单", MasterTable: MasterTable, DetailTable: DetailTable,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["GET_TYPE", "GET_NO"], DetailNoFields: string.Empty, HasWorkflow: false,
            UserId: TestUser, ExecTag: "Z",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GET_TYPE" },
            BusinessActions: JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    seq = 1,
                    eventCode = "MANUAL",
                    effectKey = MaterialIssueAllocateHandler.ActionKey,
                    enabled = true,
                    label = "按库别取料",
                    confirmTag = true,
                    failMode = "BLOCK",
                },
            }));

    private static FormDefinition Form() =>
        new(ModuleId, "领料单", MasterTable, DetailTable, true, true, "view", [], [],
            ["GET_TYPE", "GET_NO"], string.Empty, string.Empty);

    private Task<DocumentActionExecution> RunAsync(bool confirm = true) =>
        Executor().ExecuteAsync(Definition(), Form(), MaterialIssueAllocateHandler.ActionKey,
            new DocumentActionRequest([TestType, TestNo], null, Confirm: confirm),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

    private async Task<IReadOnlyDictionary<string, double>> SendQuantitiesAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT LTRIM(RTRIM(PRO_NO)), ISNULL(SEND_QTY,0) FROM dbo.{DetailTable} "
            + "WHERE GET_TYPE=@type AND GET_NO=@no;",
            connection);
        command.Parameters.AddWithValue("@type", TestType);
        command.Parameters.AddWithValue("@no", TestNo);
        var rows = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows[reader.GetString(0)] = Convert.ToDouble(reader.GetValue(1));
        }
        return rows;
    }

    private async Task<IReadOnlyList<double>> MoreQuantitiesAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT ISNULL(QTY,0) FROM dbo.{MoreTable} WHERE GET_TYPE=@type AND GET_NO=@no ORDER BY SERIAL_NO;",
            connection);
        command.Parameters.AddWithValue("@type", TestType);
        command.Parameters.AddWithValue("@no", TestNo);
        var rows = new List<double>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(Convert.ToDouble(reader.GetValue(0)));
        }
        return rows;
    }

    private async Task<int> AuditCountAsync()
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE ACTION=@action;",
            ("@action", MaterialIssueAllocateHandler.ActionKey));
    }

    // ===== 用例 =====

    [Fact]
    public async Task Allocate_CapsByDepotStock_AndDistributesMore()
    {
        var result = await RunAsync();

        Assert.True(result.Status == DocumentActionStatus.Ok, result.ErrorMessage);
        Assert.Equal(DocumentActionOutcome.Refreshed, result.Result!.Outcome);
        // A 料：需求 10，库别合计 7+3=10 ⇒ 发 10（两行按库位汇总，不取其中一行）；
        // B 料：需求 5，合计 2 ⇒ 发 2；C 料无库存行 ⇒ 0
        var send = await SendQuantitiesAsync();
        Assert.Equal(10d, send[ProductA], 6);
        Assert.Equal(2d, send[ProductB], 6);
        Assert.Equal(0d, send[ProductC], 6);
        // 待办：A 行 6+6 按可用 10 分（6, 4），B 行按可用 2 分（2）
        var more = await MoreQuantitiesAsync();
        Assert.Equal(new[] { 6d, 4d, 2d }, more.Select(value => Math.Round(value, 6)));
        Assert.Equal(1, await AuditCountAsync());
    }

    [Fact]
    public async Task ProbeOnly_ReportsAndWritesNothing()
    {
        var probe = await RunAsync(confirm: false);

        Assert.Equal(DocumentActionStatus.Ok, probe.Status);
        Assert.True(probe.RequiresConfirmation);
        var send = await SendQuantitiesAsync();
        Assert.All(send.Values, value => Assert.Equal(0d, value, 6));
        Assert.Equal(0, await AuditCountAsync());
    }
}
