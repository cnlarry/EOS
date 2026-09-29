using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 操作卡的端到端：**从"用户点了确认执行"走到落库**，并把这条链路上必须留下的痕迹逐条断言。
///
/// <para>
/// 四条硬事实（也是本文件存在的理由）：
/// <list type="number">
/// <item>单据**真的落库**（不是"接口返回 200"）；</item>
/// <item>审计记为 **Agent 代表用户**，而经办人是真人；</item>
/// <item>**确认事件单独成条**——AI 发起一条、用户确认一条，"谁在什么时候点了确认"可独立检索；</item>
/// <item>被拒的路径**不落库**，且原因可读（含"你没有这个权限"与"记录不在你的数据范围内"）。</item>
/// </list>
/// </para>
/// </summary>
[Collection("live-database")]
public sealed class AssistantActionEndToEndTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1403;
    private const string MasterTable = "COP_CHAFFER_M";
    private const string TestType = "ZZE2E";
    private const string SeededNo = "ZZE2E-0001";
    private const string TestUser = "ZZE2E001";
    private const string TestEmployee = "ZZE2E";

    private const string InsertConfirmKey = "zze2e-insert";
    private const string ReplayConfirmKey = "zze2e-replay";
    private const string DeleteConfirmKey = "zze2e-delete";
    private const string DeniedConfirmKey = "zze2e-denied";

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
    /// 只删自己造的行。审计**按设计保留**（因此计数断言一律取增量），幂等键按同样输入重算后按值删。
    /// </summary>
    private static async Task CleanupAsync(SqlConnection connection)
    {
        await ExecAsync(connection, "DELETE FROM dbo.COP_CHAFFER_M WHERE CHAFFER_TYPE = @type;", ("@type", TestType));
        foreach (var (key, request) in new (string, AssistantActionRequest)[]
        {
            (InsertConfirmKey, InsertRequest()),
            (ReplayConfirmKey, InsertRequest()),
            (DeleteConfirmKey, DeleteRequest()),
            (DeniedConfirmKey, BadDateRequest()),
        })
        {
            await ExecAsync(connection,
                "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE IDEMPOTENCY_KEY = @key;",
                ("@key", UiKey(key, request)));
        }
    }

    // ===== ① 确认执行 ⇒ 落库 + Agent 身份 + 确认事件单独成条 =====

    [Fact]
    public async Task 确认执行新增_落库_审计记为Agent_且确认事件单独成条()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var confirmsBefore = await ConfirmCountAsync(connection);
        var insertsBefore = await InsertAuditCountAsync(connection);

        var execution = await Service().ConfirmAndExecuteAsync(
            TestUser, TestEmployee, InsertRequest(), InsertConfirmKey, CancellationToken.None);

        var row = Assert.Single(execution.Rows);
        Assert.True(row.Succeeded, row.Message);
        Assert.Null(execution.ModuleDenialCode);

        // 落库：不是"返回了 200"，而是表里真的有这一行
        Assert.Equal(1, await MasterCountAsync(connection));

        // Agent 身份：助手代表的写入，经办人仍是真人（权限主体恒为使用者）
        Assert.Equal(1, await InsertAuditCountAsync(connection) - insertsBefore);
        var (actorType, clientType, actorUserId) = await LatestInsertAuditAsync(connection);
        Assert.Equal((byte)AuditActorType.Agent, actorType);
        Assert.Equal((byte)AuditClientType.Agent, clientType);
        Assert.Equal(TestUser, actorUserId);

        // 确认事件**单独成条**：它与业务写入是两行，动作码不同，可独立检索
        Assert.Equal(1, await ConfirmCountAsync(connection) - confirmsBefore);
        Assert.Equal((byte)AuditActorType.Agent, await LatestConfirmActorTypeAsync(connection));
    }

    // ===== ② 幂等：同一张卡重复点确认，业务只写一次 =====

    [Fact]
    public async Task 同一确认键重放_业务只写一次_而确认按点击各记一条()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var confirmsBefore = await ConfirmCountAsync(connection);

        var service = Service();
        var first = await service.ConfirmAndExecuteAsync(
            TestUser, TestEmployee, InsertRequest(), ReplayConfirmKey, CancellationToken.None);
        var replay = await service.ConfirmAndExecuteAsync(
            TestUser, TestEmployee, InsertRequest(), ReplayConfirmKey, CancellationToken.None);

        Assert.True(Assert.Single(first.Rows).Succeeded);
        Assert.True(Assert.Single(replay.Rows).Succeeded);
        Assert.Equal(1, await MasterCountAsync(connection));
        Assert.Equal(1, await IdempotencyCountAsync(connection, UiKey(ReplayConfirmKey, InsertRequest())));
        // 确认事件记的是"点击"：点了两次就有两条——写入去重，点击不去重
        Assert.Equal(2, await ConfirmCountAsync(connection) - confirmsBefore);
    }

    // ===== ③ 被拒就不落库，且原因可读 =====

    [Fact]
    public async Task 预演判不下来的行_不落库_且给出可读原因()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var execution = await Service().ConfirmAndExecuteAsync(
            TestUser, TestEmployee, BadDateRequest(), DeniedConfirmKey, CancellationToken.None);

        var row = Assert.Single(execution.Rows);
        Assert.False(row.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(row.Code));
        Assert.False(string.IsNullOrWhiteSpace(row.Message));
        Assert.Equal(0, await MasterCountAsync(connection));
    }

    // ===== ④ 无权限 ⇒ 被拒且不落库（明说原因，不含糊）=====

    [Fact]
    public async Task 无浏览权限的用户发起执行_模块级被拒_且不落库()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var execution = await Service(AssistantActionTestHarness.Rights(browse: false))
            .ConfirmAndExecuteAsync(TestUser, TestEmployee, InsertRequest(), DeniedConfirmKey, CancellationToken.None);

        Assert.Empty(execution.Rows);
        Assert.False(string.IsNullOrWhiteSpace(execution.ModuleDenialCode));
        Assert.False(string.IsNullOrWhiteSpace(execution.ModuleDenialMessage));
        Assert.Equal(0, await MasterCountAsync(connection));
    }

    [Fact]
    public async Task 没有删除动作位的用户发起删除_被拒_且记录仍在()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SeedAsync(connection);

        var execution = await Service(AssistantActionTestHarness.Rights(delete: false))
            .ConfirmAndExecuteAsync(TestUser, TestEmployee, DeleteRequest(), DeleteConfirmKey, CancellationToken.None);

        Assert.Empty(execution.Rows);
        Assert.Equal(WorkbenchDenialCodes.DeleteNotPermitted, execution.ModuleDenialCode);
        Assert.Equal(1, await MasterCountAsync(connection));
    }

    [Fact]
    public async Task 记录不在数据范围内_删除被拒_且记录仍在()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SeedAsync(connection);

        // 数据范围是 EXEC_TAG / DATA_FILTER 决定的；用一条把本行排除在外的人员过滤条件
        var execution = await Service(AssistantActionTestHarness.Rights(dataFilter: "CHAFFER_TYPE = 'ZZNOPE'"))
            .ConfirmAndExecuteAsync(TestUser, TestEmployee, DeleteRequest(), DeleteConfirmKey, CancellationToken.None);

        var row = Assert.Single(execution.Rows);
        Assert.False(row.Succeeded);
        Assert.Equal("RECORD_OUT_OF_SCOPE", row.Code);
        Assert.False(string.IsNullOrWhiteSpace(row.Message));
        Assert.Equal(1, await MasterCountAsync(connection));
    }

    // ===== 装配 =====

    private static AssistantActionRequest InsertRequest() => new(
        ModuleId, AssistantRecordActionKind.Insert,
        [new AssistantActionRow([],
            new Dictionary<string, string?> { ["CHAFFER_TYPE"] = TestType, ["CHAFFER_DATE"] = "2026-09-29" })]);

    /// <summary>日期不是日期：预演就会拦下，用来证"判不下来的行不落库"。</summary>
    private static AssistantActionRequest BadDateRequest() => new(
        ModuleId, AssistantRecordActionKind.Insert,
        [new AssistantActionRow([],
            new Dictionary<string, string?> { ["CHAFFER_TYPE"] = TestType, ["CHAFFER_DATE"] = "不是日期" })]);

    private static AssistantActionRequest DeleteRequest() => new(
        ModuleId, AssistantRecordActionKind.Delete,
        [new AssistantActionRow([TestType, SeededNo])]);

    /// <summary>界面点击驱动的那一次会算出的服务端幂等键（确定性，可在收尾时重算）。</summary>
    private static string UiKey(string confirmKey, AssistantActionRequest request)
    {
        var seed = AssistantActionKeySeed.FromUserConfirm(confirmKey);
        return AssistantActionIdempotency.Create(
            seed.ConversationId, seed.CallId,
            AssistantRecordActionNames.For(request.Kind),
            AssistantActionIdempotency.Canonicalize(request, request.Rows[0]));
    }

    private AssistantRecordActionService Service(ModuleRights? rights = null)
    {
        var agentContext = AssistantActionTestHarness.AgentContext();
        var auditWriter = AssistantActionTestHarness.AuditWriter(_connections, agentContext);
        var definitions = new AssistantActionTestHarness.FixedDefinitionSource(Definition(), Form());
        var policy = AssistantActionTestHarness.Policy(
            definitions,
            new AssistantActionTestHarness.FixedPermissions(rights ?? AssistantActionTestHarness.Rights()),
            ModuleId);
        return AssistantActionTestHarness.ActionService(
            new AssistantActionGate(policy),
            AssistantActionTestHarness.CommandHandler(_connections, ConnectionString, auditWriter),
            _connections, agentContext, auditWriter);
    }

    private static WorkbenchDefinition Definition() => new(
        ModuleId: ModuleId, Title: "客户询价单", MasterTable: MasterTable, DetailTable: null,
        MasterFields: [], DetailFields: [], DefaultSort: null,
        HasAdd: true, HasEdit: true, DetailNoSave: false,
        MasterPkOrder: ["CHAFFER_TYPE", "CHAFFER_NO"], DetailNoFields: "", HasWorkflow: false,
        UserId: TestUser, ExecTag: "A",
        // 数据范围过滤只能引用白名单内字段；这里放行单别，供"范围外"用例造条件
        FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CHAFFER_TYPE" },
        BusinessRule: new ModuleBusinessRule(ModuleId, true, "CHAFFER_NO", "CHAFFER_TYPE"));

    private static FormDefinition Form() => new(
        ModuleId, "客户询价单", MasterTable, null, true, true, "new",
        [
            Field("CHAFFER_TYPE", "询价单别", "nchar", primaryKey: true),
            Field("CHAFFER_NO", "询价单号", "nchar", primaryKey: true),
            Field("CHAFFER_DATE", "询价日期", "datetime"),
            Field("REMARK", "备注", "nvarchar"),
        ],
        [], ["CHAFFER_TYPE", "CHAFFER_NO"], string.Empty, string.Empty);

    private static FormFieldDefinition Field(
        string key, string label, string dataType, bool primaryKey = false) =>
        new(key, label, dataType, 100, null, false, null, null, null, false, true, false, false, null,
            [], primaryKey, false, false, false, false, false, null);

    // ===== 真库断言 =====

    private static async Task SeedAsync(SqlConnection connection) =>
        await ExecAsync(connection, """
            INSERT INTO dbo.COP_CHAFFER_M
                (CHAFFER_TYPE, CHAFFER_NO, CHAFFER_DATE, CLIENT_ID, REMARK, CONFIRM_TAG, CREATE_PERSON, LAST_UPDATE_BY)
            VALUES (@type, @no, '2026-09-01', NULL, N'初始备注', 0, N'ZZE2E', N'ZZE2E');
            """, ("@type", TestType), ("@no", SeededNo));

    private static Task<int> MasterCountAsync(SqlConnection connection) =>
        CountAsync(connection,
            "SELECT COUNT(*) FROM dbo.COP_CHAFFER_M WHERE CHAFFER_TYPE = @type;", ("@type", TestType));

    private static Task<int> ConfirmCountAsync(SqlConnection connection) =>
        CountAsync(connection,
            "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE M_IDX = @module AND ACTION = @action;",
            ("@module", ModuleId), ("@action", AssistantRecordActionService.ConfirmAuditAction));

    private static Task<int> InsertAuditCountAsync(SqlConnection connection) =>
        CountAsync(connection,
            "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE M_IDX = @module AND ACTION = N'INSERT';",
            ("@module", ModuleId));

    private static Task<int> IdempotencyCountAsync(SqlConnection connection, string key) =>
        CountAsync(connection,
            "SELECT COUNT(*) FROM dbo.WORKBENCH_IDEMPOTENCY WHERE IDEMPOTENCY_KEY = @key;", ("@key", key));

    private static async Task<(byte ActorType, byte ClientType, string UserId)> LatestInsertAuditAsync(
        SqlConnection connection)
    {
        await using var command = new SqlCommand("""
            SELECT TOP 1 ACTOR_TYPE, CLIENT_TYPE, ACTOR_USER_ID FROM dbo.AUDIT_EVENT
             WHERE M_IDX = @module AND ACTION = N'INSERT'
             ORDER BY EVENT_ID DESC;
            """, connection);
        command.Parameters.AddWithValue("@module", ModuleId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "没有找到本次写入的审计行。");
        return (reader.GetByte(0), reader.GetByte(1), reader.GetString(2));
    }

    private static async Task<byte> LatestConfirmActorTypeAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("""
            SELECT TOP 1 ACTOR_TYPE FROM dbo.AUDIT_EVENT
             WHERE M_IDX = @module AND ACTION = @action
             ORDER BY EVENT_ID DESC;
            """, connection);
        command.Parameters.AddWithValue("@module", ModuleId);
        command.Parameters.AddWithValue("@action", AssistantRecordActionService.ConfirmAuditAction);
        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return Convert.ToByte(value);
    }

    private static async Task<int> CountAsync(
        SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
}
