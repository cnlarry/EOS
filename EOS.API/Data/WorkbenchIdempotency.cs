using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>已完成的幂等结果（重复提交直接返回）。</summary>
public sealed record WorkbenchIdempotencyRecord(string? ResultKey, bool FlowStarted);

/// <summary>
/// 写路径幂等：
/// 事务内 claim/complete，业务失败回滚即释放；重复提交返回缓存结果，不重复产生副作用。
/// 并发竞争由主键约束兜底（重复插入重读既有结果）。
/// </summary>
public sealed class WorkbenchIdempotency
{
    public async Task<WorkbenchIdempotencyRecord?> TryClaimAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string key,
        int moduleId,
        string action,
        CancellationToken token)
    {
        const string selectSql = """
            SELECT RESULT_KEY, FLOW_STARTED
            FROM dbo.WORKBENCH_IDEMPOTENCY WITH (UPDLOCK, HOLDLOCK)
            WHERE IDEMPOTENCY_KEY=@Key;
            """;
        await using (var select = new SqlCommand(selectSql, connection, transaction))
        {
            select.Parameters.Add("@Key", SqlDbType.NVarChar, 128).Value = key;
            await using var reader = await select.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                return new WorkbenchIdempotencyRecord(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetBoolean(1));
            }
        }

        const string insertSql = """
            INSERT INTO dbo.WORKBENCH_IDEMPOTENCY (IDEMPOTENCY_KEY, M_IDX, ACTION)
            VALUES (@Key, @ModuleId, @Action);
            """;
        try
        {
            await using var insert = new SqlCommand(insertSql, connection, transaction);
            insert.Parameters.Add("@Key", SqlDbType.NVarChar, 128).Value = key;
            insert.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            insert.Parameters.Add("@Action", SqlDbType.NVarChar, 20).Value = action;
            await insert.ExecuteNonQueryAsync(token);
            return null;
        }
        catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
        {
            // 并发重复 claim：读既有结果（可能尚未 complete，RESULT_KEY 为 NULL）
            await using var select = new SqlCommand(selectSql, connection, transaction);
            select.Parameters.Add("@Key", SqlDbType.NVarChar, 128).Value = key;
            await using var reader = await select.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                return new WorkbenchIdempotencyRecord(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetBoolean(1));
            }
            return new WorkbenchIdempotencyRecord(null, false);
        }
    }

    public async Task CompleteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string key,
        string? resultKey,
        bool flowStarted,
        CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.WORKBENCH_IDEMPOTENCY
            SET RESULT_KEY=@ResultKey, FLOW_STARTED=@FlowStarted
            WHERE IDEMPOTENCY_KEY=@Key;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ResultKey", SqlDbType.NVarChar, 1000).Value = (object?)resultKey ?? DBNull.Value;
        command.Parameters.Add("@FlowStarted", SqlDbType.Bit).Value = flowStarted;
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 128).Value = key;
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task ReleaseAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string key,
        CancellationToken token)
    {
        const string sql = "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE IDEMPOTENCY_KEY=@Key;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 128).Value = key;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// 只读查一次既有结果（自开连接）：用于**没有共享事务**的一批操作在开始前判"这一批做过没有"。
    /// 它不作写入依据——真正防重的仍是写入路径内的事务内抢占。
    ///
    /// <para>
    /// **这条查询不加"读不加锁"提示**（此前有一条，已去掉）：它读出来的正是"这一批做过没有"的
    /// 判断依据，而脏读可能读到一笔随后回滚的插入——那一批操作会被永久跳过（既没做过、也不会再做）。
    /// 这张表按主键取单行，读锁的争用可以忽略，省不下什么。
    /// </para>
    /// </summary>
    public static async Task<WorkbenchIdempotencyRecord?> TryReadAsync(
        DbConnectionFactory connections, string key, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT RESULT_KEY, FLOW_STARTED
            FROM dbo.WORKBENCH_IDEMPOTENCY
            WHERE IDEMPOTENCY_KEY=@Key;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 128).Value = key;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return null;
        }
        return new WorkbenchIdempotencyRecord(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetBoolean(1));
    }

    /// <summary>
    /// 一步记录（自开连接 + 事务，抢占后立即写结果）：供"一次调用含多段独立事务"的写入在成功后留痕，
    /// 使同一批的重复提交能在开始前就被识别。已有记录不改写。
    /// </summary>
    public async Task RecordAsync(
        DbConnectionFactory connections,
        string key,
        int moduleId,
        string action,
        string? resultKey,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            if (await TryClaimAsync(connection, transaction, key, moduleId, action, token) is null)
            {
                await CompleteAsync(connection, transaction, key, resultKey, flowStarted: false, token);
            }
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }
}
