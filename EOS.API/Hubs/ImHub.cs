using EOS.API.Data;
using EOS.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Diagnostics;

namespace EOS.API.Hubs;

/// <summary>
/// IM 实时通道（/api/hubs/im）。业务规则在 ImHubService，本 Hub 只做：
/// 身份映射、会话组成员挂载、事件推送与错误回传。
/// 消息内容经 SignalR 推送后仅存于服务端暂存库（方案 B），客户端本地为权威历史。
/// </summary>
[Authorize]
public sealed class ImHub(
    ImHubService service,
    IImConversationRepository conversations,
    HubUserTracker tracker,
    ILogger<ImHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrWhiteSpace(userId))
        {
            Context.Abort();
            return;
        }

        tracker.Add(userId, Context.ConnectionId);
        var conversationIds = await conversations.GetMemberConversationIdsAsync(userId, Context.ConnectionAborted);
        foreach (var conversationId in conversationIds)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(conversationId), Context.ConnectionAborted);
        }

        logger.LogInformation(
            "IM 连接 connectionId={ConnectionId} userId={UserId} conversations={Count}",
            Context.ConnectionId, userId, conversationIds.Count);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.UserIdentifier is { } userId)
        {
            tracker.Remove(userId, Context.ConnectionId);
        }

        logger.LogInformation(
            "IM 断开 userId={UserId} reason={Reason}",
            Context.UserIdentifier,
            exception?.GetType().Name ?? "normal");
        await base.OnDisconnectedAsync(exception);
    }

    public Task SendText(long conversationId, Guid clientMessageId, string text)
        => ExecuteSendAsync(
            () => service.SendTextAsync(UserId, conversationId, clientMessageId, text, Context.ConnectionAborted),
            conversationId);

    public Task SendCard(long conversationId, Guid clientMessageId, string cardType, string entityId)
        => ExecuteSendAsync(
            () => service.SendCardAsync(UserId, conversationId, clientMessageId, cardType, entityId, Context.ConnectionAborted),
            conversationId);

    public Task SendFile(long conversationId, Guid clientMessageId, long fileId)
        => ExecuteSendAsync(
            () => service.SendFileAsync(UserId, conversationId, clientMessageId, fileId, Context.ConnectionAborted),
            conversationId);

    public async Task Recall(long conversationId, long messageId)
    {
        await EnsureInGroupAsync(conversationId);
        var result = await service.RecallAsync(UserId, conversationId, messageId, Context.ConnectionAborted);
        if (!result.Success)
        {
            await SendErrorAsync(result.ErrorCode!, result.ErrorMessage!);
            return;
        }

        await Clients.Group(GroupName(conversationId)).SendAsync(
            "MessageRecalled",
            new { conversationId, messageId, recalledByUserId = UserId },
            Context.ConnectionAborted);
    }

    public async Task AckReceived(long conversationId, long seq)
    {
        await service.AckReceivedAsync(UserId, conversationId, seq, Context.ConnectionAborted);
    }

    public async Task AckRead(long conversationId, long seq)
    {
        await service.AckReadAsync(UserId, conversationId, seq, Context.ConnectionAborted);
    }

    public async Task Typing(long conversationId)
    {
        await EnsureInGroupAsync(conversationId);
        if (await conversations.IsActiveMemberAsync(conversationId, UserId, Context.ConnectionAborted))
        {
            await Clients.GroupExcept(GroupName(conversationId), Context.ConnectionId).SendAsync(
                "UserTyping",
                new { conversationId, userId = UserId },
                Context.ConnectionAborted);
        }
    }

    public async Task<long?> CreateGroup(string name, IReadOnlyList<string> memberIds)
    {
        var result = await conversations.CreateGroupAsync(UserId, name, memberIds, Context.ConnectionAborted);
        if (!result.Success)
        {
            await SendErrorAsync(result.ErrorCode!, result.ErrorMessage!);
            return null;
        }

        var conversationId = result.ConversationId!.Value;
        await RefreshGroupMembershipAsync(conversationId);
        await SendSystemAsync(conversationId, "group-created", null);
        await Clients.Group(GroupName(conversationId)).SendAsync(
            "ConversationUpdated",
            new { conversationId, action = "created", actorUserId = UserId },
            Context.ConnectionAborted);
        return conversationId;
    }

    public async Task RenameGroup(long conversationId, string name)
    {
        var result = await conversations.RenameGroupAsync(conversationId, UserId, name, Context.ConnectionAborted);
        if (!result.Success)
        {
            await SendErrorAsync(result.ErrorCode!, result.ErrorMessage!);
            return;
        }

        await Clients.Group(GroupName(conversationId)).SendAsync(
            "ConversationUpdated",
            new { conversationId, action = "renamed", actorUserId = UserId, name },
            Context.ConnectionAborted);
    }

    public async Task AddMember(long conversationId, IReadOnlyList<string> memberIds)
    {
        var result = await conversations.AddMembersAsync(conversationId, UserId, memberIds, Context.ConnectionAborted);
        if (!result.Success)
        {
            await SendErrorAsync(result.ErrorCode!, result.ErrorMessage!);
            return;
        }

        await RefreshGroupMembershipAsync(conversationId);
        await SendSystemAsync(conversationId, "member-added", string.Join(",", memberIds));
        await Clients.Group(GroupName(conversationId)).SendAsync(
            "ConversationUpdated",
            new { conversationId, action = "member-added", actorUserId = UserId, memberIds },
            Context.ConnectionAborted);
    }

    public async Task RemoveMember(long conversationId, string targetUserId)
    {
        var result = await conversations.RemoveMemberAsync(conversationId, UserId, targetUserId, Context.ConnectionAborted);
        if (!result.Success)
        {
            await SendErrorAsync(result.ErrorCode!, result.ErrorMessage!);
            return;
        }

        await SendSystemAsync(conversationId, "member-removed", targetUserId);
        await Clients.Group(GroupName(conversationId)).SendAsync(
            "ConversationUpdated",
            new { conversationId, action = "member-removed", actorUserId = UserId, targetUserId },
            Context.ConnectionAborted);
        await RemoveUserFromGroupAsync(targetUserId, conversationId);
    }

    public async Task Leave(long conversationId)
    {
        var result = await conversations.LeaveAsync(conversationId, UserId, Context.ConnectionAborted);
        if (!result.Success)
        {
            await SendErrorAsync(result.ErrorCode!, result.ErrorMessage!);
            return;
        }

        await SendSystemAsync(conversationId, "member-left", UserId);
        await Clients.Group(GroupName(conversationId)).SendAsync(
            "ConversationUpdated",
            new { conversationId, action = "member-left", userId = UserId },
            Context.ConnectionAborted);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(conversationId), Context.ConnectionAborted);
    }

    /// <summary>
    /// 客户端打开/新建会话后调用：把会话全部活跃成员的连接挂到会话组，
    /// 解决"REST 建单聊后发送方收不到自己消息"的实时通道缺口。
    /// </summary>
    public async Task JoinConversation(long conversationId)
    {
        if (!await conversations.IsActiveMemberAsync(conversationId, UserId, Context.ConnectionAborted))
        {
            await SendErrorAsync("FORBIDDEN", "无权加入此会话");
            return;
        }

        await RefreshGroupMembershipAsync(conversationId);
    }

    private async Task ExecuteSendAsync(Func<Task<Models.ImSendResult>> send, long conversationId)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogDebug(
            "IM 发送开始 conversationId={ConversationId} userId={UserId} connectionId={ConnectionId}",
            conversationId, UserId, Context.ConnectionId);
        await EnsureInGroupAsync(conversationId);

        Models.ImSendResult result;
        try
        {
            result = await send();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "IM 发送异常 conversationId={ConversationId} userId={UserId} elapsedMs={ElapsedMs}",
                conversationId, UserId, stopwatch.ElapsedMilliseconds);
            throw;
        }

        logger.LogInformation(
            "IM 发送完成 conversationId={ConversationId} userId={UserId} success={Success} code={Code} elapsedMs={ElapsedMs}",
            conversationId, UserId, result.Success, result.ErrorCode, stopwatch.ElapsedMilliseconds);
        if (!result.Success)
        {
            await SendErrorAsync(result.ErrorCode!, result.ErrorMessage!);
            return;
        }

        await Clients.Group(GroupName(conversationId)).SendAsync(
            "MessageReceived", result.Message, Context.ConnectionAborted);
        logger.LogInformation(
            "IM 广播完成 conversationId={ConversationId} elapsedMs={ElapsedMs}",
            conversationId, stopwatch.ElapsedMilliseconds);
    }

    private async Task RefreshGroupMembershipAsync(long conversationId)
    {
        var memberIds = await conversations.GetActiveMemberIdsAsync(conversationId, Context.ConnectionAborted);
        foreach (var memberId in memberIds)
        {
            foreach (var connectionId in tracker.GetConnectionIds(memberId))
            {
                await Groups.AddToGroupAsync(connectionId, GroupName(conversationId), Context.ConnectionAborted);
            }
        }
    }

    private async Task EnsureInGroupAsync(long conversationId)
    {
        if (!await conversations.IsActiveMemberAsync(conversationId, UserId, Context.ConnectionAborted))
        {
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(conversationId), Context.ConnectionAborted);
    }

    private async Task RemoveUserFromGroupAsync(string userId, long conversationId)
    {
        foreach (var connectionId in tracker.GetConnectionIds(userId))
        {
            await Groups.RemoveFromGroupAsync(connectionId, GroupName(conversationId), Context.ConnectionAborted);
        }
    }

    private async Task SendSystemAsync(long conversationId, string action, string? target)
    {
        var content = ImJson.Serialize(new { action, @operator = UserId, target });
        await service.SendSystemAsync(conversationId, content, Context.ConnectionAborted);
    }

    private Task SendErrorAsync(string code, string message)
        => Clients.Caller.SendAsync(
            "HubError", new { code, message }, Context.ConnectionAborted);

    private string UserId
        => Context.UserIdentifier ?? throw new UnauthorizedAccessException("未登录的 IM 连接");

    private static string GroupName(long conversationId) => $"c:{conversationId}";
}
