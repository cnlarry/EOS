using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Tools;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 助手参数目录（ADR-030 §5）的**离线门禁**：不需要数据库，`dotnet test` 即可跑。
///
/// <para>
/// 这一组测试是"配了没人读"的拦截面。目录把参数声明成九个字段（键 / 标题 / 域 / 类型 / 单位 /
/// 说明 / 默认值 / 作用域策略 / 读取方），这里逐条断言它们**真的接上了**：
/// 每个键都有探针值能改动合成结果、每个读取方符号都能在源码里找到、默认值取自代码、
/// 坏值被报告而不是静默吞掉。
/// </para>
///
/// <para>
/// 与库有关的那一半（目录 ↔ <c>dbo.SYSSS</c> 的行一致）在 <c>scripts/check-assistant-params.ps1</c>，
/// 因为"库里的定义与目录一致"必须先连上库才能判。
/// </para>
/// </summary>
public sealed class AssistantParameterCatalogTests
{
    // ===== 构造参数行：一次给全，缺行本身也是一种被测状态 =====

    private static SystemParameterItem Row(
        AssistantParameterDescriptor descriptor, string? value, string? defaultValue = null) =>
        new(
            descriptor.Key,
            value,
            descriptor.ValueType,
            defaultValue,
            descriptor.Group,
            AssistantParameterCatalog.FindGroup(descriptor.Group)!.Label,
            descriptor.Description,
            "immediate",
            AssistantParameterCatalog.SeqNoOf(descriptor),
            null,
            null,
            null,
            IsReferenced: true);

    /// <summary>全部参数都有行，其中 <paramref name="overrides"/> 指定的几条被覆盖。</summary>
    private static List<SystemParameterItem> Rows(params (string Key, string? Value)[] overrides)
    {
        var byKey = overrides.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        return [.. AssistantParameterCatalog.All.Select(descriptor =>
            Row(descriptor, byKey.TryGetValue(descriptor.Key, out var value) ? value : null))];
    }

    /// <summary>
    /// 把参数真正影响的字段拼成指纹：用于断言"这个键确实改动了什么"。
    ///
    /// <para>
    /// 四个域直接序列化——手工列 45 个属性早晚会漏掉新加的那个，而漏掉的后果正是这条测试要防的
    /// （新参数"改了没变化"看起来像通过）。
    /// </para>
    /// </summary>
    private static string Fingerprint(AssistantPolicyValues values) => string.Join('|',
        values.SystemPrompt,
        values.EnableAutoDistill,
        JsonSerializer.Serialize(values.Cost),
        JsonSerializer.Serialize(values.Situation),
        JsonSerializer.Serialize(values.Diagnosis),
        JsonSerializer.Serialize(values.ActionLimits),
        JsonSerializer.Serialize(values.ConfigWrite),
        JsonSerializer.Serialize(values.Capability),
        JsonSerializer.Serialize(values.Chat),
        JsonSerializer.Serialize(values.Memory),
        JsonSerializer.Serialize(values.Kb),
        JsonSerializer.Serialize(values.ToolLimits));

    private static string DefaultFingerprint() => Fingerprint(AssistantPolicyValues.Default);

    [Fact]
    public void Missing_Rows_Fall_Back_To_Code_Defaults_And_Are_Reported()
    {
        // 一行都没有（迁移没跑）时：取值必须与代码默认值完全一致——这就是"恢复默认"的语义；
        // 同时**必须报告**缺行，否则"参数改了没反应"无从追查
        var values = AssistantParameterResolver.Interpret([]);

        Assert.Equal(DefaultFingerprint(), Fingerprint(values));
        Assert.Equal(AssistantParameterCatalog.All.Count, values.Problems.Count);
        Assert.All(values.Problems, problem => Assert.Contains("在库里没有行", problem, StringComparison.Ordinal));
    }

