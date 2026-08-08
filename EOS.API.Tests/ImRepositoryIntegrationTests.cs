using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// EOS.IM 仓储集成测试：直连开发库验证真实 SQL（事务、Seq 原子分配、送达标记、
/// 撤回、搜索、清理）。连接串来自 env EOS_IM_TEST_CONNECTION 或本机 Codex 配置；
/// 拿不到连接串时测试空跑跳过。测试数据在 Dispose 中清理。
/// </summary>
[Trait("Category", "Integration")]
public sealed class ImRepositoryIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly ImConversationRepository _conversations;
    private readonly ImMessageRepository _messages;
    private readonly ImAttachmentRepository _attachments;
    private readonly List<long> _createdConversations = [];
    private readonly string _base = $"it_{Guid.NewGuid():N}"[..16];

    public ImRepositoryIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ImDatabase"] = ConnectionString.Value,
            })
            .Build();
        var factory = new DbConnectionFactory(config);
        _conversations = new ImConversationRepository(factory);
        _messages = new ImMessageRepository(factory);
        _attachments = new ImAttachmentRepository(factory);
    }

    [Fact]
    public async Task CreateDirect_SendText_AllocatesSeqAndHistoryWorks()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var (a, b) = (User("a"), User("b"));
        var conversationId = await _conversations.GetOrCreateDirectAsync(a, b, CancellationToken.None);
        Assert.NotNull(conversationId);
        Track(conversationId!.Value);

        var message = await _messages.SendAsync(
            conversationId.Value, a, Guid.NewGuid(), ImMessageType.Text,
            """{"text":"你好，测试"}""", "你好，测试", CancellationToken.None);
        Assert.NotNull(message);
        Assert.Equal(1, message!.Seq);

        var after = await _messages.GetMessagesAfterAsync(conversationId.Value, 0, 50, CancellationToken.None);
        Assert.Single(after);
        Assert.Equal(a, after[0].SenderUserId);
        Assert.Contains("你好", after[0].Content);

        var before = await _messages.GetMessagesBeforeAsync(conversationId.Value, 10, 50, CancellationToken.None);
        Assert.Single(before);
        Assert.Equal(1, before[0].Seq);
    }

    [Fact]
    public async Task DuplicateClientMessageId_ReturnsExistingMessage()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var (a, b) = (User("a"), User("b"));
        var conversationId = (await _conversations.GetOrCreateDirectAsync(a, b, CancellationToken.None))!.Value;
        Track(conversationId);
        var clientId = Guid.NewGuid();

        var first = await _messages.SendAsync(
            conversationId, a, clientId, ImMessageType.Text, """{"text":"幂等"}""", "幂等", CancellationToken.None);
        var second = await _messages.SendAsync(
            conversationId, a, clientId, ImMessageType.Text, """{"text":"幂等"}""", "幂等", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(first.Seq, second.Seq);
        var all = await _messages.GetMessagesAfterAsync(conversationId, 0, 50, CancellationToken.None);
        Assert.Single(all);
    }

    [Fact]
    public async Task AckReceived_MarksDeliveredOnlyWhenAllMembersAck()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var (a, b) = (User("a"), User("b"));
        var conversationId = (await _conversations.GetOrCreateDirectAsync(a, b, CancellationToken.None))!.Value;
        Track(conversationId);
        var message = await _messages.SendAsync(
            conversationId, a, Guid.NewGuid(), ImMessageType.Text, """{"text":"送达"}""", "送达", CancellationToken.None);

        await _messages.AckReceivedAsync(conversationId, a, message!.Seq, CancellationToken.None);
        Assert.Null(await GetDeliveredAtAsync(conversationId, message.Id));

        await _messages.AckReceivedAsync(conversationId, b, message.Seq, CancellationToken.None);
        Assert.NotNull(await GetDeliveredAtAsync(conversationId, message.Id));
    }

    [Fact]
    public async Task AckRead_DrivesUnreadCount()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var (a, b) = (User("a"), User("b"));
        var conversationId = (await _conversations.GetOrCreateDirectAsync(a, b, CancellationToken.None))!.Value;
        Track(conversationId);
        for (var i = 0; i < 3; i++)
        {
            await _messages.SendAsync(
                conversationId, a, Guid.NewGuid(), ImMessageType.Text,
                $$"""{"text":"消息{{i}}"}""", $"消息{i}", CancellationToken.None);
        }

        var list = await _conversations.GetMyConversationsAsync(a, CancellationToken.None);
        var item = Assert.Single(list);
        Assert.Equal(3, item.UnreadCount);

        await _messages.AckReadAsync(conversationId, a, 3, CancellationToken.None);
        list = await _conversations.GetMyConversationsAsync(a, CancellationToken.None);
        Assert.Equal(0, Assert.Single(list).UnreadCount);
    }

    [Fact]
    public async Task Recall_OnlySenderWithinWindow()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var (a, b) = (User("a"), User("b"));
        var conversationId = (await _conversations.GetOrCreateDirectAsync(a, b, CancellationToken.None))!.Value;
        Track(conversationId);
        var message = await _messages.SendAsync(
            conversationId, a, Guid.NewGuid(), ImMessageType.Text, """{"text":"撤回我"}""", "撤回我", CancellationToken.None);

        var asB = await _messages.RecallAsync(conversationId, message!.Id, b, CancellationToken.None);
        Assert.Equal("NOT_SENDER", asB.ErrorCode);

        var asA = await _messages.RecallAsync(conversationId, message.Id, a, CancellationToken.None);
        Assert.True(asA.Success);

        var again = await _messages.RecallAsync(conversationId, message.Id, a, CancellationToken.None);
        Assert.Equal("ALREADY_RECALLED", again.ErrorCode);
    }

    [Fact]
    public async Task CreateGroup_AddRemoveLeave_UpdatesMembers()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var (owner, b, c) = (User("a"), User("b"), User("c"));
        var result = await _conversations.CreateGroupAsync(owner, "测试群", [b], CancellationToken.None);
        Assert.True(result.Success);
        var conversationId = result.ConversationId!.Value;
        Track(conversationId);

        var initialMembers = await _conversations.GetActiveMemberIdsAsync(conversationId, CancellationToken.None);
        Assert.True(initialMembers.Count == 2);
        await _conversations.AddMembersAsync(conversationId, owner, [c], CancellationToken.None);
        var withC = await _conversations.GetActiveMemberIdsAsync(conversationId, CancellationToken.None);
        Assert.True(withC.Count == 3);

        await _conversations.RemoveMemberAsync(conversationId, owner, b, CancellationToken.None);
        var afterRemove = await _conversations.GetActiveMemberIdsAsync(conversationId, CancellationToken.None);
        Assert.True(afterRemove.Count == 2);

        await _conversations.LeaveAsync(conversationId, owner, CancellationToken.None);
        var afterLeave = await _conversations.GetActiveMemberIdsAsync(conversationId, CancellationToken.None);
        Assert.Single(afterLeave);
    }

    [Fact]
    public async Task Search_OnlyReturnsMessagesOfMemberConversations()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var (a, b, stranger) = (User("a"), User("b"), User("x"));
        var conversationId = (await _conversations.GetOrCreateDirectAsync(a, b, CancellationToken.None))!.Value;
        Track(conversationId);
        await _messages.SendAsync(
            conversationId, a, Guid.NewGuid(), ImMessageType.Text,
            """{"text":"特殊关键字IM测试"}""", "特殊关键字IM测试", CancellationToken.None);

        var forMember = await _messages.SearchAsync(b, "特殊关键字", null, 50, CancellationToken.None);
        Assert.Single(forMember);

        var forStranger = await _messages.SearchAsync(stranger, "特殊关键字", null, 50, CancellationToken.None);
        Assert.Empty(forStranger);
    }

    [Fact]
    public async Task SearchUsers_ReturnsActiveUsersFromLegacyDirectory()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var result = await _conversations.SearchUsersAsync(string.Empty, 5, CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.All(result, contact => Assert.False(string.IsNullOrWhiteSpace(contact.UserId)));
    }

    [Fact]
    public async Task CleanupExpired_RemovesPastRetentionMessages()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var (a, b) = (User("a"), User("b"));
        var conversationId = (await _conversations.GetOrCreateDirectAsync(a, b, CancellationToken.None))!.Value;
        Track(conversationId);
        var message = await _messages.SendAsync(
            conversationId, a, Guid.NewGuid(), ImMessageType.Text, """{"text":"过期"}""", "过期", CancellationToken.None);
        Assert.NotNull(message);

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.im_messages
            SET ExpiresAt = DATEADD(DAY, -1, SYSUTCDATETIME())
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", message!.Id);
        await command.ExecuteNonQueryAsync();

        var removed = await _messages.CleanupExpiredAsync(CancellationToken.None);
        Assert.True(removed >= 1);
        var after = await _messages.GetMessagesAfterAsync(conversationId, 0, 50, CancellationToken.None);
        Assert.Empty(after);
    }

    [Fact]
    public async Task Attachment_UploadSendDownload_Works()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var (a, b) = (User("a"), User("b"));
        var conversationId = (await _conversations.GetOrCreateDirectAsync(a, b, CancellationToken.None))!.Value;
        Track(conversationId);
        var bytes = "测试文件内容"u8.ToArray();
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var meta = await _attachments.UploadAsync(
            conversationId, a, "报告.pdf", "application/pdf", bytes, sha256, CancellationToken.None);
        Assert.NotNull(meta);
        Assert.Equal(bytes.LongLength, meta!.SizeBytes);

        var content = System.Text.Json.JsonSerializer.Serialize(new
        {
            fileId = meta.Id,
            fileName = meta.FileName,
            sizeBytes = meta.SizeBytes,
            contentType = meta.ContentType,
        });
        var message = await _messages.SendAsync(
            conversationId, a, Guid.NewGuid(), ImMessageType.File, content, "[文件]", CancellationToken.None);
        Assert.NotNull(message);
        await _attachments.LinkToMessageAsync(meta.Id, message!.Id, CancellationToken.None);

        var file = await _attachments.GetFileAsync(meta.Id, CancellationToken.None);
        Assert.NotNull(file);
        Assert.Equal(bytes, file!.Content);
        Assert.Equal("报告.pdf", file.Meta.FileName);
    }

    public void Dispose()
    {
        if (ConnectionString.Value is null || _createdConversations.Count == 0)
        {
            return;
        }

        try
        {
            using var connection = new SqlConnection(ConnectionString.Value);
            connection.Open();
            using var command = connection.CreateCommand();
            var ids = string.Join(",", _createdConversations);
            command.CommandText = $"""
                DELETE FROM dbo.im_audit_log WHERE ConversationId IN ({ids});
                DELETE FROM dbo.im_conversations WHERE Id IN ({ids});
                """;
            command.ExecuteNonQuery();
        }
        catch
        {
            // 清理失败不影响测试结论；数据残留仅限开发库
        }
    }

    private string User(string tag) => $"{_base}{tag}";

    private void Track(long conversationId) => _createdConversations.Add(conversationId);

    private async Task<DateTime?> GetDeliveredAtAsync(long conversationId, long messageId)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DeliveredAt FROM dbo.im_messages WHERE Id = @Id AND ConversationId = @ConversationId;";
        command.Parameters.AddWithValue("@Id", messageId);
        command.Parameters.AddWithValue("@ConversationId", conversationId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToDateTime(value);
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_IM_TEST_CONNECTION");
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
                ? match.Groups[1].Value.Replace("Database=Hiswitek", "Database=EOS.IM")
                : null;
        }
        catch
        {
            return null;
        }
    }
}
