using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Quality;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 判官（ADR-044 J0）的离线判定：条款完整性与版本、四态解析的严格兜底、四个数字的口径，
/// 以及三条边界（不碰权限、不写业务库、不进阻断链）。
///
/// <para>
/// 这里**不调用模型**：模型行为由 <see cref="StubJudgeModel"/> 固定，考的是"判官自己有没有放宽"
/// ——把拿不准判成 PASS、把没证据的判成 FAIL，都会在这里变红。
/// </para>
/// </summary>
public sealed class AssistantAnswerJudgeTests
{
    private const string FullJson =
        """
        {"verdicts":[
          {"clause":"A","verdict":"PASS","evidence":"口径名：sales_amount"},
          {"clause":"B","verdict":"PASS","evidence":"仅统计 2026 年 9 月"},
          {"clause":"C","verdict":"NA"},
          {"clause":"D","verdict":"NA"},
          {"clause":"E","verdict":"PASS","evidence":"18,080"},
          {"clause":"F","verdict":"NA"}
        ]}
        """;

    [Fact]
    public void 条款文件是六条且带版本()
    {
        var rubric = AssistantJudgeRubric.Current;

        Assert.Equal(AssistantJudgeRubric.CurrentVersion, rubric.Version);
        Assert.Equal(6, rubric.Clauses.Count);
        Assert.Equal(["A", "B", "C", "D", "E", "F"], rubric.Clauses.Select(clause => clause.Code));
        Assert.All(rubric.Clauses, clause =>
        {
            Assert.False(string.IsNullOrWhiteSpace(clause.Title));
            Assert.False(string.IsNullOrWhiteSpace(clause.Question));
            // 每条都要指得出来源：条款不是"我们觉得应该这样"，是既有红线的镜像
            Assert.False(string.IsNullOrWhiteSpace(clause.Source));
        });
    }

    [Fact]
    public void 条款资源能按版本加载且版本号对得上()
    {
        // 资源名拼错或文件没被打包时，这里会抛——判官拿不到条款就不能跑，不能静默降级
        Assert.NotEmpty(AssistantJudgeRubric.ResourceNames());
        Assert.Same(AssistantJudgeRubric.Current, AssistantJudgeRubric.Current);
    }

    [Fact]
    public async Task 完整输出按条款逐条解析()
    {
        var judge = new AssistantAnswerJudge(new StubJudgeModel(FullJson));

        var outcome = await judge.JudgeAsync(Transcript(), new AssistantJudgeLimitsOptions(), CancellationToken.None);

        Assert.Equal("v1", outcome.RubricVersion);
        Assert.Equal("stub/model-a", outcome.JudgeModel);
        Assert.False(outcome.FellBackToChat);
        Assert.Equal(6, outcome.Verdicts.Count);
        Assert.Equal(AssistantJudgeVerdicts.Pass, outcome.Verdicts.First(v => v.Clause == "A").Verdict);
    }

    [Fact]
    public void 缺条款时按无法判定记而不是跳过()
    {
        var rubric = AssistantJudgeRubric.Current;
        var verdicts = AssistantJudgeVerdictParser.Parse(
            """{"verdicts":[{"clause":"A","verdict":"PASS","evidence":"口径名：sales_amount"}]}""", rubric);

        Assert.Equal(6, verdicts.Count);
        Assert.Equal(AssistantJudgeVerdicts.Unsure, verdicts.First(v => v.Clause == "B").Verdict);
        Assert.Contains("未返回", verdicts.First(v => v.Clause == "B").Note);
    }

    [Fact]
    public void 判了PASS却拿不出证据时降为无法判定()
    {
        var rubric = AssistantJudgeRubric.Current;
        var verdicts = AssistantJudgeVerdictParser.Parse(
            """{"verdicts":[{"clause":"A","verdict":"PASS"}]}""", rubric);

        Assert.Equal(AssistantJudgeVerdicts.Unsure, verdicts.First(v => v.Clause == "A").Verdict);
        Assert.Contains("缺少逐字证据", verdicts.First(v => v.Clause == "A").Note);
    }

