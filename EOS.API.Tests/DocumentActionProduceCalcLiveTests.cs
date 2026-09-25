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
/// 制令单「计算用料」（`produce-calc-materials`）真库验收。
///
/// 口径：BOM 单层叶子汇总（最新版次）→ 明细补行（项次取已用最大号 + 1）并重算需求 →
/// 订单待办按订单汇总同步 → 申购应购量回填；订单为空跳过待办/申购段；已完工结案拒绝；
/// 底数为 0 的 BOM 行拒绝；探路零写入（含审计）。
///
/// 夹具全自造（ZZPC 前缀），用完即删。
/// 需要 EOS_ERP_TEST_CONNECTION。
/// </summary>
[Collection("live-database")]
public sealed class DocumentActionProduceCalcLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1502;
    private const string MasterTable = "MOC_PRODUCE_M";
    private const string DetailTable = "MOC_PRODUCE_D";
    private const string BomTable = "BOM_STRU_D";
    private const string MoreTable = "COP_ORDER_MORE";
    private const string ApplyDetailTable = "PUR_APPLY_D";
    private const string TestType = "ZZPC";
    private const string TestNo = "ZZPC00001";
    private const string TestUser = "ZZPC00001";
    private const string FinishedProduct = "ZZPCFIN01";
    private const string ElementA = "ZZPCEL01";
    private const string ElementB = "ZZPCEL02";
    private const string StaleElement = "ZZPCOLD01";
    private const string OrderType = "ZZ";
    private const string OrderNo = "ZZPCORD01";

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
        // 产品 BOM（单层两料：A 用量 2 损耗 10，B 用量 3 损耗 0）
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.{BomTable} WHERE PRO_NO=@fin AND ELEMENT_PRO_NO=@a)
                INSERT INTO dbo.{BomTable} (PRO_NO,SERIAL_NO,EDITION,ELEMENT_PRO_NO,ELEMENT_QTY,BASE_QTY,LOST_RATE)
                VALUES (@fin,1,N'A',@a,2,1,10),
                       (@fin,2,N'A',@b,3,1,0);
            """,
            ("@fin", FinishedProduct), ("@a", ElementA), ("@b", ElementB));
        // 制令单（数量 10，引用订单；未完工）+ 一行过期明细（序号 5，需求 99，用后即删）
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.{MasterTable} WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no)
                INSERT INTO dbo.{MasterTable} (PRODUCE_TYPE,PRODUCE_NO,PRO_NO,QTY,SPARE_QTY,ORDER_TYPE,ORDER_NO,ORDER_SERIAL_NO,FINISHED_TAG)
                VALUES (@type,@no,@fin,10,0,@otype,@ono,1,0);
            INSERT INTO dbo.{DetailTable}
                (PRODUCE_TYPE,PRODUCE_NO,SERIAL_NO,PRO_NO,NEED_QTY,USED_QTY,APPLY_QTY,PURCHASE_QTY)
            VALUES (@type,@no,5,@stale,99,0,0,0);
            """,
            ("@type", TestType), ("@no", TestNo), ("@fin", FinishedProduct),
            ("@otype", OrderType), ("@ono", OrderNo), ("@stale", StaleElement));
        // 订单待办旧值 + 申购行（应购量将被回填为 20）
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.{MoreTable} WHERE ORDER_TYPE=@otype AND ORDER_NO=@ono AND PRO_NO=@a)
                INSERT INTO dbo.{MoreTable} (ORDER_TYPE,ORDER_NO,PRO_NO,NEED_QTY,APPLY_QTY,USED_QTY,PURCHASE_QTY,RECEIVE_QTY,LOST_QTY)
                VALUES (@otype,@ono,@a,888,0,0,0,0,0);
            IF NOT EXISTS (SELECT 1 FROM dbo.{ApplyDetailTable} WHERE APPLY_TYPE=@atype AND APPLY_NO=@ano AND PRO_NO=@a)
                INSERT INTO dbo.{ApplyDetailTable} (APPLY_TYPE,APPLY_NO,SERIAL_NO,ORDER_TYPE,ORDER_NO,PRO_NO,REQUIRE_QTY)
                VALUES (@atype,@ano,1,@otype,@ono,@a,0);
            """,
            ("@otype", OrderType), ("@ono", OrderNo), ("@a", ElementA),
            ("@atype", "ZZAP"), ("@ano", "ZZAP00001"));
        await ExecAsync(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user AND M_IDX=@module AND BUTTON_KEY=@key)
                INSERT INTO dbo.SYSDD_BUTTON (USER_ID,M_IDX,BUTTON_KEY,ALLOW_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@user,@module,@key,1,N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
            """,
            ("@user", TestUser), ("@module", ModuleId), ("@key", ProduceCalcMaterialsHandler.ActionKey));
    }

    public async Task DisposeAsync()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"DELETE FROM dbo.{ApplyDetailTable} WHERE APPLY_TYPE=N'ZZAP' AND APPLY_NO=N'ZZAP00001';");
        await ExecAsync(connection,
            $"DELETE FROM dbo.{MoreTable} WHERE ORDER_TYPE=@otype AND ORDER_NO=@ono;",
            ("@otype", OrderType), ("@ono", OrderNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{DetailTable} WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{MasterTable} WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{BomTable} WHERE PRO_NO=@fin OR ELEMENT_PRO_NO IN (@a,@b);",
            ("@fin", FinishedProduct), ("@a", ElementA), ("@b", ElementB));
        await ExecAsync(connection, "DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user;", ("@user", TestUser));
        await ExecAsync(connection,
            "DELETE FROM dbo.AUDIT_FIELD_CHANGE WHERE EVENT_ID IN (SELECT EVENT_ID FROM dbo.AUDIT_EVENT WHERE ACTION=@action);",
            ("@action", ProduceCalcMaterialsHandler.ActionKey));
        await ExecAsync(connection,
            "DELETE FROM dbo.AUDIT_EVENT WHERE ACTION=@action;", ("@action", ProduceCalcMaterialsHandler.ActionKey));
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
            new DocumentActionRegistry([new ProduceCalcMaterialsHandler()], NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider,
                Options.Create(new AuditSettings())),
            NullLogger<DocumentActionExecutor>.Instance);
    }

    private static WorkbenchDefinition Definition() =>
        new(ModuleId: ModuleId, Title: "制令单", MasterTable: MasterTable, DetailTable: DetailTable,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["PRODUCE_TYPE", "PRODUCE_NO"], DetailNoFields: string.Empty, HasWorkflow: false,
            UserId: TestUser, ExecTag: "Z",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRODUCE_TYPE" },
            BusinessActions: JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    seq = 1,
                    eventCode = "MANUAL",
                    effectKey = ProduceCalcMaterialsHandler.ActionKey,
                    enabled = true,
                    label = "计算用料",
                    confirmTag = true,
                    failMode = "BLOCK",
                },
            }));

    private static FormDefinition Form() =>
        new(ModuleId, "制令单", MasterTable, DetailTable, true, true, "view", [], [],
            ["PRODUCE_TYPE", "PRODUCE_NO"], string.Empty, string.Empty);

    private Task<DocumentActionExecution> RunAsync(bool confirm = true) =>
        Executor().ExecuteAsync(Definition(), Form(), ProduceCalcMaterialsHandler.ActionKey,
            new DocumentActionRequest([TestType, TestNo], null, Confirm: confirm),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

    private async Task<IReadOnlyList<(int Serial, string Product, double Need)>> DetailsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT SERIAL_NO, LTRIM(RTRIM(PRO_NO)), ISNULL(NEED_QTY,0) FROM dbo.{DetailTable} "
            + "WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no ORDER BY SERIAL_NO;",
            connection);
        command.Parameters.AddWithValue("@type", TestType);
        command.Parameters.AddWithValue("@no", TestNo);
        var rows = new List<(int, string, double)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((Convert.ToInt32(reader.GetValue(0)), reader.GetString(1), Convert.ToDouble(reader.GetValue(2))));
        }
        return rows;
    }

    private async Task<int> AuditCountAsync()
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE ACTION=@action;",
            ("@action", ProduceCalcMaterialsHandler.ActionKey));
    }

    // ===== 用例 =====

    [Fact]
    public async Task Calc_RebuildsDetails_SyncsMoreAndApply()
    {
        var result = await RunAsync();

        Assert.True(result.Status == DocumentActionStatus.Ok, result.ErrorMessage);
        Assert.Equal(DocumentActionOutcome.Refreshed, result.Result!.Outcome);
        // 过期行被删，两料按 BOM 重算（A: 2×1.1×10=22，含 10% 损耗，旧口径 NEED 即毛需求；
        // B: 3×10=30），新行序号从已用最大号 5 之后起排
        var details = await DetailsAsync();
        Assert.Equal(2, details.Count);
        Assert.DoesNotContain(details, row => row.Product == StaleElement);
        Assert.Equal(new[] { 6, 7 }, details.Select(row => row.Serial));
        Assert.Equal(22d, details.Single(row => row.Product == ElementA).Need, 6);
        Assert.Equal(30d, details.Single(row => row.Product == ElementB).Need, 6);
        // 订单待办：A 行 888→22；申购应购量回填 22
        await using var connection = await OpenAsync();
        var moreNeed = await ScalarAsync<double>(connection,
            $"SELECT ISNULL(NEED_QTY,0) FROM dbo.{MoreTable} WHERE ORDER_TYPE=@otype AND ORDER_NO=@ono AND PRO_NO=@a;",
            ("@otype", OrderType), ("@ono", OrderNo), ("@a", ElementA));
        Assert.Equal(22d, moreNeed, 6);
        var require = await ScalarAsync<double>(connection,
            $"SELECT ISNULL(REQUIRE_QTY,0) FROM dbo.{ApplyDetailTable} WHERE APPLY_TYPE=N'ZZAP' AND APPLY_NO=N'ZZAP00001';");
        Assert.Equal(22d, require, 6);
        Assert.Equal(1, await AuditCountAsync());
    }

    [Fact]
    public async Task ProbeOnly_ReportsAndWritesNothing()
    {
        var probe = await RunAsync(confirm: false);

        Assert.Equal(DocumentActionStatus.Ok, probe.Status);
        Assert.True(probe.RequiresConfirmation);
        Assert.Single(await DetailsAsync());
        Assert.Equal(0, await AuditCountAsync());
    }

    [Fact]
    public async Task FinishedDocument_IsRefused_AndWritesNothing()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"UPDATE dbo.{MasterTable} SET FINISHED_TAG=1 WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no;",
            ("@type", TestType), ("@no", TestNo));

        var result = await RunAsync();

        Assert.Equal(DocumentActionStatus.Failed, result.Status);
        Assert.Contains("完工结案", result.ErrorMessage!);
        Assert.Single(await DetailsAsync());
    }

    [Fact]
    public async Task ZeroBaseQtyBom_IsRefused()
    {
        await using var connection = await OpenAsync();
        try
        {
            await ExecAsync(connection,
                $"INSERT INTO dbo.{BomTable} (PRO_NO,SERIAL_NO,EDITION,ELEMENT_PRO_NO,ELEMENT_QTY,BASE_QTY,LOST_RATE) "
                + "VALUES (@fin,9,N'A',N'ZZPCBADA',1,0,0);",
                ("@fin", FinishedProduct));

            var result = await RunAsync();

            Assert.Equal(DocumentActionStatus.Failed, result.Status);
            Assert.Contains("底数为 0", result.ErrorMessage!);
        }
        finally
        {
            await ExecAsync(connection,
                $"DELETE FROM dbo.{BomTable} WHERE PRO_NO=@fin AND ELEMENT_PRO_NO=N'ZZPCBADA';",
                ("@fin", FinishedProduct));
        }
    }
}
