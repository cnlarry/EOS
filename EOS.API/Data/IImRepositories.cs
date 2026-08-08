using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>会话/成员/通讯录仓储接口（EOS.IM 库），供 Hub 编排与单元测试注入。</summary>
public interface IImConversationRepository
{
    /// <summary>查找或创建与另一用户的单聊会话，返回会话 ID。</summary>
    Task<long?> GetOrCreateDirectAsync(string userId, string otherUserId, CancellationToken token);

    /// <summary>创建群聊；owner 自动加入并成为 Owner，其余成员为 Member。</summary>
    Task<ImActionResult> CreateGroupAsync(string ownerUserId, string name, IReadOnlyList<string> memberIds, CancellationToken token);

    /// <summary>当前用户是否为会话的活跃成员（权限边界核心校验）。</summary>
    Task<bool> IsActiveMemberAsync(long conversationId, string userId, CancellationToken token);

    /// <summary>返回成员角色（Owner/Admin/Member），非成员返回 null。</summary>
    Task<string?> GetRoleAsync(long conversationId, string userId, CancellationToken token);

    /// <summary>重命名群聊（Owner/Admin 可操作），返回成功与否。</summary>
    Task<ImActionResult> RenameGroupAsync(long conversationId, string actorUserId, string newName, CancellationToken token);

    /// <summary>加人进群（Owner/Admin 可操作），已退群成员重新加入时恢复活跃。</summary>
    Task<ImActionResult> AddMembersAsync(long conversationId, string actorUserId, IReadOnlyList<string> userIds, CancellationToken token);

    /// <summary>把目标成员移出群（仅 Owner），返回是否成功。</summary>
    Task<ImActionResult> RemoveMemberAsync(long conversationId, string actorUserId, string targetUserId, CancellationToken token);

    /// <summary>自己退群（任何成员均可）。</summary>
    Task<ImActionResult> LeaveAsync(long conversationId, string userId, CancellationToken token);

    /// <summary>当前用户所属的全部会话 ID（活跃成员身份）。</summary>
    Task<IReadOnlyList<long>> GetMemberConversationIdsAsync(string userId, CancellationToken token);

    /// <summary>会话活跃成员 ID 列表。</summary>
    Task<IReadOnlyList<string>> GetActiveMemberIdsAsync(long conversationId, CancellationToken token);

    /// <summary>会话活跃成员列表（含姓名与角色，群管理界面用）。</summary>
    Task<IReadOnlyList<ImMemberDto>> GetMembersAsync(long conversationId, CancellationToken token);

    /// <summary>当前用户的会话列表（含未读、成员数、免打扰/归档）。</summary>
    Task<IReadOnlyList<ImConversationDto>> GetMyConversationsAsync(string userId, CancellationToken token);

    /// <summary>按关键字检索在职用户目录（来自旧系统，只读）。</summary>
    Task<IReadOnlyList<ImContactDto>> SearchUsersAsync(string keyword, int limit, CancellationToken token);

    /// <summary>写一条元数据审计（不含消息正文）。</summary>
    Task AuditAsync(
        long? conversationId,
        long? messageId,
        string actorUserId,
        string actionType,
        string? detail,
        CancellationToken token);
}

/// <summary>消息/送达/搜索/清理仓储接口（EOS.IM 库）。</summary>
public interface IImMessageRepository
{
    /// <summary>发送一条消息：原子分配 Seq、落库、更新会话预览与审计；重复 ClientMessageId 返回原消息。</summary>
    Task<ImMessageDto?> SendAsync(
        long conversationId,
        string senderUserId,
        Guid clientMessageId,
        ImMessageType type,
        string contentJson,
        string preview,
        CancellationToken token);

    /// <summary>插入系统消息（入群/退群/改名等，发件人固定为 system，跳过成员校验）。</summary>
    Task InsertSystemAsync(long conversationId, string contentJson, CancellationToken token);

    /// <summary>拉取 afterSeq 之后的消息（增量同步，升序）。</summary>
    Task<IReadOnlyList<ImMessageDto>> GetMessagesAfterAsync(long conversationId, long afterSeq, int limit, CancellationToken token);

    /// <summary>拉取 beforeSeq 之前的消息（历史翻页，降序取回后升序返回）。</summary>
    Task<IReadOnlyList<ImMessageDto>> GetMessagesBeforeAsync(long conversationId, long beforeSeq, int limit, CancellationToken token);

    /// <summary>撤回消息（仅发送者、2 分钟窗口内、软删除）。</summary>
    Task<ImRecallResult> RecallAsync(long conversationId, long messageId, string actorUserId, CancellationToken token);

    /// <summary>上报已收到序号并推进送达状态（全员收到后写 DeliveredAt）。</summary>
    Task AckReceivedAsync(long conversationId, string userId, long seq, CancellationToken token);

    /// <summary>上报已读序号（未读计算依据）。</summary>
    Task AckReadAsync(long conversationId, string userId, long seq, CancellationToken token);

    /// <summary>在用户所属会话范围内全文检索消息（参数化 LIKE，最多 limit 条）。</summary>
    Task<IReadOnlyList<ImSearchHitDto>> SearchAsync(
        string userId,
        string keyword,
        long? conversationId,
        int limit,
        CancellationToken token);

    /// <summary>清理已到保留期的暂存消息，返回删除行数。</summary>
    Task<int> CleanupExpiredAsync(CancellationToken token);
}

/// <summary>附件仓储接口（EOS.IM im_attachments）。</summary>
public interface IImAttachmentRepository
{
    /// <summary>上传附件（MessageId 暂为 NULL，发送文件消息后回填）。</summary>
    Task<ImAttachmentDto?> UploadAsync(
        long conversationId,
        string uploaderUserId,
        string fileName,
        string contentType,
        byte[] content,
        string sha256,
        CancellationToken token);

    /// <summary>按 ID 取附件元数据（不含二进制），不存在返回 null。</summary>
    Task<ImAttachmentDto?> GetAsync(long fileId, CancellationToken token);

    /// <summary>取附件二进制与元数据（下载用）。</summary>
    Task<ImAttachmentFile?> GetFileAsync(long fileId, CancellationToken token);

    /// <summary>发送文件消息后回填 MessageId。</summary>
    Task<bool> LinkToMessageAsync(long fileId, long messageId, CancellationToken token);
}

/// <summary>附件二进制读取结果。</summary>
public sealed record ImAttachmentFile(ImAttachmentDto Meta, byte[] Content);
