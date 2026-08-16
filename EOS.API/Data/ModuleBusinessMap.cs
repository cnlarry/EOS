namespace EOS.API.Data;

/// <summary>
/// 业务单据模块的领域规则注册表。
/// 对应旧系统 MODULES.UPDATE_SP / AFTERSAVE_SP / BILLKIND 单号配置的受控等价物：
/// 只有登记在册的模块才允许在统一表单保存管线中调用存储过程，
/// 且存储过程名必须是本表白名单内的固定值（禁止运行时从外部传入）。
/// 未登记的模块（包括所有无 SP 的纯 CRUD 模块）走统一表单默认管线，不执行任何存储过程。
/// </summary>
public sealed record ModuleBusinessRule(
    int ModuleId,
    string? AfterSaveSproc,
    string? WorkflowSproc,
    bool AutoBillNo,
    string? BillNoField,
    string? BillTypeField,
    string? PrepayOffsetTable = null,
    string? DomainRule = null);

public static class ModuleBusinessMap
{
    /// <summary>
    /// 模块 → 领域规则。键为 MODULES.M_IDX。
    /// 存储过程名取自旧系统 MODULES.UPDATE_SP / AFTERSAVE_SP，属受控白名单。
    /// </summary>
    private static readonly IReadOnlyDictionary<int, ModuleBusinessRule> Rules =
        new Dictionary<int, ModuleBusinessRule>
        {
            // 1201 产品/料件基本资料（1206/1210/1211 已合并至此）：仅批核工作流（P_WF_PRODUCT），无保存后副作用；PRO_NO 手工编号。
            [1201] = new(1201, null, "P_WF_PRODUCT", false, null, null),
            // 1416 客户报价单 → 批核联动客户计价表（1402）
            [1416] = new(1416, null, "P_WF_COP_QUOTE", true, "QUOTE_NO", "QUOTE_TYPE",
                DomainRule: "cop-quote"),
            // 1405 客户订单
            [1405] = new(1405, null, "P_WF_COP_ORDER", true, "ORDER_NO", "ORDER_TYPE",
                DomainRule: "cop-order"),
            // 1604 厂商报价单 → 批核联动厂商计价表（1602）
            [1604] = new(1604, null, "P_WF_PUR_QUOTE", true, "QUOTE_NO", "QUOTE_TYPE",
                DomainRule: "pur-quote"),
            // 1615 成品请购单
            [1615] = new(1615, null, "P_WF_PUR_APPLY", true, "APPLY_NO", "APPLY_TYPE",
                DomainRule: "pur-apply"),
            // 1606 采购单
            [1606] = new(1606, null, "P_WF_PUR_PURCHASE", true, "PURCHASE_NO", "PURCHASE_TYPE",
                DomainRule: "pur-purchase"),
            // 1607 收料单
            [1607] = new(1607, null, "P_WF_PUR_RECEIVE", true, "RECEIVE_NO", "RECEIVE_TYPE",
                DomainRule: "pur-receive"),
            // 1406 送货单
            [1406] = new(1406, null, "P_WF_COP_SEND", true, "SEND_NO", "SEND_TYPE",
                DomainRule: "cop-send"),
            // 1408 出货通知单：无 SP，仅自动单号（默认单别 CHPC，历史配置待业务确认）
            [1408] = new(1408, null, null, true, "SHIPMENT_NO", "SHIPMENT_TYPE"),
            // 财务：170101 应收货款单（对帐单）
            [170101] = new(170101, null, "P_WF_COP_ACCOUNT", true, "ACCOUNT_NO", "ACCOUNT_TYPE",
                DomainRule: "cop-account"),
            // 170102 收款单（预收冲抵入口）
            [170102] = new(170102, null, "P_WF_COP_RECEIPT", true, "RECEIPT_NO", "RECEIPT_TYPE",
                PrepayOffsetTable: "COP_RECEIPT_PREPAY", DomainRule: "cop-receipt"),
            // 170103 预收帐款单（AfterSave 已移植：客户校验 + 金额汇总）
            [170103] = new(170103, null, "P_WF_COP_PREPAY", true, "PREPAY_NO", "PREPAY_TYPE",
                DomainRule: "cop-prepay"),
            // 170201 应付货款单：AfterSave 已移植为确定性领域规则（purchase-due），
            // 不再调用 P_PUR_DUE_After_Save（金额汇总 + 数量校验由 C# 等价实现）
            [170201] = new(170201, null, "P_WF_PUR_DUE", true, "DUE_NO", "DUE_TYPE",
                DomainRule: "purchase-due"),
            // 170202 付款单
            [170202] = new(170202, null, "P_WF_PUR_PAY", true, "PAY_NO", "PAY_TYPE",
                PrepayOffsetTable: "PUR_PAY_PREPAY", DomainRule: "pur-pay"),
            // 170203 预付帐款单（AfterSave 已移植：厂商校验 + 预付不超采购金额 + 金额汇总）
            [170203] = new(170203, null, "P_WF_PUR_PREPAY", true, "PREPAY_NO", "PREPAY_TYPE",
                DomainRule: "pur-prepay"),
        };

    public static bool TryGet(int moduleId, out ModuleBusinessRule? rule) =>
        Rules.TryGetValue(moduleId, out rule);

    public static ModuleBusinessRule? Get(int moduleId) =>
        Rules.TryGetValue(moduleId, out var rule) ? rule : null;

    /// <summary>
    /// 受控存储过程名白名单（与 Rules 表一致），运行时只允许执行登记过的过程。
    /// </summary>
    public static bool IsKnownSproc(string sprocName) =>
        Rules.Values.Any(rule =>
            string.Equals(rule.AfterSaveSproc, sprocName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rule.WorkflowSproc, sprocName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 领域规则覆盖注册表（阶段 5）：对"由 MODULES 元数据自动注册"的模块，
/// 把 AfterSave 从受控 SP 替换为 C# 领域规则（WorkflowSproc/自动单号仍按元数据自动构建）。
/// </summary>
public static class DomainRuleMap
{
    private static readonly IReadOnlyDictionary<int, string> Rules = new Dictionary<int, string>
    {
        [110103] = "curr",
        [1204] = "bom-stru",
        [130102] = "inv-init",
        [130103] = "inv-in",
        [130104] = "inv-out",
        [130105] = "inv-transfer",
        [130106] = "inv-scrap",
        [130107] = "inv-adjust",
        [130110] = "inv-out",
        [180208] = "employee-card",
        [2205] = "sysqr-default",
        [2305] = "sysdg",
        [2817] = "inv-in",
        [2818] = "inv-out",
        [3901] = "inv-out",
        [2705] = "moc-work",
        [2706] = "moc-work-in",
        [1610] = "pur-callback",
        [1509] = "moc-produce-change",
        [1609] = "pur-purchase-change",
        [2404] = "sam-out",
        [1418] = "cop-order-change",
        [1413] = "cop-callback",
        [1522] = "moc-produce",
        [180207] = "hr-worktime",
        [1515] = "moc-product-out",
        [130101] = "inv-check-stock",
        [2708] = "sfc-plan",
    };

    public static bool TryGet(int moduleId, out string? rule) => Rules.TryGetValue(moduleId, out rule);
}
