using System.Text.Json;
using EOS.API.Features.Assistant.Evaluation;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 运行期判定器（<see cref="AssistantEvalCaseValidator"/>）的两件事：
/// **对仓库种子不能误报**（否则运维一跑就看到一堆假失败），**对坏样本要有判别力**
/// （否则它只是把每条用例都判成通过）。
///
/// <para>
/// 它与离线门禁（`AssistantEvalLayersTests` / `AssistantEvalToolExpectationTests`）判的是同一类事实，
/// 但输入不同：那边读仓库里的 `*.jsonl`，这边读库内的用例行。种子在仓库里必须同样能过这一套，
/// 所以这里直接拿种子文件当输入——**一套判定、两处输入**，库里和仓库里不会各长一套标准。
/// </para>
/// </summary>
public sealed class AssistantEvalCaseValidatorTests
{
    [Fact]
    public void 仓库里的种子全部通过确定性判定()
    {
        var cases = LoadSeedCases();
        Assert.True(cases.Count >= 150, $"种子只有 {cases.Count} 条，判定面过窄。");

        var problems = new List<string>();
        foreach (var seed in cases)
        {
            foreach (var failure in AssistantEvalCaseValidator.Validate(seed.Record)
                         .Where(check => check.Outcome == AssistantEvalOutcome.Failed))
            {
                problems.Add($"{seed.Where} [{failure.Code}] {failure.Message}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void 种子按层覆盖到八层()
    {
        // judge 层**不进仓库种子**：它的标签必须由人给，仓库里没有"人"可代签；这一层由用户在 3106 内维护。
        var layers = LoadSeedCases().Select(seed => seed.Record.Layer).Distinct().ToList();
        foreach (var layer in AssistantEvalCaseValidator.Layers.Where(item => item != "judge"))
        {
            Assert.Contains(layer, layers);
        }

        Assert.Contains("judge", AssistantEvalCaseValidator.Layers);
        Assert.DoesNotContain("judge", layers);
    }

    [Fact]
    public void 层名不认识时不猜而是直接失败()
    {
        var checks = AssistantEvalCaseValidator.Validate(Case("X-01", "unknown", """{"id":"X-01"}"""));
        Assert.Contains(checks, check => check.Code == "CASE_LAYER_UNKNOWN"
                                         && check.Outcome == AssistantEvalOutcome.Failed);
    }

    [Fact]
    public void 正文不是合法JSON时失败()
    {
        var checks = AssistantEvalCaseValidator.Validate(Case("X-02", "situation", "{not json"));
        Assert.Contains(checks, check => check.Code == "CASE_PAYLOAD_INVALID"
                                         && check.Outcome == AssistantEvalOutcome.Failed);
    }

    [Fact]
    public void 正文里的id与用例编号不一致时失败()
    {
        var checks = AssistantEvalCaseValidator.Validate(
            Case("X-03", "business", """{"id":"X-99","question":"q","expected":"e"}"""));
        Assert.Contains(checks, check => check.Code == "CASE_ID_MISMATCH");
    }

    [Fact]
    public void 处境层的摘要类别必须在闭集内且零模型()
    {
        var unknown = AssistantEvalCaseValidator.Validate(Case("X-04", "situation",
            """{"id":"X-04","situation":{"moduleId":1403},"expect":{"kinds":["forgot"],"zeroModel":true}}"""));
        Assert.Contains(unknown, check => check.Code == "SITUATION_KIND_UNKNOWN");

        var paid = AssistantEvalCaseValidator.Validate(Case("X-05", "situation",
            """{"id":"X-05","situation":{"moduleId":1403},"expect":{"kinds":["overdue"],"zeroModel":false}}"""));
        Assert.Contains(paid, check => check.Code == "SITUATION_ZERO_MODEL_OFF");
    }

    [Fact]
    public void 动作层的模块级原因码必须来自策略层闭集()
    {
        var checks = AssistantEvalCaseValidator.Validate(Case("X-06", "action",
            """{"id":"X-06","moduleId":1403,"action":"insert","rows":[{"values":{}}],"expect":{"moduleCode":"NO_SUCH_CODE"}}"""));
        Assert.Contains(checks, check => check.Code == "ACTION_CODE_UNKNOWN");
    }

    [Fact]
    public void 期望工具链指向未注册工具时失败()
    {
        var checks = AssistantEvalCaseValidator.Validate(Case("X-07", "business",
            """{"question":"q","expected":"e","sources":["WorkflowEngine"],"expect_tools":["no_such_tool"]}"""));
        Assert.Contains(checks, check => check.Code == "EXPECT_TOOLS_UNUSABLE");
    }

    [Fact]
    public void 期望工具链在页面策略下拿不到时失败()
    {
        // 配置页不发给记录写工具：样本声明 config-fields 却期望 draft_record，就是自相矛盾
        var checks = AssistantEvalCaseValidator.Validate(Case("X-08", "business",
            """{"question":"q","expected":"e","sources":["WorkflowEngine"],"expect_tools":["draft_record"],"page":"config-fields"}"""));
        Assert.Contains(checks, check => check.Code == "EXPECT_TOOLS_UNUSABLE"
                                         && check.Message.Contains("不会发给模型", StringComparison.Ordinal));
    }

    [Fact]
    public void 期望红线工具时失败()
    {
        var checks = AssistantEvalCaseValidator.Validate(Case("X-09", "business",
            """{"question":"q","expected":"e","sources":["WorkflowEngine"],"expect_tools":["approve_record"]}"""));
        Assert.Contains(checks, check => check.Code == "EXPECT_TOOLS_UNUSABLE"
                                         && check.Message.Contains("批核族", StringComparison.Ordinal));
    }

    [Fact]
    public void 工具链在列与正文里不一致时失败()
    {
        var record = Case("X-10", "business",
            """{"question":"q","expected":"e","sources":["WorkflowEngine"],"expect_tools":["search_records"]}""") with
        {
            ExpectTools = "get_record_detail",
        };
        var checks = AssistantEvalCaseValidator.Validate(record);
        Assert.Contains(checks, check => check.Code == "EXPECT_TOOLS_DISAGREE");
    }

    [Fact]
    public void 配置层的不一致原因码必须来自规则闭集()
    {
        var checks = AssistantEvalCaseValidator.Validate(Case("X-11", "config",
            """{"id":"X-11","surface":"effect","observed":{},"expectCode":"made-up"}"""));
        Assert.Contains(checks, check => check.Code == "CONFIG_CODE_UNKNOWN");
    }

    [Fact]
    public void 诊断层的规则已不在库时失败()
    {
        var lookups = new AssistantEvalLookups(new HashSet<int> { 1403 }, new HashSet<string>(StringComparer.Ordinal));
        var checks = AssistantEvalCaseValidator.Validate(Case("X-12", "diagnosis",
            """
            {"id":"X-12","moduleId":1403,"stage":"SAVE","validationKey":"gone",
             "message":"m","suggestedClass":"validation"}
            """), lookups);
        Assert.Contains(checks, check => check.Code == "DIAGNOSIS_RULE_NOT_FOUND");
    }

    [Fact]
    public void 诊断层的模块已下线时失败()
    {
        var lookups = new AssistantEvalLookups(new HashSet<int>(), new HashSet<string>(StringComparer.Ordinal));
        var checks = AssistantEvalCaseValidator.Validate(Case("X-13", "diagnosis",
            """
            {"id":"X-13","moduleId":9999,"stage":"SAVE","validationKey":"k",
             "message":"m","suggestedClass":"validation"}
            """), lookups);
        Assert.Contains(checks, check => check.Code == "MODULE_NOT_FOUND");
    }

    [Fact]
    public void 没有库内快照时相关检查记为跳过而不是通过()
    {
        var checks = AssistantEvalCaseValidator.Validate(Case("X-14", "diagnosis",
            """
            {"id":"X-14","moduleId":1403,"stage":"SAVE","validationKey":"k",
             "message":"m","suggestedClass":"validation"}
            """));
        Assert.Contains(checks, check => check.Code == "DIAGNOSIS_RULE_LOOKUP"
                                         && check.Outcome == AssistantEvalOutcome.Skipped);
        Assert.DoesNotContain(checks, check => check.Code == "DIAGNOSIS_RULE_EXISTS");
    }

    [Fact]
    public void 来源里的模块映射对不上当前登记时失败()
    {
        var checks = AssistantEvalCaseValidator.Validate(Case("X-15", "business",
            """{"question":"q","expected":"e","sources":["ModuleBusinessMap[999999]"]}"""));
        Assert.Contains(checks, check => check.Code == "SOURCES_UNANCHORED");
    }

    [Fact]
    public void 推断层的必备来源必须真的写在sources里()
    {
        var checks = AssistantEvalCaseValidator.Validate(Case("X-16", "inference",
            """{"question":"q","expected":"e","sources":["search_records:1405"],"required_sources":["search_records:1405","resolve_metric:x"]}"""));
        Assert.Contains(checks, check => check.Code == "INFERENCE_SOURCES_MISMATCH");
    }

    [Fact]
    public void 归因样本的命中事实可以刻意不完整()
    {
        // 证据不足类负样本"命中事实本来就不全"正是它要考的东西：本层不能因为缺 stage 就判它坏
        var checks = AssistantEvalCaseValidator.Validate(Case("X-17", "diagnosis",
            """
            {"hitFact":{"moduleId":1204,"validationKey":"reference-exists"},
             "reasonKind":"insufficient-evidence","expect":"deny"}
            """));
        Assert.DoesNotContain(checks, check => check.Outcome == AssistantEvalOutcome.Failed);
    }

    [Fact]
    public void 归因样本缺期望归因结果时失败()
    {
        var checks = AssistantEvalCaseValidator.Validate(Case("X-18", "diagnosis",
            """{"hitFact":{"moduleId":1204},"expect":"deny"}"""));
        Assert.Contains(checks, check => check.Code == "DIAGNOSIS_REASON_KIND_MISSING");
    }

    [Fact]
    public void 观测样本必须带实测发生次数()
    {
        var checks = AssistantEvalCaseValidator.Validate(Case("X-19", "diagnosis",
            """{"moduleId":1606,"action":"purchase-reprice","summary":"已结案不能重新取价","source":"AUDIT_EVENT","expect":"observed-rejection"}"""));
        Assert.Contains(checks, check => check.Code == "DIAGNOSIS_OCCURRENCES_MISSING");
    }

    [Fact]
    public void 代码级守卫样本不拿规则表去核()
    {
        // fieldGuard / blockers 是代码级守卫：规则表里本来就没有它们，按表核会得到恒假的失败
        var lookups = new AssistantEvalLookups(new HashSet<int> { 1302 }, new HashSet<string>(StringComparer.Ordinal));
        var checks = AssistantEvalCaseValidator.Validate(Case("X-20", "diagnosis",
            """
            {"id":"X-20","moduleId":1302,"stage":"MANUAL","validationKey":"master-field-write",
             "message":"字段由引擎维护","suggestedClass":"fieldGuard"}
            """), lookups);
        Assert.Contains(checks, check => check.Code == "DIAGNOSIS_RULE_SCOPE"
                                         && check.Outcome == AssistantEvalOutcome.Skipped);
        Assert.DoesNotContain(checks, check => check.Code == "DIAGNOSIS_RULE_NOT_FOUND");
    }

    [Fact]
    public void 各层都至少有一条检查被判过()
    {
        // 反向保护：判定器若对所有层都"无话可说"，上面那些负样本照样绿，等于什么都没判。
        foreach (var lookups in new AssistantEvalLookups?[]
                 {
                     new(new HashSet<int> { 1403 }, new HashSet<string>(StringComparer.Ordinal)),
                     null,
                 })
        {
            var cases = LoadSeedCases();
            var silent = cases
                .Where(seed => AssistantEvalCaseValidator.Validate(seed.Record, lookups)
                    .All(check => check.Outcome != AssistantEvalOutcome.Passed))
                .Select(seed => seed.Record.CaseId)
                .ToList();
            Assert.True(silent.Count == 0, $"这些用例一条检查都没判过：{string.Join("、", silent)}");
        }
    }

    /// <summary>
    /// 判官校准层：样本要"六条条款都标、PASS/FAIL 带引文、四态闭集、审阅人/日期齐备"。
    ///
    /// <para>
    /// 六条标签是硬要求：只标容易判的那两条，一致率会漂亮得没有信息量——覆盖不全的样本算不出一致率。
    /// </para>
    /// </summary>
    [Fact]
    public void 判官校准样本必须标满六条且带引文()
    {
        var complete = """
            {"id":"JG-01","messageId":34092,"question":"9 月营业额？","answer":"**18,080**　口径 sales_amount",
             "labels":[
               {"clause":"A","verdict":"PASS","evidence":"口径 sales_amount"},
               {"clause":"B","verdict":"PASS","evidence":"仅统计 9 月"},
               {"clause":"C","verdict":"NA"},
               {"clause":"D","verdict":"NA"},
               {"clause":"E","verdict":"PASS","evidence":"18,080"},
               {"clause":"F","verdict":"NA"}
             ],
             "reviewed":true,"reviewedBy":"larry","reviewedAt":"2026-11-02"}
            """;
        var good = AssistantEvalCaseValidator.Validate(Case("JG-01", "judge", complete));
        Assert.DoesNotContain(good, check => check.Outcome == AssistantEvalOutcome.Failed);
        Assert.Contains(good, check => check.Code == "JUDGE_LABELS_COVERAGE"
                                       && check.Outcome == AssistantEvalOutcome.Passed);
        // 一致率不在这一层判：判它要模型，本层只核样本完整
        Assert.Contains(good, check => check.Code == "JUDGE_AGREEMENT"
                                       && check.Outcome == AssistantEvalOutcome.Skipped);

        // 缺回答正文
        var noAnswer = AssistantEvalCaseValidator.Validate(Case("JG-02", "judge",
            """{"id":"JG-02","question":"q","labels":[]}"""));
        Assert.Contains(noAnswer, check => check.Code == "JUDGE_SAMPLE_MISSING");

        // 标了 PASS 却拿不出引文（与判官自己的硬规则同一口径）
        var noEvidence = AssistantEvalCaseValidator.Validate(Case("JG-03", "judge",
            """{"id":"JG-03","question":"q","answer":"a","labels":[{"clause":"A","verdict":"PASS"}]}"""));
        Assert.Contains(noEvidence, check => check.Code == "JUDGE_LABELS_INVALID");

        // 只标两条：覆盖不全
        var partial = AssistantEvalCaseValidator.Validate(Case("JG-04", "judge",
            """
            {"id":"JG-04","question":"q","answer":"a","labels":[
               {"clause":"A","verdict":"NA"},{"clause":"B","verdict":"NA"}]}
            """));
        Assert.Contains(partial, check => check.Code == "JUDGE_LABELS_INCOMPLETE");

        // 条款编号不在当前条款版本里
        var unknownClause = AssistantEvalCaseValidator.Validate(Case("JG-05", "judge",
            """{"id":"JG-05","question":"q","answer":"a","labels":[{"clause":"Z","verdict":"NA"}]}"""));
        Assert.Contains(unknownClause, check => check.Code == "JUDGE_LABELS_INVALID");

        // reviewed:true 必须能证明"谁在什么时候认的"
        var noReviewer = AssistantEvalCaseValidator.Validate(Case("JG-06", "judge",
            """
            {"id":"JG-06","question":"q","answer":"a","reviewed":true,"labels":[
               {"clause":"A","verdict":"NA"},{"clause":"B","verdict":"NA"},{"clause":"C","verdict":"NA"},
               {"clause":"D","verdict":"NA"},{"clause":"E","verdict":"NA"},{"clause":"F","verdict":"NA"}]}
            """));
        Assert.Contains(noReviewer, check => check.Code == "JUDGE_REVIEWED_INCOMPLETE");

        // 未审阅的草稿可保存，但明确标注"不计入校准"
        var draft = AssistantEvalCaseValidator.Validate(Case("JG-07", "judge",
            """
            {"id":"JG-07","question":"q","answer":"a","labels":[
               {"clause":"A","verdict":"NA"},{"clause":"B","verdict":"NA"},{"clause":"C","verdict":"NA"},
               {"clause":"D","verdict":"NA"},{"clause":"E","verdict":"NA"},{"clause":"F","verdict":"NA"}]}
            """));
        Assert.DoesNotContain(draft, check => check.Outcome == AssistantEvalOutcome.Failed);
        Assert.Contains(draft, check => check.Code == "JUDGE_NOT_REVIEWED"
                                        && check.Outcome == AssistantEvalOutcome.Skipped);
    }

    /// <summary>
    /// 未审阅的校准样本记 SKIP 而不是 PASS：结构完整不等于"这条校准做过了"，
    /// 读到"6 条全过"而其实一条都没校，是这条流程最容易被误读的地方。
    /// </summary>
    [Fact]
    public void 未审阅的校准样本记为跳过而不是通过()
    {
        var record = new AssistantEvalCaseRecord(
            "JG-90", "judge", null, null, null, null,
            """
            {"id":"JG-90","messageId":1,"question":"q","answer":"a",
             "labels":[
               {"clause":"A","verdict":"NA"},{"clause":"B","verdict":"NA"},{"clause":"C","verdict":"NA"},
               {"clause":"D","verdict":"NA"},{"clause":"E","verdict":"NA"},{"clause":"F","verdict":"NA"}]}
            """);

        var result = AssistantEvalRunner.Evaluate(record, null);

        Assert.Equal(AssistantEvalRunner.OutcomeSkip, result.Outcome);
    }

    private static AssistantEvalCaseRecord Case(string caseId, string layer, string payload) =>
        new(caseId, layer, null, null, null, null, payload);

    /// <summary>把仓库里的种子文件读成"库行形状"，与库内用例走同一套判定。</summary>
    private static List<(string Where, AssistantEvalCaseRecord Record)> LoadSeedCases()
    {
        var root = FindRepoRoot();
        var eval = Path.Combine(root, "EOS.API.Tests", "AssistantEval");
        Assert.True(Directory.Exists(eval), $"找不到评估目录：{eval}");

        var cases = new List<(string, AssistantEvalCaseRecord)>();
        foreach (var file in Directory.EnumerateFiles(eval, "*.jsonl", SearchOption.AllDirectories))
        {
            var layer = Path.GetFileName(Path.GetDirectoryName(file)!) ?? string.Empty;
            var lineNumber = 0;
            foreach (var raw in File.ReadAllLines(file))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                using var document = JsonDocument.Parse(raw);
                var payload = document.RootElement;
                var caseId = payload.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() ?? string.Empty
                    : $"{Path.GetFileNameWithoutExtension(file)}-{lineNumber}";
                var expectTools = payload.TryGetProperty("expect_tools", out var tools)
                                  && tools.ValueKind == JsonValueKind.Array
                    ? string.Join(",", tools.EnumerateArray().Select(item => item.GetString() ?? string.Empty))
                    : null;
                var page = payload.TryGetProperty("page", out var pageElement) && pageElement.ValueKind == JsonValueKind.String
                    ? pageElement.GetString()
                    : null;

                cases.Add((
                    $"{layer}/{Path.GetFileName(file)}:{lineNumber}",
                    new AssistantEvalCaseRecord(caseId, layer, null, null, expectTools, page, raw)));
            }
        }

        return cases;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EOS.API", "EOS.API.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found (EOS.API/EOS.API.csproj).");
    }
}
