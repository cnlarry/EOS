using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Situation;

/// <summary>汇报关系与工作组（身份段的补充事实）。</summary>
public sealed record SituationRelationship(
    string? DirectLeader,
    string? DepartmentLeader,
    IReadOnlyList<string> WorkGroups);

/// <summary>审计里的一条失败/被拒事实（未格式化，配额与截断由调用方决定）。</summary>
public sealed record SituationFailureFact(
    DateTime OccurredAt,
    string Action,
    int? ModuleId,
    string Summary,
    string? ErrorCode);

/// <summary>配置标识符的存在性探测结果。</summary>
public sealed record SituationTargetProbe(bool TableExists, bool FieldExists, bool ActionExists)
{
    public static SituationTargetProbe Empty { get; } = new(false, false, false);
}

/// <summary>
/// 处境的**事实读取**接口：身份补充、待办计数、最近被拒事件、最近活动模块、配置标识符探测。
/// 全部按当前用户参数化查询，且只读。
/// </summary>
public interface ISituationFactsReader
{
    Task<SituationRelationship> LoadRelationshipAsync(string userId, string departmentId, CancellationToken token);

    Task<SituationPending> LoadPendingAsync(string userId, CancellationToken token);

    Task<IReadOnlyList<SituationFailureFact>> LoadRecentFailuresAsync(
        string userId, int days, int limit, CancellationToken token);

    Task<IReadOnlyList<int>> LoadActivityModulesAsync(
        string userId, int days, int limit, CancellationToken token);

    Task<SituationTargetProbe> ProbeConfigTargetAsync(
        string tableId, string fieldId, long? actionId, CancellationToken token);
}

