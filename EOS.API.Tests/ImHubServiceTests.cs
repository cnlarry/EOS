using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

public sealed class ImHubServiceTests
{
    [Fact]
    public async Task SendText_EmptyMessage_Rejected()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.SendTextAsync("u1", 1, Guid.NewGuid(), "   ", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("EMPTY_MESSAGE", result.ErrorCode);
    }

    [Fact]
    public async Task SendText_TooLong_Rejected()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.SendTextAsync("u1", 1, Guid.NewGuid(), new string('a', 4001), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("MESSAGE_TOO_LONG", result.ErrorCode);
    }

    [Fact]
    public async Task SendText_NonMember_Forbidden()
    {
        var (service, conversations, _, _, _) = CreateService();
        conversations.Members.Add(("c1", "u1"));

        var result = await service.SendTextAsync("stranger", 1, Guid.NewGuid(), "hi", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task SendText_Success_PersistsJsonAndPreview()
    {
        var (service, conversations, messages, _, _) = CreateService();
        conversations.Members.Add(("c1", "u1"));
        var clientId = Guid.NewGuid();

        var result = await service.SendTextAsync("u1", 1, clientId, "  你好  ", CancellationToken.None);

        Assert.True(result.Success);
        var (convId, sender, cid, type, content, preview) = messages.Sent.Single();
        Assert.Equal(1, convId);
        Assert.Equal("u1", sender);
        Assert.Equal(clientId, cid);
        Assert.Equal(ImMessageType.Text, type);
        Assert.Equal("你好", JsonDocument.Parse(content).RootElement.GetProperty("text").GetString());
        Assert.Equal("你好", preview);
    }

    [Fact]
    public async Task SendText_RateLimited_AfterLimit()
    {
        var (service, conversations, _, _, _) = CreateService(maxPerMinute: 1);
        conversations.Members.Add(("c1", "u1"));
        await service.SendTextAsync("u1", 1, Guid.NewGuid(), "one", CancellationToken.None);

        var result = await service.SendTextAsync("u1", 1, Guid.NewGuid(), "two", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("RATE_LIMITED", result.ErrorCode);
    }

    [Fact]
    public async Task SendCard_UnsupportedType_Rejected()
    {
        var (service, conversations, _, _, _) = CreateService();
        conversations.Members.Add(("c1", "u1"));

        var result = await service.SendCardAsync("u1", 1, Guid.NewGuid(), "unknown-card", "X", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CARD_TYPE_UNSUPPORTED", result.ErrorCode);
    }

    [Fact]
    public async Task SendCard_EntityUnavailable_Rejected()
    {
        var (service, conversations, _, _, cards) = CreateService();
        conversations.Members.Add(("c1", "u1"));
        cards.Resolvable = false;

        var result = await service.SendCardAsync("u1", 1, Guid.NewGuid(), "purchase-order", "CGD-NO", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CARD_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public async Task SendCard_Success_StoresServerSnapshot()
    {
        var (service, conversations, messages, _, cards) = CreateService();
        conversations.Members.Add(("c1", "u1"));
        cards.SnapshotJson = """{"cardType":"purchase-order","entityId":"CGD-1","title":"采购单 CGD-1","fields":[],"actions":[]}""";

        var result = await service.SendCardAsync("u1", 1, Guid.NewGuid(), "purchase-order", "CGD-1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(ImMessageType.Card, messages.Sent.Single().Type);
        Assert.StartsWith("[卡片]：purchase-order", messages.Sent.Single().Preview);
    }

    [Fact]
    public async Task Recall_NonMember_Forbidden()
    {
        var (service, conversations, _, _, _) = CreateService();
        conversations.Members.Add(("c1", "u1"));

        var result = await service.RecallAsync("stranger", 1, 99, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task Recall_Member_DelegatesToRepository()
    {
        var (service, conversations, messages, _, _) = CreateService();
        conversations.Members.Add(("c1", "u1"));
        messages.RecallResult = new ImRecallResult(false, "TOO_LATE", "已超时", 99);

        var result = await service.RecallAsync("u1", 1, 99, CancellationToken.None);

        Assert.Equal("TOO_LATE", result.ErrorCode);
        Assert.Equal(99, messages.RecalledMessageId);
    }

    [Fact]
    public async Task Ack_NonMember_Ignored()
    {
        var (service, _, messages, _, _) = CreateService();

        await service.AckReceivedAsync("stranger", 1, 5, CancellationToken.None);
        await service.AckReadAsync("stranger", 1, 5, CancellationToken.None);

        Assert.Empty(messages.ReceivedAcks);
        Assert.Empty(messages.ReadAcks);
    }

    [Fact]
    public async Task SendFile_NonMember_Forbidden()
    {
        var (service, conversations, _, _, _) = CreateService();
        conversations.Members.Add(("c1", "u1"));

        var result = await service.SendFileAsync("stranger", 1, Guid.NewGuid(), 1, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task SendFile_AttachmentNotFound_Rejected()
    {
        var (service, conversations, _, attachments, _) = CreateService();
        conversations.Members.Add(("c1", "u1"));
        attachments.FileId = null;

        var result = await service.SendFileAsync("u1", 1, Guid.NewGuid(), 999, CancellationToken.None);

        Assert.Equal("FILE_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task SendFile_NotUploader_Forbidden()
    {
        var (service, conversations, _, attachments, _) = CreateService();
        conversations.Members.Add(("c1", "u1"));
        attachments.UploadedBy = "other";

        var result = await service.SendFileAsync("u1", 1, Guid.NewGuid(), 1, CancellationToken.None);

        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task SendFile_Success_LinksAttachment()
    {
        var (service, conversations, messages, attachments, _) = CreateService();
        conversations.Members.Add(("c1", "u1"));

        var result = await service.SendFileAsync("u1", 1, Guid.NewGuid(), 1, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(ImMessageType.File, messages.Sent.Single().Type);
        Assert.Equal(1, attachments.LinkedMessageId);
    }

    private static (ImHubService Service, FakeConversationRepository Conversations, FakeMessageRepository Messages, FakeAttachmentRepository Attachments, FakeCardService Cards)
        CreateService(int maxPerMinute = 60)
    {
        var conversations = new FakeConversationRepository();
        var messages = new FakeMessageRepository();
        var attachments = new FakeAttachmentRepository();
        var cards = new FakeCardService();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Im:RateLimit:MaxMessagesPerMinute"] = maxPerMinute.ToString(),
            })
            .Build();
        var limiter = new ImRateLimiter(config);
        var service = new ImHubService(conversations, messages, attachments, cards, limiter);
        return (service, conversations, messages, attachments, cards);
    }
}

internal sealed class FakeConversationRepository : IImConversationRepository
{
    public HashSet<(string Conversation, string User)> Members { get; } = [];

    public Task<long?> GetOrCreateDirectAsync(string userId, string otherUserId, CancellationToken token) => Task.FromResult<long?>(1);
    public Task<ImActionResult> CreateGroupAsync(string ownerUserId, string name, IReadOnlyList<string> memberIds, CancellationToken token)
        => Task.FromResult(new ImActionResult(true, null, null, 1));
    public Task<bool> IsActiveMemberAsync(long conversationId, string userId, CancellationToken token)
        => Task.FromResult(Members.Contains(($"c{conversationId}", userId)));
    public Task<string?> GetRoleAsync(long conversationId, string userId, CancellationToken token)
        => Task.FromResult(IsActiveMemberAsync(conversationId, userId, token).Result ? "Member" : null);
    public Task<ImActionResult> RenameGroupAsync(long conversationId, string actorUserId, string newName, CancellationToken token)
        => Task.FromResult(new ImActionResult(true, null, null, conversationId));
    public Task<ImActionResult> AddMembersAsync(long conversationId, string actorUserId, IReadOnlyList<string> userIds, CancellationToken token)
        => Task.FromResult(new ImActionResult(true, null, null, conversationId));
    public Task<ImActionResult> RemoveMemberAsync(long conversationId, string actorUserId, string targetUserId, CancellationToken token)
        => Task.FromResult(new ImActionResult(true, null, null, conversationId));
    public Task<ImActionResult> LeaveAsync(long conversationId, string userId, CancellationToken token)
        => Task.FromResult(new ImActionResult(true, null, null, conversationId));
    public Task<IReadOnlyList<long>> GetMemberConversationIdsAsync(string userId, CancellationToken token)
        => Task.FromResult<IReadOnlyList<long>>(Members.Where(m => m.User == userId).Select(m => long.Parse(m.Conversation[1..])).ToList());
    public Task<IReadOnlyList<string>> GetActiveMemberIdsAsync(long conversationId, CancellationToken token)
        => Task.FromResult<IReadOnlyList<string>>(Members.Where(m => m.Conversation == $"c{conversationId}").Select(m => m.User).ToList());
    public Task<IReadOnlyList<ImMemberDto>> GetMembersAsync(long conversationId, CancellationToken token)
        => Task.FromResult<IReadOnlyList<ImMemberDto>>(Members
            .Where(m => m.Conversation == $"c{conversationId}")
            .Select(m => new ImMemberDto(m.User, m.User, "Member"))
            .ToList());
    public Task<IReadOnlyList<ImConversationDto>> GetMyConversationsAsync(string userId, CancellationToken token)
        => Task.FromResult<IReadOnlyList<ImConversationDto>>([]);
    public Task<IReadOnlyList<ImContactDto>> SearchUsersAsync(string keyword, int limit, CancellationToken token)
        => Task.FromResult<IReadOnlyList<ImContactDto>>([]);
    public Task AuditAsync(long? conversationId, long? messageId, string actorUserId, string actionType, string? detail, CancellationToken token)
        => Task.CompletedTask;
}

internal sealed class FakeMessageRepository : IImMessageRepository
{
    public List<(long ConversationId, string Sender, Guid ClientId, ImMessageType Type, string Content, string Preview)> Sent { get; } = [];
    public List<long> ReceivedAcks { get; } = [];
    public List<long> ReadAcks { get; } = [];
    public long? RecalledMessageId { get; private set; }
    public ImRecallResult RecallResult { get; set; } = new(true, null, null, 0);

    public Task<ImMessageDto?> SendAsync(long conversationId, string senderUserId, Guid clientMessageId, ImMessageType type, string contentJson, string preview, CancellationToken token)
    {
        Sent.Add((conversationId, senderUserId, clientMessageId, type, contentJson, preview));
        return Task.FromResult<ImMessageDto?>(new ImMessageDto(
            1, conversationId, 1, clientMessageId, senderUserId, senderUserId, type, contentJson,
            DateTimeOffset.UtcNow, false, null, null));
    }

    public Task InsertSystemAsync(long conversationId, string contentJson, CancellationToken token)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ImMessageDto>> GetMessagesAfterAsync(long conversationId, long afterSeq, int limit, CancellationToken token)
        => Task.FromResult<IReadOnlyList<ImMessageDto>>([]);

    public Task<IReadOnlyList<ImMessageDto>> GetMessagesBeforeAsync(long conversationId, long beforeSeq, int limit, CancellationToken token)
        => Task.FromResult<IReadOnlyList<ImMessageDto>>([]);

    public Task<ImRecallResult> RecallAsync(long conversationId, long messageId, string actorUserId, CancellationToken token)
    {
        RecalledMessageId = messageId;
        return Task.FromResult(RecallResult);
    }

    public Task AckReceivedAsync(long conversationId, string userId, long seq, CancellationToken token)
    {
        ReceivedAcks.Add(seq);
        return Task.CompletedTask;
    }

    public Task AckReadAsync(long conversationId, string userId, long seq, CancellationToken token)
    {
        ReadAcks.Add(seq);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ImSearchHitDto>> SearchAsync(string userId, string keyword, long? conversationId, int limit, CancellationToken token)
        => Task.FromResult<IReadOnlyList<ImSearchHitDto>>([]);

    public Task<int> CleanupExpiredAsync(CancellationToken token) => Task.FromResult(0);
}

internal sealed class FakeCardService : IImCardService
{
    public bool Resolvable { get; set; } = true;
    public string SnapshotJson { get; set; } = """{"cardType":"purchase-order","entityId":"X","title":"采购单 X","fields":[],"actions":[]}""";

    public bool IsSupported(string cardType)
        => cardType is "purchase-order" or "inventory-count";

    public Task<string?> ResolveAsync(string cardType, string entityId, string userId, CancellationToken token)
        => Task.FromResult(Resolvable ? SnapshotJson : null);
}

internal sealed class FakeAttachmentRepository : IImAttachmentRepository
{
    public long? FileId { get; set; } = 1;
    public string UploadedBy { get; set; } = "u1";
    public long? LinkedMessageId { get; private set; }

    public Task<ImAttachmentDto?> UploadAsync(
        long conversationId, string uploaderUserId, string fileName, string contentType, byte[] content, string sha256, CancellationToken token)
        => Task.FromResult<ImAttachmentDto?>(new ImAttachmentDto(
            1, conversationId, uploaderUserId, fileName, contentType, content.LongLength, sha256, DateTimeOffset.UtcNow));

    public Task<ImAttachmentDto?> GetAsync(long fileId, CancellationToken token)
        => Task.FromResult(FileId is null
            ? null
            : new ImAttachmentDto(fileId, 1, UploadedBy, "a.txt", "text/plain", 3, "abc", DateTimeOffset.UtcNow));

    public Task<ImAttachmentFile?> GetFileAsync(long fileId, CancellationToken token)
        => Task.FromResult<ImAttachmentFile?>(new ImAttachmentFile(
            new ImAttachmentDto(fileId, 1, UploadedBy, "a.txt", "text/plain", 3, "abc", DateTimeOffset.UtcNow),
            [1, 2, 3]));

    public Task<bool> LinkToMessageAsync(long fileId, long messageId, CancellationToken token)
    {
        LinkedMessageId = messageId;
        return Task.FromResult(true);
    }
}
