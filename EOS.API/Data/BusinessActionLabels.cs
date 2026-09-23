namespace EOS.API.Data;

/// <summary>
/// 业务动作配置目录的中文显示名（2301 配置界面用）。
/// 目录值本身仍是 <see cref="BusinessActionCatalog"/> 的闭式枚举；本类只提供人类可读标签，
/// 不参与保存校验。新增目录值时必须同时补标签——<see cref="MissingLabelKeys"/> 为缺口清单，
/// 由单测保证为空，避免界面回落到英文码。
/// </summary>
public static class BusinessActionLabels
{
    /// <summary>事件：单据状态变化点。</summary>
    public static readonly IReadOnlyDictionary<string, string> Events =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SAVE"] = "保存后",
            ["APPROVE_EFFECT"] = "批核生效",
            ["DEAPPROVE"] = "解批",
            ["ENDCASE"] = "结案（占位）",
            ["UNENDCASE"] = "取消结案（占位）",
            ["MANUAL"] = "用户点击（自定义按钮）",
        };

    /// <summary>失败模式。</summary>
    public static readonly IReadOnlyDictionary<string, string> FailModes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BLOCK"] = "失败整链回滚",
            ["WARN"] = "警告后继续",
        };

    /// <summary>效果键。</summary>
    public static readonly IReadOnlyDictionary<string, string> EffectKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["field-accumulate"] = "量额/日期累加回写",
            ["adjust-projection"] = "在途/预计量调整",
            ["stamp-last-activity"] = "主档戳记",
            ["completion-close"] = "完成判定/自动结案",
            ["set-state"] = "状态/标志置位",
            ["link-stamp"] = "单据间引用回写",
            ["inventory-move"] = "库存移动",
            ["balance-adjust"] = "往来/银行余额调整",
            ["callback-reprice"] = "回执价重算",
            ["client-price-sync"] = "客户计价同步",
            ["supplier-price-sync"] = "厂商计价同步",
            ["field-copy"] = "字段跨表回写",
            ["hr-usage-sync"] = "工时/加班额度回写",
            ["employee-contract-sync"] = "员工合同同步",
            ["employee-dimission-sync"] = "员工离职同步",
            ["mould-batch-apply"] = "量产申请批次处理",
            ["mrp-plan-alloc"] = "MRP 计划量分配",
            ["order-change-apply"] = "订单变更生效",
            ["produce-change-apply"] = "制令变更生效",
            ["purchase-change-apply"] = "采购变更生效",
            ["payment-date-calc"] = "预计收付款日期",
            ["quote-parameter-recalc"] = "报价参数重算",
            ["half-stock-move"] = "半成品库存移动",
            ["car-filloil-sync"] = "车辆里程/油卡余额回写",
            ["detail-field-sync"] = "明细新旧字段同步",
            ["sample-edition-bump"] = "样品版次递增",
            ["mould-ids-sync"] = "制令在制模具号回写",
            ["card-sibling-close"] = "员工卡到期日收口",
            ["fields-metadata-sync"] = "字段元数据同步",
            ["detail-flag-and-rollup"] = "明细标志与主表汇总",
            ["wage-month-doc-prune"] = "离职工资同月唯一",
            ["doc-orphan-prune"] = "BOM 孤儿行清理",
            ["sfc-plan-sync"] = "工序生产计划补全",
            ["cus-account-sync"] = "海关对帐单汇总",
            ["pur-apply-sync"] = "请购单同步",
            ["bom-size-backfill"] = "BOM 长宽回填",
            ["detail-rollup"] = "明细汇总回主表",
            ["pur-pay-offset"] = "付款预冲抵",
            ["cop-receipt-offset"] = "收款预冲抵",
            ["cop-send-mo-flag"] = "送货包装标记",
            ["pur-purchase-sync"] = "采购单同步",
            ["location-path-recalc"] = "库位路径重算",
            ["depot-sentinel-location"] = "库位哨兵行维护",
            ["stocktake-scope-generate"] = "盘点范围生成",
            ["meta-link"] = "元数据联动（保留）",
            ["flow-trigger"] = "触发后续流程（保留）",
            ["job-enqueue"] = "作业入队（保留）",
        };

    /// <summary>公式行运算（闭式集）。</summary>
    public static readonly IReadOnlyDictionary<string, string> OpCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ACCUM"] = "累加",
            ["DEACCUM"] = "减",
            ["ASSIGN"] = "覆盖",
            ["ASSIGN_MAX"] = "取较大",
            ["ASSIGN_MIN"] = "取较小",
            ["APPEND"] = "追加",
            ["APPEND_UNIQ"] = "去重追加",
            ["SET_WHEN"] = "条件置为",
        };

    /// <summary>公式行源范围。</summary>
    public static readonly IReadOnlyDictionary<string, string> SourceScopes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MASTER"] = "本单主表",
            ["DETAIL"] = "本单明细",
            ["TABLE"] = "已登记上下文表",
            ["CONSTANT"] = "常量",
        };

    /// <summary>源聚合（空 = 单值）。</summary>
    public static readonly IReadOnlyDictionary<string, string> SourceAggregates =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SUM"] = "合计",
            ["MAX"] = "最大",
            ["MIN"] = "最小",
            ["DISTINCT"] = "去重",
            ["PICK"] = "唯一行原值",
        };

    /// <summary>校验规则阶段。</summary>
    public static readonly IReadOnlyDictionary<string, string> ValidationStages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SAVE"] = "保存前",
            ["APPROVE"] = "批核前",
            ["DEAPPROVE"] = "解批前",
            ["DELETE"] = "删除前",
        };

    /// <summary>校验模板键。</summary>
    public static readonly IReadOnlyDictionary<string, string> ValidationKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["qty-not-exceed"] = "不超量",
            ["reference-exists"] = "引用存在性",
            ["duplicate-check"] = "查重/唯一性",
            ["line-require"] = "行字段约束",
            ["period-overlap"] = "期间不重叠",
            ["no-cycle"] = "成环检测",
            ["custom-validation"] = "定制校验",
        };

    /// <summary>解批反向语义 kind。</summary>
    public static readonly IReadOnlyDictionary<string, string> ReverseKinds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auto-reverse"] = "按公式行自动反向",
            ["reverse-flow"] = "写反向流水（库存类）",
            ["recompute"] = "按当前数据重算",
            ["recompute-excluding-self"] = "重算时排除本单",
            ["recalc-confirmed"] = "仅对已确认行重算",
            ["no-reverse"] = "解批不反向",
            ["none"] = "明确无反向语义",
            ["snapshot"] = "快照补偿还原",
            ["net-replace"] = "净值替换",
            ["clear-refs"] = "解批清空引用",
            ["clear-refs-unfinish"] = "清空引用并取消结案",
            ["clear-finish"] = "解批清结案标志",
            ["clear-on-deapprove"] = "解批清空该列",
            ["restore-previous"] = "恢复更新前的值",
            ["restore-old-price"] = "还原旧单价",
            ["restore-active"] = "非对称还原",
        };

    /// <summary>
    /// 目录键 → 标签映射表清单（供单测做覆盖完整性校验）。
    /// </summary>
    public static IReadOnlyList<(string Name, IReadOnlySet<string> Keys, IReadOnlyDictionary<string, string> Labels)> CatalogPairs() =>
    [
        ("events", BusinessActionCatalog.Events, Events),
        ("failModes", BusinessActionCatalog.FailModes, FailModes),
        ("effectKeys", BusinessActionCatalog.EffectKeys, EffectKeys),
        ("opCodes", BusinessActionCatalog.OpCodes, OpCodes),
        ("sourceScopes", BusinessActionCatalog.SourceScopes, SourceScopes),
        ("sourceAggregates", BusinessActionCatalog.SourceAggregates, SourceAggregates),
        ("validationStages", BusinessActionCatalog.ValidationStages, ValidationStages),
        ("validationKeys", BusinessActionCatalog.ValidationKeys, ValidationKeys),
        ("reverseKinds", EffectStructSchemas.AllReverseKinds().ToHashSet(StringComparer.OrdinalIgnoreCase), ReverseKinds),
    ];

    /// <summary>缺失标签的（映射表名, 目录键）清单；空表示每个目录值都有中文标签。</summary>
    public static IReadOnlyList<string> MissingLabelKeys()
    {
        var missing = new List<string>();
        foreach (var (name, keys, labels) in CatalogPairs())
            foreach (var key in keys)
                if (!labels.ContainsKey(key))
                    missing.Add($"{name}:{key}");
        return missing;
    }
}
