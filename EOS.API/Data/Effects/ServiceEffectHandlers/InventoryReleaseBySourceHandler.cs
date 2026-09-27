using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Data.Inventory;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// 来源单据**结案即释放预留、取消结案即收回**（ADR-020 §9.7 D7-⑥ / §10 WS-22，挂 ADR-013 生命周期）。
/// </summary>
/// <remarks>
/// **为什么惰性判定（WS-20）之外还需要这个钩子**：可用量服务在读取时已经会按"来源是否结案"
/// 判掉该笔占用，所以**算出来的可用量**不会变成假性缺料；但余额行上的 `USEABLE_QTY` 是**存列**，
/// 没人重写它就还停在旧值——WS-25 的门禁要断言"列 = 数量 − 有效占用"，靠的正是这个钩子把列
/// 与占用行一起收干净。两者分工：**惰性判据防"算错"，钩子防"存脏"**。
///
/// **身份取自框架**：来源模块号 = 当前模块；来源单号 = 主键里"以 `_NO` 结尾且不以 `_TYPE` 结尾"
/// 的那一列的值（与 WS-20 的写入约定同一条规则，规则的实现在 <see cref="InventoryAvailabilityService.PickSourceNumberColumn"/>）。
/// 因此配置侧只需把这个效果挂在来源模块的 `ENDCASE` / `UNENDCASE` 两个事件上，不必给每个模块写参数。
///
/// **两个方向共用这一个效果键**（按执行事件分支，与批核 / 解批的分支方式同一种做法）：
/// 结案把该来源的有效预留整笔置 `C` 并写上归因 `ENDCASE`；取消结案**只收回带这个归因的行**
/// （人工释放的预留同样停在 `C`，但它没有归因，不该被取消结案算回占用）。
/// 两个方向都逐个重算 `USEABLE_QTY`：改动可能跨多格，每格都按可用量服务的口径落列——
/// 与冻结/预留的同步是同一个收口点。
/// </remarks>
public sealed class InventoryReleaseBySourceHandler : IEffectServiceHandler
{
    public const string EffectKeyName = "inventory-release-by-source";

    /// <summary>结案释放写进预留行的归因标记：只有带它的 `C` 行会被取消结案收回。</summary>
    internal const string EndcaseReleaseKind = "ENDCASE";

    private const string OccupancyTable = "INV_RESERVE";
    private const string OccupancyQuantityColumn = "RESERVE_QTY";

    private readonly WorkbenchAuditWriter _audit;

    public InventoryReleaseBySourceHandler(WorkbenchAuditWriter audit) => _audit = audit;

    public string EffectKey => EffectKeyName;

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var sourceNo = ReadSourceNumber(context);
        if (sourceNo.Length == 0)
        {
            // 认不出唯一单号列就不动：宁可不动（占用继续按保守方向计入），也不猜着释放/收回。
            return 0;
        }

        return context.ExecutionEvent switch
        {
            EffectEvent.Endcase => await ReleaseAsync(context, sourceNo, token),
            EffectEvent.Unendcase => await ReviveAsync(context, sourceNo, token),
            // 认不出的方向一律不动并报出来：猜着动（无论是释放还是收回）都比不动危险。
            _ => Skip(context, $"来源预留钩子在 {context.ExecutionEvent} 事件下没有对应动作，已跳过。"),
        };
    }

    /// <summary>结案：把该来源名下的有效预留整笔释放，并给它们打上"由结案释放"的归因。</summary>
    private async Task<int> ReleaseAsync(ServiceEffectContext context, string sourceNo, CancellationToken token)
    {
        var released = await InventoryFreezeAction.ReleaseBySourceAsync(
            context.Connection, context.Transaction, OccupancyTable, OccupancyQuantityColumn,
            context.Plan.ModuleId.ToString(System.Globalization.CultureInfo.InvariantCulture), sourceNo,
            EndcaseReleaseKind, context.Executor, token);
        if (released.Count == 0)
        {
            return 0;
        }

        await InventoryFreezeAction.SyncManyAsync(
            context.Connection, context.Transaction, context.Executor, "UNFREEZE",
            $"来源结案：释放预留 {released.Count} 行（来源 {context.Plan.ModuleId}/{sourceNo}）",
            released, _audit, context.Plan.ModuleId, context.RecordKey ?? sourceNo, token);

        return released.Count;
    }

    /// <summary>取消结案：把当初由结案释放的那批预留收回（置回有效），并清空归因。</summary>
    private async Task<int> ReviveAsync(ServiceEffectContext context, string sourceNo, CancellationToken token)
    {
        var revived = await InventoryFreezeAction.ReviveBySourceAsync(
            context.Connection, context.Transaction, OccupancyTable, OccupancyQuantityColumn,
            context.Plan.ModuleId.ToString(System.Globalization.CultureInfo.InvariantCulture), sourceNo,
            EndcaseReleaseKind, context.Executor, token);
        if (revived.Count == 0)
        {
            // 该来源没有"由结案释放"的预留（本来就没占料，或那些行是人工释放的）：成功且零改动。
            return 0;
        }

        await InventoryFreezeAction.SyncManyAsync(
            context.Connection, context.Transaction, context.Executor, "RESERVE",
            $"取消结案：收回预留 {revived.Count} 行（来源 {context.Plan.ModuleId}/{sourceNo}）",
            revived, _audit, context.Plan.ModuleId, context.RecordKey ?? sourceNo, token);

        return revived.Count;
    }

    private static int Skip(ServiceEffectContext context, string message)
    {
        context.Warnings?.Add(message);
        return 0;
    }

    /// <summary>来源单号：主键里"以 `_NO` 结尾且不以 `_TYPE` 结尾"的那一列的值；认不出返回空串。</summary>
    private static string ReadSourceNumber(ServiceEffectContext context)
    {
        var numberColumn = InventoryAvailabilityService.PickSourceNumberColumn(context.Plan.MasterPkOrder);
        if (numberColumn is null)
        {
            return string.Empty;
        }

        var index = context.Plan.MasterPkOrder
            .Select((column, position) => (column, position))
            .First(item => item.column.Equals(numberColumn, StringComparison.OrdinalIgnoreCase)).position;
        return (context.MasterKeyValues.Count > index ? context.MasterKeyValues[index] : null)?.Trim()
            ?? string.Empty;
    }
}
