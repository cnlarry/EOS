using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Assistant;

/// <summary>
/// 会话列表的排序键。
///
/// <para>
/// 用枚举而不是"传列名进来拼 SQL"：排序列由服务端固定映射，**没有注入面**，
/// 未知取值在控制器里就退化成默认值（与 state 同口径）。
/// </para>
/// </summary>
public enum AssistantSessionSort
{
    /// <summary>最近活跃（默认，降序）。</summary>
    LastActive = 0,

    /// <summary>创建时间。</summary>
    Created = 1,

    /// <summary>标题。</summary>
    Title = 2,

    /// <summary>消息数。</summary>
    Messages = 3,

    /// <summary>累计 token 消耗。</summary>
    Tokens = 4,
}

/// <summary>
/// 工作助手会话的**管理侧**读写（菜单组 31 / 模块 3101，见 ADR-030 §4）。
///
/// <para>
/// 与 <see cref="IAssistantRepository"/> 的关键差别：**这里不按 USER_ID 隔离**——它面向管理员，
/// 要能看到全系统的会话。因此它是**独立的一套接口与实现**，不去改个人侧的语义：
/// 个人侧那条"所有读写按 USER_ID 强制隔离"的契约仍然成立，改动那里会同时破坏它的集成测试。
/// </para>
///
/// <para>
/// **权限门不在这里**：调用方（<see cref="Controllers.AssistantAdminController"/>）必须先过
/// 3101 的 CanBrowse / CanEdit。仓储只负责数据，不猜调用者是谁。
/// </para>
///
/// <para>
/// **不看正文**（ADR-030 §2）：这里只出会话元数据，不提供按会话取消息的方法——要看正文是另一个
/// 需要独立权限位与审计的决定。
/// </para>
/// </summary>
public interface IAssistantAdminRepository
{
    /// <summary>
    /// 跨用户分页列会话（<paramref name="owner"/> 非空时只看该用户的）。
    /// 排序走 <paramref name="sort"/> ＋ <paramref name="ascending"/>：列表是**服务端分页**的，
    /// 排序也必须在服务端做，否则只会排到当前这一页。
    /// </summary>
    Task<(IReadOnlyList<AssistantSessionDto> Items, int Total)> ListSessionsAsync(
        int offset, int limit, AssistantSessionListState state, string? keyword, string? owner,
        AssistantSessionSort sort, bool ascending, CancellationToken token);

    /// <summary>出现在会话表里的全部归属用户（供管理页的"按用户筛选"下拉）。</summary>
    Task<IReadOnlyList<string>> ListOwnersAsync(CancellationToken token);

    /// <summary>归档 / 取消归档任意用户的会话（幂等）。返回受影响行数。</summary>
    Task<int> ArchiveSessionAsync(long sessionId, bool archived, CancellationToken token);

    /// <summary>
    /// 永久删除任意用户的会话及其消息。**仍然只删已归档的**（与个人侧同一口径，
    /// 见 ADR-030 §4）：先归档再删除是两步，避免在管理列表里手滑删掉在用的会话。
    /// </summary>
    Task<int> DeleteSessionAsync(long sessionId, CancellationToken token);
}

/// <summary>管理侧会话读写的 SQL Server 实现。</summary>
public sealed class AssistantAdminRepository(DbConnectionFactory connections) : IAssistantAdminRepository
{
    /// <summary>消息数的聚合子查询：列表要显示它，按它排序时也要用同一份定义。</summary>
    private const string MessageCountExpr =
        "(SELECT COUNT(*) FROM dbo.ASSISTANT_MESSAGE m WITH (NOLOCK) WHERE m.SESSION_ID = s.ID)";

    /// <summary>会话累计 token 的聚合子查询（prompt + completion，任一为 null 按 0 计）。</summary>
    private const string MessageTokensExpr =
        "(SELECT ISNULL(SUM(ISNULL(m.PROMPT_TOKENS, 0) + ISNULL(m.COMPLETION_TOKENS, 0)), 0) "
        + "FROM dbo.ASSISTANT_MESSAGE m WITH (NOLOCK) WHERE m.SESSION_ID = s.ID)";

    /// <summary>
    /// 排序列的服务端映射。**列名永远来自这里**，调用方只能传枚举，
    /// 所以不存在"把用户输入拼进 ORDER BY"的注入面。
    /// </summary>
    private static string OrderByClause(AssistantSessionSort sort, bool ascending) => (sort, ascending) switch
    {
        (AssistantSessionSort.Title, true) => "s.TITLE ASC",
        (AssistantSessionSort.Title, false) => "s.TITLE DESC",
        (AssistantSessionSort.Created, true) => "s.CREATED_AT ASC",
        (AssistantSessionSort.Created, false) => "s.CREATED_AT DESC",
        (AssistantSessionSort.Messages, true) => $"{MessageCountExpr} ASC",
        (AssistantSessionSort.Messages, false) => $"{MessageCountExpr} DESC",
        (AssistantSessionSort.Tokens, true) => $"{MessageTokensExpr} ASC",
        (AssistantSessionSort.Tokens, false) => $"{MessageTokensExpr} DESC",
        (_, true) => "s.LAST_ACTIVE_AT ASC",
        // 默认：最近活跃在前。ID 兜底，保证同值行的顺序稳定（分页才不会漏行或重行）
        _ => "s.LAST_ACTIVE_AT DESC",
    };

