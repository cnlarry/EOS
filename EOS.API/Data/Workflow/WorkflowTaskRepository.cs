using System.Data;

using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Workflow;

/// <summary>
/// 待办工作台（2102「我的任务」）与流程动作所需的工作流域读数。
///
/// 两件事此前写在 <c>WorkflowController</c> 里：逐模块取标题（N+1）与未批核计数、以及按模块号取主键列。
/// 控制器只该做契约与授权，数据访问落在这里（ADR-036 批 1 第 5 项）。
///
/// 模块与表名**只来自服务端白名单** <see cref="ModuleBusinessMap.MasterTables"/>；表名再过一道
/// <see cref="WorkbenchSql.Identifier"/> 校验后才拼进方括号。计数走 <c>WITH (NOLOCK)</c>：
/// 它是只读的看板数字，不需要与批核事务强一致（强一致路径不得加 NOLOCK，见 NOLOCK 白名单约定）。
/// </summary>
public sealed class WorkflowTaskRepository(DbConnectionFactory connections)
{
    /// <summary>一个模块的待办计数行。</summary>
    public sealed record PendingTask(int ModuleId, string Title, long Pending);

    /// <summary>
    /// 各模块未批核（<c>CONFIRM_TAG=0</c>）单据计数。模块清单由调用方按权限过滤后传入——
    /// 权限判定留在控制器侧的策略入口，这里只负责取数。
    /// </summary>
    public async Task<IReadOnlyList<PendingTask>> CountPendingAsync(
        IReadOnlyList<(int ModuleId, string Table)> modules, CancellationToken token)
    {
        if (modules.Count == 0) return [];
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        // 标题一次取回：逐模块查标题是 N+1，模块上百个时这里就是上百次往返。
        var titles = await LoadTitlesAsync(connection, modules.Select(module => module.ModuleId).ToList(), token);

        var result = new List<PendingTask>(modules.Count);
        foreach (var (moduleId, table) in modules)
        {
            if (!WorkbenchSql.Identifier.IsMatch(table)) continue;
            await using var command = new SqlCommand(
                $"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK) WHERE ISNULL(CONFIRM_TAG,0)=0;", connection);
            var pending = Convert.ToInt64(await command.ExecuteScalarAsync(token));
            result.Add(new PendingTask(moduleId, titles.GetValueOrDefault(moduleId, string.Empty), pending));
        }
        return result;
    }

    /// <summary>按模块号取主表主键列（表名来自 <c>MODULES.MASTER_TABLE</c>，经标识符校验后使用）。</summary>
    public async Task<IReadOnlyList<string>> GetModulePrimaryKeysAsync(int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var table = await ReadMasterTableAsync(connection, moduleId, token);
        if (string.IsNullOrWhiteSpace(table) || !WorkbenchSql.Identifier.IsMatch(table)) return [];
        return await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, null, table, token);
    }

    private static async Task<string?> ReadMasterTableAsync(SqlConnection connection, int moduleId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))) FROM dbo.MODULES WHERE M_IDX=@ModuleId;", connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return await command.ExecuteScalarAsync(token) as string;
    }

    private static async Task<Dictionary<int, string>> LoadTitlesAsync(
        SqlConnection connection, IReadOnlyList<int> moduleIds, CancellationToken token)
    {
        var placeholders = string.Join(',', moduleIds.Select((_, index) => $"@M{index}"));
        await using var command = new SqlCommand(
            $"SELECT M_IDX, LTRIM(RTRIM(ISNULL(M_DESC,''))) FROM dbo.MODULES WHERE M_IDX IN ({placeholders});", connection);
        for (var index = 0; index < moduleIds.Count; index++)
        {
            command.Parameters.AddWithValue($"@M{index}", moduleIds[index]);
        }
        var titles = new Dictionary<int, string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            titles[reader.GetInt32(0)] = reader.GetString(1);
        }
        return titles;
    }
}
