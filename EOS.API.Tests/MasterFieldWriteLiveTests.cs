using System.Data;
using System.Globalization;
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
/// 批次 / 库存主档**受控写入口**的真库验收（ADR-020 §9.2 D2 / §10 WS-17）。
///
/// 走生产路径：`DocumentActionExecutor`（按钮授权 = fail-closed 的 `SYSDD_BUTTON`/`SYSDH_BUTTON`）
/// → `MasterFieldWriteHandler` → **统一保存路径** `WorkbenchCommandHandler.UpdateRecordAsync`
/// （校验 / 只读判定 / 审计前后值 / 幂等全在那里）。夹具 `ZZMFW` 前缀，`IAsyncLifetime` 收尾即删。
///
/// 四条边界，逐条都有用例：
///   ① 能改人工语义列（`EFFECT_DATE`）**并落审计前后值**；
///   ② 引擎维护列（`IN_SUM` 等）**服务端拒**——即使配置把它声明成参数、请求体里带着它；
///   ③ **不产生库存流水**（不触发 `inventory-move`、`INV_DEPOT_LOG` 一行不加）；
///   ④ 探路（`CONFIRM_TAG=1` + `confirm=false`）**一个字都不写**。
/// </summary>
[Collection("live-database")]
public sealed class MasterFieldWriteLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1302;
    private const string MasterTable = "INV_BATCH_M";
    private const string BatchNo = "ZZMFW-B1";
    private const string TestProduct = "ZZMFWPRO01";
    private const string TestUser = "ZZMFW0001";
    private const string OriginalEffectDate = "2026-01-01";
    private const string NewEffectDate = "2026-12-31";
    private const double OriginalInSum = 5d;

    private DbConnectionFactory _connections = null!;
    private WorkbenchDefinitionProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _connections = Connections();
        _provider = new WorkbenchDefinitionProvider(_connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
        await ExecAsync(connection,
            "INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE) VALUES (@pro, N'ZZMFW 受控写入料件', N'规格', '3');",
            ("@pro", TestProduct));
        await ExecAsync(connection, """
            INSERT INTO dbo.INV_BATCH_M (BATCH_NO, PRO_NO, BATCH_DATE, EFFECT_DATE, AGAIN_CHECK_DATE, IN_SUM, OUT_SUM, CONFIRM_TAG)
                VALUES (@batch, @pro, '2026-01-02', @effect, '2026-01-03', @inSum, 2, 0);
            """, ("@batch", BatchNo), ("@pro", TestProduct), ("@effect", OriginalEffectDate), ("@inSum", OriginalInSum));
        // 按钮级授权是 fail-closed：不插这一行，连"能按按钮"都谈不上（但那正是它的设计）
        await ExecAsync(connection,
            "INSERT INTO dbo.SYSDD_BUTTON (USER_ID, M_IDX, BUTTON_KEY, ALLOW_TAG) VALUES (@user, @module, @key, 1);",
            ("@user", TestUser), ("@module", ModuleId), ("@key", MasterFieldWriteHandler.ActionKey));
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
    }

    private static async Task CleanupAsync(SqlConnection connection)
    {
        // 先删子表：AUDIT_FIELD_CHANGE 对 AUDIT_EVENT 有外键（先后值就落在子表里）
        await ExecAsync(connection, """
            DELETE c FROM dbo.AUDIT_FIELD_CHANGE c JOIN dbo.AUDIT_EVENT e ON e.EVENT_ID = c.EVENT_ID
             WHERE e.MODULE_ID = @module AND e.RESOURCE_KEY = @key;
            """, ("@module", ModuleId), ("@key", $"{BatchNo},{TestProduct}"));
        await ExecAsync(connection, "DELETE FROM dbo.AUDIT_EVENT WHERE MODULE_ID = @module AND RESOURCE_KEY = @key;",
            ("@module", ModuleId), ("@key", $"{BatchNo},{TestProduct}"));
        await ExecAsync(connection, "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE MODULE_ID = @module AND ACTION = N'ACTION';",
            ("@module", ModuleId));
        await ExecAsync(connection, "DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID = @user;", ("@user", TestUser));
        await ExecAsync(connection, "DELETE FROM dbo.INV_BATCH_M WHERE BATCH_NO = @batch AND PRO_NO = @pro;",
            ("@batch", BatchNo), ("@pro", TestProduct));
        await ExecAsync(connection, "DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", TestProduct));
    }

    // ===== ① 能改 + 落审计 + 不产生库存流水 =====

    [Fact]
    public async Task 改效期_落审计前后值_且不产生库存流水()
    {
        var result = await RunAsync(new Dictionary<string, string> { ["effectDate"] = NewEffectDate });

        Assert.True(result.Status == DocumentActionStatus.Ok, result.ErrorMessage);
        Assert.Equal(DocumentActionOutcome.Refreshed, result.Result!.Outcome);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(NewEffectDate, await ScalarAsync<string>(connection,
            "SELECT CONVERT(varchar(10), EFFECT_DATE, 120) FROM dbo.INV_BATCH_M WHERE BATCH_NO = @batch AND PRO_NO = @pro;",
            ("@batch", BatchNo), ("@pro", TestProduct)));

        // 审计前后值：状态列留痕靠的就是它（这里改的是属性列，同样要能追）
        var changes = new List<(string Field, string? Old, string? New)>();
        await using (var command = new SqlCommand("""
            SELECT c.FIELD_NAME, c.OLD_VALUE, c.NEW_VALUE
              FROM dbo.AUDIT_EVENT e JOIN dbo.AUDIT_FIELD_CHANGE c ON c.EVENT_ID = e.EVENT_ID
             WHERE e.MODULE_ID = @module AND e.RESOURCE_KEY = @key AND e.ACTION = N'UPDATE';
            """, connection))
        {
            command.Parameters.AddWithValue("@module", ModuleId);
            command.Parameters.AddWithValue("@key", $"{BatchNo},{TestProduct}");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                changes.Add((reader.GetString(0).Trim(),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }
        var change = Assert.Single(changes, item => item.Field.Equals("EFFECT_DATE", StringComparison.OrdinalIgnoreCase));
        // 审计里的值按写入时的区域性格式落库（实测 "2026/1/1 0:00:00"），故按日期比而不是比字符串
        Assert.Equal(new DateTime(2026, 1, 1), ParseAny(change.Old!).Date);
        Assert.Equal(new DateTime(2026, 12, 31), ParseAny(change.New!).Date);

        // 不碰库存账：本模块没有 inventory-move，改动也不该凭空写流水
        Assert.Equal(0, await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.INV_DEPOT_LOG WHERE LTRIM(RTRIM(PRO_NO)) = @pro;", ("@pro", TestProduct)));
    }

    // ===== ② 引擎维护列：配置声明了、请求带着，也一样拒 =====

    [Fact]
    public async Task 引擎维护列_配置声明成参数也照样拒()
    {
        var result = await RunAsync(new Dictionary<string, string> { ["inSum"] = "99" });

        Assert.Equal(DocumentActionStatus.Failed, result.Status);
        Assert.Contains("维护", result.ErrorMessage);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(OriginalInSum, await ScalarAsync<double>(connection,
            "SELECT IN_SUM FROM dbo.INV_BATCH_M WHERE BATCH_NO = @batch AND PRO_NO = @pro;",
            ("@batch", BatchNo), ("@pro", TestProduct)));
    }

    [Fact]
    public async Task 未声明的参数_框架层先拒()
    {
        var result = await RunAsync(new Dictionary<string, string> { ["qty"] = "5" });
        Assert.Equal(DocumentActionStatus.ValidationFailed, result.Status);
    }

    // ===== ③ 探路不写 =====

    [Fact]
    public async Task 探路_只说会改成什么_一个字都不写()
    {
        var result = await RunAsync(new Dictionary<string, string> { ["effectDate"] = NewEffectDate }, confirm: false);

        Assert.Equal(DocumentActionStatus.Ok, result.Status);
        Assert.True(result.RequiresConfirmation);
        Assert.Contains("未改动", result.Result!.Message);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        // 值没动、审计里也没有一次 UPDATE（探路整体回滚）
        Assert.Equal(OriginalEffectDate, await ScalarAsync<string>(connection,
            "SELECT CONVERT(varchar(10), EFFECT_DATE, 120) FROM dbo.INV_BATCH_M WHERE BATCH_NO = @batch AND PRO_NO = @pro;",
            ("@batch", BatchNo), ("@pro", TestProduct)));
        Assert.Equal(0, await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE MODULE_ID = @module AND RESOURCE_KEY = @key AND ACTION = N'UPDATE';",
            ("@module", ModuleId), ("@key", $"{BatchNo},{TestProduct}")));
    }

    // ===== ④ 只读位（元数据事实）与统一保存路径的第二道闸 =====

    [Fact]
    public async Task 只读位_统一保存路径拒服务端维护列()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var handler = CommandHandler();
        var result = await handler.UpdateRecordAsync(
            Definition(),
            Form(),
            [BatchNo, TestProduct],
            new SaveRecordRequest(new Dictionary<string, string?> { ["IN_SUM"] = "99" }),
            "测试经办人", TestUser, null, CancellationToken.None);

        Assert.Equal(RecordAccessStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FieldErrors!, error => error.Code == "READONLY_FIELD");
        Assert.Equal(OriginalInSum, await ScalarAsync<double>(connection,
            "SELECT IN_SUM FROM dbo.INV_BATCH_M WHERE BATCH_NO = @batch AND PRO_NO = @pro;",
            ("@batch", BatchNo), ("@pro", TestProduct)));
    }

    [Fact]
    public async Task 引擎维护列在元数据里都是只读()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        // 迁移 241 的"顺带"：台帐问题 5——只读位才是元数据事实，白名单只是会改的配置
        Assert.Equal(6, await ScalarAsync<int>(connection, """
            SELECT COUNT(*) FROM dbo.FIELDS
             WHERE RTRIM(T_ID) = N'INV_PRO_DEPOT' AND ISNULL(IS_READONLY, 0) = 1
               AND RTRIM(F_ID) IN (N'QTY', N'COST_PRICE', N'COST_AMOUNT', N'INIT_QTY', N'USEABLE_QTY', N'LAST_CHECK_DATE');
            """));
        // 对照：人工语义列必须仍可写，否则这扇门等于白开
        Assert.Equal(3, await ScalarAsync<int>(connection, """
            SELECT COUNT(*) FROM dbo.FIELDS
             WHERE RTRIM(T_ID) = N'INV_BATCH_M' AND ISNULL(IS_READONLY, 0) = 0
               AND RTRIM(F_ID) IN (N'EFFECT_DATE', N'BATCH_DATE', N'AGAIN_CHECK_DATE');
            """));
    }

    // ===== 装配（与 Program.cs 同源：真实服务 + 真库）=====

    private WorkbenchAuditWriter AuditWriter() =>
        new(_connections, new HttpContextAccessor(), _provider, Options.Create(new AuditSettings()));

    private WorkbenchCommandHandler CommandHandler()
    {
        var auditWriter = AuditWriter();
        var engine = new EffectEngineInvoker(
            new EffectEngineSettings { Enabled = true },
            new EffectPlanLoader(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            NullLogger<EffectEngineInvoker>.Instance);
        var workflow = new WorkflowEngine(_connections, auditWriter, _provider, engine, NullLogger<WorkflowEngine>.Instance);
        var idempotency = new WorkbenchIdempotency();
        var approval = new WorkbenchApprovalService(_connections, auditWriter, workflow, engine, idempotency,
            NullLogger<WorkbenchApprovalService>.Instance);
        return new WorkbenchCommandHandler(_connections, auditWriter, approval,
            new WorkbenchScopeFilter(new ApiMetrics()), engine, idempotency,
            new DepotStockPolicyService(_connections, auditWriter), NullLogger<WorkbenchCommandHandler>.Instance);
    }

    private DocumentActionExecutor Executor() =>
        new(_connections,
            new DocumentActionRegistry([new MasterFieldWriteHandler(AuditWriter())], NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(_connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            new WorkbenchAuditWriter(_connections, new HttpContextAccessor(), _provider, Options.Create(new AuditSettings())),
            NullLogger<DocumentActionExecutor>.Instance);

    private Task<DocumentActionExecution> RunAsync(IReadOnlyDictionary<string, string> parameters, bool confirm = true) =>
        Executor().ExecuteAsync(Definition(), Form(), MasterFieldWriteHandler.ActionKey,
            new DocumentActionRequest([BatchNo, TestProduct], JsonSerializer.SerializeToElement(parameters), confirm),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

    /// <summary>
    /// 模块 1302 的定义（手工构造，与已发布快照同形）。
    /// 参数声明里**故意把 `inSum` 也声明进去**：那正是"配置扩张了权限"的情形——
    /// 处理器里的白名单/维护列两道闸必须在这种情形下仍然拒（WS-17 的判别性就在这）。
    /// </summary>
    private static WorkbenchDefinition Definition() =>
        new(ModuleId: ModuleId, Title: "料件批号资料", MasterTable: MasterTable, DetailTable: null,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: false, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["BATCH_NO", "PRO_NO"], DetailNoFields: "", HasWorkflow: false,
            UserId: TestUser, ExecTag: "Z",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            BusinessActions: JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    seq = 1,
                    eventCode = "MANUAL",
                    effectKey = MasterFieldWriteHandler.ActionKey,
                    enabled = true,
                    label = "修改人工字段",
                    confirmTag = true,
                    failMode = "BLOCK",
                    condition = (string?)null,
                    // `params` 是 C# 关键字，匿名对象属性要写 @params（序列化出去仍是 "params"）
                    @params = """{"fields":[{"key":"effectDate","label":"有效日期","type":"date"},{"key":"batchDate","label":"批号启用日期","type":"date"},{"key":"againCheckDate","label":"复检日期","type":"date"},{"key":"inSum","label":"入库累计","type":"number"}]}""",
                },
            }));

    private static FormDefinition Form() =>
        new(ModuleId, "料件批号资料", MasterTable, null, false, true, "view",
            [
                Field("BATCH_NO", "批号", "nchar", primaryKey: true),
                Field("PRO_NO", "品号", "nchar", primaryKey: true),
                Field("EFFECT_DATE", "有效日期", "datetime"),
                Field("BATCH_DATE", "批号启用日期", "datetime"),
                Field("AGAIN_CHECK_DATE", "复检日期", "datetime"),
                // 引擎维护列在表单上就是只读的（元数据事实）——与本用例的"保存路径第二道闸"互为印证
                Field("IN_SUM", "入库累计", "float", readonlyField: true),
            ],
            [], ["BATCH_NO", "PRO_NO"], string.Empty, string.Empty);

    private static FormFieldDefinition Field(
        string key, string label, string dataType, bool primaryKey = false, bool readonlyField = false) =>
        new(key, label, dataType, 100, null, false, null, null, null, readonlyField, true, false, false, null,
            [], primaryKey, false, false, false, false, false, null);

    private static DbConnectionFactory Connections() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build());

    /// <summary>审计值的历史格式随写入时的区域性而变，按"能解析就行"处理（要比的是日期，不是字符串）。</summary>
    private static DateTime ParseAny(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.Parse(value, CultureInfo.CurrentCulture);

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
