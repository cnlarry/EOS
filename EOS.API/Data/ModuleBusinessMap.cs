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
            // 1201 产品/料件基本资料（1206/1210/1211 已合并至此）：批核为纯状态翻转（旧 P_WF_PRODUCT 已退役，
            // 其版次递增逻辑受 SYSSS.PRO_EDITION_TAG 门控，该开关打开时需另配效果）；PRO_NO 手工编号。
            [1201] = new(1201, null, null, false, null, null),
            // 1404 报价单：保存后校验已由校验目录承接，批核已由效果链接管（旧 P_WF_COP_QUOTE 已退役），保留自动单号。
            [1404] = new(1404, null, null, true, "QUOTE_NO", "QUOTE_TYPE"),
            // 1405 客户订单（批核/解批已由效果链接管，旧 P_WF_COP_ORDER 已退役）
            [1405] = new(1405, null, null, true, "ORDER_NO", "ORDER_TYPE",
                DomainRule: "cop-order"),
            // 1604 厂商报价单 → 批核联动厂商计价表（1602）：保存后校验已由校验目录承接，批核已由效果链接管（旧 P_WF_PUR_QUOTE 已退役）。
            [1604] = new(1604, null, null, true, "QUOTE_NO", "QUOTE_TYPE"),
            // 1615 成品请购单（批核已由效果链接管，旧 P_WF_PUR_APPLY 已退役）
            [1615] = new(1615, null, null, true, "APPLY_NO", "APPLY_TYPE",
                DomainRule: "pur-apply"),
            // 1606 采购单（批核/解批已由效果链接管，旧 P_WF_PUR_PURCHASE 已退役）
            [1606] = new(1606, null, null, true, "PURCHASE_NO", "PURCHASE_TYPE",
                DomainRule: "pur-purchase"),
            // 1607 收料单（批核已由效果链接管，旧 P_WF_PUR_RECEIVE 已退役；保存期判据已迁校验目录）
            [1607] = new(1607, null, null, true, "RECEIVE_NO", "RECEIVE_TYPE"),
            // 1406 送货单（批核已由效果链接管，旧 P_WF_COP_SEND 已退役）
            [1406] = new(1406, null, null, true, "SEND_NO", "SEND_TYPE",
                DomainRule: "cop-send"),
            // 1408 出货通知单：无 SP，仅自动单号（默认单别 CHPC，历史配置待业务确认）
            [1408] = new(1408, null, null, true, "SHIPMENT_NO", "SHIPMENT_TYPE"),
            // 财务：170101 应收货款单（对帐单，批核已由效果链接管，旧 P_WF_COP_ACCOUNT 已退役）
            [170101] = new(170101, null, null, true, "ACCOUNT_NO", "ACCOUNT_TYPE",
                DomainRule: "cop-account"),
            // 170102 收款单（预收冲抵入口；批核/解批已由效果链接管，旧 P_WF_COP_RECEIPT 已退役）
            [170102] = new(170102, null, null, true, "RECEIPT_NO", "RECEIPT_TYPE",
                PrepayOffsetTable: "COP_RECEIPT_PREPAY", DomainRule: "cop-receipt"),
            // 170103 预收帐款单（AfterSave 已实现：客户校验 + 金额汇总；批核已由效果链接管，旧 P_WF_COP_PREPAY 已退役）
            [170103] = new(170103, null, null, true, "PREPAY_NO", "PREPAY_TYPE",
                DomainRule: "cop-prepay"),
            // 170201 应付货款单：AfterSave 已实现为确定性领域规则（purchase-due），
            // 不再调用 P_PUR_DUE_After_Save（金额汇总 + 数量校验由 C# 实现）；
            // 批核已由效果链接管，旧 P_WF_PUR_DUE 已退役
            [170201] = new(170201, null, null, true, "DUE_NO", "DUE_TYPE",
                DomainRule: "purchase-due"),
            // 170202 付款单（批核/解批已由效果链接管，旧 P_WF_PUR_PAY 已退役）
            [170202] = new(170202, null, null, true, "PAY_NO", "PAY_TYPE",
                PrepayOffsetTable: "PUR_PAY_PREPAY", DomainRule: "pur-pay"),
            // 170203 预付帐款单（AfterSave 已实现：厂商校验 + 预付不超采购金额 + 金额汇总；
            // 批核已由效果链接管，旧 P_WF_PUR_PREPAY 已退役）
            [170203] = new(170203, null, null, true, "PREPAY_NO", "PREPAY_TYPE",
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
        [1204] = "bom-stru",
        [180208] = "employee-card",
        [2305] = "sysdg",
        [1502] = "moc-produce",
        [1512] = "moc-produce",
        [2803] = "moc-produce",
        [2804] = "moc-produce",
        [180401] = "sfc-daily",
        [1411] = "cop-fitout",
        [1608] = "pur-cancel",
        [1612] = "pur-cancel",
        [1616] = "pur-apply",
        [180206] = "hr-apply",
        [180301] = "hr-wage-item",
        [180310] = "hr-wage-lz",
        [1803101] = "hr-wage-lz",
        [180502] = "hrm-wage-item",
        [1506] = "moc-bom-stru",
        [2911] = "mou-pro",
        [300301] = "cus-export",
        [300302] = "cus-import",
        [300304] = "cus-export",
        [300305] = "cus-import",
        [3006] = "cus-manual",
        [3014] = "cus-account",
        [1522] = "moc-produce",
        [180207] = "hr-worktime",
        [2708] = "sfc-plan",
    };

    public static bool TryGet(int moduleId, out string? rule) => Rules.TryGetValue(moduleId, out rule);
}

