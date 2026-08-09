using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// EOS.Mail 邮件任务仓储集成测试：直连开发库验证真实 SQL（创建/查询/状态流转/更新/用户隔离）。
/// 连接串来自 env EOS_MAIL_TEST_CONNECTION 或本机 Codex 配置（替换为 Database=EOS.Mail）；
/// 拿不到连接串时测试空跑跳过。测试数据在 Dispose 中清理。
/// </summary>
[Trait("Category", "Integration")]
public sealed class MailTaskRepositoryIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly MailTaskRepository _tasks;
    private readonly List<long> _createdTaskIds = [];
    private readonly string _userA = $"mail_it_a_{Guid.NewGuid():N}"[..32];
    private readonly string _userB = $"mail_it_b_{Guid.NewGuid():N}"[..32];

    public MailTaskRepositoryIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MailDatabase"] = ConnectionString.Value,
            })
            .Build();
        _tasks = new MailTaskRepository(new DbConnectionFactory(config));
    }

    [Fact]
    public async Task Create_List_Get_Works()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var created = await _tasks.CreateAsync(_userA, Sample("确认付款计划"), CancellationToken.None);
        Track(created.Id);
        Assert.Equal("open", created.Status);
        Assert.Equal("email", created.SourceKind);
        Assert.Equal("确认付款计划", created.Title);
        Assert.NotEqual(default, created.CreatedAt);

        var list = await _tasks.ListMineAsync(_userA, "open", 50, 0, CancellationToken.None);
        Assert.Contains(list.Tasks, t => t.Id == created.Id);

        var single = await _tasks.GetMineAsync(_userA, created.Id, CancellationToken.None);
        Assert.NotNull(single);
        Assert.Equal(created.Id, single!.Id);
    }

    [Fact]
    public async Task UserIsolation_OtherUserCannotSeeOrMutate()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var created = await _tasks.CreateAsync(_userA, Sample("仅属于 A"), CancellationToken.None);
        Track(created.Id);

        var listB = await _tasks.ListMineAsync(_userB, null, 50, 0, CancellationToken.None);
        Assert.DoesNotContain(listB.Tasks, t => t.Id == created.Id);
        Assert.Null(await _tasks.GetMineAsync(_userB, created.Id, CancellationToken.None));
        Assert.Null(await _tasks.CompleteAsync(_userB, created.Id, CancellationToken.None));

        var stillOpen = await _tasks.GetMineAsync(_userA, created.Id, CancellationToken.None);
        Assert.Equal("open", stillOpen!.Status);
    }

    [Fact]
    public async Task Complete_Reopen_Cancel_LifecycleWorks()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var created = await _tasks.CreateAsync(_userA, Sample("生命周期"), CancellationToken.None);
        Track(created.Id);

        var done = await _tasks.CompleteAsync(_userA, created.Id, CancellationToken.None);
        Assert.Equal("done", done!.Status);
        Assert.NotNull(done.CompletedAt);

        var reopen = await _tasks.ReopenAsync(_userA, created.Id, CancellationToken.None);
        Assert.Equal("open", reopen!.Status);
        Assert.Null(reopen.CompletedAt);

        var cancelled = await _tasks.CancelAsync(_userA, created.Id, CancellationToken.None);
        Assert.Equal("cancelled", cancelled!.Status);

        // 已取消任务不能再次取消
        Assert.Null(await _tasks.CancelAsync(_userA, created.Id, CancellationToken.None));
        // 未完成任务不能重开
        Assert.Null(await _tasks.ReopenAsync(_userA, created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Update_FieldsAndClearDueDateWork()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var created = await _tasks.CreateAsync(_userA, Sample("原标题"), CancellationToken.None);
        Track(created.Id);
        var due = DateTime.UtcNow.AddDays(3);

        var updated = await _tasks.UpdateAsync(_userA, created.Id, new MailTaskUpdateRequest
        {
            Title = "新标题",
            Description = "新描述",
            DueDate = due,
            Priority = 1,
        }, CancellationToken.None);

        Assert.Equal("新标题", updated!.Title);
        Assert.Equal("新描述", updated.Description);
        Assert.Equal(1, updated.Priority);
        Assert.NotNull(updated.DueDate);

        var cleared = await _tasks.UpdateAsync(_userA, created.Id, new MailTaskUpdateRequest
        {
            ClearDueDate = true,
        }, CancellationToken.None);
        Assert.Null(cleared!.DueDate);
    }

    [Fact]
    public async Task List_FiltersByStatusAndPaginates()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        for (var i = 0; i < 3; i++)
        {
            Track((await _tasks.CreateAsync(_userA, Sample($"批量 {i}"), CancellationToken.None)).Id);
        }

        var all = await _tasks.ListMineAsync(_userA, null, 50, 0, CancellationToken.None);
        Assert.True(all.Total >= 3);

        var page = await _tasks.ListMineAsync(_userA, null, 2, 0, CancellationToken.None);
        Assert.Equal(2, page.Tasks.Count);
        Assert.Equal(all.Total, page.Total);
    }

    public void Dispose()
    {
        if (ConnectionString.Value is null || _createdTaskIds.Count == 0)
        {
            return;
        }

        try
        {
            using var connection = new SqlConnection(ConnectionString.Value);
            connection.Open();
            using var command = connection.CreateCommand();
            var ids = string.Join(",", _createdTaskIds);
            command.CommandText = $"DELETE FROM dbo.mail_tasks WHERE Id IN ({ids});";
            command.ExecuteNonQuery();
        }
        catch
        {
            // 清理失败不影响测试结论；数据残留仅限开发库
        }
    }

    private static MailTaskCreateRequest Sample(string title) => new()
    {
        Title = title,
        Description = "由邮件提取的待办：请在本周内完成确认。",
        SourceMailbox = "lina@eos-corp.com",
        SourceMessageId = $"<{Guid.NewGuid():N}@eos-corp.com>",
        SourceSubject = $"关于{title}",
        Priority = 2,
    };

    private void Track(long taskId) => _createdTaskIds.Add(taskId);

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_MAIL_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "MSSQL_CONNECTION_STRING\\s*=\\s*\"([^\"]+)\"");
            return match.Success
                ? match.Groups[1].Value.Replace("Database=Hiswitek", "Database=EOS.Mail")
                : null;
        }
        catch
        {
            return null;
        }
    }
}
