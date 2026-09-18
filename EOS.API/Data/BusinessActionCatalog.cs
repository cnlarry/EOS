namespace EOS.API.Data;

/// <summary>
/// 业务动作配置的封闭目录（保存即校验的数据源；执行能力由效果注册表按同一批键实现）。
/// 集合内容与效果目录 v0.2、校验模板目录保持一致。
/// 新增值必须同时登记：本类集合、对应目录文档、注册实现与单测。
/// </summary>
public static class BusinessActionCatalog
{
    /// <summary>触发事件（ENDCASE/UNENDCASE 为占位事件，效果留空）。</summary>
    public static readonly IReadOnlySet<string> Events = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SAVE",
        "APPROVE_EFFECT",
        "DEAPPROVE",
        "ENDCASE",
        "UNENDCASE",
    };

    /// <summary>失败模式：BLOCK=失败整链回滚；WARN=仅警告继续。</summary>
    public static readonly IReadOnlySet<string> FailModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "BLOCK",
        "WARN",
    };

    /// <summary>效果键（目录 v0.2：28 个已落库键 + 4 个保留键）。</summary>
    public static readonly IReadOnlySet<string> EffectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // 已落库（P1 翻译/全面审查）
        "field-accumulate",
        "adjust-projection",
        "stamp-last-activity",
        "completion-close",
        "set-state",
        "link-stamp",
        "inventory-move",
        "balance-adjust",
        "callback-reprice",
        "client-price-sync",
        "supplier-price-sync",
        "field-copy",
        "hr-usage-sync",
        "employee-contract-sync",
        "employee-dimission-sync",
        "mould-batch-apply",
        "mrp-plan-alloc",
        "order-change-apply",
        "produce-change-apply",
        "purchase-change-apply",
        "payment-date-calc",
        "quote-parameter-recalc",
        "half-stock-move",
        "car-filloil-sync",
        "detail-field-sync",
        "sample-edition-bump",
        "mould-ids-sync",
        "card-sibling-close",
        // 保留键（暂无落库实例，登记保留）
        "meta-link",
        "flow-trigger",
        "job-enqueue",
        "legacy-sproc",
    };

    /// <summary>公式行运算（§9.2 封闭集 + APPEND 已转正；与 MODULE_BUSINESS_ACTION_OP.OP_CODE 对应）。</summary>
    public static readonly IReadOnlySet<string> OpCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ACCUM",
        "DEACCUM",
        "ASSIGN",
        "ASSIGN_MAX",
        "ASSIGN_MIN",
        "APPEND",
        "APPEND_UNIQ",
        "SET_WHEN",
    };

    /// <summary>公式行源范围：本单主表 / 明细 / 已登记上下文表 / 常量。</summary>
    public static readonly IReadOnlySet<string> SourceScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MASTER",
        "DETAIL",
        "TABLE",
        "CONSTANT",
    };

    /// <summary>源聚合（null = 单值；闭式）。PICK＝按定位键的唯一相关行原值取值（不做数值默认）。</summary>
    public static readonly IReadOnlySet<string> SourceAggregates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SUM",
        "MAX",
        "MIN",
        "DISTINCT",
        "PICK",
    };

    /// <summary>校验规则阶段（§15 统一管线：SAVE/APPROVE/DEAPPROVE）。</summary>
    public static readonly IReadOnlySet<string> ValidationStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SAVE",
        "APPROVE",
        "DEAPPROVE",
    };

    /// <summary>校验模板键（目录 v0.1；新模板落地后随注册表同步扩展）。</summary>
    public static readonly IReadOnlySet<string> ValidationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "qty-not-exceed",
        "reference-exists",
        "duplicate-check",
        "line-require",
        "period-overlap",
    };

    public static bool IsKnownEvent(string value) => Events.Contains(value);

    public static bool IsKnownEffectKey(string value) => EffectKeys.Contains(value);

    public static bool IsKnownOpCode(string value) => OpCodes.Contains(value);

    public static bool IsKnownSourceScope(string value) => SourceScopes.Contains(value);

    public static bool IsKnownValidationKey(string value) => ValidationKeys.Contains(value);

    public static bool IsKnownValidationStage(string value) => ValidationStages.Contains(value);
}
