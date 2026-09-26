namespace EOS.API.Data.Effects;

/// <summary>
/// 效果配方目录（配置面的默认视图）：把实现层的效果键收敛成配置者认得的**业务概念**。
///
/// 配方**不是新的事实源**，也不新增任何可执行语义——它只回答三个问题：
/// ① 这个配方落到哪些 <see cref="BusinessActionCatalog.EffectKeys"/>（可追溯，禁止凭空发明）；
/// ② 新建一行时的默认事件、建议反向 kind、参数要点（反向 kind 必在该键的
///    <see cref="EffectReverseCompatibility"/> 兼容集内）；
/// ③ 是否需要定位键、是否需要公式行（决定界面上先让配置者填什么）。
///
/// **配方层是默认视图，不是能力阉割**：专家模式仍可逐字段编辑同一份数据，
/// 两态共用同一份草稿，切换不丢改动。
///
/// 依据与全量映射见 `docs/plans/效果配方目录.md`（配方清单、未归类与理由、参数与反向的实测分布）。
/// </summary>
public static class EffectRecipeCatalog
{
    /// <summary>
    /// 一个配方。字段刻意少而实：凡是界面能从既有契约（`/schemas` 的 `paramFields` /
    /// `reverseKindsByEffect`）读出来的，这里都不重复下发。
    /// </summary>
    public sealed record Recipe(
        /// <summary>稳定键（前后端锚点；不含版本语义）。</summary>
        string Key,
        /// <summary>业务名（配置者看得懂的话）。</summary>
        string Name,
        /// <summary>一句话用途。</summary>
        string Summary,
        /// <summary>新建时默认选中的事件（闭集内；可被配置者改）。</summary>
        IReadOnlyList<string> EventCodes,
        /// <summary>该配方覆盖的实现键（必须都在 <see cref="BusinessActionCatalog.EffectKeys"/> 内）。</summary>
        IReadOnlyList<string> EffectKeys,
        /// <summary>是否以公式行（MODULE_BUSINESS_ACTION_OP）为配置主体。</summary>
        bool FormulaMode,
        /// <summary>是否必须给定位键（目标行怎么找）。</summary>
        bool RequiresRelation,
        /// <summary>建议反向 kind；必须落在该配方每个键的兼容集内。</summary>
        string ReversePreset,
        /// <summary>参数要点（一句话）。只描述既有契约里已确证的键；没有 schema 的键如实说明。</summary>
        string ParamsHint,
        /// <summary>必须如实告知配置者的边界（没有则 null）。</summary>
        string? Note = null);

