using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed class LegacyRightsRepository(DbConnectionFactory connections, ILogger<LegacyRightsRepository> logger)
{
    public async Task<LegacyModuleRights> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var personal = await ReadRowsAsync(connection, "SYSDD", "USER_ID", userId, moduleId, cancellationToken);
        if (personal.Count > 0)
        {
            var personalRights = ToRights(personal[0], true);
            LogRights(userId, moduleId, "personal", personalRights);
            return personalRights;
        }

        const string groupSql = """
            SELECT h.EXEC_TAG,h.COST_TAG,h.SECRECY_TAG,h.SETUP_TAG,h.DENY_VIEW_FIELD_MASTER,h.DENY_VIEW_FIELD_DETAIL
            FROM dbo.SYSDH h WITH (NOLOCK)
            WHERE h.M_IDX=@ModuleId AND h.G_IDX IN
              (SELECT G_IDX FROM dbo.SYSDG_USER WITH (NOLOCK) WHERE USER_ID=@UserId);
            """;
        await using var command = new SqlCommand(groupSql, connection);
        AddParameters(command, userId, moduleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var groups = new List<RightRow>();
        while (await reader.ReadAsync(cancellationToken)) groups.Add(ReadRow(reader));
        if (groups.Count == 0)
        {
            var none = new LegacyModuleRights(false, false, false, false,
                new HashSet<string>(), new HashSet<string>());
            LogRights(userId, moduleId, "none", none);
            return none;
        }

        var execute = groups.Select(row => row.Execute).OrderByDescending(value => value, StringComparer.Ordinal).First();
        var rights = new LegacyModuleRights(
            !string.Equals(execute, "A", StringComparison.OrdinalIgnoreCase),
            groups.Any(row => row.Cost), groups.Any(row => row.Secrecy), groups.Any(row => row.Setup),
            IntersectDenied(groups.Select(row => row.DeniedMaster)),
            IntersectDenied(groups.Select(row => row.DeniedDetail)));
        LogRights(userId, moduleId, $"group({groups.Count})", rights);
        return rights;
    }

    private void LogRights(string userId, int moduleId, string source, LegacyModuleRights rights) =>
        logger.LogDebug(
            "模块权限 userId={UserId} module={ModuleId} source={Source} browse={CanBrowse} cost={CanViewCost} secrecy={CanViewSecrecy} setup={CanSetup} deniedMaster={DeniedMasterCount} deniedDetail={DeniedDetailCount}",
            userId, moduleId, source, rights.CanBrowse, rights.CanViewCost, rights.CanViewSecrecy, rights.CanSetup,
            rights.DeniedMasterFields.Count, rights.DeniedDetailFields.Count);

    private static LegacyModuleRights ToRights(RightRow row, bool found) => new(
        found && !string.Equals(row.Execute, "A", StringComparison.OrdinalIgnoreCase),
        row.Cost, row.Secrecy, row.Setup, ParseDenied(row.DeniedMaster), ParseDenied(row.DeniedDetail));

    private static IReadOnlySet<string> IntersectDenied(IEnumerable<string> values)
    {
        HashSet<string>? result = null;
        foreach (var value in values)
        {
            var current = ParseDenied(value);
            if (result is null) result = current;
            else result.IntersectWith(current);
        }
        return result ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ParseDenied(string? value) =>
        (value ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task<List<RightRow>> ReadRowsAsync(SqlConnection connection, string table, string idColumn, string userId, int moduleId, CancellationToken token)
    {
        var sql = $"SELECT EXEC_TAG,COST_TAG,SECRECY_TAG,SETUP_TAG,DENY_VIEW_FIELD_MASTER,DENY_VIEW_FIELD_DETAIL FROM dbo.{table} WITH (NOLOCK) WHERE {idColumn}=@UserId AND M_IDX=@ModuleId";
        await using var command = new SqlCommand(sql, connection);
        AddParameters(command, userId, moduleId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<RightRow>();
        while (await reader.ReadAsync(token)) rows.Add(ReadRow(reader));
        return rows;
    }

    private static RightRow ReadRow(SqlDataReader reader) => new(
        reader.GetNullableString("EXEC_TAG") ?? "A", reader.GetNullableBoolean("COST_TAG"),
        reader.GetNullableBoolean("SECRECY_TAG"), reader.GetNullableBoolean("SETUP_TAG"),
        reader.GetNullableString("DENY_VIEW_FIELD_MASTER") ?? string.Empty,
        reader.GetNullableString("DENY_VIEW_FIELD_DETAIL") ?? string.Empty);

    private static void AddParameters(SqlCommand command, string userId, int moduleId)
    {
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
    }
    private SqlConnection CreateConnection() => connections.Create();
    private sealed record RightRow(string Execute, bool Cost, bool Secrecy, bool Setup, string DeniedMaster, string DeniedDetail);
}

internal static class LegacyRightsReaderExtensions
{
    public static bool GetNullableBoolean(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal);
    }
}