    [Fact]
    public void Overrides_Are_Applied()
    {
        var values = AssistantParameterResolver.Interpret(Rows(
            ("SYSTEM_PROMPT", "你是测试提示词。"),
            ("USER_DAILY_CAP_YUAN", "8.5"),
            ("MEM_ENABLE_AUTO_DISTILL", "false"),
            ("MAX_CONSECUTIVE_FAILURES", "9")));

        Assert.Empty(values.Problems);
        Assert.Equal("你是测试提示词。", values.SystemPrompt);
        Assert.Equal(8.5, values.Cost.UserDailyCapYuan);
        Assert.False(values.EnableAutoDistill);
        Assert.Equal(9, values.Cost.MaxConsecutiveFailures);
    }

    /// <summary>
    /// **每一个声明的键都必须真的改动点什么**。
    ///
    /// <para>
    /// 加了目录条目却忘了在 <see cref="AssistantParameterResolver"/> 的映射里接上，界面会照常显示、
    /// 照常保存，而它**永远不生效**。这条用"必须为每个键准备一个探针值"把它挡住：
    /// 加了描述符而不加探针，下面的集合断言先失败。
    /// </para>
    /// </summary>
    [Fact]
    public void Every_Declared_Key_Actually_Changes_Something()
    {
        var probes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SYSTEM_PROMPT"] = "PROBE-PROMPT",
            ["GLOBAL_DAILY_CAP_YUAN"] = "88.5",
            ["USER_DAILY_CAP_YUAN"] = "7.25",
            ["MAX_CONSECUTIVE_FAILURES"] = "9",
            ["COOLDOWN_SECONDS"] = "123",
            ["RESERVE_YUAN_PER_REQUEST"] = "0.0777",
            ["INPUT_PER_MILLION_YUAN"] = "3.5",
            ["OUTPUT_PER_MILLION_YUAN"] = "11.5",
            ["ACTION_MAX_ROWS"] = "51",
            ["ACTION_MAX_APPROVAL_RECORDS"] = "26",
            ["ACTION_MAX_AUDIT_KEYS"] = "21",
            ["ACTION_MAX_CONFIG_CLONE_OBJECTS"] = "101",
            ["CONFIG_WRITE_FIELDS"] = "0",
            ["CONFIG_WRITE_DATASOURCES"] = "1",
            ["CONFIG_WRITE_BUTTONS"] = "1",
            ["CONFIG_WRITE_EFFECTS"] = "1",
            ["SIT_RESIDENT_TOKEN_LIMIT"] = "301",
            ["SIT_IDENTITY_TOKEN_LIMIT"] = "201",
            ["SIT_PENDING_TOKEN_LIMIT"] = "81",
            ["SIT_MAX_FILTERS"] = "11",
            ["SIT_MAX_SELECTION"] = "21",
            ["SIT_MAX_DIRTY_FIELDS"] = "21",
            ["SIT_MAX_VALUE_LENGTH"] = "121",
            ["SIT_MAX_NOTICE_SUMMARY_LENGTH"] = "161",
            ["SIT_OVERDUE_DAYS"] = "8",
            ["SIT_OVERDUE_MAX_AGE_DAYS"] = "366",
            ["SIT_DIGEST_MAX_ITEMS"] = "6",
            ["SIT_BLOCKED_NOW_SCAN_RECORDS"] = "11",
            ["SIT_BLOCKED_NOW_MAX_AGE_DAYS"] = "31",
            ["SIT_BLOCKED_NOW_PROBE_RULES"] = "5",
            ["SIT_DIGEST_MODULE_SCAN_LIMIT"] = "9",
            ["SIT_ACTIVITY_WINDOW_DAYS"] = "31",
            ["SIT_RECENT_FAILURE_DAYS"] = "8",
            ["SIT_RECENT_FAILURE_LIMIT"] = "4",
            ["SIT_DIGEST_TEXT_LENGTH"] = "121",
            ["DIAG_MAX_VALIDATION_RULES"] = "13",
            ["DIAG_MAX_FIELD_GUARDS"] = "9",
            ["DIAG_MAX_PROVENANCE"] = "13",
            ["DIAG_MAX_EFFECTS"] = "13",
            ["DIAG_MAX_BLOCKERS"] = "9",
            ["DIAG_LAST_FAILURE_DAYS"] = "31",
            ["DIAG_LAST_FAILURE_LIMIT"] = "2",
            ["DIAG_MAX_TEXT_LENGTH"] = "161",
            ["DIAG_ACTION_SUMMARY_LIMIT"] = "4",
            ["MEM_ENABLE_AUTO_DISTILL"] = "0",
            ["MEM_MAX_CANDIDATES"] = "7",
            ["MEM_SUGGEST_THRESHOLD"] = "91",
            ["MEM_KEEP_THRESHOLD"] = "21",
            ["MEM_DISTILL_TURNS"] = "13",
            ["MEM_MAX_PER_USER"] = "251",
            ["MEM_MAX_KEY_LENGTH"] = "121",
            ["MEM_MAX_VALUE_LENGTH"] = "1234",
            ["MEM_MAX_PREFERENCES_LENGTH"] = "2345",
            ["MEM_INJECTION_TOP_K"] = "7",
            ["KB_SEARCH_MAX_HITS"] = "7",
            ["KB_SEARCH_MAX_CONTENT_LENGTH"] = "311",
            ["KB_SEARCH_RELEVANCE_MARGIN_PCT"] = "37",
            ["KB_CHUNK_MAX_CHARS"] = "801",
            ["KB_CHUNK_OVERLAP_CHARS"] = "121",
            ["KB_ENDPOINT_MAX_HITS"] = "33",
            ["CHAT_MAX_HISTORY_MESSAGES"] = "41",
            ["CHAT_CONTEXT_RESERVE_TOKENS"] = "2048",
            ["CHAT_DEFAULT_CONTEXT_WINDOW"] = "32768",
            ["CHAT_MAX_CONTENT_LENGTH"] = "4001",
            ["CHAT_MAX_TOOL_ARGUMENTS_LENGTH"] = "1500",
            ["CHAT_MAX_TOOL_ROUNDS"] = "6",
            ["CHAT_TOOL_DIGEST_LENGTH"] = "121",
            ["CHAT_AUDIT_ARGUMENT_LENGTH"] = "151",
            ["TOOL_LIMIT_SEARCH_MAX_ROWS"] = "7",
            ["TOOL_LIMIT_SEARCH_MAX_COLUMNS"] = "9",
            ["TOOL_LIMIT_SEARCH_MAX_VALUE_LENGTH"] = "41",
            ["TOOL_LIMIT_DETAIL_MAX_COLUMNS"] = "25",
            ["TOOL_LIMIT_DETAIL_MAX_VALUE_LENGTH"] = "201",
            ["TOOL_LIMIT_DRAFT_MAX_VALUE_LENGTH"] = "501",
            ["TOOL_LIMIT_DESCRIBE_MAX_FIELDS"] = "81",
            ["TOOL_LIMIT_LIST_MODULES_MAX"] = "51",
            ["TOOL_LIMIT_LIST_CAPABILITIES_MAX"] = "52",
            ["TOOL_LIMIT_FIELD_RELATIONS_MAX"] = "53",
            ["TOOL_LIMIT_REPORT_LIST_MAX"] = "21",
            ["TOOL_LIMIT_REPORT_MAX_ROWS"] = "6",
            ["TOOL_LIMIT_REPORT_MAX_COLUMNS"] = "13",
            ["TOOL_LIMIT_REPORT_MAX_VALUE_LENGTH"] = "41",
            ["TOOL_LIMIT_RECORD_HISTORY_MAX"] = "21",
            ["TOOL_LIMIT_RECORD_ACTIVITY_DAYS"] = "91",
            ["TOOL_LIMIT_ATTACHMENT_LIST_MAX"] = "22",
        };

