using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 助手模型表（<c>dbo.ASSISTANT_MODEL</c>）的真库集成测试——见 ADR-030 §3 与迁移 291。
///
/// <para>
/// 这张表有三处"靠数据库而不是靠代码"的约束，最值得实地验：IDENTITY 主键、
/// **筛选唯一索引保证至多一条"当前模型"**、以及"当前模型删不掉"。测试后置迁移会真的建表，
/// 所以这些断言同时也在验证迁移本身。
/// </para>
///
/// <para>
/// **清理要格外小心**：这个表里留着一条 `IS_ACTIVE = 1` 的测试行，会让开发环境的助手在下次
/// 重启后去用它（那把密钥当然没配），表现成"助手突然不可用"。所以 <see cref="Dispose"/> 里
/// **先清当前、再删行**，宁可多写一行也不能留脏。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantModelCatalogIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(
        () => Environment.GetEnvironmentVariable("MSSQL_ERP_CONN"));

    private readonly AssistantModelCatalog _catalog;
    private readonly AssistantRepository _sessions;
    private readonly AssistantUsageRepository _usage;
    private readonly List<int> _modelIds = [];
    private readonly List<long> _sessionIds = [];

    public AssistantModelCatalogIntegrationTests()
    {
        EnsureSchema();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        var connections = new DbConnectionFactory(config);
        _catalog = new AssistantModelCatalog(connections);
        _sessions = new AssistantRepository(connections);
        _usage = new AssistantUsageRepository(connections);
    }

    private static void EnsureSchema()
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null)
        {
            return;
        }

        var result = DbUp.DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(ErpDatabaseInitializer).Assembly,
                name => name.Contains(".Data.Migrations.", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .WithTransactionPerScript()
            .JournalToSqlTable("dbo", "ERP_SCHEMA_JOURNAL")
            .LogToConsole()
            .Build()
            .PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException("测试前置：EOS.ERP 助手模型表迁移失败", result.Error);
        }
    }

    /// <summary>造一条测试用模型记录。<c>API_KEY_ENV_VAR</c> 用测试专用名，避免撞上真密钥。</summary>
    private async Task<int> NewModelAsync(string displayName, CancellationToken token, bool enabled = true)
    {
        var modelId = await _catalog.CreateAsync(new AssistantModelWrite(
            DisplayName: displayName,
            Provider: "deepseek",
            ModelName: "deepseek-chat",
            BaseUrl: "https://api.deepseek.com",
            ApiKeyEnvVar: "EOS_TEST_ASSISTANT_KEY",
            TimeoutSeconds: 120,
            Temperature: 0.30m,
            MaxTokens: 4096,
            Enabled: enabled,
            SortIdx: 900,
            Remark: "集成测试创建"), "eosdev-test", token);
        _modelIds.Add(modelId);
        return modelId;
    }

    [Fact]
    public async Task Model_Crud_Roundtrip()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;

        var modelId = await NewModelAsync("集成测试模型", token);

        var listed = await _catalog.ListAsync(token);
        var row = listed.Single(item => item.ModelId == modelId);
        Assert.Equal("集成测试模型", row.DisplayName);
        Assert.Equal("deepseek", row.Provider);
        Assert.Equal("EOS_TEST_ASSISTANT_KEY", row.ApiKeyEnvVar);
        Assert.Equal(0.30m, row.Temperature);
        Assert.Equal(4096, row.MaxTokens);
        Assert.True(row.Enabled);
        // 新建的一律不是当前：把新行直接置为当前，会在无人察觉时换掉正在用的模型
        Assert.False(row.IsActive);

        var updated = await _catalog.UpdateAsync(modelId, new AssistantModelWrite(
            DisplayName: "集成测试模型（改）", Provider: "openai-compatible", ModelName: "deepseek-reasoner",
            BaseUrl: "https://api.deepseek.com", ApiKeyEnvVar: "EOS_TEST_ASSISTANT_KEY",
            TimeoutSeconds: 60, Temperature: null, MaxTokens: null,
            Enabled: false, SortIdx: 901, Remark: null), "eosdev-test", token);
        Assert.True(updated);

        var reloaded = await _catalog.GetAsync(modelId, token);
        Assert.NotNull(reloaded);
        Assert.Equal("集成测试模型（改）", reloaded.DisplayName);
        Assert.Null(reloaded.Temperature);
        Assert.Null(reloaded.MaxTokens);
        Assert.False(reloaded.Enabled);

        // 非当前模型可以直接删
        Assert.True(await _catalog.DeleteAsync(modelId, token));
        _modelIds.Remove(modelId);
        Assert.Null(await _catalog.GetAsync(modelId, token));
    }

    [Fact]
    public async Task Only_One_Active_Model_And_Active_Is_Not_Deletable()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;

        var first = await NewModelAsync("集成测试模型甲", token);
        var second = await NewModelAsync("集成测试模型乙", token);

        Assert.True(await _catalog.ActivateAsync(first, "eosdev-test", token));
        Assert.Equal(first, (await _catalog.GetActiveAsync(token))!.ModelId);

        // 切到第二个：第一个必须自动让位，数据库里至多一条 IS_ACTIVE = 1
        Assert.True(await _catalog.ActivateAsync(second, "eosdev-test", token));
        var active = await _catalog.GetActiveAsync(token);
        Assert.Equal(second, active!.ModelId);
        Assert.Single((await _catalog.ListAsync(token)).Where(item => item.IsActive));

        // 当前模型删不掉——删掉它会让助手在无人察觉的情况下退回配置文件那套
        Assert.False(await _catalog.DeleteAsync(second, token));
        Assert.NotNull(await _catalog.GetAsync(second, token));

        // 停用之后再激活应当失败（不能把停用的模型设为当前）
        Assert.True(await _catalog.UpdateAsync(second, new AssistantModelWrite(
            DisplayName: "集成测试模型乙", Provider: "deepseek", ModelName: "deepseek-chat",
            BaseUrl: "https://api.deepseek.com", ApiKeyEnvVar: "EOS_TEST_ASSISTANT_KEY",
            TimeoutSeconds: 120, Temperature: null, MaxTokens: null,
            Enabled: false, SortIdx: 900, Remark: null), "eosdev-test", token));
        Assert.False(await _catalog.ActivateAsync(second, "eosdev-test", token));

        // 取消当前后，当前模型就没了（助手回到配置文件那套）
        await _catalog.ClearActiveAsync(token);
        Assert.Null(await _catalog.GetActiveAsync(token));
    }

    /// <summary>
    /// 按模型聚合用量：<c>MODEL_NAME</c> 这列一直只写不聚合，"换了模型之后用量怎么变的"无从回答，
    /// 这个查询就是补那个洞——用一条带模型名的回复实地验一次。
    /// </summary>
    [Fact]
    public async Task Per_Model_Usage_Groups_By_Model_Name()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        const string user = "eosdev-assistant-test-a";
        const string modelName = "eosdev-integration-model";

        var session = await _sessions.CreateSessionAsync(user, token);
        _sessionIds.Add(session.Id);
        await _sessions.AddAssistantMessageAsync(
            user, session.Id, "乙：用于聚合校验的回复。", modelName, 123, 45, 900, "c-model-usage", token);

        var since = new DateTimeOffset(DateTime.UtcNow.AddDays(-1).Date, TimeSpan.Zero);
        var usage = await _usage.GetPerModelUsageAsync(since, token);

        var mine = usage.Single(item => item.ModelName == modelName);
        Assert.Equal(1, mine.Usage.Requests);
        Assert.Equal(123, mine.Usage.PromptTokens);
        Assert.Equal(45, mine.Usage.CompletionTokens);

        // 趋势里也应当有今天这一格
        var trend = await _usage.GetDailyTrendAsync(since, token);
        Assert.Contains(trend, item => item.Day.Date == DateTime.UtcNow.Date && item.Usage.Requests >= 1);
    }

    public void Dispose()
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null) return;
        using var conn = new SqlConnection(connectionString);
        conn.Open();

        // 先取消"当前"：留着一条指向测试行的当前模型，会让开发环境的助手重启后去用一个没密钥的模型
        foreach (var id in _modelIds)
        {
            using var reset = new SqlCommand(
                "UPDATE dbo.ASSISTANT_MODEL SET IS_ACTIVE = 0 WHERE MODEL_ID = @Id;", conn);
            reset.Parameters.AddWithValue("@Id", id);
            reset.ExecuteNonQuery();
        }

        foreach (var id in _modelIds)
        {
            using var cmd = new SqlCommand("DELETE FROM dbo.ASSISTANT_MODEL WHERE MODEL_ID = @Id;", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }

        foreach (var id in _sessionIds)
        {
            using var cmd = new SqlCommand(
                "DELETE FROM dbo.ASSISTANT_MESSAGE WHERE SESSION_ID = @Id; DELETE FROM dbo.ASSISTANT_SESSION WHERE ID = @Id;",
                conn);
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }
    }
}
