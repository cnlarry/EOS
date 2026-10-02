using EOS.API.Features.Assistant.Config;
using EOS.API.Features.Assistant.Diagnosis;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Situation;

namespace EOS.API.Features.Assistant.Parameters;

/// <summary>
/// 一个参数域（页面分组）。<see cref="Seq"/> 是 <b>组间顺序</b>（SYSSS 的 <c>GROUP_SEQ</c>）：
/// 它按 §5.3 一次性占号，**先占号不建行**——后批落库时不必重排已有分组。
/// </summary>
public sealed record AssistantParameterGroup(string Code, string Label, int Seq);

/// <summary>
/// 作用域策略：这条参数被某一层覆盖时，值能往哪个方向走。
///
/// <para>
/// <see cref="Tighten"/> 是本框架的安全边界：作用域值**只能比上级更严**（布尔只能关、阈值只能更小），
/// 于是"给某模块单独打开一个全局已关的能力"在参数层根本表达不出来。只有确实需要放宽的成本限额
/// 才声明为 <see cref="Override"/>。
/// </para>
/// </summary>
public enum AssistantParameterScopePolicy
{
    /// <summary>不可作用域化：只能全库一个值。</summary>
    None = 0,

    /// <summary>可作用域化，但只能更严（关得掉、放不开）。</summary>
    Tighten = 1,

    /// <summary>可作用域化，可高可低。</summary>
    Override = 2,
}

/// <summary>
/// 这条参数**允许出现在哪些层**。
///
/// <para>
/// 它必须与策略分开声明，因为"能往哪个方向走"和"在哪些层上比"是两件事：
/// <see cref="AssistantParameterScopePolicy.Tighten"/> 要判"比上层更严"，而"上层"只用一层时才唯一确定。
/// 于是规则是：**收紧型只能声明一层**（模块层比全局、或用户层比全局）；两层都声明的收紧型，
/// 在"模块已收紧、用户再放回全局水平"这种组合下无法判定，保存与生效会给出不同答案——那正是要避免的。
/// <see cref="AssistantParameterScopePolicy.Override"/>（可高可低）不受此限，两层都可以声明。
/// </para>
/// </summary>
[Flags]
public enum AssistantParameterScopeLayers
{
    None = 0,

    /// <summary>可按模块覆盖（<c>SCOPE_TYPE = MODULE</c>，值为模块号）。</summary>
    Module = 1,

    /// <summary>可按用户覆盖（<c>SCOPE_TYPE = USER</c>，值为 USER_ID）。</summary>
    User = 2,
}

/// <summary>
/// 一条参数的元数据。<c>DefaultValue</c> 直接取自代码默认值（见 <see cref="AssistantParameterCatalog"/>），
/// 所以界面上显示的"默认值"不会与代码漂移。
/// </summary>
public sealed record AssistantParameterDescriptor(
    string Key,
    string DisplayName,
    string Group,
    string ValueType,
    string? Unit,
    string Description,
    string DefaultValue,
    AssistantParameterScopePolicy ScopePolicy,
    IReadOnlyList<string> Consumers,
    AssistantParameterScopeLayers Layers = AssistantParameterScopeLayers.None,
    decimal? Min = null,
    bool MinExclusive = false,
    decimal? Max = null,
    int? MaxLength = null);

/// <summary>
/// 工作助手参数的<b>唯一真源</b>：域、键、标题、类型、默认值、说明、作用域策略、读取方，都在这里声明一次。
///
/// <para>
/// 它同时是"配了没人读"的拦截面：每条参数必须声明 <see cref="AssistantParameterDescriptor.Consumers"/>
/// （读取方符号），门禁逐条断言该符号在源码里出现。**声明自己不算**——扫描时排除本文件，
/// 否则把键名列一遍就等于"有读取方"，这条断言立刻失效。
/// </para>
///
/// <para>
/// 目录与库（<c>dbo.SYSSS</c>，<c>OWNER_MODULE = 3105</c>）**按批同步生长**：一次把目标态的 106 项
/// 全部落库，会让其中绝大多数在消费改造完成前处于"配了没人读"状态。所以每批交付的是
/// "目录条目 + 库行 + 消费改造 + 门禁"四件套，本类当前只声明首批 9 项。
/// </para>
/// </summary>
public static class AssistantParameterCatalog
{
    /// <summary>助手参数的归属模块（3105 助手设置）。参数行的 <c>OWNER_MODULE</c> 就是它。</summary>
    public const int OwnerModule = EOS.API.Security.PermissionModules.AssistantAdmin.Settings;

    /// <summary>审计里的作用域令牌（<c>AUDIT_EVENT</c> 的作用域），让留痕可读。</summary>
    public const string ScopeToken = "assistant";

