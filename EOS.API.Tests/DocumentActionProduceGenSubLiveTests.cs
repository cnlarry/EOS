using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Data.Effects;
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
/// 制令单「展开子制令」（`produce-gen-sub`）真库验收。
///
/// 口径：按工单 BOM 把有下阶料的子件逐个建成子制令（父项指向本单，明细自带用量），
/// 单号由统一路径发号；已有子单的行跳过（只补漏）；成环/超深拒绝；子单不自动批核；
/// 探路零写入（含审计）。
///
/// 夹具全自造（ZZPG 前缀：订单 BOM 两层 + 父制令单），用完即删。
/// 需要 EOS_ERP_TEST_CONNECTION。
/// </summary>
[Collection("live-database")]
public sealed class DocumentActionProduceGenSubLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1502;
    private const string MasterTable = "MOC_PRODUCE_M";
    private const string DetailTable = "MOC_PRODUCE_D";
    private const string OrderBomMasterTable = "MOC_BOM_STRU_M";
    private const string OrderBomTable = "MOC_BOM_STRU_D";
    private const string TestType = "ZZPG";
    private const string TestNo = "ZZPG00001";
    private const string TestUser = "ZZPG00001";
    private const string FinishedProduct = "ZZPGFIN01";
    private const string ElementWithChild = "ZZPGEL01";
    private const string ElementLeaf = "ZZPGEL02";
    private const string GrandChild = "ZZPGGC01";
    private const string GreatGrandChild = "ZZPGGG01";

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
        // 订单 BOM：成品有两个子件，其中 EL01 自己还有下阶料（孙件 GC），GC 还有下阶料（曾孙）；
        // EL02 是叶子。共三层。
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.{OrderBomMasterTable} WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no AND PRO_NO=@fin)
                INSERT INTO dbo.{OrderBomMasterTable} (PRODUCE_TYPE,PRODUCE_NO,PRO_NO)
                VALUES (@type,@no,@fin), (@type,@no,@el1), (@type,@no,@gc);
            IF NOT EXISTS (SELECT 1 FROM dbo.{OrderBomTable} WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no AND PRO_NO=@fin AND ELEMENT_PRO_NO=@el1)
                INSERT INTO dbo.{OrderBomTable} (PRODUCE_TYPE,PRODUCE_NO,PRO_NO,SERIAL_NO,ELEMENT_PRO_NO,ELEMENT_QTY,BASE_QTY,LOST_RATE)
                VALUES (@type,@no,@fin,1,@el1,2,1,0),
                       (@type,@no,@fin,2,@el2,1,1,0),
                       (@type,@no,@el1,1,@gc,4,1,0),
                       (@type,@no,@gc,1,@gg,1,1,0);
            """,
            ("@type", TestType), ("@no", TestNo), ("@fin", FinishedProduct),
            ("@el1", ElementWithChild), ("@el2", ElementLeaf),
            ("@gc", GrandChild), ("@gg", GreatGrandChild));
        // 父制令单（数量 10，未完工；判别标志显式 0，否则过不了 1502 的模块范围过滤）
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.{MasterTable} WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no)
                INSERT INTO dbo.{MasterTable} (PRODUCE_TYPE,PRODUCE_NO,PRO_NO,QTY,SPARE_QTY,REWORK_TAG,OUTSIDE_TAG,FINISHED_TAG,PRODUCE_DATE)
                VALUES (@type,@no,@fin,10,0,0,0,0,CONVERT(nvarchar(10),SYSDATETIME(),120));
            """,
            ("@type", TestType), ("@no", TestNo), ("@fin", FinishedProduct));
        // 子件料号主档：子单明细单位必填（借一条真实料号的单位）
        foreach (var pro in new[] { ElementWithChild, ElementLeaf, GrandChild, GreatGrandChild })
        {
            await ExecAsync(connection,
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO))=@pro)
                    INSERT INTO dbo.PRODUCT (PRO_NO,PRO_NAME,UNIT_ID,CREATE_PERSON,CREATE_DATE,CONFIRM_TAG,CI)
                    SELECT @pro, N'子制令用例料号', (SELECT TOP 1 UNIT_ID FROM dbo.PRODUCT WHERE LTRIM(RTRIM(ISNULL(UNIT_ID,N'')))<>N'' ORDER BY PRO_NO),
                           N'DbUp', SYSDATETIME(), 0, N'';
                """,
                ("@pro", pro));
        }
        // 子单走统一创建路径：测试用户需要 1502 的新增权（与处理器复核的是同一把尺子）
        await ExecAsync(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDD WHERE USER_ID=@user AND M_IDX=@module)
                INSERT INTO dbo.SYSDD (USER_ID,M_IDX,ADDNEW_TAG,EDIT_TAG,DELETE_TAG,COST_TAG,SECRECY_TAG,EXEC_TAG)
                VALUES (@user,@module,1,1,1,1,1,N'Z');
            """,
            ("@user", TestUser), ("@module", ModuleId));
        await ExecAsync(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user AND M_IDX=@module AND BUTTON_KEY=@key)
                INSERT INTO dbo.SYSDD_BUTTON (USER_ID,M_IDX,BUTTON_KEY,ALLOW_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@user,@module,@key,1,N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
            """,
            ("@user", TestUser), ("@module", ModuleId), ("@key", ProduceGenSubHandler.ActionKey));
    }

    public async Task DisposeAsync()
    {
        await using var connection = await OpenAsync();
        // 子孙单据按料号链清理（本夹具全部 ZZPG 前缀料号；父单本身也在其中；子单单别是自动单号，不限定）
        await ExecAsync(connection,
            $"DELETE FROM dbo.{DetailTable} WHERE (PRODUCE_TYPE=@type AND PRODUCE_NO=@no) OR LTRIM(RTRIM(PRO_NO)) LIKE N'ZZPG%';",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{MasterTable} WHERE (PRODUCE_TYPE=@type AND PRODUCE_NO=@no) OR LTRIM(RTRIM(PRO_NO)) LIKE N'ZZPG%';",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{OrderBomTable} WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{OrderBomMasterTable} WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            "DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) IN (@a,@b,@c,@d);",
            ("@a", ElementWithChild), ("@b", ElementLeaf), ("@c", GrandChild), ("@d", GreatGrandChild));
        await ExecAsync(connection,
            "DELETE FROM dbo.SYSDD WHERE USER_ID=@user;", ("@user", TestUser));
        await ExecAsync(connection, "DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user;", ("@user", TestUser));
        await ExecAsync(connection,
            "DELETE FROM dbo.AUDIT_FIELD_CHANGE WHERE EVENT_ID IN (SELECT EVENT_ID FROM dbo.AUDIT_EVENT WHERE ACTION=@action);",
            ("@action", ProduceGenSubHandler.ActionKey));
        await ExecAsync(connection,
            "DELETE FROM dbo.AUDIT_EVENT WHERE ACTION=@action;", ("@action", ProduceGenSubHandler.ActionKey));
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
            new DocumentActionRegistry([ProduceGenSubHandlerFactory(connections)], NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider,
                Options.Create(new AuditSettings())),
            NullLogger<DocumentActionExecutor>.Instance);
    }

    private static ProduceGenSubHandler ProduceGenSubHandlerFactory(DbConnectionFactory connections)
    {
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider,
            Options.Create(new AuditSettings()));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        var rights = new ModuleRightsRepository(connections, NullLogger<ModuleRightsRepository>.Instance);
        var permissions = new PermissionService(rights, new PermissionCache(configuration));
        var engine = new EffectEngineInvoker(
            new EffectEngineSettings { Enabled = true },
            new EffectPlanLoader(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            NullLogger<EffectEngineInvoker>.Instance);
        var workflow = new WorkflowEngine(connections, auditWriter, provider, engine, NullLogger<WorkflowEngine>.Instance);
        var idempotency = new WorkbenchIdempotency();
        var approval = new WorkbenchApprovalService(connections, auditWriter, workflow, engine, idempotency,
            NullLogger<WorkbenchApprovalService>.Instance);
        var commandHandler = new WorkbenchCommandHandler(connections, auditWriter, approval,
            new WorkbenchScopeFilter(new ApiMetrics()), engine, idempotency,
            new DepotStockPolicyService(connections, auditWriter), NullLogger<WorkbenchCommandHandler>.Instance);
        var builder = new WorkbenchDefinitionBuilder(connections, provider,
            Options.Create(new UnifiedFormEditorSettings()), NullLogger<WorkbenchDefinitionBuilder>.Instance);
        return new ProduceGenSubHandler(permissions, builder, commandHandler, connections,
            NullLogger<ProduceGenSubHandler>.Instance);
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
                    seq = 2,
                    eventCode = "MANUAL",
                    effectKey = ProduceGenSubHandler.ActionKey,
                    enabled = true,
                    label = "展开子制令",
                    confirmTag = true,
                    failMode = "BLOCK",
                },
            }));

    private static FormDefinition Form() =>
        new(ModuleId, "制令单", MasterTable, DetailTable, true, true, "view", [], [],
            ["PRODUCE_TYPE", "PRODUCE_NO"], string.Empty, string.Empty);

    private Task<DocumentActionExecution> RunAsync(bool confirm = true) =>
        Executor().ExecuteAsync(Definition(), Form(), ProduceGenSubHandler.ActionKey,
            new DocumentActionRequest([TestType, TestNo], null, Confirm: confirm),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

    private async Task<IReadOnlyList<string>> ChildProductsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT LTRIM(RTRIM(PRO_NO)) FROM dbo.{MasterTable} "
            + "WHERE LTRIM(RTRIM(PARENT_TYPE))=@type AND LTRIM(RTRIM(PARENT_NO))=@no ORDER BY PRO_NO;",
            connection);
        command.Parameters.AddWithValue("@type", TestType);
        command.Parameters.AddWithValue("@no", TestNo);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }

    private async Task<int> AuditCountAsync()
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE ACTION=@action;",
            ("@action", ProduceGenSubHandler.ActionKey));
    }

    // ===== 用例 =====

    [Fact]
    public async Task Expand_CreatesChildrenRecursively_SkipsExisting()
    {
        var result = await RunAsync();

        Assert.True(result.Status == DocumentActionStatus.Ok, result.ErrorMessage);
        Assert.Equal(DocumentActionOutcome.Message, result.Result!.Outcome);
        Assert.Contains("2 张", result.Result.Message!);
        // 本单按订单 BOM 补两行明细（EL01 需求 20，EL02 需求 10）
        await using var verifyConnection = await OpenAsync();
        var selfNeed = await ScalarAsync<double>(verifyConnection,
            $"SELECT ISNULL(SUM(ISNULL(NEED_QTY,0)),0) FROM dbo.{DetailTable} WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        Assert.Equal(30d, selfNeed, 6);
        // 三层：EL01 直属本单，GC 直属 EL01 单（GG 是叶子，只进 GC 单的明细，不建单）；
        // EL02 是叶子，不建单
        var children = await ChildProductsAsync();
        Assert.Equal([ElementWithChild], children);
        await using var chainConnection = await OpenAsync();
        var grandChildParent = await ScalarAsync<string>(chainConnection,
            $"SELECT LTRIM(RTRIM(PARENT_NO)) FROM dbo.{MasterTable} WHERE LTRIM(RTRIM(PRO_NO))=@pro;",
            ("@pro", GrandChild));
        var elChildNo = await ScalarAsync<string>(chainConnection,
            $"SELECT LTRIM(RTRIM(PRODUCE_NO)) FROM dbo.{MasterTable} WHERE LTRIM(RTRIM(PARENT_NO))=@no AND LTRIM(RTRIM(PRO_NO))=@pro;",
            ("@no", TestNo), ("@pro", ElementWithChild));
        Assert.Equal(elChildNo, grandChildParent);
        // GC 单有一行明细（GG，用量 4×1×GC 单数量，叶子不建单但要进明细）
        var grandChildDetailCount = await ScalarAsync<int>(chainConnection,
            $"SELECT COUNT(*) FROM dbo.{DetailTable} d JOIN dbo.{MasterTable} m "
            + "ON d.PRODUCE_TYPE=m.PRODUCE_TYPE AND d.PRODUCE_NO=m.PRODUCE_NO "
            + "WHERE LTRIM(RTRIM(m.PRO_NO))=@pro;",
            ("@pro", GrandChild));
        Assert.Equal(1, grandChildDetailCount);

        // 第二次点：全部已有，只补漏不复制
        var again = await Executor().ExecuteAsync(Definition(), Form(), ProduceGenSubHandler.ActionKey,
            new DocumentActionRequest([TestType, TestNo], null, Confirm: true),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Equal(DocumentActionStatus.Ok, again.Status);
        Assert.Contains("不需要生成", again.Result!.Message!);
        Assert.Equal([ElementWithChild], await ChildProductsAsync());
        Assert.Equal(2, await AuditCountAsync());
    }

    [Fact]
    public async Task ProbeOnly_ReportsAndWritesNothing()
    {
        var probe = await RunAsync(confirm: false);

        Assert.Equal(DocumentActionStatus.Ok, probe.Status);
        Assert.True(probe.RequiresConfirmation);
        Assert.Empty(await ChildProductsAsync());
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
        Assert.Empty(await ChildProductsAsync());
    }
}
