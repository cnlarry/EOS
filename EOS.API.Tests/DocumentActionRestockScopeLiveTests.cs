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
/// 换区重盘（`restock-scope`）真库验收：范围选错的盘点单整单重来。
///
/// 口径：删本单既有明细 + 按新范围重插（与保存期生成器同一把尺子），项次从 1 起排；
/// 未结案且未转过单才能换（追溯凭据不可换）；不要求已批核（与 recalc/generate 有意不同）；
/// 负库存整单拒绝；探路零写入（含审计）。
///
/// 夹具全自造（ZZRS 仓 + 两库区 + 库存 + 盘点单），用完即删：不碰真实库存与单据。
/// 需要 MSSQL_ERP_CONN。
/// </summary>
[Collection("live-database")]
public sealed class DocumentActionRestockScopeLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 130101;
    private const string MasterTable = "INV_CHECK_STOCK_M";
    private const string DetailTable = "INV_CHECK_STOCK_D";
    private const string StockTable = "INV_PRO_DEPOT";
    private const string TestType = "ZZRS";
    private const string TestNo = "ZZRS00001";
    private const string TestUser = "ZZRS00001";
    /// <summary>本单的审计资源键（与执行器写入口径一致：主键值以逗号相连）。</summary>
    private const string RecordKey = TestType + "," + TestNo;
    private const string TestDepot = "ZZRS0001";
    private const string ZoneA = "ZZRS-A";
    private const string ZoneB = "ZZRS-B";
    private const string ProductA = "ZZRSAPRO1";
    private const string ProductB = "ZZRSBPRO1";

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
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@d, N'换区重盘测试仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, SEQ_NO, STATUS)
                VALUES (@d, N'-', NULL, N'/-', N'BIN', 0, N'A'),
                       (@d, @za,  NULL, N'/ZZRS-A', N'ZONE', 1, N'A'),
                       (@d, @zb,  NULL, N'/ZZRS-B', N'ZONE', 2, N'A');
            """,
            ("@d", TestDepot), ("@za", ZoneA), ("@zb", ZoneB));
        await ExecAsync(connection,
            $"""
            INSERT INTO dbo.{StockTable} (PRO_NO,DEPOT_ID,LOCATION_NO,BATCH_NO,QTY,CREATE_PERSON,CREATE_DATE,CONFIRM_TAG,CI)
            VALUES (@pa,@d,@za,N'',5,N'DbUp',SYSDATETIME(),0,N''),
                   (@pb,@d,@zb,N'',7,N'DbUp',SYSDATETIME(),0,N'');
            """,
            ("@pa", ProductA), ("@pb", ProductB), ("@d", TestDepot),
            ("@za", ZoneA), ("@zb", ZoneB));
        // 盘点单：两行 A 区旧明细（其中一行盘点数是人填的 99，重盘后必须消失），未批核。
        await ExecAsync(connection,
            $"""
            INSERT INTO dbo.{MasterTable} (CHECK_STOCK_TYPE,CHECK_STOCK_NO,DEPOT_ID,COUNT_DATE,CONFIRM_TAG,CREATE_PERSON,CREATE_DATE,CI)
            VALUES (@type,@no,@d,SYSDATETIME(),0,N'DbUp',SYSDATETIME(),N'');
            INSERT INTO dbo.{DetailTable}
                (CHECK_STOCK_TYPE,CHECK_STOCK_NO,SERIAL_NO,PRO_NO,DEPOT_ID,ACCOUNT_QTY,CHECK_QTY,LOCATION_NO,BATCH_NO)
            VALUES (@type,@no,1,@pa,@d,5,99,@za,N''),
                   (@type,@no,2,@pb,@d,7,7,@zb,N'');
            """,
            ("@type", TestType), ("@no", TestNo), ("@d", TestDepot),
            ("@pa", ProductA), ("@pb", ProductB), ("@za", ZoneA), ("@zb", ZoneB));
        await ExecAsync(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user AND M_IDX=@module AND BUTTON_KEY=@key)
                INSERT INTO dbo.SYSDD_BUTTON (USER_ID,M_IDX,BUTTON_KEY,ALLOW_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@user,@module,@key,1,N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
            """,
            ("@user", TestUser), ("@module", ModuleId), ("@key", RestockScopeHandler.ActionKey));
    }

    public async Task DisposeAsync()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"DELETE FROM dbo.{DetailTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{MasterTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{StockTable} WHERE DEPOT_ID=@d;", ("@d", TestDepot));
        await ExecAsync(connection,
            "DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@d;", ("@d", TestDepot));
        await ExecAsync(connection,
            "DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@d;", ("@d", TestDepot));
        await ExecAsync(connection, "DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user;", ("@user", TestUser));
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
            new DocumentActionRegistry([new RestockScopeHandler()], NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider,
                Options.Create(new AuditSettings())),
            NullLogger<DocumentActionExecutor>.Instance);
    }

    private static WorkbenchDefinition Definition() =>
        new(ModuleId: ModuleId, Title: "库存盘点单", MasterTable: MasterTable, DetailTable: DetailTable,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["CHECK_STOCK_TYPE", "CHECK_STOCK_NO"], DetailNoFields: string.Empty, HasWorkflow: false,
            UserId: TestUser, ExecTag: "Z",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CHECK_STOCK_TYPE" },
            BusinessActions: JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    seq = 3,
                    eventCode = "MANUAL",
                    effectKey = RestockScopeHandler.ActionKey,
                    enabled = true,
                    label = "换区重盘",
                    confirmTag = true,
                    failMode = "BLOCK",
                    @params = new
                    {
                        fields = new[]
                        {
                            new
                            {
                                key = RestockScopeHandler.ScopeParameter,
                                label = "盘点范围",
                                type = "string",
                                required = true,
                                maxLength = 30,
                            },
                        },
                    },
                },
            }));

    private static FormDefinition Form() =>
        new(ModuleId, "库存盘点单", MasterTable, DetailTable, true, true, "view", [], [],
            ["CHECK_STOCK_TYPE", "CHECK_STOCK_NO"], string.Empty, string.Empty);

    private Task<DocumentActionExecution> RunAsync(string? scopeRoot, bool confirm = true) =>
        Executor().ExecuteAsync(Definition(), Form(), RestockScopeHandler.ActionKey,
            new DocumentActionRequest(
                [TestType, TestNo],
                scopeRoot is null ? null : JsonSerializer.SerializeToElement(new Dictionary<string, string>
                {
                    [RestockScopeHandler.ScopeParameter] = scopeRoot,
                }),
                Confirm: confirm),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

    private async Task<IReadOnlyList<(int Serial, string Product, double Account, double Check, string Location)>> DetailsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT SERIAL_NO, LTRIM(RTRIM(PRO_NO)), ISNULL(ACCOUNT_QTY,0), ISNULL(CHECK_QTY,0), LTRIM(RTRIM(ISNULL(LOCATION_NO,N''))) "
            + $"FROM dbo.{DetailTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no ORDER BY SERIAL_NO;",
            connection);
        command.Parameters.AddWithValue("@type", TestType);
        command.Parameters.AddWithValue("@no", TestNo);
        var rows = new List<(int, string, double, double, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((Convert.ToInt32(reader.GetValue(0)), reader.GetString(1),
                Convert.ToDouble(reader.GetValue(2)), Convert.ToDouble(reader.GetValue(3)), reader.GetString(4)));
        }
        return rows;
    }

    /// <summary>审计是追加型的：取当前最大事件号作基线，之后只看基线之上的增量。</summary>
    private static async Task<long> AuditBaselineAsync()
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<long>(connection, "SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT;");
    }

    /// <summary>基线之上、本单（模块 + 动作 + 资源键）的审计条数。</summary>
    private async Task<int> AuditCountAsync(long baseline)
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<int>(connection,
            """
            SELECT COUNT(*) FROM dbo.AUDIT_EVENT
             WHERE EVENT_ID > @baseline AND M_IDX=@module AND ACTION=@action AND RESOURCE_KEY=@key;
            """,
            ("@baseline", baseline), ("@module", ModuleId),
            ("@action", RestockScopeHandler.ActionKey), ("@key", RecordKey));
    }

    // ===== 用例 =====

    [Fact]
    public async Task Restock_ReplacesDetails_WithNewScope()
    {
        var auditBaseline = await AuditBaselineAsync();
        var result = await RunAsync(ZoneB);

        Assert.True(result.Status == DocumentActionStatus.Ok, result.ErrorMessage);
        Assert.Equal(DocumentActionOutcome.Refreshed, result.Result!.Outcome);
        Assert.Contains(ZoneB, result.Result.Message!);
        // A 区旧明细整单消失，只剩 B 区 stock 行；项次从 1 起排，账面＝盘点＝当前量
        var details = await DetailsAsync();
        var single = Assert.Single(details);
        Assert.Equal(1, single.Serial);
        Assert.Equal(ProductB, single.Product);
        Assert.Equal(7d, single.Account, 6);
        Assert.Equal(7d, single.Check, 6);
        Assert.Equal(ZoneB, single.Location);
        // 本次换区重盘只留一条自己的审计（按本单资源键限定，别的单据/别的动作都不算）
        Assert.Equal(1, await AuditCountAsync(auditBaseline));
    }

    [Fact]
    public async Task ProbeOnly_ReportsAndWritesNothing()
    {
        var before = await DetailsAsync();
        var auditBaseline = await AuditBaselineAsync();

        var probe = await RunAsync(ZoneB, confirm: false);

        Assert.Equal(DocumentActionStatus.Ok, probe.Status);
        Assert.True(probe.RequiresConfirmation);
        var after = await DetailsAsync();
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(0, await AuditCountAsync(auditBaseline));
    }

    [Fact]
    public async Task WithoutScope_IsRefused()
    {
        var result = await RunAsync(null);

        Assert.Equal(DocumentActionStatus.ValidationFailed, result.Status);
        Assert.NotNull(result.FieldErrors);
        Assert.NotEmpty(result.FieldErrors!);
    }

    [Fact]
    public async Task FinishedDocument_IsRefused_AndWritesNothing()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"UPDATE dbo.{MasterTable} SET FINISHED_TAG=1 WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
            ("@type", TestType), ("@no", TestNo));

        var result = await RunAsync(ZoneB);

        Assert.Equal(DocumentActionStatus.Failed, result.Status);
        Assert.Contains("已结案", result.ErrorMessage!);
        Assert.Equal(2, (await DetailsAsync()).Count);
    }

    [Fact]
    public async Task NegativeStockInScope_IsRefused()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"UPDATE dbo.{StockTable} SET QTY=-3 WHERE DEPOT_ID=@d AND LTRIM(RTRIM(PRO_NO))=@pro;",
            ("@d", TestDepot), ("@pro", ProductB));

        var result = await RunAsync(ZoneB);

        Assert.Equal(DocumentActionStatus.Failed, result.Status);
        Assert.Contains("负库存", result.ErrorMessage!);
        Assert.Equal(2, (await DetailsAsync()).Count);
    }
}
