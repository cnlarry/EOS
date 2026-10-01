using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary><c>dbo.ASSISTANT_SETTING</c> 的一行（纵向参数表，照 ADR-017 系统参数纵向化的范式）。</summary>
public sealed record AssistantSettingRow(
    string ParamKey,
    string? ParamValue,
    string ValueType,
    string? DescText,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <summary>
/// 助手全局策略参数的读写（菜单组 31 / 模块 3105，见 ADR-030 §8）。
///
/// <para>
/// **只放策略参数**：系统提示词、日上限、单价兜底、熔断阈值这些。密钥不走这里（密钥只走环境变量，
/// 见 §3）。表里没有行 = 用代码默认值，所以"恢复默认"就是 <see cref="DeleteAsync"/>。
/// </para>
/// </summary>
public interface IAssistantSettingStore
{
    /// <summary>读出全部已覆盖的参数（缺行不补默认——默认值的真源在代码里）。</summary>
    Task<IReadOnlyList<AssistantSettingRow>> ListAsync(CancellationToken token);

    /// <summary>写入或覆盖一个参数。值统一按文本存，<paramref name="valueType"/> 说明怎么解析。</summary>
    Task UpsertAsync(string key, string? value, string valueType, string? desc, string actor, CancellationToken token);

    /// <summary>删除一个参数 = **恢复代码默认值**。返回是否命中行。</summary>
    Task<bool> DeleteAsync(string key, CancellationToken token);
}

/// <summary>设置表的 SQL Server 实现。</summary>
public sealed class AssistantSettingStore(DbConnectionFactory connections) : IAssistantSettingStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantSettingRow>> ListAsync(CancellationToken token)
    {
        // **刻意不加 NOLOCK**：参数读的是"改完立刻生效"的东西，脏读会让"刚保存却不生效"变成一个
        // 无法复现的怪现象——为省这点开销不值得（NOLOCK 白名单的口径也是"强一致路径不要加"）。
        const string sql = """
            SELECT PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DESC_TEXT, UPDATED_AT, UPDATED_BY
            FROM dbo.ASSISTANT_SETTING
            ORDER BY PARAM_KEY;
            """;
        var items = new List<AssistantSettingRow>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            items.Add(new AssistantSettingRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        string key, string? value, string valueType, string? desc, string actor, CancellationToken token)
    {
        // UPDATE 先行 + INSERT 兜底，放在一个事务里：并发保存同一个键时，主键冲突会退化成一次更新，
        // 而不是抛 2627 让界面看到一句看不懂的错
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(token);

        int affected;
        await using (var update = new SqlCommand("""
            UPDATE dbo.ASSISTANT_SETTING
            SET PARAM_VALUE = @Value, VALUE_TYPE = @Type, DESC_TEXT = @Desc,
                UPDATED_AT = SYSUTCDATETIME(), UPDATED_BY = @Actor
            WHERE PARAM_KEY = @Key;
            """, conn, tx))
        {
            update.Parameters.AddWithValue("@Key", key);
            update.Parameters.AddWithValue("@Value", (object?)value ?? DBNull.Value);
            update.Parameters.AddWithValue("@Type", valueType);
            update.Parameters.AddWithValue("@Desc", (object?)desc ?? DBNull.Value);
            update.Parameters.AddWithValue("@Actor", actor);
            affected = await update.ExecuteNonQueryAsync(token);
        }

        if (affected == 0)
        {
            await using var insert = new SqlCommand("""
                INSERT INTO dbo.ASSISTANT_SETTING (PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DESC_TEXT, UPDATED_BY)
                VALUES (@Key, @Value, @Type, @Desc, @Actor);
                """, conn, tx);
            insert.Parameters.AddWithValue("@Key", key);
            insert.Parameters.AddWithValue("@Value", (object?)value ?? DBNull.Value);
            insert.Parameters.AddWithValue("@Type", valueType);
            insert.Parameters.AddWithValue("@Desc", (object?)desc ?? DBNull.Value);
            insert.Parameters.AddWithValue("@Actor", actor);
            await insert.ExecuteNonQueryAsync(token);
        }

        await tx.CommitAsync(token);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string key, CancellationToken token)
    {
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand("DELETE FROM dbo.ASSISTANT_SETTING WHERE PARAM_KEY = @Key;", conn);
        cmd.Parameters.AddWithValue("@Key", key);
        return await cmd.ExecuteNonQueryAsync(token) > 0;
    }
}
