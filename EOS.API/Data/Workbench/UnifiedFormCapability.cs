using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Workbench;

/// <summary>
/// `MODULES.UNIFIED_FORM_ENABLED` / `UNIFIED_FORM_EDIT_ENABLED` 的取值。
///
/// - <see cref="FormEnabled"/> = 0：本模块**只有列表**，双击不进表单（汇总/查询类模块）；
/// - <see cref="FormEnabled"/> = 1 且 <see cref="EditEnabled"/> = 0：表单可打开、**只能浏览**——
///   表由系统/引擎维护（审计 `AUDIT_EVENT`、库存账 `INV_PRO_DEPOT` / `INV_BATCH_M`、
///   应收未收明细 `COP_ACCOUNT_M`），通用表单写入会绕过真正的写入者，故写路径必须封死；
/// - 两位都为 1：表单可用且可写。
///
/// 库约束 `CK_MODULES_UNIFIED_FORM_EDIT` 保证不会出现"未启用表单却可写"的组合。
/// </summary>
public readonly record struct UnifiedFormCapability(bool FormEnabled, bool EditEnabled)
{
    /// <summary>没有模块行 / 读不到位时的取值：一律按"没有表单、不可写"处理（fail-closed）。</summary>
    public static readonly UnifiedFormCapability None = new(false, false);
}

/// <summary>
/// 统一表单能力的**唯一读取点**。这两位是模块级声明，与批核入口 `APPROVE_ENABLED` 同口径：
/// **现读、不进定义快照**——改完即刻生效，不需要标脏/重发布（快照里带不上它们，
/// 也不该带：它们不是定义内容，而是"这个模块有没有表单面"的事实）。
///
/// 收敛成一处的理由：写路径的判定记号只能有一个来源（`check-workbench-policy-single-entry.ps1`
/// 守"判定不在控制器里"），而浏览目标、发布管理枚举与发布门、导入候选集、助手能力目录
/// 这几处消费点要的是**同一份真值**——散着写 SQL 迟早各写各的。
/// </summary>
public interface IUnifiedFormCapabilitySource
{
    /// <summary>单个模块的两位能力（模块不存在时返回 <see cref="UnifiedFormCapability.None"/>）。</summary>
    Task<UnifiedFormCapability> GetAsync(int moduleId, CancellationToken token);

    /// <summary>
    /// 统一表单可用的模块集合（含"仅浏览"的模块）。
    /// 消费方：跨模块浏览链接目标、发布管理枚举、发布门"启用"判据。
    /// </summary>
    Task<IReadOnlySet<int>> GetFormEnabledIdsAsync(CancellationToken token);

    /// <summary>允许经统一表单写入的模块集合。消费方：按模块导入的候选集。</summary>
    Task<IReadOnlySet<int>> GetWritableIdsAsync(CancellationToken token);
}

/// <summary>库实现：读 `dbo.MODULES` 的两列（单行按主键取值，集合按列筛选）。</summary>
public sealed class UnifiedFormCapabilitySource(DbConnectionFactory connections) : IUnifiedFormCapabilitySource
{
    public async Task<UnifiedFormCapability> GetAsync(int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            """
            SELECT ISNULL(UNIFIED_FORM_ENABLED, 0), ISNULL(UNIFIED_FORM_EDIT_ENABLED, 0)
            FROM dbo.MODULES WHERE M_IDX = @ModuleId;
            """, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return UnifiedFormCapability.None;
        }
        return new UnifiedFormCapability(reader.GetBoolean(0), reader.GetBoolean(1));
    }

    public Task<IReadOnlySet<int>> GetFormEnabledIdsAsync(CancellationToken token)
        => ReadIdsAsync("SELECT M_IDX FROM dbo.MODULES WHERE UNIFIED_FORM_ENABLED = 1;", token);

    public Task<IReadOnlySet<int>> GetWritableIdsAsync(CancellationToken token)
        => ReadIdsAsync(
            "SELECT M_IDX FROM dbo.MODULES WHERE UNIFIED_FORM_ENABLED = 1 AND UNIFIED_FORM_EDIT_ENABLED = 1;",
            token);

    private async Task<IReadOnlySet<int>> ReadIdsAsync(string sql, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new HashSet<int>();
        while (await reader.ReadAsync(token))
        {
            result.Add(reader.GetInt32(0));
        }
        return result;
    }
}
