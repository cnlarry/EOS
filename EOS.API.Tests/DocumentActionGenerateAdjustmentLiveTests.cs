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
/// P1 真库验收：盘点单的「生成调整单」（`generate-adjustment`）。
///
/// 逐条对上 P1 的验收：逐行携带**位置与批次**生成 130107 并**过账**；
/// 差异为 0 的行不生成；生成结果可追溯（来源回写 + 操作审计）；`once`＝转后结案锁死。
///
/// 夹具自己造库存行（ZZ 前缀料号）与盘点单，结束即清理：不碰任何真实库存与单据。
/// 需要 MSSQL_ERP_CONN。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class DocumentActionGenerateAdjustmentLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 130101;
    private const int TargetModuleId = 130107;
    private const string MasterTable = "INV_CHECK_STOCK_M";
    private const string DetailTable = "INV_CHECK_STOCK_D";
    private const string StockTable = "INV_PRO_DEPOT";
    private const string TargetMasterTable = "INV_OCCUR_ADJUST_M";
    private const string TargetDetailTable = "INV_OCCUR_ADJUST_D";
    private const string TestType = "ZZ";
    private const string TestNo = "ZZADJ00001";
    private const string TestUser = "ZZADJ0001";
    private const string TestProduct = "ZZADJPRO01";
    private const string ZeroProduct = "ZZADJPRO00";
    /// <summary>自造库别（用例自建、用完删除）：转单动作按库别过账，借真实库别会把风险引到真账上。</summary>
    private const string TestDepot = "ZZADJDP01";
    /// <summary>自造库位：库存表对库位主档有外键，库位必须真实存在。</summary>
    private const string TestLocation = "ZZ-ADJ-LOC";
    private const double StartQty = 100d;
    private const double DiffQty = 5d;
    private const double CostPrice = 3d;
    /// <summary>本单的审计资源键（与执行器写入口径一致：主键值以逗号相连）。</summary>
    private const string RecordKey = TestType + "," + TestNo;

    private static DbConnectionFactory Connections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        return new DbConnectionFactory(configuration);
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();

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

    private string _depot = string.Empty;
    private DbConnectionFactory _connections = null!;
    private WorkbenchDefinitionProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _depot = TestDepot;
        _connections = Connections();
        // 运行时定义只有已发布快照一个来源：不刷新基线就会拿不到效果引擎段，
        // 130107 的批核链（inventory-move＝过账）也不会生效。
        _provider = new WorkbenchDefinitionProvider(_connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        await _provider.RefreshAsync(CancellationToken.None);
        await using var connection = await OpenAsync();
        // 自造库别与库位：先清上一轮残留（中断时可能留下），重复运行结果才可比。
        await ExecAsync(connection,
            """
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@depot;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@depot;
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@depot, N'ZZ 转单用例仓');
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID,LOCATION_NO,LOCATION_TYPE,STORAGE_TYPE,STATUS,CONFIRM_TAG)
                VALUES (@depot,@loc,N'STAGE',N'BULK',N'A',1);
            """,
            ("@depot", TestDepot), ("@loc", TestLocation));
        // 自造库存行：100 件在 Z 库位，成本价 3 —— 盘点数 105 时差异 +5。
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.{StockTable} WHERE PRO_NO=@pro AND DEPOT_ID=@depot AND LOCATION_NO=@loc AND BATCH_NO=N'')
                INSERT INTO dbo.{StockTable} (PRO_NO,DEPOT_ID,LOCATION_NO,BATCH_NO,QTY,COST_PRICE,CREATE_PERSON,CREATE_DATE,CONFIRM_TAG)
                VALUES (@pro,@depot,@loc,N'',@qty,@price,N'DbUp',SYSDATETIME(),0);
            """,
            ("@pro", TestProduct), ("@depot", _depot), ("@loc", TestLocation), ("@qty", StartQty), ("@price", CostPrice));
        // 盘点单：一行有盈亏（105 vs 100）、一行无盈亏（10 vs 10，不得转出）
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.{MasterTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no)
                INSERT INTO dbo.{MasterTable} (CHECK_STOCK_TYPE,CHECK_STOCK_NO,DEPOT_ID,COUNT_DATE,CONFIRM_TAG,CREATE_PERSON,CREATE_DATE)
                VALUES (@type,@no,@depot,SYSDATETIME(),1,N'DbUp',SYSDATETIME());
            """,
            ("@type", TestType), ("@no", TestNo), ("@depot", _depot));
        await ExecAsync(connection,
            $"""
            INSERT INTO dbo.{DetailTable}
                (CHECK_STOCK_TYPE,CHECK_STOCK_NO,SERIAL_NO,PRO_NO,DEPOT_ID,ACCOUNT_QTY,CHECK_QTY,LOCATION_NO,BATCH_NO)
            VALUES
                (@type,@no,1,@pro,@depot,@account,@checked,@loc,N''),
                (@type,@no,2,@zeroPro,@depot,10,10,@loc,N'');
            """,
            ("@type", TestType), ("@no", TestNo), ("@pro", TestProduct), ("@zeroPro", ZeroProduct),
            ("@depot", _depot), ("@account", StartQty), ("@checked", StartQty + DiffQty), ("@loc", TestLocation));
        // 按钮授权（fail-closed 名单）+ 下游模块的新增权限（处理器按同一把尺子复核）
        await ExecAsync(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user AND M_IDX=@module AND BUTTON_KEY=@key)
                INSERT INTO dbo.SYSDD_BUTTON (USER_ID,M_IDX,BUTTON_KEY,ALLOW_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@user,@module,@key,1,N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
            """,
            ("@user", TestUser), ("@module", ModuleId), ("@key", GenerateAdjustmentHandler.ActionKey));
        // 成本/保密位要给：调整单明细的单价属成本列，无权限时表单不会下发该列（与手工新建一致）。
        await ExecAsync(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDD WHERE USER_ID=@user AND M_IDX=@module)
                INSERT INTO dbo.SYSDD (USER_ID,M_IDX,ADDNEW_TAG,EDIT_TAG,DELETE_TAG,COST_TAG,SECRECY_TAG,EXEC_TAG)
                VALUES (@user,@module,1,1,1,1,1,N'Z');
            """,
            ("@user", TestUser), ("@module", TargetModuleId));
        // 自造料号主档：调整单明细的 UNIT_ID 是必填，取自 PRODUCT.UNIT_ID（借一条真实料号的单位）。
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO))=@pro)
                INSERT INTO dbo.PRODUCT (PRO_NO,PRO_NAME,UNIT_ID,CREATE_PERSON,CREATE_DATE,CONFIRM_TAG)
                SELECT @pro, N'转单用例料号', (SELECT TOP 1 UNIT_ID FROM dbo.PRODUCT WHERE LTRIM(RTRIM(ISNULL(UNIT_ID,N'')))<>N'' ORDER BY PRO_NO),
                       N'DbUp', SYSDATETIME(), 0;
            """,
            ("@pro", TestProduct));
    }

    public async Task DisposeAsync()
    {
        await using var connection = await OpenAsync();
        // 按"备注里记着来源盘点单"清理：即使动作在过账/回写中途失败（来源没写成），
        // 已经生成出来的调整单也要被清掉，否则真库里会留下孤儿单据。
        await ExecAsync(connection,
            $"""
            DELETE FROM dbo.INV_DEPOT_LOG WHERE LTRIM(RTRIM(MUTUALITY_NO)) IN
                (SELECT LTRIM(RTRIM(OCCUR_NO)) FROM dbo.{TargetMasterTable} WHERE REMARK LIKE N'%{TestNo}%');
            """);
        await ExecAsync(connection,
            $"DELETE FROM dbo.{TargetDetailTable} WHERE LTRIM(RTRIM(OCCUR_NO)) IN (SELECT LTRIM(RTRIM(OCCUR_NO)) FROM dbo.{TargetMasterTable} WHERE REMARK LIKE N'%{TestNo}%');");
        await ExecAsync(connection,
            $"DELETE FROM dbo.{TargetMasterTable} WHERE REMARK LIKE N'%{TestNo}%';");
        await ExecAsync(connection,
            $"DELETE FROM dbo.{DetailTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{MasterTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{StockTable} WHERE PRO_NO IN (@pro,@zeroPro) AND DEPOT_ID=@depot;",
            ("@pro", TestProduct), ("@zeroPro", ZeroProduct), ("@depot", _depot));
        await ExecAsync(connection,
            "DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO))=@pro;", ("@pro", TestProduct));
        await ExecAsync(connection,
            """
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@depot;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@depot;
            """,
            ("@depot", TestDepot));
        await ExecAsync(connection, "DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user;", ("@user", TestUser));
        await ExecAsync(connection, "DELETE FROM dbo.SYSDD WHERE USER_ID=@user;", ("@user", TestUser));
        await ExecAsync(connection,
            "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE M_IDX IN (@a,@b) AND ACTION=N'ACTION';",
            ("@a", ModuleId), ("@b", TargetModuleId));
    }

    // ===== 装配（与 Program.cs 同源：真实服务，只有"按钮授权"以外的权限读取走真库 SYSDD）=====

    private GenerateAdjustmentHandler Handler()
    {
        var connections = _connections;
        var provider = _provider;
        var auditWriter = new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider, Options.Create(new AuditSettings()));
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
            new DepotStockPolicyService(connections, auditWriter), new WorkbenchVirtualColumnResolver(),
            NullLogger<WorkbenchCommandHandler>.Instance);
        var builder = new WorkbenchDefinitionBuilder(connections, provider,
            Options.Create(new UnifiedFormEditorSettings()), NullLogger<WorkbenchDefinitionBuilder>.Instance);
        var rights = new ModuleRightsRepository(connections, NullLogger<ModuleRightsRepository>.Instance);
        var permissions = new PermissionService(rights, new PermissionCache(Configuration()));
        return new GenerateAdjustmentHandler(permissions, builder, commandHandler, approval,
            NullLogger<GenerateAdjustmentHandler>.Instance);
    }

    private DocumentActionExecutor Executor() =>
        new(_connections,
            new DocumentActionRegistry([Handler()], NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(_connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            new WorkbenchAuditWriter(_connections, new HttpContextAccessor(), _provider, Options.Create(new AuditSettings())),
            NullLogger<DocumentActionExecutor>.Instance);

    private static WorkbenchDefinition Definition() =>
        new(ModuleId: ModuleId, Title: "库存盘点单", MasterTable: MasterTable, DetailTable: DetailTable,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["CHECK_STOCK_TYPE", "CHECK_STOCK_NO"], DetailNoFields: "", HasWorkflow: false,
            UserId: TestUser, ExecTag: "Z",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CHECK_STOCK_TYPE" },
            BusinessActions: JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    seq = 2,
                    eventCode = "MANUAL",
                    effectKey = GenerateAdjustmentHandler.ActionKey,
                    enabled = true,
                    label = "生成调整单",
                    confirmTag = true,
                    failMode = "BLOCK",
                    condition = """{"logic":"AND","items":[{"type":"value-eq","field":{"scope":"MASTER","field":"CONFIRM_TAG"},"value":1}]}""",
                },
            }));

    private static FormDefinition Form() =>
        new(ModuleId, "库存盘点单", MasterTable, DetailTable, true, true, "view", [], [],
            ["CHECK_STOCK_TYPE", "CHECK_STOCK_NO"], string.Empty, string.Empty);

    private Task<DocumentActionExecution> RunAsync(bool confirm = true) =>
        Executor().ExecuteAsync(Definition(), Form(), GenerateAdjustmentHandler.ActionKey,
            new DocumentActionRequest([TestType, TestNo], Confirm: confirm),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

    /// <summary>审计是追加型的：取当前最大事件号作基线，之后只看基线之上的增量。</summary>
    private static async Task<long> AuditBaselineAsync()
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<long>(connection, "SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT;");
    }

    /// <summary>基线之上、本单（模块 + 动作 + 资源键）的审计条数。</summary>
    private static async Task<int> AuditCountAsync(long baseline)
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<int>(connection,
            """
            SELECT COUNT(*) FROM dbo.AUDIT_EVENT
             WHERE EVENT_ID > @baseline AND M_IDX=@module AND ACTION=@action AND RESOURCE_KEY=@key;
            """,
            ("@baseline", baseline), ("@module", ModuleId),
            ("@action", GenerateAdjustmentHandler.ActionKey), ("@key", RecordKey));
    }

    // ===== 用例 =====

    [Fact]
    public async Task GeneratesAdjustmentWithLocationAndBatch_PostsIt_AndLocksTheStocktake()
    {
        var auditBaseline = await AuditBaselineAsync();
        var result = await RunAsync();

        // 断言带上服务端文案：转单链路的失败原因必须一眼可见（不然只剩一个 Failed）。
        Assert.True(result.Status == DocumentActionStatus.Ok, result.ErrorMessage);
        Assert.Equal(DocumentActionOutcome.Navigated, result.Result!.Outcome);
        Assert.Equal(TargetModuleId, result.Result.TargetModuleId);
        // 主键顺序 = (OCCUR_TYPE, OCCUR_NO)
        Assert.Equal(2, result.Result.TargetKey!.Count);
        var adjustType = result.Result.TargetKey[0];
        var adjustNo = result.Result.TargetKey[1];

        await using var connection = await OpenAsync();
        var storedType = await ScalarAsync<string>(connection,
            $"SELECT LTRIM(RTRIM(OCCUR_TYPE)) FROM dbo.{TargetMasterTable} WHERE LTRIM(RTRIM(OCCUR_NO))=@no;",
            ("@no", adjustNo));
        Assert.Equal(adjustType, storedType);
        // 差异为 0 的行不生成：两行明细里只有一行有盈亏
        var adjustRows = await ScalarAsync<int>(connection,
            $"SELECT COUNT(*) FROM dbo.{TargetDetailTable} WHERE LTRIM(RTRIM(OCCUR_NO))=@no;", ("@no", adjustNo));
        Assert.Equal(1, adjustRows);
        // 逐行携带位置与批次（既有实现只带库别与料号）
        var carried = await ScalarAsync<string>(connection,
            $"""
            SELECT LTRIM(RTRIM(PRO_NO)) + '|' + LTRIM(RTRIM(DEPOT_ID)) + '|' + LTRIM(RTRIM(ISNULL(LOCATION_NO,N''))) + '|' + CAST(QTY AS varchar(20))
            FROM dbo.{TargetDetailTable} WHERE LTRIM(RTRIM(OCCUR_NO))=@no;
            """, ("@no", adjustNo));
        Assert.Equal($"{TestProduct}|{_depot}|{TestLocation}|{DiffQty}", carried);
        // 过账：调整单已批核，且库存被实际改动
        var confirmed = await ScalarAsync<bool>(connection,
            $"SELECT CONFIRM_TAG FROM dbo.{TargetMasterTable} WHERE LTRIM(RTRIM(OCCUR_NO))=@no;", ("@no", adjustNo));
        Assert.True(confirmed);
        var stockQty = await ScalarAsync<double>(connection,
            $"SELECT QTY FROM dbo.{StockTable} WHERE PRO_NO=@pro AND DEPOT_ID=@depot AND LOCATION_NO=@loc AND BATCH_NO=N'';",
            ("@pro", TestProduct), ("@depot", _depot), ("@loc", TestLocation));
        Assert.Equal(StartQty + DiffQty, stockQty, 6);
        // 来源可追溯 + once 结案锁死
        var source = await ScalarAsync<string>(connection,
            $"SELECT LTRIM(RTRIM(ISNULL(ADJUST_TYPE,N''))) + '|' + LTRIM(RTRIM(ISNULL(ADJUST_NO,N''))) + '|' + CAST(ISNULL(FINISHED_TAG,0) AS varchar(2)) FROM dbo.{MasterTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        Assert.Equal($"{adjustType}|{adjustNo}|1", source);
        // 动作留痕：基线之上恰好一条自己的审计（别的模块、别的动作、别的单据都不算）
        Assert.Equal(1, await AuditCountAsync(auditBaseline));
    }

    [Fact]
    public async Task SecondClick_IsRefused_OnceLocked()
    {
        await RunAsync();

        var again = await RunAsync();

        Assert.Equal(DocumentActionStatus.Failed, again.Status);
        await using var connection = await OpenAsync();
        var adjustCount = await ScalarAsync<int>(connection,
            $"SELECT COUNT(*) FROM dbo.{TargetMasterTable} WHERE ISNULL(LTRIM(RTRIM(REMARK)),N'') LIKE N'%{TestNo}%';");
        Assert.Equal(1, adjustCount);
    }

    [Fact]
    public async Task ProbeOnly_ReportsWhatWouldHappen_AndWritesNothing()
    {
        var auditBaseline = await AuditBaselineAsync();
        var probe = await RunAsync(confirm: false);

        Assert.Equal(DocumentActionStatus.Ok, probe.Status);
        Assert.True(probe.RequiresConfirmation);
        await using var connection = await OpenAsync();
        var adjustCount = await ScalarAsync<int>(connection,
            $"SELECT COUNT(*) FROM dbo.{TargetMasterTable} WHERE ISNULL(LTRIM(RTRIM(REMARK)),N'') LIKE N'%{TestNo}%';");
        Assert.Equal(0, adjustCount);
        var finished = await ScalarAsync<bool>(connection,
            $"SELECT ISNULL(FINISHED_TAG,0) FROM dbo.{MasterTable} WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        Assert.False(finished);
        // 探路整体回滚：连审计也不留（否则"什么都没写"这句话不成立）
        Assert.Equal(0, await AuditCountAsync(auditBaseline));
    }

    [Fact]
    public async Task WithoutDifferences_ReportsInsteadOfCreating()
    {
        await using (var connection = await OpenAsync())
        {
            // 把两行都改成无盈亏
            await ExecAsync(connection,
                $"UPDATE dbo.{DetailTable} SET CHECK_QTY=ACCOUNT_QTY WHERE CHECK_STOCK_TYPE=@type AND CHECK_STOCK_NO=@no;",
                ("@type", TestType), ("@no", TestNo));
        }

        var result = await RunAsync();

        Assert.Equal(DocumentActionStatus.Ok, result.Status);
        Assert.Equal(DocumentActionOutcome.Message, result.Result!.Outcome);
        Assert.Contains("没有盈亏", result.Result.Message!);
    }
}
