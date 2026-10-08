using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 元数据写路径脏标记：2302 字段维护、工作台列设置/列宽、菜单默认列
/// 保存后仅把模块标记为「脏」，不逐次生成快照；由发布动作批量校验并生成快照。
/// 支持独立连接与事务内（与业务写入同生共死）两种方式。
///
/// **只标统一工作台模块**（判据取自 `dbo.V_MODULE_NODE`，两个入口同一处过滤）：
/// 目录节点与自定义承载页不装配工作台定义，发布门会直接拒绝它们，标脏只会在"待发布"清单里
/// 挂上永远发布不出来的条目（历史遗留的那批已由迁移清理）。把过滤放在这里而不是各调用方，
/// 是因为"改这张表要重发布"的调用方很多，任何一个漏判都会重新长出孤儿脏标记。
/// 形态从工作台切走的节点，其残留脏标记由 <see cref="ClearDirtyAsync"/> 撤掉。
/// </summary>
public sealed class WorkbenchDirtyMarker(DbConnectionFactory connections)
{
    public async Task MarkDirtyAsync(int moduleId, string? updatedBy, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await MarkDirtyCoreAsync(connection, null, moduleId, updatedBy, token);
    }

    public async Task MarkDirtyForTableAsync(string table, string? updatedBy, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await MarkDirtyForTableCoreAsync(connection, null, table, updatedBy, token);
    }

    public async Task MarkDirtyAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId, string? updatedBy, CancellationToken token)
        => await MarkDirtyCoreAsync(connection, transaction, moduleId, updatedBy, token);

    public async Task MarkDirtyForTableAsync(
        SqlConnection connection, SqlTransaction transaction, string table, string? updatedBy, CancellationToken token)
        => await MarkDirtyForTableCoreAsync(connection, transaction, table, updatedBy, token);

    /// <summary>撤掉某个模块的脏标记（模块不再是工作台形态、或其编号已不存在时用）。</summary>
    public async Task ClearDirtyAsync(SqlConnection connection, SqlTransaction transaction, int moduleId, CancellationToken token)
    {
        const string sql = "DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX=@ModuleId;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task MarkDirtyCoreAsync(
        SqlConnection connection, SqlTransaction? transaction, int moduleId, string? updatedBy, CancellationToken token)
    {
        const string sql = """
            MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
            USING (
                SELECT @ModuleId AS M_IDX
                WHERE EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE n
                              WHERE n.M_IDX=@ModuleId AND n.NODE_KIND=N'WORKBENCH')
            ) AS s ON t.M_IDX = s.M_IDX
            WHEN MATCHED THEN UPDATE SET DIRTY_TAG=1, LAST_MODIFIED_BY=@UpdatedBy, LAST_MODIFIED_AT=SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
                VALUES (s.M_IDX, 1, @UpdatedBy, SYSDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 100).Value = (object?)updatedBy ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task MarkDirtyForTableCoreAsync(
        SqlConnection connection, SqlTransaction? transaction, string table, string? updatedBy, CancellationToken token)
    {
        const string sql = """
            MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
            USING (
                SELECT m.M_IDX
                FROM dbo.MODULES m
                INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX=m.M_IDX AND n.NODE_KIND=N'WORKBENCH'
                WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))) = @Table
                   OR LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE,''))) = @Table
            ) AS s ON t.M_IDX = s.M_IDX
            WHEN MATCHED THEN UPDATE SET DIRTY_TAG=1, LAST_MODIFIED_BY=@UpdatedBy, LAST_MODIFIED_AT=SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
                VALUES (s.M_IDX, 1, @UpdatedBy, SYSDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 100).Value = (object?)updatedBy ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }
}
