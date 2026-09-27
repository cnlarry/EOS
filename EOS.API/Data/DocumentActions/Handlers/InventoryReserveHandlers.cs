using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions.Handlers;

/// <summary>
/// 按来源单据占料（D7-⑥ / WS-22）。
/// </summary>
/// <remarks>
/// 与冻结（WS-21）共用同一套格子身份与同步原语，差别只有一处、也是本质的一处：
/// **预留必须有来源单据**——它的生命周期归来源所有，来源取消/结案时要能按来源一把释放。
/// 于是参数里带上 `sourceType`（**字符串形式的模块号**，与 WS-20 定义的写入约定一致）与 `sourceNo`。
///
/// **不校验来源单据此刻是否存在**：可用量服务的惰性判定已经处理了"来源查不到/已结案"的情形
/// （判不出来就保守计入），在写入侧再加一道只会在"先占料、后建单"的流程上误伤。
/// </remarks>
internal sealed class InventoryReserveHandler : IDocumentUserAction
{
    public const string ActionKey = "inventory-reserve";

    internal const string SourceTypeParameter = "sourceType";
    internal const string SourceNoParameter = "sourceNo";

    private readonly WorkbenchAuditWriter _audit;

    public InventoryReserveHandler(WorkbenchAuditWriter audit) => _audit = audit;

    public string Key => ActionKey;

    public string Label => "库存预留";

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var quantity = InventoryFreezeAction.ReadQuantity(context, "预留");
        var reason = InventoryFreezeAction.ReadReason(context);
        var sourceType = InventoryFreezeAction.ReadRequired(context, SourceTypeParameter, "来源类型（模块号）");
        var sourceNo = InventoryFreezeAction.ReadRequired(context, SourceNoParameter, "来源单号");
        var slot = InventoryFreezeAction.ReadSlot(context);
        var before = await InventoryFreezeAction.ReadUseableAsync(context, slot, token);

        if (context.Preview)
        {
            var available = await InventoryFreezeAction.ReadAvailableAsync(context, slot, token);
            return new DocumentActionResult(DocumentActionOutcome.Message,
                $"将为 {sourceType}/{sourceNo} 在 {slot.ProductNo}/{slot.DepotId}/{slot.LocationNo}/{slot.BatchNo} 预留 {quantity}："
                + $"可用量 {InventoryFreezeAction.Show(available)} → {InventoryFreezeAction.Show(available - quantity)}。"
                + "本次未改动任何数据。");
        }

        await using (var command = new SqlCommand("""
            INSERT INTO dbo.INV_RESERVE
                (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SOURCE_TYPE, SOURCE_NO, RESERVE_QTY, REASON, STATUS,
                 CREATE_PERSON, CREATE_DATE)
                VALUES (@pro, @depot, @location, @batch, @sourceType, @sourceNo, @qty, @reason, N'A', @actor, GETDATE());
            """, context.Connection, context.Transaction))
        {
            command.Parameters.AddWithValue("@pro", slot.ProductNo);
            command.Parameters.AddWithValue("@depot", slot.DepotId);
            command.Parameters.AddWithValue("@location", slot.LocationNo);
            command.Parameters.AddWithValue("@batch", slot.BatchNo);
            command.Parameters.AddWithValue("@sourceType", sourceType);
            command.Parameters.AddWithValue("@sourceNo", sourceNo);
            command.Parameters.AddWithValue("@qty", quantity);
            command.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
            command.Parameters.AddWithValue("@actor", context.Executor);
            await command.ExecuteNonQueryAsync(token);
        }

        var after = await InventoryFreezeAction.SyncUseableAsync(context, slot, token);
        await InventoryFreezeAction.WriteAuditAsync(context, _audit, slot, "RESERVE",
            $"为 {sourceType}/{sourceNo} 预留 {quantity}（{reason ?? "未填原因"}）", before, after, token);

        return new DocumentActionResult(DocumentActionOutcome.Refreshed,
            $"已为 {sourceType}/{sourceNo} 预留 {quantity}：可用量 {InventoryFreezeAction.Show(before)} → "
            + $"{InventoryFreezeAction.Show(after)}（来源结案或取消时会自动释放）。");
    }
}

/// <summary>
/// 手工释放预留（D7-⑥ 的"兜底"那一半，WS-22）：来源单据还没来得及结案、或来源丢失时，
/// 由人把这一格的预留按先建先解放掉。
/// </summary>
/// <remarks>
/// 与解冻（WS-21）同一套释放原语，只是换了一张表：**"释放"只有一种语义**，
/// 冻结的解冻与预留的释放不该各写一份算术。
/// </remarks>
internal sealed class InventoryReleaseHandler : IDocumentUserAction
{
    public const string ActionKey = "inventory-release";

    private readonly WorkbenchAuditWriter _audit;

    public InventoryReleaseHandler(WorkbenchAuditWriter audit) => _audit = audit;

    public string Key => ActionKey;

    public string Label => "手工释放预留";

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var quantity = InventoryFreezeAction.ReadQuantity(context, "释放");
        var reason = InventoryFreezeAction.ReadReason(context);
        var slot = InventoryFreezeAction.ReadSlot(context);
        var before = await InventoryFreezeAction.ReadUseableAsync(context, slot, token);
        var active = await InventoryFreezeAction.ReadActiveRowsAsync(
            context, "INV_RESERVE", "RESERVE_QTY", slot, token);
        var activeTotal = active.Sum(row => row.Quantity);

        if (context.Preview)
        {
            return new DocumentActionResult(DocumentActionOutcome.Message,
                $"将对 {slot.ProductNo}/{slot.DepotId}/{slot.LocationNo}/{slot.BatchNo} 手工释放预留 {quantity}"
                + $"（当前预留合计 {InventoryFreezeAction.Show(activeTotal)}）。本次未改动任何数据。");
        }
        if (quantity > activeTotal)
        {
            throw new EffectValidationException(
                $"释放 {quantity} 超过该格当前预留合计 {InventoryFreezeAction.Show(activeTotal)}：没有那么多占用来释放。");
        }

        await InventoryFreezeAction.ReleaseSlotAsync(
            context, "INV_RESERVE", "RESERVE_QTY", slot, quantity, active, token);
        var after = await InventoryFreezeAction.SyncUseableAsync(context, slot, token);
        await InventoryFreezeAction.WriteAuditAsync(context, _audit, slot, "RELEASE",
            $"手工释放预留 {quantity}（{reason ?? "未填原因"}）", before, after, token);

        return new DocumentActionResult(DocumentActionOutcome.Refreshed,
            $"已手工释放 {quantity}：可用量 {InventoryFreezeAction.Show(before)} → {InventoryFreezeAction.Show(after)}"
            + "（在库数量不变）。");
    }
}
