using System.Data;
using EOS.API.Data.Effects;
// 复用效果处理器那套标识符引用助手（先按白名单校验再方括号包裹）：一次实现，不各写一份。
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Data.Inventory;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions.Handlers;

/// <summary>
/// `recalc-account`（重算账面数量）：用户在盘点单上点一下，把**已有明细行**的账面数按当前库存重算一遍。
///
/// 与保存期的 `stocktake-scope-generate` 是一对紧挨着的口径，两者的分工必须一起读：
///   · `stocktake-scope-generate`（保存期）——单据**还没有**明细时按盘点范围把库存行展开成明细
///     （首次生成：明细行数、位置/批次都由它决定）；
///   · `recalc-account`（用户点击）——明细**已经有了**，只把每行的账面数刷新成当前库存量
///     （重算：不新增行、不删行、不动盘点数，只改账面数）。
/// 换句话说：明细行由"生成"决定，行上的账面数由"重算"决定；两者都不得碰对方的东西。
///
/// 回写方式是**逐行 UPDATE**（每行一条参数化语句，只写 ACCOUNT_QTY 一列），不是整表 UPDATE：
/// 整表回写会把用户已经录进去的盘点数一并推平，这正是该操作最容易被做坏的地方。
/// 与同一操作族的其它处理器一样，表名/列名来自本文件里的常量并先对物理列白名单校验（fail-closed），
/// 用户输入不进 SQL 结构，只作为参数值。
/// </summary>
internal sealed class RecalcAccountHandler : IDocumentUserAction, IDocumentActionPlacement
{
    public const string ActionKey = "recalc-account";

    // 盘点明细的库存定位口径（库别 + 料号 + 库位 + 批号）——与 stocktake-scope-generate 生成明细时
    // 落进明细行的四列一一对应，重算才能按同一把尺子找回来。
    // 库存侧的同一把尺子在 InventoryQueryService 里（那里才是四键口径的唯一出口）。
    private const string DepotField = "DEPOT_ID";
    private const string ProductField = "PRO_NO";
    private const string LocationField = "LOCATION_NO";
    private const string BatchField = "BATCH_NO";
    private const string AccountQtyField = "ACCOUNT_QTY";

    public string Key => ActionKey;

    public string Label => "重算账面数量";

    /// <summary>账面数在明细行上，按钮落在子表标题栏。</summary>
    public string Placement => DocumentActionPlacements.Detail;

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var definition = context.Definition;
        if (definition.DetailTable is null)
        {
            throw new InvalidOperationException("该模块没有明细表，无法重算账面数量。");
        }

        var q = ServiceEffectSql.Q;
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var detailTable = definition.DetailTable;
        foreach (var (table, column) in new[]
                 {
                     (definition.MasterTable, DepotField),
                     (detailTable, DepotField), (detailTable, ProductField),
                     (detailTable, LocationField), (detailTable, BatchField), (detailTable, AccountQtyField),
                 })
        {
            if (!columns.Contains(table + "." + column))
            {
                throw new InvalidOperationException($"重算账面数量需要 {table}.{column}，该模块没有这一列，请联系管理员调整。");
            }
        }

        var detailKeys = await WorkbenchSql.GetPrimaryKeyColumnsAsync(context.Connection, context.Transaction, detailTable, token);
        if (detailKeys.Count == 0 || context.MasterPkOrder.Any(key => !detailKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("盘点明细缺少与主表关联的主键列，禁止无条件重算。");
        }

        var masterDepot = await ReadMasterDepotAsync(context, token);
        var rows = await ReadDetailRowsAsync(context, detailKeys, masterDepot, token);
        if (rows.Count == 0)
        {
            return new DocumentActionResult(DocumentActionOutcome.Message, "该盘点单还没有明细，请先按盘点范围生成明细或手工录入。");
        }

        var changed = 0;
        var missingStock = 0;
        foreach (var row in rows)
        {
            if (row.StockQty is null)
            {
                missingStock++;
            }
            if (row.AccountQty is not null && row.StockQty is not null && Math.Abs(row.AccountQty.Value - row.StockQty.Value) < 0.000001)
            {
                continue; // 账面数已经是最新：不动它，减少无谓写入
            }
            await UpdateAccountQtyAsync(context, detailKeys, row, token);
            changed++;
        }

        var summary = $"账面数量已重算：明细 {rows.Count} 行，更新 {changed} 行"
            + (missingStock > 0 ? $"，其中 {missingStock} 行已无库存记录，账面数按 0 计" : string.Empty)
            + "。盘点数与明细行未改动。";
        return new DocumentActionResult(DocumentActionOutcome.Refreshed, summary);
    }

