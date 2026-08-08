using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;

namespace EOS.API.Services;

/// <summary>
/// IM 实时通道编排（与 SignalR 解耦，便于单元测试）：
/// 发送前做成员校验、限流、卡片解析；所有业务规则在服务层，Hub 只做身份映射与事件推送。
/// </summary>
public sealed class ImHubService(
    IImConversationRepository conversations,
    IImMessageRepository messages,
    IImAttachmentRepository attachments,
    IImCardService cards,
    ImRateLimiter rateLimiter)
{
    public async Task<ImSendResult> SendTextAsync(
        string userId, long conversationId, Guid clientMessageId, string text, CancellationToken token)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return new(false, "EMPTY_MESSAGE", "消息不能为空", null);
        }

        if (trimmed.Length > 4000)
        {
            return new(false, "MESSAGE_TOO_LONG", "消息不能超过 4000 字符", null);
        }

        if (!await conversations.IsActiveMemberAsync(conversationId, userId, token))
        {
            return new(false, "FORBIDDEN", "无权在此会话发言", null);
        }

        if (!rateLimiter.TryConsume(userId))
        {
            return new(false, "RATE_LIMITED", "发送过于频繁，请稍后再试", null);
        }

        var content = ImJson.Serialize(new { text = trimmed });
        var message = await messages.SendAsync(
            conversationId, userId, clientMessageId, ImMessageType.Text, content,
            ImPreview.Build(ImMessageType.Text, content), token);
        return message is null
            ? new(false, "SEND_FAILED", "消息发送失败", null)
            : new(true, null, null, message);
    }

    public async Task<ImSendResult> SendCardAsync(
        string userId, long conversationId, Guid clientMessageId, string cardType, string entityId, CancellationToken token)
    {
        if (!cards.IsSupported(cardType))
        {
            return new(false, "CARD_TYPE_UNSUPPORTED", "不支持的卡片类型", null);
        }

        if (string.IsNullOrWhiteSpace(entityId))
        {
            return new(false, "EMPTY_ENTITY", "卡片实体不能为空", null);
        }

        if (!await conversations.IsActiveMemberAsync(conversationId, userId, token))
        {
            return new(false, "FORBIDDEN", "无权在此会话发言", null);
        }

        if (!rateLimiter.TryConsume(userId))
        {
            return new(false, "RATE_LIMITED", "发送过于频繁，请稍后再试", null);
        }

        var snapshot = await cards.ResolveAsync(cardType, entityId, userId, token);
        if (snapshot is null)
        {
            return new(false, "CARD_UNAVAILABLE", "卡片实体不存在或当前用户无权查看", null);
        }

        var preview = ImPreview.Build(ImMessageType.Card, snapshot);
        var message = await messages.SendAsync(
            conversationId, userId, clientMessageId, ImMessageType.Card, snapshot, preview, token);
        return message is null
            ? new(false, "SEND_FAILED", "消息发送失败", null)
            : new(true, null, null, message);
    }

    public async Task<ImRecallResult> RecallAsync(
        string userId, long conversationId, long messageId, CancellationToken token)
    {
        if (!await conversations.IsActiveMemberAsync(conversationId, userId, token))
        {
            return new(false, "FORBIDDEN", "无权操作此会话", messageId);
        }

        return await messages.RecallAsync(conversationId, messageId, userId, token);
    }

    public async Task AckReceivedAsync(string userId, long conversationId, long seq, CancellationToken token)
    {
        if (await conversations.IsActiveMemberAsync(conversationId, userId, token))
        {
            await messages.AckReceivedAsync(conversationId, userId, seq, token);
        }
    }

    public async Task AckReadAsync(string userId, long conversationId, long seq, CancellationToken token)
    {
        if (await conversations.IsActiveMemberAsync(conversationId, userId, token))
        {
            await messages.AckReadAsync(conversationId, userId, seq, token);
        }
    }

    /// <summary>插入系统消息（入群/退群/改名等），由 Hub 在成员变更成功后调用。</summary>
    public async Task SendSystemAsync(long conversationId, string contentJson, CancellationToken token)
    {
        await messages.InsertSystemAsync(conversationId, contentJson, token);
    }

    /// <summary>把已上传附件作为文件消息发出（仅上传者可发，附件必须属于该会话）。</summary>
    public async Task<ImSendResult> SendFileAsync(
        string userId, long conversationId, Guid clientMessageId, long fileId, CancellationToken token)
    {
        if (!await conversations.IsActiveMemberAsync(conversationId, userId, token))
        {
            return new(false, "FORBIDDEN", "无权在此会话发言", null);
        }

        if (!rateLimiter.TryConsume(userId))
        {
            return new(false, "RATE_LIMITED", "发送过于频繁，请稍后再试", null);
        }

        var attachment = await attachments.GetAsync(fileId, token);
        if (attachment is null || attachment.ConversationId != conversationId)
        {
            return new(false, "FILE_NOT_FOUND", "附件不存在或不属于该会话", null);
        }

        if (!string.Equals(attachment.UploadedByUserId, userId, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "FORBIDDEN", "只能发送自己上传的附件", null);
        }

        var content = ImJson.Serialize(new
        {
            fileId = attachment.Id,
            fileName = attachment.FileName,
            sizeBytes = attachment.SizeBytes,
            contentType = attachment.ContentType,
        });
        var message = await messages.SendAsync(
            conversationId, userId, clientMessageId, ImMessageType.File, content,
            ImPreview.Build(ImMessageType.File, content), token);
        if (message is null)
        {
            return new(false, "SEND_FAILED", "消息发送失败", null);
        }

        await attachments.LinkToMessageAsync(fileId, message.Id, token);
        return new(true, null, null, message);
    }
}