    /// <summary>
    /// 配方清单。顺序 = 界面默认顺序（按覆盖行数降序，见目录文档 §2）。
    /// </summary>
    public static readonly IReadOnlyList<Recipe> All =
    [
        new(
            "write-upstream-qty",
            "回写上游单数量",
            "本单生效后，把数量/金额/日期按定位键累加或覆盖回上游单据（收料回采购已收量、送货回订单已送量）。",
            ["APPROVE_EFFECT"],
            ["field-accumulate"],
            FormulaMode: true,
            RequiresRelation: true,
            ReversePreset: "auto-reverse",
            ParamsHint: "参数区留空：语义全在公式行（目标表.列 ← 来源范围[.列][.聚合]，运算取累加/覆盖/取大等）。",
            Note: "存量 127 行全部不带参数，改这个配方就是改公式行。"),

        new(
            "move-stock",
            "写库存（出入库移动）",
            "按明细行生成库存移动：扣减或增加库存余额并写流水，可选同步重算 MRP。",
            ["APPROVE_EFFECT"],
            ["inventory-move", "half-stock-move"],
            FormulaMode: false,
            RequiresRelation: true,
            ReversePreset: "reverse-flow",
            ParamsHint: "direction 必填（IN 入库 / OUT 出库）；fieldMap 必填（masterDate 本单日期列、qty 数量来源、detail 要带到库存行的明细列）；depotField 默认 DEPOT_ID；mrp 默认 false；rowFilter 可限定参与移动的明细行。"),

        new(
            "completion-close",
            "完成度判定与自动结案",
            "按目标行的完成度判定是否达成，达成即自动写入结案标记（标记 + 人 + 日期）；解批按当前数据重算。",
            ["APPROVE_EFFECT"],
            ["completion-close"],
            FormulaMode: true,
            RequiresRelation: true,
            ReversePreset: "recompute",
            ParamsHint: "targets 指定判定哪张表；direction 取 -1 表示“不超过”；condition/marker 可选；写入动作用公式行表达（FINISHED_TAG 置 1、FINISHED_PERSON 写 SYSTEM、FINISHED_DATE 写当前时间）。",
            Note: "主表与明细通常成对配置（X_M 与 X_D 各一组），只配一半会出现“单据结案了、明细没有”。"),

        new(
            "adjust-projection",
            "在途/预计量投影",
            "调整料件主档上的在途量与预计量（采购在途、订单预出、未入库量等）。",
            ["APPROVE_EFFECT"],
            ["adjust-projection"],
            FormulaMode: true,
            RequiresRelation: true,
            ReversePreset: "auto-reverse",
            ParamsHint: "公式行目标列实测集中在 PRODUCT 的 NOT_IN_QTY / NOT_GET_QTY / NOT_SEND_QTY / MRP_QTY / IN_BUY_QTY；来源多为明细合计（DETAIL.SUM）或主表列。"),

        new(
            "set-state",
            "状态/标志置位",
            "保存后把目标行的状态位/标志位置为指定值（或按来源写日期，取最早/最晚）。",
            ["SAVE"],
            ["set-state"],
            FormulaMode: false,
            RequiresRelation: true,
            ReversePreset: "none",
            ParamsHint: "targets[] 指定目标表与定位引用，state 给出列 → 值（值可为 now）；两种形态二选一、以 targets 为主。"),

        new(
            "balance-adjust",
            "往来/银行余额调整",
            "调整客户/厂商往来余额或银行票据余额（含方向与金额列覆盖）。",
            ["APPROVE_EFFECT"],
            ["balance-adjust"],
            FormulaMode: false,
            RequiresRelation: false,
            ReversePreset: "auto-reverse",
            ParamsHint: "client / supplier / bank 三个分支按需选一（每个分支给金额列与方向）；该键不填表名（表由分支决定）。"),

        new(
            "stamp-last-activity",
            "主档戳记",
            "把本单的最新价或最新交易日期戳记到主档；解批不回退戳记。",
            ["APPROVE_EFFECT"],
            ["stamp-last-activity"],
            FormulaMode: true,
            RequiresRelation: true,
            ReversePreset: "no-reverse",
            ParamsHint: "戳哪些列走参数（targetTable + fields[]，可选 matchBy/condition）；“什么时候戳”走公式行（OPERATION=取较大）。",
            Note: "该键有两种形态并存：参数形态（改戳记列）与公式形态（改戳的条件）。"),

        new(
            "mrp-plan-alloc",
            "MRP 计划量分配",
            "按需求与可用库存把 MRP 计划量分配到目标单据行。",
            ["APPROVE_EFFECT"],
            ["mrp-plan-alloc"],
            FormulaMode: false,
            RequiresRelation: false,
            ReversePreset: "recompute",
            ParamsHint: "targetTable 必填；stockSource 给可用库存来源（如 PRODUCT.MRP_QTY）；scope 必须显式选（本单或全局）——留空会按全局分配。"),

        new(
            "link-stamp",
            "单据引用盖章",
            "把本单的单别/单号/项次盖章到关联单据的引用列，建立单据间引用。",
            ["SAVE"],
            ["link-stamp"],
            FormulaMode: false,
            RequiresRelation: true,
            ReversePreset: "none",
            ParamsHint: "fields[] 给出 目标列 ← 本单列；targets[] 给出目标表与定位引用；mode 可选覆盖/追加，finish 可随盖章写结案三元。"),

        new(
            "detail-rollup",
            "明细汇总回主表",
            "把明细按列汇总（可带四舍五入）回写主表指定列。",
            ["SAVE"],
            ["detail-rollup", "cus-account-sync", "mould-ids-sync", "detail-flag-and-rollup"],
            FormulaMode: false,
            RequiresRelation: false,
            ReversePreset: "none",
            ParamsHint: "detailTable 指定明细表；assignments[] 给出 目标列 ← 汇总列（可加 plusMaster 追加主表列）；roundDigits 控制位数。",
            Note: "这几个键尚无参数 schema（paramFields），参数按根键编辑。"),

        new(
            "change-apply",
            "变更单生效回原单",
            "变更单生效后把变更按列写回原单（主表字段 + 明细列 + 合计），并同步在途/预计量投影。",
            ["APPROVE_EFFECT"],
            ["order-change-apply", "produce-change-apply", "purchase-change-apply"],
            FormulaMode: false,
            RequiresRelation: false,
            ReversePreset: "none",
            ParamsHint: "detail.fields[] 是要写回原单的明细列（主体）；可选 master.fields[] 写回主表列、totals 把明细合计回写主表、projection.mode 控制投影口径。",
            Note: "变更单解批不开放反向（与既有行为一致）；这几个键尚无参数 schema。"),
    ];

    /// <summary>按稳定键取配方；不存在返回 null。</summary>
    public static Recipe? Find(string? key) =>
        key is null
            ? null
            : All.FirstOrDefault(recipe => recipe.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>配方覆盖的实现键并集（界面用它判断"这个键有没有配方可走"）。</summary>
    public static IReadOnlySet<string> CoveredEffectKeys() =>
        All.SelectMany(recipe => recipe.EffectKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
