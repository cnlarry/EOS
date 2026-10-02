using EOS.API.Data;
using EOS.API.Features.Assistant.Config;
using EOS.API.Features.Assistant.Diagnosis;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Situation;

namespace EOS.API.Features.Assistant.Parameters;

/// <summary>
/// 解析出来的**助手生效参数**（强类型），以及这批解析里没能生效的项。
///
/// <para>
/// 除了提示词、成本与熔断，它还把**处境预算、诊断预算、动作阈值、配置写准入**一并带着走——
/// 它们此前各自绑在一个 <c>IConfiguration</c> 节上（改一次要改文件加重启，而 <c>appsettings.json</c>
/// 里其实根本没有这些节），现在与其它参数同源：声明在目录、取值在库里、解释在这一处。
/// </para>
///
/// <para>
/// <c>Problems</c> 不是装饰：库里写了一个解析不了的值时，如果只是悄悄用默认值，
/// 表现是"界面显示 5 元、实际按默认值跑"，而没有任何线索。它由调用方记进日志、
/// 并随 3105 页面一起展示。
/// </para>
/// </summary>
public sealed record AssistantPolicyValues(
    string SystemPrompt,
    bool EnableAutoDistill,
    AssistantCostOptions Cost,
    IReadOnlyList<string> Problems)
{
    /// <summary>代码默认值：尚未解析任何参数时的初始状态（也是解析的起点）。</summary>
    public static AssistantPolicyValues Default { get; } = CreateDefault();

    /// <summary>处境上下文的预算与上限。</summary>
    public AssistantSituationBudgetOptions Situation { get; init; } = new();

    /// <summary>对象级诊断的输出上限。</summary>
    public AssistantDiagnosisOptions Diagnosis { get; init; } = new();

    /// <summary>可代理动作的阈值（行数 / 请求卡 / 审计键 / 克隆对象）。</summary>
    public AssistantActionLimitsOptions ActionLimits { get; init; } = new();

    /// <summary>配置写准入（字段 / 数据源 / 按钮 / 效果键各自独立）。</summary>
    public AssistantConfigWriteOptions ConfigWrite { get; init; } = new();

    /// <summary>能力面：哪些工具被关掉了（工具开关的域，见 <see cref="AssistantToolKeys"/>）。</summary>
    public AssistantCapabilityOptions Capability { get; init; } = new();

    /// <summary>对话行为的上限与预算（条数 / token / 截断长度 / 工具轮数）。</summary>
    public AssistantChatLimitsOptions Chat { get; init; } = new();

    /// <summary>记忆的阈值与长度上限。</summary>
    public AssistantMemoryLimitsOptions Memory { get; init; } = new();

    /// <summary>知识库的检索与切块上限。</summary>
    public AssistantKbLimitsOptions Kb { get; init; } = new();

    /// <summary>工具输出上限（7 个读工具的行数 / 列数 / 截断长度）。</summary>
    public AssistantToolLimitsOptions ToolLimits { get; init; } = new();

    private static AssistantPolicyValues CreateDefault()
    {
        var settings = new AssistantSettings();
        return new AssistantPolicyValues(
            settings.SystemPrompt, settings.EnableAutoDistill, new AssistantCostOptions(), []);
    }
}

/// <summary>
/// 一次解析的**完整结果**：原始参数行 + 解释出来的值。
///
/// <para>
/// 为什么要连原始行一起带着走：作用域覆盖（ADR-030 §6.2）的做法是"把覆盖压到全局行上，
/// 再交给同一个 <see cref="AssistantParameterResolver.Interpret"/> 解释"。
/// 只留解释后的值就没法叠加了——所以行集合要随快照一起留着，而不是解析完就丢。
/// </para>
/// </summary>
public sealed record AssistantParameterSet(
    IReadOnlyList<SystemParameterItem> Rows,
    AssistantPolicyValues Values)
{
    public static AssistantParameterSet Empty { get; } = new([], AssistantPolicyValues.Default);
}

