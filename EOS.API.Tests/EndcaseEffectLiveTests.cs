using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Data.Inventory;
using EOS.API.Models;
using EOS.API.Tests.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 结案 / 取消结案接入效果链的真库验收（D7-⑥ / WS-22）。
///
/// 三条腿一起验：**主表标记** + **明细标记**（单据级结案即"全部结案"）+ **效果链**，
/// 三者同一事务；其中效果链那条腿就是 `ENDCASE` / `UNENDCASE` 两个事件在库里的落地。
///
/// 七条：① 有预留的制令结案 ⇒ 预留置 `C` 并写归因、可用量按口径回升、步骤可见；
/// ② 无预留结案 ⇒ 成功且影响 0 行（不是错误、也不是"跳过"）；
/// ③ 取消结案 ⇒ 当初由结案释放的预留收回、可用量回原值；
/// ④ **边界**：人工释放过的预留不因取消结案复活；
/// ⑤ 重复结案 / 重复取消结案 ⇒ 被守卫拒绝且零写入；
/// ⑥ 明细与主表同向置位，且结案后该单明细不再计入 MRP 未领需求；
/// ⑦ 效果链失败 ⇒ 主表标记一并回滚（不存在"标记翻了但预留没处理"的半截状态）。
///
/// 判别性：关掉结案路径的效果链派发 ⇒ ①②③ 变红；把取消结案的收回判据放宽成
/// "该来源所有 `STATUS='C'` 的行" ⇒ 恰好 ④ 变红。
///
/// 需要 MSSQL_ERP_CONN。
/// </summary>
[Collection("live-database")]
public sealed class EndcaseEffectLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int StockModule = 1303;
    private const int SourceModule = 1502;
    private const string MasterTable = "MOC_PRODUCE_M";
    private const string DetailTable = "MOC_PRODUCE_D";
    private const string ProduceType = "ZZENDC";
    private const string ProduceNo = "ZZENDC-MO1";
    private const string ReserveProduct = "ZZENDC-R1";
    private const string ReserveProduct2 = "ZZENDC-R2";
    private const string IssueProduct = "ZZENDC-I1";
    private const string IssueProduct2 = "ZZENDC-I2";
    private const string Depot = "CP";
    private const string Location = "-";
    private const string Batch = "";
    private const string TestUser = "ZZENDC0001";
    private const double StockQty = 100d;
    private const double ReserveQty = 20d;
    private const string DocumentRecordKey = ProduceType + "," + ProduceNo;

    private static string SlotRecordKey(string product) => product + "," + Depot + "," + Location + "," + Batch;

    private DbConnectionFactory _connections = null!;
    private WorkbenchDefinitionProvider _provider = null!;
    private WorkbenchAuditWriter _audit = null!;

    public async Task InitializeAsync()
    {
        _connections = Connections();
        _provider = new WorkbenchDefinitionProvider(_connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        _audit = new WorkbenchAuditWriter(_connections, new HttpContextAccessor(), _provider, Options.Create(new AuditSettings()));

        await using var connection = await OpenAsync();
        await CleanupAsync(connection);
        await ExecAsync(connection, """
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE)
                VALUES (@rpro, N'ZZENDC 预留料件', N'规格', '3'),
                       (@rpro2, N'ZZENDC 预留料件二', N'规格', '3'),
                       (@ipro, N'ZZENDC 领用料件', N'规格', '3'),
                       (@ipro2, N'ZZENDC 领用料件二', N'规格', '3');
            INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, USEABLE_QTY, COST_PRICE, COST_AMOUNT)
                VALUES (@rpro, @depot, @location, @batch, @qty, @qty, @qty, 3, @qty * 3),
                       (@rpro2, @depot, @location, @batch, @qty, @qty, @qty, 3, @qty * 3);
            INSERT INTO dbo.MOC_PRODUCE_M (PRODUCE_TYPE, PRODUCE_NO, FINISHED_TAG, CONFIRM_TAG)
                VALUES (N'ZZENDC', N'ZZENDC-MO1', 0, 1);
            -- 明细主键是 (单别, 单号, 料号)：两条未领需求各挂一个料号，合计 10 + 6
            INSERT INTO dbo.MOC_PRODUCE_D (PRODUCE_TYPE, PRODUCE_NO, PRO_NO, SERIAL_NO, NEED_QTY, USED_QTY, FINISHED_TAG)
                VALUES (N'ZZENDC', N'ZZENDC-MO1', @ipro, 1, 10, 0, 0),
                       (N'ZZENDC', N'ZZENDC-MO1', @ipro2, 2, 6, 0, 0);
            """, ("@rpro", ReserveProduct), ("@rpro2", ReserveProduct2), ("@ipro", IssueProduct),
            ("@ipro2", IssueProduct2), ("@depot", Depot), ("@location", Location), ("@batch", Batch),
            ("@qty", StockQty));
    }

    public async Task DisposeAsync()
    {
        await using var connection = await OpenAsync();
        await CleanupAsync(connection);
    }

    private static async Task CleanupAsync(SqlConnection connection)
    {
        // 审计行**不删**：它是追加型痕迹，用例的痕迹按设计留在库里（断言改用"基线之上的增量"）。
        await ExecAsync(connection, """
            DELETE FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) IN (@rpro, @rpro2, @ipro, @ipro2);
            DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) IN (@rpro, @rpro2, @ipro, @ipro2);
            DELETE FROM dbo.MOC_PRODUCE_D WHERE PRODUCE_TYPE = N'ZZENDC';
            DELETE FROM dbo.MOC_PRODUCE_M WHERE PRODUCE_TYPE = N'ZZENDC';
            DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) IN (@rpro, @rpro2, @ipro, @ipro2);
            """, ("@rpro", ReserveProduct), ("@rpro2", ReserveProduct2),
            ("@ipro", IssueProduct), ("@ipro2", IssueProduct2));
    }

    // ===== ① 有预留的制令结案 =====

    [Fact]
    public async Task 结案释放预留_明细同步_步骤可见()
    {
        await ReserveAsync(ReserveQty);
        var auditBaseline = await AuditBaselineAsync();

        var (blocked, steps) = await FinishViaCoreAsync(finish: true, EndcaseAction(), UnendcaseAction());

        Assert.Null(blocked);
        // 步骤可见：这一步确实跑了（而不是"配了不跑"），并报出影响行数
        var step = Assert.Single(steps, item => item.EffectKey == InventoryReleaseBySourceHandler.EffectKeyName);
        Assert.Equal(EffectStepOutcome.Ran, step.Outcome);
        Assert.Equal(1, step.RowsAffected);

        await using var connection = await OpenAsync();
        Assert.True(await ScalarAsync<bool>(connection, MasterFinishedSql));
        Assert.True(await ScalarAsync<bool>(connection, DetailAllFinishedSql));
        Assert.Equal("C", await ReserveStatusAsync(connection));
        // 归因写明"这次释放是结案干的"：取消结案据此逐行收回，不靠"该来源所有 C 行"这类宽判据
        Assert.Equal("ENDCASE", await ReserveReleaseKindAsync(connection));
        // 来源已结案后该笔占用不再计入 ⇒ 可用量按口径回升到数量本身
        Assert.Equal(StockQty, await UseableAsync(connection));
        // 留痕：结案动作本身 + 钩子写下的释放（审计按基线之上的增量断言，不删历史行）
        var actions = await AuditActionsAsync(auditBaseline);
        Assert.Contains("ENDCASE", actions);
        Assert.Contains("UNFREEZE", actions);
    }

    // ===== ② 无预留 =====

    [Fact]
    public async Task 无预留的制令结案成功且释放零行()
    {
        var (blocked, steps) = await FinishViaCoreAsync(finish: true, EndcaseAction());

        Assert.Null(blocked);
        var step = Assert.Single(steps, item => item.EffectKey == InventoryReleaseBySourceHandler.EffectKeyName);
        // 影响 0 行是**正常**结果（没有占料可释放），既不是失败，也不该被记成"跳过"
        Assert.Equal(0, step.RowsAffected);
        Assert.Equal(EffectStepOutcome.Ran, step.Outcome);

        await using var connection = await OpenAsync();
        Assert.True(await ScalarAsync<bool>(connection, MasterFinishedSql));
    }

    // ===== ③ 取消结案收回 =====

    [Fact]
    public async Task 取消结案收回由结案释放的预留()
    {
        await ReserveAsync(ReserveQty);
        Assert.True((await FinishAsync(finish: true, EndcaseAction(), UnendcaseAction())).Status == RecordAccessStatus.Ok);

        // 中间态必须成立：结案确实释放了（否则"取消结案收回"这条断言在"什么都没发生"时也会通过）
        await using (var released = await OpenAsync())
        {
            Assert.Equal("C", await ReserveStatusAsync(released));
            Assert.Equal("ENDCASE", await ReserveReleaseKindAsync(released));
            Assert.Equal(StockQty, await UseableAsync(released));
        }

        Assert.True((await FinishAsync(finish: false, EndcaseAction(), UnendcaseAction())).Status == RecordAccessStatus.Ok);

        await using var connection = await OpenAsync();
        Assert.False(await ScalarAsync<bool>(connection, MasterFinishedSql));
        Assert.False(await ScalarAsync<bool>(connection, DetailAllFinishedSql));
        Assert.Equal("A", await ReserveStatusAsync(connection));
        Assert.Equal("", await ReserveReleaseKindAsync(connection));
        // 占用回来了 ⇒ 可用量按口径重新扣减（100 − 20）
        Assert.Equal(StockQty - ReserveQty, await UseableAsync(connection));
    }

    // ===== ④ 边界：人工释放的预留不因取消结案复活 =====

    [Fact]
    public async Task 人工释放的预留不因取消结案复活()
    {
        // 同一张制令名下两格料：一格被人手工释放，另一格留给结案去释放。
        // 取消结案之后两者必须**走出不同的结果**——这正是"逐行归因"与"该来源所有 C 行"的分野。
        await ReserveAsync(ReserveQty, ReserveProduct);
        await ReserveAsync(ReserveQty, ReserveProduct2);
        await ManualReleaseAsync("20", ReserveProduct);
        await using (var check = await OpenAsync())
        {
            // 人工释放与结案释放的差别**只**在归因：前者不留归因
            Assert.Equal("C", await ReserveStatusAsync(check, ReserveProduct));
            Assert.Equal("", await ReserveReleaseKindAsync(check, ReserveProduct));
        }

        Assert.True((await FinishAsync(finish: true, EndcaseAction(), UnendcaseAction())).Status == RecordAccessStatus.Ok);
        await using (var released = await OpenAsync())
        {
            // 结案释放的是另一格（人工释放过的那格已经没有有效占用了）
            Assert.Equal("C", await ReserveStatusAsync(released, ReserveProduct2));
            Assert.Equal("ENDCASE", await ReserveReleaseKindAsync(released, ReserveProduct2));
        }

        Assert.True((await FinishAsync(finish: false, EndcaseAction(), UnendcaseAction())).Status == RecordAccessStatus.Ok);

        await using var connection = await OpenAsync();
        // 取消结案只收回"由结案释放"的那格；人工释放的那格仍为 C，可用量也不被它二次扣减
        Assert.Equal("C", await ReserveStatusAsync(connection, ReserveProduct));
        Assert.Equal("", await ReserveReleaseKindAsync(connection, ReserveProduct));
        Assert.Equal(StockQty, await UseableAsync(connection, ReserveProduct));
        Assert.Equal("A", await ReserveStatusAsync(connection, ReserveProduct2));
        Assert.Equal("", await ReserveReleaseKindAsync(connection, ReserveProduct2));
        Assert.Equal(StockQty - ReserveQty, await UseableAsync(connection, ReserveProduct2));
    }

    // ===== ⑤ 重复点击 =====

    [Fact]
    public async Task 重复结案与重复取消结案被拒且零写入()
    {
        await ReserveAsync(ReserveQty);
        Assert.True((await FinishAsync(finish: true, EndcaseAction(), UnendcaseAction())).Status == RecordAccessStatus.Ok);

        var again = await FinishAsync(finish: true, EndcaseAction(), UnendcaseAction());
        Assert.Equal(RecordAccessStatus.ValidationFailed, again.Status);
        Assert.Equal("ENDCASE_STATE_CONFLICT", again.ErrorCode);

        await using (var check = await OpenAsync())
        {
            // 零写入：主表标记仍是结案、预留没有被"再释放一次"弄脏，也没有反向收回
            Assert.True(await ScalarAsync<bool>(check, MasterFinishedSql));
            Assert.Equal("C", await ReserveStatusAsync(check));
            Assert.Equal("ENDCASE", await ReserveReleaseKindAsync(check));
            Assert.Equal(StockQty, await UseableAsync(check));
        }

        Assert.True((await FinishAsync(finish: false, EndcaseAction(), UnendcaseAction())).Status == RecordAccessStatus.Ok);
        var againReverse = await FinishAsync(finish: false, EndcaseAction(), UnendcaseAction());
        Assert.Equal(RecordAccessStatus.ValidationFailed, againReverse.Status);
        Assert.Equal("ENDCASE_STATE_CONFLICT", againReverse.ErrorCode);

        await using var connection = await OpenAsync();
        Assert.False(await ScalarAsync<bool>(connection, MasterFinishedSql));
        Assert.Equal("A", await ReserveStatusAsync(connection));
        Assert.Equal(StockQty - ReserveQty, await UseableAsync(connection));
    }

    // ===== ⑥ 明细腿与 MRP 口径 =====

    [Fact]
    public async Task 明细与主表同步置位_结案后不再计入MRP未领需求()
    {
        // 结案前：两张明细行的未领量（10 + 6）都计入 MRP 口径
        Assert.Equal(16d, await MrpNotGetAsync());

        Assert.True((await FinishAsync(finish: true, EndcaseAction())).Status == RecordAccessStatus.Ok);

        await using (var connection = await OpenAsync())
        {
            Assert.True(await ScalarAsync<bool>(connection, MasterFinishedSql));
            Assert.Equal(2, await ScalarAsync<int>(connection, """
                SELECT COUNT(*) FROM dbo.MOC_PRODUCE_D
                 WHERE PRODUCE_TYPE = N'ZZENDC' AND PRODUCE_NO = N'ZZENDC-MO1' AND ISNULL(FINISHED_TAG,0) = 1;
                """));
        }
        // 明细已结案 ⇒ 该单的"生产未领"需求不再计入（这正是旧系统结案在读侧的行为）
        Assert.Equal(0d, await MrpNotGetAsync());

        Assert.True((await FinishAsync(finish: false, EndcaseAction())).Status == RecordAccessStatus.Ok);
        // 取消结案把明细复位，需求重新计入——两个方向对称
        Assert.Equal(16d, await MrpNotGetAsync());
    }

    // ===== ⑦ 效果链失败整笔回滚 =====

    [Fact]
    public async Task 效果链失败时结案整笔回滚()
    {
        await ReserveAsync(ReserveQty);

        // seq 8 是保留键 flow-trigger（注册表里标 Reserved ⇒ 管线直接判"尚未实现"），
        // 它在 seq 7 已经把预留释放掉之后才失败：失败的整链回滚必须把那一手也一起退掉。
        var (blocked, _) = await FinishViaCoreAsync(finish: true, EndcaseAction(seq: 7), UnimplementedAction(seq: 8));

        Assert.NotNull(blocked);
        Assert.Equal("WORKFLOW_FAILED", blocked.ErrorCode);

        await using var connection = await OpenAsync();
        // 主表标记也未翻（不存在"标记翻了但预留没处理"的半截状态）
        Assert.False(await ScalarAsync<bool>(connection, MasterFinishedSql));
        Assert.False(await ScalarAsync<bool>(connection, DetailAllFinishedSql));
        Assert.Equal("A", await ReserveStatusAsync(connection));
        Assert.Equal("", await ReserveReleaseKindAsync(connection));
        Assert.Equal(StockQty - ReserveQty, await UseableAsync(connection));
    }

    // ===== 动作配置 =====

    private static object EndcaseAction(int seq = 7) => new
    {
        seq,
        eventCode = "ENDCASE",
        effectKey = InventoryReleaseBySourceHandler.EffectKeyName,
        effectName = "结案释放预留",
        enabled = true,
        failMode = "BLOCK",
        condition = (string?)null,
        @params = (string?)null,
        reverse = (string?)null,
        ops = Array.Empty<object>(),
    };

    private static object UnendcaseAction(int seq = 7) => new
    {
        seq,
        eventCode = "UNENDCASE",
        effectKey = InventoryReleaseBySourceHandler.EffectKeyName,
        effectName = "取消结案收回预留",
        enabled = true,
        failMode = "BLOCK",
        condition = (string?)null,
        @params = (string?)null,
        reverse = (string?)null,
        ops = Array.Empty<object>(),
    };

    private static object UnimplementedAction(int seq) => new
    {
        seq,
        eventCode = "ENDCASE",
        effectKey = "flow-trigger",
        effectName = "必失败探针",
        enabled = true,
        failMode = "BLOCK",
        condition = (string?)null,
        @params = (string?)null,
        reverse = (string?)null,
        ops = Array.Empty<object>(),
    };

    // ===== 驱动 =====

    private static IReadOnlyList<string> KeyValues => [ProduceType, ProduceNo];

    private Task<RecordSaveResult> FinishAsync(bool finish, params object[] actions) =>
        CreateApprovals().FinishAsync(Definition(actions), KeyValues, finish, TestUser, TestUser, null, CancellationToken.None);

    /// <summary>跑事务内核心（提交），把逐步结果取回来——Steps 只在核心层可见。</summary>
    private async Task<(RecordSaveResult? Blocked, IReadOnlyList<EffectStepResult> Steps)> FinishViaCoreAsync(
        bool finish, params object[] actions)
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var outcome = await CreateApprovals().RunFinishCoreAsync(
            connection, transaction, Definition(actions), KeyValues, finish, TestUser, TestUser, CancellationToken.None);
        if (outcome.Blocked is null)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }
        return (outcome.Blocked, outcome.Steps);
    }

    private static WorkbenchApprovalService CreateApprovals()
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(
            connections, new HttpContextAccessor(), provider, Options.Create(new AuditSettings()));
        var engine = new EffectEngineInvoker(
            new EffectEngineSettings { Enabled = true },
            new EffectPlanLoader(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            NullLogger<EffectEngineInvoker>.Instance);
        var workflow = new WorkflowEngine(connections, auditWriter, provider, engine, NullLogger<WorkflowEngine>.Instance);
        return new WorkbenchApprovalService(
            connections, auditWriter, workflow, engine, new WorkbenchIdempotency(),
            NullLogger<WorkbenchApprovalService>.Instance);
    }

    private static WorkbenchDefinition Definition(params object[] actions) =>
        new(ModuleId: SourceModule,
            Title: "制令单",
            MasterTable: MasterTable,
            DetailTable: DetailTable,
            MasterFields: [],
            DetailFields: [],
            DefaultSort: null,
            HasAdd: true,
            HasEdit: true,
            DetailNoSave: false,
            MasterPkOrder: ["PRODUCE_TYPE", "PRODUCE_NO"],
            DetailNoFields: string.Empty,
            HasWorkflow: false,
            UserId: TestUser,
            ExecTag: "Z",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            BusinessActions: JsonSerializer.SerializeToElement(actions),
            EffectEngine: JsonSerializer.SerializeToElement(new { enabled = true }));

    // ===== 夹具与直查 =====

    /// <summary>造一笔有效预留，并把可用量按**服务的口径**落列（不自己手算减法）。</summary>
    private async Task ReserveAsync(double quantity, string product = ReserveProduct)
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection, """
            INSERT INTO dbo.INV_RESERVE
                (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SOURCE_TYPE, SOURCE_NO, RESERVE_QTY, REASON, STATUS,
                 CREATE_PERSON, CREATE_DATE)
                VALUES (@pro, @depot, @location, @batch, N'1502', N'ZZENDC-MO1', @qty, N'ZZENDC 用例', N'A', @user, GETDATE());
            """, ("@pro", product), ("@depot", Depot), ("@location", Location), ("@batch", Batch),
            ("@qty", quantity), ("@user", TestUser));
        await InventoryAvailabilityService.SyncSlotsAsync(connection, null, [Slot(product)], CancellationToken.None);
    }

    /// <summary>走**真实的手工释放路径**（模块 1303 的 `inventory-release`），把预留释放掉。</summary>
    private async Task ManualReleaseAsync(string quantity, string product = ReserveProduct)
    {
        await using var connection = await OpenAsync();
        var context = new DocumentActionContext(
            ModuleId: StockModule,
            Definition: StockDefinition(),
            Form: StockForm(),
            Connection: connection,
            Transaction: null!,
            RecordKey: SlotRecordKey(product),
            KeyValues: [product, Depot, Location, Batch],
            MasterPkOrder: [InventoryQueryService.ProductColumn, InventoryQueryService.DepotColumn,
                InventoryQueryService.LocationColumn, InventoryQueryService.BatchColumn],
            Parameters: new Dictionary<string, string?>
            {
                [InventoryFreezeHandler.QuantityParameter] = quantity,
                [InventoryFreezeHandler.ReasonParameter] = "ZZENDC 用例：人工释放",
            },
            Executor: TestUser,
            ExecutorUserId: TestUser,
            DataFilter: null,
            IdempotencyKey: Guid.NewGuid().ToString("N"),
            Confirm: true,
            Preview: false);

        var result = await new InventoryReleaseHandler(_audit).ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(DocumentActionOutcome.Refreshed, result.Outcome);
    }

    private static InventoryAvailabilityService.SlotKey Slot(string product) => new(product, Depot, Location, Batch);

    private static WorkbenchDefinition StockDefinition() =>
        new(ModuleId: StockModule,
            Title: "料件库存资料",
            MasterTable: "INV_PRO_DEPOT",
            DetailTable: null,
            MasterFields: [],
            DetailFields: [],
            DefaultSort: null,
            HasAdd: false,
            HasEdit: true,
            DetailNoSave: false,
            MasterPkOrder: [InventoryQueryService.ProductColumn, InventoryQueryService.DepotColumn,
                InventoryQueryService.LocationColumn, InventoryQueryService.BatchColumn],
            DetailNoFields: string.Empty,
            HasWorkflow: false);

    private static FormDefinition StockForm() =>
        new(StockModule, "料件库存资料", "INV_PRO_DEPOT", null, false, true, "view", [], [],
            [InventoryQueryService.ProductColumn, InventoryQueryService.DepotColumn,
                InventoryQueryService.LocationColumn, InventoryQueryService.BatchColumn],
            string.Empty, string.Empty);

    private const string MasterFinishedSql =
        "SELECT ISNULL(FINISHED_TAG,0) FROM dbo.MOC_PRODUCE_M WHERE PRODUCE_TYPE = N'ZZENDC' AND PRODUCE_NO = N'ZZENDC-MO1';";

    private const string DetailAllFinishedSql = """
        SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_D
                                  WHERE PRODUCE_TYPE = N'ZZENDC' AND PRODUCE_NO = N'ZZENDC-MO1'
                                    AND ISNULL(FINISHED_TAG,0) = 0) THEN 0 ELSE 1 END;
        """;

    /// <summary>审计基线：本次动作之前的最大事件号（审计只增不删，增量断言靠它）。</summary>
    private static async Task<long> AuditBaselineAsync()
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<long>(connection, "SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT;");
    }

    /// <summary>基线之上、落在本单上的审计动作清单。</summary>
    private static async Task<IReadOnlyList<string>> AuditActionsAsync(long baseline)
    {
        await using var connection = await OpenAsync();
        var actions = new List<string>();
        await using var command = new SqlCommand(
            "SELECT RTRIM(ACTION) FROM dbo.AUDIT_EVENT WHERE EVENT_ID > @baseline AND RESOURCE_KEY = @key;",
            connection);
        command.Parameters.AddWithValue("@baseline", baseline);
        command.Parameters.AddWithValue("@key", DocumentRecordKey);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(0));
        }
        return actions;
    }

    private static Task<string?> ReserveStatusAsync(SqlConnection connection, string product = ReserveProduct) =>
        ScalarAsync<string>(connection,
            "SELECT RTRIM(STATUS) FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;",
            ("@pro", product));

    private static Task<string?> ReserveReleaseKindAsync(SqlConnection connection, string product = ReserveProduct) =>
        ScalarAsync<string>(connection,
            "SELECT LTRIM(RTRIM(ISNULL(RELEASE_KIND, N''))) FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(PRO_NO)) = @pro;",
            ("@pro", product));

    private static Task<double> UseableAsync(SqlConnection connection, string product = ReserveProduct) =>
        ScalarAsync<double>(connection,
            "SELECT ISNULL(USEABLE_QTY,0) FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;",
            ("@pro", product));

    /// <summary>
    /// MRP「生产未领」口径：与 <see cref="MrpRecalcService.RecalcSql"/> 的 `NOT_GET_QTY` 子查询同一判据，
    /// 只把范围收窄到本例的制令单。不跑全量重算——那会把整张 PRODUCT 的占用列清零重算
    /// （并对 `COP_ORDER_M`/`PRODUCT` 取 TABLOCKX），远超本用例的验证范围。
    /// </summary>
    private static async Task<double> MrpNotGetAsync()
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<double>(connection, """
            SELECT ISNULL(SUM(d.NEED_QTY - d.USED_QTY), 0)
              FROM dbo.MOC_PRODUCE_M m
              JOIN dbo.MOC_PRODUCE_D d
                ON m.PRODUCE_TYPE = d.PRODUCE_TYPE AND m.PRODUCE_NO = d.PRODUCE_NO
             WHERE m.CONFIRM_TAG = 1 AND d.FINISHED_TAG = 0 AND d.NEED_QTY > d.USED_QTY
               AND m.PRODUCE_TYPE = N'ZZENDC' AND m.PRODUCE_NO = N'ZZENDC-MO1';
            """);
    }

    private static DbConnectionFactory Connections() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build());

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
}
