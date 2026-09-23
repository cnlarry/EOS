using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions;

/// <summary>
/// Builds the button metadata published with the definition. Only configured actions the user is
/// authorized for travel to the client, so an unauthorized button is not merely disabled — it is not
/// there at all (the server still re-authorizes every request).
/// </summary>
internal static class DocumentActionMetadataFactory
{
    public static IReadOnlyList<DocumentActionMetadata> Build(
        IReadOnlyList<DocumentActionConfig> configured,
        IReadOnlySet<string> authorized,
        DocumentActionRegistry registry) =>
        configured
            .Where(config => authorized.Contains(config.Key))
            .Select(config => new DocumentActionMetadata(
                config.Key,
                config.Label.Length > 0 ? config.Label : registry.LabelOf(config.Key),
                config.ConfirmTag,
                config.FailMode,
                config.Params,
                registry.PlacementOf(config.Key)))
            .ToList();
}

/// <summary>Per-button authorization counts (the mirror the configuration surface shows).</summary>
public sealed record DocumentActionAuthorizationCount(int Users, int Groups);

/// <summary>
/// Button-level authorization for document actions, fail-closed: once a button is configured and
/// published, nobody can press it until an administrator grants it explicitly. Structure and
/// aggregation follow the report override tables (personal / group dual channel, aggregated through
/// SYSDG_USER), with one deliberate difference: a report with no override row falls back to the
/// module's REPORT_TAG (open), a button with no override row is denied. There is no exemption —
/// an administrator has to grant the button to themselves like anybody else.
///
/// Personal rows override the group channel entirely for a module: if the user has any row for that
/// module, only those rows count (a row with ALLOW_TAG=0 then means "explicitly denied", even when a
/// group grants the button). Otherwise the groups the user belongs to are OR-ed together.
/// </summary>
public sealed class DocumentActionAuthorization(DbConnectionFactory connections)
{
    /// <summary>Keys the user is allowed to press out of <paramref name="buttonKeys"/>.</summary>
    public async Task<IReadOnlySet<string>> AuthorizedKeysAsync(
        string userId,
        int moduleId,
        IReadOnlyCollection<string> buttonKeys,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(userId) || buttonKeys.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        // 个人通道一旦在该模块有行，就完全接管组通道：允许位是 0 的行表达"显式拒绝"，
        // 此时组授权不再兜底（与模块/报表权限的"个人覆盖组"同一口径）。
        var personal = await ReadPersonalRowsAsync(connection, userId, moduleId, token);
        if (personal.Count > 0)
        {
            return buttonKeys
                .Where(key => personal.TryGetValue(key, out var allowed) && allowed)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var groups = await ReadGroupAsync(connection, userId, moduleId, token);
        return buttonKeys
            .Where(groups.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether the user may press one button.</summary>
    public async Task<bool> IsAuthorizedAsync(string userId, int moduleId, string buttonKey, CancellationToken token) =>
        (await AuthorizedKeysAsync(userId, moduleId, [buttonKey], token)).Count > 0;

    /// <summary>
    /// How many users and groups hold each button of the module (configuration-side mirror).
    /// "Configured but nobody can press it" is a normal state, so it must not be a silent one.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DocumentActionAuthorizationCount>> CountsAsync(
        int moduleId,
        IReadOnlyCollection<string> buttonKeys,
        CancellationToken token)
    {
        var result = buttonKeys.ToDictionary(
            key => key,
            _ => new DocumentActionAuthorizationCount(0, 0),
            StringComparer.OrdinalIgnoreCase);
        if (buttonKeys.Count == 0)
        {
            return result;
        }
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var users = await ReadCountsAsync(connection, "SYSDD_BUTTON", "USER_ID", moduleId, token);
        var groups = await ReadCountsAsync(connection, "SYSDH_BUTTON", "G_IDX", moduleId, token);
        foreach (var key in result.Keys.ToList())
        {
            result[key] = new DocumentActionAuthorizationCount(
                users.GetValueOrDefault(key),
                groups.GetValueOrDefault(key));
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadCountsAsync(
        SqlConnection connection,
        string table,
        string ownerColumn,
        int moduleId,
        CancellationToken token)
    {
        await using var command = new SqlCommand(
            $"""
            SELECT BUTTON_KEY, COUNT(DISTINCT {ownerColumn})
            FROM dbo.{table}
            WHERE M_IDX=@ModuleId AND ALLOW_TAG=1
            GROUP BY BUTTON_KEY;
            """, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            result[reader.GetString(0).Trim()] = reader.GetInt32(1);
        }
        return result;
    }

    /// <summary>
    /// Personal channel: every row the user has for this module, with its allow bit — the caller has
    /// to see deny rows as well, because their very presence takes the module off the group channel.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, bool>> ReadPersonalRowsAsync(
        SqlConnection connection,
        string userId,
        int moduleId,
        CancellationToken token)
    {
        await using var command = new SqlCommand(
            """
            SELECT BUTTON_KEY, ALLOW_TAG FROM dbo.SYSDD_BUTTON
            WHERE M_IDX=@ModuleId AND USER_ID=@Owner;
            """, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@Owner", SqlDbType.NVarChar, 20).Value = userId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            result[reader.GetString(0).Trim()] = reader.GetBoolean(1);
        }
        return result;
    }

    /// <summary>
    /// Group channel: the groups the user belongs to, joined through SYSDG_USER so "which groups is
    /// this user in" stays the same question the existing rights aggregation asks.
    /// </summary>
    private static async Task<IReadOnlySet<string>> ReadGroupAsync(
        SqlConnection connection,
        string userId,
        int moduleId,
        CancellationToken token) =>
        await ReadKeysAsync(connection,
            """
            SELECT b.BUTTON_KEY FROM dbo.SYSDH_BUTTON b
            INNER JOIN dbo.SYSDG_USER u ON u.G_IDX=b.G_IDX
            WHERE b.M_IDX=@ModuleId AND b.ALLOW_TAG=1 AND u.USER_ID=@Owner;
            """, userId, moduleId, token);

    private static async Task<IReadOnlySet<string>> ReadKeysAsync(
        SqlConnection connection,
        string sql,
        string owner,
        int moduleId,
        CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@Owner", SqlDbType.NVarChar, 20).Value = owner.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            result.Add(reader.GetString(0).Trim());
        }
        return result;
    }
}
