namespace EOS.API.Data;

/// <summary>
/// 业务动作配置的封闭目录（保存即校验的数据源；执行能力由效果注册表按同一批键实现）。
/// 集合内容与效果目录 v0.2、校验模板目录保持一致。
/// 新增值必须同时登记：本类集合、对应目录文档、注册实现与单测。
/// </summary>
public static class BusinessActionCatalog
{
    /// <summary>触发事件（各事件均已有运行期派发点；MANUAL 不由任何单据事件触发，见下）。</summary>
    public static readonly IReadOnlySet<string> Events = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SAVE",
        "APPROVE_EFFECT",
        "DEAPPROVE",
        "ENDCASE",
        "UNENDCASE",
        "MANUAL",
    };

    /// <summary>用户主动触发的事件码：该行不会被任何单据事件顺带执行，只在用户点击按钮时运行；
    /// 因此它不参与效果链（加载、物理参数校验、明细派生判定、发布键校验一律跳过），
    /// 其键由单据操作注册表单独把关。</summary>
    public const string ManualEvent = "MANUAL";

    /// <summary>
    /// 当前**接不到效果链**的事件：配置能存、发布能过，但没有任何调用点把它交给效果引擎。
    ///
    /// 界面据此**如实标注**（不是隐藏）：隐藏会让既有配置无法编辑，而"配了不跑"必须让配置者看见。
    ///
    /// 空集是**实测结论**而非默认值：事件闭集里的每个事件都已有派发点——
    /// SAVE / DELETE 走保存与删除路径，APPROVE_EFFECT / DEAPPROVE 走批核路径（`RunApprovalCoreAsync`），
    /// ENDCASE / UNENDCASE 走结案路径（`FinishCoreAsync`），MANUAL 走单据操作注册表。
    /// 事件闭集新增成员时必须重新核对这一条。
    /// </summary>
    public static readonly IReadOnlySet<string> InertEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
        "fields-metadata-sync",
        "detail-flag-and-rollup",
        "wage-month-doc-prune",
        "doc-orphan-prune",
        "sfc-plan-sync",
        "cus-account-sync",
        "pur-apply-sync",
        "bom-size-backfill",
        "detail-rollup",
        "pur-pay-offset",
        "cop-receipt-offset",
        "cop-send-mo-flag",
        "pur-purchase-sync",
        "location-path-recalc",
        "depot-sentinel-location",
        "stocktake-scope-generate",
        "inventory-release-by-source",
        // 保留键（暂无落库实例，登记保留）
        "meta-link",
        "flow-trigger",
        "job-enqueue",
    };

    /// <summary>公式行运算（封闭集 + APPEND 已转正；与 MODULE_BUSINESS_ACTION_OP.OP_CODE 对应）。</summary>
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

    /// <summary>校验规则阶段（统一管线：SAVE/APPROVE/DEAPPROVE/DELETE）。
    /// DELETE 用于"删除前"的守卫：删除不产生保存后行为，但主档里的受保护行要在删除前拦下。</summary>
    public static readonly IReadOnlySet<string> ValidationStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SAVE",
        "APPROVE",
        "DEAPPROVE",
        "DELETE",
    };

    /// <summary>校验模板键（目录 v0.1；新模板落地后随注册表同步扩展）。</summary>
    public static readonly IReadOnlySet<string> ValidationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "qty-not-exceed",
        "reference-exists",
        "duplicate-check",
        "line-require",
        "period-overlap",
        "no-cycle",
        "custom-validation",
    };

    public static bool IsKnownEvent(string value) => Events.Contains(value);

    /// <summary>Whether the event code marks a user-triggered document action rather than an effect-chain step.</summary>
    public static bool IsManualEvent(string? value) =>
        value is not null && value.Trim().Equals(ManualEvent, StringComparison.OrdinalIgnoreCase);

    public static bool IsKnownEffectKey(string value) => EffectKeys.Contains(value);

    public static bool IsKnownOpCode(string value) => OpCodes.Contains(value);

    public static bool IsKnownSourceScope(string value) => SourceScopes.Contains(value);

    public static bool IsKnownValidationKey(string value) => ValidationKeys.Contains(value);

    public static bool IsKnownValidationStage(string value) => ValidationStages.Contains(value);

    /// <summary>明细行由服务端在保存期派生的效果键（按主表声明写出明细行）。
    /// 模块声明其中任一动作时，"调用方没提交明细"不等于"这张单据保存后没有明细"，
    /// 保存路径不得据此直接拒绝——真正的判据是保存结束时明细表里有没有行。</summary>
    public static readonly IReadOnlySet<string> DetailGeneratorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "stocktake-scope-generate",
    };

    public static bool IsDetailGenerator(string value) => DetailGeneratorKeys.Contains(value);
}