    /// <summary>协议类型白名单：与 <c>dbo.SYSSS.VALUE_TYPE</c> 的 CHECK 约束同源。</summary>
    public static readonly IReadOnlySet<string> ValueTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "bit", "int", "decimal", "string",
        };

    /// <summary>域定义。<c>Seq</c> 按 §5.3 一次性占号，未落库的域只是还没有参数行。</summary>
    public static IReadOnlyList<AssistantParameterGroup> Groups { get; } =
    [
        new("PROMPT", "提示词", 10),
        new("CHAT", "对话行为", 20),
        new("TOOL_LIMIT", "工具输出", 30),
        new("CAPABILITY", "能力面", 40),
        new("GOVERNANCE", "成本与熔断", 50),
        new("SITUATION", "处境", 60),
        new("DIAGNOSIS", "诊断", 70),
        new("MEMORY", "记忆", 80),
        new("KB", "知识库", 90),
    ];

    // ===== 默认值的真源：代码默认对象 =====
    // 界面上的"默认值是多少"、迁移脚本里写进 DEFAULT_VALUE 的值，都以这里为准；
    // 各写一份早晚会漂移成"界面显示 5 元、代码其实是 8 元"。

    private static readonly AssistantSettings Defaults = new();
    private static readonly AssistantCostOptions CostDefaults = new();
    private static readonly AssistantSituationBudgetOptions SituationDefaults = new();
    private static readonly AssistantDiagnosisOptions DiagnosisDefaults = new();
    private static readonly AssistantConfigWriteOptions ConfigWriteDefaults = new();
    private static readonly AssistantChatLimitsOptions ChatDefaults = new();
    private static readonly AssistantMemoryLimitsOptions MemoryDefaults = new();
    private static readonly AssistantKbLimitsOptions KbDefaults = new();
    private static readonly AssistantToolLimitsOptions ToolLimitDefaults = new();

    /// <summary>
    /// 参数清单。**声明顺序即同组内的呈现顺序**（<c>SEQ_NO</c> 按 10 递增推导），
    /// 所以插入一条参数时把它放在该组内该在的位置上，而不是追加到末尾。
    /// </summary>
    public static IReadOnlyList<AssistantParameterDescriptor> All { get; } = Build();

    /// <summary>
    /// 工具开关**不手写**：键名与工具名由 <see cref="AssistantToolKeys"/> 机械对应，
    /// 这里只把它们按工具清单展开。加一个工具却在开关清单里漏了它，
    /// 由离线门禁（扫源码里的 <c>ToolName</c> 常量）当场判红。
    /// </summary>
    private static IReadOnlyList<AssistantParameterDescriptor> Build() =>
    [
        // ---- 域 PROMPT（提示词）----
        new(
            "SYSTEM_PROMPT",
            "系统提示词",
            "PROMPT",
            "string",
            null,
            "每轮对话注入的指令。**输出格式那一段是与前端渲染面的契约**（回答按 Markdown 渲染），"
            + "改之前先看手册 60 篇。",
            Defaults.SystemPrompt,
            AssistantParameterScopePolicy.None,
            ["SystemPrompt"]),

        // ---- 域 CAPABILITY（能力面：配置写准入）----
        // 四类配置写**各自独立**、不做"一个开关放开全部"：四类的风险与验证程度不同，
        // 某一类未经验证时不该被其它类的放开带走。默认值原样保留既有口径（只有字段类是开的）。
        ConfigWriteSwitch("CONFIG_WRITE_FIELDS", "可写字段元数据", ConfigWriteDefaults.Fields,
            "字段元数据（显示名 / 可见 / 只读 / 必填等）。"),
        ConfigWriteSwitch("CONFIG_WRITE_DATASOURCES", "可写字段数据来源", ConfigWriteDefaults.DataSources,
            "字段的数据来源（FIELD_DATASOURCE：源表 / 过滤结构 / 回填映射）。默认关闭。"),
        ConfigWriteSwitch("CONFIG_WRITE_BUTTONS", "可写自定义按钮", ConfigWriteDefaults.Buttons,
            "自定义按钮（MODULE_BUSINESS_ACTION 的 MANUAL 行；**不含按钮授权**）。默认关闭。"),
        ConfigWriteSwitch("CONFIG_WRITE_EFFECTS", "可写效果键与公式", ConfigWriteDefaults.Effects,
            "效果键与公式行（MODULE_BUSINESS_ACTION(_OP) 的非 MANUAL 行）。默认关闭。"),

        // ---- 域 GOVERNANCE（成本与熔断）----
        new(
            "GLOBAL_DAILY_CAP_YUAN",
            "全局日上限",
            "GOVERNANCE",
            "decimal",
            "元",
            "全体用户当日合计上限，超过即拒绝新请求。必须大于 0——设成 0 会让所有人立刻被拒。",
            CostDefaults.GlobalDailyCapYuan.ToString("0.####"),
            AssistantParameterScopePolicy.None,
            ["GlobalDailyCapYuan"],
            Min: 0,
            MinExclusive: true),
        new(
            "USER_DAILY_CAP_YUAN",
            "每人日上限",
            "GOVERNANCE",
            "decimal",
            "元",
            "单个用户当日上限。必须大于 0。**这是唯一允许按用户放宽的参数**——给别人调高限额是管理决定，"
            + "不是越权。",
            CostDefaults.UserDailyCapYuan.ToString("0.####"),
            AssistantParameterScopePolicy.Override,
            ["UserDailyCapYuan"],
            Layers: AssistantParameterScopeLayers.User,
            Min: 0,
            MinExclusive: true),
        // 其余参数暂不声明作用域层：域 CHAT / TOOL_LIMIT / CAPABILITY / SITUATION / DIAGNOSIS 的参数
        // 随各自的消费改造落地时，按 §5.3 的"模块 · 收紧"补上 Layers: Module。
        new(
            "MAX_CONSECUTIVE_FAILURES",
            "连续失败熔断阈值",
            "GOVERNANCE",
            "int",
            "次",
            "同一用户连续技术失败达到这个次数即冷却；成功一次即清零。权限拒绝与用户取消不计入。",
            CostDefaults.MaxConsecutiveFailures.ToString(),
            AssistantParameterScopePolicy.None,
            ["MaxConsecutiveFailures"],
            Min: 1,
            Max: 100),
        new(
            "COOLDOWN_SECONDS",
            "熔断冷却",
            "GOVERNANCE",
            "int",
            "秒",
            "触发熔断后的冷却时长。",
            CostDefaults.CooldownSeconds.ToString(),
            AssistantParameterScopePolicy.None,
            ["CooldownSeconds"],
            Min: 0,
            Max: 86_400),
        new(
            "RESERVE_YUAN_PER_REQUEST",
            "每轮预留额",
            "GOVERNANCE",
            "decimal",
            "元",
            "单轮模型调用的预留额，实际预留 = 本值 ×（工具轮上限 + 1），覆盖工具多轮调用的最坏次数。"
            + "单位是**元**而不是内部记账用的微元——微元是实现单位，不该出现在管理界面上。",
            ((decimal)CostDefaults.ReserveMicroYuanPerRequest / AssistantCost.MicroYuanPerYuan).ToString("0.######"),
            AssistantParameterScopePolicy.None,
            ["ReserveMicroYuanPerRequest"],
            Min: 0),
        new(
            "INPUT_PER_MILLION_YUAN",
            "输入单价兜底",
            "GOVERNANCE",
            "decimal",
            "元/百万 token",
            "**模型行没填单价时**用它。必须大于 0——0 元会让日上限永远不触发。",
            CostDefaults.InputPerMillionYuan.ToString("0.####"),
            AssistantParameterScopePolicy.None,
            ["InputPerMillionYuan"],
            Min: 0,
            MinExclusive: true),
        new(
            "OUTPUT_PER_MILLION_YUAN",
            "输出单价兜底",
            "GOVERNANCE",
            "decimal",
            "元/百万 token",
            "同上，作用于输出 token。",
            CostDefaults.OutputPerMillionYuan.ToString("0.####"),
            AssistantParameterScopePolicy.None,
            ["OutputPerMillionYuan"],
            Min: 0,
            MinExclusive: true),

        // 可代理动作的阈值。默认值引用 AssistantActionLimits 的常量——**阈值只在那一处声明一次**
        // （AGENTS 的硬约束：业务代码不得写死同值数字）。下限一律 1：0 是"停机"语义，写在阈值上只会让动作静默失败
        IntParameter("ACTION_MAX_ROWS", "一次动作最多处理行数", "GOVERNANCE", "行",
            "一次动作请求最多处理的行数：不设上限等于允许一句「全删了」。",
            AssistantActionLimits.MaxRowsPerAction, ["MaxRowsPerAction"],
            min: AssistantActionLimits.MinimumThreshold),
        IntParameter("ACTION_MAX_APPROVAL_RECORDS", "操作请求卡最多列出的单据数", "GOVERNANCE", "张",
            "一次「操作请求卡」最多列出的单据数（只准备请求、不执行处置）。",
            AssistantActionLimits.MaxApprovalRequestRecords, ["MaxApprovalRequestRecords"],
            min: AssistantActionLimits.MinimumThreshold),
        IntParameter("ACTION_MAX_AUDIT_KEYS", "审计保留的资源键条数上限", "GOVERNANCE", "条",
            "审计里保留的资源键条数上限——审计是检索入口，不是数据出口。",
            AssistantActionLimits.MaxAuditResourceKeys, ["MaxAuditResourceKeys"],
            min: AssistantActionLimits.MinimumThreshold),
        IntParameter("ACTION_MAX_CONFIG_CLONE_OBJECTS", "照 A 配 B 最多搬运的对象数", "GOVERNANCE", "个",
            "一次「照 A 配 B」最多搬运的对象数。",
            AssistantActionLimits.MaxConfigCloneObjects, ["MaxConfigCloneObjects"],
            min: AssistantActionLimits.MinimumThreshold),

        // ---- 域 TOOL_LIMIT（工具输出上限）----
        // 每个数字有两个读取方：工具执行时截断，以及 AssistantToolRegistry 生成**发给模型的声明文本**。
        // 后者是必须的：这些上限原先也写在工具的 Description 里（"返回前 5 行""每行 8 列"），
        // 只接执行侧会让"参数改成 10 行、模型看到的说明还是 5 行"。
        IntParameter("TOOL_LIMIT_SEARCH_MAX_ROWS", "搜索工具返回的行数上限", "TOOL_LIMIT", "条",
            "search_records 一次最多返回几行；同时写进发给模型的工具说明。",
            ToolLimitDefaults.SearchMaxRows, ["SearchMaxRows"], min: 1, max: 100),
        IntParameter("TOOL_LIMIT_SEARCH_MAX_COLUMNS", "搜索工具每行的列数上限", "TOOL_LIMIT", "列",
            "search_records 每行最多带几列。",
            ToolLimitDefaults.SearchMaxColumns, ["SearchMaxColumns"], min: 1, max: 100),
        IntParameter("TOOL_LIMIT_SEARCH_MAX_VALUE_LENGTH", "搜索工具单值字符上限", "TOOL_LIMIT", "字符",
            "search_records 每个字段值截断到多少字符（列表视图的语义，不是详情）。",
            ToolLimitDefaults.SearchMaxValueLength, ["SearchMaxValueLength"], min: 1, max: 1_000),
        IntParameter("TOOL_LIMIT_DETAIL_MAX_COLUMNS", "明细工具每行列数上限", "TOOL_LIMIT", "列",
            "get_record_detail 单行最多带几列。",
            ToolLimitDefaults.DetailMaxColumns, ["DetailMaxColumns"], min: 1, max: 200),
        IntParameter("TOOL_LIMIT_DETAIL_MAX_VALUE_LENGTH", "明细工具单值字符上限", "TOOL_LIMIT", "字符",
            "get_record_detail 每个字段值截断到多少字符。",
            ToolLimitDefaults.DetailMaxValueLength, ["DetailMaxValueLength"], min: 1, max: 5_000),
        IntParameter("TOOL_LIMIT_DRAFT_MAX_VALUE_LENGTH", "试算工具单值字符上限", "TOOL_LIMIT", "字符",
            "draft_record 试算时单个字段值截断到多少字符；截断会随试算结果一起告警。",
            ToolLimitDefaults.DraftMaxValueLength, ["DraftMaxValueLength"], min: 1, max: 20_000),
        IntParameter("TOOL_LIMIT_DESCRIBE_MAX_FIELDS", "模块描述每表字段数上限", "TOOL_LIMIT", "个",
            "describe_module 每张表最多列出几个字段。",
            ToolLimitDefaults.DescribeMaxFields, ["DescribeMaxFields"], min: 1, max: 500),
        IntParameter("TOOL_LIMIT_LIST_MODULES_MAX", "模块列表条数上限", "TOOL_LIMIT", "个",
            "list_modules 一次最多返回几个模块。",
            ToolLimitDefaults.ListModulesMax, ["ListModulesMax"], min: 1, max: 500),
        IntParameter("TOOL_LIMIT_LIST_CAPABILITIES_MAX", "我的能力列表条数上限", "TOOL_LIMIT", "个",
            "list_my_capabilities 一次最多返回几个模块。",
            ToolLimitDefaults.ListCapabilitiesMax, ["ListCapabilitiesMax"], min: 1, max: 500),
        IntParameter("TOOL_LIMIT_FIELD_RELATIONS_MAX", "字段关系条数上限", "TOOL_LIMIT", "条",
            "get_field_relations 一次最多返回几条关系。",
            ToolLimitDefaults.FieldRelationsMax, ["FieldRelationsMax"], min: 1, max: 500),
        IntParameter("TOOL_LIMIT_REPORT_LIST_MAX", "模块报表清单条数上限", "TOOL_LIMIT", "个",
            "list_reports 一次最多列出几个报表。",
            ToolLimitDefaults.ReportListMax, ["ReportListMax"], min: 1, max: 200),
        IntParameter("TOOL_LIMIT_REPORT_MAX_ROWS", "报表取数行数上限", "TOOL_LIMIT", "行",
            "run_report 一次最多带回几行；同时写进发给模型的工具说明。",
            ToolLimitDefaults.ReportMaxRows, ["ReportMaxRows"], min: 1, max: 200),
        IntParameter("TOOL_LIMIT_REPORT_MAX_COLUMNS", "报表取数列数上限", "TOOL_LIMIT", "列",
            "run_report 每行最多带几列。",
            ToolLimitDefaults.ReportMaxColumns, ["ReportMaxColumns"], min: 1, max: 100),
        IntParameter("TOOL_LIMIT_REPORT_MAX_VALUE_LENGTH", "报表取数单值字符上限", "TOOL_LIMIT", "字符",
            "run_report 每个单元格截断到多少字符。",
            ToolLimitDefaults.ReportMaxValueLength, ["ReportMaxValueLength"], min: 1, max: 1_000),
        IntParameter("TOOL_LIMIT_RECORD_HISTORY_MAX", "单据历史条数上限", "TOOL_LIMIT", "条",
            "get_record_history 的审批历史与最近操作各最多带回几条；同时写进发给模型的工具说明。",
            ToolLimitDefaults.RecordHistoryMax, ["RecordHistoryMax"], min: 1, max: 200),
        IntParameter("TOOL_LIMIT_RECORD_ACTIVITY_DAYS", "单据历史的时间窗", "TOOL_LIMIT", "天",
            "get_record_history 只看最近多少天的操作记录（窗口越大越慢、越贵）。",
            ToolLimitDefaults.RecordActivityDays, ["RecordActivityDays"], min: 1, max: 3_650),

        // ---- 域 CHAT（对话行为）----
        // 默认值一律取自 AssistantChatLimitsOptions 的属性初始值（ChatDefaults），不写第二遍数字
        IntParameter("CHAT_MAX_HISTORY_MESSAGES", "读入的历史消息条数上限", "CHAT", "条",
            "一次请求读入多少条历史消息。上限不是裁剪本身——真正的裁剪按当前模型的上下文窗口算。",
            ChatDefaults.MaxHistoryMessages, ["MaxHistoryMessages"], min: 1, max: 500),
        IntParameter("CHAT_CONTEXT_RESERVE_TOKENS", "上下文预留 token", "CHAT", "token",
            "留给系统提示、记忆、处境段与工具结果的余量；它越小，带的历史越多，但被厂商拒绝的风险越大。",
            ChatDefaults.ContextReserveTokens, ["ContextReserveTokens"], min: 256, max: 262_144),
        IntParameter("CHAT_DEFAULT_CONTEXT_WINDOW", "默认上下文窗口", "CHAT", "token",
            "模型行没填上下文窗口时的保守默认。宁可少带历史，也不要因为算大了被拒。",
            ChatDefaults.DefaultContextWindow, ["DefaultContextWindow"], min: 1_024, max: 2_097_152),
        IntParameter("CHAT_MAX_CONTENT_LENGTH", "单条消息字符上限", "CHAT", "字符",
            "用户单条消息的长度上限；控制器入参校验与对话编排用的是同一个值。",
            ChatDefaults.MaxContentLength, ["MaxContentLength"], min: 100, max: 100_000),
        IntParameter("CHAT_MAX_TOOL_ARGUMENTS_LENGTH", "工具参数 JSON 字符上限", "CHAT", "字符",
            "模型输出的工具参数长度上限，防止把一次调用撑成一段文本。",
            ChatDefaults.MaxToolArgumentsLength, ["MaxToolArgumentsLength"], min: 100, max: 100_000),
        IntParameter("CHAT_MAX_TOOL_ROUNDS", "工具轮数上限", "CHAT", "轮",
            "一轮回答里最多做几次工具调用。它同时决定成本预留倍率（预留 = 单次预留 ×（轮数 + 1）），"
            + "所以调大它不只是慢一点，还会按倍数占用额度。",
            ChatDefaults.MaxToolRounds, ["MaxToolRounds"], min: 0, max: 20),
        IntParameter("CHAT_TOOL_DIGEST_LENGTH", "工具结果摘要字符上限", "CHAT", "字符",
            "落库 TOOL_CALLS_JSON 时工具结果摘要保留多少字符（完整结果不落库）。",
            ChatDefaults.ToolDigestLength, ["ToolDigestLength"], min: 20, max: 2_000),
        IntParameter("CHAT_AUDIT_ARGUMENT_LENGTH", "审计参数片段字符上限", "CHAT", "字符",
            "审计里工具参数片段保留多少字符。",
            ChatDefaults.AuditArgumentLength, ["AuditArgumentLength"], min: 20, max: 2_000),

        // ---- 域 SITUATION（处境）----
        // 默认值一律取自 AssistantSituationBudgetOptions 的属性初始值（下面的 SituationDefaults）——
        // 那是"代码默认值"的唯一一份，目录只引用它，不写第二遍数字
        IntParameter("SIT_RESIDENT_TOKEN_LIMIT", "常驻处境合计预算", "SITUATION", "token",
            "常驻处境（身份 + 待办）合计的 token 预算，超限按上限硬截断并记 Warning。",
            SituationDefaults.ResidentTokenLimit, ["ResidentTokenLimit"]),
        IntParameter("SIT_IDENTITY_TOKEN_LIMIT", "身份段预算", "SITUATION", "token",
            "身份段（我是谁、能看什么）的 token 预算。",
            SituationDefaults.IdentityTokenLimit, ["IdentityTokenLimit"]),
        IntParameter("SIT_PENDING_TOKEN_LIMIT", "待办段预算", "SITUATION", "token",
            "待办段（我手上压着什么）的 token 预算。",
            SituationDefaults.PendingTokenLimit, ["PendingTokenLimit"]),
        IntParameter("SIT_MAX_FILTERS", "上报筛选条件条数上限", "SITUATION", "条",
            "前端上报的筛选条件条数上限，超限截断并记 Warning。",
            SituationDefaults.MaxFilters, ["MaxFilters"]),
        IntParameter("SIT_MAX_SELECTION", "上报选中行条数上限", "SITUATION", "条",
            "前端上报的选中行主键条数上限。",
            SituationDefaults.MaxSelection, ["MaxSelection"]),
        IntParameter("SIT_MAX_DIRTY_FIELDS", "上报脏字段条数上限", "SITUATION", "条",
            "前端上报的未保存字段条数上限。",
            SituationDefaults.MaxDirtyFields, ["MaxDirtyFields"]),
        IntParameter("SIT_MAX_VALUE_LENGTH", "上报单个值的字符上限", "SITUATION", "字符",
            "上报文本（筛选值、主键、字段值）的字符上限。",
            SituationDefaults.MaxValueLength, ["MaxValueLength"]),
        IntParameter("SIT_MAX_NOTICE_SUMMARY_LENGTH", "上报拒绝摘要的字符上限", "SITUATION", "字符",
            "前端上报的被拒摘要的字符上限。",
            SituationDefaults.MaxNoticeSummaryLength, ["MaxNoticeSummaryLength"]),
        IntParameter("SIT_OVERDUE_DAYS", "单据滞留判定天数", "SITUATION", "天",
            "建立日期早于该天数仍未批核即视为滞留。",
            SituationDefaults.OverdueDays, ["OverdueDays"]),
        IntParameter("SIT_OVERDUE_MAX_AGE_DAYS", "滞留取数的年龄上界", "SITUATION", "天",
            "超过该年龄的历史单据不进摘要——没有上界时按默认排序会取到多年前的遗留单。",
            SituationDefaults.OverdueMaxAgeDays, ["OverdueMaxAgeDays"]),
        IntParameter("SIT_DIGEST_MAX_ITEMS", "打开即见摘要的最大条目数", "SITUATION", "条",
            "打开助手即见（零模型调用）的摘要最多列几条。",
            SituationDefaults.DigestMaxItems, ["DigestMaxItems"]),
        IntParameter("SIT_BLOCKED_NOW_SCAN_RECORDS", "「此刻办不下去」每模块扫描记录数", "SITUATION", "条",
            "逐单扫描的每模块记录数上限（按建立日期倒序取最近的），与年龄上界一起把这条链路的总代价限住。",
            SituationDefaults.BlockedNowScanRecords, ["BlockedNowScanRecords"]),
        IntParameter("SIT_BLOCKED_NOW_MAX_AGE_DAYS", "「此刻办不下去」扫描的年龄上界", "SITUATION", "天",
            "只看近期单据——既是「此刻」的语义，也把代价限住。",
            SituationDefaults.BlockedNowMaxAgeDays, ["BlockedNowMaxAgeDays"]),
        IntParameter("SIT_BLOCKED_NOW_PROBE_RULES", "「此刻办不下去」每模块判定的校验判据条数", "SITUATION", "条",
            "逐单求值的成本上界。",
            SituationDefaults.BlockedNowProbeRules, ["BlockedNowProbeRules"]),
        IntParameter("SIT_DIGEST_MODULE_SCAN_LIMIT", "摘要扫描的候选模块数上限", "SITUATION", "个",
            "按本人最近活动取候选模块的个数上限。",
            SituationDefaults.DigestModuleScanLimit, ["DigestModuleScanLimit"]),
        IntParameter("SIT_ACTIVITY_WINDOW_DAYS", "本人最近活动的时间窗", "SITUATION", "天",
            "取「本人最近活动模块」的时间窗。",
            SituationDefaults.ActivityWindowDays, ["ActivityWindowDays"]),
        IntParameter("SIT_RECENT_FAILURE_DAYS", "最近被拒事件的时间窗", "SITUATION", "天",
            "取「最近被拒事件」的时间窗。",
            SituationDefaults.RecentFailureDays, ["RecentFailureDays"]),
        IntParameter("SIT_RECENT_FAILURE_LIMIT", "最近被拒事件条数上限", "SITUATION", "条",
            "取「最近被拒事件」的条数上限。",
            SituationDefaults.RecentFailureLimit, ["RecentFailureLimit"]),
        IntParameter("SIT_DIGEST_TEXT_LENGTH", "摘要条目文案的字符上限", "SITUATION", "字符",
            "摘要条目文案的字符上限。",
            SituationDefaults.DigestTextLength, ["DigestTextLength"]),

        // ---- 域 DIAGNOSIS（诊断）----
        IntParameter("DIAG_MAX_VALIDATION_RULES", "列出的校验规则条数上限", "DIAGNOSIS", "条",
            "诊断里列出的校验规则条数上限，超限截断并记入 caveat。",
            DiagnosisDefaults.MaxValidationRules, ["MaxValidationRules"]),
        IntParameter("DIAG_MAX_FIELD_GUARDS", "列出的字段保护条数上限", "DIAGNOSIS", "条",
            "诊断里列出的字段保护条数上限。",
            DiagnosisDefaults.MaxFieldGuards, ["MaxFieldGuards"]),
        IntParameter("DIAG_MAX_PROVENANCE", "列出的值来源条数上限", "DIAGNOSIS", "条",
            "诊断里列出的值来源（这个字段的值是怎么来的）条数上限。",
            DiagnosisDefaults.MaxProvenance, ["MaxProvenance"]),
        IntParameter("DIAG_MAX_EFFECTS", "列出的效果影响面条数上限", "DIAGNOSIS", "条",
            "诊断里列出的效果影响面条数上限。",
            DiagnosisDefaults.MaxEffects, ["MaxEffects"]),
        IntParameter("DIAG_MAX_BLOCKERS", "列出的阻塞原因条数上限", "DIAGNOSIS", "条",
            "诊断里列出的阻塞原因条数上限。",
            DiagnosisDefaults.MaxBlockers, ["MaxBlockers"]),
        IntParameter("DIAG_LAST_FAILURE_DAYS", "取最近一次失败的时间窗", "DIAGNOSIS", "天",
            "取「最近一次对该记录的失败」的时间窗。",
            DiagnosisDefaults.LastFailureDays, ["LastFailureDays"]),
        IntParameter("DIAG_LAST_FAILURE_LIMIT", "取最近一次失败的条数上限", "DIAGNOSIS", "条",
            "只取最新的一条附上。",
            DiagnosisDefaults.LastFailureLimit, ["LastFailureLimit"]),
        IntParameter("DIAG_MAX_TEXT_LENGTH", "单条原因文案的字符上限", "DIAGNOSIS", "字符",
            "单条原因 / 文案的字符上限。",
            DiagnosisDefaults.MaxTextLength, ["MaxTextLength"]),
        IntParameter("DIAG_ACTION_SUMMARY_LIMIT", "归因文案条数上限", "DIAGNOSIS", "条",
            "「为什么办不下去」的归因条数上限。它原先写死在代码里——一个配置入口都没有。",
            DiagnosisDefaults.MaxActionSummary, ["MaxActionSummary"]),

        // ---- 域 MEMORY（记忆）----
        new(
            "MEM_ENABLE_AUTO_DISTILL",
            "会话结束自动提炼记忆",
            "MEMORY",
            "bit",
            null,
            "done 之后异步提炼候选记忆（待用户确认）。关掉可以省一次模型调用。",
            Defaults.EnableAutoDistill ? "1" : "0",
            AssistantParameterScopePolicy.None,
            ["EnableAutoDistill"]),
        IntParameter("MEM_MAX_CANDIDATES", "一次提炼的候选条数上限", "MEMORY", "条",
            "一次自动提炼最多产出几条候选记忆（都要用户确认才生效）。",
            MemoryDefaults.MaxCandidates, ["MaxCandidates"], min: 1, max: 50),
        IntParameter("MEM_SUGGEST_THRESHOLD", "候选记忆自动转正的分数门槛", "MEMORY", "分",
            "达到此分的候选记忆**自动转正**（直接生效并覆盖同名记忆），低于此分维持待用户确认。"
            + "注意这不是提示文案上的数字：调低它等于放宽「什么会被自动记住」这件事。"
            + "**必须大于保留门槛**，否则候选要么全留、要么全丢——两者不成立时本轮退回默认值并在页面上示警。",
            MemoryDefaults.SuggestThreshold, ["SuggestThreshold"], min: 0, max: 100),
        IntParameter("MEM_KEEP_THRESHOLD", "丢弃候选的分数门槛", "MEMORY", "分",
            "低于此分直接丢弃，连候选都不进。与建议门槛成对调整。",
            MemoryDefaults.KeepThreshold, ["KeepThreshold"], min: 0, max: 100),
        IntParameter("MEM_DISTILL_TURNS", "提炼时取最近几轮对话", "MEMORY", "轮",
            "提炼只看最近这几轮——取全量会把同一件事反复记成多条。",
            MemoryDefaults.DistillTurns, ["DistillTurns"], min: 1, max: 100),
        IntParameter("MEM_MAX_PER_USER", "每人记忆条数上限", "MEMORY", "条",
            "超限时按最久未访问归档一条，而不是拒绝写入。",
            MemoryDefaults.MaxPerUser, ["MaxPerUser"], min: 1, max: 10_000),
        IntParameter("MEM_MAX_KEY_LENGTH", "记忆标题字符上限", "MEMORY", "字符",
            "记忆标题的长度上限。",
            MemoryDefaults.MaxKeyLength, ["MaxKeyLength"], min: 1, max: 2_000),
        IntParameter("MEM_MAX_VALUE_LENGTH", "记忆内容字符上限", "MEMORY", "字符",
            "记忆内容的长度上限。",
            MemoryDefaults.MaxValueLength, ["MaxValueLength"], min: 1, max: 20_000),
        IntParameter("MEM_MAX_PREFERENCES_LENGTH", "偏好内容字符上限", "MEMORY", "字符",
            "派生画像（偏好）的长度上限。",
            MemoryDefaults.MaxPreferencesLength, ["MaxPreferencesLength"], min: 1, max: 40_000),
        IntParameter("MEM_INJECTION_TOP_K", "每轮注入的记忆条数", "MEMORY", "条",
            "每轮对话注入提示词的记忆条数上限。调大不只是多花 token，还会挤占历史与工具结果的预算。",
            MemoryDefaults.InjectionTopK, ["InjectionTopK"], min: 0, max: 50),

        // ---- 域 KB（知识库）----
        // 检索侧 2 项 + 入库切块 2 项。切块参数不是实现细节：块长直接决定"检索回来的片段够不够
        // 回答一个问题"，而它与文档体裁有关；但重叠必须小于块长，否则切块原地打转。
        IntParameter("KB_SEARCH_MAX_HITS", "一次检索的命中条数上限", "KB", "条",
            "一次知识库检索最多返回几条命中。",
            KbDefaults.SearchMaxHits, ["SearchMaxHits"], min: 1, max: 50),
        IntParameter("KB_SEARCH_MAX_CONTENT_LENGTH", "命中片段的字符上限", "KB", "字符",
            "命中片段进模型前截断到多少字符。",
            KbDefaults.SearchMaxContentLength, ["SearchMaxContentLength"], min: 1, max: 10_000),
        IntParameter("KB_CHUNK_MAX_CHARS", "入库切块的块长", "KB", "字符",
            "文档入库时的切块长度。**必须大于重叠长度**，否则切块原地打转——不成立时本轮退回默认值。",
            KbDefaults.ChunkMaxChars, ["ChunkMaxChars"], min: 1, max: 10_000),
        IntParameter("KB_CHUNK_OVERLAP_CHARS", "相邻块的重叠长度", "KB", "字符",
            "相邻块的重叠字符数，保证跨块句子不被切断。必须小于块长。",
            KbDefaults.ChunkOverlapChars, ["ChunkOverlapChars"], min: 0, max: 5_000),
        IntParameter("KB_ENDPOINT_MAX_HITS", "检索端点的条数上界", "KB", "条",
            "REST 检索端点（POST /api/v1/assistant/kb/search）一次最多返回几条：调用方传的 TopK 会被夹在"
            + " 1 与本值之间，响应里回显实际生效的条数。"
            + "**与「一次检索的命中条数上限」分开**：那个管进模型上下文几条，这个管界面能翻出几条——"
            + "合成一个的话，为省 token 调小工具上限会把检索页一起缩水。",
            KbDefaults.EndpointMaxHits, ["EndpointMaxHits"], min: 1, max: 200),

        // ---- 域 CAPABILITY（能力面：工具开关）----
        // 键名 = TOOL_ + 工具名转大写；默认全开（关掉才落非默认值）。
        // 这里排除"批后追加"的工具：它们排在动作族之后（见下），否则会把动作族的序号推后。
        .. AssistantToolKeys.ToolNames
            .Where(name => !AssistantToolKeys.AppendedToolNames.Contains(name, StringComparer.Ordinal))
            .Select(ToolSwitch),

        // ---- 域 CAPABILITY（能力面：记录动作族开关）----
        // 追加在组内**末尾**：组内序号由声明顺序推导（SEQ_NO 按 10 递增），插在中间会让
        // 已在库里的行与目录对不上（连库门禁逐字段比对序号）。**新条目一律追加，不改已有顺序。**
        //
        // 这三个是本目录里**唯一声明了模块层**的参数：消费点（AssistantActionGate）本来就带着
        // "哪个模块"，所以"某模块不许助手删除"是一条能真正生效的配置，而不是一个没人读的声明。
        // 策略是**收紧**：只能关、不能开——全局已关的动作，任何模块都不许放开。
        .. AssistantActionKeys.ActionNames.Select(ActionSwitch),

        // ---- 域 CAPABILITY（能力面：批后新增的工具开关）----
        // 排在组末，理由见 AssistantToolKeys.AppendedToolNames：它们是动作族之后才加的工具，
        // 插回工具清单中间会让动作族及其后所有参数的序号整体后移。
        .. AssistantToolKeys.AppendedToolNames.Select(ToolSwitch),
    ];

    /// <summary>
    /// 一个记录动作族的开关。**读取方是 <c>AssistantActionGate</c>**——预演与执行都从那里过，
    /// 所以"关掉了"意味着这个动作在助手侧根本走不下去（不是靠模型自觉不调）。
    /// </summary>
    private static AssistantParameterDescriptor ActionSwitch(string actionName) => new(
        AssistantActionKeys.ParameterKeyOf(actionName),
        $"助手可执行「{actionName}」",
        "CAPABILITY",
        "bit",
        null,
        $"关掉之后助手不能对记录做 {actionName} 操作（预演与执行都在门禁处被拒）；"
        + "只能越关越少——全局关掉的动作，任何模块都不许单独打开。",
        AssistantActionKeys.EnabledByDefault ? "1" : "0",
        AssistantParameterScopePolicy.Tighten,
        ["AssistantActionGate"],
        Layers: AssistantParameterScopeLayers.Module);

    /// <summary>
    /// 一个工具的开关。**读取方就是工具名本身**——工具名以字符串常量写在工具类里
    /// （<c>ToolName = "search_records"</c>），所以"这个开关有人读"是可证的。
    ///
    /// <para>
    /// 标题取 <see cref="AssistantToolKeys.LabelOf"/> 的中文名（管理员读的），说明里带上工具名
    /// （查问题、对日志时要用）。标题与工具清单的一一对应由离线门禁断言。
    /// </para>
    /// </summary>
    private static AssistantParameterDescriptor ToolSwitch(string toolName) => new(
        AssistantToolKeys.ParameterKeyOf(toolName),
        AssistantToolKeys.LabelOf(toolName),
        "CAPABILITY",
        "bit",
        null,
        $"关掉之后模型看不到「{AssistantToolKeys.LabelOf(toolName)}」（{toolName}）这个工具，也调不动它"
        + "（幻觉出一个已关闭的工具名会被明确拒绝，而不是当成工具不存在）。",
        "1",
        AssistantParameterScopePolicy.None,
        [toolName]);

    /// <summary>
    /// 整型参数（含取值范围）的声明写法。处境与诊断两域有 28 条形状完全相同的条目，
    /// 逐条展开成 8 行的构造调用只会把"值"淹在样板里；键 / 标题 / 组 / 单位 / 说明 / 默认值 / 读取方
    /// 仍然是逐条写明的，没有省略任何判断。
    ///
    /// <para>
    /// **作用域暂不声明**（`None`）：这些参数此刻只有"全局"这一层被接线。声明一个还没接线的层，
    /// 正是这套框架要根除的"配了没人读"——模块层随各自的按模块解析接入后再补 `Layers`。
    /// </para>
    /// </summary>
    private static AssistantParameterDescriptor IntParameter(
        string key, string displayName, string group, string? unit, string description,
        int defaultValue, IReadOnlyList<string> consumers, int min = 0, int? max = null) =>
        new(
            key,
            displayName,
            group,
            "int",
            unit,
            description,
            defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AssistantParameterScopePolicy.None,
            consumers,
            Min: min,
            Max: max);

    /// <summary>配置写准入开关（域 `CAPABILITY`）：默认值与"哪几类开放"由调用处写明。</summary>
    private static AssistantParameterDescriptor ConfigWriteSwitch(
        string key, string displayName, bool defaultValue, string description) =>
        new(
            key,
            displayName,
            "CAPABILITY",
            "bit",
            null,
            description,
            defaultValue ? "1" : "0",
            AssistantParameterScopePolicy.None,
            ["IsEnabled"]);

    /// <summary>这条参数是否允许出现在该层（<paramref name="scopeType"/> 取 MODULE / USER）。</summary>
    public static bool AllowsLayer(AssistantParameterDescriptor descriptor, string scopeType)
    {
        var layer = string.Equals(scopeType, AssistantParameterScopeRules.Module, StringComparison.OrdinalIgnoreCase)
            ? AssistantParameterScopeLayers.Module
            : string.Equals(scopeType, AssistantParameterScopeRules.User, StringComparison.OrdinalIgnoreCase)
                ? AssistantParameterScopeLayers.User
                : AssistantParameterScopeLayers.None;
        return layer != AssistantParameterScopeLayers.None && descriptor.Layers.HasFlag(layer);
    }

    /// <summary>
    /// 字符串型参数的默认值**不写进 <c>DEFAULT_VALUE</c> 列**，留在代码里（§6.3）。
    ///
    /// <para>
    /// 理由是它属于"随代码演进的文本"而非"运营要校准的数字"：写进列里只会多出一份需要人工同步的长文本，
    /// 而它一旦跟不上代码，界面给出的默认值就是错的。标量默认值（金额、次数、开关）才写进列里——
    /// 那里它们确实是可校准的数据。
    /// </para>
    /// </summary>
    public static bool WritesColumnDefault(AssistantParameterDescriptor descriptor) =>
        !string.Equals(descriptor.ValueType, "string", StringComparison.Ordinal);

    /// <summary>
    /// 红线键：这些名字**永远不是合法参数**，出现在参数表 / 作用域表 / 配置里即启动失败。
    ///
    /// <para>
    /// 它们没有"可写参数"的形态——预演与幂等恒为开、越权上限恒为 0、批核族在助手侧没有注册项。
    /// 登记在这里只为让门禁能机械识别一次覆盖尝试；写成同义拼法也算（那多半是想绕过）。
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> RedLineKeys { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 四条红线及其同义拼法
            "DRYRUNREQUIRED", "DRY_RUN_REQUIRED", "DRYRUNENABLED", "DRY_RUN_ENABLED",
            "IDEMPOTENCYREQUIRED", "IDEMPOTENCY_REQUIRED", "IDEMPOTENCYENABLED", "IDEMPOTENCY_ENABLED",
            "MAXUNAUTHORIZEDACTIONS", "MAX_UNAUTHORIZED_ACTIONS",
            "UNAUTHORIZEDACTIONLIMIT", "UNAUTHORIZED_ACTION_LIMIT",
            "APPROVALFAMILYACTIONCOUNT", "APPROVAL_FAMILY_ACTION_COUNT",
            "APPROVALFAMILYLIMIT", "APPROVAL_FAMILY_LIMIT", "APPROVAL_FAMILY_ENABLED",
            // 批核族与权限授予类**在助手侧没有注册项**，因此也不可能有开关；出现即越界
            "TOOL_APPROVE", "TOOL_DEAPPROVE", "TOOL_END_CASE", "TOOL_UNEND_CASE",
            "TOOL_RIGHTS_ADMIN", "TOOL_USER_ADMIN", "TOOL_MENU_ADMIN",
        };

    /// <summary>疑似凭据的键名：参数类型里没有任何一种能表达凭据，出现即失败。</summary>
    public static IReadOnlySet<string> CredentialKeys { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "API_KEY", "APIKEY", "SECRET", "TOKEN", "PASSWORD", "PWD", "CREDENTIAL",
        };

    /// <summary>按 Key 找元数据；找不到返回 null（库里出现了界面不认识的键）。</summary>
    public static AssistantParameterDescriptor? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(item => string.Equals(item.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>域定义；未知域返回 null。</summary>
    public static AssistantParameterGroup? FindGroup(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : Groups.FirstOrDefault(item => string.Equals(item.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 组内序号（<c>SEQ_NO</c>，按 10 递增）。由**声明顺序**推导——这样"排序"只有一处需要维护，
    /// 不会出现"目录里的顺序"与"库里的序号"两套。
    /// </summary>
    public static int SeqNoOf(AssistantParameterDescriptor descriptor)
    {
        var index = 0;
        foreach (var item in All)
        {
            if (!string.Equals(item.Group, descriptor.Group, StringComparison.Ordinal)) continue;
            index++;
            if (ReferenceEquals(item, descriptor)) return index * 10;
        }

        return 0;
    }

    /// <summary>
    /// 把界面/库里拿到的文本规范成**可存储形式**，并做类型与取值范围的校验。
    ///
    /// <para>
    /// 空值按"未设置"处理（等于用默认值）——界面上清空输入就是恢复默认，比报一句"不能为空"更实用。
    /// </para>
    ///
    /// <para>
    /// **保存与生效共用这一个解析器**（保存走本方法，生效走 <c>AssistantParameterResolver</c> 里的
    /// 同名调用）：各写一份，"保存时通过、生效时被忽略"就是必然结果。
    /// </para>
    /// </summary>
    public static bool TryNormalize(
        AssistantParameterDescriptor descriptor, string? raw, out string? normalized, out string problem)
    {
        normalized = null;
        problem = string.Empty;

        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return true;

        switch (descriptor.ValueType)
        {
            case "bit":
                if (text is "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "是")
                {
                    normalized = "1";
                    return true;
                }

                if (text is "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "否")
                {
                    normalized = "0";
                    return true;
                }

                problem = $"“{text}”不是开关值（应为 是 / 否）";
                return false;

            case "int":
                if (!int.TryParse(text, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var number))
                {
                    problem = $"“{text}”不是整数";
                    return false;
                }

                if (!InRange(number, descriptor, out problem)) return false;
                normalized = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;

            case "decimal":
                if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var amount))
                {
                    problem = $"“{text}”不是数值";
                    return false;
                }

                if (!InRange(amount, descriptor, out problem)) return false;
                normalized = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;

            case "string":
                var limit = descriptor.MaxLength ?? 4000;
                if (text.Length > limit)
                {
                    problem = $"长度超出上限（{limit} 字符，当前 {text.Length}）";
                    return false;
                }

                normalized = text;
                return true;

            default:
                problem = $"未知的类型 {descriptor.ValueType}";
                return false;
        }
    }

    /// <summary>按类型给出"这个值长什么样才对"，用于界面提示。</summary>
    public static string DescribeRange(AssistantParameterDescriptor descriptor)
    {
        var bounds = new List<string>();
        if (descriptor.Min is { } min)
        {
            bounds.Add(descriptor.MinExclusive ? $"大于 {min:0.####}" : $"不小于 {min:0.####}");
        }

        if (descriptor.Max is { } max) bounds.Add($"不大于 {max:0.####}");

        return bounds.Count == 0 ? string.Empty : string.Join("，", bounds);
    }

    private static bool InRange(decimal value, AssistantParameterDescriptor descriptor, out string problem)
    {
        problem = string.Empty;
        if (descriptor.Min is { } min && (descriptor.MinExclusive ? value <= min : value < min))
        {
            problem = descriptor.MinExclusive ? $"必须大于 {min:0.####}" : $"不能小于 {min:0.####}";
            return false;
        }

        if (descriptor.Max is { } max && value > max)
        {
            problem = $"不能大于 {max:0.####}";
            return false;
        }

        return true;
    }
}
