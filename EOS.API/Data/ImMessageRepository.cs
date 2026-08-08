using System.Data;
using EOS.API.Models;
using EOS.API.Services;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// EOS.IM 消息仓储：方案 B 下服务器只负责"送达暂存"，客户端本地为权威历史。
/// 消息序号由 im_conversations.LastMessageSeq 计数器经 UPDATE … OUTPUT 原子分配，
/// 保证并发安全；所有查询参数化，禁止拼接用户输入。
/// </summary>
public sealed class ImMessageRepository(DbConnectionFactory connections) : IImMessageRepository
{
    public async Task<ImMessageDto?> SendAsync(
        long conversationId,
        string senderUserId,
        Guid clientMessageId,
        ImMessageType type,
        string contentJson,
        string preview,
        CancellationToken token)
        => await InsertCoreAsync(conversationId, senderUserId, clientMessageId, type, contentJson, preview, checkMembership: true, token);

    public async Task InsertSystemAsync(long conversationId, string contentJson, CancellationToken token)
    {
        await InsertCoreAsync(
            conversationId,
            "system",
            Guid.NewGuid(),
            ImMessageType.System,
            contentJson,
            ImPreview.Build(ImMessageType.System, contentJson),
            checkMembership: false,
            token);
    }

    private async Task<ImMessageDto?> InsertCoreAsync(
        long conversationId,
        string senderUserId,
        Guid clientMessageId,
        ImMessageType type,
        string contentJson,
        string preview,
        bool checkMembership,
        CancellationToken token)
    {
        var sender = senderUserId.Trim();
        await using var connection = connections.CreateIm();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            if (checkMembership)
            {
                const string memberSql = """
                    SELECT 1
                    FROM dbo.im_conversation_members
                    WHERE ConversationId = @ConversationId AND UserId = @Sender AND LeftAt IS NULL;
                    """;
                await using var memberCheck = new SqlCommand(memberSql, connection, transaction);
                memberCheck.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
                memberCheck.Parameters.Add("@Sender", SqlDbType.NVarChar, 64).Value = sender;
                var isMember = await memberCheck.ExecuteScalarAsync(token);
                if (isMember is null or DBNull)
                {
                    return null;
                }
            }

            if (checkMembership)
            {
                const string duplicateSql = """
                    SELECT TOP (1) Id
                    FROM dbo.im_messages
                    WHERE ConversationId = @ConversationId AND ClientMessageId = @ClientMessageId;
                    """;
                await using var duplicateCheck = new SqlCommand(duplicateSql, connection, transaction);
                duplicateCheck.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
                duplicateCheck.Parameters.Add("@ClientMessageId", SqlDbType.UniqueIdentifier).Value = clientMessageId;
                var duplicate = await duplicateCheck.ExecuteScalarAsync(token);
                if (duplicate is not null and not DBNull)
                {
                    var existing = await GetMessageByIdAsync(connection, transaction, conversationId, Convert.ToInt64(duplicate), token);
                    await transaction.CommitAsync(token);
                    return existing;
                }
            }

            const string allocSql = """
                UPDATE dbo.im_conversations
                SET LastMessageSeq = LastMessageSeq + 1
                OUTPUT inserted.LastMessageSeq
                WHERE Id = @ConversationId;
                """;
            await using var alloc = new SqlCommand(allocSql, connection, transaction);
            alloc.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
            var seqValue = await alloc.ExecuteScalarAsync(token);
            if (seqValue is null or DBNull)
            {
                return null;
            }

            var seq = Convert.ToInt64(seqValue);
            const string insertSql = """
                INSERT INTO dbo.im_messages
                    (ConversationId, Seq, ClientMessageId, SenderUserId, MessageType, Content, SentAt, ExpiresAt)
                OUTPUT inserted.Id, inserted.SentAt
                VALUES (@ConversationId, @Seq, @ClientMessageId, @Sender, @Type, @Content,
                        SYSUTCDATETIME(), DATEADD(DAY, 30, SYSUTCDATETIME()));
                """;
            await using var insert = new SqlCommand(insertSql, connection, transaction);
            insert.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
            insert.Parameters.Add("@Seq", SqlDbType.BigInt).Value = seq;
            insert.Parameters.Add("@ClientMessageId", SqlDbType.UniqueIdentifier).Value = clientMessageId;
            insert.Parameters.Add("@Sender", SqlDbType.NVarChar, 64).Value = sender;
            insert.Parameters.Add("@Type", SqlDbType.NVarChar, 16).Value = type.ToString();
            insert.Parameters.Add("@Content", SqlDbType.NVarChar).Value = contentJson;
            long messageId;
            DateTime sentAt;
            await using (var reader = await insert.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token))
                {
                    return null;
                }

