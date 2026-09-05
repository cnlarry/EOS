namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// Verified cross-module document edges. Every entry must correspond to an actually
/// executed rule (approval stored procedure or AfterSave validation); unregistered
/// relations are reported as unknown, never guessed. Add entries only with code/DB
/// evidence and keep the note to the business fact (which document affects which).
/// </summary>
public sealed record FlowEdge(int FromModule, string Action, int ToModule, string Note);

public static class ModuleFlowEdges
{
    private static readonly IReadOnlyList<FlowEdge> Edges =
    [
        // 厂商报价单批核后回写厂商计价表（SUPPLIER_PRICE_M/D）与料件最近购价。
        new(1604, "批核", 1602, "批核后回写厂商计价与料件最近购价"),
        // 报价单批核后回写客户计价表（CLIENT_PRICE）。
        new(1404, "批核", 1402, "批核后回写客户计价"),
        // 送货单明细 ORDER_* 引用客户订单，客户须一致且订单须存在。
        new(1406, "引用", 1405, "明细引用客户订单（客户一致性校验）"),
        // 退货单明细 ORDER_* 引用客户订单。
        new(1407, "引用", 1405, "明细引用客户订单（客户一致性校验）"),
        new(1409, "引用", 1405, "明细引用客户订单（客户一致性校验）"),
        // 收料单明细 PURCHASE_* 引用采购单。
        new(1607, "引用", 1606, "明细引用采购单（订单存在性校验）"),
        // 采购退料单明细引用采购单与收料单。
        new(1608, "引用", 1606, "明细引用采购单（厂商一致性校验）"),
        new(1608, "引用", 1607, "明细引用收料单（序号存在性校验）"),
        new(1612, "引用", 1606, "明细引用采购单（厂商一致性校验）"),
        new(1612, "引用", 1607, "明细引用收料单（序号存在性校验）"),
    ];

    public static IReadOnlyList<FlowEdge> Outgoing(int moduleId) =>
        Edges.Where(edge => edge.FromModule == moduleId).ToArray();

    public static IReadOnlyList<FlowEdge> Incoming(int moduleId) =>
        Edges.Where(edge => edge.ToModule == moduleId).ToArray();
}
