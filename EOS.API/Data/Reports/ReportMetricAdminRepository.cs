using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Reports;

/// <summary>口径维护用的完整行（含待确认项，供 2201 报表设置页的"指标口径"区展示）。</summary>
public sealed record ReportMetricAdminItem(
    string MetricId,
    string MetricName,
    string Definition,
    string SourceTable,
    string DimensionKeys,
    string Domain,
    int Version,
    string ConfirmStatus,
    string? RowFilter,
    string? Description,
    string CreatedBy,
    DateTime? ModifiedTime,
    string? ApprovedBy,
    DateTime? ApprovedTime);

/// <summary>口径新增/修改草稿。`MetricId` 是身份，修改时不允许改。</summary>
public sealed record ReportMetricAdminDraft(
    string MetricId,
    string MetricName,
    string Definition,
    string SourceTable,
    string? DimensionKeys,
    string? Domain,
    string? RowFilter,
    string? Description,
    string ConfirmStatus);

/// <summary>
/// `REPORT_METRIC` 的维护面（新增/修改/删除）。
///
/// <para>
/// 口径此前只能靠迁移登记，客户环境里口径名/维度不合适就只能找开发（ADR-046 的 `FIX_AT=metric`）。
/// 这里把写路径摆出来，但**校验必须比界面更严**：表达式走受控解析、来源表与维度必须是物理列、
/// 行过滤的表归属与关联列由 <see cref="Features.Assistant.Metrics.MetricDefinitionValidator"/> 统一把关。
/// </para>
///
/// <para>
/// **删除只允许候选口径**：已确认口径可能被报表与结论引用，删掉会让历史结论失去来源；
/// 要删先把它改回候选（这一步本身就是一次显式决策）。
/// </para>
/// </summary>
public sealed class ReportMetricAdminRepository(DbConnectionFactory connections)
{
    public async Task<IReadOnlyList<ReportMetricAdminItem>> ListAsync(CancellationToken token)
    {
        const string sql = """
            SELECT METRIC_ID, METRIC_NAME, DEFINITION, LTRIM(RTRIM(ISNULL(SOURCE_TABLE, ''))),
                   LTRIM(RTRIM(ISNULL(DIMENSION_KEYS, ''))), LTRIM(RTRIM(ISNULL(DOMAIN, ''))),
                   VERSION, LTRIM(RTRIM(ISNULL(CONFIRM_STATUS, 'CANDIDATE'))), ROW_FILTER, DESCRIPTION,
                   LTRIM(RTRIM(ISNULL(CREATED_BY, ''))), MODIFIED_TIME,
                   NULLIF(LTRIM(RTRIM(ISNULL(APPROVED_BY, ''))), ''), APPROVED_TIME
            FROM dbo.REPORT_METRIC
            ORDER BY DOMAIN, METRIC_ID;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var items = new List<ReportMetricAdminItem>();
        while (await reader.ReadAsync(token))
        {
            items.Add(new ReportMetricAdminItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetDateTime(13)));
        }
        return items;
    }

    public async Task<bool> ExistsAsync(string metricId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.REPORT_METRIC WHERE METRIC_ID = @Id;", connection);
        command.Parameters.Add("@Id", SqlDbType.NVarChar, 50).Value = metricId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) > 0;
    }

    public async Task CreateAsync(ReportMetricAdminDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.REPORT_METRIC
                (METRIC_ID, METRIC_NAME, DEFINITION, SOURCE_TABLE, DIMENSION_KEYS, DOMAIN, VERSION,
                 DESCRIPTION, ROW_FILTER, CONFIRM_STATUS, CREATED_BY, CREATED_TIME, APPROVED_BY, APPROVED_TIME)
            VALUES (@Id, @Name, @Definition, @SourceTable, @Dimensions, @Domain, 1,
                    @Description, @RowFilter, @Status, @User, SYSDATETIME(),
                    CASE WHEN @Status = N'CONFIRMED' THEN @User END,
                    CASE WHEN @Status = N'CONFIRMED' THEN SYSDATETIME() END);
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        Bind(command, draft, user);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>修改口径：版本号递增；确认状态变化时同步批核人/时间（改回候选则清空）。</summary>
    public async Task<bool> UpdateAsync(string metricId, ReportMetricAdminDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.REPORT_METRIC
            SET METRIC_NAME = @Name,
                DEFINITION = @Definition,
                SOURCE_TABLE = @SourceTable,
                DIMENSION_KEYS = @Dimensions,
                DOMAIN = @Domain,
                DESCRIPTION = @Description,
                ROW_FILTER = @RowFilter,
                CONFIRM_STATUS = @Status,
                VERSION = VERSION + 1,
                MODIFIED_BY = @User,
                MODIFIED_TIME = SYSDATETIME(),
                APPROVED_BY = CASE WHEN @Status = N'CONFIRMED'
                                   THEN COALESCE(NULLIF(@User, ''), APPROVED_BY) END,
                APPROVED_TIME = CASE WHEN @Status = N'CONFIRMED'
                                     THEN COALESCE(APPROVED_TIME, SYSDATETIME()) END
            WHERE METRIC_ID = @Id;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        Bind(command, draft, user);
        command.Parameters["@Id"].Value = metricId;
        return await command.ExecuteNonQueryAsync(token) > 0;
    }

    /// <summary>删除候选口径；已确认的拒绝（先改回候选再删）。</summary>
    public async Task<bool> DeleteAsync(string metricId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            DELETE FROM dbo.REPORT_METRIC
            WHERE METRIC_ID = @Id AND LTRIM(RTRIM(ISNULL(CONFIRM_STATUS, 'CANDIDATE'))) <> N'CONFIRMED';
            """, connection);
        command.Parameters.Add("@Id", SqlDbType.NVarChar, 50).Value = metricId;
        return await command.ExecuteNonQueryAsync(token) > 0;
    }

    private static void Bind(SqlCommand command, ReportMetricAdminDraft draft, string user)
    {
        command.Parameters.Add("@Id", SqlDbType.NVarChar, 50).Value = draft.MetricId.Trim();
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 100).Value = draft.MetricName.Trim();
        command.Parameters.Add("@Definition", SqlDbType.NVarChar, 500).Value = draft.Definition.Trim();
        command.Parameters.Add("@SourceTable", SqlDbType.NVarChar, 100).Value = draft.SourceTable.Trim();
        command.Parameters.Add("@Dimensions", SqlDbType.NVarChar, 200).Value =
            (object?)(draft.DimensionKeys?.Trim() ?? string.Empty) ?? string.Empty;
        command.Parameters.Add("@Domain", SqlDbType.NVarChar, 20).Value =
            (object?)(draft.Domain?.Trim() ?? string.Empty) ?? string.Empty;
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 500).Value =
            (object?)(draft.Description?.Trim()) ?? DBNull.Value;
        command.Parameters.Add("@RowFilter", SqlDbType.NVarChar, 2000).Value =
            string.IsNullOrWhiteSpace(draft.RowFilter) ? DBNull.Value : draft.RowFilter.Trim();
        command.Parameters.Add("@Status", SqlDbType.NVarChar, 20).Value =
            draft.ConfirmStatus.Trim().ToUpperInvariant();
        command.Parameters.Add("@User", SqlDbType.NVarChar, 20).Value = user.Trim();
    }
}