                messageId = reader.GetInt64(0);
                sentAt = reader.GetDateTime(1);
            }

            const string updateConvSql = """
                UPDATE dbo.im_conversations
                SET LastMessageAt = SYSUTCDATETIME(), LastMessagePreview = @Preview
                WHERE Id = @ConversationId;
                """;
            await using var updateConv = new SqlCommand(updateConvSql, connection, transaction);
            updateConv.Parameters.Add("@Preview", SqlDbType.NVarChar, 200).Value = preview;
            updateConv.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
            await updateConv.ExecuteNonQueryAsync(token);

            const string auditSql = """
                INSERT INTO dbo.im_audit_log (ConversationId, MessageId, ActorUserId, ActionType, CreatedAt)
                VALUES (@ConversationId, @MessageId, @Sender, N'Send', SYSUTCDATETIME());
                """;
            await using var audit = new SqlCommand(auditSql, connection, transaction);
            audit.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
            audit.Parameters.Add("@MessageId", SqlDbType.BigInt).Value = messageId;
            audit.Parameters.Add("@Sender", SqlDbType.NVarChar, 64).Value = sender;
            await audit.ExecuteNonQueryAsync(token);

            var created = await GetMessageByIdAsync(connection, transaction, conversationId, messageId, token);
            await transaction.CommitAsync(token);
            return created;
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    public async Task<IReadOnlyList<ImMessageDto>> GetMessagesAfterAsync(
        long conversationId, long afterSeq, int limit, CancellationToken token)
    {
        const string sql = """
            SELECT TOP (@Limit) m.Id, m.ConversationId, m.Seq, m.ClientMessageId, m.SenderUserId,
                   m.MessageType, m.Content, m.SentAt, m.IsRecalled, m.RecalledAt, m.RecalledByUserId,
                   ISNULL(n.EMP_NAME, m.SenderUserId) AS SenderName
            FROM dbo.im_messages m
            LEFT JOIN Hiswitek.dbo.SYSDL u WITH (NOLOCK) ON LTRIM(RTRIM(u.USER_ID)) = m.SenderUserId
            LEFT JOIN Hiswitek.dbo.SYSDN n WITH (NOLOCK) ON n.EMP_ID = u.EMP_ID
            WHERE m.ConversationId = @ConversationId AND m.Seq > @AfterSeq
            ORDER BY m.Seq ASC;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = Math.Clamp(limit, 1, 200);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        command.Parameters.Add("@AfterSeq", SqlDbType.BigInt).Value = afterSeq;
        await connection.OpenAsync(token);
        return await ReadMessagesAsync(command, token);
    }

    public async Task<IReadOnlyList<ImMessageDto>> GetMessagesBeforeAsync(
        long conversationId, long beforeSeq, int limit, CancellationToken token)
    {
        const string sql = """
            SELECT TOP (@Limit) m.Id, m.ConversationId, m.Seq, m.ClientMessageId, m.SenderUserId,
                   m.MessageType, m.Content, m.SentAt, m.IsRecalled, m.RecalledAt, m.RecalledByUserId,
                   ISNULL(n.EMP_NAME, m.SenderUserId) AS SenderName
            FROM dbo.im_messages m
            LEFT JOIN Hiswitek.dbo.SYSDL u WITH (NOLOCK) ON LTRIM(RTRIM(u.USER_ID)) = m.SenderUserId
            LEFT JOIN Hiswitek.dbo.SYSDN n WITH (NOLOCK) ON n.EMP_ID = u.EMP_ID
            WHERE m.ConversationId = @ConversationId AND m.Seq < @BeforeSeq
            ORDER BY m.Seq DESC;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = Math.Clamp(limit, 1, 200);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        command.Parameters.Add("@BeforeSeq", SqlDbType.BigInt).Value = beforeSeq;
        await connection.OpenAsync(token);
        var rows = await ReadMessagesAsync(command, token);
        return rows.Reverse().ToList();
    }

    public async Task<ImRecallResult> RecallAsync(
        long conversationId, long messageId, string actorUserId, CancellationToken token)
    {
        const string selectSql = """
            SELECT Id, SenderUserId, SentAt, IsRecalled
            FROM dbo.im_messages
            WHERE Id = @MessageId AND ConversationId = @ConversationId;
            """;
        await using var connection = connections.CreateIm();
        await using var select = new SqlCommand(selectSql, connection);
        select.Parameters.Add("@MessageId", SqlDbType.BigInt).Value = messageId;
        select.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        await connection.OpenAsync(token);
        string senderUserId;
        DateTime sentAt;
        bool isRecalled;
        await using (var reader = await select.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token))
            {
                return new(false, "NOT_FOUND", "消息不存在", messageId);
            }

            senderUserId = reader.GetString("SenderUserId").Trim();
            sentAt = reader.GetDateTime("SentAt");
            isRecalled = reader.GetBoolean("IsRecalled");
        }

        if (isRecalled)
        {
            return new(false, "ALREADY_RECALLED", "消息已被撤回", messageId);
        }

        if (!string.Equals(senderUserId, actorUserId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "NOT_SENDER", "只能撤回自己发送的消息", messageId);
        }

        var windowSeconds = 120;
        if (DateTime.UtcNow - DateTime.SpecifyKind(sentAt, DateTimeKind.Utc) > TimeSpan.FromSeconds(windowSeconds))
        {
            return new(false, "TOO_LATE", $"发送超过 {windowSeconds} 秒的消息无法撤回", messageId);
        }

        const string updateSql = """
            UPDATE dbo.im_messages
            SET IsRecalled = 1, RecalledAt = SYSUTCDATETIME(), RecalledByUserId = @Actor
            WHERE Id = @MessageId AND ConversationId = @ConversationId;
            """;
        await using var update = new SqlCommand(updateSql, connection);
        update.Parameters.Add("@MessageId", SqlDbType.BigInt).Value = messageId;
        update.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        update.Parameters.Add("@Actor", SqlDbType.NVarChar, 64).Value = actorUserId.Trim();
        await update.ExecuteNonQueryAsync(token);

        const string auditSql = """
            INSERT INTO dbo.im_audit_log (ConversationId, MessageId, ActorUserId, ActionType, CreatedAt)
            VALUES (@ConversationId, @MessageId, @Actor, N'Recall', SYSUTCDATETIME());
            """;
        await using var audit = new SqlCommand(auditSql, connection);
        audit.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        audit.Parameters.Add("@MessageId", SqlDbType.BigInt).Value = messageId;
        audit.Parameters.Add("@Actor", SqlDbType.NVarChar, 64).Value = actorUserId.Trim();
        await audit.ExecuteNonQueryAsync(token);
        return new(true, null, null, messageId);
    }

    public async Task AckReceivedAsync(long conversationId, string userId, long seq, CancellationToken token)
    {
        const string updateMemberSql = """
            UPDATE dbo.im_conversation_members
            SET LastReceivedMessageSeq = @Seq, UpdatedAt = SYSUTCDATETIME()
            WHERE ConversationId = @ConversationId AND UserId = @UserId AND LeftAt IS NULL
              AND LastReceivedMessageSeq < @Seq;
            """;
        const string markDeliveredSql = """
            UPDATE mm
            SET mm.DeliveredAt = SYSUTCDATETIME()
            FROM dbo.im_messages mm
            WHERE mm.ConversationId = @ConversationId AND mm.DeliveredAt IS NULL AND mm.Seq <= @Seq
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.im_conversation_members m
                  WHERE m.ConversationId = mm.ConversationId AND m.LeftAt IS NULL
                    AND m.LastReceivedMessageSeq < mm.Seq);
            """;
        await using var connection = connections.CreateIm();
        await connection.OpenAsync(token);
        await using var updateMember = new SqlCommand(updateMemberSql, connection);
        updateMember.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        updateMember.Parameters.Add("@UserId", SqlDbType.NVarChar, 64).Value = userId.Trim();
        updateMember.Parameters.Add("@Seq", SqlDbType.BigInt).Value = seq;
        await updateMember.ExecuteNonQueryAsync(token);

        await using var markDelivered = new SqlCommand(markDeliveredSql, connection);
        markDelivered.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        markDelivered.Parameters.Add("@Seq", SqlDbType.BigInt).Value = seq;
        await markDelivered.ExecuteNonQueryAsync(token);
    }

    public async Task AckReadAsync(long conversationId, string userId, long seq, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.im_conversation_members
            SET LastReadMessageSeq = @Seq, UpdatedAt = SYSUTCDATETIME()
            WHERE ConversationId = @ConversationId AND UserId = @UserId AND LeftAt IS NULL
              AND LastReadMessageSeq < @Seq;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 64).Value = userId.Trim();
        command.Parameters.Add("@Seq", SqlDbType.BigInt).Value = seq;
        await connection.OpenAsync(token);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<IReadOnlyList<ImSearchHitDto>> SearchAsync(
        string userId, string keyword, long? conversationId, int limit, CancellationToken token)
    {
        var pattern = $"%{keyword?.Trim() ?? string.Empty}%";
        const string sql = """
            SELECT TOP (@Limit) m.Id, m.ConversationId, m.Seq, m.ClientMessageId, m.SenderUserId,
                   m.MessageType, m.Content, m.SentAt, m.IsRecalled, m.RecalledAt, m.RecalledByUserId,
                   ISNULL(n.EMP_NAME, m.SenderUserId) AS SenderName,
                   c.Name AS ConversationName
            FROM dbo.im_messages m
            JOIN dbo.im_conversations c ON c.Id = m.ConversationId
            LEFT JOIN Hiswitek.dbo.SYSDL u WITH (NOLOCK) ON LTRIM(RTRIM(u.USER_ID)) = m.SenderUserId
            LEFT JOIN Hiswitek.dbo.SYSDN n WITH (NOLOCK) ON n.EMP_ID = u.EMP_ID
            WHERE m.IsRecalled = 0 AND m.Content LIKE @Pattern
              AND EXISTS (
                  SELECT 1 FROM dbo.im_conversation_members x
                  WHERE x.ConversationId = m.ConversationId AND x.UserId = @UserId AND x.LeftAt IS NULL)
              AND (@ConversationId IS NULL OR m.ConversationId = @ConversationId)
            ORDER BY m.SentAt DESC;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = Math.Clamp(limit, 1, 100);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 64).Value = userId.Trim();
        command.Parameters.Add("@Pattern", SqlDbType.NVarChar).Value = pattern;
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value =
            conversationId is null ? DBNull.Value : conversationId.Value;
        await connection.OpenAsync(token);
        var result = new List<ImSearchHitDto>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result.Add(new ImSearchHitDto(
                reader.GetInt64("ConversationId"),
                reader.GetNullableString("ConversationName"),
                ReadMessage(reader)));
        }

        return result;
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken token)
    {
        const string sql = """
            DELETE FROM dbo.im_audit_log
            WHERE MessageId IN (SELECT Id FROM dbo.im_messages WHERE ExpiresAt < SYSUTCDATETIME());
            DELETE FROM dbo.im_messages WHERE ExpiresAt < SYSUTCDATETIME();
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        await connection.OpenAsync(token);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<ImMessageDto?> GetMessageByIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long conversationId,
        long messageId,
        CancellationToken token)
    {
        const string sql = """
            SELECT TOP (1) m.Id, m.ConversationId, m.Seq, m.ClientMessageId, m.SenderUserId,
                   m.MessageType, m.Content, m.SentAt, m.IsRecalled, m.RecalledAt, m.RecalledByUserId,
                   ISNULL(n.EMP_NAME, m.SenderUserId) AS SenderName
            FROM dbo.im_messages m
            LEFT JOIN Hiswitek.dbo.SYSDL u WITH (NOLOCK) ON LTRIM(RTRIM(u.USER_ID)) = m.SenderUserId
            LEFT JOIN Hiswitek.dbo.SYSDN n WITH (NOLOCK) ON n.EMP_ID = u.EMP_ID
            WHERE m.ConversationId = @ConversationId AND m.Id = @MessageId;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        command.Parameters.Add("@MessageId", SqlDbType.BigInt).Value = messageId;
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadMessage(reader) : null;
    }

    private static async Task<IReadOnlyList<ImMessageDto>> ReadMessagesAsync(
        SqlCommand command, CancellationToken token)
    {
        var result = new List<ImMessageDto>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result.Add(ReadMessage(reader));
        }

        return result;
    }

    private static ImMessageDto ReadMessage(SqlDataReader reader)
        => new(
            reader.GetInt64("Id"),
            reader.GetInt64("ConversationId"),
            reader.GetInt64("Seq"),
            reader.GetGuid("ClientMessageId"),
            reader.GetString("SenderUserId").Trim(),
            reader.GetNullableString("SenderName")?.Trim(),
            Enum.TryParse<ImMessageType>(reader.GetString("MessageType").Trim(), ignoreCase: true, out var type)
                ? type
                : ImMessageType.Text,
            reader.GetString("Content"),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime("SentAt"), DateTimeKind.Utc)),
            reader.GetBoolean("IsRecalled"),
            reader.IsDBNull(reader.GetOrdinal("RecalledAt"))
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime("RecalledAt"), DateTimeKind.Utc)),
            reader.GetNullableString("RecalledByUserId")?.Trim());
}
