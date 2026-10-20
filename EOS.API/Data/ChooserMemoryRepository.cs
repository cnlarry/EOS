using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>选择器记忆：某个用户对某张目标表上次使用的筛选字段与查询值。</summary>
public sealed record ChooserMemory(string? FilterField, string? Keyword);

/// <summary>
/// 选择器记忆读写（表 dbo.SYS_USER_CHOOSER_MEMORY，主键 USER_ID + T_ID）。
/// 只存"上次怎么搜的"，不存"选了哪条"——与旧系统的选择器行为一致。
/// 表名／字段名只作为参数值写入，不进入任何动态 SQL 标识符位置。
/// </summary>
public sealed class ChooserMemoryRepository(DbConnectionFactory connections)
{
    public async Task<ChooserMemory?> GetAsync(string userId, string tableId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT TOP 1 F_ID, F_VALUE
              FROM dbo.SYS_USER_CHOOSER_MEMORY
             WHERE USER_ID = @UserId AND T_ID = @TableId;
            """, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 20).Value = userId;
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 200).Value = tableId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new ChooserMemory(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public async Task SaveAsync(string userId, string tableId, string? filterField, string? keyword, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            UPDATE dbo.SYS_USER_CHOOSER_MEMORY
               SET F_ID = @FilterField, F_VALUE = @Keyword
             WHERE USER_ID = @UserId AND T_ID = @TableId;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.SYS_USER_CHOOSER_MEMORY (USER_ID, T_ID, F_ID, F_VALUE)
                VALUES (@UserId, @TableId, @FilterField, @Keyword);
            """, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 20).Value = userId;
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 200).Value = tableId;
        command.Parameters.Add("@FilterField", SqlDbType.NVarChar, 200).Value = (object?)filterField ?? DBNull.Value;
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 200).Value = (object?)keyword ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }
}
