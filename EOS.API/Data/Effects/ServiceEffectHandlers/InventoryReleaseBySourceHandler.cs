using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Data.Inventory;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// 来源单据**结案/取消即释放预留**（ADR-020 §9.7 D7-⑥ / §10 WS-22，挂 ADR-013 生命周期）。
/// </summary>
/// <remarks>
/// **为什么惰性判定（WS-20）之外还需要这个钩子**：可用量服务在读取时已经会按"来源是否结案"
/// 判掉该笔占用，所以**算出来的可用量**不会变成假性缺料；但余额行上的 `USEABLE_QTY` 是**存列**，
/// 没人重写它就还停在旧值——WS-25 的门禁要断言"列 = 数量 − 有效占用"，靠的正是这个钩子把列
/// 与占用行一起收干净。两者分工：**惰性判据防"算错"，钩子防"存脏"**。
///
/// **身份取自框架**：来源模块号 = 当前模块；来源单号 = 主键里"以 `_NO` 结尾且不以 `_TYPE` 结尾"
/// 的那一列的值（与 WS-20 的写入约定同一条规则，规则的实现在 <see cref="InventoryAvailabilityService.PickSourceNumberColumn"/>）。
/// 因此配置侧只需把这个效果挂在来源模块的 `ENDCASE` 事件上，不必给每个模块写参数。
///
/// **释放后逐个重算 `USEABLE_QTY`**：释放可能跨多格，每格都按可用量服务的口径落列——
/// 与冻结/预留的同步是同一个收口点。
/// </remarks>
public sealed class InventoryReleaseBySourceHandler : IEffectServiceHandler
{
    public const string EffectKeyName = "inventory-release-by-source";

    private readonly WorkbenchAuditWriter _audit;

    public InventoryReleaseBySourceHandler(WorkbenchAuditWriter audit) => _audit = audit;

    public string EffectKey => EffectKeyName;

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var numberColumn = InventoryAvailabilityService.PickSourceNumberColumn(context.Plan.MasterPkOrder);
        if (numberColumn is null)
        {
            // 认不出唯一单号列就不动：宁可不动（占用继续按保守方向计入），也不猜着释放。
            return 0;
        }

        var index = context.Plan.MasterPkOrder
            .Select((column, position) => (column, position))
            .First(item => item.column.Equals(numberColumn, StringComparison.OrdinalIgnoreCase)).position;
        var sourceNo = (context.MasterKeyValues.Count > index ? context.MasterKeyValues[index] : null)?.Trim() ?? string.Empty;
        if (sourceNo.Length == 0)
        {
            return 0;
        }

        var released = await InventoryFreezeAction.ReleaseBySourceAsync(
            context.Connection, context.Transaction, "INV_RESERVE", "RESERVE_QTY",
            context.Plan.ModuleId.ToString(System.Globalization.CultureInfo.InvariantCulture), sourceNo,
            context.Executor, token);
        if (released.Count == 0)
        {
            return 0;
        }

        await InventoryFreezeAction.SyncManyAsync(
            context.Connection, context.Transaction, context.Executor,
            $"来源结案：释放预留 {released.Count} 行（来源 {context.Plan.ModuleId}/{sourceNo}）",
            released, _audit, context.Plan.ModuleId, context.RecordKey ?? sourceNo, token);

        return released.Count;
    }
}