/// <summary>
/// 把 <c>dbo.SYSSS</c> 里 <c>OWNER_MODULE = 3105</c> 的参数行解析成强类型策略对象。
///
/// <para>
/// 解析口径只有一条：**<c>PARAM_VALUE</c> 为空 = 用 <c>DEFAULT_VALUE</c>；再为空 = 用代码默认值**。
/// 第三步是"字符串型参数的默认值留在代码里"（<see cref="AssistantParameterCatalog.WritesColumnDefault"/>）
/// 的自然结果，也是"恢复默认"只需清空取值、不必删行的实现依据。
/// </para>
///
/// <para>
/// 注册为**单例**（运行期快照的构建者活在请求作用域之外），所以参数行经
/// <see cref="SystemParameterService.LoadItemsAsync"/> 这个静态入口读取——与页面读的是同一段 SQL。
/// </para>
/// </summary>
public sealed class AssistantParameterResolver(
    DbConnectionFactory connections,
    ILogger<AssistantParameterResolver> logger)
{
    /// <summary>读取并解析全部已落库的助手参数，连同**原始行**一起返回。</summary>
    public async Task<AssistantParameterSet> ResolveAsync(CancellationToken token)
    {
        var rows = await SystemParameterService.LoadItemsAsync(
            connections, AssistantParameterCatalog.OwnerModule, token);

        var values = Interpret(rows);
        foreach (var problem in values.Problems)
        {
            logger.LogWarning("助手参数未生效：{Problem}", problem);
        }

        return new AssistantParameterSet(rows, values);
    }

    /// <summary>
    /// 把参数行解释成策略对象。**唯一一处**"参数键 → 策略字段"的映射与校验：
    /// 3105 页面校验取值时用的也是它，所以"保存时通过、生效时被忽略"不可能发生。
    /// </summary>
    public static AssistantPolicyValues Interpret(IReadOnlyList<SystemParameterItem> rows)
    {
        var builder = new Builder();
        var problems = new List<string>();

        var byKey = new Dictionary<string, SystemParameterItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) byKey[row.Key] = row;

        foreach (var descriptor in AssistantParameterCatalog.All)
        {
            if (!byKey.TryGetValue(descriptor.Key, out var row))
            {
                // 目录里有、库里没有：多半是迁移没跑完。**不静默**——否则"参数改了没反应"无从追查
                problems.Add($"参数 {descriptor.Key} 在库里没有行（迁移未执行？），本次用代码默认值。");
                continue;
            }

            var raw = row.EffectiveValue;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (!AssistantParameterCatalog.TryNormalize(descriptor, raw, out var normalized, out var problem))
            {
                // 说明里带上键名：日志里只有一个中文标题时，定位不到是哪一行
                problems.Add($"{descriptor.DisplayName}（{descriptor.Key}）：{problem}（已忽略，仍用默认值）");
                continue;
            }

            // 工具开关与动作族开关都是**机械对应**的键（TOOL_ + 工具名、ACTION_ + 动作名），
            // 不走下面那张手写映射表——手写 30 个 case 只会多一处"加了工具忘了接上"的机会
            if (AssistantToolKeys.TryGetToolName(descriptor.Key, out var toolName))
            {
                // 默认全开，所以只处理"关"：值非 0 时不落进禁止集，等同默认
                if (normalized == "0") builder.Capability.DisabledTools.Add(toolName);
                continue;
            }

            if (AssistantActionKeys.TryGetActionName(descriptor.Key, out var actionName))
            {
                if (normalized == "0") builder.Capability.DisabledActions.Add(actionName);
                continue;
            }

            Apply(builder, descriptor.Key, normalized!);
        }

        foreach (var row in rows)
        {
            if (AssistantParameterCatalog.Find(row.Key) is null)
            {
                // 参数下线后库里留着的行：提示但不报错，删掉即可。
                // 红线键（预演 / 幂等 / 越权上限 / 批核族）也走这条路——它们**在目录里没有条目、
                // 在下面的 Apply 里也没有分支**，所以除了被列出来之外，它们对行为没有任何影响路径。
                problems.Add(AssistantParameterCatalog.RedLineKeys.Contains(row.Key)
                    ? $"参数 {row.Key} 是红线键，不是可配置的参数——已忽略（它也没有任何生效路径）。"
                    : $"参数 {row.Key} 已不在参数目录里，被忽略。");
            }
        }

        // 跨字段的配对关系在**全部行应用完之后**才判：单看某一行看不出"建议阈值 ≤ 保留阈值"。
        EnforcePairs(builder, problems);

        return new AssistantPolicyValues(
            builder.Settings.SystemPrompt,
            builder.Settings.EnableAutoDistill,
            builder.Cost,
            problems)
        {
            Situation = builder.Situation,
            Diagnosis = builder.Diagnosis,
            ActionLimits = builder.ActionLimits,
            ConfigWrite = builder.ConfigWrite,
            Capability = builder.Capability,
            Chat = builder.Chat,
            Memory = builder.Memory,
            Kb = builder.Kb,
            ToolLimits = builder.ToolLimits,
        };
    }

    /// <summary>解析过程中逐步填好的各个域（避免 <c>Apply</c> 拖一长串 <c>out</c> 参数）。</summary>
    private sealed class Builder
    {
        public AssistantSettings Settings { get; } = new();

        public AssistantCostOptions Cost { get; } = new();

        public AssistantSituationBudgetOptions Situation { get; } = new();

        public AssistantDiagnosisOptions Diagnosis { get; } = new();

        public AssistantActionLimitsOptions ActionLimits { get; } = new();

        public AssistantConfigWriteOptions ConfigWrite { get; } = new();

        public AssistantCapabilityOptions Capability { get; } = new();

        public AssistantChatLimitsOptions Chat { get; } = new();

        public AssistantMemoryLimitsOptions Memory { get; } = new();

        public AssistantKbLimitsOptions Kb { get; } = new();

        public AssistantToolLimitsOptions ToolLimits { get; } = new();
    }

    /// <summary>
    /// 把一个规范值应用到策略对象上。**唯一一处**"参数键 → 策略字段"的映射。
    /// 每个键都必须在这里有分支：只加目录条目、不加分支，那个参数就永远不生效——
    /// 离线门禁的逐键探针测试会当场变红。
    /// </summary>
    private static void Apply(Builder builder, string key, string value)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        // 整数型参数取值：写成局部函数而不是在方法开头无脑解析一次——那会让**每一个**参数
        // （包括 SYSTEM_PROMPT 这种长文本）都先过一遍整数解析，字符串参数会当场抛异常。
        int Number() => int.Parse(value, invariant);
        switch (key)
        {
            // ---- 域 PROMPT ----
            case "SYSTEM_PROMPT":
                builder.Settings.SystemPrompt = value;
                break;

            // ---- 域 GOVERNANCE ----
            case "GLOBAL_DAILY_CAP_YUAN":
                builder.Cost.GlobalDailyCapYuan = double.Parse(value, invariant);
                break;

            case "USER_DAILY_CAP_YUAN":
                builder.Cost.UserDailyCapYuan = double.Parse(value, invariant);
                break;

            case "MAX_CONSECUTIVE_FAILURES":
                builder.Cost.MaxConsecutiveFailures = int.Parse(value, invariant);
                break;

            case "COOLDOWN_SECONDS":
                builder.Cost.CooldownSeconds = int.Parse(value, invariant);
                break;

            case "RESERVE_YUAN_PER_REQUEST":
                // 界面上的单位是元，内部按微元记账：换算是实现细节，不进参数表
                builder.Cost.ReserveMicroYuanPerRequest = (long)Math.Round(
                    decimal.Parse(value, invariant) * AssistantCost.MicroYuanPerYuan,
                    MidpointRounding.AwayFromZero);
                break;

            case "INPUT_PER_MILLION_YUAN":
                builder.Cost.InputPerMillionYuan = double.Parse(value, invariant);
                break;

            case "OUTPUT_PER_MILLION_YUAN":
                builder.Cost.OutputPerMillionYuan = double.Parse(value, invariant);
                break;

            case "ACTION_MAX_ROWS":
                builder.ActionLimits.MaxRowsPerAction = int.Parse(value, invariant);
                break;

            case "ACTION_MAX_APPROVAL_RECORDS":
                builder.ActionLimits.MaxApprovalRequestRecords = int.Parse(value, invariant);
                break;

            case "ACTION_MAX_AUDIT_KEYS":
                builder.ActionLimits.MaxAuditResourceKeys = int.Parse(value, invariant);
                break;

            case "ACTION_MAX_CONFIG_CLONE_OBJECTS":
                builder.ActionLimits.MaxConfigCloneObjects = int.Parse(value, invariant);
                break;

            // ---- 域 CAPABILITY（配置写准入）----
            case "CONFIG_WRITE_FIELDS":
                builder.ConfigWrite.Fields = value == "1";
                break;

            case "CONFIG_WRITE_DATASOURCES":
                builder.ConfigWrite.DataSources = value == "1";
                break;

            case "CONFIG_WRITE_BUTTONS":
                builder.ConfigWrite.Buttons = value == "1";
                break;

            case "CONFIG_WRITE_EFFECTS":
                builder.ConfigWrite.Effects = value == "1";
                break;

            // ---- 域 SITUATION ----
            case "SIT_RESIDENT_TOKEN_LIMIT":
                builder.Situation.ResidentTokenLimit = int.Parse(value, invariant);
                break;

            case "SIT_IDENTITY_TOKEN_LIMIT":
                builder.Situation.IdentityTokenLimit = int.Parse(value, invariant);
                break;

            case "SIT_PENDING_TOKEN_LIMIT":
                builder.Situation.PendingTokenLimit = int.Parse(value, invariant);
                break;

            case "SIT_MAX_FILTERS":
                builder.Situation.MaxFilters = int.Parse(value, invariant);
                break;

            case "SIT_MAX_SELECTION":
                builder.Situation.MaxSelection = int.Parse(value, invariant);
                break;

            case "SIT_MAX_DIRTY_FIELDS":
                builder.Situation.MaxDirtyFields = int.Parse(value, invariant);
                break;

            case "SIT_MAX_VALUE_LENGTH":
                builder.Situation.MaxValueLength = int.Parse(value, invariant);
                break;

            case "SIT_MAX_NOTICE_SUMMARY_LENGTH":
                builder.Situation.MaxNoticeSummaryLength = int.Parse(value, invariant);
                break;

            case "SIT_OVERDUE_DAYS":
                builder.Situation.OverdueDays = int.Parse(value, invariant);
                break;

            case "SIT_OVERDUE_MAX_AGE_DAYS":
                builder.Situation.OverdueMaxAgeDays = int.Parse(value, invariant);
                break;

            case "SIT_DIGEST_MAX_ITEMS":
                builder.Situation.DigestMaxItems = int.Parse(value, invariant);
                break;

            case "SIT_BLOCKED_NOW_SCAN_RECORDS":
                builder.Situation.BlockedNowScanRecords = int.Parse(value, invariant);
                break;

            case "SIT_BLOCKED_NOW_MAX_AGE_DAYS":
                builder.Situation.BlockedNowMaxAgeDays = int.Parse(value, invariant);
                break;

            case "SIT_BLOCKED_NOW_PROBE_RULES":
                builder.Situation.BlockedNowProbeRules = int.Parse(value, invariant);
                break;

            case "SIT_DIGEST_MODULE_SCAN_LIMIT":
                builder.Situation.DigestModuleScanLimit = int.Parse(value, invariant);
                break;

            case "SIT_ACTIVITY_WINDOW_DAYS":
                builder.Situation.ActivityWindowDays = int.Parse(value, invariant);
                break;

            case "SIT_RECENT_FAILURE_DAYS":
                builder.Situation.RecentFailureDays = int.Parse(value, invariant);
                break;

            case "SIT_RECENT_FAILURE_LIMIT":
                builder.Situation.RecentFailureLimit = int.Parse(value, invariant);
                break;

            case "SIT_DIGEST_TEXT_LENGTH":
                builder.Situation.DigestTextLength = int.Parse(value, invariant);
                break;

            // ---- 域 DIAGNOSIS ----
            case "DIAG_MAX_VALIDATION_RULES":
                builder.Diagnosis.MaxValidationRules = int.Parse(value, invariant);
                break;

            case "DIAG_MAX_FIELD_GUARDS":
                builder.Diagnosis.MaxFieldGuards = int.Parse(value, invariant);
                break;

            case "DIAG_MAX_PROVENANCE":
                builder.Diagnosis.MaxProvenance = int.Parse(value, invariant);
                break;

            case "DIAG_MAX_EFFECTS":
                builder.Diagnosis.MaxEffects = int.Parse(value, invariant);
                break;

            case "DIAG_MAX_BLOCKERS":
                builder.Diagnosis.MaxBlockers = int.Parse(value, invariant);
                break;

            case "DIAG_LAST_FAILURE_DAYS":
                builder.Diagnosis.LastFailureDays = int.Parse(value, invariant);
                break;

            case "DIAG_LAST_FAILURE_LIMIT":
                builder.Diagnosis.LastFailureLimit = int.Parse(value, invariant);
                break;

            case "DIAG_MAX_TEXT_LENGTH":
                builder.Diagnosis.MaxTextLength = int.Parse(value, invariant);
                break;

            case "DIAG_ACTION_SUMMARY_LIMIT":
                builder.Diagnosis.MaxActionSummary = int.Parse(value, invariant);
                break;

            // ---- 域 MEMORY ----
            case "MEM_ENABLE_AUTO_DISTILL":
                builder.Settings.EnableAutoDistill = value == "1";
                break;
            case "MEM_MAX_CANDIDATES":
                builder.Memory.MaxCandidates = Number();
                break;
            case "MEM_SUGGEST_THRESHOLD":
                builder.Memory.SuggestThreshold = Number();
                break;
            case "MEM_KEEP_THRESHOLD":
                builder.Memory.KeepThreshold = Number();
                break;
            case "MEM_DISTILL_TURNS":
                builder.Memory.DistillTurns = Number();
                break;
            case "MEM_MAX_PER_USER":
                builder.Memory.MaxPerUser = Number();
                break;
            case "MEM_MAX_KEY_LENGTH":
                builder.Memory.MaxKeyLength = Number();
                break;
            case "MEM_MAX_VALUE_LENGTH":
                builder.Memory.MaxValueLength = Number();
                break;
            case "MEM_MAX_PREFERENCES_LENGTH":
                builder.Memory.MaxPreferencesLength = Number();
                break;
            case "MEM_INJECTION_TOP_K":
                builder.Memory.InjectionTopK = Number();
                break;

            // ---- 域 KB ----
            case "KB_SEARCH_MAX_HITS":
                builder.Kb.SearchMaxHits = Number();
                break;
            case "KB_SEARCH_MAX_CONTENT_LENGTH":
                builder.Kb.SearchMaxContentLength = Number();
                break;
            case "KB_CHUNK_MAX_CHARS":
                builder.Kb.ChunkMaxChars = Number();
                break;
            case "KB_CHUNK_OVERLAP_CHARS":
                builder.Kb.ChunkOverlapChars = Number();
                break;
            case "KB_ENDPOINT_MAX_HITS":
                builder.Kb.EndpointMaxHits = Number();
                break;

            // ---- 域 CHAT ----
            case "CHAT_MAX_HISTORY_MESSAGES":
                builder.Chat.MaxHistoryMessages = Number();
                break;
            case "CHAT_CONTEXT_RESERVE_TOKENS":
                builder.Chat.ContextReserveTokens = Number();
                break;
            case "CHAT_DEFAULT_CONTEXT_WINDOW":
                builder.Chat.DefaultContextWindow = Number();
                break;
            case "CHAT_MAX_CONTENT_LENGTH":
                builder.Chat.MaxContentLength = Number();
                break;
            case "CHAT_MAX_TOOL_ARGUMENTS_LENGTH":
                builder.Chat.MaxToolArgumentsLength = Number();
                break;
            case "CHAT_MAX_TOOL_ROUNDS":
                builder.Chat.MaxToolRounds = Number();
                break;
            case "CHAT_TOOL_DIGEST_LENGTH":
                builder.Chat.ToolDigestLength = Number();
                break;
            case "CHAT_AUDIT_ARGUMENT_LENGTH":
                builder.Chat.AuditArgumentLength = Number();
                break;

            // ---- 域 TOOL_LIMIT ----
            case "TOOL_LIMIT_SEARCH_MAX_ROWS":
                builder.ToolLimits.SearchMaxRows = Number();
                break;
            case "TOOL_LIMIT_SEARCH_MAX_COLUMNS":
                builder.ToolLimits.SearchMaxColumns = Number();
                break;
            case "TOOL_LIMIT_SEARCH_MAX_VALUE_LENGTH":
                builder.ToolLimits.SearchMaxValueLength = Number();
                break;
            case "TOOL_LIMIT_DETAIL_MAX_COLUMNS":
                builder.ToolLimits.DetailMaxColumns = Number();
                break;
            case "TOOL_LIMIT_DETAIL_MAX_VALUE_LENGTH":
                builder.ToolLimits.DetailMaxValueLength = Number();
                break;
            case "TOOL_LIMIT_DRAFT_MAX_VALUE_LENGTH":
                builder.ToolLimits.DraftMaxValueLength = Number();
                break;
            case "TOOL_LIMIT_DESCRIBE_MAX_FIELDS":
                builder.ToolLimits.DescribeMaxFields = Number();
                break;
            case "TOOL_LIMIT_LIST_MODULES_MAX":
                builder.ToolLimits.ListModulesMax = Number();
                break;
            case "TOOL_LIMIT_LIST_CAPABILITIES_MAX":
                builder.ToolLimits.ListCapabilitiesMax = Number();
                break;
            case "TOOL_LIMIT_FIELD_RELATIONS_MAX":
                builder.ToolLimits.FieldRelationsMax = Number();
                break;
            case "TOOL_LIMIT_REPORT_LIST_MAX":
                builder.ToolLimits.ReportListMax = Number();
                break;
            case "TOOL_LIMIT_REPORT_MAX_ROWS":
                builder.ToolLimits.ReportMaxRows = Number();
                break;
            case "TOOL_LIMIT_REPORT_MAX_COLUMNS":
                builder.ToolLimits.ReportMaxColumns = Number();
                break;
            case "TOOL_LIMIT_REPORT_MAX_VALUE_LENGTH":
                builder.ToolLimits.ReportMaxValueLength = Number();
                break;
            case "TOOL_LIMIT_RECORD_HISTORY_MAX":
                builder.ToolLimits.RecordHistoryMax = Number();
                break;
            case "TOOL_LIMIT_RECORD_ACTIVITY_DAYS":
                builder.ToolLimits.RecordActivityDays = Number();
                break;
            case "TOOL_LIMIT_ATTACHMENT_LIST_MAX":
                builder.ToolLimits.AttachmentListMax = Number();
                break;
        }
    }

    /// <summary>
    /// 跨字段的配对关系不成立时**退回两者的默认值并报告**，而不是照着一组自相矛盾的阈值往下跑。
    ///
    /// <para>
    /// 为什么不是"照单全收"：`MEM_SUGGEST_THRESHOLD ≤ MEM_KEEP_THRESHOLD` 意味着候选记忆要么全被标成
    /// 建议、要么全被丢弃，两种都不是管理员想要的结果，而他从界面上看不出来。为什么不"替谁改正"：
    /// 改一个留一个，等于界面显示的值与生效的值不一样——那正是本 ADR 要根除的形态。
    /// 退回默认值是唯一同时满足"行为可预期"与"界面所见即生效"的做法，并随问题清单一起示警。
    /// </para>
    /// </summary>
    private static void EnforcePairs(Builder builder, List<string> problems)
    {
        if (!builder.Memory.IsPairConsistent())
        {
            var defaults = new AssistantMemoryLimitsOptions();
            problems.Add(
                $"MEM_SUGGEST_THRESHOLD（{builder.Memory.SuggestThreshold}）必须大于 "
                + $"MEM_KEEP_THRESHOLD（{builder.Memory.KeepThreshold}），否则候选记忆要么全留、要么全丢；"
                + $"本轮退回默认值（{defaults.SuggestThreshold} / {defaults.KeepThreshold}）。");
            builder.Memory.SuggestThreshold = defaults.SuggestThreshold;
            builder.Memory.KeepThreshold = defaults.KeepThreshold;
        }

        if (!builder.Kb.IsChunkingConsistent())
        {
            var defaults = new AssistantKbLimitsOptions();
            problems.Add(
                $"KB_CHUNK_OVERLAP_CHARS（{builder.Kb.ChunkOverlapChars}）必须小于 "
                + $"KB_CHUNK_MAX_CHARS（{builder.Kb.ChunkMaxChars}），否则切块会原地打转；"
                + $"本轮退回默认值（{defaults.ChunkMaxChars} / {defaults.ChunkOverlapChars}）。");
            builder.Kb.ChunkMaxChars = defaults.ChunkMaxChars;
            builder.Kb.ChunkOverlapChars = defaults.ChunkOverlapChars;
        }
    }
}
