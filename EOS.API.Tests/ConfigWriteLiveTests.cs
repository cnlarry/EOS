using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Data.Effects;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Config;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 配置写能力的真库用例：字段 / 数据来源 / 按钮 / 效果键四类各跑到落库，并逐项比对结果。
///
/// <para>
/// 夹具一律**自造键**（自造的表标识、字段名、模块号），结束即清理；不借真实主档的取样值参与写入，
/// 也不删审计。真库用例跳过时不算通过——环境没有连接串时整类不执行。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConfigWriteLiveTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private const string UserId = "IT";
    private const int SourceModuleBase = 991000000;
    private readonly List<(string Table, string Field)> _fields = [];
    private readonly List<int> _modules = [];
    private readonly List<string> _idempotencyKeys = [];

    // ===== C1：字段面照 A 配 B 端到端 =====

    [Fact]
    public async Task 字段面克隆端到端且只作用于点名对象()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        var suffix = Random.Shared.Next(100000, 999999).ToString();
        var sourceField = $"ZZ_S4S_{suffix}";
        var untouchedField = $"ZZ_S4U_{suffix}";
        // 表标识自造（不指向任何真实业务表），以避免用例改动真实配置。
        var sourceTable = $"ZZ_S4ST_{suffix}";
        var targetTable = $"ZZ_S4TT_{suffix}";
        await SeedFieldAsync(sourceTable, sourceField, "源显示名", width: 222, visible: true, readonlyFlag: false);
        await SeedFieldAsync(targetTable, sourceField, "目标显示名", width: 100, visible: false, readonlyFlag: false);
        await SeedFieldAsync(targetTable, untouchedField, "不该改的字段", width: 100, visible: true, readonlyFlag: false);

        var service = CreateService();
        var request = new ConfigCloneRequest(
            ConfigSurface.Fields, sourceTable, targetTable, Objects: [sourceField]);

        var plan = await service.PlanAsync(UserId, request, token);
        Assert.Null(plan.BlockedCode);
        // C6/C7：只作用于点名的对象集合——未被点名的字段不产生任何项。
        var item = Assert.Single(plan.Items);
        Assert.Contains(item.Changes, change => change.Field == "F_DESC" && change.NewValue == "源显示名");
        Assert.Contains(item.Changes, change => change.Field == "DISPLAY_LENGTH" && change.NewValue == "222");
        Assert.Contains(item.Changes, change => change.Field == "IS_VISIBLE" && change.NewValue == "是");
        // C4：字段元数据改动不在预演覆盖内，规划阶段就已逐项标注（不需要真实单据）。
        Assert.Contains("无法预演", item.PreviewNote);

        var seed = KeySeed("field-1");
        RegisterKey(seed, request, [item.Id]);
        var result = await service.ApplyAsync(UserId, "IT", request, [item.Id], seed, token);
        var applied = Assert.Single(result.Items);
        Assert.True(applied.Applied, $"{applied.Code}: {applied.Message}");

        // 落库后逐字段比对（排除自增键与时间戳）。
        var after = await ReadFieldAsync(targetTable, sourceField);
        Assert.Equal("源显示名", after.Field.Label);
        Assert.Equal(222, after.Field.Width);
        Assert.True(after.Field.IsVisible);
        // 未被点名的字段保持原值——目标化，不是整表重排。
        var untouched = await ReadFieldAsync(targetTable, untouchedField);
        Assert.Equal("不该改的字段", untouched.Field.Label);
    }

    // ===== C1：数据来源面端到端 =====

    [Fact]
    public async Task 数据来源面克隆端到端()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        var suffix = Random.Shared.Next(100000, 999999).ToString();
        var field = $"ZZ_S4D_{suffix}";
        var sourceTable = $"ZZ_S4DS_{suffix}";
        var targetTable = $"ZZ_S4DT_{suffix}";
        await SeedFieldAsync(sourceTable, field, "源字段", 100, visible: true, readonlyFlag: false);
        await SeedFieldAsync(targetTable, field, "目标字段", 100, visible: true, readonlyFlag: false);
        // 自造一条数据来源：源表用真实表（保存期会校验源表存在），键落在自造字段上。
        await ExecuteAsync(
            """
            INSERT INTO dbo.FIELD_DATASOURCE (T_ID,F_ID,SERIAL_NO,ACTIVE_TAG,SOURCE_T_ID,SOURCE_DESC,
                CREATE_BY,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (@Table,@Field,1,1,N'COMPANY',N'客户来源','IT',GETDATE(),'IT',GETDATE());
            """,
            ("@Table", sourceTable), ("@Field", field));

        var service = CreateService();
        var request = new ConfigCloneRequest(
            ConfigSurface.DataSources, sourceTable, targetTable, Objects: [field]);
        var plan = await service.PlanAsync(UserId, request, token);
        Assert.Null(plan.BlockedCode);
        var item = Assert.Single(plan.Items);
        Assert.Contains(item.Changes, change => change.NewValue is not null && change.NewValue.Contains("COMPANY"));

        var seed = KeySeed("datasource-1");
        RegisterKey(seed, request, [item.Id]);
        var result = await service.ApplyAsync(UserId, "IT", request, [item.Id], seed, token);
        var applied = Assert.Single(result.Items);
        Assert.True(applied.Applied, $"{applied.Code}: {applied.Message}");

        var choosers = await ReadChoosersAsync(targetTable, field);
        var chooser = Assert.Single(choosers);
        Assert.Equal("COMPANY", chooser.Table);
        Assert.Equal("客户来源", chooser.Description);
        Assert.True(chooser.Active);
    }

    // ===== C2：幂等重放不重复写 =====

    [Fact]
    public async Task 同一幂等键重放不重复写()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        var suffix = Random.Shared.Next(100000, 999999).ToString();
        var field = $"ZZ_S4I_{suffix}";
        var sourceTable = $"ZZ_S4IS_{suffix}";
        var targetTable = $"ZZ_S4IT_{suffix}";
        await SeedFieldAsync(sourceTable, field, "幂等源名", 150, visible: true, readonlyFlag: false);
        await SeedFieldAsync(targetTable, field, "幂等目标名", 100, visible: true, readonlyFlag: false);

        var service = CreateService();
        var request = new ConfigCloneRequest(
            ConfigSurface.Fields, sourceTable, targetTable, Objects: [field]);
        var plan = await service.PlanAsync(UserId, request, token);
        var item = Assert.Single(plan.Items);
        var seed = KeySeed("idem-1");
        var key = RegisterKey(seed, request, [item.Id]);

        var first = await service.ApplyAsync(UserId, "IT", request, [item.Id], seed, token);
        var firstItem = Assert.Single(first.Items);
        Assert.True(firstItem.Applied, $"{firstItem.Code}: {firstItem.Message}");
        var afterFirst = await ReadFieldAsync(targetTable, field);
        Assert.Equal("幂等源名", afterFirst.Field.Label);
        var stampAfterFirst = await ReadLastUpdateAsync(targetTable, field);

        // 重放：同一键 ⇒ 返回同一结果（逐项仍是"已应用"），且不再落库（时间戳不动即证明没有第二次写）。
        var second = await service.ApplyAsync(UserId, "IT", request, [item.Id], seed, token);
        var secondItem = Assert.Single(second.Items);
        Assert.True(secondItem.Applied, $"{secondItem.Code}: {secondItem.Message}");
        Assert.Equal(item.Id, secondItem.Id);
        Assert.Equal(ConfigWriteService.ReplayNote, secondItem.Message);
        var stampAfterSecond = await ReadLastUpdateAsync(targetTable, field);
        Assert.Equal(stampAfterFirst, stampAfterSecond);
        Assert.Equal(1, await CountIdempotencyRowsAsync(key));
    }

    // ===== C3：审计与业务同事务（审计写不进去 ⇒ 业务回滚）=====

    [Fact]
    public async Task 审计写入失败时业务必须回滚()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        var suffix = Random.Shared.Next(100000, 999999).ToString();
        var field = $"ZZ_S4A_{suffix}";
        var sourceTable = $"ZZ_S4AS_{suffix}";
        var targetTable = $"ZZ_S4AT_{suffix}";
        await SeedFieldAsync(sourceTable, field, "审计源名", 180, visible: true, readonlyFlag: false);
        await SeedFieldAsync(targetTable, field, "审计目标名", 100, visible: true, readonlyFlag: false);

        var service = CreateService();
        var request = new ConfigCloneRequest(
            ConfigSurface.Fields, sourceTable, targetTable, Objects: [field]);
        var plan = await service.PlanAsync(UserId, request, token);
        var item = Assert.Single(plan.Items);
        var seed = KeySeed("audit-1");
        var key = RegisterKey(seed, request, [item.Id]);

        // 让审计写入必然失败：独占锁住审计表，再用一个很短的取消窗口触发等待中止。
        await using var blocker = new SqlConnection(ConnectionString.Value);
        await blocker.OpenAsync();
        await using var blockTransaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var tableLock = new SqlCommand(
            "SELECT 1 FROM dbo.AUDIT_EVENT WITH (TABLOCKX, HOLDLOCK);", blocker, blockTransaction))
        {
            await tableLock.ExecuteScalarAsync();
        }

        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var failure = await Record.ExceptionAsync(
            () => service.ApplyAsync(UserId, "IT", request, [item.Id], seed, window.Token));
        Assert.NotNull(failure);
        Assert.True(failure is OperationCanceledException or SqlException,
            $"审计写入被阻塞后应以取消 / 中止结束，实际是 {failure.GetType().Name}：{failure.Message}");

        await blockTransaction.RollbackAsync();

        // 业务必须回滚：字段没有被改，幂等占位也随之释放。
        var after = await ReadFieldAsync(targetTable, field);
        Assert.Equal("审计目标名", after.Field.Label);
        Assert.Equal(0, await CountIdempotencyRowsAsync(key));
    }

    // ===== C1：按钮面端到端（照 A 模块配 B 模块）=====

    [Fact]
    public async Task 按钮面克隆端到端且不复制按钮授权()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        var (sourceModuleId, targetModuleId) = await SeedModulesAsync();
        var config = CreateModuleConfig();
        var sourceAction = ManualAction(seq: 1, label: "源按钮标题", confirm: true, remark: "源备注");
        var targetAction = ManualAction(seq: 1, label: "目标按钮标题", confirm: false, remark: "目标备注");
        await config.SaveAsync(sourceModuleId, new([sourceAction], []), "IT", token);
        await config.SaveAsync(targetModuleId, new([targetAction], []), "IT", token);
        var buttonAuthBefore = await CountButtonAuthorizationRowsAsync();

        var service = CreateService();
        var request = new ConfigCloneRequest(
            ConfigSurface.Buttons, SourceModuleId: sourceModuleId, TargetModuleId: targetModuleId);
        var plan = await service.PlanAsync(UserId, request, token);
        Assert.Null(plan.BlockedCode);
        var item = Assert.Single(plan.Items);
        Assert.Contains(item.Changes, change => change.Field == "LABEL"
            && change.OldValue == "目标按钮标题" && change.NewValue == "源按钮标题");

        var seed = KeySeed("button-1");
        RegisterKey(seed, request, [item.Id]);
        var result = await service.ApplyAsync(UserId, "IT", request, [item.Id], seed, token);
        var applied = Assert.Single(result.Items);
        Assert.True(applied.Applied, $"{applied.Code}: {applied.Message}");

        var written = await ReadActionsAsync(targetModuleId);
        var manual = Assert.Single(written, action => action.EventCode == "MANUAL");
        Assert.Equal("源按钮标题", manual.Label);
        Assert.Equal("源备注", manual.Remark);
        Assert.True(manual.ConfirmTag);
        // T6：按钮授权不随配置迁移——授权行数一个都没动。
        Assert.Equal(buttonAuthBefore, await CountButtonAuthorizationRowsAsync());
    }

    // ===== C1：效果面端到端 =====

    [Fact]
    public async Task 效果面克隆端到端()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        var (sourceModuleId, targetModuleId) = await SeedModulesAsync();
        var config = CreateModuleConfig();
        var sourceAction = new BusinessActionDto(
            Seq: 1, EventCode: "APPROVE_EFFECT", EffectKey: "set-state", Enabled: true, FailMode: "BLOCK",
            Params: """{"targets":["MODULES"],"state":{"M_DESC":"已批核"}}""", Remark: "源效果");
        var targetAction = sourceAction with
        {
            Params = """{"targets":["MODULES"],"state":{"M_DESC":"旧值"}}""",
            Enabled = false,
            Remark = "目标效果",
        };
        await config.SaveAsync(sourceModuleId, new([sourceAction], []), "IT", token);
        await config.SaveAsync(targetModuleId, new([targetAction], []), "IT", token);

        var service = CreateService();
        var request = new ConfigCloneRequest(
            ConfigSurface.Effects, SourceModuleId: sourceModuleId, TargetModuleId: targetModuleId);
        var plan = await service.PlanAsync(UserId, request, token);
        Assert.Null(plan.BlockedCode);
        var item = Assert.Single(plan.Items);
        // C4：批核生效的效果链可以预演，但需要一张真实单据——规划阶段如实标注"未预演"。
        Assert.Contains("预演", item.PreviewNote);

        var seed = KeySeed("effect-1");
        RegisterKey(seed, request, [item.Id]);
        var result = await service.ApplyAsync(UserId, "IT", request, [item.Id], seed, token);
        var applied = Assert.Single(result.Items);
        Assert.True(applied.Applied, $"{applied.Code}: {applied.Message}");

        var written = await ReadActionsAsync(targetModuleId);
        var effect = Assert.Single(written, action => action.EventCode == "APPROVE_EFFECT");
        Assert.Equal("set-state", effect.EffectKey);
        Assert.Equal(sourceAction.Params, effect.Params);
        Assert.True(effect.Enabled);
    }

    // ===== 装配与夹具 =====

    private static AssistantActionKeySeed KeySeed(string callId) => AssistantActionKeySeed.FromToolCall(1, callId);

    private ConfigWriteService CreateService(AssistantConfigWriteOptions? configWrite = null)
        => new(
            Options.Create(configWrite ?? new AssistantConfigWriteOptions
            {
                Fields = true,
                DataSources = true,
                Buttons = true,
                Effects = true,
            }),
            new SetupPermissions(),
            CreateConnections(),
            new WorkbenchIdempotency(),
            new ConfigClonePlanner(CreateFields(), CreateModuleConfig(), NullLogger<ConfigClonePlanner>.Instance),
            // 真库用例不跑深度预演（那是效果链侧的事）：这里只验规划与应用。
            null!,
            CreateFields(),
            CreateModuleConfig(),
            NullLogger<ConfigWriteService>.Instance);

    private FieldAdminRepository CreateFields()
    {
        var connections = CreateConnections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        return new FieldAdminRepository(
            connections,
            new WorkbenchDirtyMarker(connections),
            new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider,
                Options.Create(new AuditSettings())),
            new RestrictedExpressionService(connections),
            new WorkbenchIdempotency(),
            NullLogger<FieldAdminRepository>.Instance);
    }

    private ModuleBusinessConfigRepository CreateModuleConfig()
    {
        var connections = CreateConnections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        return new ModuleBusinessConfigRepository(
            connections,
            new WorkbenchDirtyMarker(connections),
            new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider,
                Options.Create(new AuditSettings())),
            new DocumentActionRegistry([new DocumentActionProbeHandler()], NullLogger<DocumentActionRegistry>.Instance),
            new WorkbenchIdempotency(),
            NullLogger<ModuleBusinessConfigRepository>.Instance);
    }

    private static DbConnectionFactory CreateConnections()
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build());

    private static BusinessActionDto ManualAction(int seq, string label, bool confirm, string remark)
        => new(Seq: seq, EventCode: "MANUAL", EffectKey: DocumentActionProbeHandler.ActionKey,
            Enabled: true, FailMode: "BLOCK", Label: label, ConfirmTag: confirm, Remark: remark);

    private async Task<(int Source, int Target)> SeedModulesAsync()
    {
        var source = SourceModuleBase + Random.Shared.Next(1, 4999);
        var target = source + 5000;
        foreach (var moduleId in new[] { source, target })
        {
            await ExecuteAsync(
                """
                INSERT INTO dbo.MODULES (M_IDX,M_DESC,M_URL,MODI_URL,MASTER_TABLE,EFFECT_ENGINE_TAG)
                VALUES (@ModuleId,N'配置写用例模块',N'',N'',N'COMPANY',1);
                """,
                ("@ModuleId", moduleId));
            _modules.Add(moduleId);
        }
        return (source, target);
    }

    private async Task SeedFieldAsync(
        string table, string field, string description, int width, bool visible, bool readonlyFlag)
    {
        await ExecuteAsync(
            """
            INSERT INTO dbo.FIELDS (T_ID,F_ID,F_DESC,F_TYPE,IS_QUERY,IS_DEFAULT_FIELDS,IS_VISIBLE,IS_READONLY,
                DISPLAY_LENGTH,ITEM_ALIGN,HEADER_ALIGN,IS_COST,IS_SECRECY,CAN_COPY,IS_VIRTUAL,IS_AUTOINC,
                LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES (@Table,@Field,@Description,N'nvarchar',1,1,@Visible,@Readonly,
                @Width,N'left',N'center',0,0,1,0,0,'IT',GETDATE());
            """,
            ("@Table", table), ("@Field", field), ("@Description", description),
            ("@Width", width), ("@Visible", visible), ("@Readonly", readonlyFlag));
        _fields.Add((table, field));
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private async Task<FieldAdminMetadata> ReadFieldAsync(string table, string field)
    {
        var metadata = await CreateFields().GetMetadataAsync(table, field, CancellationToken.None);
        Assert.NotNull(metadata);
        return metadata!;
    }

    private async Task<DateTime?> ReadLastUpdateAsync(string table, string field)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT LAST_UPDATE_DATE FROM dbo.FIELDS WHERE T_ID=@Table AND LTRIM(RTRIM(F_ID))=@Field;", connection);
        command.Parameters.AddWithValue("@Table", table);
        command.Parameters.AddWithValue("@Field", field);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToDateTime(value);
    }

    private async Task<IReadOnlyList<FieldAdminChooser>> ReadChoosersAsync(string table, string field)
        => (await ReadFieldAsync(table, field)).Field.Choosers;

    private async Task<IReadOnlyList<BusinessActionDto>> ReadActionsAsync(int moduleId)
    {
        var config = await CreateModuleConfig().GetAsync(moduleId, CancellationToken.None);
        Assert.NotNull(config);
        return config!.Actions;
    }

    /// <summary>
    /// 该批在服务端算出的幂等键（与服务端同一份推导：调用身份 + 面 + 请求意图）。
    /// 用例按这个显式键做断言与清理，不做模糊匹配。
    /// </summary>
    private string RegisterKey(
        AssistantActionKeySeed seed, ConfigCloneRequest request, IReadOnlyList<string> itemIds)
    {
        var key = ConfigWriteService.BatchKeyFor(seed, request.Surface, request, itemIds);
        _idempotencyKeys.Add(key);
        return key;
    }

    private async Task<int> CountIdempotencyRowsAsync(string key)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.WORKBENCH_IDEMPOTENCY WHERE IDEMPOTENCY_KEY=@Key;", connection);
        command.Parameters.AddWithValue("@Key", key);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountButtonAuthorizationRowsAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT (SELECT COUNT(*) FROM dbo.SYSDH_BUTTON) + (SELECT COUNT(*) FROM dbo.SYSDD_BUTTON);
            """, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        if (ConnectionString.Value is null) return;
        try
        {
            CleanupAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 清理失败不影响测试结论；残留仅为自造的用例数据。
        }
    }

    private async Task CleanupAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        foreach (var (table, field) in _fields)
        {
            await using var delete = new SqlCommand(
                """
                DELETE FROM dbo.FIELDS WHERE T_ID=@Table AND LTRIM(RTRIM(F_ID))=@Field;
                DELETE FROM dbo.FIELD_DATASOURCE WHERE T_ID=@Table AND LTRIM(RTRIM(F_ID))=@Field;
                """, connection);
            delete.Parameters.AddWithValue("@Table", table);
            delete.Parameters.AddWithValue("@Field", field);
            await delete.ExecuteNonQueryAsync();
        }
        foreach (var moduleId in _modules)
        {
            await using var delete = new SqlCommand(
                """
                DELETE FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@ModuleId;
                DELETE FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX=@ModuleId;
                DELETE FROM dbo.MODULES WHERE M_IDX=@ModuleId;
                """, connection);
            delete.Parameters.AddWithValue("@ModuleId", moduleId);
            await delete.ExecuteNonQueryAsync();
        }
        foreach (var key in _idempotencyKeys)
        {
            await using var delete = new SqlCommand(
                "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE IDEMPOTENCY_KEY=@Key;", connection);
            delete.Parameters.AddWithValue("@Key", key);
            await delete.ExecuteNonQueryAsync();
        }
        _fields.Clear();
        _modules.Clear();
        _idempotencyKeys.Clear();
    }

    /// <summary>用例内的权限档位：字段维护门与配置面门都有设置权（权限门本身由纯内存用例覆盖）。</summary>
    private sealed class SetupPermissions : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
            => Task.FromResult(new ModulePermission(Rights()));

        public Task<ModulePermission> RequireAsync(
            string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken)
            => Task.FromResult(new ModulePermission(Rights()));

        private static ModuleRights Rights() => new(
            CanBrowse: true, CanViewCost: true, CanViewSecrecy: true, CanSetup: true,
            DeniedMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DeniedDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CanAddNew: true, CanEdit: true, CanDelete: true,
            CanApprove: false, CanDeapprove: false, CanEndCase: false, CanUnEndCase: false,
            CanFileView: true, CanFileUpda: true, CanFileEdit: true, CanFileDele: true,
            DenyNewMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyNewDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyModiMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyModiDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DataFilter: "", ExecuteTag: "A", CanModuleConfig: true);
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_ERP_CONN\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }
}