        // 工具开关与动作族开关：键名由各自的清单机械生成，所以探针也按同一份清单生成——
        // 若某个工具 / 动作在目录里没有对应条目，下面的集合断言会先红
        foreach (var toolName in AssistantToolKeys.ToolNames)
        {
            probes[AssistantToolKeys.ParameterKeyOf(toolName)] = "0";
        }

        foreach (var actionName in AssistantActionKeys.ActionNames)
        {
            probes[AssistantActionKeys.ParameterKeyOf(actionName)] = "0";
        }

        Assert.Equal(
            AssistantParameterCatalog.All.Select(item => item.Key).OrderBy(item => item, StringComparer.Ordinal),
            probes.Keys.OrderBy(item => item, StringComparer.Ordinal));

        var defaults = DefaultFingerprint();
        foreach (var descriptor in AssistantParameterCatalog.All)
        {
            var values = AssistantParameterResolver.Interpret(Rows((descriptor.Key, probes[descriptor.Key])));

            Assert.Empty(values.Problems);
            Assert.NotEqual(defaults, Fingerprint(values));
        }

        // 微元换算：界面上的 0.0777 元 = 77700 微元。这条专门盯住"单位改了但换换算没跟上"
        var reserve = AssistantParameterResolver.Interpret(Rows(("RESERVE_YUAN_PER_REQUEST", "0.0777")));
        Assert.Equal(77_700, reserve.Cost.ReserveMicroYuanPerRequest);
        Assert.Equal(AssistantCost.MicroYuanPerYuan, 1_000_000m);
    }

    /// <summary>
    /// 工具输出上限必须**同时**出现在两处：执行时按它截断，以及**发给模型的声明文本**里那一句。
    ///
    /// <para>
    /// 只做前一半，就会得到"系统给模型 10 行、说明里写着 5 行"——模型按 5 行规划，
    /// 用户看到的却是 10 行。这条断言盯的正是后半句（<c>AssistantToolRegistry.LimitNote</c>）。
    /// </para>
    /// </summary>
    [Fact]
    public void Tool_Declaration_Text_Carries_The_Parameter_Values()
    {
        var policy = AssistantPolicyValues.Default with
        {
            ToolLimits = new AssistantToolLimitsOptions
            {
                SearchMaxRows = 7,
                SearchMaxColumns = 9,
                SearchMaxValueLength = 41,
            },
        };

        var registry = new AssistantToolRegistry(
            [new StubTool(SearchRecordsTool.ToolName)],
            AssistantTestRuntime.Fixed(new AssistantSettings(), policy));

        var description = Assert.Single(registry.Definitions).Description;

        Assert.Contains("最多 7 行", description, StringComparison.Ordinal);
        Assert.Contains("每行 9 列", description, StringComparison.Ordinal);
        Assert.Contains("41 字符", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// 报表工具的上限同样必须**同时**出现在两处：执行时按它截断，以及在发给模型的声明文本里。
    ///
    /// <para>
    /// 单独一条而不是并进上一条：报表是两个工具、四个数字，且 <c>list_reports</c> 只有"几个报表"
    /// 一个上限——合在一起断言时，漏接一个工具会被另一条的命中盖过去。
    /// </para>
    /// </summary>
    [Fact]
    public void Report_Tool_Declaration_Text_Carries_The_Parameter_Values()
    {
        var policy = AssistantPolicyValues.Default with
        {
            ToolLimits = new AssistantToolLimitsOptions
            {
                ReportListMax = 7,
                ReportMaxRows = 11,
                ReportMaxColumns = 13,
                ReportMaxValueLength = 17,
            },
        };

        var registry = new AssistantToolRegistry(
            [new StubTool(ListReportsTool.ToolName), new StubTool(RunReportTool.ToolName)],
            AssistantTestRuntime.Fixed(new AssistantSettings(), policy));

        var definitions = registry.Definitions.ToDictionary(item => item.Name, item => item.Description);

        Assert.Contains("最多列出 7 个报表", definitions[ListReportsTool.ToolName], StringComparison.Ordinal);
        Assert.Contains("最多 11 行", definitions[RunReportTool.ToolName], StringComparison.Ordinal);
        Assert.Contains("每行 13 列", definitions[RunReportTool.ToolName], StringComparison.Ordinal);
        Assert.Contains("17 字符", definitions[RunReportTool.ToolName], StringComparison.Ordinal);
    }

    /// <summary>附件清单工具的上限同理：条数与"只给元数据"这句都要出现在声明文本里。</summary>
    [Fact]
    public void Attachment_Tool_Declaration_Text_Carries_The_Parameter_Values()
    {
        var policy = AssistantPolicyValues.Default with
        {
            ToolLimits = new AssistantToolLimitsOptions { AttachmentListMax = 8 },
        };

        var registry = new AssistantToolRegistry(
            [new StubTool(AttachmentListTool.ToolName)],
            AssistantTestRuntime.Fixed(new AssistantSettings(), policy));

        var description = Assert.Single(registry.Definitions).Description;

        Assert.Contains("最多列出 8 个附件", description, StringComparison.Ordinal);
        Assert.Contains("不含内容", description, StringComparison.Ordinal);
    }

    /// <summary>单据历史工具的上限同理：条数与时间窗都要出现在发给模型的声明文本里。</summary>
    [Fact]
    public void Record_History_Tool_Declaration_Text_Carries_The_Parameter_Values()
    {
        var policy = AssistantPolicyValues.Default with
        {
            ToolLimits = new AssistantToolLimitsOptions { RecordHistoryMax = 9, RecordActivityDays = 31 },
        };

        var registry = new AssistantToolRegistry(
            [new StubTool(RecordHistoryTool.ToolName)],
            AssistantTestRuntime.Fixed(new AssistantSettings(), policy));

        var description = Assert.Single(registry.Definitions).Description;

        Assert.Contains("各最多 9 条", description, StringComparison.Ordinal);
        Assert.Contains("最近 31 天", description, StringComparison.Ordinal);
    }

    /// <summary>只为验证声明的上限句而存在的假工具：名字用真工具的常量，于是会命中同一分支。</summary>
    private sealed class StubTool(string name) : AssistantToolBase
    {
        public override string Name => name;
        public override AssistantToolRisk Risk => AssistantToolRisk.Read;
        public override string Description => "PROBE";
        public override string ParametersJson => "{}";

        public override Task<ToolExecutionResult> ExecuteAsync(
            string userId, JsonElement arguments, CancellationToken token) =>
            Task.FromResult(ToolExecutionResult.Deny("PROBE 不执行任何东西。"));
    }

    [Fact]
    public void Blank_Value_Means_Not_Set()
    {
        // 界面上清空输入 = 恢复默认，所以空串不该被当成"值"去解析（否则提示词会被清成空）
        var values = AssistantParameterResolver.Interpret(Rows(
            ("SYSTEM_PROMPT", "   "),
            ("USER_DAILY_CAP_YUAN", string.Empty)));

        Assert.Empty(values.Problems);
        Assert.Equal(new AssistantSettings().SystemPrompt, values.SystemPrompt);
        Assert.Equal(new AssistantSettings().Cost.UserDailyCapYuan, values.Cost.UserDailyCapYuan);
    }

    [Theory]
    [InlineData("USER_DAILY_CAP_YUAN", "abc")]
    [InlineData("USER_DAILY_CAP_YUAN", "0")]
    [InlineData("USER_DAILY_CAP_YUAN", "-3")]
    [InlineData("GLOBAL_DAILY_CAP_YUAN", "0")]
    [InlineData("INPUT_PER_MILLION_YUAN", "0")]
    [InlineData("MAX_CONSECUTIVE_FAILURES", "0")]
    [InlineData("MAX_CONSECUTIVE_FAILURES", "9999")]
    [InlineData("COOLDOWN_SECONDS", "abc")]
    [InlineData("MEM_ENABLE_AUTO_DISTILL", "yes")]
    public void Bad_Values_Are_Reported_And_Keep_The_Default(string key, string value)
    {
        var values = AssistantParameterResolver.Interpret(Rows((key, value)));

        // 必须**报告**：静默忽略会让"界面显示 5 元、实际按默认值跑"无从追查
        var problem = Assert.Single(values.Problems);
        Assert.Contains(AssistantParameterCatalog.Find(key)!.DisplayName, problem, StringComparison.Ordinal);
        Assert.Contains(key, problem, StringComparison.Ordinal);
        // 同时必须**保住默认值**，不能落成 0 或空
        Assert.Equal(DefaultFingerprint(), Fingerprint(values));
    }

    [Fact]
    public void Unknown_Keys_Are_Reported_But_Do_Not_Break_The_Rest()
    {
        // 参数下线后库里可能留着旧行：提示它、跳过它，但别的键照常生效
        var rows = Rows(("USER_DAILY_CAP_YUAN", "6"));
        rows.Add(Row(AssistantParameterCatalog.All[0], "1") with { Key = "SOME_RETIRED_KEY" });

        var values = AssistantParameterResolver.Interpret(rows);

        var problem = Assert.Single(values.Problems);
        Assert.Contains("SOME_RETIRED_KEY", problem, StringComparison.Ordinal);
        Assert.Equal(6, values.Cost.UserDailyCapYuan);
    }

    [Fact]
    public void Descriptor_Defaults_Come_From_Code()
    {
        // 界面上的"默认值"提示必须取自代码默认值，否则会与真实行为漂移
        var settings = new AssistantSettings();
        Assert.Equal(settings.SystemPrompt,
            AssistantParameterCatalog.Find("SYSTEM_PROMPT")!.DefaultValue);
        Assert.Equal(settings.Cost.UserDailyCapYuan.ToString("0.####"),
            AssistantParameterCatalog.Find("USER_DAILY_CAP_YUAN")!.DefaultValue);
        Assert.Equal(
            ((decimal)settings.Cost.ReserveMicroYuanPerRequest / AssistantCost.MicroYuanPerYuan).ToString("0.######"),
            AssistantParameterCatalog.Find("RESERVE_YUAN_PER_REQUEST")!.DefaultValue);

        Assert.All(AssistantParameterCatalog.All, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(item.Description));
            Assert.Contains(item.ValueType, AssistantParameterCatalog.ValueTypes);
            Assert.NotEmpty(item.Consumers);

            // 域必须登记过——否则页面分组会漏掉它（GROUP_SEQ 也写不出来）
            Assert.NotNull(AssistantParameterCatalog.FindGroup(item.Group));

            // 红线与凭据永远不是合法参数；目录里出现即说明有人想把它做成开关
            Assert.DoesNotContain(item.Key, AssistantParameterCatalog.RedLineKeys);
            Assert.DoesNotContain(item.Key, AssistantParameterCatalog.CredentialKeys);
        });
    }

    [Fact]
    public void GroupSeq_And_SeqNo_Are_Unique_And_Positive()
    {
        // 门禁（check-system-params）要求 GROUP_SEQ 非 0 且同一归属模块内不重复、组内 SEQ_NO 不重复
        var groups = AssistantParameterCatalog.Groups
            .Select(item => (item.Code, item.Label, item.Seq))
            .ToList();

        Assert.All(groups, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Code));
            Assert.False(string.IsNullOrWhiteSpace(item.Label));
            Assert.True(item.Seq > 0);
        });
        Assert.Equal(groups.Count, groups.Select(item => item.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(groups.Count, groups.Select(item => item.Seq).Distinct().Count());

        var perGroup = AssistantParameterCatalog.All
            .GroupBy(item => item.Group, StringComparer.Ordinal)
            .Select(group => group.Select(AssistantParameterCatalog.SeqNoOf).ToList());
        foreach (var seqNos in perGroup)
        {
            Assert.All(seqNos, seqNo => Assert.True(seqNo > 0));
            Assert.Equal(seqNos.Count, seqNos.Distinct().Count());
        }
    }

    /// <summary>
    /// 每个读取方符号都必须能在**源码里**找到（catalog 自身除外——把键名列一遍不等于有人读）。
    ///
    /// <para>
    /// 这是一条**弱断言**（符号出现 ≠ 真的读了它），所以它配着上面那条探针测试一起看：
    /// 探针证明"改这个键会让合成结果变化"，这条证明"有人按这个名字读它"。
    /// 两者都过，才谈得上"这个参数是活的"。
    /// </para>
    /// </summary>
    [Fact]
    public void Every_Consumer_Symbol_Is_Present_In_Source()
    {
        var root = RepositoryRoot();
        var catalogFile = Path.Combine(
            root, "EOS.API", "Features", "Assistant", "Parameters", "AssistantParameterCatalog.cs");
        Assert.True(File.Exists(catalogFile), $"找不到参数目录文件：{catalogFile}");

        var sources = Directory
            .EnumerateFiles(Path.Combine(root, "EOS.API"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !string.Equals(file, catalogFile, StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

        foreach (var descriptor in AssistantParameterCatalog.All)
        {
            foreach (var consumer in descriptor.Consumers)
            {
                Assert.True(
                    sources.Any(text => text.Contains(consumer, StringComparison.Ordinal)),
                    $"参数 {descriptor.Key} 声明的读取方 {consumer} 在 EOS.API 源码里找不到——"
                    + "要么是名字写错了，要么这条参数其实没有读取方。");
            }
        }
    }

    /// <summary>
    /// 迁移脚本必须**点名**每一条参数键。
    ///
    /// <para>
    /// 它挡住的是最容易发生的一件事：加了目录条目却忘了写迁移——那样参数在库里根本没有行，
    /// 界面显示着默认值、保存时却会撞上"库中无此行"。取值与类型是否与库一致由连库门禁判
    /// （迁移一旦执行就不可改，真正的事实是库里的行）。
    /// </para>
    /// </summary>
    [Fact]
    public void Migration_Script_Names_Every_Declared_Key()
    {
        var migrations = Path.Combine(RepositoryRoot(), "EOS.API", "Data", "Migrations");
        // 选取规则**按名字**而不是按编号：原先写的是 294/295/296/… 一串前缀，每落一批参数就要回来补一个，
        // 而漏补的后果正是这条测试本身要防的那件事（新键找不到迁移）。助手参数的迁移文件名都带 assistant。
        var scripts = Directory
            .EnumerateFiles(migrations, "*.sql", SearchOption.TopDirectoryOnly)
            .Where(file => Path.GetFileName(file).Contains("assistant", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToList();
        Assert.NotEmpty(scripts);

        foreach (var descriptor in AssistantParameterCatalog.All)
        {
            Assert.True(
                scripts.Any(text => text.Contains($"N'{descriptor.Key}'", StringComparison.Ordinal)),
                $"参数 {descriptor.Key} 在助手参数的迁移脚本里找不到——"
                + "目录加了条目却没落库，界面上的这一项保存时会失败。");
        }
    }

    /// <summary>
    /// 工具清单（<see cref="AssistantToolKeys.ToolNames"/>）与工具类里声明的 <c>ToolName</c> 常量
    /// 必须**逐名一致**。
    ///
    /// <para>
    /// 它挡的是"新加一个工具却忘了给它一条开关"——那种情况下这个工具**绕过能力面管理**：
    /// 界面上看不见它、也关不掉它，而任何人都不会立刻发现。
    /// </para>
    /// </summary>
    [Fact]
    public void Tool_Name_List_Matches_The_Tool_Sources()
    {
        var directory = Path.Combine(RepositoryRoot(), "EOS.API", "Features", "Assistant");
        var regex = new Regex("public const string ToolName = \"(?<name>[a-z0-9_]+)\";", RegexOptions.Compiled);
        var declared = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in regex.Matches(File.ReadAllText(file)))
            {
                declared.Add(match.Groups["name"].Value);
            }
        }

        Assert.NotEmpty(declared);
        Assert.Equal(declared, new SortedSet<string>(AssistantToolKeys.ToolNames, StringComparer.Ordinal));
    }

    /// <summary>
    /// 工具开关的**中文标题**必须与工具清单一一对应。
    ///
    /// <para>
    /// 少一个：标题退回标识符（难看，但要在门禁上红——它说明有人加了工具没加标题，页面上就会冒出
    /// 一行 <c>search_records</c>）；多一个：标题表里躺着一个**不存在的工具**，
    /// 于是页面上出现一个"关掉它有用"的开关，而它管的东西并不存在。后者比前者危险，所以两个方向都断。
    /// </para>
    /// </summary>
    [Fact]
    public void Tool_Labels_Cover_Every_Tool()
    {
        Assert.Equal(
            new SortedSet<string>(AssistantToolKeys.ToolNames, StringComparer.Ordinal),
            new SortedSet<string>(AssistantToolKeys.Labels.Keys, StringComparer.Ordinal));

        // 标题不能只是把标识符抄一遍：那样这张表就没有意义（管理员看到的还是 search_records）
        Assert.All(
            AssistantToolKeys.ToolNames,
            name => Assert.NotEqual(name, AssistantToolKeys.LabelOf(name)));
    }

    /// <summary>从测试输出目录向上找到仓库根（含 <c>EOS.API/EOS.API.csproj</c> 的那一层）。</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EOS.API", "EOS.API.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（向上没找到 EOS.API/EOS.API.csproj）。");
    }
}
