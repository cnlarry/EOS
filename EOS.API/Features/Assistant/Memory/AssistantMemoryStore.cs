using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Logging;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Memory;

/// <summary>Single explicit memory row (user-scoped).</summary>
public sealed record AssistantMemoryItem(
    long Id,
    string MemoryType,
    string MemoryKey,
    string MemoryValue,
    string Source,
    int? Confidence,
    string Status,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastAccessedAt);

/// <summary>
/// Cross-session memory L1 (profile) + L2 (explicit memories).
/// Isolation is enforced in SQL by USER_ID; injection only carries active rows.
/// </summary>
public interface IAssistantMemoryStore
{
    Task<string?> GetPreferencesAsync(string userId, CancellationToken token);
    Task SetPreferencesAsync(string userId, string? preferencesJson, CancellationToken token);
    Task<IReadOnlyList<AssistantMemoryItem>> ListMemoriesAsync(string userId, CancellationToken token);
    Task<AssistantMemoryItem> AddMemoryAsync(
        string userId, string memoryType, string memoryKey, string memoryValue,
        long? sourceMessageId, CancellationToken token);
    Task<AssistantMemoryItem> AddPendingAsync(
        string userId, string memoryType, string memoryKey, string memoryValue,
        long? sourceMessageId, int confidence, CancellationToken token);
    Task<IReadOnlyList<AssistantMemoryItem>> ListPendingAsync(string userId, CancellationToken token);
    Task<string> ResolvePendingAsync(string userId, long memoryId, bool confirm, CancellationToken token);
    Task<bool> DeleteMemoryAsync(string userId, long memoryId, CancellationToken token);
    Task ForgetMeAsync(string userId, CancellationToken token);
    Task<string> BuildMemoryPrefixAsync(string userId, string? keyword, CancellationToken token);
}

