using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// EOS.IM 会话/成员/通讯录仓储。成员关系即访问权限边界；
/// 用户目录（姓名/部门）来自 EOS.ERP 旧系统，只读跨库查询，不新建用户体系。
/// </summary>
public sealed class ImConversationRepository(DbConnectionFactory connections) : IImConversationRepository
{
    public async Task<long?> GetOrCreateDirectAsync(string userId, string otherUserId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(otherUserId))
        {
            return null;
        }

        var a = userId.Trim();
        var b = otherUserId.Trim();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        const string findSql = """
            SELECT TOP (1) c.Id
            FROM dbo.im_conversations c
            WHERE c.ConversationType = N'Direct'
              AND EXISTS (SELECT 1 FROM dbo.im_conversation_members m
                          WHERE m.ConversationId = c.Id AND m.UserId = @A AND m.LeftAt IS NULL)
              AND EXISTS (SELECT 1 FROM dbo.im_conversation_members m
                          WHERE m.ConversationId = c.Id AND m.UserId = @B AND m.LeftAt IS NULL)
              AND (SELECT COUNT(*) FROM dbo.im_conversation_members m2
                   WHERE m2.ConversationId = c.Id AND m2.LeftAt IS NULL) = 2;
            """;

        await using var connection = connections.CreateIm();
        await connection.OpenAsync(token);
        await using var find = new SqlCommand(findSql, connection);
        find.Parameters.Add("@A", SqlDbType.NVarChar, 64).Value = a;
        find.Parameters.Add("@B", SqlDbType.NVarChar, 64).Value = b;
        var existing = await find.ExecuteScalarAsync(token);
        if (existing is not null && existing is not DBNull)
        {
            return Convert.ToInt64(existing);
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            const string insertConvSql = """
                INSERT INTO dbo.im_conversations (ConversationType, CreatedByUserId, LastMessageSeq)
                OUTPUT inserted.Id
                VALUES (N'Direct', @Actor, 0);
                """;
            await using var insertConv = new SqlCommand(insertConvSql, connection, transaction);
            insertConv.Parameters.Add("@Actor", SqlDbType.NVarChar, 64).Value = a;
            var conversationId = Convert.ToInt64((await insertConv.ExecuteScalarAsync(token))!);

            await InsertMemberAsync(connection, transaction, conversationId, a, token);
            await InsertMemberAsync(connection, transaction, conversationId, b, token);
            await WriteAuditAsync(
                connection, transaction, conversationId, null, a, "CreateConversation",
                $"{{\"type\":\"Direct\",\"with\":\"{EscapeJson(b)}\"}}", token);
            await transaction.CommitAsync(token);
            return conversationId;
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    public async Task<ImActionResult> CreateGroupAsync(
        string ownerUserId, string name, IReadOnlyList<string> memberIds, CancellationToken token)
    {
        var owner = ownerUserId.Trim();
        var groupName = name?.Trim() ?? string.Empty;
        if (groupName.Length is 0 or > 50)
        {
            return new(false, "INVALID_NAME", "群名称长度需为 1-50 个字符", null);
        }

        var members = memberIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Where(id => !string.Equals(id, owner, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (members.Count == 0)
        {
            return new(false, "NO_MEMBERS", "群聊至少需要一名成员", null);
        }

        await using var connection = connections.CreateIm();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            const string insertConvSql = """
                INSERT INTO dbo.im_conversations (ConversationType, Name, OwnerUserId, CreatedByUserId, LastMessageSeq)
                OUTPUT inserted.Id
                VALUES (N'Group', @Name, @Owner, @Owner, 0);
                """;
            await using var insertConv = new SqlCommand(insertConvSql, connection, transaction);
            insertConv.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = groupName;
            insertConv.Parameters.Add("@Owner", SqlDbType.NVarChar, 64).Value = owner;
            var conversationId = Convert.ToInt64((await insertConv.ExecuteScalarAsync(token))!);

            await InsertMemberAsync(connection, transaction, conversationId, owner, token, "Owner");
            foreach (var member in members)
            {
                await InsertMemberAsync(connection, transaction, conversationId, member, token, "Member");
            }

            await WriteAuditAsync(
                connection, transaction, conversationId, null, owner, "CreateConversation",
                $"{{\"type\":\"Group\",\"name\":\"{EscapeJson(groupName)}\",\"members\":{System.Text.Json.JsonSerializer.Serialize(members)}}}",
                token);
            await transaction.CommitAsync(token);
            return new(true, null, null, conversationId);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    public async Task<bool> IsActiveMemberAsync(long conversationId, string userId, CancellationToken token)
        => await GetRoleAsync(conversationId, userId, token) is not null;

    public async Task<string?> GetRoleAsync(long conversationId, string userId, CancellationToken token)
    {
        const string sql = """
            SELECT Role
            FROM dbo.im_conversation_members
            WHERE ConversationId = @ConversationId AND UserId = @UserId AND LeftAt IS NULL;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 64).Value = userId.Trim();
        await connection.OpenAsync(token);
        var role = await command.ExecuteScalarAsync(token);
        return role is null or DBNull ? null : Convert.ToString(role);
    }

    public async Task<ImActionResult> RenameGroupAsync(
        long conversationId, string actorUserId, string newName, CancellationToken token)
    {
        var name = newName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 50)
        {
            return new(false, "INVALID_NAME", "群名称长度需为 1-50 个字符", conversationId);
        }

        var role = await GetRoleAsync(conversationId, actorUserId.Trim(), token);
        if (role is not ("Owner" or "Admin"))
        {
            return new(false, "FORBIDDEN", "只有群主或管理员可以重命名群聊", conversationId);
        }

        const string sql = """
            UPDATE dbo.im_conversations
            SET Name = @Name, UpdatedAt = SYSUTCDATETIME()
            OUTPUT inserted.Id
            WHERE Id = @ConversationId AND ConversationType = N'Group';
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = name;
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        await connection.OpenAsync(token);
        var id = await command.ExecuteScalarAsync(token);
        if (id is null or DBNull)
        {
            return new(false, "NOT_FOUND", "会话不存在或不是群聊", conversationId);
        }

        await AuditAsync(conversationId, null, actorUserId.Trim(), "RenameConversation",
            $"{{\"name\":\"{EscapeJson(name)}\"}}", token);
        return new(true, null, null, conversationId);
    }

    public async Task<ImActionResult> AddMembersAsync(
        long conversationId, string actorUserId, IReadOnlyList<string> userIds, CancellationToken token)
    {
        var role = await GetRoleAsync(conversationId, actorUserId.Trim(), token);
        if (role is not ("Owner" or "Admin"))
        {
            return new(false, "FORBIDDEN", "只有群主或管理员可以添加成员", conversationId);
        }

        var targets = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0)
        {
            return new(false, "NO_MEMBERS", "没有可添加的成员", conversationId);
        }

        await using var connection = connections.CreateIm();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            foreach (var target in targets)
            {
                const string restoreSql = """
                    UPDATE dbo.im_conversation_members
                    SET LeftAt = NULL, UpdatedAt = SYSUTCDATETIME()
                    WHERE ConversationId = @ConversationId AND UserId = @UserId AND LeftAt IS NOT NULL;
                    """;
                await using var restore = new SqlCommand(restoreSql, connection, transaction);
                restore.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
                restore.Parameters.Add("@UserId", SqlDbType.NVarChar, 64).Value = target;
                var restored = await restore.ExecuteNonQueryAsync(token);
                if (restored == 0)
                {
                    await InsertMemberAsync(connection, transaction, conversationId, target, token, "Member");
                }
            }

            await WriteAuditAsync(
                connection, transaction, conversationId, null, actorUserId.Trim(), "AddMember",
                $"{{\"members\":{System.Text.Json.JsonSerializer.Serialize(targets)}}}", token);
            await transaction.CommitAsync(token);
            return new(true, null, null, conversationId);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    public async Task<ImActionResult> RemoveMemberAsync(
        long conversationId, string actorUserId, string targetUserId, CancellationToken token)
    {
        var actor = actorUserId.Trim();
        var target = targetUserId.Trim();
        var role = await GetRoleAsync(conversationId, actor, token);
        if (role != "Owner")
        {
            return new(false, "FORBIDDEN", "只有群主可以移出成员", conversationId);
        }

        if (string.Equals(actor, target, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "INVALID_TARGET", "群主请使用退群", conversationId);
        }

        const string sql = """
            UPDATE dbo.im_conversation_members
            SET LeftAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME()
            WHERE ConversationId = @ConversationId AND UserId = @Target AND LeftAt IS NULL;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        command.Parameters.Add("@Target", SqlDbType.NVarChar, 64).Value = target;
        await connection.OpenAsync(token);
        var affected = await command.ExecuteNonQueryAsync(token);
        if (affected == 0)
        {
            return new(false, "NOT_MEMBER", "目标用户不是本群活跃成员", conversationId);
        }

        await AuditAsync(conversationId, null, actor, "RemoveMember",
            $"{{\"target\":\"{EscapeJson(target)}\"}}", token);
        return new(true, null, null, conversationId);
    }

    public async Task<ImActionResult> LeaveAsync(long conversationId, string userId, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.im_conversation_members
            SET LeftAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME()
            WHERE ConversationId = @ConversationId AND UserId = @UserId AND LeftAt IS NULL;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 64).Value = userId.Trim();
        await connection.OpenAsync(token);
        var affected = await command.ExecuteNonQueryAsync(token);
        if (affected == 0)
        {
            return new(false, "NOT_MEMBER", "你不是本群活跃成员", conversationId);
        }

        await AuditAsync(conversationId, null, userId.Trim(), "LeaveConversation", null, token);
        return new(true, null, null, conversationId);
    }

    public async Task<IReadOnlyList<long>> GetMemberConversationIdsAsync(string userId, CancellationToken token)
    {
        const string sql = """
            SELECT ConversationId
            FROM dbo.im_conversation_members
            WHERE UserId = @UserId AND LeftAt IS NULL;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 64).Value = userId.Trim();
        await connection.OpenAsync(token);
        var result = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetActiveMemberIdsAsync(long conversationId, CancellationToken token)
    {
        const string sql = """
            SELECT UserId
            FROM dbo.im_conversation_members
            WHERE ConversationId = @ConversationId AND LeftAt IS NULL;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        await connection.OpenAsync(token);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result.Add(reader.GetString(0).Trim());
        }

        return result;
    }

    public async Task<IReadOnlyList<ImMemberDto>> GetMembersAsync(long conversationId, CancellationToken token)
    {
        const string sql = """
            SELECT m.UserId, m.Role, ISNULL(n.EMP_NAME, m.UserId) AS EmployeeName
            FROM dbo.im_conversation_members m
            LEFT JOIN EOS.ERP.dbo.SYSDL u WITH (NOLOCK) ON LTRIM(RTRIM(u.USER_ID)) = m.UserId
            LEFT JOIN EOS.ERP.dbo.SYSDN n WITH (NOLOCK) ON n.EMP_ID = u.EMP_ID
            WHERE m.ConversationId = @ConversationId AND m.LeftAt IS NULL
            ORDER BY CASE m.Role WHEN N'Owner' THEN 0 WHEN N'Admin' THEN 1 ELSE 2 END, EmployeeName;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        await connection.OpenAsync(token);
        var result = new List<ImMemberDto>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result.Add(new ImMemberDto(
                reader.GetString("UserId").Trim(),
                reader.GetString("EmployeeName").Trim(),
                reader.GetString("Role").Trim()));
        }

        return result;
    }

    public async Task<IReadOnlyList<ImConversationDto>> GetMyConversationsAsync(string userId, CancellationToken token)
    {
        const string sql = """
            SELECT c.Id, c.ConversationType, c.Name, c.LastMessageSeq, c.LastMessageAt, c.LastMessagePreview,
                   m.IsMuted, m.IsArchived, m.LastReadMessageSeq,
                   CASE WHEN c.OwnerUserId = @UserId THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS CanManage,
                   (SELECT COUNT(*) FROM dbo.im_conversation_members a
                    WHERE a.ConversationId = c.Id AND a.LeftAt IS NULL) AS MemberCount,
                   (SELECT TOP (1) n.EMP_NAME
                    FROM dbo.im_conversation_members o
                    JOIN EOS.ERP.dbo.SYSDL u WITH (NOLOCK) ON LTRIM(RTRIM(u.USER_ID)) = o.UserId
                    JOIN EOS.ERP.dbo.SYSDN n WITH (NOLOCK) ON n.EMP_ID = u.EMP_ID
                    WHERE o.ConversationId = c.Id AND o.UserId <> @UserId AND o.LeftAt IS NULL) AS DirectName
            FROM dbo.im_conversations c
            JOIN dbo.im_conversation_members m ON m.ConversationId = c.Id AND m.UserId = @UserId AND m.LeftAt IS NULL
            ORDER BY c.LastMessageAt DESC;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 64).Value = userId.Trim();
        await connection.OpenAsync(token);
        var result = new List<ImConversationDto>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var lastMessageSeq = reader.GetInt64("LastMessageSeq");
            var lastReadSeq = reader.GetInt64("LastReadMessageSeq");
            var type = reader.GetString("ConversationType").Trim() == "Group"
                ? ImConversationType.Group
                : ImConversationType.Direct;
            var name = reader.GetNullableString("Name");
            if (type == ImConversationType.Direct)
            {
                name = reader.GetNullableString("DirectName") ?? name;
            }

            result.Add(new ImConversationDto(
                reader.GetInt64("Id"),
                type,
                name,
                lastMessageSeq,
                reader.IsDBNull(reader.GetOrdinal("LastMessageAt"))
                    ? null
                    : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime("LastMessageAt"), DateTimeKind.Utc)),
                reader.GetNullableString("LastMessagePreview"),
                Math.Max(0, lastMessageSeq - lastReadSeq),
                reader.GetInt32("MemberCount"),
                reader.GetBoolean("IsMuted"),
                reader.GetBoolean("IsArchived"),
                reader.GetBoolean("CanManage")));
        }

        return result;
    }

    public async Task<IReadOnlyList<ImContactDto>> SearchUsersAsync(string keyword, int limit, CancellationToken token)
    {
        var pattern = $"%{keyword?.Trim() ?? string.Empty}%";
        const string sql = """
            SELECT TOP (@Limit) u.USER_ID, n.EMP_NAME, EOS.ERP.dbo.f_get_dept_desc(n.DEPT_ID) AS DEPT_DESC
            FROM EOS.ERP.dbo.SYSDL u WITH (NOLOCK)
            JOIN EOS.ERP.dbo.SYSDN n WITH (NOLOCK) ON n.EMP_ID = u.EMP_ID
            WHERE u.ACTIVE_TAG = 1
              AND (LTRIM(RTRIM(u.USER_ID)) LIKE @Pattern OR n.EMP_NAME LIKE @Pattern)
            ORDER BY n.EMP_NAME;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = Math.Clamp(limit, 1, 50);
        command.Parameters.Add("@Pattern", SqlDbType.NVarChar, 100).Value = pattern;
        await connection.OpenAsync(token);
        var result = new List<ImContactDto>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result.Add(new ImContactDto(
                reader.GetString("USER_ID").Trim(),
                reader.GetString("EMP_NAME").Trim(),
                reader.GetNullableString("DEPT_DESC")?.Trim()));
        }

        return result;
    }

    public async Task AuditAsync(
        long? conversationId,
        long? messageId,
        string actorUserId,
        string actionType,
        string? detail,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.im_audit_log (ConversationId, MessageId, ActorUserId, ActionType, Detail, CreatedAt)
            VALUES (@ConversationId, @MessageId, @Actor, @Action, @Detail, SYSUTCDATETIME());
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId is null
            ? DBNull.Value : conversationId.Value;
        command.Parameters.Add("@MessageId", SqlDbType.BigInt).Value = messageId is null
            ? DBNull.Value : messageId.Value;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 64).Value = actorUserId.Trim();
        command.Parameters.Add("@Action", SqlDbType.NVarChar, 32).Value = actionType;
        command.Parameters.Add("@Detail", SqlDbType.NVarChar).Value = detail is null ? DBNull.Value : detail;
        await connection.OpenAsync(token);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertMemberAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long conversationId,
        string userId,
        CancellationToken token,
        string role = "Member")
    {
        const string sql = """
            INSERT INTO dbo.im_conversation_members (ConversationId, UserId, Role, JoinedAt)
            VALUES (@ConversationId, @UserId, @Role, SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 64).Value = userId;
        command.Parameters.Add("@Role", SqlDbType.NVarChar, 16).Value = role;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task WriteAuditAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long? conversationId,
        long? messageId,
        string actorUserId,
        string actionType,
        string? detail,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.im_audit_log (ConversationId, MessageId, ActorUserId, ActionType, Detail, CreatedAt)
            VALUES (@ConversationId, @MessageId, @Actor, @Action, @Detail, SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId is null
            ? DBNull.Value : conversationId.Value;
        command.Parameters.Add("@MessageId", SqlDbType.BigInt).Value = messageId is null
            ? DBNull.Value : messageId.Value;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 64).Value = actorUserId;
        command.Parameters.Add("@Action", SqlDbType.NVarChar, 32).Value = actionType;
        command.Parameters.Add("@Detail", SqlDbType.NVarChar).Value = detail is null ? DBNull.Value : detail;
        await command.ExecuteNonQueryAsync(token);
    }

    private static string EscapeJson(string value)
        => Services.ImJson.Serialize(value).Trim('"');
}
