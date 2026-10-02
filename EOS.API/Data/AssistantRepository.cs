using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 会话列表的过滤视图：在列 / 只看已归档 / 两者都要。
/// 三态而不是"是否含归档"两态——管理页要单独看已归档（删除只在那里开放），
/// 而"已归档"若靠前端从含归档的结果里筛，分页与总数就都不准了。
/// </summary>
public enum AssistantSessionListState
{
    /// <summary>只看在列的（默认）。</summary>
    Active = 0,

    /// <summary>只看已归档的。</summary>
    Archived = 1,

    /// <summary>两者都要。</summary>
    All = 2,
}

/// <summary>ASSISTANT_SESSION 行。</summary>
public sealed record AssistantSessionDto(
    long Id,
    string UserId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt,
    /// <summary>归档时刻；null = 在列。归档是"从列表里收起来但留住历史"，与删除是两回事。</summary>
    DateTimeOffset? ArchivedAt = null,
    /// <summary>消息条数。只有列表查询会聚合出来（单条查询与插入回显为 0）。</summary>
    int MessageCount = 0,
    /// <summary>该会话累计消耗的 token（prompt + completion 之和）。同上，只有列表查询会算。</summary>
    long MessageTokens = 0,
    /// <summary>
    /// 归属用户的**姓名**（经 SYSDL.EMP_ID → SYSDN.EMP_NAME 取）。只有管理侧列表会带出来：
    /// `USER_ID` 是账号/编号，看列表的人需要的是"这是谁的会话"。取不到（账号不在 SYSDL 里，
    /// 例如测试账号）就为 null，界面回落显示账号本身。
    /// </summary>
    string? EmployeeName = null);

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
    DateTimeOffset CreatedAt,
    /// <summary>本次回复用过的工具摘要（库内 JSON）；只有历史查询会带出，插入回显时为 null。</summary>
    string? ToolCallsJson = null,
    /// <summary>用户反馈：1 = 赞，-1 = 踩，null = 没反馈（与 0 同义）。只有历史查询会带出。</summary>
    int? Feedback = null,
    /// <summary>踩的原因（受控短文本）。赞或没反馈时为 null。</summary>
    string? FeedbackReason = null,
    /// <summary>
    /// 该回复的完成原因（厂商语义，如 <c>length</c>）。<c>length</c> = 被输出上限截断，
    /// 界面据此提示"回答未写完"——只靠当次流式事件的话，刷新会话后这条提示就没了。
    /// </summary>
    string? FinishReason = null);

/// <summary>
/// 工作助手会话/消息持久化契约。所有读写按 USER_ID 强制隔离；
/// 越权会话的写入抛 UnauthorizedAccessException，读取返回空/null。
/// </summary>
public interface IAssistantRepository
{
    Task<AssistantSessionDto> CreateSessionAsync(string userId, CancellationToken token);

    /// <summary>
    /// 分页列会话：<paramref name="state"/> 决定看哪些（在列 / 已归档 / 全部），
    /// <paramref name="keyword"/> 只搜标题。返回当页数据与过滤后的**总数**（供分页器用）。
    /// </summary>
    Task<(IReadOnlyList<AssistantSessionDto> Items, int Total)> ListSessionsAsync(
        string userId, int offset, int limit, AssistantSessionListState state, string? keyword, CancellationToken token);

    /// <summary>重命名会话（归属校验在 SQL 内完成）。返回受影响行数。</summary>
    Task<int> RenameSessionAsync(string userId, long sessionId, string title, CancellationToken token);

    /// <summary>归档 / 取消归档会话（幂等；归属校验在 SQL 内完成）。返回受影响行数。</summary>
    Task<int> ArchiveSessionAsync(string userId, long sessionId, bool archived, CancellationToken token);

    /// <summary>归属校验 + 取单个会话；不存在或非本人返回 null。</summary>
    Task<AssistantSessionDto?> GetSessionAsync(string userId, long sessionId, CancellationToken token);

    /// <summary>
    /// 删除会话及其全部消息。均带 USER_ID 归属条件，**并且只删已归档的会话**
    /// （服务端强制，防止绕过界面误删在列会话）。返回受影响行数。
    /// </summary>
    Task<int> DeleteSessionAsync(string userId, long sessionId, CancellationToken token);

    Task<IReadOnlyList<AssistantMessageDto>> ListMessagesAsync(string userId, long sessionId, CancellationToken token);

    /// <summary>追加用户消息并把会话活跃时间前移；无任何历史时用首条内容作标题。</summary>
    Task<AssistantMessageDto> AddUserMessageAsync(
        string userId, long sessionId, string content, string correlationId, CancellationToken token);

    /// <summary>追加助手回复（含用量统计；estimated 表示用量为服务端保守估算）。</summary>
    Task<AssistantMessageDto> AddAssistantMessageAsync(
        string userId, long sessionId, string content, string modelName,
        int? promptTokens, int? completionTokens, int? elapsedMs, string correlationId,
        CancellationToken token, bool estimated = false, string? finishReason = null);