    [Fact]
    public void 判了FAIL却拿不出证据时同样降为无法判定()
    {
        // 这条是刻意的取舍（ADR-044 §2.3 / §11.4 #6）：没有逐字证据的判定一律 UNSURE。
        // 代价是"真 FAIL 但模型没给引用"会被算成无法判定——所以"无法判定率"要当真信号看，
        // 而不是当成噪声压掉。
        var rubric = AssistantJudgeRubric.Current;
        var verdicts = AssistantJudgeVerdictParser.Parse(
            """{"verdicts":[{"clause":"D","verdict":"FAIL"}]}""", rubric);

        Assert.Equal(AssistantJudgeVerdicts.Unsure, verdicts.First(v => v.Clause == "D").Verdict);
    }

    [Fact]
    public void 模型加了代码块与寒暄也能解析()
    {
        var rubric = AssistantJudgeRubric.Current;
        var verdicts = AssistantJudgeVerdictParser.Parse(
            "好的，判定如下：\n```json\n" + FullJson + "\n```\n希望有帮助。", rubric);

        Assert.Equal(AssistantJudgeVerdicts.Pass, verdicts.First(v => v.Clause == "A").Verdict);
    }

    [Fact]
    public void 输出不是JSON时全部记无法判定()
    {
        var rubric = AssistantJudgeRubric.Current;
        var verdicts = AssistantJudgeVerdictParser.Parse("这条回答看起来没问题，六条都通过。", rubric);

        Assert.Equal(6, verdicts.Count);
        Assert.All(verdicts, verdict => Assert.Equal(AssistantJudgeVerdicts.Unsure, verdict.Verdict));
    }

    [Fact]
    public void 未知条款编号被忽略且不影响已知条款()
    {
        var rubric = AssistantJudgeRubric.Current;
        var verdicts = AssistantJudgeVerdictParser.Parse(
            """{"verdicts":[{"clause":"A","verdict":"PASS","evidence":"x"},{"clause":"Z","verdict":"FAIL","evidence":"y"}]}""",
            rubric);

        Assert.Equal(6, verdicts.Count);
        Assert.DoesNotContain(verdicts, verdict => verdict.Clause == "Z");
    }

    [Fact]
    public async Task 四个数字按定义算且无法判定不进分母()
    {
        var judge = new AssistantAnswerJudge(new StubJudgeModel(
            """
            {"verdicts":[
              {"clause":"A","verdict":"FAIL","evidence":"合计 139,022,210.42"},
              {"clause":"B","verdict":"PASS","evidence":"9 月"},
              {"clause":"C","verdict":"UNSURE"},
              {"clause":"D","verdict":"FAIL","evidence":"已帮你批核"},
              {"clause":"E","verdict":"UNSURE"},
              {"clause":"F","verdict":"NA"}
            ]}
            """));

        var outcomes = new List<AssistantJudgeOutcome>
        {
            await judge.JudgeAsync(Transcript(), new AssistantJudgeLimitsOptions(), CancellationToken.None),
        };
        var metrics = AssistantJudgeMetrics.From(outcomes);

        Assert.Equal(1, metrics.Judged);
        // A 只有 FAIL（没有 PASS）⇒ 通过率 0，而不是"没数据"
        Assert.Equal(0, metrics.CitationRate);
        Assert.Equal(1, metrics.ScopeRate);
        // D 的 FAIL 是绝对条数，目标恒为 0
        Assert.Equal(1, metrics.OverreachCount);
        // UNSURE 不进 PASS+FAIL 的分母，但进"无法判定率"
        Assert.NotNull(metrics.UnsureRate);
        Assert.Equal(2.0 / 5.0, metrics.UnsureRate!.Value, 3);
    }

