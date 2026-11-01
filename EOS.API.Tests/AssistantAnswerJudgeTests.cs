using EOS.API.Features.Assistant.Governance;
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

        var outcome = await judge.JudgeAsync(Transcript(), CancellationToken.None);

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
            await judge.JudgeAsync(Transcript(), CancellationToken.None),
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

    [Fact]
    public void 判官不碰权限也不写业务库()
    {
        // 红线（ADR-044 §2.2 / §11.4）：判官既不引权限服务，也不写业务表。
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

        public Task<string> CompleteAsync(string system, string user, CancellationToken token)
        {
            Assert.False(string.IsNullOrWhiteSpace(system));
            Assert.False(string.IsNullOrWhiteSpace(user));
            return Task.FromResult(response);
        }
    }
}
