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
        ["inventory-release-by-source"] = "来源结案释放预留",
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
    /// 效果键说明：一段人话讲清"这个键在单据上做什么"。
    /// 标签只回答"叫什么"，说明回答"什么时候用、会发生什么"——界面上两个都要看得到。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> EffectKeyDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["field-accumulate"] = "把本单的数量/金额/日期按定位键累加回写到上游单据（如收料单批核后回写采购单已收量）。",
            ["adjust-projection"] = "调整料件的在途量与预计量（如采购在途、订单预出）。",
            ["stamp-last-activity"] = "把本单的最新价或最新交易日期戳记到主档；解批不回退戳记。",
            ["completion-close"] = "按目标行的完成度判定是否达成，达成即自动写入结案标记。",
            ["set-state"] = "把目标行的状态位/标志位置为指定值，或按来源写入日期（取最早/最晚）。",
            ["link-stamp"] = "把本单的单别单号盖章到关联单据的引用列，建立单据间引用。",
            ["inventory-move"] = "按明细行生成库存移动：扣减或增加库存余额并写流水，可选同步重算 MRP。",
            ["balance-adjust"] = "调整客户/厂商往来余额或银行票据余额（含方向与金额列覆盖）。",
            ["callback-reprice"] = "回执单生效后按回执价重算相关单据的价格。",
            ["client-price-sync"] = "把报价/订单价格同步到客户计价档（可保留旧价、按时间覆盖）。",
            ["supplier-price-sync"] = "把采购价格同步到厂商计价档（可保留旧价、按时间覆盖）。",
            ["field-copy"] = "把本单字段按定位键回写到其它表（跨表字段复制）。",
            ["hr-usage-sync"] = "把工时/请假/加班占用写回额度明细；解批按公式行自动反向。",
            ["employee-contract-sync"] = "员工合同生效后同步人事档；解批重算时排除本单。",
            ["employee-dimission-sync"] = "员工离职生效后同步人事状态；解批非对称还原为在职。",
            ["mould-batch-apply"] = "量产申请生效后按批次处理模具与产品字段。",
            ["mrp-plan-alloc"] = "按需求与可用库存分配 MRP 计划量。",
            ["order-change-apply"] = "订单变更单生效后把变更写回原订单（净替换或累计）。",
            ["produce-change-apply"] = "制令变更单生效后把变更写回原制令（含在途/预计量投影）。",
            ["purchase-change-apply"] = "采购变更单生效后把变更写回原采购单。",
            ["payment-date-calc"] = "按结帐月份与客户/厂商的付款天数推算预计收付款日期。",
            ["quote-parameter-recalc"] = "重算报价参数表的费用与参数列（仅对已确认行）。",
            ["half-stock-move"] = "半成品出入库移动（与库存移动同构，方向以 1/-1 表达）。",
            ["car-filloil-sync"] = "车辆/油卡单据生效后回写里程与油卡余额；解批恢复更新前的值。",
            ["detail-field-sync"] = "把明细上的旧字段同步到新字段；解批恢复更新前的值。",
            ["sample-edition-bump"] = "样品首次晋升时递增版次；解批不回退版次。",
            ["mould-ids-sync"] = "把制令在制的模具号汇总回写到主档。",
            ["card-sibling-close"] = "员工卡换发时收口上一张卡的到期日。",
            ["fields-metadata-sync"] = "同步字段元数据；属派生结果，解批不反向，重算即可。",
            ["detail-flag-and-rollup"] = "按明细标志汇总金额与数量后回写主表。",
            ["wage-month-doc-prune"] = "保证离职工资单据同月唯一（重复行清理）。",
            ["doc-orphan-prune"] = "清理 BOM 里已无来源的孤儿行。",
            ["sfc-plan-sync"] = "按工序与明细补全生产计划信息。",
            ["cus-account-sync"] = "海关对帐单的金额与数量汇总。",
            ["pur-apply-sync"] = "请购单生效后同步到采购单（回写已采购量等）。",
            ["bom-size-backfill"] = "把来源表的长宽回填到 BOM 行。",
            ["detail-rollup"] = "明细按列汇总后回写主表指定列。",
            ["pur-pay-offset"] = "付款单与来源单据的预冲抵（含超额与到期的提示文案）。",
            ["cop-receipt-offset"] = "收款单与来源单据的预冲抵（含超额与到期的提示文案）。",
            ["cop-send-mo-flag"] = "送货单按类型标记制令包装字段。",
            ["pur-purchase-sync"] = "采购单生效后同步厂商计价与关联字段。",
            ["location-path-recalc"] = "库位层级变动后重算整棵子树的物化路径。",
            ["depot-sentinel-location"] = "维护库别的『未指定位置』哨兵行。",
            ["stocktake-scope-generate"] = "按盘点范围（库别/库位路径）生成盘点单明细行。",
        ["inventory-release-by-source"] = "来源单据结案/取消时，把该单据名下的有效预留整笔释放，并按可用量口径重算受影响格子的 USEABLE_QTY。",

            ["meta-link"] = "保留键：元数据联动，当前没有落库实例。",
            ["flow-trigger"] = "保留键：触发后续流程，当前没有落库实例。",
            ["job-enqueue"] = "保留键：作业入队，当前没有落库实例。",
        };

    /// <summary>
    /// 反向 kind 说明：每个取值"解批时到底怎么反悔"。
    /// 名义闭集（16 个取值）不等于每个效果键的执行闭集；说明只解释语义，
    /// 某个键到底认哪几个取值由 <see cref="Effects.EffectReverseCompatibility"/> 判定。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ReverseKindDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auto-reverse"] = "按公式行自身的语义自动反向（累加的反向就是减回）。",
            ["clear-on-deapprove"] = "解批清空该列；原值需由别处记载，单条公式行无法表达。",
            ["no-reverse"] = "解批不做任何反向（明确声明不回退）。",
            ["clear-refs"] = "解批清空单据间引用列。",
            ["net-replace"] = "净值替换：按当前净值重算目标列，而不是回退到旧值。",
            ["none"] = "明确没有反向语义（与 no-reverse 同义，旧配置沿用）。",
            ["recompute"] = "解批后按当前事实重算，而不是回退到旧值。",
            ["recompute-excluding-self"] = "重算时排除本单自身的影响（本单不再计入）。",
            ["restore-active"] = "非对称还原：解批后恢复为『在职 / 有效』这类正向状态。",
            ["recalc-confirmed"] = "仅对已确认的行重算。",
            ["restore-old-price"] = "还原更新前的单价并清理引用。",
            ["reverse-flow"] = "写一笔反向流水（库存类常用）：原流水不删除。",
            ["clear-finish"] = "解批清除结案标志。",
            ["clear-refs-unfinish"] = "清空引用并同时取消结案。",
            ["restore-previous"] = "恢复更新前的值（由处理器记录旧值后还原）。",
            ["snapshot"] = "按快照补偿还原。",
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

    /// <summary>
    /// 缺失说明的（映射表名, 目录键）清单；空表示每个效果键与每个反向 kind 都有人话说明。
    /// 说明不是装饰：配置面靠它回答"这个键做什么""解批会怎么反悔"。
    /// </summary>
    public static IReadOnlyList<string> MissingDescriptionKeys()
    {
        var missing = new List<string>();
        foreach (var key in BusinessActionCatalog.EffectKeys)
        {
            if (!EffectKeyDescriptions.ContainsKey(key))
            {
                missing.Add($"effectKeyDescriptions:{key}");
            }
        }
        foreach (var kind in EffectStructSchemas.AllReverseKinds())
        {
            if (!ReverseKindDescriptions.ContainsKey(kind))
            {
                missing.Add($"reverseKindDescriptions:{kind}");
            }
        }
        return missing;
    }
}
