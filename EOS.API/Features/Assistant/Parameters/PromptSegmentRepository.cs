using System.Data;
using Microsoft.Data.SqlClient;
using EOS.API.Data;

namespace EOS.API.Features.Assistant.Parameters;

/// <summary>规则段的读取与维护。文本在库（ADR-046），锁定行只允许迁移改。</summary>
public interface IPromptSegments
{
    /// <summary>已启用的规则段，按 SORT_IDX 排序；库内一张行都没有时返回空（调用方用代码默认值兜底）。</summary>
    Task<IReadOnlyList<PromptSegment>> ListEnabledAsync(CancellationToken token);

    Task<PromptSegmentUpdateStatus> UpdateTextAsync(string key, string text, string userId, CancellationToken token);
}

public enum PromptSegmentUpdateStatus
{
    Updated,
    NotFound,
    Locked,
}

public sealed class PromptSegmentRepository(DbConnectionFactory connections) : IPromptSegments
{
    public async Task<IReadOnlyList<PromptSegment>> ListEnabledAsync(CancellationToken token)
    {
        const string sql = """
            SELECT SEGMENT_KEY, TITLE, SEGMENT_TEXT, REQUIRED_TOOLS, IS_LOCKED, SORT_IDX
            FROM dbo.ASSISTANT_PROMPT_SEGMENT
            WHERE ENABLED = 1
            ORDER BY SORT_IDX, SEGMENT_KEY;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var segments = new List<PromptSegment>();
        while (await reader.ReadAsync(token))
        {
            var tools = reader.IsDBNull(3)
                ? []
                : reader.GetString(3)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
            segments.Add(new PromptSegment(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                tools,
                reader.GetBoolean(4),
                reader.GetInt32(5)));
        }
        return segments;
    }

    public async Task<PromptSegmentUpdateStatus> UpdateTextAsync(string key, string text, string userId,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        await using (var lookup = new SqlCommand(
            "SELECT IS_LOCKED FROM dbo.ASSISTANT_PROMPT_SEGMENT WHERE SEGMENT_KEY = @Key;", connection))
        {
            lookup.Parameters.Add("@Key", SqlDbType.NVarChar, 40).Value = key;
            var locked = await lookup.ExecuteScalarAsync(token);
            if (locked is null or DBNull) return PromptSegmentUpdateStatus.NotFound;
            if (locked is true) return PromptSegmentUpdateStatus.Locked;
        }

        await using var update = new SqlCommand("""
            UPDATE dbo.ASSISTANT_PROMPT_SEGMENT
            SET SEGMENT_TEXT = @Text, MODIFIED_BY = @User, MODIFIED_TIME = GETDATE()
            WHERE SEGMENT_KEY = @Key AND IS_LOCKED = 0;
            """, connection);
        update.Parameters.Add("@Text", SqlDbType.NVarChar, 2000).Value = text.Trim();
        update.Parameters.Add("@User", SqlDbType.NVarChar, 20).Value = userId.Trim();
        update.Parameters.Add("@Key", SqlDbType.NVarChar, 40).Value = key;
        var affected = await update.ExecuteNonQueryAsync(token);
        return affected > 0 ? PromptSegmentUpdateStatus.Updated : PromptSegmentUpdateStatus.Locked;
    }
}
