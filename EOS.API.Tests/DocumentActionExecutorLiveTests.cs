using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.DocumentActions.Handlers;
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
/// 单据操作端到端管线（真库）：审计一条、幂等键挡重复点击、探路不落库且不占键、
/// 范围外拒绝、失败整体回滚（磁盘上不留半成品）、WARN 只报告不阻断。
/// 用例只用只读的既有主数据（仓库资料）作为单据载体，断言结束后清理自己写入的
/// AUDIT_EVENT / WORKBENCH_IDEMPOTENCY 行，业务表一行不改。
/// 需要 MSSQL_ERP_CONN。
/// </summary>
[Collection("live-database")]
public sealed class DocumentActionExecutorLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    /// <summary>仓库资料：主表 DEPOT 有存量行，探针不碰业务表，故不需要构造单据。</summary>
    private const int ModuleId = 110306;
    private const string MasterTable = "DEPOT";
    private const string FailingActionKey = "live-test-failing";

    private static DbConnectionFactory Connections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        return new DbConnectionFactory(configuration);
    }

    /// <summary>用例经办人：按钮授权是 fail-closed 名单，整类的每个用例先给它授权、结束收回。
    /// 授权表的目标列与 SYSDD/SYSDH 同宽（NCHAR(10)），故用例账号也取同宽。</summary>
    private const string TestUserId = "ZZLIVE0001";

    public Task InitializeAsync() =>
        GrantAsync(TestUserId, DocumentActionProbeHandler.ActionKey, FailingActionKey);

    public Task DisposeAsync() => RevokeAsync(TestUserId);

    private sealed class ThrowingAction : IDocumentUserAction
    {
        public string Key => FailingActionKey;

        public string Label => "失败用例";

        public Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token) =>
            throw new InvalidOperationException("用例故意失败");
    }

    private static DocumentActionExecutor Executor(params IDocumentUserAction[] extraActions)
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider, Options.Create(new AuditSettings()));
        var actions = new List<IDocumentUserAction> { new DocumentActionProbeHandler() };
        actions.AddRange(extraActions);
        return new DocumentActionExecutor(
            connections,
            new DocumentActionRegistry(actions, NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            auditWriter,
            NullLogger<DocumentActionExecutor>.Instance);
    }

    /// <summary>
    /// 按钮授权是 fail-closed 名单：用例先显式授权再用，结束后收回（不留名单行）。
    /// 名单表按 (目标, 模块, 键) 存，键不必先有配置行，因此用例不碰任何模块的配置。
    /// </summary>
    private static async Task GrantAsync(string userId, params string[] keys)
    {
        await using var connection = await OpenAsync();
        foreach (var key in keys)
        {
            await using var command = new SqlCommand(
                """
                DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@User AND M_IDX=@ModuleId AND BUTTON_KEY=@Key;
                INSERT INTO dbo.SYSDD_BUTTON (USER_ID,M_IDX,BUTTON_KEY,ALLOW_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@User,@ModuleId,@Key,1,N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
                """, connection);
            command.Parameters.Add("@User", SqlDbType.NVarChar, 20).Value = userId;
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = key;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task RevokeAsync(string userId)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            "DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@User AND M_IDX=@ModuleId;", connection);
        command.Parameters.Add("@User", SqlDbType.NVarChar, 20).Value = userId;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
        await command.ExecuteNonQueryAsync();
    }

    private static WorkbenchDefinition Definition(JsonElement? actions, string execTag = "Z", bool hasOwnerColumn = true) =>
        new(ModuleId: ModuleId, Title: "仓库资料", MasterTable: MasterTable, DetailTable: null,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["DEPOT_ID"], DetailNoFields: "", HasWorkflow: false,
            UserId: TestUserId,
            ExecTag: execTag,
            HasOwnerColumn: hasOwnerColumn,
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DEPOT_ID", "DEPOT_NAME" },
            BusinessActions: actions);

    private static FormDefinition Form() =>
        new(ModuleId, "仓库资料", MasterTable, null, true, true, "view", [], [], ["DEPOT_ID"], string.Empty, string.Empty);

    private static JsonElement Actions(params object[] rows) => JsonSerializer.SerializeToElement(rows);

    private static object ManualRow(string key, bool confirmTag = false, string failMode = "BLOCK", int seq = 1) =>
        new { seq, eventCode = "MANUAL", effectKey = key, enabled = true, label = "用例按钮", confirmTag, failMode };

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<string> AnyDepotIdAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("SELECT TOP 1 DEPOT_ID FROM dbo.DEPOT ORDER BY DEPOT_ID;", connection);
        // DEPOT_ID 是定长列，读回来带尾随空格；单据主键在写入路径上会被去空白，用例须取同一形态。
        return (await command.ExecuteScalarAsync() as string)?.Trim() is { Length: > 0 } depotId
            ? depotId
            : throw new InvalidOperationException("dbo.DEPOT 无存量行，无法验证单据操作管线。");
    }

    private static async Task<int> AuditCountAsync(string recordKey, string action)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE RESOURCE_KEY=@Key AND ACTION=@Action;", connection);
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 200).Value = recordKey;
        command.Parameters.Add("@Action", SqlDbType.NVarChar, 60).Value = action;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> IdempotencyRecordExistsAsync(string key)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.WORKBENCH_IDEMPOTENCY WHERE IDEMPOTENCY_KEY=@Key;", connection);
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 128).Value = key;
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task CleanupAsync(string recordKey, params string[] actionKeys)
    {
        foreach (var actionKey in actionKeys)
        {
            await using var connection = await OpenAsync();
            await using var command = new SqlCommand(
                "DELETE FROM dbo.AUDIT_EVENT WHERE RESOURCE_KEY=@Key AND ACTION=@Action;", connection);
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 200).Value = recordKey;
            command.Parameters.Add("@Action", SqlDbType.NVarChar, 60).Value = actionKey;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task CleanupIdempotencyAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            await using var connection = await OpenAsync();
            await using var command = new SqlCommand(
                "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE IDEMPOTENCY_KEY=@Key;", connection);
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 128).Value = key;
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Probe_RunsThroughTheEnvelope_WritesOneAuditEvent_AndClaimsTheKey()
    {
        var depotId = await AnyDepotIdAsync();
        var idempotencyKey = Guid.NewGuid().ToString("N");
        var action = DocumentActionProbeHandler.ActionKey;
        try
        {
            var result = await Executor().ExecuteAsync(
                Definition(Actions(ManualRow(action))), Form(), action,
                new DocumentActionRequest([depotId]), TestUserId, "测试经办人", null, idempotencyKey, CancellationToken.None);

            Assert.Equal(DocumentActionStatus.Ok, result.Status);
            Assert.Equal(DocumentActionOutcome.Message, result.Result!.Outcome);
            Assert.Contains(depotId, result.Result.Message!);
            Assert.False(result.RequiresConfirmation);
            Assert.Equal(1, await AuditCountAsync(depotId, action));
            Assert.True(await IdempotencyRecordExistsAsync(idempotencyKey));
        }
        finally
        {
            await CleanupAsync(depotId, action);
            await CleanupIdempotencyAsync(idempotencyKey);
        }
    }

    [Fact]
    public async Task RepeatedIdempotencyKey_ReturnsRecordedOutcome_WithoutRunningTwice()
    {
        var depotId = await AnyDepotIdAsync();
        var idempotencyKey = Guid.NewGuid().ToString("N");
        var action = DocumentActionProbeHandler.ActionKey;
        try
        {
            var executor = Executor();
            var definition = Definition(Actions(ManualRow(action)));
            var first = await executor.ExecuteAsync(definition, Form(), action,
                new DocumentActionRequest([depotId]), TestUserId, "测试经办人", null, idempotencyKey, CancellationToken.None);
            var second = await executor.ExecuteAsync(definition, Form(), action,
                new DocumentActionRequest([depotId]), TestUserId, "测试经办人", null, idempotencyKey, CancellationToken.None);

            Assert.Equal(DocumentActionStatus.Ok, first.Status);
            Assert.Equal(DocumentActionStatus.Ok, second.Status);
            Assert.Equal(first.Result!.Message, second.Result!.Message);
            Assert.Equal(1, await AuditCountAsync(depotId, action));
        }
        finally
        {
            await CleanupAsync(depotId, action);
            await CleanupIdempotencyAsync(idempotencyKey);
        }
    }

    [Fact]
    public async Task ConfirmTaggedAction_InProbeMode_WritesNothing_AndLeavesTheKeyUnclaimed()
    {
        var depotId = await AnyDepotIdAsync();
        var idempotencyKey = Guid.NewGuid().ToString("N");
        var action = DocumentActionProbeHandler.ActionKey;
        try
        {
            var result = await Executor().ExecuteAsync(
                Definition(Actions(ManualRow(action, confirmTag: true))), Form(), action,
                new DocumentActionRequest([depotId], Confirm: false), TestUserId, "测试经办人", null, idempotencyKey, CancellationToken.None);

            Assert.Equal(DocumentActionStatus.Ok, result.Status);
            Assert.True(result.RequiresConfirmation);
            Assert.Equal(0, await AuditCountAsync(depotId, action));
            Assert.False(await IdempotencyRecordExistsAsync(idempotencyKey));

            // 探路不占键：紧接着带确认的真实执行必须能跑（否则用户第一次点永远失败）。
            var confirmed = await Executor().ExecuteAsync(
                Definition(Actions(ManualRow(action, confirmTag: true))), Form(), action,
                new DocumentActionRequest([depotId], Confirm: true), TestUserId, "测试经办人", null, idempotencyKey, CancellationToken.None);
            Assert.Equal(DocumentActionStatus.Ok, confirmed.Status);
            Assert.False(confirmed.RequiresConfirmation);
            Assert.Equal(1, await AuditCountAsync(depotId, action));
        }
        finally
        {
            await CleanupAsync(depotId, action);
            await CleanupIdempotencyAsync(idempotencyKey);
        }
    }

    [Fact]
    public async Task UnconfiguredOrUnregisteredAction_IsNotFound()
    {
        var depotId = await AnyDepotIdAsync();
        var definition = Definition(Actions(ManualRow(DocumentActionProbeHandler.ActionKey)));

        var notConfigured = await Executor().ExecuteAsync(definition, Form(), "recalc-account",
            new DocumentActionRequest([depotId]), TestUserId, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Equal(DocumentActionStatus.NotFound, notConfigured.Status);

        // 配置了但实现没注册：同样是"不可执行"，不得静默跑掉别的东西。
        var notRegistered = await Executor().ExecuteAsync(Definition(Actions(ManualRow(FailingActionKey))), Form(), FailingActionKey,
            new DocumentActionRequest([depotId]), TestUserId, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Equal(DocumentActionStatus.NotFound, notRegistered.Status);
    }

    [Fact]
    public async Task BlockFailure_RollsBack_AndAuditsTheFailureOutsideTheTransaction()
    {
        var depotId = await AnyDepotIdAsync();
        var idempotencyKey = Guid.NewGuid().ToString("N");
        try
        {
            var result = await Executor(new ThrowingAction()).ExecuteAsync(
                Definition(Actions(ManualRow(FailingActionKey))), Form(), FailingActionKey,
                new DocumentActionRequest([depotId]), TestUserId, "测试经办人", null, idempotencyKey, CancellationToken.None);

            Assert.Equal(DocumentActionStatus.Failed, result.Status);
            Assert.Equal(DocumentActionErrorCodes.Failed, result.ErrorCode);
            // 失败审计必须落在回滚之外，否则"谁点了、为什么失败"随事务一起消失。
            Assert.Equal(1, await AuditCountAsync(depotId, FailingActionKey));
            // BLOCK 失败不占幂等键：修好状态/换个人再点不该被上一次失败挡住。
            Assert.False(await IdempotencyRecordExistsAsync(idempotencyKey));
        }
        finally
        {
            await CleanupAsync(depotId, FailingActionKey);
            await CleanupIdempotencyAsync(idempotencyKey);
        }
    }

    [Fact]
    public async Task WarnFailure_IsReportedAsWarning_WithoutBlockingTheCaller()
    {
        var depotId = await AnyDepotIdAsync();
        var idempotencyKey = Guid.NewGuid().ToString("N");
        try
        {
            var result = await Executor(new ThrowingAction()).ExecuteAsync(
                Definition(Actions(ManualRow(FailingActionKey, failMode: "WARN"))), Form(), FailingActionKey,
                new DocumentActionRequest([depotId]), TestUserId, "测试经办人", null, idempotencyKey, CancellationToken.None);

            Assert.Equal(DocumentActionStatus.Ok, result.Status);
            Assert.Equal(DocumentActionOutcome.Message, result.Result!.Outcome);
            Assert.Contains(result.Result.Warnings!, warning => warning.Code == "ACTION_WARNING");
            Assert.Equal(1, await AuditCountAsync(depotId, FailingActionKey));
        }
        finally
        {
            await CleanupAsync(depotId, FailingActionKey);
            await CleanupIdempotencyAsync(idempotencyKey);
        }
    }

    [Fact]
    public async Task OutOfRangeDocument_IsRefused_EvenWithAConfiguredAction()
    {
        var depotId = await AnyDepotIdAsync();
        var action = DocumentActionProbeHandler.ActionKey;
        try
        {
            var result = await Executor().ExecuteAsync(
                Definition(Actions(ManualRow(action))), Form(), action,
                new DocumentActionRequest([depotId]), TestUserId, "测试经办人",
                $"{MasterTable}.DEPOT_ID='__NOT_IN_RANGE__'", Guid.NewGuid().ToString("N"), CancellationToken.None);

            Assert.Equal(DocumentActionStatus.OutOfScope, result.Status);
            Assert.Equal(0, await AuditCountAsync(depotId, action));
        }
        finally
        {
            await CleanupAsync(depotId, action);
        }
    }

    [Fact]
    public async Task UnsupportedRangeConfiguration_FailsClosed()
    {
        var depotId = await AnyDepotIdAsync();
        var action = DocumentActionProbeHandler.ActionKey;

        // EXEC_TAG=B 需要 OWNER 列；模块主表没有该列时不得退化为"全可见"。
        var result = await Executor().ExecuteAsync(
            Definition(Actions(ManualRow(action)), execTag: "B", hasOwnerColumn: false), Form(), action,
            new DocumentActionRequest([depotId]), TestUserId, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.Equal(DocumentActionStatus.FilterUnsupported, result.Status);
    }

    [Fact]
    public async Task MissingRecord_IsNotFound()
    {
        var action = DocumentActionProbeHandler.ActionKey;

        var result = await Executor().ExecuteAsync(
            Definition(Actions(ManualRow(action))), Form(), action,
            new DocumentActionRequest(["__NO_SUCH_DEPOT__"]), TestUserId, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.Equal(DocumentActionStatus.NotFound, result.Status);
    }
}
