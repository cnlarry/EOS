using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>ASSISTANT_SESSION 行（ADR-007 §7）。</summary>
public sealed record AssistantSessionDto(
    long Id,
    string UserId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt);

/// <summary>ASSISTANT_MESSAGE 行。</summary>
public sealed record AssistantMessageDto(
    long Id,
    long SessionId,
    int Role, // 1=USER 2=ASSISTANT 3=SYSTEM
    string Content,
    string? ModelName,
    int? PromptTokens,
    int? CompletionTokens,
    int? ElapsedMs,
    string? CorrelationId,
    DateTimeOffset CreatedAt);

/// <summary>
/// 工作助手会话/消息持久化契约（ADR-007 §7）。所有读写按 USER_ID 强制隔离；
/// 越权会话的写入抛 UnauthorizedAccessException，读取返回空/null。
/// </summary>
public interface IAssistantRepository
{
    Task<AssistantSessionDto> CreateSessionAsync(string userId, CancellationToken token);

    Task<IReadOnlyList<AssistantSessionDto>> ListSessionsAsync(string userId, int limit, CancellationToken token);

    /// <summary>归属校验 + 取单个会话；不存在或非本人返回 null。</summary>
    Task<AssistantSessionDto?> GetSessionAsync(string userId, long sessionId, CancellationToken token);

    /// <summary>删除会话及其全部消息；均带 USER_ID 归属条件。返回受影响行数。</summary>
    Task<int> DeleteSessionAsync(string userId, long sessionId, CancellationToken token);

    Task<IReadOnlyList<AssistantMessageDto>> ListMessagesAsync(string userId, long sessionId, CancellationToken token);

    /// <summary>追加用户消息并把会话活跃时间前移；无任何历史时用首条内容作标题。</summary>
    Task<AssistantMessageDto> AddUserMessageAsync(
        string userId, long sessionId, string content, string correlationId, CancellationToken token);

    /// <summary>追加助手回复（含用量统计）。</summary>
    Task<AssistantMessageDto> AddAssistantMessageAsync(
        string userId, long sessionId, string content, string modelName,
        int? promptTokens, int? completionTokens, int? elapsedMs, string correlationId,
        CancellationToken token);

    /// <summary>组装模型上下文用的最近 N 条历史（正序返回）；归属校验在 SQL 内完成。</summary>
    Task<IReadOnlyList<(int Role, string Content)>> LoadRecentHistoryAsync(
        string userId, long sessionId, int maxMessages, CancellationToken token);

    /// <summary>回填最终回复行使用的工具调用摘要（归属校验在 SQL 内完成）。</summary>
    Task UpdateToolCallsJsonAsync(string userId, long messageId, string toolCallsJson, CancellationToken token);
}