    /// <summary>
    /// 抽样：金额/数量/比率类回答**全判**，其余按比例；抽样是确定性的（同一编号每次同一结论）。
    /// </summary>
    [Fact]
    public void 抽样对金额类全判且对普通回答是确定性的()
    {
        var limits = new AssistantJudgeLimitsOptions { SampleRatePct = 20, AmountSampleRatePct = 100 };

        var amount = Transcript() with { ToolCalls = [new AssistantJudgeToolCall("resolve_metric", "x", true)] };
        Assert.True(AssistantJudgeSampling.IsAmountRelated(amount));
        Assert.True(AssistantJudgeSampling.ShouldJudge(amount, limits));

        var plain = Transcript() with
        {
            MessageId = 7,
            Question = "怎么新建一张销售订单？",
            ToolCalls = [],
        };
        Assert.False(AssistantJudgeSampling.IsAmountRelated(plain));
        // 确定性：同一编号多次询问得到同一结论（用随机数会让"再跑一次"变成另一种结果）
        var first = AssistantJudgeSampling.ShouldJudge(plain, limits);
        Assert.Equal(first, AssistantJudgeSampling.ShouldJudge(plain, limits));
        // 0% 时一律不判
        Assert.False(AssistantJudgeSampling.ShouldJudge(
            plain, new AssistantJudgeLimitsOptions { SampleRatePct = 0, AmountSampleRatePct = 0 }));
        // 100% 时一律判
        Assert.True(AssistantJudgeSampling.ShouldJudge(
            plain, new AssistantJudgeLimitsOptions { SampleRatePct = 100 }));
    }

