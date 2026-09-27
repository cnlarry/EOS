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
/// 按钮级授权的真库验收（fail-closed 名单）：无名单行即拒绝、个人行接管组通道、组通道经
/// SYSDG_USER 落到人、授权镜子给出 N 用户 / M 组，以及端点在无授权时 403 且留痕。
/// 用例只写自己造的合成用户/组（ZZ 前缀），按钮配置行也临时插入并在 finally 清理。
/// 需要 MSSQL_ERP_CONN。
/// </summary>
[Collection("live-database")]
public sealed class DocumentActionAuthorizationLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 110306;
    private const string MasterTable = "DEPOT";
    private const string ButtonKey = DocumentActionProbeHandler.ActionKey;
    private const string OtherKey = "live-test-other-button";
    private const string UserWithPersonal = "ZZBTN0001";
    private const string UserInGroup = "ZZBTN0002";
    private const string GroupId = "ZZBTNTST01";
    private const string AdminName = "按钮授权用例";

    private static DbConnectionFactory Connections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        return new DbConnectionFactory(configuration);
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    // ===== 夹具：合成组 + 一条临时 MANUAL 配置行（授权键必须来自配置，故必须真配一行） =====

    private static async Task FixtureUpAsync()
    {
        await using var connection = await OpenAsync();
        await using (var group = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDG WHERE G_IDX=@Id)
                INSERT INTO dbo.SYSDG (G_IDX,G_DESC,CREATE_PERSON,CREATE_DATE,CONFIRM_TAG,CI)
                VALUES (@Id,N'按钮授权用例组',N'DbUp',SYSDATETIME(),0,N'');
            """, connection))
        {
            group.Parameters.Add("@Id", SqlDbType.NVarChar, 20).Value = GroupId;
            await group.ExecuteNonQueryAsync();
        }
        await using (var member = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDG_USER WHERE G_IDX=@Id AND USER_ID=@User)
                INSERT INTO dbo.SYSDG_USER (G_IDX,USER_ID) VALUES (@Id,@User);
            """, connection))
        {
            member.Parameters.Add("@Id", SqlDbType.NVarChar, 20).Value = GroupId;
            member.Parameters.Add("@User", SqlDbType.NVarChar, 20).Value = UserInGroup;
            await member.ExecuteNonQueryAsync();
        }
        await using (var action = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                           WHERE M_IDX=@ModuleId AND EVENT_CODE=N'MANUAL' AND EFFECT_KEY=@Key)
                INSERT INTO dbo.MODULE_BUSINESS_ACTION
                    (M_IDX,EVENT_CODE,SEQ,EFFECT_KEY,EFFECT_NAME,ENABLED,FAIL_MODE,LABEL,CONFIRM_TAG,
                     CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@ModuleId,N'MANUAL',98,@Key,N'管线探针（用例）',1,N'BLOCK',N'管线探针（用例）',0,
                        N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
            """, connection))
        {
            action.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            action.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = ButtonKey;
            await action.ExecuteNonQueryAsync();
        }
    }

    private static async Task FixtureDownAsync()
    {
        await using var connection = await OpenAsync();
        foreach (var sql in new[]
        {
            "DELETE FROM dbo.SYSDD_BUTTON WHERE BUTTON_KEY=@Key AND USER_ID IN (@User1,@User2);",
            "DELETE FROM dbo.SYSDH_BUTTON WHERE BUTTON_KEY=@Key AND G_IDX=@Group;",
            "DELETE FROM dbo.SYSDG_USER WHERE G_IDX=@Group;",
            "DELETE FROM dbo.SYSDG WHERE G_IDX=@Group;",
            "DELETE FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@ModuleId AND EVENT_CODE=N'MANUAL' AND EFFECT_KEY=@Key;",
        })
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = ButtonKey;
            command.Parameters.Add("@User1", SqlDbType.NVarChar, 20).Value = UserWithPersonal;
            command.Parameters.Add("@User2", SqlDbType.NVarChar, 20).Value = UserInGroup;
            command.Parameters.Add("@Group", SqlDbType.NVarChar, 20).Value = GroupId;
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task GrantUserAsync(string userId, bool allow)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            """
            DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@User AND M_IDX=@ModuleId AND BUTTON_KEY=@Key;
            INSERT INTO dbo.SYSDD_BUTTON (USER_ID,M_IDX,BUTTON_KEY,ALLOW_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (@User,@ModuleId,@Key,@Allow,N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
            """, connection);
        command.Parameters.Add("@User", SqlDbType.NVarChar, 20).Value = userId;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = ButtonKey;
        command.Parameters.Add("@Allow", SqlDbType.Bit).Value = allow;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task GrantGroupAsync(bool allow)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            """
            DELETE FROM dbo.SYSDH_BUTTON WHERE G_IDX=@Group AND M_IDX=@ModuleId AND BUTTON_KEY=@Key;
            INSERT INTO dbo.SYSDH_BUTTON (G_IDX,M_IDX,BUTTON_KEY,ALLOW_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (@Group,@ModuleId,@Key,@Allow,N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
            """, connection);
        command.Parameters.Add("@Group", SqlDbType.NVarChar, 20).Value = GroupId;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = ButtonKey;
        command.Parameters.Add("@Allow", SqlDbType.Bit).Value = allow;
        await command.ExecuteNonQueryAsync();
    }

    // ===== 授权聚合 =====

    [Fact]
    public async Task NoOverrideRow_IsDenied_FailClosed()
    {
        await FixtureUpAsync();
        try
        {
            var authorization = new DocumentActionAuthorization(Connections());

            var keys = await authorization.AuthorizedKeysAsync(UserWithPersonal, ModuleId, [ButtonKey, OtherKey], CancellationToken.None);

            Assert.Empty(keys);
            Assert.False(await authorization.IsAuthorizedAsync(UserWithPersonal, ModuleId, ButtonKey, CancellationToken.None));
        }
        finally
        {
            await FixtureDownAsync();
        }
    }

    [Fact]
    public async Task GroupChannel_GrantsThroughMembership()
    {
        await FixtureUpAsync();
        try
        {
            await GrantGroupAsync(allow: true);
            var authorization = new DocumentActionAuthorization(Connections());

            Assert.True(await authorization.IsAuthorizedAsync(UserInGroup, ModuleId, ButtonKey, CancellationToken.None));
            // 只有组里的那个人被授权，组外用户仍然不可点。
            Assert.False(await authorization.IsAuthorizedAsync(UserWithPersonal, ModuleId, ButtonKey, CancellationToken.None));
        }
        finally
        {
            await FixtureDownAsync();
        }
    }

    [Fact]
    public async Task PersonalRow_TakesOverTheGroupChannel_EvenWhenItDenies()
    {
        await FixtureUpAsync();
        try
        {
            await GrantGroupAsync(allow: true);
            // 个人行存在（且为拒绝）即接管组授权：个人 ≠ 个人与组的并集。
            await GrantUserAsync(UserInGroup, allow: false);
            var authorization = new DocumentActionAuthorization(Connections());

            Assert.False(await authorization.IsAuthorizedAsync(UserInGroup, ModuleId, ButtonKey, CancellationToken.None));

            // 个人行改成允许 → 立刻生效（同一通道内以名单为准）。
            await GrantUserAsync(UserInGroup, allow: true);
            Assert.True(await authorization.IsAuthorizedAsync(UserInGroup, ModuleId, ButtonKey, CancellationToken.None));
        }
        finally
        {
            await FixtureDownAsync();
        }
    }

    [Fact]
    public async Task Counts_ReportUsersAndGroups_ForTheMirror()
    {
        await FixtureUpAsync();
        try
        {
            await GrantUserAsync(UserWithPersonal, allow: true);
            await GrantUserAsync(UserInGroup, allow: true);
            await GrantGroupAsync(allow: true);
            var authorization = new DocumentActionAuthorization(Connections());

            var counts = await authorization.CountsAsync(ModuleId, [ButtonKey, OtherKey], CancellationToken.None);

            Assert.Equal(2, counts[ButtonKey].Users);
            Assert.Equal(1, counts[ButtonKey].Groups);
            // 没有授权行的按钮不能凭空出现计数："配了没人能用"要能如实显示为 0 / 0。
            Assert.Equal(0, counts[OtherKey].Users);
            Assert.Equal(0, counts[OtherKey].Groups);
        }
        finally
        {
            await FixtureDownAsync();
        }
    }

    // ===== 端点侧：无授权 403 且留痕 =====

    private static readonly string[] FilterKeys = ["DEPOT_ID", "DEPOT_NAME"];

    private static WorkbenchDefinition Definition() =>
        new(ModuleId: ModuleId, Title: "仓库资料", MasterTable: MasterTable, DetailTable: null,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["DEPOT_ID"], DetailNoFields: "", HasWorkflow: false,
            UserId: "codex-live-test", ExecTag: "Z",
            FilterFieldKeys: FilterKeys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            BusinessActions: JsonSerializer.SerializeToElement(new object[]
            {
                new { seq = 98, eventCode = "MANUAL", effectKey = ButtonKey, enabled = true, label = "管线探针（用例）" },
            }));

    private static FormDefinition Form() =>
        new(ModuleId, "仓库资料", MasterTable, null, true, true, "view", [], [], ["DEPOT_ID"], string.Empty, string.Empty);

    private static DocumentActionExecutor Executor()
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider, Options.Create(new AuditSettings()));
        return new DocumentActionExecutor(
            connections,
            new DocumentActionRegistry([new DocumentActionProbeHandler()], NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            auditWriter,
            NullLogger<DocumentActionExecutor>.Instance);
    }

    private static async Task<string> AnyDepotIdAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("SELECT TOP 1 DEPOT_ID FROM dbo.DEPOT ORDER BY DEPOT_ID;", connection);
        return (await command.ExecuteScalarAsync() as string)?.Trim()
            ?? throw new InvalidOperationException("dbo.DEPOT 无存量行，无法验证单据操作管线。");
    }

    [Fact]
    public async Task Endpoint_RefusesUnauthorizedButton_403_AndLeavesAnAuditTrail()
    {
        await FixtureUpAsync();
        var depotId = await AnyDepotIdAsync();
        var auditBaseline = await AuditBaselineAsync();
        try
        {
            var result = await Executor().ExecuteAsync(Definition(), Form(), ButtonKey,
                new DocumentActionRequest([depotId]), UserWithPersonal, AdminName, null, Guid.NewGuid().ToString("N"), CancellationToken.None);

            Assert.Equal(DocumentActionStatus.Forbidden, result.Status);
            Assert.Equal(DocumentActionErrorCodes.Forbidden, result.ErrorCode);
            Assert.Equal(1, await AuditCountAsync(depotId, auditBaseline));
        }
        finally
        {
            await FixtureDownAsync();
        }
    }

    [Fact]
    public async Task Endpoint_RunsOnceTheButtonIsGranted()
    {
        await FixtureUpAsync();
        var depotId = await AnyDepotIdAsync();
        var auditBaseline = await AuditBaselineAsync();
        try
        {
            await GrantUserAsync(UserWithPersonal, allow: true);
            var result = await Executor().ExecuteAsync(Definition(), Form(), ButtonKey,
                new DocumentActionRequest([depotId]), UserWithPersonal, AdminName, null, Guid.NewGuid().ToString("N"), CancellationToken.None);

            Assert.Equal(DocumentActionStatus.Ok, result.Status);
            Assert.Equal(DocumentActionOutcome.Message, result.Result!.Outcome);
            Assert.Equal(1, await AuditCountAsync(depotId, auditBaseline));
        }
        finally
        {
            await FixtureDownAsync();
        }
    }

    /// <summary>审计是追加型的：取当前最大事件号作基线，之后只看基线之上的增量。</summary>
    private static async Task<long> AuditBaselineAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT;", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>基线之上、指定（资源键 + 按钮键）的审计条数。</summary>
    private static async Task<int> AuditCountAsync(string recordKey, long baseline)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM dbo.AUDIT_EVENT
             WHERE EVENT_ID > @Baseline AND RESOURCE_KEY=@Key AND ACTION=@Action;
            """, connection);
        command.Parameters.Add("@Baseline", SqlDbType.BigInt).Value = baseline;
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 200).Value = recordKey;
        command.Parameters.Add("@Action", SqlDbType.NVarChar, 60).Value = ButtonKey;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
