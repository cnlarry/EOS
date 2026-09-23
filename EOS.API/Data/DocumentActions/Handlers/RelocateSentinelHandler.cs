using EOS.API.Models;

namespace EOS.API.Data.DocumentActions.Handlers;

/// <summary>
/// `relocate-sentinel`（哨兵存量归位）：把某库别记在『未指定位置』（`'-'`）上的存量，
/// 按用户指定的目标库位整批改记一次——**库别总量不变**，只是这批货从"不知道在哪"变成"认定在这个位"。
///
/// 为什么是 Dalton 意义上的"操作"而不是"保存的一部分"：
/// 归位是一次**账面认定**（它把货当作就在目标库位），不是策略配置的副作用；
/// 想换个目标库位再归一次时，不该要求用户先去改一遍策略配置。
/// 这里的写都在调用方事务里（<see cref="DepotStockPolicyService.RelocateSentinelStockAsync"/> 不自开事务），
/// 所以"探路＝执行后回滚"由框架兜住，处理器不必自行分支。
///
/// 目标库位由用户在弹出 little form 里填（`PARAM_STRUCT` 声明 `relocateTo`，服务端按同一声明复核），
/// 不接受任何表/列/SQL 片段。
/// </summary>
internal sealed class RelocateSentinelHandler(DepotStockPolicyService policies) : IDocumentUserAction, IDocumentActionPlacement
{
    public const string ActionKey = "relocate-sentinel";

    /// <summary>目标库位参数的键：与 110310 上该按钮的 PARAM_STRUCT 声明同名。</summary>
    public const string TargetParameter = "relocateTo";

    public string Key => ActionKey;

    public string Label => "哨兵存量归位";

    /// <summary>策略行没有明细表，按钮只能落在单据级工具条。</summary>
    public string Placement => DocumentActionPlacements.Master;

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var depotId = context.KeyValues is { Count: > 0 } ? context.KeyValues[0].Trim() : string.Empty;
        if (depotId.Length == 0
            || string.Equals(depotId, DepotStockPolicyService.DeploymentScope, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("归位按库别执行：请选中具体库别的策略行（部署级默认不是库别）。");
        }

        var target = context.Parameters.TryGetValue(TargetParameter, out var value)
            ? value?.Trim() ?? string.Empty
            : string.Empty;
        if (target.Length == 0)
        {
            throw new InvalidOperationException("未指定目标库位：请填写要把这批货记到哪个库位。");
        }

        var result = await policies.RelocateSentinelStockAsync(
            depotId, target, context.Executor, context.Connection, context.Transaction, token);
        if (result.Errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join('；', result.Errors));
        }

        // 归位改的是库存余额而不是策略行本身：没有需要重刷的字段时也没必要让界面转圈，
        // 一律返回 Refreshed 让列表回到最新状态，并把"改了多少/为什么没改"说清楚。
        return new DocumentActionResult(DocumentActionOutcome.Refreshed, result.Message);
    }
}
