using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 管理侧会话仓储集成测试（菜单组 31 / 模块 3101，见 ADR-030）：直连 EOS.ERP 验证真实 SQL。
///
/// <para>
/// 重点验两件事：**跨用户可见**（这正是管理侧与个人侧的分野，也要顺带确认个人侧的隔离契约
/// 没被这次新增破坏），以及**删除仍然只对已归档开放**。
/// </para>
///
/// <para>
/// 连接串与跳过策略、测试前置迁移均沿用 <see cref="AssistantRepositoryIntegrationTests"/>——
/// 注意它跑的是与生产同一套 DbUp 迁移，所以迁移 288（根组 31 与模块 3101）也会在这里被执行到。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantAdminRepositoryIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly AssistantRepository _personal;
    private readonly AssistantAdminRepository _admin;
    private readonly List<long> _sessionIds = [];

    public AssistantAdminRepositoryIntegrationTests()
    {
        EnsureSchema();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        var connections = new DbConnectionFactory(config);
        _personal = new AssistantRepository(connections);
        _admin = new AssistantAdminRepository(connections);
    }

    private static string? ResolveConnectionString()
        => Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");

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

    private async Task<AssistantSessionDto> NewSessionAsync(string user, CancellationToken token)
    {
        var session = await _personal.CreateSessionAsync(user, token);
        _sessionIds.Add(session.Id);
        return session;
    }

    /// <summary>管理侧的默认视图（在列）取一页，默认排序。</summary>
    private Task<(IReadOnlyList<AssistantSessionDto> Items, int Total)> ListActiveAsync(string? owner, CancellationToken token)
        => _admin.ListSessionsAsync(
            0, 50, AssistantSessionListState.Active, null, owner, AssistantSessionSort.LastActive, false, token);

    /// <summary>
    /// 管理侧能同时看到不同用户的会话；按归属用户筛能收窄；**个人侧的隔离契约不受影响**。
    /// </summary>
    [Fact]
    public async Task Admin_Sees_Other_Users_Sessions_While_Personal_Stays_Isolated()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        const string userA = "eosdev-assistant-test-a";
        const string userB = "eosdev-assistant-test-b";

        var mine = await NewSessionAsync(userA, token);
        var other = await NewSessionAsync(userB, token);

        // 管理侧：两个用户的会话都在一条列表里
        var all = await ListActiveAsync(null, token);
        Assert.Contains(all.Items, s => s.Id == mine.Id);
        Assert.Contains(all.Items, s => s.Id == other.Id);

        // 按归属用户筛：只看得到那一个
        var onlyA = await ListActiveAsync(userA, token);
        Assert.Contains(onlyA.Items, s => s.Id == mine.Id);
        Assert.DoesNotContain(onlyA.Items, s => s.Id == other.Id);

        // 个人侧对照：userB 的会话在 userA 的列表里**依然看不到**（新增管理侧没有削弱个人侧契约）
        var personalA = await _personal.ListSessionsAsync(
            userA, 0, 50, AssistantSessionListState.Active, null, token);
        Assert.DoesNotContain(personalA.Items, s => s.Id == other.Id);
    }

    /// <summary>管理侧的删除同样只对已归档开放：在列的会话先被拒，归档之后才删得掉。</summary>
    [Fact]
    public async Task Admin_Delete_Still_Requires_Archived()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        const string user = "eosdev-assistant-test-a";

        var session = await NewSessionAsync(user, token);

        Assert.Equal(0, await _admin.DeleteSessionAsync(session.Id, token));
        Assert.Equal(1, await _admin.ArchiveSessionAsync(session.Id, true, token));
        Assert.Equal(1, await _admin.DeleteSessionAsync(session.Id, token));
        _sessionIds.Remove(session.Id);
    }

    /// <summary>列表带出消息数与三态视图（消息数与个人侧同源，都是列表查询里现算的）。</summary>
    [Fact]
    public async Task Admin_List_Reports_MessageCount_And_Respects_State()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        const string user = "eosdev-assistant-test-a";

        var session = await NewSessionAsync(user, token);
        await _personal.AddUserMessageAsync(user, session.Id, "甲：采购单主表是哪张？", "c-admin", token);

        var active = await ListActiveAsync(null, token);
        var mine = active.Items.Single(s => s.Id == session.Id);
        Assert.Equal(1, mine.MessageCount);
        Assert.Equal(user, mine.UserId);

        // 归档后：默认视图看不到它，已归档视图看得到，且该视图里每一行都是已归档
        Assert.Equal(1, await _admin.ArchiveSessionAsync(session.Id, true, token));
        Assert.DoesNotContain((await ListActiveAsync(null, token)).Items, s => s.Id == session.Id);
        var archived = await _admin.ListSessionsAsync(
            0, 50, AssistantSessionListState.Archived, null, null, AssistantSessionSort.LastActive, false, token);
        Assert.Contains(archived.Items, s => s.Id == session.Id);
        Assert.All(archived.Items, s => Assert.NotNull(s.ArchivedAt));

        // 关键词只搜标题
        Assert.Contains(
            (await _admin.ListSessionsAsync(
                0, 50, AssistantSessionListState.All, "甲：采购单", null,
                AssistantSessionSort.LastActive, false, token)).Items,
            s => s.Id == session.Id);
    }

    /// <summary>
    /// 排序在**服务端**做（列表是服务端分页的，在前端排只会排当前这一页）：
    /// 按消息数降序时，消息多的会话排在前面。
    /// </summary>
    [Fact]
    public async Task Admin_List_Sorts_On_Server()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;
        const string user = "eosdev-assistant-test-a";

        var few = await NewSessionAsync(user, token);
        var many = await NewSessionAsync(user, token);
        await _personal.AddUserMessageAsync(user, many.Id, "甲", "c-sort", token);
        await _personal.AddUserMessageAsync(user, many.Id, "乙", "c-sort", token);

        var byMessages = await _admin.ListSessionsAsync(
            0, 200, AssistantSessionListState.Active, null, user,
            AssistantSessionSort.Messages, false, token);
        var ids = byMessages.Items.Select(s => s.Id).ToList();

        var manyIndex = ids.IndexOf(many.Id);
        var fewIndex = ids.IndexOf(few.Id);
        Assert.True(manyIndex >= 0 && fewIndex >= 0, "两个测试会话都应在列表里。");
        Assert.True(manyIndex < fewIndex, "按消息数降序时，消息多的会话应排在消息少的前面。");
    }

    /// <summary>归属用户下拉：至少包含上面建过会话的两个用户。</summary>
    [Fact]
    public async Task Admin_Lists_Owners()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;

        await NewSessionAsync("eosdev-assistant-test-a", token);
        await NewSessionAsync("eosdev-assistant-test-b", token);

        var owners = await _admin.ListOwnersAsync(token);
        Assert.Contains("eosdev-assistant-test-a", owners);
        Assert.Contains("eosdev-assistant-test-b", owners);
    }

    public void Dispose()
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null || _sessionIds.Count == 0) return;
        using var conn = new SqlConnection(connectionString);
        conn.Open();
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
