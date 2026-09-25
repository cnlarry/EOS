namespace EOS.API.Data.Effects;

/// <summary>
/// Per-key parameter descriptions (name, type, whether it is required, enum values, default,
/// what it means, an example). The catalog in <see cref="EffectStructSchemas"/> only says which
/// root keys exist; this is the layer that tells a configurator what to put in them, so the UI
/// can render real controls instead of a JSON box.
///
/// Coverage is deliberately partial: only keys whose semantics are established by their handler
/// are described. Keys without an entry keep the current root-key editor — inventing a
/// description would be worse than admitting there is none.
/// </summary>
public static class EffectParamDescriptors
{
    /// <summary>Closed set of descriptor types (drives which control the UI renders).</summary>
    public static readonly IReadOnlyList<string> Types =
        ["string", "number", "boolean", "array", "object", "fieldRef", "tableRef", "fieldMap"];

    public sealed record Descriptor(
        string Name,
        string Type,
        bool Required,
        string Description,
        IReadOnlyList<string>? EnumValues = null,
        string? Default = null,
        string? Example = null);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Descriptor>> ByEffectKey =
        new Dictionary<string, IReadOnlyList<Descriptor>>(StringComparer.OrdinalIgnoreCase)
        {
            ["inventory-move"] =
            [
                new("direction", "string", true, "库存变动方向：IN 入库（增加库存）、OUT 出库（扣减库存）。",
                    EnumValues: ["IN", "OUT"], Example: "OUT"),
                new("fieldMap", "fieldMap", true,
                    "单据字段到库存行的映射：masterDate 是本单日期列；qty 是数量来源（列名，或 {terms:[{field,coef}]} 闭式加减项，coef 仅允许 1/-1）；detail 是要带到库存行的明细列（列名或 {column,constant}）。"),
                new("depotField", "fieldRef", false, "本单上取库别的列名；库位列由它推导（把 DEPOT_ID 换成 LOCATION_NO）。",
                    Default: "DEPOT_ID", Example: "DEPOT_ID"),
                new("rowFilter", "object", false,
                    "行级闸门：{anyPositive:[列]} 表示只有这些列为正数的明细行参与本次移动；不配即全部明细行参与。"),
                new("mrp", "boolean", false, "是否在本次移动后同步重算 MRP 派生量。"),
                new("targetTable", "tableRef", false, "库存目标表；不配即取库存读取出口的余额表。"),
            ],
            ["half-stock-move"] =
            [
                new("direction", "number", true, "半成品移动方向：1 流入、-1 流出。", EnumValues: ["1", "-1"]),
                new("fieldMap", "fieldMap", true, "单据字段到半成品库存行的映射（同 inventory-move 的 fieldMap）。"),
            ],
            ["set-state"] =
            [
                new("targetTable", "tableRef", true, "要置位的目标表（通常是上游单据表）。"),
                new("stateField", "fieldRef", false, "单一形态下要写入的状态列名。"),
                new("stateValue", "string", false, "要写入状态列的值（日期模式可为 now）。"),
                new("targets", "array", false,
                    "多目标形态：每个目标一张表 + 一组引用（refs：{target,source|masterSource}），按本单明细或主表列定位目标行。"),
                new("state", "object", false, "多目标形态下的状态映射：列名 → 值（值可为 now 表示当前时间）。"),
                new("source", "object", false, "日期来源：{scope:TABLE, table:表}；整单一行取该表当前行的日期。"),
                new("sourceField", "fieldRef", false, "日期来源列（与 source 二选一）。"),
                new("dateField", "fieldRef", false, "要写入的日期列。"),
                new("dateMode", "string", false, "多来源日期的取值方式：MIN 取最早、MAX 取最晚。", EnumValues: ["MIN", "MAX"]),
            ],
            ["field-accumulate"] =
            [
                new("mode", "string", false, "累加口径：按目标列的语义累加数量或金额。"),
                new("targets", "array", false, "多目标形态：按表分组的回写目标集合。"),
            ],
            ["adjust-projection"] =
            [
                new("mode", "string", false, "在途量调整口径（增加或减少预计量）。"),
                new("inFields", "array", false, "本单上表示在途数量的列集合。"),
                new("getFields", "array", false, "本单上表示预计入库数量的列集合。"),
            ],
            ["completion-close"] =
            [
                new("targets", "array", false, "要判定完成度的目标表集合。"),
                new("condition", "object", false, "结构化完成条件（与动作级条件同一套算子）。"),
                new("marker", "object", false, "完成后要写入的结案标记列与值。"),
                new("direction", "string", false, "完成度比较方向（达到/不超过）。"),
                new("offsets", "array", false, "容差或偏移量配置。"),
            ],
            ["balance-adjust"] =
            [
                new("client", "object", false, "客户往来余额分支：金额列与方向（direction 决定增/减）。"),
                new("supplier", "object", false, "厂商往来余额分支：金额列与方向。"),
                new("bank", "object", false, "银行/票据余额分支：金额列与方向。"),
            ],
            ["payment-date-calc"] =
            [
                new("targetField", "fieldRef", true, "要写入的预计收付款日期列。"),
                new("dateField", "fieldRef", true, "本单上的基准日期列（单据日期或结帐月份）。"),
                new("monthField", "fieldRef", false, "结帐月份列（CHAR(8) 的 YYYY-MM- 形态）。"),
                new("paymentDaysFrom", "number", false, "客户/厂商档上的付款天数来源列或固定天数。"),
            ],
            ["detail-field-sync"] =
            [
                new("targetTable", "tableRef", true, "要同步的目标明细表。"),
                new("key", "object", true, "目标行的定位键：本单列 → 目标表列。"),
                new("pairs", "array", true, "字段对：{from,to} 表示把本单列的值写到目标列。"),
            ],
            ["car-filloil-sync"] =
            [
                new("master", "object", true, "本单主表上车辆与油卡的定位列。"),
                new("carTable", "tableRef", true, "车辆主档表（里程回写目标）。"),
                new("oilcardTable", "tableRef", true, "油卡主档表（余额回写目标）。"),
            ],
            ["link-stamp"] =
            [
                new("targetTable", "tableRef", true, "要盖章（写引用）的目标表。"),
                new("field", "fieldRef", false, "单一形态下要写入的引用列。"),
                new("fields", "array", false, "要写入的引用列集合。"),
                new("targets", "array", false, "多目标形态：每张表的引用列与来源（refs / sourceRefs）。"),
                new("mode", "string", false, "写入模式（覆盖 / 追加）。"),
                new("finish", "object", false, "盖章时一并写入的结案三元（标记/人/日期）。"),
            ],
            ["field-copy"] =
            [
                new("targetTable", "tableRef", true, "要回写的目标表。"),
                new("field", "fieldRef", false, "单一形态下要写入的目标列。"),
                new("fields", "array", false, "要写入的目标列集合。"),
                new("targets", "array", false, "多目标形态：每张表与它的列映射。"),
                new("sourceField", "fieldRef", false, "本单上取值的来源列。"),
                new("headerFields", "array", false, "随行携带的表头字段集合。"),
            ],
            ["mrp-plan-alloc"] =
            [
                new("targetTable", "tableRef", true, "MRP 计划量写入的目标表。"),
                new("mode", "string", false, "分配口径（按需求或按可用量）。"),
                new("scope", "object", false, "本次分配的作用范围（库别/料号等维度）。"),
                new("field", "fieldRef", false, "要写入的计划量列。"),
                new("fields", "array", false, "要写入的计划量列集合。"),
                new("stockSource", "object", false, "可用库存的读取来源（表与列）。"),
                new("note", "string", false, "备注（不参与计算）。"),
            ],
            ["hr-usage-sync"] =
            [
                new("targetTable", "tableRef", true, "额度回写的目标明细表（HR_ENACTMENT_D / HR_APPLY_D）。"),
                new("scopeKey", "object", true, "额度行的定位键：emp 为员工号来源列（EMP_ID）。"),
                new("sourceFields", "array", true, "本单上取用量的列集合。"),
                new("targetFields", "array", true, "目标表上要扣减/回写的列集合。"),
            ],
            ["quote-parameter-recalc"] =
            [
                new("targetTable", "tableRef", true, "要重算的报价参数表。"),
                new("mode", "string", true, "重算模式：recalc-confirmed 仅对已确认行重算。", EnumValues: ["recalc-confirmed"]),
                new("feeFields", "array", false, "参与重算的费用列集合。"),
            ],
        };

    /// <summary>Descriptors for one effect key (empty when the key is not described yet).</summary>
    public static IReadOnlyList<Descriptor> For(string effectKey) =>
        ByEffectKey.TryGetValue(effectKey.Trim(), out var fields) ? fields : [];

    /// <summary>Effect keys that carry a description set (drives the /schemas payload).</summary>
    public static IReadOnlyList<string> DescribedKeys() =>
        ByEffectKey.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();
}
