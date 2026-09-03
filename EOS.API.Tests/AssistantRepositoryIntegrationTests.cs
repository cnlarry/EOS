using EOS.API.Data;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 工作助手会话/消息仓储集成测试：直连 EOS.ERP 验证真实 SQL。
/// 连接串来源与跳过策略同 AttachmentRepositoryIntegrationTests；
/// 测试前置用与生产一致的 DbUp 迁移创建/升级 ASSISTANT_SESSION/ASSISTANT_MESSAGE；
/// 测试数据在 Dispose 中按会话 ID 清理。
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantRepositoryIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly AssistantRepository _repository;
    private readonly List<long> _sessionIds = [];

    public AssistantRepositoryIntegrationTests()
    {
        EnsureSchema();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        _repository = new AssistantRepository(new DbConnectionFactory(config));
    }

    private static string? ResolveConnectionString()
        => Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");

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
            throw new InvalidOperationException("测试前置：EOS.ERP 助手表迁移失败", result.Error);
        }
    }

    public void Dispose()
    {
        if (ConnectionString.Value is null || _sessionIds.Count == 0)
        {
            return;
        }

        try
        {
            using var connection = new SqlConnection(ConnectionString.Value);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"DELETE FROM dbo.ASSISTANT_MESSAGE WHERE SESSION_ID IN ({Ids()});"
                + $"DELETE FROM dbo.ASSISTANT_SESSION WHERE ID IN ({Ids()});";
            command.ExecuteNonQuery();
        }
        catch
        {
            // 清理失败不影响测试结论；数据残留仅限开发库
        }

        string Ids() => string.Join(',', _sessionIds);
    }

    private async Task<Data.AssistantSessionDto> NewSessionAsync(string userId)
    {
        var session = await _repository.CreateSessionAsync(userId, CancellationToken.None);
        _sessionIds.Add(session.Id);
        return session;
    }

    [Fact]
    public async Task Session_Crud_And_Isolation_Works()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;

        var mine = await NewSessionAsync("eosdev-assistant-test-a");
        Assert.Equal("新对话", mine.Title);

        // 他人视角不可见
        var otherView = await _repository.GetSessionAsync("eosdev-assistant-test-b", mine.Id, token);
        Assert.Null(otherView);

        // 列表只含本人
        var listA = await _repository.ListSessionsAsync("eosdev-assistant-test-a", 50, token);
        Assert.Contains(listA, s => s.Id == mine.Id);
        var listB = await _repository.ListSessionsAsync("eosdev-assistant-test-b", 50, token);
        Assert.DoesNotContain(listB, s => s.Id == mine.Id);

        // 删除后不可见
        var deleted = await _repository.DeleteSessionAsync("eosdev-assistant-test-a", mine.Id, token);
        Assert.Equal(1, deleted);
        _sessionIds.Remove(mine.Id);
        Assert.Null(await _repository.GetSessionAsync("eosdev-assistant-test-a", mine.Id, token));
    }

    [Fact]
    public async Task Messages_Title_History_And_Usage_Persist()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        const string user = "eosdev-assistant-test-a";

        var session = await NewSessionAsync(user);

        // 首条用户消息成为标题
        var first = await _repository.AddUserMessageAsync(user, session.Id, "第一轮提问：库存怎么查？", "corr-1", token);
        Assert.Equal(1, first.Role);
        var titled = await _repository.GetSessionAsync(user, session.Id, token);
        Assert.StartsWith("第一轮提问", titled!.Title);

        // 助手回复带用量
        var reply = await _repository.AddAssistantMessageAsync(
            user, session.Id, "在库存管理菜单查询。", "deepseek-chat",
            promptTokens: 120, completionTokens: 30, elapsedMs: 800, correlationId: "corr-1", token);
        Assert.Equal(2, reply.Role);
        Assert.Equal("deepseek-chat", reply.ModelName);
        Assert.Equal(120, reply.PromptTokens);

        // 历史正序返回
        var history = await _repository.LoadRecentHistoryAsync(user, session.Id, 40, token);
        Assert.Equal([(1, "第一轮提问：库存怎么查？"), (2, "在库存管理菜单查询。")], history);

        // 消息列表含用量字段；越权读取为空
        var messages = await _repository.ListMessagesAsync(user, session.Id, token);
        Assert.Equal(2, messages.Count);
        Assert.Empty(await _repository.ListMessagesAsync("eosdev-assistant-test-b", session.Id, token));

        // 越权写入被拒（WHERE 归属条件无匹配行）
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _repository.AddUserMessageAsync("eosdev-assistant-test-b", session.Id, "越权", "x", token));
    }

    [Fact]
    public async Task Delete_Removes_Messages_Too()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        const string user = "eosdev-assistant-test-a";

        var session = await NewSessionAsync(user);
        await _repository.AddUserMessageAsync(user, session.Id, "待删除消息", "corr-d", token);
        await _repository.AddAssistantMessageAsync(user, session.Id, "回复", "m", null, null, null, "corr-d", token);

        await _repository.DeleteSessionAsync(user, session.Id, token);
        _sessionIds.Remove(session.Id);

        var conn = new SqlConnection(ConnectionString.Value);
        await using (conn.ConfigureAwait(false))
        {
            await conn.OpenAsync(token);
            await using var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.ASSISTANT_MESSAGE WHERE SESSION_ID = @Id;", conn);
            cmd.Parameters.AddWithValue("@Id", session.Id);
            Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync(token)));
        }
    }
}
