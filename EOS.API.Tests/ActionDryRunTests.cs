using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 助手动作的预演验收（真库）：**预演零落库**——预演跑的是真实写管线，在真实事务里跑完整条路径
/// （校验、单号、效果链）后无条件回滚，因此"不落库"只有把前后数据逐项对拍才能证明。
///
/// <para>
/// 三项必须逐项成立：主表行数与关键字段不变、单号序列被释放（不跳号也不占用）、流程待办不变。
/// 夹具全部落在自造键上（单别 <c>ZZDRY</c>），收尾只删自己造的行。
/// </para>
/// </summary>
[Collection("live-database")]
public sealed class ActionDryRunTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1403;
    private const string MasterTable = "COP_CHAFFER_M";
    private const string TestType = "ZZDRY";
    private const string SeededNo = "ZZDRY-0001";
    private const string TestUser = "ZZDRY0001";
    private const string TestEmployee = "ZZDRY 测试员";

    private DbConnectionFactory _connections = null!;

    public async Task InitializeAsync()
    {
        _connections = AssistantActionTestHarness.Connections(ConnectionString);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
    }

    /// <summary>
    /// 收尾只删自己造的行。幂等键是**服务端算出来的哈希**（不含用例前缀），
    /// 因此按同样的输入重算一遍再按值删——不能按 M_IDX 整体删，那会动到真实单据的幂等记录。
    /// </summary>
    private static async Task CleanupAsync(SqlConnection connection)
    {
        await ExecAsync(connection,
            "DELETE FROM dbo.COP_CHAFFER_M WHERE CHAFFER_TYPE = @type;", ("@type", TestType));
        foreach (var key in new[] { "zzdry-insert-preview", "zzdry-delete-preview", ServerKeyForInsert() })
        {
            await ExecAsync(connection,
                "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE IDEMPOTENCY_KEY = @key;", ("@key", key));
        }
    }

    /// <summary>本用例里"助手执行新增"那一次会算出的服务端幂等键（确定性，可在收尾时重算）。</summary>
    private static string ServerKeyForInsert()
    {
        var request = AssistantInsertRequest();
        return AssistantActionIdempotency.Create(4242, "call_zzdry_1",
            AssistantRecordActionNames.For(request.Kind),
            AssistantActionIdempotency.Canonicalize(request, request.Rows[0]));
    }

    private static AssistantActionRequest AssistantInsertRequest() => new(
        ModuleId, AssistantRecordActionKind.Insert,
        [new AssistantActionRow([],
            new Dictionary<string, string?> { ["CHAFFER_TYPE"] = TestType, ["CHAFFER_DATE"] = "2026-09-29" })]);

    // ===== ① 预演新增：零落库、无审计、不占幂等键、单号序列不变 =====

    [Fact]
    public async Task 预演新增_零落库_不留审计_不占幂等键_单号序列不变()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            var expectedBillNo = await BillNoGenerator.PeekAsync(connection, transaction, ModuleId, default);
            await transaction.RollbackAsync();
            Assert.False(string.IsNullOrWhiteSpace(expectedBillNo), "模块 1403 未配置默认自动单别，用例前提不成立。");

            var sequenceBefore = await SequenceAsync(connection);
            var auditBefore = await MaxEventIdAsync(connection);
            var tasksBefore = await TaskCountAsync(connection);

            var handler = Handler();
            const string idempotencyKey = "zzdry-insert-preview";
            var result = await handler.CreateRecordAsync(
                Definition(), Form(),
                new SaveRecordRequest(
                    new Dictionary<string, string?>
                    {
                        ["CHAFFER_TYPE"] = TestType,
                        ["CHAFFER_DATE"] = "2026-09-29",
                    },
                    IdempotencyKey: idempotencyKey),
                TestEmployee, TestUser, dataFilter: null, CancellationToken.None, dryRun: true);

            Assert.Equal(RecordAccessStatus.Ok, result.Status);
            Assert.NotNull(result.Key);
            // 单号是预演里真实生成的：与"取号前的下一个号"对齐，才能说明号确实被取用又被释放
            Assert.Contains(expectedBillNo!, result.Key!);
            Assert.Contains(TestType, result.Key!);

            Assert.Equal(0, await MasterCountAsync(connection));
            Assert.Equal(auditBefore, await MaxEventIdAsync(connection));
            Assert.Equal(sequenceBefore, await SequenceAsync(connection));
            Assert.Equal(tasksBefore, await TaskCountAsync(connection));
            Assert.Equal(0, await IdempotencyCountAsync(connection, idempotencyKey));
        }
    }

    // ===== ② 预演修改：字段与审计逐项不变 =====

    [Fact]
    public async Task 预演修改_字段保持原值_审计不增行()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SeedAsync(connection);
        var auditBefore = await MaxEventIdAsync(connection);

        var result = await Handler().UpdateRecordAsync(
            Definition(), Form(), [TestType, SeededNo],
            new SaveRecordRequest(new Dictionary<string, string?> { ["REMARK"] = "预演改过的备注" }),
            TestEmployee, TestUser, dataFilter: null, CancellationToken.None, dryRun: true);

        Assert.Equal(RecordAccessStatus.Ok, result.Status);
        Assert.Equal("初始备注", await RemarkAsync(connection));
        Assert.Equal(auditBefore, await MaxEventIdAsync(connection));
    }

    // ===== ③ 预演删除：行仍在 =====

    [Fact]
    public async Task 预演删除_行仍在_且不占幂等键()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SeedAsync(connection);
        var auditBefore = await MaxEventIdAsync(connection);
        const string idempotencyKey = "zzdry-delete-preview";

        var result = await Handler().DeleteRecordAsync(
            Definition(), Form(), [TestType, SeededNo], TestUser, dataFilter: null,
            idempotencyKey: idempotencyKey, token: CancellationToken.None, dryRun: true);

        Assert.Equal(RecordAccessStatus.Ok, result.Status);
        Assert.Equal(1, await MasterCountAsync(connection));
        Assert.Equal(auditBefore, await MaxEventIdAsync(connection));
        Assert.Equal(0, await IdempotencyCountAsync(connection, idempotencyKey));
    }

    // ===== ④ 逐行摊开 + 级联影响面 + 预演留痕（走助手动作层）=====

    [Fact]
    public async Task 预演删除_逐行列出主键_并给出效果链影响面_且留一条预演审计()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SeedAsync(connection);
        var auditBefore = await DryRunAuditCountAsync(connection);

        var service = BuildService(Definition(ImpactActions()));

        var preview = await service.PreviewAsync(TestUser, TestEmployee, new AssistantActionRequest(
            ModuleId, AssistantRecordActionKind.Delete,
            [new AssistantActionRow([TestType, SeededNo])]), CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.True(row.Allowed, row.DenialMessage);
        // 逐行摊开：结论必须点名这一行是哪一张单，而不是"将删除 1 行"
        Assert.Equal(2, row.Keys.Count);
        Assert.Contains(SeededNo, row.Keys);
        // 级联影响面：该模块声明的生效链会碰到的目标表.字段必须看得见
        Assert.NotNull(row.Impacts);
        Assert.Contains(row.Impacts!, impact => impact.TargetTable == "INV_PRO_DEPOT" && impact.OpCode == "ACCUM");
        // 预演留痕：best-effort 的一条 DRYRUN 审计
        Assert.Equal(auditBefore + 1, await DryRunAuditCountAsync(connection));
        // 行走的仍是同一条写管线：预演不留业务痕迹
        Assert.Equal(1, await MasterCountAsync(connection));
    }

    // ===== ⑤ 助手写入：审计身份可分辨，权限主体仍是真人 =====

    [Fact]
    public async Task 助手写入_审计记为Agent_经办人仍是真实用户()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var service = BuildService(Definition());

        var execution = await service.ExecuteAsync(
            TestUser, TestEmployee, AssistantInsertRequest(),
            conversationId: 4242, toolCallId: "call_zzdry_1", CancellationToken.None);

        var row = Assert.Single(execution.Rows);
        Assert.True(row.Succeeded, row.Message);
        Assert.Equal(1, await MasterCountAsync(connection));

        await using var command = new SqlCommand("""
            SELECT TOP 1 ACTOR_TYPE, CLIENT_TYPE, ACTOR_USER_ID
              FROM dbo.AUDIT_EVENT
             WHERE M_IDX = @module AND ACTION = N'INSERT' AND RESULT = 1
             ORDER BY EVENT_ID DESC;
            """, connection);
        command.Parameters.AddWithValue("@module", ModuleId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "助手写入没有留下 INSERT 审计。");
        Assert.Equal(3, reader.GetByte(0));                       // ACTOR_TYPE = Agent（代表用户）
        Assert.Equal(3, reader.GetByte(1));                       // CLIENT_TYPE = Agent
        Assert.Equal(TestUser, reader.GetString(2));              // ACTOR_USER_ID 仍是真人
        await reader.DisposeAsync();

        // 幂等键落在服务端生成的键上（不是模型给的、也不是客户端给的）
        Assert.Equal(1, await IdempotencyCountAsync(connection, row.IdempotencyKey));
    }

    // ===== 装配 =====

    private WorkbenchCommandHandler Handler()
        => AssistantActionTestHarness.CommandHandler(
            _connections, ConnectionString, AssistantActionTestHarness.AuditWriter(_connections));

    /// <summary>
    /// 助手动作层 + **同一个**审计写入器：写管线与动作层必须共用它，
    /// 否则"这次写是助手发起的"这个标记传不到审计上（两处各建一个写入器就断链了）。
    /// </summary>
    private AssistantRecordActionService BuildService(WorkbenchDefinition definition)
    {
        var agentContext = AssistantActionTestHarness.AgentContext();
        var auditWriter = AssistantActionTestHarness.AuditWriter(_connections, agentContext);
        var definitions = new AssistantActionTestHarness.FixedDefinitionSource(definition, Form());
        var policy = AssistantActionTestHarness.Policy(
            definitions, new AssistantActionTestHarness.FixedPermissions(AssistantActionTestHarness.Rights()),
            ModuleId);
        return AssistantActionTestHarness.ActionService(
            new AssistantActionGate(policy),
            AssistantActionTestHarness.CommandHandler(_connections, ConnectionString, auditWriter),
            _connections, agentContext, auditWriter);
    }

    private static WorkbenchDefinition Definition(JsonElement? businessActions = null) => new(
        ModuleId: ModuleId, Title: "客户询价单", MasterTable: MasterTable, DetailTable: null,
        MasterFields: [], DetailFields: [], DefaultSort: null,
        HasAdd: true, HasEdit: true, DetailNoSave: false,
        MasterPkOrder: ["CHAFFER_TYPE", "CHAFFER_NO"], DetailNoFields: "", HasWorkflow: false,
        UserId: TestUser, ExecTag: "A",
        FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        BusinessRule: new ModuleBusinessRule(ModuleId, true, "CHAFFER_NO", "CHAFFER_TYPE"),
        BusinessActions: businessActions);

    /// <summary>
    /// 一条"批核生效会动库存"的效果链配置：删除的影响面里就必须看得见它在碰哪张表哪个字段。
    /// </summary>
    private static JsonElement ImpactActions() => JsonSerializer.SerializeToElement(
        JsonDocument.Parse("""
            [{"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","effectName":"库存移动","enabled":true,
              "failMode":"BLOCK",
              "ops":[{"opSeq":1,"targetTable":"INV_PRO_DEPOT","targetField":"QTY","opCode":"ACCUM",
                      "sourceScope":"DETAIL","sourceField":"QTY"}]}]
            """).RootElement);

    private static FormDefinition Form() => new(
        ModuleId, "客户询价单", MasterTable, null, true, true, "new",
        [
            Field("CHAFFER_TYPE", "询价单别", "nchar", primaryKey: true),
            Field("CHAFFER_NO", "询价单号", "nchar", primaryKey: true),
            Field("CHAFFER_DATE", "询价日期", "datetime"),
            Field("CLIENT_ID", "客户编号", "nchar"),
            Field("REMARK", "备注", "nvarchar"),
        ],
        [], ["CHAFFER_TYPE", "CHAFFER_NO"], string.Empty, string.Empty);

    private static FormFieldDefinition Field(
        string key, string label, string dataType, bool primaryKey = false) =>
        new(key, label, dataType, 100, null, false, null, null, null, false, true, false, false, null,
            [], primaryKey, false, false, false, false, false, null);

    // ===== 真库探针 =====

    private static async Task SeedAsync(SqlConnection connection) =>
        await ExecAsync(connection, """
            INSERT INTO dbo.COP_CHAFFER_M
                (CHAFFER_TYPE, CHAFFER_NO, CHAFFER_DATE, CLIENT_ID, REMARK, CONFIRM_TAG, CREATE_PERSON, LAST_UPDATE_BY)
            VALUES (@type, @no, '2026-09-01', NULL, N'初始备注', 0, N'ZZDRY', N'ZZDRY');
            """, ("@type", TestType), ("@no", SeededNo));

    private static Task<int> MasterCountAsync(SqlConnection connection) =>
        ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.COP_CHAFFER_M WHERE CHAFFER_TYPE = @type;", ("@type", TestType));

    private static Task<string?> RemarkAsync(SqlConnection connection) =>
        ScalarAsync<string>(connection, "SELECT REMARK FROM dbo.COP_CHAFFER_M WHERE CHAFFER_TYPE = @type AND CHAFFER_NO = @no;",
            ("@type", TestType), ("@no", SeededNo));

    private static Task<long> MaxEventIdAsync(SqlConnection connection) =>
        ScalarAsync<long>(connection, "SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT;");

    private static Task<int> DryRunAuditCountAsync(SqlConnection connection) =>
        ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE M_IDX = @module AND ACTION = @action;",
            ("@module", ModuleId), ("@action", AssistantRecordActionService.DryRunAuditAction));

    private static Task<int> IdempotencyCountAsync(SqlConnection connection, string key) =>
        ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.WORKBENCH_IDEMPOTENCY WHERE IDEMPOTENCY_KEY = @key;",
            ("@key", key));

    /// <summary>流程待办：预演不得发起流程（本模块本就没有流程定义，这里守的是"preview 前后不变"）。</summary>
    private static async Task<string> TaskCountAsync(SqlConnection connection)
    {
        var pending = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.WF_MYTASK;");
        var monitor = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.WF_MONITOR;");
        return $"{pending}/{monitor}";
    }

    /// <summary>该模块默认单别在当前日期段的流水计数器（不存在记为空）。</summary>
    private static async Task<string> SequenceAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("""
            SELECT BILL_CODE, PERIOD_KEY, CURRENT_NO, TITLE
              FROM dbo.BILL_NO_SEQUENCE
             WHERE BILL_CODE IN (SELECT LTRIM(RTRIM(BILL_CODE)) FROM dbo.BILLKIND
                                  WHERE B_M_IDX = @module AND IS_DEFAULT = 1 AND IS_AUTO = 1);
            """, connection);
        command.Parameters.AddWithValue("@module", ModuleId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return "<none>";
        }
        return $"{reader.GetString(0)}/{reader.GetString(1)}/{reader.GetInt64(2)}/{reader.GetString(3)}";
    }

    private static async Task ExecAsync(
        SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(
        SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
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
