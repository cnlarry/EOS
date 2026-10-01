using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 助手模型配置两级（<c>dbo.ASSISTANT_PROVIDER</c> + <c>dbo.ASSISTANT_MODEL</c>）的真库集成测试
/// ——见 ADR-030 §3 与迁移 292。
///
/// <para>
/// 最值得实地验的是三处"靠数据库而不是靠代码"的约束：供应商 CODE 唯一、
/// **筛选唯一索引保证至多一条"当前模型"**、以及"当前模型删不掉 / 名下有模型的供应商删不掉"。
/// 测试后置迁移会真的建表，所以这些断言同时也在验证迁移本身。
/// </para>
///
/// <para>
/// **清理要格外小心**：表里留着一条 <c>IS_ACTIVE = 1</c> 的测试行，会让开发环境的助手在下次重启后
/// 去用它（那把密钥当然没配），表现成"助手突然不可用"。所以 <see cref="Dispose"/> 里
/// **先清当前 → 再删模型 → 再删供应商**（外键顺序），宁可多写几行也不能留脏。
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
    private readonly List<int> _providerIds = [];
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

    /// <summary>
    /// 造一个测试用供应商。CODE 每次唯一：CODE 上有唯一约束，而测试之间不该互相绊倒。
    /// 环境变量名用测试专用的，避免撞上真实密钥。
    /// </summary>
    private async Task<int> NewProviderAsync(CancellationToken token, bool enabled = true)
    {
        var providerId = await _catalog.CreateProviderAsync(new AssistantProviderWrite(
            Code: $"eosdev-{Guid.NewGuid():N}"[..16],
            DisplayName: "集成测试供应商",
            BaseUrl: "https://api.integration.test/v1",
            ApiKeyEnvVar: "EOS_TEST_ASSISTANT_KEY",
            TimeoutSeconds: 120,
            Enabled: enabled,
            SortIdx: 900,
            Remark: "集成测试创建"), "eosdev-test", token);
        _providerIds.Add(providerId);
        return providerId;
    }

    private async Task<int> NewModelAsync(int providerId, string modelCode, CancellationToken token, bool enabled = true)
    {
        var modelId = await _catalog.CreateModelAsync(new AssistantModelWrite(
            ProviderId: providerId,
            ModelCode: modelCode,
            DisplayName: $"{modelCode}（集成测试）",
            ContextWindow: 65536,
            MaxOutputTokens: 4096,
            DefaultTemperature: 0.30m,
            TimeoutSeconds: 120,
            InputPerMillionYuan: 1.5m,
            OutputPerMillionYuan: 6m,
            SupportsTools: true,
            Enabled: enabled,
            SortIdx: 900,
            Remark: "集成测试创建"), "eosdev-test", token);
        _modelIds.Add(modelId);
        return modelId;
    }

    [Fact]
    public async Task Provider_And_Model_Crud_Roundtrip()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;

        var providerId = await NewProviderAsync(token);
        var modelId = await NewModelAsync(providerId, "deepseek-chat", token);

        var provider = await _catalog.GetProviderAsync(providerId, token);
        Assert.NotNull(provider);
        // 端点在**供应商**上：再加一个模型时不必重复填它
        Assert.Equal("https://api.integration.test/v1", provider.BaseUrl);
        Assert.Equal("EOS_TEST_ASSISTANT_KEY", provider.ApiKeyEnvVar);

        var model = await _catalog.GetModelAsync(modelId, token);
        Assert.NotNull(model);
        Assert.Equal("deepseek-chat", model.ModelCode);
        Assert.Equal(65536, model.ContextWindow);
        Assert.Equal(1.5m, model.InputPerMillionYuan);
        Assert.Equal(6m, model.OutputPerMillionYuan);
        Assert.True(model.SupportsTools);
        // 新建的一律不是当前：把新行直接置为当前，会在无人察觉时换掉正在用的模型
        Assert.False(model.IsActive);

        // 两级清单：模型挂在它的供应商下
        var providers = await _catalog.ListProvidersAsync(token);
        Assert.Contains(providers, item => item.ProviderId == providerId);
        var models = await _catalog.ListModelsAsync(token);
        Assert.Contains(models, item => item.ModelId == modelId && item.ProviderId == providerId);

        var updated = await _catalog.UpdateModelAsync(modelId, new AssistantModelWrite(
            ProviderId: providerId,
            ModelCode: "deepseek-reasoner",
            DisplayName: "推理模型（改）",
            ContextWindow: 131072,
            MaxOutputTokens: null,
            DefaultTemperature: null,
            TimeoutSeconds: 600,
            InputPerMillionYuan: null,
            OutputPerMillionYuan: null,
            SupportsTools: false,
            Enabled: false,
            SortIdx: 901,
            Remark: null), "eosdev-test", token);
        Assert.True(updated);

        var reloaded = await _catalog.GetModelAsync(modelId, token);
        Assert.NotNull(reloaded);
        Assert.Equal("deepseek-reasoner", reloaded.ModelCode);
        Assert.Equal(131072, reloaded.ContextWindow);
        Assert.Null(reloaded.MaxOutputTokens);
        Assert.Null(reloaded.InputPerMillionYuan);
        Assert.False(reloaded.SupportsTools);
        Assert.False(reloaded.Enabled);
        // 超时覆盖留了值：推理模型要比供应商默认更久，所以是覆盖而不是继承
        Assert.Equal(600, reloaded.TimeoutSeconds);

        Assert.True(await _catalog.DeleteModelAsync(modelId, token));
        _modelIds.Remove(modelId);
        Assert.Null(await _catalog.GetModelAsync(modelId, token));
    }

    [Fact]
    public async Task Provider_With_Models_Is_Not_Deletable()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;

        var providerId = await NewProviderAsync(token);
        var modelId = await NewModelAsync(providerId, "deepseek-chat", token);

        // 名下有模型就不给删：级联会一次带走整家供应商的配置，手滑的代价太大
        Assert.Equal(1, await _catalog.CountModelsAsync(providerId, token));
        Assert.False(await _catalog.DeleteProviderAsync(providerId, token));
        Assert.NotNull(await _catalog.GetProviderAsync(providerId, token));

        // 先删模型，供应商才删得掉
        Assert.True(await _catalog.DeleteModelAsync(modelId, token));
        _modelIds.Remove(modelId);
        Assert.True(await _catalog.DeleteProviderAsync(providerId, token));
        _providerIds.Remove(providerId);
        Assert.Null(await _catalog.GetProviderAsync(providerId, token));
    }

    [Fact]
    public async Task Only_One_Active_Model_And_Disabled_Provider_Takes_Its_Models_Offline()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;

        var providerId = await NewProviderAsync(token);
        // 同一供应商下的两个模型：这正是"DeepSeek 的 Flash 与 Pro"的形状
        var chat = await NewModelAsync(providerId, "deepseek-chat", token);
        var reasoner = await NewModelAsync(providerId, "deepseek-reasoner", token);

        Assert.True(await _catalog.ActivateModelAsync(chat, "eosdev-test", token));
        var active = await _catalog.GetActiveAsync(token);
        Assert.Equal(chat, active!.Model.ModelId);
        // 解析模型配置要同时拿到两级：端点在供应商、窗口与单价在模型
        Assert.Equal(providerId, active.Provider.ProviderId);
        Assert.Equal(65536, active.Model.ContextWindow);
        Assert.Equal(1.5m, active.Model.InputPerMillionYuan);

        // 切到第二个：第一个必须自动让位，数据库里至多一条 IS_ACTIVE = 1
        Assert.True(await _catalog.ActivateModelAsync(reasoner, "eosdev-test", token));
        Assert.Equal(reasoner, (await _catalog.GetActiveAsync(token))!.Model.ModelId);
        Assert.Single(await _catalog.ListModelsAsync(token), item => item.IsActive);

        // 当前模型删不掉——删掉它会让助手在无人察觉的情况下变成"未配置"
        Assert.False(await _catalog.DeleteModelAsync(reasoner, token));
        Assert.NotNull(await _catalog.GetModelAsync(reasoner, token));

        // 停用**供应商**：它名下所有模型一起下线，不必逐个停用模型
        Assert.True(await _catalog.UpdateProviderAsync(providerId, new AssistantProviderWrite(
            Code: (await _catalog.GetProviderAsync(providerId, token))!.Code,
            DisplayName: "集成测试供应商",
            BaseUrl: "https://api.integration.test/v1",
            ApiKeyEnvVar: "EOS_TEST_ASSISTANT_KEY",
            TimeoutSeconds: 120,
            Enabled: false,
            SortIdx: 900,
            Remark: null), "eosdev-test", token));
        Assert.Null(await _catalog.GetActiveAsync(token));
        // 模型行本身没被改动："停用"是供应商层面的状态，不是把模型也改了
        Assert.True((await _catalog.GetModelAsync(reasoner, token))!.IsActive);

        // 供应商停用期间也不能把它设为当前
        Assert.False(await _catalog.ActivateModelAsync(reasoner, "eosdev-test", token));

        // 取消当前后就没有"当前模型"了（助手回到未配置）
        await _catalog.ClearActiveModelAsync(token);
        Assert.Null(await _catalog.GetActiveAsync(token));
    }

    /// <summary>批量添加（界面上"从预设勾选几个模型一次添加"）：一次事务，要么都进去要么都不进。</summary>
    [Fact]
    public async Task Batch_Create_Models_Inserts_All_Of_Them()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;

        var providerId = await NewProviderAsync(token);
        var created = await _catalog.CreateModelsAsync(
        [
            TestWrite(providerId, "batch-a"),
            TestWrite(providerId, "batch-b"),
            TestWrite(providerId, "batch-c"),
        ], "eosdev-test", token);
        Assert.Equal(3, created);

        var models = await _catalog.ListModelsAsync(token);
        var mine = models.Where(item => item.ProviderId == providerId).ToList();
        Assert.Equal(3, mine.Count);
        foreach (var model in mine)
        {
            _modelIds.Add(model.ModelId);
        }

        // 同一供应商下重复的模型标识要被唯一约束挡住（不能靠界面自觉）
        await Assert.ThrowsAsync<SqlException>(() => _catalog.CreateModelAsync(
            TestWrite(providerId, "batch-a"), "eosdev-test", token));
    }

    private static AssistantModelWrite TestWrite(int providerId, string modelCode) => new(
        ProviderId: providerId,
        ModelCode: modelCode,
        DisplayName: modelCode,
        ContextWindow: 32768,
        MaxOutputTokens: 2048,
        DefaultTemperature: null,
        TimeoutSeconds: null,
        InputPerMillionYuan: null,
        OutputPerMillionYuan: null,
        SupportsTools: true,
        Enabled: true,
        SortIdx: 0,
        Remark: null);

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

        // 顺序不能换：先取消"当前"，再删模型，最后删供应商——反过来会被外键挡住。
        // 而且**按供应商删模型**，不是只删被跟踪到的 ID：用例在"记下 ID"之前就断言失败时
        // （比如"批量插了 3 条"那条），已经插进去的行根本没被跟踪到，只删 ID 会留下孤儿行。
        // 我第一版就是这样留下的脏数据，它后来还让 292 的干跑门禁误报了一次。
        foreach (var id in _providerIds)
        {
            using (var reset = new SqlCommand(
                "UPDATE dbo.ASSISTANT_MODEL SET IS_ACTIVE = 0 WHERE PROVIDER_ID = @Id;", conn))
            {
                reset.Parameters.AddWithValue("@Id", id);
                reset.ExecuteNonQuery();
            }

            using var deleteModels = new SqlCommand(
                "DELETE FROM dbo.ASSISTANT_MODEL WHERE PROVIDER_ID = @Id;", conn);
            deleteModels.Parameters.AddWithValue("@Id", id);
            deleteModels.ExecuteNonQuery();
        }

        // 兜底：万一有模型不在任何被跟踪的供应商下（理论上不会有），也别留下来
        foreach (var id in _modelIds)
        {
            using var del = new SqlCommand("DELETE FROM dbo.ASSISTANT_MODEL WHERE MODEL_ID = @Id;", conn);
            del.Parameters.AddWithValue("@Id", id);
            del.ExecuteNonQuery();
        }

        foreach (var id in _providerIds)
        {
            using var cmd = new SqlCommand(
                "DELETE FROM dbo.ASSISTANT_PROVIDER WHERE PROVIDER_ID = @Id AND NOT EXISTS (SELECT 1 FROM dbo.ASSISTANT_MODEL WHERE PROVIDER_ID = @Id);", conn);
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