/// <summary>处境的库内事实源（唯一一处写这些 SQL 的地方）。</summary>
public sealed class SituationFactsReader(
    DbConnectionFactory connections,
    ILogger<SituationFactsReader> logger) : ISituationFactsReader
{
    public async Task<SituationRelationship> LoadRelationshipAsync(
        string userId, string departmentId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        string? directLeader = null;
        var workGroups = new List<string>();
        await using (var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(ISNULL(l.SIR_ID, ''))), LTRIM(RTRIM(ISNULL(s.EMP_ID, ''))), LTRIM(RTRIM(ISNULL(e.EMP_NAME, '')))
            FROM dbo.SYSDL l WITH (NOLOCK)
            LEFT JOIN dbo.SYSDL s WITH (NOLOCK) ON LTRIM(RTRIM(s.USER_ID)) = LTRIM(RTRIM(l.SIR_ID))
            LEFT JOIN dbo.HR_EMPLOYEE e WITH (NOLOCK) ON LTRIM(RTRIM(e.EMP_ID)) = LTRIM(RTRIM(s.EMP_ID))
            WHERE LTRIM(RTRIM(l.USER_ID)) = @UserId;
            """, connection))
        {
            command.Parameters.Add("@UserId", SqlDbType.NVarChar, 40).Value = userId;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                directLeader = FirstNonEmpty(reader.GetString(2), reader.GetString(1), reader.GetString(0));
            }
        }

        await using (var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(ISNULL(g.G_DESC, '')))
            FROM dbo.SYSDG_USER u WITH (NOLOCK)
            JOIN dbo.SYSDG g WITH (NOLOCK) ON LTRIM(RTRIM(g.G_IDX)) = LTRIM(RTRIM(u.G_IDX))
            WHERE LTRIM(RTRIM(u.USER_ID)) = @UserId
            ORDER BY g.G_IDX;
            """, connection))
        {
            command.Parameters.Add("@UserId", SqlDbType.NVarChar, 40).Value = userId;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var name = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(name)) workGroups.Add(name);
            }
        }

        string? departmentLeader = null;
        if (!string.IsNullOrWhiteSpace(departmentId))
        {
            await using var command = new SqlCommand("""
                SELECT LTRIM(RTRIM(ISNULL(d.EMP_ID_LEADER, ''))), LTRIM(RTRIM(ISNULL(e.EMP_NAME, '')))
                FROM dbo.DEPT d WITH (NOLOCK)
                LEFT JOIN dbo.HR_EMPLOYEE e WITH (NOLOCK) ON LTRIM(RTRIM(e.EMP_ID)) = LTRIM(RTRIM(d.EMP_ID_LEADER))
                WHERE LTRIM(RTRIM(d.DEPT_ID)) = @DeptId;
                """, connection);
            command.Parameters.Add("@DeptId", SqlDbType.NVarChar, 40).Value = departmentId;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                departmentLeader = FirstNonEmpty(reader.GetString(1), reader.GetString(0));
            }
        }

        logger.LogDebug("助手处境身份采集 user={UserId} groups={Groups} hasLeader={Leader}",
            userId, workGroups.Count, departmentLeader is not null);
        return new SituationRelationship(directLeader, departmentLeader, workGroups);
    }

    public async Task<SituationPending> LoadPendingAsync(string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var myApproval = await CountAsync(connection, """
            SELECT COUNT_BIG(1) FROM dbo.WF_MYTASK T WITH (NOLOCK)
            WHERE LTRIM(RTRIM(T.APPROVER)) = @UserId AND ISNULL(T.APPROVE_STATE, '') = ''
              AND ISNULL(T.APPROVE_TAG, 0) = 0;
            """, userId, token);
        var started = await CountAsync(connection, """
            SELECT COUNT_BIG(1) FROM dbo.WF_MONITOR M WITH (NOLOCK)
            WHERE LTRIM(RTRIM(ISNULL(M.START_USER, ''))) = @UserId AND M.WF_STATE = '0';
            """, userId, token);
        return new SituationPending(myApproval, started);
    }

    public async Task<IReadOnlyList<SituationFailureFact>> LoadRecentFailuresAsync(
        string userId, int days, int limit, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT TOP (@Max) OCCURRED_AT, ACTION, M_IDX, SUMMARY, ERROR_CODE
            FROM dbo.AUDIT_EVENT WITH (NOLOCK)
            WHERE LTRIM(RTRIM(ACTOR_USER_ID)) = @UserId AND RESULT = 0 AND OCCURRED_AT >= @Since
            ORDER BY OCCURRED_AT DESC, EVENT_ID DESC;
            """, connection);
        command.Parameters.Add("@Max", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 40).Value = userId;
        command.Parameters.Add("@Since", SqlDbType.DateTime2).Value = DateTime.UtcNow.AddDays(-days);
        var items = new List<SituationFailureFact>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            items.Add(new SituationFailureFact(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return items;
    }

    public async Task<IReadOnlyList<int>> LoadActivityModulesAsync(
        string userId, int days, int limit, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT TOP (@Max) M_IDX
            FROM dbo.AUDIT_EVENT WITH (NOLOCK)
            WHERE LTRIM(RTRIM(ACTOR_USER_ID)) = @UserId AND M_IDX IS NOT NULL AND OCCURRED_AT >= @Since
            GROUP BY M_IDX
            ORDER BY MAX(OCCURRED_AT) DESC;
            """, connection);
        command.Parameters.Add("@Max", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 40).Value = userId;
        command.Parameters.Add("@Since", SqlDbType.DateTime2).Value = DateTime.UtcNow.AddDays(-days);
        var modules = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            modules.Add(reader.GetInt32(0));
        }

        return modules;
    }

    public async Task<SituationTargetProbe> ProbeConfigTargetAsync(
        string tableId, string fieldId, long? actionId, CancellationToken token)
    {
        if (tableId.Length == 0 && actionId is null) return SituationTargetProbe.Empty;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT
              CASE WHEN @TableId <> '' AND EXISTS (SELECT 1 FROM dbo.FIELDS f WITH (NOLOCK)
                   WHERE LTRIM(RTRIM(f.T_ID)) = @TableId) THEN 1 ELSE 0 END,
              CASE WHEN @FieldId <> '' AND EXISTS (SELECT 1 FROM dbo.FIELDS f WITH (NOLOCK)
                   WHERE LTRIM(RTRIM(f.T_ID)) = @TableId AND LTRIM(RTRIM(f.F_ID)) = @FieldId) THEN 1 ELSE 0 END,
              CASE WHEN @ActionId > 0 AND EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a WITH (NOLOCK)
                   WHERE a.ACTION_ID = @ActionId) THEN 1 ELSE 0 END;
            """, connection);
        command.Parameters.Add("@TableId", SqlDbType.NVarChar, 100).Value = tableId;
        command.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = fieldId;
        command.Parameters.Add("@ActionId", SqlDbType.BigInt).Value = actionId ?? 0L;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return SituationTargetProbe.Empty;
        var probe = new SituationTargetProbe(
            reader.GetInt32(0) == 1, reader.GetInt32(1) == 1, reader.GetInt32(2) == 1);
        logger.LogDebug("助手配置处境标识符校验 table={Table} field={Field} action={Action} ok={TableOk}/{FieldOk}/{ActionOk}",
            tableId, fieldId, actionId, probe.TableExists, probe.FieldExists, probe.ActionExists);
        return probe;
    }

    private static async Task<long> CountAsync(
        SqlConnection connection, string sql, string userId, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 40).Value = userId;
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }

    private static string? FirstNonEmpty(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        }

        return null;
    }
}
