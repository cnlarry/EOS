using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Metrics;

public sealed record MetricDefinitionRow(
    string MetricId,
    string MetricName,
    string Definition,
    string SourceTable,
    string DimensionKeys,
    int Version,
    string ConfirmStatus,
    string? RowFilter,
    string? Description);

public interface IMetricRepository
{
    Task<MetricDefinitionRow?> GetAsync(string metricId, CancellationToken token);

    /// <summary>
    /// 按业务词查已确认的口径：标识、名称、说明三处匹配，标识/名称精确命中排在前面。
    /// 供"用户说的是业务词而不是标识"时解析——命中唯一才可用，多条必须让调用方指定。
    /// 只返回 <c>CONFIRMED</c>：候选口径不能用于计算。
    /// </summary>
    Task<IReadOnlyList<MetricDefinitionRow>> SearchConfirmedAsync(string keyword, CancellationToken token);

    /// <summary>
    /// Modules whose master or detail table matches the metric source table,
    /// master matches first — the module owning the table anchors permission
    /// scoping for computation.
    /// </summary>
    Task<IReadOnlyList<int>> FindModuleIdsByTableAsync(string table, CancellationToken token);
}

public sealed class MetricRepository(DbConnectionFactory connections) : IMetricRepository
{
    public async Task<MetricDefinitionRow?> GetAsync(string metricId, CancellationToken token)
    {
        const string sql = """
            SELECT METRIC_ID, METRIC_NAME, DEFINITION,
                   LTRIM(RTRIM(ISNULL(SOURCE_TABLE, ''))) AS SOURCE_TABLE,
                   LTRIM(RTRIM(ISNULL(DIMENSION_KEYS, ''))) AS DIMENSION_KEYS,
                   VERSION,
                   LTRIM(RTRIM(ISNULL(CONFIRM_STATUS, 'CANDIDATE'))) AS CONFIRM_STATUS,
                   ROW_FILTER, DESCRIPTION
            FROM dbo.REPORT_METRIC WITH (NOLOCK)
            WHERE METRIC_ID = @MetricId;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@MetricId", SqlDbType.NVarChar, 50).Value = metricId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return null;
        }
        return ReadRow(reader);
    }

    public async Task<IReadOnlyList<MetricDefinitionRow>> SearchConfirmedAsync(string keyword, CancellationToken token)
    {
        const string sql = """
            SELECT TOP (10) METRIC_ID, METRIC_NAME, DEFINITION,
                   LTRIM(RTRIM(ISNULL(SOURCE_TABLE, ''))) AS SOURCE_TABLE,
                   LTRIM(RTRIM(ISNULL(DIMENSION_KEYS, ''))) AS DIMENSION_KEYS,
                   VERSION,
                   LTRIM(RTRIM(ISNULL(CONFIRM_STATUS, 'CANDIDATE'))) AS CONFIRM_STATUS,
                   ROW_FILTER, DESCRIPTION
            FROM dbo.REPORT_METRIC WITH (NOLOCK)
            WHERE LTRIM(RTRIM(ISNULL(CONFIRM_STATUS, 'CANDIDATE'))) = 'CONFIRMED'
              AND (METRIC_ID = @Keyword
                   OR LTRIM(RTRIM(METRIC_NAME)) = @Keyword
                   OR METRIC_NAME LIKE '%' + @Keyword + '%'
                   OR DESCRIPTION LIKE '%' + @Keyword + '%')
            ORDER BY CASE WHEN METRIC_ID = @Keyword THEN 0
                          WHEN LTRIM(RTRIM(METRIC_NAME)) = @Keyword THEN 1 ELSE 2 END,
                     METRIC_ID;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 100).Value = keyword;
        var rows = new List<MetricDefinitionRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(ReadRow(reader));
        }
        return rows;
    }

    private static MetricDefinitionRow ReadRow(SqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    public async Task<IReadOnlyList<int>> FindModuleIdsByTableAsync(string table, CancellationToken token)
    {
        const string sql = """
            SELECT M_IDX
            FROM dbo.MODULES WITH (NOLOCK)
            WHERE LTRIM(RTRIM(ISNULL(MASTER_TABLE, ''))) = @Table
               OR LTRIM(RTRIM(ISNULL(DETAIL_TABLE, ''))) = @Table
            ORDER BY CASE WHEN LTRIM(RTRIM(ISNULL(MASTER_TABLE, ''))) = @Table THEN 0 ELSE 1 END, M_IDX;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = table;
        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            ids.Add(reader.GetInt32(0));
        }
        return ids;
    }
}
