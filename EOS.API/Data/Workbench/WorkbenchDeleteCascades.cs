using System.Data;
using EOS.API.Data.Inventory;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Workbench;

/// <summary>
/// 删除主档前的从属行处置（按主表登记，闭集）。
///
/// <para>
/// 主档删除会被外键拦下，而其中一部分从属行是**宿主自动生成**的：新建仓库时服务端即写入一条库位哨兵行
/// （`LOCATION_NO='-'`，"未指定位置"），用户既看不到它、也没有逐个删除的入口。若不做任何处置，
/// 这类主档"建出来就删不掉"——外键冲突直接冒泡成 500，界面只说"服务器内部错误"。
/// </para>
/// <para>
/// 口径是**未被库存使用时级联清理这些自动行，被使用时拒绝并给出可读码**：
/// 不能一律级联——库位会被库存余额 / 流水 / 批次明细与货位分配引用，级联删掉等于物理销毁库存台账；
/// 也不能一律拒绝——那样登记错一个仓库就再也退不回来。
/// </para>
/// </summary>
internal static class WorkbenchDeleteCascades
{
    /// <summary>该仓库仍被库存使用时给出的错误码。</summary>
    internal const string DepotInUseCode = "DEPOT_IN_USE";

    /// <summary>拒绝结果：错误码 + 用户可见文案。</summary>
    internal sealed record Refusal(string Code, string Message);

    private static readonly Dictionary<string, Func<SqlConnection, SqlTransaction, IReadOnlyList<string>, CancellationToken, Task<Refusal?>>> Rules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DEPOT"] = CascadeDepotLocationsAsync,
        };

    /// <summary>该主表是否有登记在册的从属行处置。</summary>
    internal static bool HasRule(string masterTable) => Rules.ContainsKey(masterTable);

    /// <summary>
    /// 执行从属行处置。返回 <c>null</c> 表示已清理完毕、可以继续删主表；
    /// 返回非空表示拒绝删除（调用方转成 400 可读错误，不得继续删）。
    /// </summary>
    internal static Task<Refusal?> PrepareAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string masterTable,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
        => Rules.TryGetValue(masterTable, out var rule)
            ? rule(connection, transaction, keyValues, token)
            : Task.FromResult<Refusal?>(null);

    /// <summary>
    /// 仓库：先判该库别在库存域是否还有痕迹，再清理它自动生成的库位行。
    /// 判据与读法都收在 <see cref="InventoryQueryService"/>（库存读取收口）。
    /// </summary>
    private static async Task<Refusal?> CascadeDepotLocationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
    {
        if (keyValues.Count != 1 || string.IsNullOrWhiteSpace(keyValues[0]))
        {
            // 主键不是单列库别（与模块定义不符）：不处置，交给外键守卫与可读错误兜底。
            return null;
        }
        var depot = keyValues[0].Trim();

        var traces = await InventoryQueryService.GetDepotTraceCountsAsync(connection, transaction, depot, token);
        if (traces.Any)
        {
            return new Refusal(DepotInUseCode,
                $"该仓库已有库存记录（{traces.Describe()}），不能删除；请先用库存单据清空或改为停用。");
        }

        var allocated = await CountAsync(connection, transaction,
            "SELECT COUNT(*) FROM dbo.DEPOT_PRODUCT_LOCATION WHERE LTRIM(RTRIM(DEPOT_ID))=@Depot;", depot, token);
        if (allocated > 0)
        {
            return new Refusal(DepotInUseCode,
                $"该仓库仍分配了物料主货位（{allocated} 条），不能删除；请先解除货位分配。");
        }

        await DeleteDepotLocationsAsync(connection, transaction, depot, token);
        return null;
    }

    /// <summary>
    /// 删除该库别的全部库位行。库位是自引用树（`PARENT_NO` 指向同级库位，`FK_DEPOT_LOCATION_PARENT`
    /// 是 SAME TABLE REFERENCE：行级即时校验，同一语句里先删父再删子会当场冲突）。
    /// 因此按"没有下级引用的行优先"反复删——注意判据是"没有下级"，**不是"没有上级"**：
    /// 根节点同样可能有子树，把根当叶子会连带删掉仍被引用的行。
    /// 存在环时首轮就删不动，循环随即退出——留下的行由外键守卫拦下并给出可读错误，不会死循环。
    /// </summary>
    private static async Task DeleteDepotLocationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string depot,
        CancellationToken token)
    {
        while (true)
        {
            await using var command = new SqlCommand(
                "DELETE FROM dbo.DEPOT_LOCATION WHERE LTRIM(RTRIM(DEPOT_ID))=@Depot "
                + "AND NOT EXISTS ("
                + "    SELECT 1 FROM dbo.DEPOT_LOCATION child"
                + "    WHERE LTRIM(RTRIM(child.DEPOT_ID))=@Depot"
                + "      AND LTRIM(RTRIM(child.PARENT_NO))=LTRIM(RTRIM(DEPOT_LOCATION.LOCATION_NO)));",
                connection, transaction);
            command.Parameters.Add("@Depot", SqlDbType.NVarChar, 10).Value = depot;
            if (await command.ExecuteNonQueryAsync(token) == 0)
            {
                return;
            }
        }
    }

    private static async Task<int> CountAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        string depot,
        CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Depot", SqlDbType.NVarChar, 10).Value = depot;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token) ?? 0);
    }
}