/// <summary>
/// 保存后行为已由校验目录（MODULE_VALIDATION_RULE）承接的模块：这些模块不再调用遗留
/// 保存后过程、也不再登记 C# 领域规则，保存期校验完全由目录实例执行。
/// 本表只是"遗留钩子已迁目录"的事实登记，与自动单号无关——是否自动编号只看 BILLKIND
/// 里有没有该模块的单号规则。
/// </summary>
public static class CatalogAfterSaveMap
{
    private static readonly IReadOnlySet<int> Modules = new HashSet<int>
    {
        2914,              // 料号开模评估资料唯一
        2906,              // 量产模具完工：申请数量不超承认单可申请数量
        3303,              // 品质日分析：制令/产品引用存在 + 受门控的品检数量不超生产单
        2912,              // 量产模入库：入库不超模具完工未入数量（受门控）
        2707,              // 工序发料：出库不超工序工单入库数量（受门控）
        2815, 1515,        // 生产出库：出库不超制令/订单可出库（受门控，FITOUT_TAG 二选一）+ 批管品必填批号
        2703,              // 产品制程：用固定时间时固定时间不得为零
        1413,              // 送货回执：送/退货行不得已有回执（被引用行条件断言）
        1509,              // 制令变更：原单已批核 + 变更量不小于已生产/已领料
        1609,              // 采购变更：原单已批核 + 变更量不小于已收货
        1418,              // 订单变更：原单已批核 + 变更量下限 + 客户订单号不重复
        1503, 1514, 1517, 2805, 2806, 2907,   // 生产领料族与模房领料：批管品必填批号（保存期 line-require）
        1507,              // 生产计划：计划量不超订单（目录 qty-not-exceed 已承接，原 C# 为空实现）
        110103,            // 货币资料：本位币汇率只能为一（主表行字段断言）＋本位币唯一
        130101,            // 库存盘点单：盘点数不小于零（明细行字段断言，命中回报序号）
        180102, 180105, 180110, 180111,   // 员工工号唯一
        180205,            // 每人每月一笔出勤参数
        180211, 180651,    // 当月每人一班排班
        180309, 1803091, 180504,          // 当月每人一份工资表
        180106, 180107, 180108,           // 合同/投保/证件的期间不重叠与同单重复
        2704,              // 工单制程：制令单引用存在
        2908, 2909, 2910,  // 模具出/入库与报废：模具编号引用存在
        1404,              // 报价单：客户与询价单引用存在
        1604,              // 厂商报价单：厂商与询价单引用存在
        1610,              // 收料核价单：厂商引用存在
        130102, 130103, 130104, 130105, 130106, 130107, 130110, 2817, 2818, 3901,   // 库存异动族：批管品必填批号（保存期 line-require）
        1423, 1412, 130108, 130109, 1505, 1519, 2816,   // 同形族：批管品必填批号（保存期 line-require）
        1407, 1409, 2913,   // 同形族：批管品必填批号（保存期 line-require）
        2404,               // 样品出库不超样品库存（保存期 this-not-exceed）
        2705, 2706,         // 工序工单/工序入库不超量（保存期 usage-not-exceed）
    };

    public static bool IsPorted(int moduleId) => Modules.Contains(moduleId);
}