    /// <inheritdoc />
    public async Task<(IReadOnlyList<AssistantSessionDto> Items, int Total)> ListSessionsAsync(
        int offset, int limit, AssistantSessionListState state, string? keyword, string? owner,
        AssistantSessionSort sort, bool ascending, CancellationToken token)
    {
        // 与个人侧同构，只有一处刻意不同：**没有 s.USER_ID = @UserId**，换成可选的 @Owner 过滤。
        // 两个 LEFT JOIN 取归属用户姓名（USER_ID → SYSDL.EMP_ID → SYSDN.EMP_NAME）：都是 1:1，
        // 不会放大行数，COUNT 也照样准；账号不在 SYSDL 里（如测试账号）时姓名为 null，界面回落显示账号。
        const string filter = """
            FROM dbo.ASSISTANT_SESSION s WITH (NOLOCK)
            LEFT JOIN dbo.SYSDL d WITH (NOLOCK) ON d.USER_ID = s.USER_ID
            LEFT JOIN dbo.SYSDN n WITH (NOLOCK) ON n.EMP_ID = d.EMP_ID
            WHERE ((@State = 0 AND s.ARCHIVED_AT IS NULL)
                OR (@State = 1 AND s.ARCHIVED_AT IS NOT NULL)
                OR @State = 2)
              AND (@Keyword IS NULL OR s.TITLE LIKE @Keyword ESCAPE '\')
              AND (@Owner IS NULL OR s.USER_ID = @Owner)
            """;
        var sql = $"""
            SELECT s.ID, s.USER_ID, s.TITLE, s.CREATED_AT, s.LAST_ACTIVE_AT, s.ARCHIVED_AT,
                   {MessageCountExpr}, {MessageTokensExpr}, n.EMP_NAME
            {filter}
            ORDER BY {OrderByClause(sort, ascending)}, s.ID DESC
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
            """;
        const string countSql = $"SELECT COUNT(*) {filter};";

        // 用户输入里的 LIKE 元字符要转义，否则搜 "100%" 会变成通配符匹配。
        var pattern = string.IsNullOrWhiteSpace(keyword)
            ? null
            : $"%{keyword.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[")}%";
        var ownerFilter = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim();

        void Bind(SqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("@State", (int)state);
            cmd.Parameters.AddWithValue("@Keyword", (object?)pattern ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Owner", (object?)ownerFilter ?? DBNull.Value);
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
                items.Add(AssistantRepository.ReadSession(reader));
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
    public async Task<IReadOnlyList<string>> ListOwnersAsync(CancellationToken token)
    {
        const string sql = """
            SELECT DISTINCT USER_ID
            FROM dbo.ASSISTANT_SESSION WITH (NOLOCK)
            WHERE USER_ID IS NOT NULL
            ORDER BY USER_ID;
            """;
        var owners = new List<string>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            owners.Add(reader.GetString(0));
        }

        return owners;
    }

    /// <inheritdoc />
    public async Task<int> ArchiveSessionAsync(long sessionId, bool archived, CancellationToken token)
    {
        // 归档是"收起来"而不是"删掉"：只置/清 ARCHIVED_AT，消息一行不动。
        const string sql = """
            UPDATE dbo.ASSISTANT_SESSION
            SET ARCHIVED_AT = CASE WHEN @Archived = 1 THEN SYSUTCDATETIME() ELSE NULL END
            WHERE ID = @Id;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Archived", archived ? 1 : 0);
        cmd.Parameters.AddWithValue("@Id", sessionId);
        return await cmd.ExecuteNonQueryAsync(token);
    }

    /// <inheritdoc />
    public async Task<int> DeleteSessionAsync(long sessionId, CancellationToken token)
    {
        // 守卫与个人侧一致：只删已归档的会话。
        const string sql = """
            DELETE FROM dbo.ASSISTANT_MESSAGE
            WHERE SESSION_ID IN (
                SELECT ID FROM dbo.ASSISTANT_SESSION
                WHERE ID = @Id AND ARCHIVED_AT IS NOT NULL);
            DELETE FROM dbo.ASSISTANT_SESSION
            WHERE ID = @Id AND ARCHIVED_AT IS NOT NULL;
            SELECT @@ROWCOUNT AS Deleted;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", sessionId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }
}
