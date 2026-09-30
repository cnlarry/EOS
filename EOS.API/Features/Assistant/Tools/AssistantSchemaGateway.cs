using System.Data;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Tools;

public sealed record AssistantSchemaView(string Name);
public sealed record AssistantSchemaProcedure(string Name, string Signature);

public interface IAssistantSchemaGateway
{
    /// <summary>库内模块清单（模块号 + 名称），与字段维护页读的是同一张模块定义表。</summary>
    Task<IReadOnlyList<FieldAdminModule>> ListModulesAsync(CancellationToken token);

    Task<IReadOnlyList<FieldAdminTable>> ListTablesAsync(string? kind, CancellationToken token);
    Task<(FieldAdminTableDetail? Table, IReadOnlyList<FieldAdminFieldSummary> Fields, IReadOnlyList<FieldAdminUnmanagedField> Unmanaged)> DescribeTableAsync(string tableId, CancellationToken token);
    Task<IReadOnlyList<AssistantSchemaView>> ListViewsAsync(string? keyword, CancellationToken token);
    Task<IReadOnlyList<AssistantSchemaProcedure>> ListProceduresAsync(string? keyword, CancellationToken token);
}

public sealed class AssistantSchemaGateway(
    FieldAdminRepository fieldAdmin,
    DbConnectionFactory connections) : IAssistantSchemaGateway
{
    public Task<IReadOnlyList<FieldAdminModule>> ListModulesAsync(CancellationToken token) =>
        fieldAdmin.GetModulesAsync(token);

    public Task<IReadOnlyList<FieldAdminTable>> ListTablesAsync(string? kind, CancellationToken token) =>
        fieldAdmin.GetTablesAsync(kind, token);

    public async Task<(FieldAdminTableDetail? Table, IReadOnlyList<FieldAdminFieldSummary> Fields, IReadOnlyList<FieldAdminUnmanagedField> Unmanaged)> DescribeTableAsync(
        string tableId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using (var physical = new SqlCommand("""
            SELECT 1
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id=o.schema_id
            WHERE s.name=N'dbo' AND o.name=@TableId AND o.type IN ('U','V');
            """, connection))
        {
            physical.Parameters.Add("@TableId", SqlDbType.NVarChar, 128).Value = tableId;
            if (await physical.ExecuteScalarAsync(token) is null) return (null, [], []);
        }

        var table = await fieldAdmin.GetTableAsync(tableId, token);
        if (table is null) return (null, [], []);

        var fields = new List<FieldAdminFieldSummary>();
        var page = 1;
        FieldAdminPageResult result;
        do
        {
            result = await fieldAdmin.GetFieldsAsync(tableId, null, page, 100, token);
            fields.AddRange(result.Items);
            page++;
        } while (fields.Count < result.Total && result.Items.Count > 0);

        return (table, fields, await fieldAdmin.GetUnmanagedFieldsAsync(tableId, token));
    }

    public async Task<IReadOnlyList<AssistantSchemaView>> ListViewsAsync(string? keyword, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT o.name
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id=o.schema_id
            WHERE s.name=N'dbo' AND o.type='V'
              AND (@Keyword='' OR o.name LIKE @LikeKeyword)
            ORDER BY o.name;
            """;
        await using var command = new SqlCommand(sql, connection);
        var value = keyword?.Trim() ?? string.Empty;
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 128).Value = value;
        command.Parameters.Add("@LikeKeyword", SqlDbType.NVarChar, 260).Value = "%" + value + "%";
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<AssistantSchemaView>();
        while (await reader.ReadAsync(token)) result.Add(new(reader.GetString(0)));
        return result;
    }

    public async Task<IReadOnlyList<AssistantSchemaProcedure>> ListProceduresAsync(string? keyword, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT o.name, p.parameter_id, p.name, TYPE_NAME(p.user_type_id), p.max_length
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id=o.schema_id
            LEFT JOIN sys.parameters p ON p.object_id=o.object_id
            WHERE s.name=N'dbo' AND o.type='P'
              AND (@Keyword='' OR o.name LIKE @LikeKeyword)
            ORDER BY o.name, p.parameter_id;
            """;
        await using var command = new SqlCommand(sql, connection);
        var value = keyword?.Trim() ?? string.Empty;
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 128).Value = value;
        command.Parameters.Add("@LikeKeyword", SqlDbType.NVarChar, 260).Value = "%" + value + "%";
        await using var reader = await command.ExecuteReaderAsync(token);
        var signatures = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            var name = reader.GetString(0);
            if (!signatures.TryGetValue(name, out var parameters))
                signatures[name] = parameters = [];
            if (!reader.IsDBNull(1))
            {
                var parameter = reader.IsDBNull(2) ? "@parameter" : reader.GetString(2);
                var type = reader.IsDBNull(3) ? "sql_variant" : reader.GetString(3);
                parameters.Add($"{parameter} {type}");
            }
        }

        return signatures.Select(pair => new AssistantSchemaProcedure(pair.Key,
            $"{pair.Key}({string.Join(", ", pair.Value)})")).ToArray();
    }
}