    /// <summary>
    /// 记录 / 取消一条助手回复的反馈（1 赞 / -1 踩 / null 取消）。归属校验在 SQL 内完成，
    /// 且只对助手消息（ROLE = 2）生效——给自己的提问点赞没有意义。返回受影响行数。
    /// </summary>
    Task<int> SetMessageFeedbackAsync(
        string userId, long messageId, int? feedback, string? reason, CancellationToken token);

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
    public async Task<(IReadOnlyList<AssistantSessionDto> Items, int Total)> ListSessionsAsync(
        string userId, int offset, int limit, AssistantSessionListState state, string? keyword, CancellationToken token)
    {
        // 会话管理页要显示"这条会话有多少条消息"（判断哪些值得留下），所以在同一查询里聚合。
        // 关键词只搜标题——会话没有别的人类可读字段。
        const string filter = """
            FROM dbo.ASSISTANT_SESSION s WITH (NOLOCK)
            WHERE s.USER_ID = @UserId
              AND ((@State = 0 AND s.ARCHIVED_AT IS NULL)
                OR (@State = 1 AND s.ARCHIVED_AT IS NOT NULL)
                OR @State = 2)
              AND (@Keyword IS NULL OR s.TITLE LIKE @Keyword ESCAPE '\')
            """;
        const string sql = $"""
            SELECT s.ID, s.USER_ID, s.TITLE, s.CREATED_AT, s.LAST_ACTIVE_AT, s.ARCHIVED_AT,
                   (SELECT COUNT(*) FROM dbo.ASSISTANT_MESSAGE m WITH (NOLOCK) WHERE m.SESSION_ID = s.ID)
            {filter}
            ORDER BY s.LAST_ACTIVE_AT DESC, s.ID DESC
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
            """;
        // 单独取总数：COUNT(*) OVER() 在"页越界返回空页"时拿不到总数，分页器会显示成 0。
        const string countSql = $"SELECT COUNT(*) {filter};";

        // 用户输入里的 LIKE 元字符要转义，否则搜 "100%" 会变成通配符匹配。
        var pattern = string.IsNullOrWhiteSpace(keyword)
            ? null
            : $"%{keyword.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[")}%";

        void Bind(SqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@State", (int)state);
            cmd.Parameters.AddWithValue("@Keyword", (object?)pattern ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Offset", Math.Max(offset, 0));
            cmd.Parameters.AddWithValue("@Limit", Math.Clamp(limit, 1, 200));
        }

        var items = new List<AssistantSessionDto>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);

        await using (var cmd = new SqlCommand(sql, conn))
        {
            Bind(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                items.Add(ReadSession(reader));
            }
        }

        int total;
        await using (var cmd = new SqlCommand(countSql, conn))
        {
            Bind(cmd);
            total = Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
        }

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<AssistantSessionDto?> GetSessionAsync(string userId, long sessionId, CancellationToken token)
    {
        const string sql = """
            SELECT ID, USER_ID, TITLE, CREATED_AT, LAST_ACTIVE_AT, ARCHIVED_AT
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
        // 守卫：只删已归档的会话。控制器已挡了一层，这里再挡一层——"取消归档"与"删除"并发交错时，
        // SQL 里的条件才是最终裁决（否则可能删掉刚被取消归档的会话）。
        const string sql = """
            DELETE FROM dbo.ASSISTANT_MESSAGE
            WHERE SESSION_ID IN (
                SELECT ID FROM dbo.ASSISTANT_SESSION
                WHERE ID = @Id AND USER_ID = @UserId AND ARCHIVED_AT IS NOT NULL);
            DELETE FROM dbo.ASSISTANT_SESSION
            WHERE ID = @Id AND USER_ID = @UserId AND ARCHIVED_AT IS NOT NULL;
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
    public async Task<int> RenameSessionAsync(string userId, long sessionId, string title, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.ASSISTANT_SESSION SET TITLE = @Title
            WHERE ID = @Id AND USER_ID = @UserId;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Title", title);
        cmd.Parameters.AddWithValue("@Id", sessionId);
        cmd.Parameters.AddWithValue("@UserId", userId);
        return await cmd.ExecuteNonQueryAsync(token);
    }

    /// <inheritdoc />
    public async Task<int> ArchiveSessionAsync(string userId, long sessionId, bool archived, CancellationToken token)
    {
        // 归档是"收起来"而不是"删掉"：只置/清 ARCHIVED_AT，消息一行不动。
        const string sql = """
            UPDATE dbo.ASSISTANT_SESSION
            SET ARCHIVED_AT = CASE WHEN @Archived = 1 THEN SYSUTCDATETIME() ELSE NULL END
            WHERE ID = @Id AND USER_ID = @UserId;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Archived", archived ? 1 : 0);
        cmd.Parameters.AddWithValue("@Id", sessionId);
        cmd.Parameters.AddWithValue("@UserId", userId);
        return await cmd.ExecuteNonQueryAsync(token);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantMessageDto>> ListMessagesAsync(string userId, long sessionId, CancellationToken token)
    {
        const string sql = """
            SELECT m.ID, m.SESSION_ID, m.ROLE, m.CONTENT, m.MODEL_NAME,
                   m.PROMPT_TOKENS, m.COMPLETION_TOKENS, m.ELAPSED_MS, m.CORRELATION_ID, m.CREATED_AT,
                   m.TOOL_CALLS_JSON, m.FEEDBACK, m.FEEDBACK_REASON, m.FINISH_REASON
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
        CancellationToken token, bool estimated = false, string? finishReason = null)
    {
        const string sql = """
            INSERT INTO dbo.ASSISTANT_MESSAGE (SESSION_ID, ROLE, CONTENT, MODEL_NAME,
                PROMPT_TOKENS, COMPLETION_TOKENS, ELAPSED_MS, CORRELATION_ID, IS_ESTIMATED, FINISH_REASON)
            OUTPUT INSERTED.ID, INSERTED.SESSION_ID, INSERTED.ROLE, INSERTED.CONTENT,
                    INSERTED.MODEL_NAME, INSERTED.PROMPT_TOKENS, INSERTED.COMPLETION_TOKENS,
                    INSERTED.ELAPSED_MS, INSERTED.CORRELATION_ID, INSERTED.CREATED_AT
            SELECT @SessionId, 2, @Content, @ModelName, @PromptTokens, @CompletionTokens, @ElapsedMs, @CorrelationId, @Estimated,
                   @FinishReason
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
            cmd.Parameters.Add("@Estimated", System.Data.SqlDbType.Bit).Value = estimated;
            cmd.Parameters.Add("@FinishReason", System.Data.SqlDbType.NVarChar, 32).Value =
                (object?)finishReason ?? DBNull.Value;
        }, token)
        ?? throw new UnauthorizedAccessException("会话不存在或不属于当前用户。");
    }

    /// <inheritdoc />
    public async Task<int> SetMessageFeedbackAsync(
        string userId, long messageId, int? feedback, string? reason, CancellationToken token)
    {
        // 「赞」一律不留原因（界面上也没有输入口），「取消」把两个字段一起清掉——
        // 否则撤回之后库里还留着一条孤儿原因，看上去像"曾经踩过但没记录方向"。
        const string sql = """
            UPDATE m SET
                m.FEEDBACK = @Feedback,
                m.FEEDBACK_REASON = CASE WHEN @Feedback = 1 THEN NULL ELSE @Reason END,
                m.FEEDBACK_AT = CASE WHEN @Feedback IS NULL THEN NULL ELSE SYSUTCDATETIME() END
            FROM dbo.ASSISTANT_MESSAGE m
            INNER JOIN dbo.ASSISTANT_SESSION s WITH (NOLOCK) ON s.ID = m.SESSION_ID
            WHERE m.ID = @Id AND s.USER_ID = @UserId AND m.ROLE = 2;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@Feedback", System.Data.SqlDbType.SmallInt).Value = (object?)feedback ?? DBNull.Value;
        cmd.Parameters.Add("@Reason", System.Data.SqlDbType.NVarChar, 200).Value = (object?)reason ?? DBNull.Value;
        cmd.Parameters.AddWithValue("@Id", messageId);
        cmd.Parameters.AddWithValue("@UserId", userId);
        return await cmd.ExecuteNonQueryAsync(token);
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

    /// <summary>
    /// 读一行会话。<c>internal</c> 供管理侧仓储复用：两侧的 SELECT 列完全一致
    /// （见 <see cref="AssistantAdminRepository"/>），复制一份容易在加列时漏改一处。
    /// </summary>
    internal static AssistantSessionDto ReadSession(SqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        ToUtc(reader.GetDateTime(3)),
        ToUtc(reader.GetDateTime(4)),
        // 末几列只有列表查询会 SELECT（插入回显走的 OUTPUT、单条查询都不带），故按列数存在与否取值：
        // 个人侧带 7 列（到消息数），管理侧带 9 列（另有 token 与归属用户姓名）。
        reader.FieldCount > 5 && !reader.IsDBNull(5) ? ToUtc(reader.GetDateTime(5)) : null,
        reader.FieldCount > 6 ? reader.GetInt32(6) : 0,
        reader.FieldCount > 7 ? Convert.ToInt64(reader.GetValue(7)) : 0L,
        reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null);

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
        ToUtc(reader.GetDateTime(9)),
        // 工具摘要、反馈与截断原因只有历史查询会 SELECT（插入/回填走的 OUTPUT 不带它们），
        // 故一律按列数存在与否取值——插入回显时它们是 null，而不是"读到了别的列"。
        reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetString(10) : null,
        reader.FieldCount > 11 && !reader.IsDBNull(11) ? Convert.ToInt32(reader.GetValue(11)) : null,
        reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null,
        reader.FieldCount > 13 && !reader.IsDBNull(13) ? reader.GetString(13) : null);
}
