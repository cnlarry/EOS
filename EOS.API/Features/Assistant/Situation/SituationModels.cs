namespace EOS.API.Features.Assistant.Situation;

/// <summary>
/// 前端上报的处境（界面状态总线的原始输入）。
/// 上报内容一律是**数据**：只用于解释与定位，不进提示词的指令区，也不作为权限依据。
/// </summary>
public sealed record SituationReport(
    int? ModuleId,
    string? ModuleTitle,
    string? PageType,
    string? DocNo,
    IReadOnlyList<SituationFilter>? Filters,
    IReadOnlyList<string>? Selection,
    IReadOnlyList<SituationDirtyField>? FormDirty,
    SituationNotice? LastNotice,
    SituationConfigTarget? ConfigTarget);

/// <summary>列表上的一条结构化筛选条件（字段 + 操作符 + 值）。</summary>
public sealed record SituationFilter(string? Field, string? Operator, string? Value);

/// <summary>表单上已改未保存的字段：字段名 + 旧值 + 新值。</summary>
public sealed record SituationDirtyField(string? Field, string? Old, string? New);

/// <summary>最近一次服务端拒绝：错误码（须为服务端已登记的错误码）+ 摘要。</summary>
public sealed record SituationNotice(string? Code, string? Summary);

/// <summary>配置页处境：正在配哪个对象。标识符须经库内元数据校验，且只用于解释与定位。</summary>
public sealed record SituationConfigTarget(
    string? Surface,
    string? TableId,
    string? FieldId,
    long? ActionId,
    string? EffectKey);

/// <summary>服务端校验与截断之后的处境。<paramref name="Dropped"/> 记录被剔除的上报项。</summary>
public sealed record SituationContext(
    int? ModuleId,
    string? ModuleTitle,
    string? PageType,
    string? DocNo,
    IReadOnlyList<SituationFilter> Filters,
    IReadOnlyList<string> Selection,
    IReadOnlyList<SituationDirtyField> FormDirty,
    SituationNotice? LastNotice,
    SituationConfigTarget? ConfigTarget,
    IReadOnlyList<string> Dropped)
{
    public static SituationContext Empty { get; } =
        new(null, null, null, null, [], [], [], null, null, []);

    /// <summary>是否有"你在哪"可讲（模块页或配置页）。</summary>
    public bool HasPage => ModuleId is not null || !string.IsNullOrWhiteSpace(PageType)
        || !string.IsNullOrWhiteSpace(DocNo) || ConfigTarget is not null;
}

/// <summary>页面类型白名单：单据态（list/view/edit/new/copy）+ 配置态。</summary>
public static class SituationPageTypes
{
    public const string List = "list";
    public const string View = "view";
    public const string Edit = "edit";
    public const string New = "new";
    public const string Copy = "copy";
    public const string ConfigFields = "config-fields";
    public const string ConfigDataSource = "config-datasource";
    public const string ConfigButtons = "config-buttons";
    public const string ConfigEffect = "config-effect";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        List, View, Edit, New, Copy, ConfigFields, ConfigDataSource, ConfigButtons, ConfigEffect,
    };

    public static bool IsKnown(string? pageType)
        => !string.IsNullOrWhiteSpace(pageType) && All.Contains(pageType.Trim());
}

/// <summary>配置面的四个面。</summary>
public static class SituationConfigSurfaces
{
    public const string Fields = "fields";
    public const string DataSource = "datasource";
    public const string Buttons = "buttons";
    public const string Effect = "effect";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Fields, DataSource, Buttons, Effect,
    };

    public static bool IsKnown(string? surface)
        => !string.IsNullOrWhiteSpace(surface) && All.Contains(surface.Trim());
}

/// <summary>允许上报的筛选操作符，与统一查询算子表同一份口径。</summary>
public static class SituationOperators
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "gt", "gte", "lt", "lte", "contains", "notcontains",
        "startswith", "endswith", "empty", "notempty", "between",
    };

    public static bool IsKnown(string? op)
        => !string.IsNullOrWhiteSpace(op) && All.Contains(op.Trim());
}