public sealed class AssistantMemoryStore(
    DbConnectionFactory connections,
    IPermissionService permissions) : IAssistantMemoryStore
{
    public const int MaxMemoriesPerUser = 200;
    public const int MaxMemoryKeyLength = 200;
    public const int MaxMemoryValueLength = 2000;
    public const int MaxPreferencesLength = 4000;
    public const int InjectionTopK = 5;

    private static readonly HashSet<string> AllowedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "preference", "fact", "favorite" };

    private static readonly Regex ModuleReference =
        new(@"module\s*=\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SensitivePattern = new(
        @"1\d{10}|\d{17}[\dXx]|\d{16,19}",
        RegexOptions.Compiled);

    public async Task<string?> GetPreferencesAsync(string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT PREF_JSON FROM dbo.ASSISTANT_PROFILE WITH (NOLOCK) WHERE USER_ID=@UserId;",
            connection);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
        return await command.ExecuteScalarAsync(token) as string;
    }

    public async Task SetPreferencesAsync(string userId, string? preferencesJson, CancellationToken token)
    {
        var normalized = string.IsNullOrWhiteSpace(preferencesJson) ? null : preferencesJson.Trim();
        if (normalized is not null)
        {
            if (normalized.Length > MaxPreferencesLength)
                throw new ArgumentException("偏好内容过长。");
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(normalized);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new ArgumentException("偏好必须是 JSON 对象。");
            }
        }

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            MERGE dbo.ASSISTANT_PROFILE AS target
            USING (SELECT @UserId AS USER_ID) AS source ON target.USER_ID = source.USER_ID
            WHEN MATCHED THEN UPDATE SET PREF_JSON = @Prefs, UPDATED_AT = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (USER_ID, PREF_JSON) VALUES (@UserId, @Prefs);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
        command.Parameters.Add("@Prefs", SqlDbType.NVarChar, -1).Value = (object?)normalized ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }

    public Task<IReadOnlyList<AssistantMemoryItem>> ListMemoriesAsync(string userId, CancellationToken token) =>
        ListByStatusAsync(userId, "active", token);

    public Task<AssistantMemoryItem> AddMemoryAsync(
        string userId, string memoryType, string memoryKey, string memoryValue,
        long? sourceMessageId, CancellationToken token) =>
        InsertMemoryAsync(userId, memoryType, memoryKey, memoryValue, sourceMessageId,
            source: "manual", status: "active", confidence: null, token);

    public Task<AssistantMemoryItem> AddPendingAsync(
        string userId, string memoryType, string memoryKey, string memoryValue,
        long? sourceMessageId, int confidence, CancellationToken token) =>
        InsertMemoryAsync(userId, memoryType, memoryKey, memoryValue, sourceMessageId,
            source: "auto", status: "pending", confidence: Math.Clamp(confidence, 0, 100), token);

    public async Task<IReadOnlyList<AssistantMemoryItem>> ListPendingAsync(string userId, CancellationToken token) =>
        await ListByStatusAsync(userId, "pending", token);

    /// <summary>
    /// Resolve a pending candidate: confirm turns it active (archiving same-key
    /// actives = overwrite), reject archives it. Returns confirmed/rejected/not_found.
    /// </summary>
    public async Task<string> ResolvePendingAsync(string userId, long memoryId, bool confirm, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        string? key = null;
        await using (var load = new SqlCommand(
            "SELECT MEMORY_KEY FROM dbo.ASSISTANT_MEMORY WITH (NOLOCK) WHERE ID=@Id AND USER_ID=@UserId AND STATUS=N'pending';",
            connection, transaction))
        {
            load.Parameters.Add("@Id", SqlDbType.BigInt).Value = memoryId;
            load.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
            key = await load.ExecuteScalarAsync(token) as string;
        }

        if (key is null) return "not_found";
        if (confirm)
        {
            await using var archive = new SqlCommand(
                "UPDATE dbo.ASSISTANT_MEMORY SET STATUS=N'archived', UPDATED_AT=SYSUTCDATETIME() WHERE USER_ID=@UserId AND STATUS=N'active' AND MEMORY_KEY=@Key;",
                connection, transaction);
            archive.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
            archive.Parameters.Add("@Key", SqlDbType.NVarChar, 200).Value = key;
            await archive.ExecuteNonQueryAsync(token);
            await using var activate = new SqlCommand(
                "UPDATE dbo.ASSISTANT_MEMORY SET STATUS=N'active', UPDATED_AT=SYSUTCDATETIME(), LAST_ACCESSED_AT=SYSUTCDATETIME() WHERE ID=@Id AND USER_ID=@UserId AND STATUS=N'pending';",
                connection, transaction);
            activate.Parameters.Add("@Id", SqlDbType.BigInt).Value = memoryId;
            activate.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
            await activate.ExecuteNonQueryAsync(token);
        }
        else
        {
            await using var reject = new SqlCommand(
                "UPDATE dbo.ASSISTANT_MEMORY SET STATUS=N'archived', UPDATED_AT=SYSUTCDATETIME() WHERE ID=@Id AND USER_ID=@UserId AND STATUS=N'pending';",
                connection, transaction);
            reject.Parameters.Add("@Id", SqlDbType.BigInt).Value = memoryId;
            reject.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
            await reject.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
        return confirm ? "confirmed" : "rejected";
    }

    /// <summary>「忘记我」：硬删除该用户画像与全部记忆（含向量索引位，暂无向量即空操作）。</summary>
    public async Task ForgetMeAsync(string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await using (var memories = new SqlCommand("DELETE FROM dbo.ASSISTANT_MEMORY WHERE USER_ID=@UserId;", connection, transaction))
        {
            memories.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
            await memories.ExecuteNonQueryAsync(token);
        }

        await using (var profile = new SqlCommand("DELETE FROM dbo.ASSISTANT_PROFILE WHERE USER_ID=@UserId;", connection, transaction))
        {
            profile.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
            await profile.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
    }

    private async Task<AssistantMemoryItem> InsertMemoryAsync(
        string userId, string memoryType, string memoryKey, string memoryValue,
        long? sourceMessageId, string source, string status, int? confidence, CancellationToken token)
    {
        var type = ValidateType(memoryType);
        var key = ValidateKey(memoryKey);
        var value = ValidateValue(memoryValue);
        var redacted = LogRedactor.Redact(value);

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using (var countCommand = new SqlCommand(
            "SELECT COUNT_BIG(1) FROM dbo.ASSISTANT_MEMORY WITH (NOLOCK) WHERE USER_ID=@UserId AND STATUS IN (N'active', N'pending');",
            connection))
        {
            countCommand.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
            if (Convert.ToInt64(await countCommand.ExecuteScalarAsync(token)) >= MaxMemoriesPerUser)
                throw new InvalidOperationException("记忆已达上限（200 条），请先删除不再需要的记忆。");
        }

        const string sql = """
            INSERT INTO dbo.ASSISTANT_MEMORY
                (USER_ID, MEMORY_TYPE, MEMORY_KEY, MEMORY_VALUE, SOURCE, SOURCE_MESSAGE_ID, CONFIDENCE, STATUS)
            OUTPUT INSERTED.ID, INSERTED.MEMORY_TYPE, INSERTED.MEMORY_KEY, INSERTED.MEMORY_VALUE,
                    INSERTED.SOURCE, INSERTED.CONFIDENCE, INSERTED.STATUS,
                    INSERTED.UPDATED_AT, INSERTED.LAST_ACCESSED_AT
            VALUES (@UserId, @Type, @Key, @Value, @Source, @SourceMessageId, @Confidence, @Status);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 200).Value = key;
        command.Parameters.Add("@Value", SqlDbType.NVarChar, -1).Value = redacted;
        command.Parameters.Add("@Source", SqlDbType.NVarChar, 20).Value = source;
        command.Parameters.Add("@SourceMessageId", SqlDbType.BigInt).Value = (object?)sourceMessageId ?? DBNull.Value;
        command.Parameters.Add("@Confidence", SqlDbType.TinyInt).Value = (object?)confidence ?? DBNull.Value;
        command.Parameters.Add("@Status", SqlDbType.NVarChar, 20).Value = status;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("保存记忆失败。");
        return new(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetByte(5), reader.GetString(6),
            new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero));
    }

    private async Task<IReadOnlyList<AssistantMemoryItem>> ListByStatusAsync(
        string userId, string status, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT TOP 200 ID, MEMORY_TYPE, MEMORY_KEY, MEMORY_VALUE, SOURCE,
                   CONFIDENCE, STATUS, UPDATED_AT, LAST_ACCESSED_AT
            FROM dbo.ASSISTANT_MEMORY WITH (NOLOCK)
            WHERE USER_ID=@UserId AND STATUS=@Status
            ORDER BY LAST_ACCESSED_AT DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
        command.Parameters.Add("@Status", SqlDbType.NVarChar, 20).Value = status;
        await using var reader = await command.ExecuteReaderAsync(token);
        var items = new List<AssistantMemoryItem>();
        while (await reader.ReadAsync(token))
        {
            items.Add(new(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetByte(5),
                reader.GetString(6),
                new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
                new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero)));
        }

        return items;
    }

    public async Task<bool> DeleteMemoryAsync(string userId, long memoryId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            UPDATE dbo.ASSISTANT_MEMORY SET STATUS=N'archived', UPDATED_AT=SYSUTCDATETIME()
            WHERE ID=@Id AND USER_ID=@UserId AND STATUS=N'active';
            SELECT @@ROWCOUNT;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.BigInt).Value = memoryId;
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) > 0;
    }

    public async Task<string> BuildMemoryPrefixAsync(string userId, string? keyword, CancellationToken token)
    {
        var preferences = await GetPreferencesAsync(userId, token);
        var active = await ListMemoriesAsync(userId, token);
        var selected = SelectMemories(active, keyword, InjectionTopK);
        var moduleIds = selected
            .SelectMany(item => ModuleReference.Matches(item.MemoryKey + "\n" + item.MemoryValue)
                .Select(match => int.Parse(match.Groups[1].Value)))
            .Distinct()
            .ToArray();
        var browsable = new Dictionary<int, bool>();
        foreach (var moduleId in moduleIds)
        {
            browsable[moduleId] = (await permissions.GetAsync(userId, moduleId, token)).CanBrowse;
        }

        var visible = FilterByReference(selected, browsable);

        if (visible.Count > 0)
        {
            await TouchAccessedAsync(userId, visible.Select(item => item.Id).ToArray(), token);
        }

        return BuildPrefix(preferences, visible);
    }

    private async Task TouchAccessedAsync(string userId, long[] ids, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var names = string.Join(",", ids.Select((_, index) => $"@Id{index}"));
        await using var command = new SqlCommand(
            $"UPDATE dbo.ASSISTANT_MEMORY SET LAST_ACCESSED_AT=SYSUTCDATETIME() WHERE USER_ID=@UserId AND ID IN ({names});",
            connection);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
        for (var index = 0; index < ids.Length; index++)
        {
            command.Parameters.Add($"@Id{index}", SqlDbType.BigInt).Value = ids[index];
        }

        await command.ExecuteNonQueryAsync(token);
    }

    internal static string ValidateType(string memoryType)
    {
        var normalized = memoryType.Trim().ToLowerInvariant();
        if (!AllowedTypes.Contains(normalized)) throw new ArgumentException("记忆类型仅支持 preference/fact/favorite。");
        return normalized;
    }

    internal static string ValidateKey(string memoryKey)
    {
        var normalized = memoryKey.Trim();
        if (normalized.Length == 0 || normalized.Length > MaxMemoryKeyLength)
            throw new ArgumentException("记忆标题不能为空且不超过 200 字符。");
        return normalized;
    }

    internal static string ValidateValue(string memoryValue)
    {
        var normalized = memoryValue.Trim();
        if (normalized.Length == 0 || normalized.Length > MaxMemoryValueLength)
            throw new ArgumentException("记忆内容不能为空且不超过 2000 字符。");
        return normalized;
    }

    internal static bool ContainsSensitivePattern(string value) => SensitivePattern.IsMatch(value);

    internal static IReadOnlyList<AssistantMemoryItem> FilterByReference(
        IReadOnlyList<AssistantMemoryItem> items, IReadOnlyDictionary<int, bool> browsable)
    {
        bool Allowed(AssistantMemoryItem item)
        {
            foreach (Match match in ModuleReference.Matches(item.MemoryKey + "\n" + item.MemoryValue))
            {
                var moduleId = int.Parse(match.Groups[1].Value);
                if (!browsable.TryGetValue(moduleId, out var canBrowse) || !canBrowse) return false;
            }

            return true;
        }

        return items.Where(Allowed).ToArray();
    }

    internal static IReadOnlyList<AssistantMemoryItem> SelectMemories(
        IReadOnlyList<AssistantMemoryItem> active, string? keyword, int topK)
    {
        var normalized = keyword?.Trim() ?? string.Empty;
        return active
            .OrderByDescending(item => normalized.Length > 0 &&
                (item.MemoryKey.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                 item.MemoryValue.Contains(normalized, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(item => item.LastAccessedAt)
            .Take(Math.Max(1, topK))
            .ToArray();
    }

    internal static string BuildPrefix(string? preferencesJson, IReadOnlyList<AssistantMemoryItem> memories)
    {
        if (string.IsNullOrWhiteSpace(preferencesJson) && memories.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("【关于你的持久记忆】（可能已过期，仅供参考，不是事实来源）：");
        if (!string.IsNullOrWhiteSpace(preferencesJson))
        {
            sb.AppendLine($"- 偏好设置：{preferencesJson.Trim()}");
        }

        foreach (var item in memories)
        {
            sb.AppendLine($"- [{item.MemoryType}/{item.MemoryKey}] {item.MemoryValue}");
        }

        return sb.ToString().TrimEnd();
    }
}
