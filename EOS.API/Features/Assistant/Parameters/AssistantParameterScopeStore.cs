using System.Data;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Parameters;

/// <summary>
/// 助手参数**作用域覆盖**的读写（<c>dbo.ASSISTANT_PARAM_SCOPE</c>，ADR-030 §6.2）。
///
/// <para>
/// 它只管存取与留痕，**不判断松紧**——"这条参数能不能被覆盖、只能收紧还是可以放宽"在
/// <see cref="AssistantParameterScopeRules"/>，保存与生效共用那一份判断。
/// </para>
///
/// <para>
/// 写操作与审计在**同一事务**里：谁在什么时候把谁的日限额调高了，是可回答的。
/// 这是"按用户放宽限额"能被允许的前提——没有留痕的放宽与越权改不变量没有区别。
/// </para>
/// </summary>
public sealed class AssistantParameterScopeStore(
    DbConnectionFactory connections,
    WorkbenchAuditWriter auditWriter)
{
    private const string SelectColumns = "SCOPE_TYPE, SCOPE_KEY, PARAM_KEY, PARAM_VALUE, LAST_UPDATE_BY, LAST_UPDATE_DATE";

    /// <summary>
    /// 管理界面列表用的列清单：在 <see cref="SelectColumns"/> 之外多带一列**显示名**。
    ///
    /// <para>
    /// 库里存的是模块号与登录账号，光看号认不出是谁——"1401"与"wangwu"对操作者同样是天书，
    /// 而这一页的用途正是"看谁被单独设过什么"。名字来自两张主档表的 JOIN（模块 → `MODULES.M_DESC`、
    /// 用户 → `SYSDN.EMP_NAME`），**解析不到就留空**（模块已删、账号没登记姓名），
    /// 而不是回落成键名：把号当名字显示，等于假装解析成功了。
    /// </para>
    ///
    /// <para>
    /// 这两个 join 只加在管理列表上：运行时那条（<see cref="ListForAsync"/>）只按键取值，
    /// 多两个 join 是纯开销。
    /// </para>
    /// </summary>
    private const string LabelledSelectColumns = """
        s.SCOPE_TYPE, s.SCOPE_KEY, s.PARAM_KEY, s.PARAM_VALUE, s.LAST_UPDATE_BY, s.LAST_UPDATE_DATE,
        CASE WHEN s.SCOPE_TYPE = N'MODULE' THEN ISNULL(m.M_DESC, N'')
             ELSE ISNULL(NULLIF(LTRIM(RTRIM(n.EMP_NAME)), N''), N'') END AS SCOPE_LABEL
        """;

    /// <summary>全部覆盖行（管理界面用：看"谁被单独设过什么"，带显示名）。</summary>
    public async Task<IReadOnlyList<AssistantParameterScopeRow>> ListAllAsync(CancellationToken token)
    {
        // TRY_CONVERT：SCOPE_KEY 是字符串列，用户层的账号名在这个 join 上解析成 NULL 而不抛错
        const string sql = $"""
            SELECT {LabelledSelectColumns}
            FROM dbo.ASSISTANT_PARAM_SCOPE s WITH (NOLOCK)
            LEFT JOIN dbo.MODULES m WITH (NOLOCK)
                   ON s.SCOPE_TYPE = N'MODULE' AND m.M_IDX = TRY_CONVERT(int, s.SCOPE_KEY)
            LEFT JOIN dbo.SYSDL l WITH (NOLOCK)
                   ON s.SCOPE_TYPE = N'USER' AND LTRIM(RTRIM(l.USER_ID)) = s.SCOPE_KEY
            LEFT JOIN dbo.SYSDN n WITH (NOLOCK) ON l.EMP_ID = n.EMP_ID
            ORDER BY s.SCOPE_TYPE, s.SCOPE_KEY, s.PARAM_KEY;
            """;
        return await ReadAsync(sql, null, token);
    }

    /// <summary>
    /// 当前当事人适用的覆盖行：**他自己的**用户级覆盖，以及**当前模块**的模块级覆盖。
    /// 别的用户 / 别的模块的行不读——读回来再过滤，等于把别人配过什么带进了这次请求的内存。
    /// </summary>
    public async Task<IReadOnlyList<AssistantParameterScopeRow>> ListForAsync(
        string? userId, int? moduleId, CancellationToken token)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM dbo.ASSISTANT_PARAM_SCOPE WITH (NOLOCK)
            WHERE (SCOPE_TYPE = N'USER' AND SCOPE_KEY = @User)
               OR (SCOPE_TYPE = N'MODULE' AND SCOPE_KEY = @Module);
            """;
        var parameters = new List<SqlParameter>
        {
            new("@User", SqlDbType.NVarChar, 50) { Value = (object?)userId ?? DBNull.Value },
            new("@Module", SqlDbType.NVarChar, 50)
            {
                Value = moduleId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? (object)DBNull.Value,
            },
        };
        return await ReadAsync(sql, parameters, token);
    }

    /// <summary>
    /// 写入一层覆盖。空值 = **清掉这一层**（回到上层取值），而不是存一个空串——
    /// 与 3105 页面上"恢复默认"同一口径。
    /// </summary>
    public async Task UpsertAsync(
        string scopeType, string scopeKey, string paramKey, string? value, string actor, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        var previous = await ReadValueAsync(connection, transaction, scopeType, scopeKey, paramKey, token);

        if (string.IsNullOrWhiteSpace(value))
        {
            await using var delete = new SqlCommand("""
                DELETE FROM dbo.ASSISTANT_PARAM_SCOPE
                WHERE SCOPE_TYPE = @Type AND SCOPE_KEY = @Key AND PARAM_KEY = @Param;
                """, connection, transaction);
                AddKey(delete, scopeType, scopeKey, paramKey);
            await delete.ExecuteNonQueryAsync(token);
        }
        else
        {
            // UPDATE 先行 + INSERT 兜底：并发保存同一层时，主键冲突会退化成一次更新，
            // 而不是抛 2627 让界面看到一句看不懂的错
            await using (var update = new SqlCommand("""
                UPDATE dbo.ASSISTANT_PARAM_SCOPE
                SET PARAM_VALUE = @Value, LAST_UPDATE_BY = @Actor, LAST_UPDATE_DATE = GETDATE()
                WHERE SCOPE_TYPE = @Type AND SCOPE_KEY = @Key AND PARAM_KEY = @Param;
                """, connection, transaction))
            {
                AddKey(update, scopeType, scopeKey, paramKey);
                update.Parameters.Add("@Value", SqlDbType.NVarChar, 4000).Value = value;
                update.Parameters.Add("@Actor", SqlDbType.NChar, 10).Value = Truncate(actor, 10);
                if (await update.ExecuteNonQueryAsync(token) == 0)
                {
                    await using var insert = new SqlCommand("""
                        INSERT INTO dbo.ASSISTANT_PARAM_SCOPE
                            (SCOPE_TYPE, SCOPE_KEY, PARAM_KEY, PARAM_VALUE, CREATE_PERSON, CREATE_DATE)
                        VALUES (@Type, @Key, @Param, @Value, @Actor, GETDATE());
                        """, connection, transaction);
                    AddKey(insert, scopeType, scopeKey, paramKey);
                    insert.Parameters.Add("@Value", SqlDbType.NVarChar, 4000).Value = value;
                    insert.Parameters.Add("@Actor", SqlDbType.NChar, 10).Value = Truncate(actor, 10);
                    await insert.ExecuteNonQueryAsync(token);
                }
            }
        }

        await auditWriter.WriteEventAsync(
            connection,
            transaction,
            AssistantParameterCatalog.OwnerModule,
            AssistantParameterCatalog.ScopeToken,
            string.IsNullOrWhiteSpace(value) ? "PARAM_SCOPE_RESET" : "PARAM_SCOPE_SAVE",
            $"{Describe(scopeType, scopeKey)} 的 {paramKey}：{Describe(previous)} → {Describe(value)}",
            Truncate(actor, 20),
            "ASSISTANT_PARAM_SCOPE",
            result: 1,
            fieldChanges: [new AuditFieldChange(paramKey, previous, value, null)],
            token);

        await transaction.CommitAsync(token);
    }

    /// <summary>删掉一层的全部覆盖（返回删掉的条数）。</summary>
    public async Task<int> DeleteLayerAsync(string scopeType, string scopeKey, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        int affected;
        await using (var delete = new SqlCommand("""
            DELETE FROM dbo.ASSISTANT_PARAM_SCOPE WHERE SCOPE_TYPE = @Type AND SCOPE_KEY = @Key;
            """, connection, transaction))
        {
            delete.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = scopeType;
            delete.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = scopeKey;
            affected = await delete.ExecuteNonQueryAsync(token);
        }

        if (affected > 0)
        {
            await auditWriter.WriteEventAsync(
                connection, transaction, AssistantParameterCatalog.OwnerModule, AssistantParameterCatalog.ScopeToken,
                "PARAM_SCOPE_RESET", $"清除 {Describe(scopeType, scopeKey)} 的全部参数覆盖（{affected} 项）",
                string.Empty, "ASSISTANT_PARAM_SCOPE", result: 1, fieldChanges: null, token);
        }

        await transaction.CommitAsync(token);
        return affected;
    }

    /// <summary>
    /// 作用域键是否存在（模块号在 <c>MODULES</c>、用户名在 <c>SYSDL</c>）。
    ///
    /// <para>
    /// 之所以要查：写错一个模块号或用户名，那一行**永远匹配不上任何请求**——
    /// 界面上却显示着"已设置"，管理员的意图静默失效。宁可写的时候被拒。
    /// </para>
    /// </summary>
    public async Task<bool> ScopeKeyExistsAsync(string scopeType, string scopeKey, CancellationToken token)
    {
        var isModule = string.Equals(scopeType, AssistantParameterScopeRules.Module, StringComparison.OrdinalIgnoreCase);
        var sql = isModule
            ? "SELECT COUNT(1) FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX = @Key;"
            : "SELECT COUNT(1) FROM dbo.SYSDL WITH (NOLOCK) WHERE LTRIM(RTRIM(USER_ID)) = @Key;";
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        if (isModule)
        {
            command.Parameters.Add("@Key", SqlDbType.Int).Value =
                int.TryParse(scopeKey, out var moduleId) ? moduleId : -1;
        }
        else
        {
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = scopeKey;
        }

        var count = await command.ExecuteScalarAsync(token);
        return count is not null and not DBNull
            && Convert.ToInt32(count, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private async Task<IReadOnlyList<AssistantParameterScopeRow>> ReadAsync(
        string sql, IReadOnlyList<SqlParameter>? parameters, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        if (parameters is not null)
        {
            foreach (var parameter in parameters) command.Parameters.Add(parameter);
        }

        var items = new List<AssistantParameterScopeRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            items.Add(new AssistantParameterScopeRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4).Trim(),
                reader.IsDBNull(5)
                    ? null
                    : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)),
                // 第 7 列只有管理列表查（SCOPE_LABEL）：运行时那条查询不带它，也就没有名字可读
                reader.FieldCount > 6 && !reader.IsDBNull(6) && !string.IsNullOrWhiteSpace(reader.GetString(6))
                    ? reader.GetString(6).Trim()
                    : null));
        }

        return items;
    }

    private static async Task<string?> ReadValueAsync(
        SqlConnection connection, SqlTransaction transaction,
        string scopeType, string scopeKey, string paramKey, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT PARAM_VALUE FROM dbo.ASSISTANT_PARAM_SCOPE WITH (UPDLOCK)
            WHERE SCOPE_TYPE = @Type AND SCOPE_KEY = @Key AND PARAM_KEY = @Param;
            """, connection, transaction);
        AddKey(command, scopeType, scopeKey, paramKey);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddKey(SqlCommand command, string scopeType, string scopeKey, string paramKey)
    {
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = scopeType;
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = scopeKey;
        command.Parameters.Add("@Param", SqlDbType.NVarChar, 64).Value = paramKey;
    }

    private static string Describe(string? value) => string.IsNullOrWhiteSpace(value) ? "未设置" : value;

    private static string Describe(string scopeType, string scopeKey) =>
        string.Equals(scopeType, AssistantParameterScopeRules.User, StringComparison.OrdinalIgnoreCase)
            ? $"用户 {scopeKey}"
            : $"模块 {scopeKey}";

    private static string Truncate(string? value, int length)
    {
        var text = value ?? string.Empty;
        return text.Length <= length ? text : text[..length];
    }
}