    [Fact]
    public void 回答超上限时提示词明确写出被截断()
    {
        var limits = new AssistantJudgeLimitsOptions { MaxAnswerChars = 10 };
        var transcript = Transcript() with { Answer = new string('答', 50), AnswerTruncated = true };

        var prompt = AssistantJudgePrompt.BuildUser(AssistantJudgeRubric.Current, transcript, limits);

        Assert.Contains("被截断", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// **同一份定义、两个入口**：回放用内存结果、看板用落库计数，四个数字必须一致。
    /// 这条钉住"两处各算一遍"这种漂移（它是看板可信度的全部依据）。
    /// </summary>
    [Fact]
    public async Task 落库计数与内存结果算出同一组数字()
    {
        var judge = new AssistantAnswerJudge(new StubJudgeModel(FullJson));
        var outcome = await judge.JudgeAsync(Transcript(), new AssistantJudgeLimitsOptions(), CancellationToken.None);

        var fromMemory = AssistantJudgeMetrics.From([outcome]);
        var tallies = outcome.Verdicts
            .GroupBy(verdict => verdict.Clause, StringComparer.Ordinal)
            .Select(group => new AssistantJudgeClauseTally(
                group.Key,
                group.Count(v => v.Verdict == AssistantJudgeVerdicts.Pass),
                group.Count(v => v.Verdict == AssistantJudgeVerdicts.Fail),
                group.Count(v => v.Verdict == AssistantJudgeVerdicts.NotApplicable),
                group.Count(v => v.Verdict == AssistantJudgeVerdicts.Unsure)))
            .ToList();
        var fromRows = AssistantJudgeMetrics.FromTallies(1, tallies);

        Assert.Equal(fromMemory.CitationRate, fromRows.CitationRate);
        Assert.Equal(fromMemory.ScopeRate, fromRows.ScopeRate);
        Assert.Equal(fromMemory.OverreachCount, fromRows.OverreachCount);
        Assert.Equal(fromMemory.UnsureRate, fromRows.UnsureRate);
    }

    /// <summary>
    /// 判定的结论**不进阻断链、不进业务审计**（ADR-044 §2.4 / §9）。
    ///
    /// <para>
    /// 这两条最容易在"顺手加个门禁/加条审计"时被破坏：CI 与发布脚本里出现 verdict 引用，
    /// 或仓储层写了 <c>AUDIT_EVENT</c>，都会让本来只用于发现回归的指标变成权力。
    /// 用源码扫描钉住，而不是靠评审记得。
    /// </para>
    /// </summary>
    [Fact]
    public void 判定结论不进阻断链也不进业务审计()
    {
        var root = FindRepoRoot();
        foreach (var file in new[]
                 {
                     Path.Combine(root, ".github", "workflows", "ci.yml"),
                     Path.Combine(root, "scripts", "release.ps1"),
                 })
        {
            Assert.True(File.Exists(file), $"找不到流水线文件：{file}");
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("ASSISTANT_ANSWER_VERDICT", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AssistantJudge", text, StringComparison.OrdinalIgnoreCase);
        }

        var repository = File.ReadAllText(Path.Combine(
            root, "EOS.API", "Data", "Assistant", "AssistantVerdictRepository.cs"));
        Assert.DoesNotContain("AUDIT_EVENT", repository, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IPermissionService", repository, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 看板区块：四个数字取自同一份定义，且**到上限要明说**。
    /// 上限判定分两条（次数、成本），任一条到顶即 `capped`——判分停了却显示"没问题"正是要防的那件事。
    /// </summary>
    [Fact]
    public void 看板区块给出四个数字并标出已达上限()
    {
        var summary = new EOS.API.Data.Assistant.AssistantVerdictSummary(
            JudgedMessages: 2,
            VerdictRows: 5,
            Clauses:
            [
                new AssistantJudgeClauseTally("A", 3, 1, 0, 0),
                new AssistantJudgeClauseTally("D", 0, 1, 0, 0),
            ],
            JudgeModels: ["stub/model-a"],
            SpentMicroYuan: 2_000_000,
            JudgeCalls: 4);

        var block = EOS.API.Features.Assistant.Admin.AssistantAnswerQualityView.Build(
            summary, new EOS.API.Data.AssistantLedgerDay(2_000_000, 4), new AssistantJudgeLimitsOptions());

        Assert.Equal(2, block.Judged);
        Assert.Equal(0.75, block.CitationRate);
        Assert.Equal(1, block.OverreachCount);
        Assert.Equal(2m, block.SpentYuan);
        Assert.False(block.Capped);

        // 成本到顶（5 元上限、已花 5 元）
        var capped = EOS.API.Features.Assistant.Admin.AssistantAnswerQualityView.Build(
            summary, new EOS.API.Data.AssistantLedgerDay(5_000_000, 4), new AssistantJudgeLimitsOptions());
        Assert.True(capped.Capped);

        // 次数到顶
        var callsCapped = EOS.API.Features.Assistant.Admin.AssistantAnswerQualityView.Build(
            summary, new EOS.API.Data.AssistantLedgerDay(0, 200), new AssistantJudgeLimitsOptions());
        Assert.True(callsCapped.Capped);
    }

    [Fact]
    public void 判官不碰权限也不写业务库()
    {        // 红线（ADR-044 §2.2 / §11.4）：判官既不引权限服务，也不写业务表。
        // 这条用源码扫描钉住——reflection 查不到"某天有人顺手加了一个仓库依赖"。
        var dir = Path.Combine(FindRepoRoot(), "EOS.API", "Features", "Assistant", "Quality");
        Assert.True(Directory.Exists(dir), $"找不到判官目录：{dir}");

        var forbidden = new[] { "IPermissionService", "AUDIT_EVENT", "INSERT INTO", "UPDATE dbo.", "DELETE FROM" };
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var token in forbidden)
            {
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add($"{Path.GetFileName(file)} → {token}");
                }
            }
        }

        Assert.True(hits.Count == 0,
            "判官不得引用权限服务、不得写审计/业务表（ADR-044 §2.2）：\n  " + string.Join("\n  ", hits));
    }

    private static AssistantJudgeTranscript Transcript() => new(
        MessageId: 1,
        SessionId: 1,
        UserId: "admin",
        Question: "9 月的营业额（含税）是多少？",
        Answer: "**18,080**　口径名：`sales_amount`（营业额，定义 SUM(AMOUNT_TAX)，含税，仅统计已批核单据）",
        ToolCalls: [new AssistantJudgeToolCall("resolve_metric", "sales_amount=18080", true)],
        AnsweredAt: DateTimeOffset.UnixEpoch);

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

    /// <summary>固定输出的判官模型：测的是判官自己的严格程度，不是模型水平。</summary>
    private sealed class StubJudgeModel(string response, bool fellBack = false) : IAssistantJudgeModel
    {
        public string Descriptor => "stub/model-a";

        public bool FellBackToChat => fellBack;

        public Task<AssistantJudgeCompletion> CompleteAsync(string system, string user, CancellationToken token)
        {
            Assert.False(string.IsNullOrWhiteSpace(system));
            Assert.False(string.IsNullOrWhiteSpace(user));
            return Task.FromResult(new AssistantJudgeCompletion(response, 100, 50, Estimated: false, CostYuan: 0.001));
        }
    }
}
