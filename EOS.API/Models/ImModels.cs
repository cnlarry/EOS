namespace EOS.API.Models;

/// <summary>会话类型，与 im_conversations.ConversationType 对应。</summary>
public enum ImConversationType
{
    Direct,
    Group,
}

/// <summary>消息类型，与 im_messages.MessageType 对应（File 由迁移 002 放开）。</summary>
public enum ImMessageType
{
    Text,
    Card,
    System,
    File,
}

/// <summary>一条消息的传输视图（发送方与接收方共用，客户端以此落本地权威历史）。</summary>
public sealed record ImMessageDto(
    long Id,
    long ConversationId,
    long Seq,
    Guid ClientMessageId,
    string SenderUserId,
    string? SenderName,
    ImMessageType MessageType,
    string Content,
    DateTimeOffset SentAt,
    bool IsRecalled,
    DateTimeOffset? RecalledAt,
    string? RecalledByUserId);

/// <summary>会话列表项（含未读、成员数、免打扰/归档状态）。</summary>
public sealed record ImConversationDto(
    long Id,
    ImConversationType Type,
    string? Name,
    long LastMessageSeq,
    DateTimeOffset? LastMessageAt,
    string? LastMessagePreview,
    long UnreadCount,
    int MemberCount,
    bool IsMuted,
    bool IsArchived,
    bool CanManage);

/// <summary>通讯录联系人（来自旧系统用户目录，只读）。</summary>
public sealed record ImContactDto(string UserId, string EmployeeName, string? DepartmentName);

/// <summary>会话成员视图（成员列表/群管理用）。</summary>
public sealed record ImMemberDto(string UserId, string EmployeeName, string Role);

/// <summary>附件元数据（上传/文件消息用；下载时另取二进制）。</summary>
public sealed record ImAttachmentDto(
    long Id,
    long ConversationId,
    string UploadedByUserId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset UploadedAt);

/// <summary>搜索结果命中：会话 + 命中消息。</summary>
public sealed record ImSearchHitDto(long ConversationId, string? ConversationName, ImMessageDto Message);

// ---- 请求体 ----
public sealed record ImSendTextRequest(long ConversationId, Guid ClientMessageId, string Text);
public sealed record ImSendCardRequest(long ConversationId, Guid ClientMessageId, string CardType, string EntityId);
public sealed record ImRecallRequest(long MessageId);
public sealed record ImAckRequest(long? LastReceivedSeq, long? LastReadSeq);
public sealed record ImCreateDirectRequest(string TargetUserId);
public sealed record ImCreateGroupRequest(string Name, IReadOnlyList<string> MemberIds);
public sealed record ImRenameGroupRequest(string Name);
public sealed record ImMemberActionRequest(string TargetUserId);

// ---- 服务层结果 ----
public sealed record ImSendResult(bool Success, string? ErrorCode, string? ErrorMessage, ImMessageDto? Message);
public sealed record ImRecallResult(bool Success, string? ErrorCode, string? ErrorMessage, long? MessageId);
public sealed record ImActionResult(bool Success, string? ErrorCode, string? ErrorMessage, long? ConversationId);
