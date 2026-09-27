using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Inventory;

/// <summary>
/// 「放哪」的**取值侧**（/ D1c）：把解析器需要的候选从库里查出来，
/// 交给 <see cref="DepotLocationResolver"/> 这个纯函数做决策。
/// </summary>
/// <remarks>
/// 分工是刻意的：决策表（FIXED 认主货位 / RANDOM 三级退化 / 未登记档不猜）能整张穷举单测，
/// 取值只做"按既定口径去查"。库存侧的读一律经 <see cref="InventoryQueryService"/>（收口），
/// 主货位表 `DEPOT_PRODUCT_LOCATION` 不是库存账、由本类自己读。
/// 调用方还留着两件事（本类与解析器都不代劳）：把解析结果过一遍**位置存在性**前置校验，
/// 以及决定"什么时候才解析"。
/// </remarks>
public static class DepotLocationService
{
    /// <summary>第 3 级只取"按 SEQ_NO 的第一个"，多取几个是为了将来按区 / 容量排序时不必改接口。</summary>
    private const int EmptyCandidateLimit = 5;

    /// <summary>
    /// 入库时该落在哪个位置（位置为空 / 哨兵时的系统建议）。
    /// </summary>
    /// <remarks>
    /// **`LOCATION_MODE` 0/1 一律不解析**（返回哨兵）：档 0 是"不管位置"、档 1 是"可填"，
    /// 两者都是"位置由人定"；系统替人挑会凭空改变库存键，破坏 的 R1 等价性。
    /// 档 2/3 才按 <paramref name="storageMode"/> 走：`FIXED` 认主货位（**查不到回落哨兵、不抛异常**——
    /// 缺配置是配置缺口，不是调用方单据的失败）；`RANDOM` / `MIXED` 走三级退化。
    /// </remarks>
    public static async Task<LocationResolution> ResolveInboundAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int locationMode,
        string? storageMode,
        string depotId,
        string proNo,
        string? sentinel,
        CancellationToken token)
    {
        var effectiveSentinel = string.IsNullOrWhiteSpace(sentinel)
            ? DepotLocationResolver.DefaultSentinel
            : sentinel.Trim();

        if (locationMode <= 1)
            return new LocationResolution(effectiveSentinel, LocationResolutionSource.Sentinel);

        var mode = (storageMode ?? string.Empty).Trim().ToUpperInvariant();
        IReadOnlyList<string>? primary = null;
        string? occupied = null;
        string? lastPlaced = null;
        IReadOnlyList<string>? empty = null;

        if (mode == DepotLocationResolver.FixedMode)
        {
            primary = await GetPrimaryLocationsAsync(connection, transaction, depotId, proNo, token);
        }
        else if (mode is DepotLocationResolver.RandomMode or DepotLocationResolver.MixedMode)
        {
            // 逐级去查：前一级查到了就不查后一级——查了也不用，白读一次库
            occupied = await InventoryQueryService.GetOccupiedLocationAsync(
                connection, transaction, proNo, depotId, token);
            if (occupied is null)
            {
                lastPlaced = await InventoryQueryService.GetLatestLedgerLocationAsync(
                    connection, transaction, proNo, depotId, token);
            }
            if (occupied is null && lastPlaced is null)
            {
                empty = await InventoryQueryService.GetEmptyLocationsAsync(
                    connection, transaction, depotId, EmptyCandidateLimit, token);
            }
        }

        return new LocationResolutionInput(mode, effectiveSentinel, primary, occupied, lastPlaced, empty).Resolve();
    }

    /// <summary>
    /// 该 (料号, 库别) 的主货位（`IS_PRIMARY = 1`），按 `SEQ_NO` 升序。
    /// 只认**存在且启用**的位置：停用的货位不该再往上放货（解析器的候选里不留墓碑）。
    /// 表里没有这个物料的主货位时返回空表——调用方据此回落哨兵。
    /// </summary>
    private static async Task<IReadOnlyList<string>> GetPrimaryLocationsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string depotId,
        string proNo,
        CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT l.LOCATION_NO
              FROM dbo.DEPOT_PRODUCT_LOCATION l
              JOIN dbo.DEPOT_LOCATION d
                ON d.DEPOT_ID = l.DEPOT_ID AND d.LOCATION_NO = l.LOCATION_NO
             WHERE LTRIM(RTRIM(l.DEPOT_ID)) = @d
               AND LTRIM(RTRIM(l.PRO_NO)) = @p
               AND l.IS_PRIMARY = 1
               AND ISNULL(d.STATUS, N'A') = N'A'
             ORDER BY ISNULL(l.SEQ_NO, 2147483647), l.LOCATION_NO;
            """, connection, transaction);
        command.Parameters.Add("@d", SqlDbType.NVarChar, 10).Value = depotId.Trim();
        command.Parameters.Add("@p", SqlDbType.NVarChar, 30).Value = proNo.Trim();

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (!reader.IsDBNull(0) && reader.GetString(0).Trim() is { Length: > 0 } value)
                result.Add(value);
        }
        return result;
    }
}
