using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 元数据写路径脏标记：2302 字段维护、工作台列设置/列宽、菜单默认列
/// 保存后仅把模块标记为「脏」，不逐次生成快照；由发布动作批量校验并生成快照。
/// 支持独立连接与事务内（与业务写入同生共死）两种方式。
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

    private static async Task MarkDirtyCoreAsync(
        SqlConnection connection, SqlTransaction? transaction, int moduleId, string? updatedBy, CancellationToken token)
    {
        const string sql = """
            MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
            USING (SELECT @ModuleId AS M_IDX) AS s ON t.M_IDX = s.M_IDX
            WHEN MATCHED THEN UPDATE SET DIRTY_TAG=1, LAST_MODIFIED_BY=@UpdatedBy, LAST_MODIFIED_AT=SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
                VALUES (@ModuleId, 1, @UpdatedBy, SYSDATETIME());
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
                SELECT m.M_IDX FROM dbo.MODULES m
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