/// <summary>
/// 服务端错误码白名单：上报的 <c>lastNotice.code</c> 必须命中其一，否则整条丢弃。
/// 白名单之外的字符串一律不进入提示词，避免自造串成为注入通道。
/// </summary>
public static class SituationNoticeCodes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        // 通用契约
        "INVALID_ARGUMENT", "INVALID_MODEL", "NOT_FOUND", "UNAUTHORIZED", "FORBIDDEN", "INTERNAL_ERROR",
        "LOGIN_INVALID_INPUT", "LOGIN_USER_NOT_FOUND", "LOGIN_INVALID_PASSWORD", "LOGIN_DISABLED", "LOGIN_LOCKED",
        // 写入与校验
        "VALIDATION_FAILED", "BUSINESS_VALIDATION_FAILED", "REQUIRED_FIELD_MISSING", "SERVER_FILL_MISSING",
        "NO_WRITABLE_FIELDS", "CONCURRENT_MODIFIED", "IDEMPOTENCY_KEY_REQUIRED", "DUPLICATE_RECORD_KEY",
        "BILL_NO_CONFLICT", "RECORD_NOT_FOUND", "RECORD_KEY_MISMATCH", "TABLE_MISMATCH", "NO_PRIMARY_KEY",
        // 生命周期与状态
        "APPROVED_RECORD_NOT_DELETABLE", "CONFIRMED_EDIT_FORBIDDEN", "FINISHED_EDIT_FORBIDDEN",
        "FINISHED_RECORD_NOT_DEAPPROVABLE", "FINISHED_RECORD_NOT_DELETABLE", "ENDCASE_STATE_CONFLICT",
        "ENDCASE_NOT_SUPPORTED", "MASTER_NO_CONFIRM_TAG", "MODULE_NO_APPROVE", "LIFECYCLE_COLUMN_MISSING",
        // 数据范围与归属
        "RECORD_OUT_OF_SCOPE", "RECORD_OUT_OF_MODULE_FILTER", "DATA_FILTER_UNSUPPORTED",
        // 审批流
        "FLOW_NOT_FOUND", "FLOW_NOT_ACTIVE", "FLOW_IN_PROGRESS", "FLOW_IN_PROGRESS_EDIT_FORBIDDEN",
        "FLOW_IN_PROGRESS_DELETE_FORBIDDEN", "FLOW_ACTIVE_CANNOT_EDIT", "FLOW_ACTIVE_CANNOT_DELETE",
        "FLOW_ALREADY_FINISHED", "FLOW_NO_APPROVE_POWER", "FLOW_NO_STEPS", "FLOW_NAME_REQUIRED",
        "FLOW_NAME_TOO_LONG", "FLOW_CONDITION_UNSUPPORTED", "INVALID_FLOW_PAYLOAD", "INVALID_FLOW_STATE",
        "INVALID_APPROVE_STATE", "INVALID_WITHDRAW_REQUEST", "WORKFLOW_FAILED", "WORKFLOW_NOT_SUPPORTED",
        "WORKFLOW_STATE_CONFLICT", "NOT_FLOW_INITIATOR", "NOBACK_BLOCKED", "MUST_SIGN_NOT_SIGN",
        "MUST_SIGN_NOT_IN_PEOPLE", "STEP_NO_PEOPLE", "STEP_DESC_REQUIRED", "STEP_DESC_TOO_LONG",
        "SIGN_NEED_PERCENT", "INVALID_PASS_PERCENT", "UNKNOWN_PERSON",
        // 选择器与元数据
        "CHOOSER_FILTER_INVALID", "CHOOSER_RETURN_INVALID", "UNKNOWN_SOURCE", "SOURCE_EMPTY", "SOURCE_NOT_FOUND",
        "INVALID_TABLE_KIND", "INVALID_MASTER_TABLE", "MODULE_NOT_FOUND", "MODULE_NOT_WORKBENCH",
        "MODULE_DEFINITION_NOT_PUBLISHED", "MODULE_NOT_SIMULATABLE", "SIMULATION_NOT_SUPPORTED",
        "SIMULATION_EVENT_UNSUPPORTED", "SIMULATION_TIMEOUT", "DRAFT_CONFIG_INVALID",
        "INVALID_EXPRESSION_KIND", "INVALID_RECORD_KEY", "INVALID_SORT_NO", "DUPLICATE_SORT_NO",
        // 参数与范围
        "INVALID_PARAMETERS", "INVALID_DATE", "INVALID_RANGE", "INVALID_MODE", "INVALID_MONTH",
        "INVALID_ORDER", "INVALID_CARDS", "INVALID_GROUP_INDEX", "RANGE_TOO_LARGE", "TOO_MANY_FIELDS",
        "NO_TARGET", "NO_EMPLOYEE", "WAGE_SETUP_MISSING", "DETAIL_NOT_SUPPORTED", "INVENTORY_LOG_EXISTS",
        // 助手自身
        "MEMORY_LIMIT", "MEMORY_PII_RISK", "CHANGESET_BLOCKED", "CONFIRM_REQUIRED",
        "AI_MODEL_NOT_CONFIGURED", "AI_MODEL_ERROR", "AI_MODEL_EMPTY_REPLY",
        "COST_LIMIT_EXCEEDED", "RATE_LIMITED",
        // 传输层兜底码（前端在服务端未给 code 时按状态码合成）
        "HTTP_400", "HTTP_403", "HTTP_404", "HTTP_409", "HTTP_429", "HTTP_500", "HTTP_503",
    };

    public static bool IsKnown(string? code)
        => !string.IsNullOrWhiteSpace(code) && All.Contains(code.Trim());
}