/// <summary>ASSISTANT_SESSION/ASSISTANT_MESSAGE 的 SQL Server 实现（EOS.ERP 唯一业务库）。</summary>
public sealed class AssistantRepository(DbConnectionFactory connections) : IAssistantRepository
{
    public async Task<AssistantSessionDto> CreateSessionAsync(string userId, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.ASSISTANT_SESSION (USER_ID)
            OUTPUT INSERTED.ID, INSERTED.USER_ID, INSERTED.TITLE, INSERTED.CREATED_AT, INSERTED.LAST_ACTIVE_AT
            VALUES (@UserId);
            """;
        return await QuerySingleSession(sql, cmd => cmd.Parameters.AddWithValue("@UserId", userId), token)
               ?? throw new InvalidOperationException("创建助手会话失败。");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantSessionDto>> ListSessionsAsync(string userId, int limit, CancellationToken token)
    {
        const string sql = """
            SELECT TOP (@Limit) ID, USER_ID, TITLE, CREATED_AT, LAST_ACTIVE_AT
            FROM dbo.ASSISTANT_SESSION WITH (NOLOCK)
            WHERE USER_ID = @UserId
            ORDER BY LAST_ACTIVE_AT DESC;
            """;
        var items = new List<AssistantSessionDto>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Limit", Math.Clamp(limit, 1, 100));
        cmd.Parameters.AddWithValue("@UserId", userId);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            items.Add(ReadSession(reader));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<AssistantSessionDto?> GetSessionAsync(string userId, long sessionId, CancellationToken token)
    {
        const string sql = """
            SELECT ID, USER_ID, TITLE, CREATED_AT, LAST_ACTIVE_AT
            FROM dbo.ASSISTANT_SESSION WITH (NOLOCK)
            WHERE ID = @Id AND USER_ID = @UserId;
            """;
        return await QuerySingleSession(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@Id", sessionId);
            cmd.Parameters.AddWithValue("@UserId", userId);
        }, token);
    }

    /// <inheritdoc />
    public async Task<int> DeleteSessionAsync(string userId, long sessionId, CancellationToken token)
    {
        const string sql = """
            DELETE FROM dbo.ASSISTANT_MESSAGE
            WHERE SESSION_ID IN (SELECT ID FROM dbo.ASSISTANT_SESSION WHERE ID = @Id AND USER_ID = @UserId);
            DELETE FROM dbo.ASSISTANT_SESSION WHERE ID = @Id AND USER_ID = @UserId;
            SELECT @@ROWCOUNT AS Deleted;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", sessionId);
        cmd.Parameters.AddWithValue("@UserId", userId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantMessageDto>> ListMessagesAsync(string userId, long sessionId, CancellationToken token)
    {
        const string sql = """
            SELECT m.ID, m.SESSION_ID, m.ROLE, m.CONTENT, m.MODEL_NAME,
                   m.PROMPT_TOKENS, m.COMPLETION_TOKENS, m.ELAPSED_MS, m.CORRELATION_ID, m.CREATED_AT
            FROM dbo.ASSISTANT_MESSAGE m WITH (NOLOCK)
            INNER JOIN dbo.ASSISTANT_SESSION s WITH (NOLOCK) ON s.ID = m.SESSION_ID
            WHERE s.ID = @SessionId AND s.USER_ID = @UserId
            ORDER BY m.ID;
            """;
        var items = new List<AssistantMessageDto>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@SessionId", sessionId);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            items.Add(ReadMessage(reader));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<AssistantMessageDto> AddUserMessageAsync(
        string userId, long sessionId, string content, string correlationId, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.ASSISTANT_SESSION SET
                TITLE = CASE WHEN EXISTS (
                             SELECT 1 FROM dbo.ASSISTANT_MESSAGE WITH (NOLOCK)
                             WHERE SESSION_ID = dbo.ASSISTANT_SESSION.ID)
                             THEN TITLE
                             ELSE LEFT(@Content, 60) END,
                LAST_ACTIVE_AT = SYSUTCDATETIME()
            WHERE ID = @SessionId AND USER_ID = @UserId;
            INSERT INTO dbo.ASSISTANT_MESSAGE (SESSION_ID, ROLE, CONTENT, CORRELATION_ID)
            OUTPUT INSERTED.ID, INSERTED.SESSION_ID, INSERTED.ROLE, INSERTED.CONTENT,
                   INSERTED.MODEL_NAME, INSERTED.PROMPT_TOKENS, INSERTED.COMPLETION_TOKENS,
                   INSERTED.ELAPSED_MS, INSERTED.CORRELATION_ID, INSERTED.CREATED_AT
            SELECT @SessionId, 1, @Content, @CorrelationId
            WHERE EXISTS (SELECT 1 FROM dbo.ASSISTANT_SESSION WHERE ID = @SessionId AND USER_ID = @UserId);
            """;
        return await QuerySingleMessage(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@SessionId", sessionId);
            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@Content", content);
            cmd.Parameters.AddWithValue("@CorrelationId", correlationId);
        }, token)
        ?? throw new UnauthorizedAccessException("会话不存在或不属于当前用户。");
    }

    /// <inheritdoc />
    public async Task<AssistantMessageDto> AddAssistantMessageAsync(
        string userId, long sessionId, string content, string modelName,
        int? promptTokens, int? completionTokens, int? elapsedMs, string correlationId,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.ASSISTANT_MESSAGE (SESSION_ID, ROLE, CONTENT, MODEL_NAME,
                PROMPT_TOKENS, COMPLETION_TOKENS, ELAPSED_MS, CORRELATION_ID)
            OUTPUT INSERTED.ID, INSERTED.SESSION_ID, INSERTED.ROLE, INSERTED.CONTENT,
                   INSERTED.MODEL_NAME, INSERTED.PROMPT_TOKENS, INSERTED.COMPLETION_TOKENS,
                   INSERTED.ELAPSED_MS, INSERTED.CORRELATION_ID, INSERTED.CREATED_AT
            SELECT @SessionId, 2, @Content, @ModelName, @PromptTokens, @CompletionTokens, @ElapsedMs, @CorrelationId
            WHERE EXISTS (SELECT 1 FROM dbo.ASSISTANT_SESSION WHERE ID = @SessionId AND USER_ID = @UserId);
            """;
        return await QuerySingleMessage(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@SessionId", sessionId);
            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@Content", content);
            cmd.Parameters.AddWithValue("@ModelName", modelName);
            AddNullable(cmd, "@PromptTokens", promptTokens);
            AddNullable(cmd, "@CompletionTokens", completionTokens);
            AddNullable(cmd, "@ElapsedMs", elapsedMs);
            cmd.Parameters.AddWithValue("@CorrelationId", correlationId);
        }, token)
        ?? throw new UnauthorizedAccessException("会话不存在或不属于当前用户。");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(int Role, string Content)>> LoadRecentHistoryAsync(
        string userId, long sessionId, int maxMessages, CancellationToken token)
    {
        const string sql = """
            SELECT TOP (@Max) ROLE, CONTENT
            FROM dbo.ASSISTANT_MESSAGE WITH (NOLOCK)
            WHERE SESSION_ID = @SessionId
              AND EXISTS (SELECT 1 FROM dbo.ASSISTANT_SESSION WITH (NOLOCK)
                          WHERE ID = @SessionId AND USER_ID = @UserId)
            ORDER BY ID DESC;
            """;
        var items = new List<(int Role, string Content)>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Max", maxMessages);
        cmd.Parameters.AddWithValue("@SessionId", sessionId);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            items.Add((Convert.ToInt32(reader.GetValue(0)), reader.GetString(1))); // ROLE 为 TINYINT
        }

        items.Reverse(); // DESC 取最近 N 条后恢复正序
        return items;
    }

    /// <inheritdoc />
    public async Task UpdateToolCallsJsonAsync(string userId, long messageId, string toolCallsJson, CancellationToken token)
    {
        const string sql = """
            UPDATE m SET m.TOOL_CALLS_JSON = @Json
            FROM dbo.ASSISTANT_MESSAGE m
            INNER JOIN dbo.ASSISTANT_SESSION s WITH (NOLOCK) ON s.ID = m.SESSION_ID
            WHERE m.ID = @Id AND s.USER_ID = @UserId;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@Json", System.Data.SqlDbType.NVarChar, -1).Value = toolCallsJson;
        cmd.Parameters.AddWithValue("@Id", messageId);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static void AddNullable(SqlCommand cmd, string name, int? value)
    {
        if (value is null) cmd.Parameters.Add(name, System.Data.SqlDbType.Int).Value = DBNull.Value;
        else cmd.Parameters.Add(name, System.Data.SqlDbType.Int).Value = value.Value;
    }

    private async Task<AssistantSessionDto?> QuerySingleSession(string sql, Action<SqlCommand> bind, CancellationToken token)
    {
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadSession(reader) : null;
    }

    private async Task<AssistantMessageDto?> QuerySingleMessage(string sql, Action<SqlCommand> bind, CancellationToken token)
    {
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadMessage(reader) : null;
    }

    /// <summary>库内 DATETIME2(3) 由 SYSUTCDATETIME 写入（UTC）；Kind=Unspecified 按 UTC 解读。</summary>
    private static DateTimeOffset ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? new DateTimeOffset(value) : new DateTimeOffset(value, TimeSpan.Zero);

    private static AssistantSessionDto ReadSession(SqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        ToUtc(reader.GetDateTime(3)),
        ToUtc(reader.GetDateTime(4)));

    private static AssistantMessageDto ReadMessage(SqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        Convert.ToInt32(reader.GetValue(2)), // ROLE 为 TINYINT，SqlDataReader 返回 Byte
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        ToUtc(reader.GetDateTime(9)));
}
