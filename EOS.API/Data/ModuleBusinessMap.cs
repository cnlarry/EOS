using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// Registry of business-module domain rules: only registered modules may execute stored
/// procedures in the unified save pipeline, and procedure names must come from this table.
/// Unregistered modules (plain CRUD) use the default pipeline without procedures.
/// </summary>

public static class ModuleBusinessMap
{
    /// <summary>
    /// 模块 → 领域规则。键为 MODULES.M_IDX。
    /// 存储过程名取自 MODULES.UPDATE_SP / AFTERSAVE_SP，属受控白名单。
    /// </summary>
    private static readonly IReadOnlyDictionary<int, ModuleBusinessRule> Rules =
        new Dictionary<int, ModuleBusinessRule>
        {
            // 1201 产品/料件基本资料（1206/1210/1211 已合并至此）：仅批核工作流（P_WF_PRODUCT），无保存后副作用；PRO_NO 手工编号。
            [1201] = new(1201, null, "P_WF_PRODUCT", false, null, null),
            // 1404 报价单
            [1404] = new(1404, null, "P_WF_COP_QUOTE", true, "QUOTE_NO", "QUOTE_TYPE",
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
            // 170103 预收帐款单（AfterSave 已实现：客户校验 + 金额汇总）
            [170103] = new(170103, null, "P_WF_COP_PREPAY", true, "PREPAY_NO", "PREPAY_TYPE",
                DomainRule: "cop-prepay"),
            // 170201 应付货款单：AfterSave 已实现为确定性领域规则（purchase-due），
            // 不再调用 P_PUR_DUE_After_Save（金额汇总 + 数量校验由 C# 实现）
            [170201] = new(170201, null, "P_WF_PUR_DUE", true, "DUE_NO", "DUE_TYPE",
                DomainRule: "purchase-due"),
            // 170202 付款单
            [170202] = new(170202, null, "P_WF_PUR_PAY", true, "PAY_NO", "PAY_TYPE",
                PrepayOffsetTable: "PUR_PAY_PREPAY", DomainRule: "pur-pay"),
            // 170203 预付帐款单（AfterSave 已实现：厂商校验 + 预付不超采购金额 + 金额汇总）
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

    /// <summary>
    /// 需批核单据模块 → 主表映射（待办工作台「我的任务」扫描清单，单源， ）：
    /// 业务闭环 17 单据 + 生产/库存核心单据，主表名来自服务端常量。
    /// 新增需批核模块在此登记，漏登记会导致「我的任务」计数缺失。
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> MasterTables =
        new Dictionary<int, string>
        {
            [1404] = "COP_QUOTE_M",
            [1405] = "COP_ORDER_M",
            [1406] = "COP_SEND_M",
            [1408] = "COP_SHIPMENT_M",
            [170101] = "COP_ACCOUNT_M",
            [170102] = "COP_RECEIPT_M",
            [170103] = "COP_PREPAY_M",
            [1604] = "PUR_QUOTE_M",
            [1615] = "PUR_APPLY_M",
            [1606] = "PUR_PURCHASE_M",
            [1607] = "PUR_RECEIVE_M",
            [170201] = "PUR_DUE_M",
            [170202] = "PUR_PAY_M",
            [170203] = "PUR_PREPAY_M",
            [1502] = "MOC_PRODUCE_M",
            [1505] = "MOC_PRODUCT_IN_M",
            [130103] = "INV_OCCUR_IN_M",
            [130104] = "INV_OCCUR_OUT_M",
        };
}

/// <summary>
/// 领域规则覆盖注册表：对"由 MODULES 元数据自动注册"的模块，
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
        [2305] = "sysdg",
        [2817] = "inv-in",
        [2818] = "inv-out",
        [3901] = "inv-out",
        [2705] = "moc-work",
        [2706] = "moc-work-in",
        [1502] = "moc-produce",
        [1503] = "moc-get",
        [1505] = "moc-product-in",
        [1512] = "moc-produce",
        [1514] = "moc-get",
        [1517] = "moc-get",
        [1519] = "moc-product-in",
        [2703] = "sfc-process",
        [2803] = "moc-produce",
        [2804] = "moc-produce",
        [2805] = "moc-get",
        [2806] = "moc-get",
        [2815] = "moc-product-out",
        [2816] = "moc-product-in",
        [180401] = "sfc-daily",
        [1407] = "cop-return",
        [1409] = "cop-return",
        [1411] = "cop-fitout",
        [1412] = "cop-fitin",
        [1423] = "cop-back",
        [1608] = "pur-cancel",
        [1612] = "pur-cancel",
        [1616] = "pur-apply",
        [180102] = "hr-employee",
        [180105] = "hr-employee",
        [180106] = "hr-contract",
        [180107] = "hr-safe",
        [180108] = "hr-certify",
        [180110] = "hr-employee",
        [180111] = "hr-employee",
        [180205] = "hr-enactment",
        [180211] = "hr-plan",
        [180206] = "hr-apply",
        [180301] = "hr-wage-item",
        [180309] = "hr-wage",
        [1803091] = "hr-wage",
        [180310] = "hr-wage-lz",
        [1803101] = "hr-wage-lz",
        [180502] = "hrm-wage-item",
        [180504] = "hrm-wage",
        [180651] = "hrm-plan",
        [130108] = "inv-loan",
        [130109] = "inv-return",
        [1506] = "moc-bom-stru",
        [1507] = "moc-plan",
        [2704] = "moc-produce-process",
        [2707] = "moc-work-out",
        [2903] = "mou-apply",
        [2904] = "mou-accept",
        [2906] = "mou-batch",
        [2907] = "mou-get",
        [2908] = "mou-out",
        [2909] = "mou-in",
        [2910] = "mou-scrap",
        [2911] = "mou-pro",
        [2912] = "mou-batchin",
        [2913] = "mou-get2",
        [2914] = "mou-assess",
        [300301] = "cus-export",
        [300302] = "cus-import",
        [300304] = "cus-export",
        [300305] = "cus-import",
        [3006] = "cus-manual",
        [3014] = "cus-account",
        [3303] = "qc-analysis",
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