    /// <summary>主表库别：明细行没填库别时按单据的库别定位库存（老单据的手工明细常留空）。</summary>
    private static async Task<string> ReadMasterDepotAsync(DocumentActionContext context, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var where = string.Join(" AND ", context.MasterPkOrder.Select((column, index) => $"{q(column)}=@k{index}"));
        await using var command = new SqlCommand(
            $"SELECT ISNULL({q(DepotField)}, N'') FROM dbo.{q(context.Definition.MasterTable)} WHERE {where};",
            context.Connection, context.Transaction);
        AddKeyParameters(command, context.MasterPkOrder, context.KeyValues);
        return (await command.ExecuteScalarAsync(token) as string)?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// 逐行取出明细行的键值与当前库存量（没有库存记录的行也要参与重算——账面数归零）。
    /// 一次读取不做任何整表写回。
    ///
    /// 定位仍按（库别, 料号, 库位, 批号）四列，但**怎么读库存交给 <see cref="InventoryQueryService"/>**：
    /// 哨兵位置的归一、四键的唯一性假设都在那里，这里只把两边按同一把尺子对齐后做内存 LEFT JOIN。
    /// </summary>
    private static async Task<IReadOnlyList<DetailRow>> ReadDetailRowsAsync(
        DocumentActionContext context,
        IReadOnlyList<string> detailKeys,
        string masterDepot,
        CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var detailTable = context.Definition.DetailTable!;
        var keyFilter = string.Join(" AND ", context.MasterPkOrder.Select((column, index) => $"d.{q(column)}=@k{index}"));
        var detailColumns = string.Join(", ", detailKeys.Select(column => $"d.{q(column)}"));
        await using var command = new SqlCommand(
            $"""
            SELECT {detailColumns}, d.{q(AccountQtyField)}, d.{q(DepotField)}, d.{q(ProductField)},
                   d.{q(LocationField)}, d.{q(BatchField)}
            FROM dbo.{q(detailTable)} d WITH (UPDLOCK, HOLDLOCK)
            WHERE {keyFilter};
            """, context.Connection, context.Transaction);
        AddKeyParameters(command, context.MasterPkOrder, context.KeyValues);

        var pending = new List<PendingRow>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var keyValues = new string[detailKeys.Count];
                for (var index = 0; index < detailKeys.Count; index++)
                {
                    keyValues[index] = reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString()!.Trim();
                }
                var offset = detailKeys.Count;
                pending.Add(new PendingRow(
                    keyValues,
                    reader.IsDBNull(offset) ? null : Convert.ToDouble(reader.GetValue(offset)),
                    reader.IsDBNull(offset + 1) ? string.Empty : reader.GetString(offset + 1).Trim(),
                    reader.IsDBNull(offset + 2) ? string.Empty : reader.GetString(offset + 2).Trim(),
                    reader.IsDBNull(offset + 3) ? string.Empty : reader.GetString(offset + 3).Trim(),
                    reader.IsDBNull(offset + 4) ? string.Empty : reader.GetString(offset + 4).Trim()));
            }
        }
        if (pending.Count == 0) return [];

        // 明细没填库别的行按单据库别定位库存（老单据的手工明细常留空），两者都要计入候选。
        var depots = pending.Select(row => row.Depot)
            .Concat([masterDepot])
            .Where(depot => depot.Length > 0 || masterDepot.Length == 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var stock = await InventoryQueryService.GetRowsAsync(
            context.Connection, context.Transaction,
            new InventoryQueryService.RowScope { DepotIds = depots },
            InventoryQueryService.ReadLock.None, token);
        var quantities = stock.ToDictionary(
            row => StockKey(row.DepotId, row.ProductNo, row.LocationNo, row.BatchNo),
            row => row.Quantity);

        return pending.Select(row => new DetailRow(
                row.KeyValues,
                row.AccountQty,
                quantities.GetValueOrDefault(StockKey(
                    row.Depot.Length > 0 ? row.Depot : masterDepot,
                    row.Product,
                    row.Location,
                    row.Batch))))
            .ToList();
    }

    /// <summary>四键的对齐键：两侧都走同一套归一化（空值/哨兵不产生差异）。</summary>
    private static string StockKey(string depot, string product, string location, string batch) =>
        string.Join('|',
            depot.Trim(),
            product.Trim(),
            location.Trim().Length == 0 ? InventoryQueryService.LocationSentinel : location.Trim(),
            batch.Trim());

    private sealed record PendingRow(
        IReadOnlyList<string> KeyValues,
        double? AccountQty,
        string Depot,
        string Product,
        string Location,
        string Batch);

    /// <summary>逐行写回账面数：每行一条语句，只碰 ACCOUNT_QTY 一列。</summary>
    private static async Task UpdateAccountQtyAsync(
        DocumentActionContext context, IReadOnlyList<string> detailKeys, DetailRow row, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var where = string.Join(" AND ", detailKeys.Select((column, index) => $"{q(column)}=@r{index}"));
        await using var command = new SqlCommand(
            $"UPDATE dbo.{q(context.Definition.DetailTable!)} SET {q(AccountQtyField)}=@qty WHERE {where};",
            context.Connection, context.Transaction);
        // 库存里没有这一行 ⇒ 账面数归零（该位置已经没有货了），而不是保留一个过期的数。
        command.Parameters.Add("@qty", SqlDbType.Float).Value = (object?)row.StockQty ?? 0d;
        for (var index = 0; index < detailKeys.Count; index++)
        {
            command.Parameters.AddWithValue($"@r{index}", row.KeyValues[index]);
        }
        await command.ExecuteNonQueryAsync(token);
    }

    private static void AddKeyParameters(SqlCommand command, IReadOnlyList<string> keyColumns, IReadOnlyList<string> keyValues)
    {
        for (var index = 0; index < keyColumns.Count; index++)
        {
            command.Parameters.AddWithValue($"@k{index}", index < keyValues.Count ? keyValues[index] : string.Empty);
        }
    }

    private sealed record DetailRow(IReadOnlyList<string> KeyValues, double? AccountQty, double? StockQty);
}
