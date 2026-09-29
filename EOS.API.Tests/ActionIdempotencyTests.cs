using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 助手执行类动作的幂等验收。
///
/// <para>
/// 核心事实有两条：**幂等键由服务端生成**（模型既不生成也拿不到），
/// 且**重放同一键不重复写**。第一条靠"schema 里根本没有这个字段"证，第二条靠真库重放证。
/// </para>
/// </summary>
[Collection("live-database")]
public sealed class ActionIdempotencyTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1403;
    private const string MasterTable = "COP_CHAFFER_M";
    private const string TestType = "ZZIDEM";
    private const string TestUser = "ZZIDEM001";

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
    /// 收尾只删自己造的行。幂等键由服务端从工具调用身份算出（哈希里没有用例前缀），
    /// 因此按同样的输入重算一遍再按值删——按 M_IDX 整体删会动到真实单据的幂等记录。
    /// </summary>
    private static async Task CleanupAsync(SqlConnection connection)
    {
        await using (var master = new SqlCommand("DELETE FROM dbo.COP_CHAFFER_M WHERE CHAFFER_TYPE = @type;", connection))
        {
            master.Parameters.AddWithValue("@type", TestType);
            await master.ExecuteNonQueryAsync();
        }
        await using var keys = new SqlCommand(
            "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE IDEMPOTENCY_KEY = @key;", connection);
        keys.Parameters.AddWithValue("@key", ReplayKey());
        await keys.ExecuteNonQueryAsync();
    }

    /// <summary>本用例重放的那一次会算出的服务端幂等键（确定性，可在收尾时重算）。</summary>
    private static string ReplayKey()
    {
        var request = new AssistantActionRequest(ModuleId, AssistantRecordActionKind.Insert,
            [new AssistantActionRow([],
                new Dictionary<string, string?> { ["CHAFFER_TYPE"] = TestType, ["CHAFFER_DATE"] = "2026-09-29" })]);
        return AssistantActionIdempotency.Create(7, "tool:call-replay", AssistantRecordActionNames.For(request.Kind),
            AssistantActionIdempotency.Canonicalize(request, request.Rows[0]));
    }

    // ===== ① 键不在模型可见的 schema 里 =====

    [Fact]
    public void 幂等键不出现在模型可见的工具参数_schema_里()
    {
        // 两个工具的参数 schema 都是服务端硬编码的常量（构造依赖只服务执行，读取 schema 不触及它们）
        var schemas = new[]
        {
            AssistantRecordActionArguments.ParametersJson,
            new PreviewRecordActionTool(null!, null!, null!).ParametersJson,
            new ApplyRecordActionTool(null!, null!, null!).ParametersJson,
        };
        Assert.All(schemas, schema => Assert.Equal(AssistantRecordActionArguments.ParametersJson, schema));

        foreach (var schema in schemas)
        {
            // 参数 schema 里只能有 module/action/rows 与行内的业务字段；
            // 出现任何"键"类参数都意味着模型可以每次造一个新键，幂等保护随即失效。
            using var document = JsonDocument.Parse(schema);
            var properties = document.RootElement.GetProperty("properties").EnumerateObject()
                .Select(property => property.Name).ToList();
            Assert.DoesNotContain(properties, name => name.Contains("idempot", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("idempotencyKey", properties);

            var rowProperties = document.RootElement.GetProperty("properties").GetProperty("rows")
                .GetProperty("items").GetProperty("properties").EnumerateObject()
                .Select(property => property.Name).ToList();
            Assert.DoesNotContain(rowProperties, name => name.Contains("idempot", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(new[] { "keys", "values", "details", "detail_serials" }, rowProperties);
        }
    }

    // ===== ② 键由"这次工具调用"推导：同调用稳定，换参数即换键 =====

    [Fact]
    public void 幂等键_同一次工具调用稳定_参数变化即变化()
    {
        var first = new AssistantActionRequest(ModuleId, AssistantRecordActionKind.Insert, [Row("甲")]);
        var sameAgain = new AssistantActionRequest(ModuleId, AssistantRecordActionKind.Insert, [Row("甲")]);
        var changedValue = new AssistantActionRequest(ModuleId, AssistantRecordActionKind.Insert, [Row("乙")]);
        var changedAction = new AssistantActionRequest(ModuleId, AssistantRecordActionKind.Update, [Row("甲")]);

        var key = Key(first, "call-1");
        Assert.Equal(key, Key(sameAgain, "call-1"));
        Assert.NotEqual(key, Key(changedValue, "call-1"));
        Assert.NotEqual(key, Key(changedAction, "call-1"));
        Assert.NotEqual(key, Key(first, "call-2"));
        Assert.Equal(AssistantActionIdempotency.KeyLength, key.Length);
    }

    // ===== ③ 重放同一键：不重复写，返回同一结果 =====

    [Fact]
    public async Task 重放同一键_只写一次_且返回同一结果()
    {
        var agentContext = AssistantActionTestHarness.AgentContext();
        var auditWriter = AssistantActionTestHarness.AuditWriter(_connections, agentContext);
        var definitions = new AssistantActionTestHarness.FixedDefinitionSource(Definition(), Form());
        var policy = AssistantActionTestHarness.Policy(
            definitions, new AssistantActionTestHarness.FixedPermissions(AssistantActionTestHarness.Rights()),
            ModuleId);
        var service = AssistantActionTestHarness.ActionService(
            new AssistantActionGate(policy),
            AssistantActionTestHarness.CommandHandler(_connections, ConnectionString, auditWriter),
            _connections, agentContext, auditWriter);

        var request = new AssistantActionRequest(ModuleId, AssistantRecordActionKind.Insert,
            [new AssistantActionRow([],
                new Dictionary<string, string?> { ["CHAFFER_TYPE"] = TestType, ["CHAFFER_DATE"] = "2026-09-29" })]);

        // 同一次工具调用重放：会话与工具调用 ID 相同 ⇒ 服务端算出同一个键
        var seed = AssistantActionKeySeed.FromToolCall(7, "call-replay");
        var first = await service.ExecuteAsync(TestUser, TestUser, request, seed, CancellationToken.None);
        var replay = await service.ExecuteAsync(TestUser, TestUser, request, seed, CancellationToken.None);

        var firstRow = Assert.Single(first.Rows);
        var replayRow = Assert.Single(replay.Rows);
        Assert.True(firstRow.Succeeded, firstRow.Message);
        Assert.True(replayRow.Succeeded, replayRow.Message);
        Assert.Equal(firstRow.IdempotencyKey, replayRow.IdempotencyKey);
        Assert.Equal(firstRow.ResultKeys, replayRow.ResultKeys);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(1, await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.COP_CHAFFER_M WHERE CHAFFER_TYPE = @type;", ("@type", TestType)));
        Assert.Equal(1, await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.WORKBENCH_IDEMPOTENCY WHERE IDEMPOTENCY_KEY = @key;",
            ("@key", firstRow.IdempotencyKey)));
    }

    // ===== 装配 =====

    private static AssistantActionRow Row(string remark) => new([], new Dictionary<string, string?>
    {
        ["CHAFFER_TYPE"] = TestType,
        ["CHAFFER_DATE"] = "2026-09-29",
        ["REMARK"] = remark,
    });

    private static string Key(AssistantActionRequest request, string toolCallId) =>
        AssistantActionIdempotency.Create(7, "tool:" + toolCallId, AssistantRecordActionNames.For(request.Kind),
            AssistantActionIdempotency.Canonicalize(request, request.Rows[0]));

    private static WorkbenchDefinition Definition() => new(
        ModuleId: ModuleId, Title: "客户询价单", MasterTable: MasterTable, DetailTable: null,
        MasterFields: [], DetailFields: [], DefaultSort: null,
        HasAdd: true, HasEdit: true, DetailNoSave: false,
        MasterPkOrder: ["CHAFFER_TYPE", "CHAFFER_NO"], DetailNoFields: "", HasWorkflow: false,
        UserId: TestUser, ExecTag: "A",
        FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
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
